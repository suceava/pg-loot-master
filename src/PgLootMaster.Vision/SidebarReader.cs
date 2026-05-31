using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;
using OpenCvRect = OpenCvSharp.Rect;

namespace PgLootMaster.Vision;

public sealed class SidebarItem
{
    public int Index { get; }
    public OpenCvMat Icon { get; }
    public OpenCvRect FrameRect { get; }
    // Frame-Y centre of the row's bar (the regularised even-grid centre, not the
    // jittery icon-artwork centre). The count number / checkmark sit on this line.
    public int RowCenterY { get; set; }
    public string Name { get; set; } = string.Empty;
    // Count of matches captured so far for this item, parsed from the trailing number in the
    // OCR'd row (e.g. "Pixie Sugar 12" → CaptureCount=12). Null if no count was found
    // (item already captured, shown with a checkmark instead, or OCR missed it).
    public int? CaptureCount { get; set; }
    // True if the right edge of the item row shows a green checkmark (item already captured).
    public bool Captured { get; set; }

    public SidebarItem(int index, OpenCvMat icon, OpenCvRect frameRect)
    {
        Index = index;
        Icon = icon;
        FrameRect = frameRect;
    }
}

public sealed class SidebarReader
{
    private const int SidebarOffsetXFromTitle = 950;
    private const int SidebarOffsetYFromTitle = 0;
    private const int SidebarWidth = 380;
    private const int SidebarHeight = 1100;

    private const int IconColumnXInSidebar = 18;
    private const int IconColumnWidth = 80;
    private const int ScanStartYInSidebar = 400;
    private const int ScanEndYInSidebar = 980;
    // Icon extraction: the sidebar icon is a colourful blob at the left of each row
    // bar, OVERHANGING the captured sidebar rect's left edge by ~25 px. It is detected
    // (not assumed) and cropped straight from the full frame — see DetectIconRect.
    private const int IconSearchLeftFromSidebar = -46;
    private const int IconSearchRightFromSidebar = 96;
    private const int IconSearchHalfHeight = 46;
    // The icon occupies this column band (offsets from the sidebar's left edge); the
    // item name always starts to the right of it. Detection is confined to this band so
    // the name text can never bleed into the icon crop.
    private const int IconBandLeftFromSidebar = -38;
    private const int IconBandRightFromSidebar = 75;
    // Crop = detected artwork bbox × this margin. Proportional (not a fixed pixel size)
    // so every icon fills the SAME fraction of its crop — consistent zoom — while the
    // crop stays tight to the artwork, which is what keeps match distances low.
    private const double IconCropMargin = 1.15;
    private const int IconFallbackCenterFromSidebar = 20;
    private const int IconFallbackHalfSize = 46;
    // Item rows are anchored on the icon column: the icon band is scanned for solid
    // colour blobs (one per row), and a blob run longer than ~1.4x ExpectedRowStride
    // is split. Name text — below ItemAreaTopInSidebar and left of
    // CountColumnLeftXInSidebar — is OCR'd and each line assigned to the nearest row.
    private const int ItemAreaTopInSidebar = 380;
    // Left edge of the capture-count column. Counts are left-justified at ~+284, so
    // this sits just left of them: name text ends to the left, counts / checkmarks to
    // the right. Kept tight (~4 px clearance) so a long name's tail never lands in the
    // count region — the widest observed label still ends left of this.
    private const int CountColumnLeftXInSidebar = 280;
    private const int ExpectedRowStride = 81;
    // Icon-blob row scan: a row Y counts as "icon" when at least IconRowMinPixels icon
    // pixels fall in the band [IconBandLeftFromSidebar .. IconRowScanBandRight]; runs
    // shorter than IconRowMinHeight are noise. The scan stops at IconRowScanBottom —
    // above the dungeon wall visible below the panel. Bumped from 960 → 985 to fit a
    // 7-item sidebar where the 7th icon ends ~y=965; the wall starts ~y=972 and would
    // register as a single 13-row run below the min-height filter, so it's discarded.
    private const int IconRowScanBandRight = 48;
    private const int IconRowScanBottom = 985;
    private const int IconRowMinPixels = 8;
    private const int IconRowMinHeight = 20;

    private static int _lastDumpedItemCount = -1;
    private static readonly string DebugDir = Path.Combine(Path.GetTempPath(), "pg-loot-master-sidebar-debug");

    private readonly SidebarOcr _ocr = new();

    // Shared "next item with N matches is yours to keep!" threshold, parsed from sidebar OCR.
    // null until first successful read.
    public int? CaptureThreshold { get; private set; }

    /// <summary>
    /// PNG of a debug montage — one row per item, the cropped count-column region the
    /// OCR reads (count digits + any checkmark) next to the parsed value — for eyeballing
    /// the number reads. Rebuilt every <see cref="ReadItems"/>; null until the first read.
    /// </summary>
    public byte[]? LastNumbersMontagePng { get; private set; }

    // Number from the "Turns Left:" header row. null until first successful read.
    public int? TurnsLeft { get; private set; }

    // Number from the "Score:" header row. Monotonically non-decreasing within a game;
    // a parse that jumps backward is dropped as OCR noise. null until first successful read.
    public int? Score { get; private set; }

    // Number from the "Turns Made:" header row. Monotonically non-decreasing within a game;
    // a parse that jumps backward is dropped as OCR noise. null until first successful read.
    public int? TurnsMade { get; private set; }

    // Reset header values at the start of a new game (panel re-acquired). Called by the
    // overlay so the new game doesn't inherit the prior game's monotonic floor.
    public void ResetForNewGame()
    {
        Score = null;
        TurnsMade = null;
        TurnsLeft = null;
        // Re-arm the debug dump so the new game writes fresh icon crops.
        _lastDumpedItemCount = -1;
    }

    private static readonly System.Text.RegularExpressions.Regex GameOverRe = new(
        @"scored\s+(\d[\d,]*)\s+in\s+(\d+)\s+turn",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
        | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// OCR the central panel area looking for the Game Over modal's
    /// "You scored X in Y turns!" message. Returns (score, turns) when the text is found.
    /// Authoritative — overrides any sidebar OCR readings for the final game state.
    /// </summary>
    public (int Score, int Turns)? TryReadGameOver(OpenCvMat bgrFrame, OpenCvRect titleBar)
    {
        if (!_ocr.IsAvailable || bgrFrame.Empty()) return null;

        // The dialog is centered horizontally in the board area (left of the sidebar)
        // and sits roughly in the upper-mid panel vertically. The crop is generous on
        // purpose so the text is fully inside.
        OpenCvRect rect = new(
            titleBar.X + 150,
            titleBar.Y + 200,
            Math.Min(950, SidebarOffsetXFromTitle - 100),
            550);
        rect = ClampToFrame(rect, bgrFrame);
        if (rect.Width < 200 || rect.Height < 100) return null;

        using OpenCvMat dialogCrop = new(bgrFrame, rect);
        IReadOnlyList<OcrLine> lines = _ocr.Recognize(dialogCrop);
        foreach (OcrLine line in lines)
        {
            System.Text.RegularExpressions.Match m = GameOverRe.Match(line.Text);
            if (m.Success
                && int.TryParse(m.Groups[1].Value.Replace(",", ""), out int score)
                && int.TryParse(m.Groups[2].Value, out int turns))
            {
                return (score, turns);
            }
        }
        return null;
    }

    public IReadOnlyList<SidebarItem> ReadItems(OpenCvMat bgrFrame, OpenCvRect titleBar)
    {
        OpenCvRect sidebarRect = new(
            titleBar.X + SidebarOffsetXFromTitle,
            titleBar.Y + SidebarOffsetYFromTitle,
            SidebarWidth,
            SidebarHeight);
        sidebarRect = ClampToFrame(sidebarRect, bgrFrame);
        if (sidebarRect.Width <= 0 || sidebarRect.Height <= 0)
            return Array.Empty<SidebarItem>();

        // --- OCR the whole sidebar -----------------------------------------------
        // Row detection is driven by the OCR'd item NAMES, not a pixel projection. The
        // projection cannot tell a separator strip from a column-saturating icon (both
        // read as full-width "high profile"), so it merged or dropped rows. The OCR
        // finds every item's name cleanly at the panel's fixed row stride.
        IReadOnlyList<OcrLine> ocrLines = Array.Empty<OcrLine>();
        List<OcrLine> sorted = new();
        if (_ocr.IsAvailable)
        {
            try
            {
                using OpenCvMat sidebarForOcr = new(bgrFrame, sidebarRect);
                ocrLines = _ocr.Recognize(sidebarForOcr);
                sorted = ocrLines.OrderBy(l => l.Bbox.Y).ToList();

                // Header values: capture threshold, score, turns made, turns left.
                System.Text.RegularExpressions.Regex thresholdRe = new(@"(\d+)\s*matches");
                foreach (OcrLine line in sorted)
                {
                    System.Text.RegularExpressions.Match m = thresholdRe.Match(line.Text);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int n))
                    {
                        CaptureThreshold = n;
                        break;
                    }
                }
                // The sidebar crop clips ~23px off the header labels' left edge, so
                // "Score:" survives OCR only as "core:" / "c re:". Match the tail "re:"
                // — unique to Score among the header rows — not "Score"/"core".
                int? scoreVal = FindValueBesideLabel(sorted, "re:", sidebarForOcr);
                if (scoreVal is int s && (Score is null || s >= Score)) Score = s;
                int? turnsMadeVal = FindValueBesideLabel(sorted, "made", sidebarForOcr);
                if (turnsMadeVal is int tm && (TurnsMade is null || tm >= TurnsMade)) TurnsMade = tm;
                int? turnsLeftVal = FindValueBesideLabel(sorted, "left", sidebarForOcr);
                if (turnsLeftVal is int tl) TurnsLeft = tl;
            }
            catch { }
        }

        // --- Item rows: anchored on the icon column ------------------------------
        // Each item row carries exactly ONE icon — a solid colour blob at the left,
        // evenly spaced, that never wraps. Item NAMES wrap to 1-3 lines and a wrapped
        // line can land closer to a neighbouring row's centre than to its own, so
        // grouping OCR'd name lines by Y-gap merged adjacent items. Rows are detected
        // from the icon blobs; name lines are then assigned to whichever row is nearest.
        List<int> rowCentersY = DetectIconRowCenters(bgrFrame, sidebarRect);

        // Individual (un-grouped) OCR name lines, with their frame-Y centres.
        List<(int FrameYCenter, string Text)> nameLines = new();
        foreach (OcrLine line in sorted)
        {
            if (line.Bbox.Y < ItemAreaTopInSidebar) continue;          // header region
            if (line.Bbox.X >= CountColumnLeftXInSidebar) continue;    // count column
            if (!line.Text.Any(char.IsLetter)) continue;               // stray digit
            // Reject OCR fragments whose centre sits in the ICON column. A busy icon (the
            // cricket especially) is sometimes misread as a few letters ("_ rid") and,
            // landing near a row centre, gets appended to that item's name. Real names are
            // centre-justified well right of the icon band (centres ~x=162); an icon
            // misread's centre falls inside it.
            int lineCenterX = line.Bbox.X + line.Bbox.Width / 2;
            if (lineCenterX <= IconBandRightFromSidebar) continue;
            nameLines.Add((sidebarRect.Y + line.Bbox.Y + line.Bbox.Height / 2, line.Text));
        }

        // --- Build an item per detected row --------------------------------------
        // The row grid's stride shrinks as the list fills up, so the icon search
        // window is derived from the measured stride — a fixed ±46 px window overlapped
        // the neighbouring icon once 7 rows packed the sidebar.
        int rowStride = rowCentersY.Count >= 2
            ? rowCentersY[1] - rowCentersY[0]
            : ExpectedRowStride;
        int iconSearchHalf = Math.Clamp(rowStride / 2, 28, IconSearchHalfHeight);
        List<SidebarItem> items = new();
        for (int i = 0; i < rowCentersY.Count; i++)
        {
            int rowY = rowCentersY[i];
            OpenCvRect iconRect = DetectIconRect(bgrFrame, sidebarRect, rowY, iconSearchHalf);
            if (iconRect.Width < 24 || iconRect.Height < 24) continue;
            OpenCvMat iconCrop = new OpenCvMat(bgrFrame, iconRect).Clone();
            SidebarItem item = new(items.Count, iconCrop, iconRect) { RowCenterY = rowY };

            // Name: every OCR name line whose nearest row is THIS one, top-to-bottom.
            // Per-line assignment keeps each line of a wrapped name with its own item
            // even when the 2-3 line block overflows the row bar.
            string name = string.Join(" ", nameLines
                .Where(nl => NearestRowIndex(rowCentersY, nl.FrameYCenter) == i)
                .OrderBy(nl => nl.FrameYCenter)
                .Select(nl => nl.Text));

            // Capture count: a digit OCR line in the count column at this row's Y.
            int? count = null;
            foreach (OcrLine line in sorted)
            {
                if (line.Bbox.X < CountColumnLeftXInSidebar) continue;
                int lyc = sidebarRect.Y + line.Bbox.Y + line.Bbox.Height / 2;
                if (Math.Abs(lyc - rowY) > rowStride / 2) continue;
                string digits = line.Text.Replace('O', '0').Replace('o', '0').Trim();
                if (digits.Length > 0 && digits.All(char.IsDigit)
                    && int.TryParse(digits, out int cn))
                {
                    count = cn;
                    break;
                }
            }
            // Strip any stray digit token the OCR merged into the name. PG lists new
            // items at 0 and OCR drops a lone "0", so a named item with no count is 0.
            if (!string.IsNullOrEmpty(name))
            {
                List<string> nameToks = new();
                foreach (string tok in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    string norm = tok.Replace('O', '0').Replace('o', '0');
                    if (norm.Length > 0 && norm.All(char.IsDigit)
                        && int.TryParse(norm, out int tn))
                    {
                        count ??= tn;
                        continue;
                    }
                    nameToks.Add(tok);
                }
                name = string.Join(' ', nameToks);
                count ??= 0;
            }
            item.Name = name;
            item.CaptureCount = count;

            // Captured-checkmark: a bright-green check at the right edge of the row.
            OpenCvRect checkRect = ClampToFrame(
                new OpenCvRect(sidebarRect.X + sidebarRect.Width - 90, rowY - 24, 75, 48),
                bgrFrame);
            if (checkRect.Width > 5 && checkRect.Height > 5)
            {
                using OpenCvMat checkRoi = new(bgrFrame, checkRect);
                using OpenCvMat greenMask = new();
                // Tight green: a real checkmark is saturated green (low R/B, high G).
                Cv2.InRange(checkRoi, new Scalar(20, 150, 20), new Scalar(140, 255, 150), greenMask);
                bool green = Cv2.CountNonZero(greenMask) > 30;
                // Game-rule gate: an item below the capture threshold CANNOT be captured.
                // A captured row shows a ✓ (count parses null/0); an uncaptured row shows
                // its real count — so a visible positive count below the threshold
                // overrides a false-positive green read.
                item.Captured = (item.CaptureCount is int cc && cc > 0 && CaptureThreshold is int th)
                    ? green && cc >= th
                    : green;
            }

            items.Add(item);
        }

        BuildNumbersMontage(bgrFrame, sidebarRect, items);

        // Debug dump — refreshed whenever the item count changes (a capture introduced
        // a new row), and only on a VALID read so a garbage mid-transition frame
        // (empty names, no icons) is never frozen as the permanent diagnostic.
        bool validRead = items.Count >= 3 && items.Any(it => !string.IsNullOrEmpty(it.Name));
        if (validRead && items.Count != _lastDumpedItemCount)
        {
            _lastDumpedItemCount = items.Count;
            try
            {
                Directory.CreateDirectory(DebugDir);
                using OpenCvMat sidebarCrop = new(bgrFrame, sidebarRect);
                Cv2.ImWrite(Path.Combine(DebugDir, "sidebar.png"), sidebarCrop);
                using OpenCvMat annotated = bgrFrame.Clone();
                Cv2.Rectangle(annotated, sidebarRect, new Scalar(0, 255, 0), 4);
                foreach (SidebarItem item in items)
                    Cv2.Rectangle(annotated, item.FrameRect, new Scalar(0, 0, 255), 3);
                Cv2.ImWrite(Path.Combine(DebugDir, "sidebar-annotated.png"), annotated);
                for (int i = 0; i < items.Count; i++)
                    Cv2.ImWrite(Path.Combine(DebugDir, $"icon-{i}.png"), items[i].Icon);
                File.WriteAllLines(Path.Combine(DebugDir, "names.txt"),
                    items.Select(it => $"{it.Index}: rowY={it.FrameRect.Y + it.FrameRect.Height / 2} " +
                        $"count={(it.CaptureCount?.ToString() ?? "-")} captured={it.Captured} name='{it.Name}'"));
                File.WriteAllLines(Path.Combine(DebugDir, "ocr-lines.txt"),
                    ocrLines.Select(l => $"x={l.Bbox.X} y={l.Bbox.Y} w={l.Bbox.Width} h={l.Bbox.Height} text='{l.Text}'"));
            }
            catch { }
        }

        return items;
    }

    private int? FindValueBesideLabel(
        IReadOnlyList<OcrLine> sortedLines,
        string labelSubstring,
        OpenCvMat sidebarCrop)
    {
        OcrLine? labelLine = null;
        foreach (OcrLine line in sortedLines)
        {
            if (line.Text.IndexOf(labelSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                labelLine = line;
                break;
            }
        }
        if (labelLine is null) return null;

        // Case 1: value lives on the same OCR line as the label (e.g. "Score: 250").
        System.Text.RegularExpressions.Match inline = System.Text.RegularExpressions.Regex.Match(
            labelLine.Text, @"(\d[\d,]*)\s*$");
        if (inline.Success && int.TryParse(inline.Groups[1].Value.Replace(",", ""), out int inlineN))
        {
            return inlineN;
        }

        // Case 2: value sits on its own line at a similar Y center.
        int labelYCenter = labelLine.Bbox.Y + labelLine.Bbox.Height / 2;
        foreach (OcrLine line in sortedLines)
        {
            if (line == labelLine) continue;
            int lineYCenter = line.Bbox.Y + line.Bbox.Height / 2;
            if (Math.Abs(lineYCenter - labelYCenter) > 15) continue;
            string normalized = NormalizeDigits(line.Text);
            if (normalized.Length > 0
                && normalized.All(char.IsDigit)
                && int.TryParse(normalized, out int n))
            {
                return n;
            }
        }

        // Case 3: Windows OCR drops small / single-digit values in busy images. Re-OCR
        // just the value column (right of the label, similar Y), upscaled 2× to give the
        // OCR engine larger, isolated digits to recognize.
        return RetryOcrValueColumn(sidebarCrop, labelLine);
    }

    private int? RetryOcrValueColumn(OpenCvMat sidebarCrop, OcrLine labelLine)
    {
        int yCenter = labelLine.Bbox.Y + labelLine.Bbox.Height / 2;
        int padY = labelLine.Bbox.Height;       // generous vertical padding
        int yTop = Math.Max(0, yCenter - padY);
        int yBot = Math.Min(sidebarCrop.Rows, yCenter + padY);
        int xStart = Math.Min(sidebarCrop.Cols - 1, labelLine.Bbox.X + labelLine.Bbox.Width + 30);
        int xEnd = sidebarCrop.Cols;
        int width = xEnd - xStart;
        int height = yBot - yTop;
        if (width < 20 || height < 12) return null;

        OpenCvRect cropRect = new(xStart, yTop, width, height);
        using OpenCvMat valueCrop = new(sidebarCrop, cropRect);
        using OpenCvMat scaled = new();
        Cv2.Resize(
            valueCrop,
            scaled,
            new Size(width * 3, height * 3),
            0, 0,
            InterpolationFlags.Cubic);

        IReadOnlyList<OcrLine> retryLines = _ocr.Recognize(scaled);
        foreach (OcrLine line in retryLines)
        {
            string normalized = NormalizeDigits(line.Text);
            if (normalized.Length > 0
                && normalized.All(char.IsDigit)
                && int.TryParse(normalized, out int n))
            {
                return n;
            }
        }
        return null;
    }

    private static string NormalizeDigits(string text)
    {
        return text.Trim()
            .Replace(",", "")
            .Replace("O", "0").Replace("o", "0")
            .Replace("l", "1").Replace("I", "1")
            .Replace("Z", "2")
            .Replace("S", "5")
            .Replace("B", "8");
    }

    private static void AddSegmentRows(List<int> rowCentersY, int scanYOrigin, int segStart, int segEnd, int minLen, int expectedStride)
    {
        int len = segEnd - segStart + 1;
        if (len < minLen) return;
        // A segment longer than ~1.4x stride likely contains multiple icons (adjacent items
        // both saturating the column mask). Split into N equal-width sub-rows.
        int subCount = Math.Max(1, (int)Math.Round(len / (double)expectedStride));
        int subLen = len / subCount;
        for (int s = 0; s < subCount; s++)
        {
            int subStart = segStart + s * subLen;
            int subEnd = (s == subCount - 1) ? segEnd : (subStart + subLen - 1);
            int midLocal = (subStart + subEnd) / 2;
            rowCentersY.Add(scanYOrigin + midLocal);
        }
    }

    /// <summary>
    /// Row centres (frame Y) detected from the icon column. Each item row carries one
    /// solid colour blob, evenly spaced, that never wraps — a far more reliable anchor
    /// than grouping OCR'd name lines, which wrap across row boundaries. A blob run
    /// longer than ~1.4x the stride (two adjacent icons, no brown gap) is split.
    /// </summary>
    private static List<int> DetectIconRowCenters(OpenCvMat bgrFrame, OpenCvRect sidebarRect)
    {
        List<int> centers = new();
        int bandL = Math.Max(0, sidebarRect.X + IconBandLeftFromSidebar);
        int bandR = Math.Min(bgrFrame.Cols - 1, sidebarRect.X + IconRowScanBandRight);
        int yTop = Math.Max(0, sidebarRect.Y + ItemAreaTopInSidebar);
        int yBot = Math.Min(bgrFrame.Rows - 1, sidebarRect.Y + IconRowScanBottom);
        if (bandR - bandL < 20 || yBot - yTop < 40) return centers;

        Mat.Indexer<Vec3b> px = bgrFrame.GetGenericIndexer<Vec3b>();
        int runStart = -1;
        for (int y = yTop; y <= yBot; y++)
        {
            int cnt = 0;
            for (int x = bandL; x <= bandR; x++)
                if (IsIconPixel(px[y, x])) cnt++;
            bool isIconRow = cnt >= IconRowMinPixels;
            if (isIconRow && runStart < 0) runStart = y;
            else if (!isIconRow && runStart >= 0)
            {
                AddSegmentRows(centers, 0, runStart, y - 1, IconRowMinHeight, ExpectedRowStride);
                runStart = -1;
            }
        }
        if (runStart >= 0)
            AddSegmentRows(centers, 0, runStart, yBot, IconRowMinHeight, ExpectedRowStride);
        return centers;
    }

    // NOTE: an earlier version snapped these centres onto a least-squares even-spaced
    // grid to absorb icon-artwork jitter. That ASSUMED bars are equal height, which
    // is FALSE — a multi-line name (e.g. "Armor Potion Extreme") makes its bar
    // visibly taller, so the row stride between items above and below it differs
    // from the rest. Fitting an even grid through a non-uniform stride pulls every
    // row off its true centre AND pushes the bottom rows past the scan cutoff,
    // losing items. Raw run centres are correct (±few px) for every item count
    // including 7-item full sidebars; the small artwork-centre jitter is well
    // within the count-crop's 48-px window.

    /// <summary>Index of the row centre nearest <paramref name="y"/>, or -1 if none.</summary>
    private static int NearestRowIndex(IReadOnlyList<int> rowCentersY, int y)
    {
        int best = -1, bestD = int.MaxValue;
        for (int i = 0; i < rowCentersY.Count; i++)
        {
            int d = Math.Abs(rowCentersY[i] - y);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Find one sidebar item's icon and return a SQUARE crop rect (frame coords).
    ///
    /// The icon is a colourful blob at the left of the row bar; the row bar and the
    /// surrounding panel are low-saturation brown, and the item name is warm-cream text —
    /// so the icon is the leftmost non-brown / non-text blob. The blob overhangs the
    /// captured sidebar rect's left edge, so the search window deliberately reaches left
    /// of <paramref name="sidebarRect"/> and the crop is taken from the full frame. The
    /// crop is a square centred on the detected blob, sized to the blob × a constant
    /// margin (<see cref="IconCropMargin"/>) — tight to the artwork, consistent zoom.
    /// Falls back to a fixed geometric crop if no blob is found.
    /// </summary>
    private static OpenCvRect DetectIconRect(OpenCvMat bgrFrame, OpenCvRect sidebarRect,
        int rowCenterY, int searchHalfHeight)
    {
        OpenCvRect fallback = ClampToFrame(new OpenCvRect(
            sidebarRect.X + IconFallbackCenterFromSidebar - IconFallbackHalfSize,
            rowCenterY - IconFallbackHalfSize,
            IconFallbackHalfSize * 2, IconFallbackHalfSize * 2), bgrFrame);

        OpenCvRect win = ClampToFrame(new OpenCvRect(
            sidebarRect.X + IconSearchLeftFromSidebar,
            rowCenterY - searchHalfHeight,
            IconSearchRightFromSidebar - IconSearchLeftFromSidebar,
            searchHalfHeight * 2), bgrFrame);
        if (win.Width < 50 || win.Height < 50) return fallback;

        using OpenCvMat roi = new(bgrFrame, win);
        int w = roi.Cols, h = roi.Rows;
        Mat.Indexer<Vec3b> px = roi.GetGenericIndexer<Vec3b>();
        int[] colCount = new int[w];
        int[] firstY = new int[w];
        int[] lastY = new int[w];
        for (int x = 0; x < w; x++) { firstY[x] = -1; lastY[x] = -1; }
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!IsIconPixel(px[y, x])) continue;
                colCount[x]++;
                if (firstY[x] < 0) firstY[x] = y;
                lastY[x] = y;
            }
        }

        // The icon lives in a fixed column BAND at the left of the row; the item name is
        // always well to the right of it. Confining detection to that band makes text
        // bleed impossible — so growth can bridge gaps as aggressively as a hollow icon
        // needs without ever reaching the name.
        int bandL = Math.Max(0, (sidebarRect.X + IconBandLeftFromSidebar) - win.X);
        int bandR = Math.Min(w - 1, (sidebarRect.X + IconBandRightFromSidebar) - win.X);
        if (bandR - bandL < 20) return fallback;

        // Grow the icon span outward from its densest column. A hollow or beaded icon —
        // the necklace loop especially — dips to near-zero between strands, so a
        // left-to-right "sustained run" stops partway and the crop loses most of the
        // icon. Growing from the densest column (always deep inside the artwork) both
        // ways, bridging wide internal gaps, captures the whole shape.
        const int ColStay = 3, MaxGap = 16;
        int coreX = bandL;
        for (int x = bandL; x <= bandR; x++)
            if (colCount[x] > colCount[coreX]) coreX = x;
        if (colCount[coreX] < 10) return fallback;   // no real icon in the band

        int iconL = coreX, iconR = coreX, gap = 0;
        for (int x = coreX - 1; x >= bandL; x--)
        {
            if (colCount[x] >= ColStay) { iconL = x; gap = 0; }
            else if (++gap >= MaxGap) break;
        }
        gap = 0;
        for (int x = coreX + 1; x <= bandR; x++)
        {
            if (colCount[x] >= ColStay) { iconR = x; gap = 0; }
            else if (++gap >= MaxGap) break;
        }

        int top = int.MaxValue, bot = -1;
        for (int x = iconL; x <= iconR; x++)
        {
            if (firstY[x] >= 0 && firstY[x] < top) top = firstY[x];
            if (lastY[x] > bot) bot = lastY[x];
        }
        if (bot < 0) return fallback;

        // Crop = the detected artwork bbox scaled by a CONSTANT margin: every icon fills
        // the same fraction of its crop (consistent zoom) while the crop stays tight to
        // the artwork. A fixed pixel size was tried and reverted — it left small icons
        // swimming in background and inflated every match distance roughly 2x.
        int bw = iconR - iconL + 1, bh = bot - top + 1;
        int side = (int)Math.Round(Math.Max(bw, bh) * IconCropMargin);
        int cx = win.X + (iconL + iconR) / 2;
        int cy = win.Y + (top + bot) / 2;
        OpenCvRect square = ClampToFrame(
            new OpenCvRect(cx - side / 2, cy - side / 2, side, side), bgrFrame);
        return (square.Width >= 24 && square.Height >= 24) ? square : fallback;
    }

    /// <summary>
    /// True if a pixel belongs to an icon — i.e. NOT the white item text and NOT the
    /// low-saturation brown of the row bar / panel background. Saturated golds (the yarn
    /// spool) survive the brown test because brown is gated to low saturation.
    /// </summary>
    private static bool IsIconPixel(Vec3b bgr)
    {
        int b = bgr.Item0, g = bgr.Item1, r = bgr.Item2;
        // Only the low-saturation brown of the row bar / panel is excluded. No colour
        // test for the item NAME is needed — DetectIconRect confines detection to a
        // geometric band the text lies outside of, and a colour-based text filter risked
        // eating pale or cream icons (white glass, flour). Brown is always R >= G >= B.
        int mx = Math.Max(r, Math.Max(g, b));
        int mn = Math.Min(r, Math.Min(g, b));
        double sat = mx > 0 ? (mx - mn) / (double)mx : 0.0;
        if (sat < 0.58 && r >= 55 && r <= 205 && r >= g && g >= b)
        {
            double gr = g / (double)r, br = b / (double)r;
            if (gr >= 0.68 && br >= 0.30 && br <= 0.80) return false;
        }
        return true;
    }

    /// <summary>
    /// Build the OCR-numbers debug montage: one row per item — the cropped count-column
    /// region (count digits + checkmark area, exactly what the count parse / capture
    /// detector see) next to the parsed value — so the number reads can be eyeballed.
    /// </summary>
    private void BuildNumbersMontage(OpenCvMat bgrFrame, OpenCvRect sidebarRect,
        List<SidebarItem> items)
    {
        try
        {
            const int cropW = 150, rowH = 46;
            int rows = Math.Max(items.Count, 1);
            using OpenCvMat montage = new(rows * rowH, cropW + 300, MatType.CV_8UC3, Scalar.All(28));
            for (int i = 0; i < items.Count; i++)
            {
                SidebarItem it = items[i];
                int rowCenterY = it.RowCenterY;
                int y0 = i * rowH;
                // The count column + checkmark area — what the count parse / capture
                // detector consume. Anchored on the regularised row centre and the
                // count column's left edge so neither the label nor a neighbour bleeds in.
                OpenCvRect numRect = ClampToFrame(new OpenCvRect(
                    sidebarRect.X + CountColumnLeftXInSidebar, rowCenterY - 24,
                    Math.Max(1, sidebarRect.Width - CountColumnLeftXInSidebar), 48), bgrFrame);
                if (numRect.Width > 4 && numRect.Height > 4)
                {
                    using OpenCvMat crop = new(bgrFrame, numRect);
                    using OpenCvMat resized = new();
                    Cv2.Resize(crop, resized, new Size(cropW, rowH - 6));
                    resized.CopyTo(montage[new OpenCvRect(0, y0 + 3, cropW, rowH - 6)]);
                }
                string name = it.Name.Length > 16 ? it.Name.Substring(0, 16) : it.Name;
                if (string.IsNullOrEmpty(name)) name = $"row{i}";
                string label = $"{name} = {(it.CaptureCount?.ToString() ?? "-")}"
                    + (it.Captured ? "  [DONE]" : "");
                Cv2.PutText(montage, label, new Point(cropW + 8, y0 + 29),
                    HersheyFonts.HersheySimplex, 0.55, new Scalar(90, 255, 255), 1);
            }
            Cv2.ImEncode(".png", montage, out byte[] png);
            LastNumbersMontagePng = png;
        }
        catch
        {
            // Debug aid only — never let it disturb the read.
        }
    }

    private static OpenCvRect ClampToFrame(OpenCvRect r, OpenCvMat frame)
    {
        int x = Math.Max(0, r.X);
        int y = Math.Max(0, r.Y);
        int w = Math.Min(r.Width, frame.Cols - x);
        int h = Math.Min(r.Height, frame.Rows - y);
        return new OpenCvRect(x, y, w, h);
    }

    private static bool HasMaskContentNear(OpenCvMat scanMask, OpenCvRect scanRect, int frameY, int radius)
    {
        int localCenter = frameY - scanRect.Y;
        int yTop = Math.Max(0, localCenter - radius);
        int yBot = Math.Min(scanMask.Rows, localCenter + radius);
        if (yBot <= yTop) return false;
        using OpenCvMat band = new(scanMask, new OpenCvRect(0, yTop, scanMask.Cols, yBot - yTop));
        return Cv2.CountNonZero(band) > 25;
    }
}
