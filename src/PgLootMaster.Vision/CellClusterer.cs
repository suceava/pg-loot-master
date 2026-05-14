using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;
using OpenCvRect = OpenCvSharp.Rect;

namespace PgLootMaster.Vision;

public sealed class CellClusterer
{
    private const int SignatureSize = 12;
    private const double SimilarityThreshold = 30.0;
    private const double CenterCropFraction = 0.85;
    private const double StableAvgInterframeThreshold = 5.0;
    private const double StableMaxInterframeThreshold = 15.0;
    private const double LargeChangeThreshold = 25.0;
    private const int StableFramesBeforeCapture = 3;
    private const double StickyBuffer = 12.0;

    private byte[][]? _previousSignatures;
    private byte[][]? _canonicalSignatures;
    private List<byte[]>? _canonicalClusterReps;
    private int[]? _previousStableIds;
    private int _consecutiveStableFrames;
    private bool _needsRecapture = true;

    public int[] ClusterCells(OpenCvMat bgrFrame, IReadOnlyList<OpenCvRect> cells)
    {
        if (cells.Count == 0) return Array.Empty<int>();

        byte[][] signatures = new byte[cells.Count][];
        for (int i = 0; i < cells.Count; i++)
        {
            signatures[i] = ComputeSignature(bgrFrame, cells[i]);
        }

        bool isStableFrame = false;
        bool isLargeChange = false;
        if (_previousSignatures is not null && _previousSignatures.Length == cells.Count)
        {
            double sum = 0;
            double maxDiff = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                double d = AverageAbsDifference(signatures[i], _previousSignatures[i]);
                sum += d;
                if (d > maxDiff) maxDiff = d;
            }
            double avgInterframe = sum / cells.Count;
            isStableFrame = avgInterframe < StableAvgInterframeThreshold
                            && maxDiff < StableMaxInterframeThreshold;
            isLargeChange = avgInterframe > LargeChangeThreshold;
        }

        if (isLargeChange)
        {
            _consecutiveStableFrames = 0;
            _needsRecapture = true;
        }
        else if (isStableFrame)
        {
            _consecutiveStableFrames++;
            if (_consecutiveStableFrames >= StableFramesBeforeCapture && _needsRecapture)
            {
                CaptureCanonical(signatures);
                _needsRecapture = false;
            }
        }
        else
        {
            _consecutiveStableFrames = 0;
        }

        int[] stableIds;
        if (_canonicalClusterReps is not null && _canonicalClusterReps.Count > 0)
        {
            stableIds = new int[cells.Count];
            for (int i = 0; i < cells.Count; i++)
            {
                int bestC = 0;
                double bestDist = double.MaxValue;
                for (int c = 0; c < _canonicalClusterReps.Count; c++)
                {
                    double d = AverageAbsDifference(signatures[i], _canonicalClusterReps[c]);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestC = c;
                    }
                }

                if (_previousStableIds is not null && i < _previousStableIds.Length)
                {
                    int prevId = _previousStableIds[i];
                    if (prevId != bestC && prevId >= 0 && prevId < _canonicalClusterReps.Count)
                    {
                        double prevDist = AverageAbsDifference(signatures[i], _canonicalClusterReps[prevId]);
                        if (prevDist - bestDist < StickyBuffer)
                        {
                            bestC = prevId;
                        }
                    }
                }

                stableIds[i] = bestC;
            }
        }
        else
        {
            stableIds = GreedyCluster(signatures, out _);
        }

        _previousSignatures = signatures;
        _previousStableIds = (int[])stableIds.Clone();
        return stableIds;
    }

    private void CaptureCanonical(byte[][] signatures)
    {
        _canonicalSignatures = new byte[signatures.Length][];
        for (int i = 0; i < signatures.Length; i++)
        {
            _canonicalSignatures[i] = (byte[])signatures[i].Clone();
        }

        int[] freshIds = GreedyCluster(signatures, out List<byte[]> freshReps);
        _canonicalClusterReps = freshReps;
    }

    private static int[] GreedyCluster(byte[][] signatures, out List<byte[]> reps)
    {
        int[] ids = new int[signatures.Length];
        reps = new List<byte[]>();
        for (int i = 0; i < signatures.Length; i++)
        {
            int assigned = -1;
            for (int c = 0; c < reps.Count; c++)
            {
                double d = AverageAbsDifference(signatures[i], reps[c]);
                if (d < SimilarityThreshold)
                {
                    assigned = c;
                    break;
                }
            }
            if (assigned < 0)
            {
                assigned = reps.Count;
                reps.Add((byte[])signatures[i].Clone());
            }
            ids[i] = assigned;
        }
        return ids;
    }

    private static byte[] ComputeSignature(OpenCvMat bgrFrame, OpenCvRect cell)
    {
        int marginX = (int)(cell.Width * (1.0 - CenterCropFraction) / 2.0);
        int marginY = (int)(cell.Height * (1.0 - CenterCropFraction) / 2.0);
        OpenCvRect inner = new(cell.X + marginX, cell.Y + marginY,
            cell.Width - 2 * marginX, cell.Height - 2 * marginY);
        OpenCvRect safe = ClampRect(inner, bgrFrame.Cols, bgrFrame.Rows);
        if (safe.Width <= 0 || safe.Height <= 0)
            return new byte[SignatureSize * SignatureSize * 3];

        using OpenCvMat crop = new(bgrFrame, safe);
        using OpenCvMat resized = new();
        Cv2.Resize(crop, resized, new Size(SignatureSize, SignatureSize), 0, 0, InterpolationFlags.Area);

        byte[] data = new byte[SignatureSize * SignatureSize * 3];
        for (int y = 0; y < SignatureSize; y++)
        {
            for (int x = 0; x < SignatureSize; x++)
            {
                Vec3b p = resized.At<Vec3b>(y, x);
                int idx = (y * SignatureSize + x) * 3;
                data[idx] = p.Item0;
                data[idx + 1] = p.Item1;
                data[idx + 2] = p.Item2;
            }
        }
        return data;
    }

    private static OpenCvRect ClampRect(OpenCvRect r, int width, int height)
    {
        int x = Math.Max(0, r.X);
        int y = Math.Max(0, r.Y);
        int w = Math.Min(r.Width, width - x);
        int h = Math.Min(r.Height, height - y);
        return new OpenCvRect(x, y, w, h);
    }

    private static double AverageAbsDifference(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }
        return (double)sum / a.Length;
    }
}
