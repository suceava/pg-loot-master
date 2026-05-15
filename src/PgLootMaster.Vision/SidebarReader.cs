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
    private const int ScanEndYInSidebar = 900;
    private const int IconCropXInSidebar = 20;
    private const int IconCropSize = 55;
    private const int MinBlobHeight = 18;
    private const int RowMergeYDistance = 45;
    private const int RowStridePx = 105;

    private static int _debugDumped;
    private static readonly string DebugDir = Path.Combine(Path.GetTempPath(), "pg-loot-master-sidebar-debug");

    private readonly SidebarOcr _ocr = new();

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

        bool[] isIconRow = new bool[scanH];
        for (int y = 0; y < scanH; y++)
        {
            isIconRow[y] = smoothed[y] >= 2 && smoothed[y] < sepCutoff;
        }

        List<int> rowCentersY = new();
        int segStart = -1;
        int minSegmentLen = 15;
        for (int y = 0; y < scanH; y++)
        {
            if (isIconRow[y] && segStart == -1)
            {
                segStart = y;
            }
            else if (!isIconRow[y] && segStart != -1)
            {
                int segEnd = y - 1;
                if (segEnd - segStart + 1 >= minSegmentLen)
                {
                    int midLocal = (segStart + segEnd) / 2;
                    rowCentersY.Add(scanRect.Y + midLocal);
                }
                segStart = -1;
            }
        }
        if (segStart != -1 && scanH - segStart >= minSegmentLen)
        {
            int midLocal = (segStart + scanH - 1) / 2;
            rowCentersY.Add(scanRect.Y + midLocal);
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
            items.Add(new SidebarItem(i, iconCrop, iconRect));
        }

        // OCR the sidebar to extract item names. Match each OCR line to the row whose Y is closest.
        IReadOnlyList<OcrLine> ocrLines = Array.Empty<OcrLine>();
        if (_ocr.IsAvailable && items.Count > 0)
        {
            try
            {
                using OpenCvMat sidebarCropForOcr = new(bgrFrame, sidebarRect);
                ocrLines = _ocr.Recognize(sidebarCropForOcr);
                foreach (OcrLine line in ocrLines)
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
                        $"{it.Index}: rowY={it.FrameRect.Y + it.FrameRect.Height / 2} name='{it.Name}'"));
                File.WriteAllLines(
                    Path.Combine(DebugDir, "ocr-lines.txt"),
                    ocrLines.Select(l =>
                        $"x={l.Bbox.X} y={l.Bbox.Y} w={l.Bbox.Width} h={l.Bbox.Height} text='{l.Text}'"));
            }
            catch { }
        }

        return items;
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
