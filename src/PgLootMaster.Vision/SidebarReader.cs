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
    private const int IconCropXInSidebar = 20;
    private const int IconCropSize = 55;
    private const int MinBlobHeight = 18;
    private const int RowMergeYDistance = 45;
    private const int RowStridePx = 105;

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

        OpenCvRect scanRect = new(
            sidebarRect.X + IconColumnXInSidebar,
            sidebarRect.Y + ScanStartYInSidebar,
            Math.Min(IconColumnWidth, sidebarRect.Width - IconColumnXInSidebar),
            Math.Min(ScanEndYInSidebar - ScanStartYInSidebar, sidebarRect.Height - ScanStartYInSidebar));
        scanRect = ClampToFrame(scanRect, bgrFrame);
        if (scanRect.Width < 30 || scanRect.Height < 30)
            return Array.Empty<SidebarItem>();

        using OpenCvMat scanRoi = new(bgrFrame, scanRect);

        // Mask the tan/brown sidebar background. Anything outside this range is icon content.
        using OpenCvMat tanMask = new();
        Cv2.InRange(
            scanRoi,
            new Scalar(60, 90, 120),
            new Scalar(140, 170, 200),
            tanMask);
        using OpenCvMat nonTanMask = new();
        Cv2.BitwiseNot(tanMask, nonTanMask);

        // Light closing kernel: fill small holes inside an icon without bridging adjacent rows.
        using OpenCvMat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(nonTanMask, nonTanMask, MorphTypes.Close, kernel);

        // 1D vertical projection: sum non-tan pixels per row, smooth, find contiguous segments.
        // Each segment is one item row. Segment midpoint = row center.
        int scanH = nonTanMask.Rows;
        int[] profile = new int[scanH];
        for (int y = 0; y < scanH; y++)
        {
            profile[y] = Cv2.CountNonZero(nonTanMask.Row(y));
        }
        // Sidebar layout: rows alternate between SEPARATOR strips (profile near full column width,
        // a slightly different tan shade) and ITEM rows (profile 3-40 — sparse icon pixels on tan).
        // Identify rows as "icon row" only when profile is moderate (icon present, not the
        // wide separator band). Then find contiguous icon-row stretches.
        int colWidth = scanRect.Width;
        int sepCutoff = (int)(colWidth * 0.7);
        int[] smoothed = new int[scanH];
        int smoothWindow = 5;
        for (int y = 0; y < scanH; y++)
        {
            int sum = 0;
            for (int dy = -smoothWindow; dy <= smoothWindow; dy++)
            {
                int yy = y + dy;
                if (yy >= 0 && yy < scanH) sum += profile[yy];
            }
            smoothed[y] = sum / (2 * smoothWindow + 1);
        }

        // First pass: moderate-profile rows are clearly icons. Separator strips have profile
        // near full column width (>= sepCutoff). But some items (Mercury — silvery, doesn't
        // match the tan mask) saturate to profile=80 throughout the icon, indistinguishable
        // from a separator on its own. Disambiguate by stretch length: ≤30 rows = separator,
        // > 30 rows = icon row (real icons are ~70-100 rows tall).
        bool[] isIconRow = new bool[scanH];
        int highStart = -1;
        for (int y = 0; y < scanH; y++)
        {
            bool moderate = smoothed[y] >= 2 && smoothed[y] < sepCutoff;
            bool high = smoothed[y] >= sepCutoff;
            isIconRow[y] = moderate;
            // Track high-profile stretches; if a stretch is too long to be a separator,
            // backfill it as icon rows.
            if (high && highStart == -1) highStart = y;
            else if (!high && highStart != -1)
            {
                int len = y - highStart;
                if (len > 55)
                {
                    for (int k = highStart; k < y; k++) isIconRow[k] = true;
                }
                highStart = -1;
            }
        }
        // Re-promote a high-profile stretch that runs to scan end IF it's substantial.
        // With ScanEndYInSidebar set just inside the panel bottom, the stone wall is past
        // scan end. A real last-item icon (e.g. Mercury silvery) at the very bottom gives
        // a high-profile stretch reaching scan end with length ≥ ~50 (an icon's height).
        // The 4-item case has no stretch touching scan end because everything is empty tan.
        if (highStart != -1 && scanH - highStart >= 50)
        {
            for (int k = highStart; k < scanH; k++) isIconRow[k] = true;
        }

        List<int> rowCentersY = new();
        int segStart = -1;
        int minSegmentLen = 15;
        const int ExpectedRowStride = 80;
        for (int y = 0; y < scanH; y++)
        {
            if (isIconRow[y] && segStart == -1)
            {
                segStart = y;
            }
            else if (!isIconRow[y] && segStart != -1)
            {
                int segEnd = y - 1;
                AddSegmentRows(rowCentersY, scanRect.Y, segStart, segEnd, minSegmentLen, ExpectedRowStride);
                segStart = -1;
            }
        }
        if (segStart != -1)
        {
            AddSegmentRows(rowCentersY, scanRect.Y, segStart, scanH - 1, minSegmentLen, ExpectedRowStride);
        }

        // Gap-fill: if two consecutive detected rows are >1.5x stride apart, the row between
        // them was missed (e.g., adjacent silvery items both fully saturating the column mask).
        // Insert evenly-spaced rows in the gap to recover them.
        if (rowCentersY.Count >= 2)
        {
            List<int> filled = new() { rowCentersY[0] };
            for (int i = 1; i < rowCentersY.Count; i++)
            {
                int gap = rowCentersY[i] - rowCentersY[i - 1];
                int extras = (int)Math.Round(gap / (double)ExpectedRowStride) - 1;
                for (int e = 1; e <= extras; e++)
                {
                    int y = rowCentersY[i - 1] + e * gap / (extras + 1);
                    filled.Add(y);
                }
                filled.Add(rowCentersY[i]);
            }
            rowCentersY = filled;
        }

        // For each row center, snap to a fixed icon rect at a fixed X.
        int iconXFrame = sidebarRect.X + IconCropXInSidebar;
        List<SidebarItem> items = new();
        for (int i = 0; i < rowCentersY.Count; i++)
        {
            OpenCvRect iconRect = new(
                iconXFrame,
                rowCentersY[i] - IconCropSize / 2,
                IconCropSize,
                IconCropSize);
            iconRect = ClampToFrame(iconRect, bgrFrame);
            if (iconRect.Width < IconCropSize - 5 || iconRect.Height < IconCropSize - 5) continue;
            OpenCvMat iconCrop = new OpenCvMat(bgrFrame, iconRect).Clone();
            SidebarItem item = new(i, iconCrop, iconRect);

            // Captured-checkmark detection: the right edge of each row shows a bright green
            // checkmark for captured items. Sample a small box at the right side of the row.
            OpenCvRect checkRect = new(
                sidebarRect.X + sidebarRect.Width - 90,
                rowCentersY[i] - 24,
                75,
                48);
            checkRect = ClampToFrame(checkRect, bgrFrame);
            if (checkRect.Width > 5 && checkRect.Height > 5)
            {
                using OpenCvMat checkRoi = new(bgrFrame, checkRect);
                using OpenCvMat greenMask = new();
                Cv2.InRange(checkRoi, new Scalar(20, 140, 20), new Scalar(180, 255, 180), greenMask);
                int greenCount = Cv2.CountNonZero(greenMask);
                item.Captured = greenCount > 30;
                try
                {
                    Cv2.ImWrite(Path.Combine(DebugDir, $"check-{i}.png"), checkRoi);
                    Cv2.ImWrite(Path.Combine(DebugDir, $"check-mask-{i}.png"), greenMask);
                }
                catch { }
            }

            items.Add(item);
        }

        // OCR the sidebar to extract item names. Match each OCR line to the row whose Y is
        // closest. Sort lines by Y first so multi-line wrapped names (e.g. "Extreme Healing /
        // Potion") join in natural reading order.
        IReadOnlyList<OcrLine> ocrLines = Array.Empty<OcrLine>();
        if (_ocr.IsAvailable && items.Count > 0)
        {
            try
            {
                using OpenCvMat sidebarCropForOcr = new(bgrFrame, sidebarRect);
                ocrLines = _ocr.Recognize(sidebarCropForOcr);
                List<OcrLine> sorted = ocrLines.OrderBy(l => l.Bbox.Y).ToList();

                // Parse the shared capture threshold from the header help text.
                // Text varies slightly between captures: "the next item with 30 matches is".
                // Look for an integer followed by "matches" anywhere in the OCR text.
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

                // Parse the three header numeric values. OCR reads the label noisily (e.g.
                // "Irns Left:" instead of "Turns Left:"), and the digit value sits on a
                // separate line at a similar Y but further right. Find each label by a
                // distinctive substring, then read the value beside it — first via the main
                // OCR pass, falling back to a targeted re-OCR of just the value column
                // (Windows OCR misses small isolated single-digit values in large images).
                // OCR commonly drops the leading "S" of "Score:" — match on "core".
                int? scoreVal = FindValueBesideLabel(sorted, "core", sidebarCropForOcr);
                if (scoreVal is int s && (Score is null || s >= Score))
                    Score = s;

                int? turnsMadeVal = FindValueBesideLabel(sorted, "made", sidebarCropForOcr);
                if (turnsMadeVal is int tm && (TurnsMade is null || tm >= TurnsMade))
                    TurnsMade = tm;

                int? turnsLeftVal = FindValueBesideLabel(sorted, "left", sidebarCropForOcr);
                if (turnsLeftVal is int tl) TurnsLeft = tl;

                foreach (OcrLine line in sorted)
                {
                    int lineYInFrame = sidebarRect.Y + line.Bbox.Y + line.Bbox.Height / 2;
                    // Skip header text (Score, Turns Left, help text) — anything well above the first item.
                    if (items.Count > 0 && lineYInFrame < items[0].FrameRect.Y - RowStridePx / 2) continue;
                    SidebarItem? nearest = null;
                    int nearestDist = int.MaxValue;
                    foreach (SidebarItem it in items)
                    {
                        int itemYCenter = it.FrameRect.Y + it.FrameRect.Height / 2;
                        int dist = Math.Abs(itemYCenter - lineYInFrame);
                        if (dist < nearestDist) { nearestDist = dist; nearest = it; }
                    }
                    if (nearest is not null && nearestDist <= RowStridePx / 2)
                    {
                        if (nearest.Name.Length > 0)
                            nearest.Name = nearest.Name + " " + line.Text;
                        else
                            nearest.Name = line.Text;
                    }
                }

                // Pull the capture-count number out of each item's name. The count is OCR'd as
                // a pure-digit token (e.g. "Pixie Sugar 12"). For wrapped names where the count
                // appears mid-string due to the wrap rejoin ("Healing Potion 3 Extreme"), find
                // the standalone digit token, strip it, and treat the remaining words as the name.
                foreach (SidebarItem it in items)
                {
                    if (string.IsNullOrEmpty(it.Name)) continue;
                    string[] tokens = it.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    int? count = null;
                    List<string> nameTokens = new();
                    foreach (string tok in tokens)
                    {
                        // OCR commonly reads digit "0" as letter "O" (e.g. "Fillet O" really
                        // means count=0). Normalize O/o → 0 before checking if the whole token
                        // is digits. Word tokens like "Iocaine"/"Cotton" still won't qualify
                        // since they contain non-digit/non-O characters.
                        string normalized = tok.Replace('O', '0').Replace('o', '0');
                        if (normalized.Length > 0
                            && normalized.All(char.IsDigit)
                            && int.TryParse(normalized, out int n))
                        {
                            count = n;
                            continue;
                        }
                        nameTokens.Add(tok);
                    }
                    // OCR drops single-digit "0" counts entirely (engine quirk). When the name
                    // was parsed but no count token was found, default to 0 — that matches
                    // newly-listed items in PG which start at 0.
                    if (count is null && nameTokens.Count > 0)
                    {
                        count = 0;
                    }
                    it.CaptureCount = count;
                    it.Name = string.Join(' ', nameTokens);
                }
            }
            catch { }
        }

        if (Interlocked.Increment(ref _debugDumped) == 1)
        {
            try
            {
                Directory.CreateDirectory(DebugDir);
                using OpenCvMat sidebarCrop = new(bgrFrame, sidebarRect);
                Cv2.ImWrite(Path.Combine(DebugDir, "sidebar.png"), sidebarCrop);
                Cv2.ImWrite(Path.Combine(DebugDir, "scan-roi.png"), scanRoi);
                Cv2.ImWrite(Path.Combine(DebugDir, "non-tan-mask.png"), nonTanMask);
                List<string> profileLines = new();
                for (int y = 0; y < scanH; y++)
                {
                    profileLines.Add($"{y},{profile[y]},{smoothed[y]}");
                }
                File.WriteAllLines(Path.Combine(DebugDir, "profile.csv"), profileLines);
                using OpenCvMat annotated = bgrFrame.Clone();
                Cv2.Rectangle(annotated, sidebarRect, new Scalar(0, 255, 0), 4);
                Cv2.Rectangle(annotated, scanRect, new Scalar(255, 255, 0), 2);
                foreach (SidebarItem item in items)
                {
                    Cv2.Rectangle(annotated, item.FrameRect, new Scalar(0, 0, 255), 3);
                }
                Cv2.ImWrite(Path.Combine(DebugDir, "sidebar-annotated.png"), annotated);
                for (int i = 0; i < items.Count; i++)
                {
                    Cv2.ImWrite(Path.Combine(DebugDir, $"icon-{i}.png"), items[i].Icon);
                }
                File.WriteAllLines(
                    Path.Combine(DebugDir, "names.txt"),
                    items.Select(it =>
                        $"{it.Index}: rowY={it.FrameRect.Y + it.FrameRect.Height / 2} count={(it.CaptureCount?.ToString() ?? "-")} captured={it.Captured} name='{it.Name}'"));
                File.WriteAllLines(
                    Path.Combine(DebugDir, "ocr-lines.txt"),
                    ocrLines.Select(l =>
                        $"x={l.Bbox.X} y={l.Bbox.Y} w={l.Bbox.Width} h={l.Bbox.Height} text='{l.Text}'"));
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
