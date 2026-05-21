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
///    cross-correlation (NCC) — invariant to brightness, so PG's pulse/flash animations
///    contribute nothing. NCC measures the spatial PATTERN.
///  - <see cref="Color"/>: a 4x4 spatial grid of mean LAB a*/b* chrominance.
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
/// Clustering is purely metric-driven: two groups merge only if they are genuinely close
/// (below <see cref="ClusterThreshold"/>). There is NO forced cluster count — the number
/// of clusters falls out of the data, so two visually-different items can never be fused
/// just to hit a target N. (The Loot Master board starts with 4 tile types and gains one
/// per captured item up to 7; the count is dynamic and not reliably known up front, which
/// is exactly why it is not used.)
///
/// The canonical cluster set is captured once from an averaged still board. When cells
/// appear that match no existing rep — a freshly-introduced tile type — a new rep is
/// APPENDED for them and every cell re-assigned the same commit, so the new tiles land in
/// the right cluster immediately. The canonical only ever grows; existing reps are never
/// lost. Cluster IDs are FROZEN between cascades and recomputed once per cascade.
///
/// Every commit logs the full 7x7 grid of clusterID:matchDistance for diagnostics.
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
    // comparable numeric range with the color distance before the two are summed.
    private const double StructWeight = 100.0;

    // Two clusters merge only while their distance is below this. With the top-3 region
    // color metric (see Distance), same-item pairs land well under it and visually
    // distinct items well over it — so distinct items are never merged.
    private const double ClusterThreshold = 30.0;

    // A cell whose best match to EVERY canonical rep exceeds this resembles no known tile
    // type — a freshly-introduced item with no rep yet. Such cells get a new rep appended.
    // Clean settled tiles match their rep at ~0-6, an unrepresented tile reads 100+, so
    // this sits in a wide empty gap.
    private const double NewTypeMatchThreshold = 34.0;

    // Lower bound on cluster count for the main board clustering — a guard against a
    // pathological full collapse.
    private const int MinClusters = 2;

    // Canonical capture: the board must be genuinely STILL for this many consecutive
    // frames, then the canonical is clustered from their per-cell average. There is NO
    // force-capture fallback — capturing during a cascade/dissolve animation produces a
    // garbage canonical that scrambles every later match. If the board never settles, no
    // canonical is captured (the overlay simply shows nothing until it does).
    // BufferDepth spans a "suggested move" pulse cycle so the best-frame assignment sees
    // each hint tile near its natural (un-scaled) size.
    private const int WarmupFrames = 10;
    private const int BufferDepth = 8;

    // Per-cell structure-only interframe distance above this = that cell's tile is in
    // motion. StructMotionMinCells of them = the board is mid-cascade. Structure NCC is
    // brightness-invariant, so flashing/pulse never trips these — only real tile motion.
    private const double StructMotionCellThreshold = 25.0;
    private const int StructMotionMinCells = 5;
    private const double StructStillAvgThreshold = 8.0;

    // The board must be structurally still for this many consecutive frames before the
    // re-commit logic begins evaluating.
    private const int SettleConfirmFrames = 2;
    // Hard cap on re-commit deferral — bounds the post-move wait for the rare case of a
    // genuinely-new tile type (which never matches a rep until one is appended).
    private const int MaxCommitWaitFrames = 12;
    // The re-commit is HELD until every cell's best match is below this — i.e. every cell,
    // freshly-entered ones included, has found a clean SETTLED frame in the buffer. A tile
    // still animating in resembles NO rep (distance 100+), so the commit waits it out
    // instead of freezing a wrong assignment. Settled tiles sit at ~2-6, so 20 is a
    // wide-margin "everything has settled" line.
    private const double CommitClearThreshold = 20.0;

    private List<CellSignature>? _canonicalClusterReps;
    private CellSignature[]? _previousSignatures;
    private int[]? _settledIds;
    private double[]? _settledDists;
    private readonly Queue<CellSignature[]> _stillBuffer = new();
    private int _stillCount;
    private bool _committedThisSettle;
    private int _framesWaitingToCommit;

    /// <summary>
    /// True once the board has settled AND its cluster IDs have been committed. Both this
    /// and <see cref="LastFrameClusterIdsStable"/> drive the OverlayWindow display gate.
    /// </summary>
    public bool LastFrameWasStable { get; private set; }

    /// <inheritdoc cref="LastFrameWasStable"/>
    public bool LastFrameClusterIdsStable { get; private set; }

    /// <summary>
    /// Per-cell best-match distance to the canonical, parallel to the last
    /// <see cref="ClusterCells"/> result. A clean settled tile reads ~2-6; a cell that
    /// resembles no rep reads 100+. Exposed for the on-screen debug grid.
    /// </summary>
    public IReadOnlyList<double>? LastCellMatchDistances => _settledDists;

    /// <summary>True until the canonical has been captured for the current game.</summary>
    public bool NeedsRecapture => _canonicalClusterReps is null;

    /// <summary>
    /// PNG bytes of the latest cell-crop montage (the 49 per-cell crops the clusterer
    /// actually consumed, labeled with cluster ID). Refreshed on every capture/commit.
    /// Exposed so the overlay can show it live in the debug window.
    /// </summary>
    public byte[]? LastCropMontagePng { get; private set; }

    /// <summary>
    /// Unused — clustering is metric-driven and infers the cluster count itself; it does
    /// not take a target. Kept on the API because OverlayWindow still sets it.
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
        _settledDists = null;
        _stillBuffer.Clear();
        _stillCount = 0;
        _committedThisSettle = false;
        _framesWaitingToCommit = 0;
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
        // ≈ 0. Only genuine tile motion (a cascade) registers.
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

        // --- Still-frame buffer ---
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
            _committedThisSettle = false;
            _framesWaitingToCommit = 0;
        }

        // --- Canonical capture (only from a genuinely still board) ---
        if (_canonicalClusterReps is null && _stillCount >= WarmupFrames)
        {
            CaptureCanonical(AverageBuffer());
            _settledIds = AssignBestFrame(_stillBuffer.ToArray(), out double[] capDists);
            _settledDists = capDists;
            _committedThisSettle = true;
            LogGrid("CAPTURE", _settledIds, capDists);
            DumpCellCrops(bgrFrame, cells, _settledIds);
        }

        // --- Per-cascade re-commit ---
        // Cluster IDs are frozen between cascades. After SettleConfirmFrames of structural
        // stillness the cascade is positionally over — but freshly-entered tiles may still
        // be animating in. The commit is HELD until every cell matches a rep cleanly
        // (worstMatch below CommitClearThreshold); a still-animating tile resembles no rep,
        // so this waits it out instead of freezing a wrong assignment. At the wait cap,
        // any cell still matching nothing is a genuinely-new tile type → a rep is appended
        // for it. Either way the new cluster IDs are then committed.
        if (_canonicalClusterReps is not null && _settledIds is not null
            && !_committedThisSettle && _stillCount >= SettleConfirmFrames)
        {
            _framesWaitingToCommit++;
            CellSignature[][] frames = _stillBuffer.ToArray();
            int[] candidate = AssignBestFrame(frames, out double[] cellDists);
            double worstMatch = 0;
            for (int i = 0; i < cellDists.Length; i++)
                if (cellDists[i] > worstMatch) worstMatch = cellDists[i];

            if (worstMatch < CommitClearThreshold || _framesWaitingToCommit >= MaxCommitWaitFrames)
            {
                // Any cell matching NO existing rep is a freshly-introduced tile type.
                // Cluster those cells and APPEND new reps (the canonical only grows;
                // existing reps untouched), then re-assign so the new tiles — and the
                // ones already on the board — all land in the right cluster THIS commit.
                List<int> unmatched = new();
                for (int i = 0; i < cellDists.Length; i++)
                    if (cellDists[i] > NewTypeMatchThreshold) unmatched.Add(i);
                if (unmatched.Count > 0)
                {
                    CellSignature[] avg = AverageBuffer();
                    CellSignature[] newCells = new CellSignature[unmatched.Count];
                    for (int k = 0; k < unmatched.Count; k++) newCells[k] = avg[unmatched[k]];
                    _ = Cluster(newCells, 1, out List<CellSignature> newReps);
                    _canonicalClusterReps.AddRange(newReps);
                    ClustererLog.Write($"{unmatched.Count} cells matched no rep — appended " +
                                       $"{newReps.Count} cluster(s); canonical now {_canonicalClusterReps.Count}");
                    candidate = AssignBestFrame(frames, out cellDists);
                }
                _settledIds = candidate;
                _settledDists = cellDists;
                _committedThisSettle = true;
                LogGrid($"COMMIT {_framesWaitingToCommit}f", candidate, cellDists);
                DumpCellCrops(bgrFrame, cells, candidate);
            }
        }

        bool settled = _settledIds is not null && _settledIds.Length == n && _committedThisSettle;
        LastFrameWasStable = settled;
        LastFrameClusterIdsStable = settled;

        return _settledIds is not null && _settledIds.Length == n
            ? (int[])_settledIds.Clone()
            : new int[n];
    }

    /// <summary>
    /// Assign each cell to a canonical rep using its BEST frame across the still buffer:
    /// for each cell, score every rep by the MINIMUM distance over all buffered frames,
    /// then take the closest rep. PG's "suggested move" hint scale-animates two tiles
    /// forever — they have no at-rest frame — but across a buffer spanning a pulse cycle
    /// at least one frame catches each hint tile near its natural scale.
    /// <paramref name="cellDists"/> returns each cell's best-match distance to the
    /// canonical — the diagnostic + re-commit signal.
    /// </summary>
    private int[] AssignBestFrame(CellSignature[][] frames, out double[] cellDists)
    {
        List<CellSignature> reps = _canonicalClusterReps!;
        int n = frames.Length > 0 ? frames[^1].Length : 0;
        int[] ids = new int[n];
        cellDists = new double[n];
        for (int i = 0; i < n; i++)
        {
            int bestC = 0;
            double bestDist = double.MaxValue;
            for (int c = 0; c < reps.Count; c++)
            {
                double repBest = double.MaxValue;
                foreach (CellSignature[] f in frames)
                {
                    if (f.Length != n) continue;
                    double d = Distance(f[i], reps[c]);
                    if (d < repBest) repBest = d;
                }
                if (repBest < bestDist) { bestDist = repBest; bestC = c; }
            }
            ids[i] = bestC;
            cellDists[i] = bestDist < double.MaxValue ? bestDist : 0;
        }
        return ids;
    }

    /// <summary>
    /// Diagnostic: log the 7x7 board as clusterID:matchDistance so a mis-categorized cell
    /// can be told apart — low distance = a clean tile matched to a wrong rep (rep
    /// problem); high distance = an unsettled / unrepresented cell.
    /// </summary>
    private static void LogGrid(string tag, int[] ids, double[] dists)
    {
        if (ids.Length != 49 || dists.Length != 49)
        {
            ClustererLog.Write($"{tag}: {ids.Length} cells");
            return;
        }
        System.Text.StringBuilder sb = new($"{tag} — grid (id:dist):");
        for (int r = 0; r < 7; r++)
        {
            sb.Append(Environment.NewLine).Append("   ");
            for (int c = 0; c < 7; c++)
            {
                int i = r * 7 + c;
                sb.Append($"{ids[i]}:{dists[i]:F0}".PadRight(8));
            }
        }
        ClustererLog.Write(sb.ToString());
    }

    private void CaptureCanonical(CellSignature[] signatures)
    {
        _ = Cluster(signatures, MinClusters, out List<CellSignature> reps);
        _canonicalClusterReps = reps;
        ClustererLog.Write($"Canonical CAPTURED: {reps.Count} clusters from {signatures.Length} cells " +
                           $"(avg of {_stillBuffer.Count} still frames)");
    }

    /// <summary>
    /// Agglomerative (centroid-linkage) clustering. Repeatedly merges the two closest
    /// clusters and STOPS once the closest remaining pair is at least
    /// <see cref="ClusterThreshold"/> apart. No target cluster count — the number of
    /// clusters is whatever the data supports, so two visually-distinct items are never
    /// merged just to satisfy a count. <paramref name="minClusters"/> floors the result
    /// (2 for the whole board; 1 when clustering a handful of new-type cells).
    /// </summary>
    private static int[] Cluster(CellSignature[] sigs, int minClusters, out List<CellSignature> reps)
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

        int floor = Math.Min(minClusters, n);
        while (clusters.Count > floor)
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
            if (bestA < 0 || bestDist >= ClusterThreshold) break;

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
    /// Full distance: structure NCC distance + color distance. Both pulse/flash-invariant.
    ///
    /// Color distance = mean of the THREE most-different regions of the 4x4 LAB-chroma
    /// grid. A plain mean over all 16 regions diluted a LOCALIZED color difference — e.g.
    /// green vs red Oil differ only in the small liquid area, and averaging that against
    /// ~13 identical glass/cork regions made the two oils measure as nearly the same item.
    /// The top-3 keeps a localized difference loud, and still catches a whole-icon color
    /// difference (the flowers) because then every region is high anyway.
    /// </summary>
    private static double Distance(CellSignature a, CellSignature b)
    {
        float[] ca = a.Color, cb = b.Color;
        double d0 = 0, d1 = 0, d2 = 0;   // three largest region diffs, descending
        for (int r = 0; r < ColorGridDim * ColorGridDim; r++)
        {
            int idx = r * 2;
            double rd = Math.Abs(ca[idx] - cb[idx]) + Math.Abs(ca[idx + 1] - cb[idx + 1]);
            if (rd > d0) { d2 = d1; d1 = d0; d0 = rd; }
            else if (rd > d1) { d2 = d1; d1 = rd; }
            else if (rd > d2) { d2 = rd; }
        }
        return StructureDistance(a, b) + (d0 + d1 + d2) / 3.0;
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

    /// <summary>
    /// Diagnostic: tile the 49 inner-58% crops (exactly what ComputeSignature consumes)
    /// into one montage, each labeled with its assigned cluster ID, saved to TEMP. Lets us
    /// SEE whether the clusterer is fed clean centered icons or garbage/offset crops.
    /// </summary>
    private void DumpCellCrops(OpenCvMat bgrFrame, IReadOnlyList<OpenCvRect> cells, int[] ids)
    {
        try
        {
            int n = cells.Count;
            const int cols = 7;
            int rows = (n + cols - 1) / cols;
            const int tile = 80;
            using OpenCvMat montage = new(rows * tile, cols * tile, MatType.CV_8UC3, Scalar.All(30));
            for (int i = 0; i < n; i++)
            {
                int marginX = (int)(cells[i].Width * (1.0 - CenterCropFraction) / 2.0);
                int marginY = (int)(cells[i].Height * (1.0 - CenterCropFraction) / 2.0);
                OpenCvRect inner = new(cells[i].X + marginX, cells[i].Y + marginY,
                    cells[i].Width - 2 * marginX, cells[i].Height - 2 * marginY);
                OpenCvRect safe = ClampRect(inner, bgrFrame.Cols, bgrFrame.Rows);
                if (safe.Width <= 0 || safe.Height <= 0) continue;
                int r = i / cols, c = i % cols;
                using (OpenCvMat crop = new(bgrFrame, safe))
                using (OpenCvMat resized = new())
                {
                    Cv2.Resize(crop, resized, new Size(tile, tile));
                    resized.CopyTo(montage[new OpenCvRect(c * tile, r * tile, tile, tile)]);
                }
                string label = i < ids.Length ? ids[i].ToString() : "?";
                Cv2.PutText(montage, label, new Point(c * tile + 4, r * tile + 24),
                    HersheyFonts.HersheySimplex, 0.8, new Scalar(0, 255, 255), 2);
            }
            Cv2.ImEncode(".png", montage, out byte[] montagePng);
            LastCropMontagePng = montagePng;
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pg-loot-master-cellcrops.png");
            File.WriteAllBytes(path, montagePng);
        }
        catch (Exception ex)
        {
            ClustererLog.Write($"DumpCellCrops failed: {ex.GetType().Name}: {ex.Message}");
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
