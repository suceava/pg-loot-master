using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;
using OpenCvRect = OpenCvSharp.Rect;

namespace PgLootMaster.Vision;

public sealed class BoardExtractor
{
    public const int GridDim = 7;

    private const int CellMinSize = 60;
    private const int CellMaxSize = 200;
    private const double CellMinAspect = 0.7;
    private const double CellMaxAspect = 1.4;

    private static int _debugCount;
    private static readonly string DebugDir = Path.Combine(Path.GetTempPath(), "pg-loot-master-grid-debug");

    private static List<OpenCvRect> SortIntoGrid(List<OpenCvRect> cells)
    {
        if (cells.Count != GridDim * GridDim) return cells;

        int minY = cells.Min(c => c.Y);
        int maxY = cells.Max(c => c.Y);
        double rowSpan = Math.Max(1, (maxY - minY) / (double)(GridDim - 1));

        List<List<OpenCvRect>> rows = new(GridDim);
        for (int i = 0; i < GridDim; i++) rows.Add(new List<OpenCvRect>());
        foreach (OpenCvRect cell in cells)
        {
            int row = (int)Math.Round((cell.Y - minY) / rowSpan);
            row = Math.Clamp(row, 0, GridDim - 1);
            rows[row].Add(cell);
        }

        if (rows.Any(r => r.Count != GridDim))
        {
            List<OpenCvRect> byY = cells.OrderBy(c => c.Y).ToList();
            List<OpenCvRect> fallback = new(cells.Count);
            for (int r = 0; r < GridDim; r++)
            {
                List<OpenCvRect> rowCells = byY.GetRange(r * GridDim, GridDim);
                rowCells.Sort((a, b) => a.X.CompareTo(b.X));
                fallback.AddRange(rowCells);
            }
            return fallback;
        }

        List<OpenCvRect> sorted = new(cells.Count);
        for (int r = 0; r < GridDim; r++)
        {
            rows[r].Sort((a, b) => a.X.CompareTo(b.X));
            sorted.AddRange(rows[r]);
        }
        return sorted;
    }

    public IReadOnlyList<OpenCvRect> TryDetectCells(OpenCvMat bgrFrame, OpenCvRect titleBar)
    {
        if (bgrFrame.Channels() != 3) return Array.Empty<OpenCvRect>();

        int sx = Math.Max(0, titleBar.X - 30);
        int sy = Math.Max(0, titleBar.Y + titleBar.Height);
        int sw = Math.Min(940, bgrFrame.Cols - sx);
        int sh = Math.Min(1050, bgrFrame.Rows - sy);
        if (sw < 200 || sh < 200) return Array.Empty<OpenCvRect>();

        using OpenCvMat roi = new(bgrFrame, new OpenCvRect(sx, sy, sw, sh));

        using OpenCvMat mask = new();
        Cv2.InRange(
            roi,
            new Scalar(110, 130, 150),
            new Scalar(170, 200, 220),
            mask);

        Cv2.FindContours(
            mask,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        List<OpenCvRect> cells = new();
        foreach (Point[] c in contours)
        {
            OpenCvRect bbox = Cv2.BoundingRect(c);
            if (bbox.Width < CellMinSize || bbox.Width > CellMaxSize) continue;
            if (bbox.Height < CellMinSize || bbox.Height > CellMaxSize) continue;
            double ratio = (double)bbox.Width / bbox.Height;
            if (ratio < CellMinAspect || ratio > CellMaxAspect) continue;
            cells.Add(new OpenCvRect(bbox.X + sx, bbox.Y + sy, bbox.Width, bbox.Height));
        }

        if (cells.Count > GridDim * GridDim)
        {
            cells = cells.OrderByDescending(c => (long)c.Width * c.Height).Take(GridDim * GridDim).ToList();
        }

        cells = SortIntoGrid(cells);

        if (Interlocked.Increment(ref _debugCount) == 1)
        {
            try
            {
                Directory.CreateDirectory(DebugDir);
                Cv2.ImWrite(Path.Combine(DebugDir, "roi.png"), roi);
                Cv2.ImWrite(Path.Combine(DebugDir, "tile-mask.png"), mask);
                using OpenCvMat annotated = roi.Clone();
                foreach (Point[] c in contours)
                {
                    OpenCvRect b = Cv2.BoundingRect(c);
                    Cv2.Rectangle(annotated, b, new Scalar(0, 0, 255), 2);
                }
                Cv2.ImWrite(Path.Combine(DebugDir, "all-contours.png"), annotated);
            }
            catch { }
        }

        return cells;
    }
}
