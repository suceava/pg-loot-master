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

    public IReadOnlyList<OpenCvRect> TryDetectCells(OpenCvMat bgrFrame, OpenCvRect titleBar)
    {
        if (bgrFrame.Channels() != 3) return Array.Empty<OpenCvRect>();

        int sx = Math.Max(0, titleBar.X - 200);
        int sy = Math.Max(0, titleBar.Y + titleBar.Height);
        int sw = Math.Min(1800, bgrFrame.Cols - sx);
        int sh = Math.Min(1400, bgrFrame.Rows - sy);
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
