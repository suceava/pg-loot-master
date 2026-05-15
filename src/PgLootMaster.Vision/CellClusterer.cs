using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;
using OpenCvRect = OpenCvSharp.Rect;

namespace PgLootMaster.Vision;

internal static class ClustererLog
{
    private static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pg-loot-master.log");
    private static readonly string SolverPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pg-loot-master-solver.log");
    private static readonly object Sync = new();
    public static void Write(string m)
    {
        lock (Sync)
        {
            try
            {
                string line = $"{DateTime.Now:HH:mm:ss.fff} CLUSTER: {m}{Environment.NewLine}";
                File.AppendAllText(Path, line);
                File.AppendAllText(SolverPath, line);
            }
            catch { }
        }
    }
}

public sealed class CellClusterer
{
    private const int SignatureSize = 24;
    private const int SignatureLen = SignatureSize * SignatureSize * 3;
    private const double SimilarityThreshold = 30.0;
    private const double InterClusterMergeThreshold = 0.0;
    private const double CenterCropFraction = 0.7;
    private const double StableAvgInterframeThreshold = 5.0;
    private const double StableMaxInterframeThreshold = 15.0;
    private const double LargeChangeThreshold = 25.0;
    private const double CellChangedThreshold = 30.0;
    private const int MinChangedCellsForLargeChange = 3;
    private const int StableFramesBeforeCapture = 3;
    private const double StickyBuffer = 30.0;

    private byte[][]? _previousSignatures;
    private byte[][]? _canonicalSignatures;
    private List<byte[]>? _canonicalClusterReps;
    private int[]? _previousStableIds;
    private int _consecutiveStableFrames;
    private int _framesSinceLargeChangeEnded;
    private bool _needsRecapture = true;
    private const int FramesBeforeForceRecapture = 5;
    private readonly Queue<byte[][]> _stableFrameBuffer = new();
    private const int StableFrameBufferDepth = 4;

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
            int changedCells = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                double d = AverageAbsDifference(signatures[i], _previousSignatures[i]);
                sum += d;
                if (d > maxDiff) maxDiff = d;
                if (d > CellChangedThreshold) changedCells++;
            }
            double avgInterframe = sum / cells.Count;
            isStableFrame = avgInterframe < StableAvgInterframeThreshold
                            && maxDiff < StableMaxInterframeThreshold;
            isLargeChange = avgInterframe > LargeChangeThreshold
                            || changedCells >= MinChangedCellsForLargeChange;
        }

        // Maintain a rolling buffer of recent stable-frame signatures so canonical capture
        // can average over multiple frames — averaging out pulsating-tile drift so the
        // pulsating cell ends up in the same cluster as its non-pulsing siblings.
        if (isStableFrame)
        {
            _stableFrameBuffer.Enqueue(CloneSignatures(signatures));
            while (_stableFrameBuffer.Count > StableFrameBufferDepth) _stableFrameBuffer.Dequeue();
        }
        else if (isLargeChange)
        {
            _stableFrameBuffer.Clear();
        }

        if (_canonicalClusterReps is null)
        {
            CaptureCanonical(AveragedStableSignatures(signatures));
            _needsRecapture = false;
            _consecutiveStableFrames = 0;
            _framesSinceLargeChangeEnded = 0;
        }
        else if (isLargeChange)
        {
            if (!_needsRecapture) ClustererLog.Write("Large change detected -> needsRecapture=true");
            _consecutiveStableFrames = 0;
            _framesSinceLargeChangeEnded = 0;
            _needsRecapture = true;
        }
        else if (_needsRecapture)
        {
            _framesSinceLargeChangeEnded++;
            if (isStableFrame) _consecutiveStableFrames++;
            else _consecutiveStableFrames = 0;

            bool stabilityReached = _consecutiveStableFrames >= StableFramesBeforeCapture;
            bool timeoutReached = _framesSinceLargeChangeEnded >= FramesBeforeForceRecapture;
            if (stabilityReached || timeoutReached)
            {
                ClustererLog.Write($"Recapturing canonical (stable={stabilityReached}, timeout={timeoutReached}, buffer={_stableFrameBuffer.Count})");
                CaptureCanonical(AveragedStableSignatures(signatures));
                _needsRecapture = false;
                _framesSinceLargeChangeEnded = 0;
                _consecutiveStableFrames = 0;
            }
        }
        else
        {
            _consecutiveStableFrames = 0;
        }

        int[] stableIds = new int[cells.Count];
        if (_canonicalClusterReps is not null && _canonicalClusterReps.Count > 0)
        {
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

    private byte[][] AveragedStableSignatures(byte[][] currentFrame)
    {
        if (_stableFrameBuffer.Count == 0) return currentFrame;
        int cellCount = currentFrame.Length;
        int sigLen = currentFrame[0].Length;
        byte[][] avg = new byte[cellCount][];
        for (int i = 0; i < cellCount; i++) avg[i] = new byte[sigLen];

        int frameCount = 0;
        foreach (byte[][] frame in _stableFrameBuffer)
        {
            if (frame.Length != cellCount) continue;
            frameCount++;
            for (int i = 0; i < cellCount; i++)
            {
                if (frame[i].Length != sigLen) continue;
                for (int b = 0; b < sigLen; b++)
                {
                    avg[i][b] += (byte)(frame[i][b] / Math.Max(1, _stableFrameBuffer.Count));
                }
            }
        }
        if (frameCount == 0) return currentFrame;
        // Recompute as proper average to avoid rounding.
        for (int i = 0; i < cellCount; i++)
        {
            int[] sums = new int[sigLen];
            int n = 0;
            foreach (byte[][] frame in _stableFrameBuffer)
            {
                if (frame.Length != cellCount || frame[i].Length != sigLen) continue;
                n++;
                for (int b = 0; b < sigLen; b++) sums[b] += frame[i][b];
            }
            if (n == 0) { Array.Copy(currentFrame[i], avg[i], sigLen); continue; }
            for (int b = 0; b < sigLen; b++) avg[i][b] = (byte)(sums[b] / n);
        }
        return avg;
    }

    private static byte[][] CloneSignatures(byte[][] sigs)
    {
        byte[][] clone = new byte[sigs.Length][];
        for (int i = 0; i < sigs.Length; i++)
        {
            clone[i] = (byte[])sigs[i].Clone();
        }
        return clone;
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
        ClustererLog.Write($"Canonical CAPTURED: {freshReps.Count} clusters from {signatures.Length} cells");
    }

    private static int[] GreedyCluster(byte[][] signatures, out List<byte[]> reps)
    {
        int[] ids = new int[signatures.Length];
        List<List<byte[]>> clusterMembers = new();
        for (int i = 0; i < signatures.Length; i++)
        {
            int assigned = -1;
            for (int c = 0; c < clusterMembers.Count; c++)
            {
                double d = AverageAbsDifference(signatures[i], clusterMembers[c][0]);
                if (d < SimilarityThreshold)
                {
                    assigned = c;
                    break;
                }
            }
            if (assigned < 0)
            {
                assigned = clusterMembers.Count;
                clusterMembers.Add(new List<byte[]>());
            }
            clusterMembers[assigned].Add(signatures[i]);
            ids[i] = assigned;
        }

        int sigLen = signatures.Length > 0 ? signatures[0].Length : 0;
        List<byte[]> averages = new(clusterMembers.Count);
        foreach (List<byte[]> members in clusterMembers)
        {
            averages.Add(ComputeAverage(members, sigLen));
        }

        // Post-process: merge small clusters (≤2 members) into their nearest larger cluster
        // when the centroid distance is below SmallClusterMergeThreshold. This catches
        // pulsating-tile outliers — a hint cell whose pulse phase pulled it just past the
        // similarity threshold during canonical capture.
        bool mergedSmall = true;
        const double SmallClusterMergeThreshold = 35.0;
        const int SmallClusterMaxMembers = 1;
        while (mergedSmall && clusterMembers.Count > 1)
        {
            mergedSmall = false;
            for (int a = 0; a < clusterMembers.Count; a++)
            {
                if (clusterMembers[a].Count > SmallClusterMaxMembers) continue;
                int nearestB = -1;
                double nearestDist = SmallClusterMergeThreshold;
                for (int b = 0; b < clusterMembers.Count; b++)
                {
                    if (b == a) continue;
                    if (clusterMembers[b].Count <= SmallClusterMaxMembers) continue;
                    double d = AverageAbsDifference(averages[a], averages[b]);
                    if (d < nearestDist) { nearestDist = d; nearestB = b; }
                }
                if (nearestB >= 0)
                {
                    int from = a;
                    int to = nearestB;
                    clusterMembers[to].AddRange(clusterMembers[from]);
                    averages[to] = ComputeAverage(clusterMembers[to], sigLen);
                    clusterMembers.RemoveAt(from);
                    averages.RemoveAt(from);
                    // After removing 'from', indices > from shift down by 1.
                    int actualTo = to > from ? to - 1 : to;
                    for (int i = 0; i < ids.Length; i++)
                    {
                        if (ids[i] == from) ids[i] = actualTo;
                        else if (ids[i] > from) ids[i]--;
                    }
                    mergedSmall = true;
                    break;
                }
            }
        }

        bool merged = true;
        while (merged && clusterMembers.Count > 1)
        {
            merged = false;
            int bestA = -1, bestB = -1;
            double bestDist = InterClusterMergeThreshold;
            for (int a = 0; a < clusterMembers.Count; a++)
            {
                for (int b = a + 1; b < clusterMembers.Count; b++)
                {
                    double d = AverageAbsDifference(averages[a], averages[b]);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestA = a;
                        bestB = b;
                    }
                }
            }
            if (bestA >= 0)
            {
                clusterMembers[bestA].AddRange(clusterMembers[bestB]);
                clusterMembers.RemoveAt(bestB);
                averages[bestA] = ComputeAverage(clusterMembers[bestA], sigLen);
                averages.RemoveAt(bestB);
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i] == bestB) ids[i] = bestA;
                    else if (ids[i] > bestB) ids[i]--;
                }
                merged = true;
            }
        }

        reps = averages;
        return ids;
    }

    private static byte[] ComputeAverage(List<byte[]> members, int sigLen)
    {
        byte[] avg = new byte[sigLen];
        for (int b = 0; b < sigLen; b++)
        {
            int sum = 0;
            foreach (byte[] sig in members) sum += sig[b];
            avg[b] = (byte)(sum / members.Count);
        }
        return avg;
    }

    private static byte[] ComputeSignature(OpenCvMat bgrFrame, OpenCvRect cell)
    {
        int marginX = (int)(cell.Width * (1.0 - CenterCropFraction) / 2.0);
        int marginY = (int)(cell.Height * (1.0 - CenterCropFraction) / 2.0);
        OpenCvRect inner = new(cell.X + marginX, cell.Y + marginY,
            cell.Width - 2 * marginX, cell.Height - 2 * marginY);
        OpenCvRect safe = ClampRect(inner, bgrFrame.Cols, bgrFrame.Rows);
        if (safe.Width <= 0 || safe.Height <= 0)
            return new byte[SignatureLen];

        using OpenCvMat crop = new(bgrFrame, safe);
        using OpenCvMat resized = new();
        Cv2.Resize(crop, resized, new Size(SignatureSize, SignatureSize), 0, 0, InterpolationFlags.Area);

        byte[] data = new byte[SignatureLen];
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
