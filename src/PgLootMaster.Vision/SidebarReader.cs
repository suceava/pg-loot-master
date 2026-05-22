using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;
using OpenCvRect = OpenCvSharp.Rect;

namespace PgLootMaster.Vision;

public sealed class SidebarItem
{
    public int Index { get; }
    public OpenCvMat Icon { get; }
    public OpenCvRect FrameRect { get; }
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
    // Item rows are read from the OCR'd item NAMES. A name line is OCR text below
    // ItemAreaTopInSidebar (the header sits above) and left of ItemNameMaxXInSidebar
    // (the capture-count numbers sit right of that). OCR lines closer than
    // RowGroupMaxGap belong to the same row — a wrapped two-line name. Rows step by
    // roughly ExpectedRowStride, used only to gap-fill a row whose name OCR missed.
    private const int ItemAreaTopInSidebar = 380;
    private const int ItemNameMaxXInSidebar = 255;
    private const int RowGroupMaxGap = 48;
    private const int ExpectedRowStride = 81;

    private static int _debugDumped;
    private static readonly string DebugDir = Path.Combine(Path.GetTempPath(), "pg-loot-master-sidebar-debug");

    private readonly SidebarOcr _ocr = new();

    // Shared "next item with N matches is yours to keep!" threshold, parsed from sidebar OCR.
    // null until first successful read.
    public int? CaptureThreshold { get; private set; }

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
        // Re-arm the one-shot debug dump so each new game writes fresh icon crops.
        _debugDumped = 0;
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
                int? scoreVal = FindValueBesideLabel(sorted, "core", sidebarForOcr);
                if (scoreVal is int s && (Score is null || s >= Score)) Score = s;
                int? turnsMadeVal = FindValueBesideLabel(sorted, "made", sidebarForOcr);
                if (turnsMadeVal is int tm && (TurnsMade is null || tm >= TurnsMade)) TurnsMade = tm;
                int? turnsLeftVal = FindValueBesideLabel(sorted, "left", sidebarForOcr);
                if (turnsLeftVal is int tl) TurnsLeft = tl;
            }
            catch { }
        }

        // --- Item rows from the OCR name lines -----------------------------------
        // A name line is OCR text below the header and left of the count column. Lines
        // within RowGroupMaxGap of each other are the same row (a wrapped name like
        // "Power Potion" / "Extreme"); a larger jump starts a new row.
        List<(int CenterY, string Name)> ocrRows = new();
        foreach (OcrLine line in sorted)
        {
            if (line.Bbox.Y < ItemAreaTopInSidebar) continue;      // header region
            if (line.Bbox.X >= ItemNameMaxXInSidebar) continue;    // count column
            if (!line.Text.Any(char.IsLetter)) continue;           // stray digit
            int yc = line.Bbox.Y + line.Bbox.Height / 2;
            if (ocrRows.Count > 0 && yc - ocrRows[^1].CenterY <= RowGroupMaxGap)
            {
                (int cy, string nm) = ocrRows[^1];
                ocrRows[^1] = ((cy + yc) / 2, nm + " " + line.Text);
            }
            else
            {
                ocrRows.Add((yc, line.Text));
            }
        }

        List<int> rowCentersY = ocrRows.Select(r => sidebarRect.Y + r.CenterY).ToList();

        // Gap-fill: a row whose name OCR failed entirely leaves a >1.5x-stride hole.
        // Insert evenly-spaced centres so the icon is still cropped (icon matching
        // works without a name).
        if (rowCentersY.Count >= 2)
        {
            List<int> filled = new() { rowCentersY[0] };
            for (int i = 1; i < rowCentersY.Count; i++)
            {
                int gap = rowCentersY[i] - rowCentersY[i - 1];
                int extras = (int)Math.Round(gap / (double)ExpectedRowStride) - 1;
                for (int e = 1; e <= extras; e++)
                    filled.Add(rowCentersY[i - 1] + e * gap / (extras + 1));
                filled.Add(rowCentersY[i]);
            }
            rowCentersY = filled;
        }

        // --- Build an item per detected row --------------------------------------
        List<SidebarItem> items = new();
        for (int i = 0; i < rowCentersY.Count; i++)
        {
            int rowY = rowCentersY[i];
            OpenCvRect iconRect = DetectIconRect(bgrFrame, sidebarRect, rowY);
            if (iconRect.Width < 24 || iconRect.Height < 24) continue;
            OpenCvMat iconCrop = new OpenCvMat(bgrFrame, iconRect).Clone();
            SidebarItem item = new(items.Count, iconCrop, iconRect);

            // Name: the OCR row group whose centre is nearest this row.
            string name = "";
            int bestD = ExpectedRowStride / 2;
            foreach ((int cy, string nm) in ocrRows)
            {
                int d = Math.Abs((sidebarRect.Y + cy) - rowY);
                if (d < bestD) { bestD = d; name = nm; }
            }

            // Capture count: a digit OCR line in the count column at this row's Y.
            int? count = null;
            foreach (OcrLine line in sorted)
            {
                if (line.Bbox.X < ItemNameMaxXInSidebar) continue;
                int lyc = sidebarRect.Y + line.Bbox.Y + line.Bbox.Height / 2;
                if (Math.Abs(lyc - rowY) > ExpectedRowStride / 2) continue;
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
                Cv2.InRange(checkRoi, new Scalar(20, 140, 20), new Scalar(180, 255, 180), greenMask);
                item.Captured = Cv2.CountNonZero(greenMask) > 30;
            }

            items.Add(item);
        }

        // One-shot debug dump — but only on a VALID read, so it doesn't freeze a garbage
        // mid-transition frame (empty names, no icons) as the permanent diagnostic.
        bool validRead = items.Count >= 3 && items.Any(it => !string.IsNullOrEmpty(it.Name));
        if (validRead && Interlocked.Increment(ref _debugDumped) == 1)
        {
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
    private static OpenCvRect DetectIconRect(OpenCvMat bgrFrame, OpenCvRect sidebarRect, int rowCenterY)
    {
        OpenCvRect fallback = ClampToFrame(new OpenCvRect(
            sidebarRect.X + IconFallbackCenterFromSidebar - IconFallbackHalfSize,
            rowCenterY - IconFallbackHalfSize,
            IconFallbackHalfSize * 2, IconFallbackHalfSize * 2), bgrFrame);

        OpenCvRect win = ClampToFrame(new OpenCvRect(
            sidebarRect.X + IconSearchLeftFromSidebar,
            rowCenterY - IconSearchHalfHeight,
            IconSearchRightFromSidebar - IconSearchLeftFromSidebar,
            IconSearchHalfHeight * 2), bgrFrame);
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
