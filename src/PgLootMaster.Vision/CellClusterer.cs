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

/// <summary>
/// Per-cell appearance signature. Two pulse-invariant components:
///  - <see cref="Structure"/>: the LAB L* (luminance) channel of the icon crop, zero-meaned
///    and L2-normalized. Comparing two of these by dot product yields normalized
///    cross-correlation (NCC) — invariant to brightness scale/offset, so PG's pulse and
///    flashing animations contribute nothing. NCC measures the spatial PATTERN: distinct
///    icon silhouettes (Oil vs Potato) correlate poorly.
///  - <see cref="Color"/>: a 4x4 spatial grid of mean LAB a*/b* chrominance. Chrominance
///    separates same-shape / different-color items (yellow Oil vs red Oil) that structure
///    NCC alone would merge.
/// </summary>
internal sealed class CellSignature
{
    public float[] Structure { get; }
    public float[] Color { get; }

    public CellSignature(float[] structure, float[] color)
    {
        Structure = structure;
        Color = color;
    }
}

/// <summary>
/// Groups the 49 board cells by item identity.
///
/// Two design pillars, both aimed at "no flicker, ever":
///  1. The canonical cluster set is captured ONCE per game from an averaged still board.
///     A match-3 game has a fixed item roster, so it stays valid for the whole game.
///  2. Cluster IDs are FROZEN between cascades. They are recomputed only once per cascade —
///     after the board has been structurally still for several frames — from the average
///     of those still frames. Flashing/pulse is a brightness effect; structure NCC is
///     brightness-invariant, so flashing never registers as motion and never triggers a
///     recompute. Cluster IDs therefore physically cannot change while the board sits
///     still, no matter how the tiles flash.
///
/// A new game or the "Recompute clusters" button calls <see cref="Reset"/>.
/// </summary>
public sealed class CellClusterer
{
    // Icon crop is resized to SignatureSize x SignatureSize before feature extraction.
    private const int SignatureSize = 32;
    private const int StructDim = SignatureSize * SignatureSize;   // 1024
    private const int ColorGridDim = 4;                            // 4x4 spatial color grid
    private const int ColorRegion = SignatureSize / ColorGridDim;  // 8x8 px per region
    private const int ColorDim = ColorGridDim * ColorGridDim * 2;  // 32  (a*, b* per region)

    // Crop to the central 58% of the cell rect — drops the wood-colored border (similar
    // across every cell regardless of item) which would otherwise inflate correlation
    // between distinct items and add positional noise.
    private const double CenterCropFraction = 0.58;

    // Structure NCC distance (1 - correlation) is in [0, 2]; scale it up so it shares a
    // comparable numeric range with the color mean-abs-diff before the two are summed.
    private const double StructWeight = 100.0;

    // Agglomerative merge floor. Two clusters are merged only while the closest remaining
    // pair is below this. Same-item cells land ~3-13 apart; distinct items ~20+ (even the
    // hardest case — two same-shape oils — is ~35+ thanks to the color term). 16 sits in
    // the gap, so distinct items are NEVER merged.
    private const double SimilarityThreshold = 16.0;

    // Canonical capture: collect WarmupFrames still frames, then cluster their per-cell
    // average. MaxWarmupFrames is a hard cap so a never-fully-still board still captures.
    private const int WarmupFrames = 6;
    private const int MaxWarmupFrames = 30;
    private const int BufferDepth = 6;

    // Per-cell structure-only interframe distance above this = that cell's tile is in
    // motion. StructMotionMinCells of them = the board is mid-cascade. Structure NCC is
    // brightness-invariant, so flashing/pulse never trips these — only real tile motion.
    private const double StructMotionCellThreshold = 25.0;
    private const int StructMotionMinCells = 5;
    private const double StructStillAvgThreshold = 8.0;

    // The board must be structurally still for this many consecutive frames before its
    // cluster IDs are (re)committed — long enough to be sure the cascade has fully ended.
    private const int SettleConfirmFrames = 4;

    private List<CellSignature>? _canonicalClusterReps;
    private CellSignature[]? _previousSignatures;
    private int[]? _settledIds;
    private readonly Queue<CellSignature[]> _stillBuffer = new();
    private int _stillCount;
    private int _framesSinceReset;

    /// <summary>
    /// True once the board has been still long enough for its cluster IDs to be committed.
    /// Both this and <see cref="LastFrameClusterIdsStable"/> drive the OverlayWindow
    /// display gate; they are equal by construction (IDs are only ever committed on a
    /// confirmed-still frame).
    /// </summary>
    public bool LastFrameWasStable { get; private set; }

    /// <inheritdoc cref="LastFrameWasStable"/>
    public bool LastFrameClusterIdsStable { get; private set; }

    /// <summary>True until the canonical has been captured for the current game.</summary>
    public bool NeedsRecapture => _canonicalClusterReps is null;

    /// <summary>
    /// Sidebar item count. Advisory only — the threshold-based agglomerative clustering
    /// finds the natural cluster count on its own, so this is currently unused. Kept on
    /// the API because OverlayWindow still sets it.
    /// </summary>
    public int? TargetMinClusterCount { get; set; }

    /// <summary>
    /// Drop the canonical and re-capture after a fresh warmup. Called by the Settings
    /// "Recompute clusters" button and by OverlayWindow when a new game starts.
    /// </summary>
    public void Reset()
    {
        _canonicalClusterReps = null;
        _previousSignatures = null;
        _settledIds = null;
        _stillBuffer.Clear();
        _stillCount = 0;
        _framesSinceReset = 0;
        LastFrameWasStable = false;
        LastFrameClusterIdsStable = false;
        ClustererLog.Write("Reset() — canonical dropped, will recapture after warmup");
    }

    public int[] ClusterCells(OpenCvMat bgrFrame, IReadOnlyList<OpenCvRect> cells)
    {
        if (cells.Count == 0) return Array.Empty<int>();
        int n = cells.Count;

        CellSignature[] signatures = new CellSignature[n];
        for (int i = 0; i < n; i++)
        {
            signatures[i] = ComputeSignature(bgrFrame, cells[i]);
        }

        // --- Structure-only interframe motion ---
        // NCC structure is invariant to brightness, so PG's flashing/pulse moves this
        // ≈ 0. Only genuine tile motion (a cascade) registers. This is what separates
        // "the board is just flashing" from "tiles actually changed".
        bool isStill = false;
        if (_previousSignatures is not null && _previousSignatures.Length == n)
        {
            double sum = 0;
            int moving = 0;
            for (int i = 0; i < n; i++)
            {
                double d = StructureDistance(signatures[i], _previousSignatures[i]);
                sum += d;
                if (d > StructMotionCellThreshold) moving++;
            }
            isStill = (sum / n) < StructStillAvgThreshold && moving < StructMotionMinCells;
        }
        _previousSignatures = signatures;
        _framesSinceReset++;

        // --- Still-frame buffer ---
        // Holds only consecutive still frames; any motion clears it. So at any point it
        // contains exactly the last min(_stillCount, BufferDepth) still frames, all of
        // the SAME settled scene — safe to average.
        if (isStill)
        {
            _stillCount++;
            _stillBuffer.Enqueue(signatures);
            while (_stillBuffer.Count > BufferDepth) _stillBuffer.Dequeue();
        }
        else
        {
            _stillCount = 0;
            _stillBuffer.Clear();
        }

        // --- Canonical capture (once per game) ---
        if (_canonicalClusterReps is null
            && (_stillCount >= WarmupFrames
                || (_framesSinceReset >= MaxWarmupFrames && _stillBuffer.Count > 0)))
        {
            _settledIds = CaptureCanonical(AverageBuffer());
        }

        // --- Per-cascade re-commit ---
        // Cluster IDs are frozen between cascades. They are recomputed exactly once per
        // cascade: when the board hits SettleConfirmFrames consecutive still frames, the
        // cascade is provably over. Re-cluster from the average of those still frames
        // (averaging cancels any flash on the commit frame). Firing on '== ' makes this
        // happen once per settle, not every subsequent still frame.
        if (_canonicalClusterReps is not null && _settledIds is not null
            && _stillCount == SettleConfirmFrames)
        {
            _settledIds = AssignToCanonical(AverageBuffer());
        }

        bool settled = _settledIds is not null && _settledIds.Length == n
                       && _stillCount >= SettleConfirmFrames;
        LastFrameWasStable = settled;
        LastFrameClusterIdsStable = settled;

        return _settledIds is not null && _settledIds.Length == n
            ? (int[])_settledIds.Clone()
            : new int[n];
    }

    /// <summary>Assign each cell to its nearest canonical cluster representative.</summary>
    private int[] AssignToCanonical(CellSignature[] signatures)
    {
        List<CellSignature> reps = _canonicalClusterReps!;
        int[] ids = new int[signatures.Length];
        for (int i = 0; i < signatures.Length; i++)
        {
            int bestC = 0;
            double bestDist = double.MaxValue;
            for (int c = 0; c < reps.Count; c++)
            {
                double d = Distance(signatures[i], reps[c]);
                if (d < bestDist) { bestDist = d; bestC = c; }
            }
            ids[i] = bestC;
        }
        return ids;
    }

    private int[] CaptureCanonical(CellSignature[] signatures)
    {
        int[] ids = Cluster(signatures, out List<CellSignature> reps);
        _canonicalClusterReps = reps;
        ClustererLog.Write($"Canonical CAPTURED: {reps.Count} clusters from {signatures.Length} cells " +
                           $"(avg of {_stillBuffer.Count} still frames)");
        return ids;
    }

    /// <summary>
    /// Agglomerative (centroid-linkage) clustering. Each cell starts as its own cluster;
    /// the two closest clusters are merged repeatedly, and merging STOPS once the closest
    /// remaining pair exceeds <see cref="SimilarityThreshold"/>. There is no forced
    /// cluster count, so two genuinely distinct items can never be merged together.
    /// </summary>
    private static int[] Cluster(CellSignature[] sigs, out List<CellSignature> reps)
    {
        int n = sigs.Length;
        int[] ids = new int[n];
        if (n == 0) { reps = new List<CellSignature>(); return ids; }

        List<List<int>> clusters = new(n);
        List<CellSignature> centroids = new(n);
        for (int i = 0; i < n; i++)
        {
            clusters.Add(new List<int> { i });
            centroids.Add(sigs[i]);
        }

        while (clusters.Count > 1)
        {
            int bestA = -1, bestB = -1;
            double bestDist = double.MaxValue;
            for (int a = 0; a < clusters.Count; a++)
            {
                for (int b = a + 1; b < clusters.Count; b++)
                {
                    double d = Distance(centroids[a], centroids[b]);
                    if (d < bestDist) { bestDist = d; bestA = a; bestB = b; }
                }
            }
            if (bestA < 0 || bestDist > SimilarityThreshold) break;

            clusters[bestA].AddRange(clusters[bestB]);
            clusters.RemoveAt(bestB);
            centroids.RemoveAt(bestB);
            List<CellSignature> members = new(clusters[bestA].Count);
            foreach (int idx in clusters[bestA]) members.Add(sigs[idx]);
            centroids[bestA] = Average(members);
        }

        for (int c = 0; c < clusters.Count; c++)
        {
            foreach (int idx in clusters[c]) ids[idx] = c;
        }
        reps = centroids;
        return ids;
    }

    /// <summary>Per-cell average of every frame currently in the still buffer.</summary>
    private CellSignature[] AverageBuffer()
    {
        CellSignature[][] frames = _stillBuffer.ToArray();
        if (frames.Length == 0) return Array.Empty<CellSignature>();
        int n = frames[^1].Length;
        CellSignature[] result = new CellSignature[n];
        List<CellSignature> perCell = new(frames.Length);
        for (int i = 0; i < n; i++)
        {
            perCell.Clear();
            foreach (CellSignature[] f in frames)
            {
                if (f.Length == n) perCell.Add(f[i]);
            }
            result[i] = perCell.Count > 0 ? Average(perCell) : frames[^1][i];
        }
        return result;
    }

    private static CellSignature Average(IReadOnlyList<CellSignature> members)
    {
        if (members.Count == 1) return members[0];
        float[] structure = new float[StructDim];
        float[] color = new float[ColorDim];
        foreach (CellSignature s in members)
        {
            for (int i = 0; i < StructDim; i++) structure[i] += s.Structure[i];
            for (int i = 0; i < ColorDim; i++) color[i] += s.Color[i];
        }
        int n = members.Count;
        for (int i = 0; i < ColorDim; i++) color[i] /= n;
        // Structure: the L2 renormalize is scale-invariant, so summing (vs averaging)
        // the components first makes no difference — renormalize handles it.
        Renormalize(structure);
        return new CellSignature(structure, color);
    }

    /// <summary>
    /// Full distance: structure NCC distance + color mean-abs-diff. Both components are
    /// pulse/flash-invariant. Same item ~3-13, distinct items ~20+.
    /// </summary>
    private static double Distance(CellSignature a, CellSignature b)
    {
        double colorSum = 0;
        float[] ca = a.Color, cb = b.Color;
        for (int i = 0; i < ca.Length; i++) colorSum += Math.Abs(ca[i] - cb[i]);
        return StructureDistance(a, b) + colorSum / ca.Length;
    }

    /// <summary>
    /// Structure-only distance: NCC distance on the luminance channel. Both structure
    /// vectors are zero-mean + unit-L2, so NCC == dot product. Invariant to brightness,
    /// hence to PG's flashing/pulse — used for motion detection.
    /// </summary>
    private static double StructureDistance(CellSignature a, CellSignature b)
    {
        double dot = 0;
        float[] sa = a.Structure, sb = b.Structure;
        for (int i = 0; i < sa.Length; i++) dot += sa[i] * sb[i];
        return (1.0 - dot) * StructWeight;
    }

    private static CellSignature ComputeSignature(OpenCvMat bgrFrame, OpenCvRect cell)
    {
        int marginX = (int)(cell.Width * (1.0 - CenterCropFraction) / 2.0);
        int marginY = (int)(cell.Height * (1.0 - CenterCropFraction) / 2.0);
        OpenCvRect inner = new(cell.X + marginX, cell.Y + marginY,
            cell.Width - 2 * marginX, cell.Height - 2 * marginY);
        OpenCvRect safe = ClampRect(inner, bgrFrame.Cols, bgrFrame.Rows);
        if (safe.Width <= 0 || safe.Height <= 0)
            return new CellSignature(new float[StructDim], new float[ColorDim]);

        using OpenCvMat crop = new(bgrFrame, safe);
        using OpenCvMat resized = new();
        Cv2.Resize(crop, resized, new Size(SignatureSize, SignatureSize), 0, 0, InterpolationFlags.Area);
        // Light blur → makes NCC tolerant of a pixel or two of cell-rect drift.
        using OpenCvMat blurred = new();
        Cv2.GaussianBlur(resized, blurred, new Size(3, 3), 0);
        using OpenCvMat lab = new();
        Cv2.CvtColor(blurred, lab, ColorConversionCodes.BGR2Lab);

        float[] structure = new float[StructDim];
        float[] color = new float[ColorDim];
        for (int y = 0; y < SignatureSize; y++)
        {
            for (int x = 0; x < SignatureSize; x++)
            {
                Vec3b p = lab.At<Vec3b>(y, x);
                structure[y * SignatureSize + x] = p.Item0;   // L*  (luminance → structure)
                int gx = x / ColorRegion;
                int gy = y / ColorRegion;
                int idx = (gy * ColorGridDim + gx) * 2;
                color[idx] += p.Item1;       // a*
                color[idx + 1] += p.Item2;   // b*
            }
        }
        int perRegion = ColorRegion * ColorRegion;
        for (int i = 0; i < ColorDim; i++) color[i] /= perRegion;

        Renormalize(structure);
        return new CellSignature(structure, color);
    }

    /// <summary>Zero-mean then L2-normalize in place, so dot product == NCC.</summary>
    private static void Renormalize(float[] v)
    {
        double mean = 0;
        for (int i = 0; i < v.Length; i++) mean += v[i];
        mean /= v.Length;
        double norm = 0;
        for (int i = 0; i < v.Length; i++)
        {
            v[i] -= (float)mean;
            norm += v[i] * (double)v[i];
        }
        norm = Math.Sqrt(norm);
        if (norm > 1e-6)
        {
            float inv = (float)(1.0 / norm);
            for (int i = 0; i < v.Length; i++) v[i] *= inv;
        }
    }

    private static OpenCvRect ClampRect(OpenCvRect r, int width, int height)
    {
        int x = Math.Max(0, r.X);
        int y = Math.Max(0, r.Y);
        int w = Math.Min(r.Width, width - x);
        int h = Math.Min(r.Height, height - y);
        return new OpenCvRect(x, y, w, h);
    }
}
