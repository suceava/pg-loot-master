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
    // Need at least this many of the 49 cells detected to reliably resolve the grid.
    private const int MinDetectedCells = GridDim * GridDim - 10;

    private static int _debugCount;
    private static readonly string DebugDir = Path.Combine(Path.GetTempPath(), "pg-loot-master-grid-debug");

    /// <summary>
    /// Build a clean 7x7 grid of cell rects from the detected cell contours. The board is
    /// a perfectly regular grid, so rather than SORT the contours into rows (which
    /// interleaves rows when contour bounding boxes jitter a few pixels, mis-indexing
    /// cells), this finds the 7 column-X and 7 row-Y centres from the natural gaps in the
    /// detected centres and GENERATES the 49 rects. A generated grid cannot interleave or
    /// put a cell at the wrong index, and it tolerates a handful of undetected cells.
    /// </summary>
    private static List<OpenCvRect> BuildGrid(List<OpenCvRect> cells)
    {
        double[] colX = AxisCentres(cells.Select(c => c.X + c.Width / 2.0));
        double[] rowY = AxisCentres(cells.Select(c => c.Y + c.Height / 2.0));

        int[] ws = cells.Select(c => c.Width).OrderBy(v => v).ToArray();
        int[] hs = cells.Select(c => c.Height).OrderBy(v => v).ToArray();
        int medW = ws[ws.Length / 2];
        int medH = hs[hs.Length / 2];

        List<OpenCvRect> grid = new(GridDim * GridDim);
        for (int r = 0; r < GridDim; r++)
        {
            for (int c = 0; c < GridDim; c++)
            {
                grid.Add(new OpenCvRect(
                    (int)Math.Round(colX[c] - medW / 2.0),
                    (int)Math.Round(rowY[r] - medH / 2.0),
                    medW, medH));
            }
        }
        return grid;
    }

    /// <summary>
    /// Resolve <see cref="GridDim"/> evenly-spaced axis centres from a set of cell-centre
    /// coordinates: sort them, split into GridDim groups at the GridDim-1 largest gaps
    /// (the inter-row / inter-column gaps), and average each group.
    /// </summary>
    private static double[] AxisCentres(IEnumerable<double> coords)
    {
        double[] sorted = coords.OrderBy(v => v).ToArray();
        double[] centres = new double[GridDim];
        if (sorted.Length <= GridDim)
        {
            for (int i = 0; i < GridDim; i++)
                centres[i] = sorted.Length > 0 ? sorted[Math.Min(i, sorted.Length - 1)] : 0;
            return centres;
        }

        // The GridDim-1 largest gaps separate the GridDim rows/columns.
        List<(double gap, int idx)> gaps = new(sorted.Length - 1);
        for (int i = 1; i < sorted.Length; i++) gaps.Add((sorted[i] - sorted[i - 1], i));
        List<int> splits = gaps.OrderByDescending(g => g.gap)
                                .Take(GridDim - 1)
                                .Select(g => g.idx)
                                .OrderBy(i => i)
                                .ToList();

        int start = 0;
        for (int g = 0; g < GridDim; g++)
        {
            int end = g < splits.Count ? splits[g] : sorted.Length;
            double sum = 0;
            int count = 0;
            for (int i = start; i < end; i++) { sum += sorted[i]; count++; }
            centres[g] = count > 0 ? sum / count : sorted[Math.Min(start, sorted.Length - 1)];
            start = end;
        }
        return centres;
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

        // Too few cells detected — the grid geometry can't be trusted; report no board.
        if (cells.Count < MinDetectedCells) return Array.Empty<OpenCvRect>();

        List<OpenCvRect> grid = BuildGrid(cells);

        if (Interlocked.Increment(ref _debugCount) == 1)
        {
            try
            {
                Directory.CreateDirectory(DebugDir);
                Cv2.ImWrite(Path.Combine(DebugDir, "roi.png"), roi);
                Cv2.ImWrite(Path.Combine(DebugDir, "tile-mask.png"), mask);
                using OpenCvMat annotated = roi.Clone();
                foreach (OpenCvRect d in cells)
                    Cv2.Rectangle(annotated, new OpenCvRect(d.X - sx, d.Y - sy, d.Width, d.Height),
                        new Scalar(0, 0, 255), 1);
                foreach (OpenCvRect g in grid)
                    Cv2.Rectangle(annotated, new OpenCvRect(g.X - sx, g.Y - sy, g.Width, g.Height),
                        new Scalar(0, 255, 0), 2);
                Cv2.ImWrite(Path.Combine(DebugDir, "all-contours.png"), annotated);
            }
            catch { }
        }

        return grid;
    }
}
