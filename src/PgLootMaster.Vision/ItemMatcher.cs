using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;
using OpenCvRect = OpenCvSharp.Rect;

namespace PgLootMaster.Vision;

internal sealed class PreparedCrop : IDisposable
{
    public OpenCvMat ImageBgr { get; }
    public OpenCvMat ImageGray { get; }
    public OpenCvMat Mask { get; }
    public OpenCvMat HueHist { get; }
    public Scalar MeanLab { get; }
    public double AspectRatio { get; }
    // 3×3 center-grid BGR samples: 9 colors at fixed positions in the prepared (48×48) image.
    // Catches items that share overall color but differ at specific spots.
    public Vec3b[] Fingerprint { get; }
    // Max saturation found anywhere in the foreground mask. Distinguishes items that have a
    // saturated accent pixel (Mercury's blue dot) from uniformly desaturated items (Winterhue
    // gray). Single scalar so within-item variance is small.
    public int MaxSaturation { get; }
    // 7 log-transformed Hu moments of the icon's foreground mask. Captures shape invariants
    // (translation/scale/rotation independent) — discriminates flower vs flask vs gem etc.
    public double[] HuMoments { get; }
    // 2D histogram of (a*, b*) values for foreground pixels. Captures color DISTRIBUTION
    // (multi-color makeup) — discriminates icons with similar mean color but different
    // color compositions (e.g. flower with yellow center + green leaves vs flask with
    // red liquid + gold trim).
    public OpenCvMat ChromaHist { get; }

    public PreparedCrop(OpenCvMat imageBgr, OpenCvMat imageGray, OpenCvMat mask, OpenCvMat hueHist, Scalar meanLab, double aspectRatio, Vec3b[] fingerprint, int maxSaturation, double[] huMoments, OpenCvMat chromaHist)
    {
        ImageBgr = imageBgr;
        ImageGray = imageGray;
        Mask = mask;
        HueHist = hueHist;
        MeanLab = meanLab;
        AspectRatio = aspectRatio;
        Fingerprint = fingerprint;
        MaxSaturation = maxSaturation;
        HuMoments = huMoments;
        ChromaHist = chromaHist;
    }

    public void Dispose()
    {
        ImageBgr.Dispose();
        ImageGray.Dispose();
        Mask.Dispose();
        ChromaHist.Dispose();
        HueHist.Dispose();
    }
}

public sealed class ItemMatcher
{
    private const int MatchSize = 48;
    private const double CellCenterCropFraction = 0.7;
    private const double TemplateCenterCropFraction = 0.95;
    private const int SaturationCutoff = 25;
    private const int DarkValueCutoff = 90;
    private const int BrightValueCutoff = 210;
    private const int HueBins = 24;

    private PreparedCrop[] _templatePrepared = Array.Empty<PreparedCrop>();
    private IReadOnlyList<SidebarItem> _templates = Array.Empty<SidebarItem>();
    private int[]? _previousLabels;
    // Per-cluster-ID memory: clusterId → previously assigned template index. Survives
    // cluster count changes (unlike the array-indexed _previousLabels). Used for hysteresis
    // so labels don't flip when scores fluctuate slightly within stable-board noise.
    private readonly Dictionary<int, int> _lastLabelByClusterId = new();
    private int[]? _previousLabelLogIds;
    // Per-cell-index cache of the SPLIT outcome: clusterIdAtSplit[cellIdx] = final cluster id
    // assigned to that cell. Reused across frames so pulse-induced fingerprint jitter doesn't
    // flip a cell between sub-clusters.
    private int[]? _previousSplitIds;
    private int[]? _previousInputClusterIds;
    private const double HysteresisMargin = 0.10;

    public IReadOnlyList<SidebarItem> Templates => _templates;
    public int TemplateCount => _templatePrepared.Length;

    /// <summary>
    /// Drop the split-cache so the next SplitMixedClusters call re-evaluates from scratch.
    /// Used by the Settings "Recompute clusters" button alongside CellClusterer.Reset().
    /// </summary>
    public void Reset()
    {
        _previousSplitIds = null;
        _previousInputClusterIds = null;
    }

    public void SetTemplates(IReadOnlyList<SidebarItem> templates)
    {
        foreach (PreparedCrop p in _templatePrepared) p.Dispose();
        _templates = templates;
        _templatePrepared = new PreparedCrop[templates.Count];
        for (int i = 0; i < templates.Count; i++)
        {
            _templatePrepared[i] = PrepareCrop(templates[i].Icon, TemplateCenterCropFraction);
        }
    }

    // Split clusters that contain cells with widely-different hue signatures. Catches the
    // case where the BGR signature distance is just under the clusterer's threshold but
    // the cells are actually distinct items by color. New cluster IDs are appended above
    // the existing max ID. Returns a new cluster-ids array.
    public int[] SplitMixedClusters(OpenCvMat bgrFrame, IReadOnlyList<OpenCvRect> cells, IReadOnlyList<int> clusterIds)
    {
        if (cells.Count == 0 || clusterIds.Count != cells.Count) return clusterIds.ToArray();

        // Reuse cached split if the upstream clusterer's IDs match last frame. The clusterer
        // keeps IDs sticky during pulse (its sticky-buffer logic). Only when IDs actually
        // change (a swap, refill, cascade) do we re-evaluate the split.
        if (_previousSplitIds is not null
            && _previousSplitIds.Length == cells.Count
            && _previousInputClusterIds is not null
            && _previousInputClusterIds.Length == cells.Count)
        {
            bool identical = true;
            for (int i = 0; i < cells.Count; i++)
            {
                if (_previousInputClusterIds[i] != clusterIds[i]) { identical = false; break; }
            }
            if (identical) return (int[])_previousSplitIds.Clone();
        }

        // Compute hue histograms + mean LAB + 3×3 fingerprint + max-sat for each cell.
        OpenCvMat[] hists = new OpenCvMat[cells.Count];
        Scalar[] labs = new Scalar[cells.Count];
        Vec3b[][] fingerprints = new Vec3b[cells.Count][];
        int[] maxSats = new int[cells.Count];
        for (int i = 0; i < cells.Count; i++)
        {
            using PreparedCrop p = PrepareCropFromRect(bgrFrame, cells[i], CellCenterCropFraction);
            hists[i] = p.HueHist.Clone();
            labs[i] = p.MeanLab;
            fingerprints[i] = (Vec3b[])p.Fingerprint.Clone();
            maxSats[i] = p.MaxSaturation;
        }

        int maxId = 0;
        for (int i = 0; i < clusterIds.Count; i++) if (clusterIds[i] > maxId) maxId = clusterIds[i];
        int[] result = new int[cells.Count];
        for (int i = 0; i < cells.Count; i++) result[i] = clusterIds[i];

        System.Text.StringBuilder splitLog = new();
        splitLog.AppendLine($"-- splitter dump @ {DateTime.Now:HH:mm:ss.fff}: {cells.Count} cells --");
        try
        {
            int nextId = maxId + 1;
            for (int cid = 0; cid <= maxId; cid++)
            {
                List<int> members = new();
                for (int i = 0; i < result.Length; i++) if (result[i] == cid) members.Add(i);
                if (members.Count < 2) continue;

                int anchorMaxSat = maxSats[members[0]];
                splitLog.AppendLine($"cluster {cid}: {members.Count} cells, anchor=cell[{members[0] / 7},{members[0] % 7}] maxSat={anchorMaxSat}");
                OpenCvMat anchorHist = hists[members[0]];
                Scalar anchorLab = labs[members[0]];
                List<int> splitOff = new();
                Vec3b[] anchorFp = fingerprints[members[0]];
                foreach (int idx in members)
                {
                    if (idx == members[0]) continue;
                    double corr = Cv2.CompareHist(anchorHist, hists[idx], HistCompMethods.Correl);
                    double dA = anchorLab.Val1 - labs[idx].Val1;
                    double dB = anchorLab.Val2 - labs[idx].Val2;
                    double chromaDist = Math.Sqrt(dA * dA + dB * dB);

                    Vec3b[] fp = fingerprints[idx];
                    double maxFpDist = 0;
                    for (int k = 0; k < 9; k++)
                    {
                        int da = anchorFp[k].Item1 - fp[k].Item1;
                        int db = anchorFp[k].Item2 - fp[k].Item2;
                        double d = Math.Sqrt(da * da + db * db);
                        if (d > maxFpDist) maxFpDist = d;
                    }
                    int maxSatDiff = Math.Abs(anchorMaxSat - maxSats[idx]);

                    // Triggers:
                    //  1. Very different hue distribution (corr < 0.3)
                    //  2. Chroma AND fingerprint both clearly disagree
                    //  3. Very large fingerprint distance (>25) — single-spot drastic disagreement
                    //  4. Max-saturation differs significantly (>40): one item has a saturated
                    //     accent pixel, the other doesn't (Mercury blue accent vs Winterhue gray).
                    //     Within-item the max-sat is consistent because the accent is present in
                    //     every cell of the same item.
                    bool veryDifferentHue = corr < 0.3;
                    bool colorAndPointDisagree = chromaDist > 15 && maxFpDist > 30;
                    bool drasticPointDisagree = maxFpDist > 25;
                    bool maxSatDiverges = maxSatDiff > 40;
                    bool split = veryDifferentHue || colorAndPointDisagree || drasticPointDisagree || maxSatDiverges;
                    splitLog.AppendLine(
                        $"  cell[{idx / 7},{idx % 7}] corr={corr:F3} chroma={chromaDist:F1} maxFp={maxFpDist:F1} maxSat={maxSats[idx]} dSat={maxSatDiff} split={split}");
                    if (split) splitOff.Add(idx);
                }
                if (splitOff.Count > 0)
                {
                    // Sub-cluster the split-off cells by feature similarity, so multiple
                    // distinct items mixed into one parent cluster each get their own ID.
                    // Greedy: for each split-off cell, find a sub-group whose representative
                    // is close in chroma+fingerprint+maxSat; else start a new sub-group.
                    List<List<int>> subGroups = new();
                    foreach (int idx in splitOff)
                    {
                        int assigned = -1;
                        for (int g = 0; g < subGroups.Count; g++)
                        {
                            int repIdx = subGroups[g][0];
                            double dA2 = labs[repIdx].Val1 - labs[idx].Val1;
                            double dB2 = labs[repIdx].Val2 - labs[idx].Val2;
                            double chromaDist2 = Math.Sqrt(dA2 * dA2 + dB2 * dB2);
                            double maxFpDist2 = 0;
                            for (int k = 0; k < 9; k++)
                            {
                                int da = fingerprints[repIdx][k].Item1 - fingerprints[idx][k].Item1;
                                int db = fingerprints[repIdx][k].Item2 - fingerprints[idx][k].Item2;
                                double d = Math.Sqrt(da * da + db * db);
                                if (d > maxFpDist2) maxFpDist2 = d;
                            }
                            int dSat2 = Math.Abs(maxSats[repIdx] - maxSats[idx]);
                            // Close enough to be the same sub-item: features within tight bounds.
                            if (chromaDist2 < 5 && maxFpDist2 < 15 && dSat2 < 20)
                            {
                                assigned = g;
                                break;
                            }
                        }
                        if (assigned < 0)
                        {
                            subGroups.Add(new List<int> { idx });
                        }
                        else
                        {
                            subGroups[assigned].Add(idx);
                        }
                    }
                    foreach (List<int> group in subGroups)
                    {
                        foreach (int idx in group) result[idx] = nextId;
                        nextId++;
                    }
                    splitLog.AppendLine($"  → split into {subGroups.Count} sub-cluster(s) of sizes [{string.Join(",", subGroups.Select(g => g.Count))}]");
                }
            }
        }
        finally
        {
            foreach (OpenCvMat h in hists) h.Dispose();
        }
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "pg-loot-master-splitter.log"),
                splitLog.ToString());
        }
        catch { }

        _previousSplitIds = (int[])result.Clone();
        _previousInputClusterIds = clusterIds.ToArray();
        return result;
    }

    // Label each cluster (group of cells with the same clusterId) with the best-matching
    // template index. Aggregates feature scores across all cells in each cluster — averaging
    // out per-cell noise. Returns an array where index = clusterId, value = template index.
    public int[] LabelClusters(OpenCvMat bgrFrame, IReadOnlyList<OpenCvRect> cells, IReadOnlyList<int> clusterIds)
    {
        if (cells.Count == 0 || _templatePrepared.Length == 0 || clusterIds.Count != cells.Count)
            return Array.Empty<int>();

        int maxClusterId = 0;
        for (int i = 0; i < clusterIds.Count; i++)
            if (clusterIds[i] > maxClusterId) maxClusterId = clusterIds[i];
        int clusterCount = maxClusterId + 1;

        double[,] sumScores = new double[clusterCount, _templatePrepared.Length];
        // Per-feature accumulators for diagnostic logging.
        double[,] sumHue = new double[clusterCount, _templatePrepared.Length];
        double[,] sumLab = new double[clusterCount, _templatePrepared.Length];
        double[,] sumNcc = new double[clusterCount, _templatePrepared.Length];
        double[,] sumAspect = new double[clusterCount, _templatePrepared.Length];
        double[,] sumHu = new double[clusterCount, _templatePrepared.Length];
        double[,] sumChroma = new double[clusterCount, _templatePrepared.Length];
        int[] cellsPerCluster = new int[clusterCount];

        for (int i = 0; i < cells.Count; i++)
        {
            int cid = clusterIds[i];
            if (cid < 0) continue;
            cellsPerCluster[cid]++;
            using PreparedCrop cellPrepared = PrepareCropFromRect(bgrFrame, cells[i], CellCenterCropFraction);
            for (int t = 0; t < _templatePrepared.Length; t++)
            {
                double hue = HueHistogramScore(cellPrepared, _templatePrepared[t]);
                double lab = LabColorScore(cellPrepared, _templatePrepared[t]);
                double ncc = NccScore(cellPrepared, _templatePrepared[t]);
                double aspect = AspectRatioScore(cellPrepared, _templatePrepared[t]);
                double hu = HuMomentScore(cellPrepared, _templatePrepared[t]);
                double chroma = ChromaHistScore(cellPrepared, _templatePrepared[t]);
                // Chroma histogram replaces mean-LAB's role as the primary color feature.
                // It captures color DISTRIBUTION (multi-color icons separate cleanly).
                sumScores[cid, t] += 0.20 * hue + 0.10 * lab + 0.10 * ncc + 0.10 * aspect + 0.0 * hu + 0.50 * chroma;
                sumHue[cid, t] += hue;
                sumLab[cid, t] += lab;
                sumNcc[cid, t] += ncc;
                sumAspect[cid, t] += aspect;
                sumHu[cid, t] += hu;
                sumChroma[cid, t] += chroma;
            }
        }

        // Bipartite assignment: each template used at most once. Greedy max-pair:
        // repeatedly pick the (cluster, template) with the highest avg score and remove both.
        double[,] avgScores = new double[clusterCount, _templatePrepared.Length];
        for (int c = 0; c < clusterCount; c++)
        {
            if (cellsPerCluster[c] == 0) continue;
            for (int t = 0; t < _templatePrepared.Length; t++)
            {
                avgScores[c, t] = sumScores[c, t] / cellsPerCluster[c];
            }
        }

        int[] labels = new int[clusterCount];
        for (int i = 0; i < labels.Length; i++) labels[i] = -1;
        bool[] clusterTaken = new bool[clusterCount];
        bool[] templateTaken = new bool[_templatePrepared.Length];
        for (int c = 0; c < clusterCount; c++)
        {
            if (cellsPerCluster[c] == 0) clusterTaken[c] = true;
        }

        // Hysteresis: if the previous frame's label for this cluster scores within
        // HysteresisMargin of the current best, keep the previous label. Avoids flicker
        // between near-tied templates frame to frame.
        if (_previousLabels is not null && _previousLabels.Length == clusterCount)
        {
            for (int c = 0; c < clusterCount; c++)
            {
                if (cellsPerCluster[c] == 0) continue;
                int prev = _previousLabels[c];
                if (prev < 0 || prev >= _templatePrepared.Length) continue;
                // If the previously assigned template still scores close to current best, keep it.
                double prevScore = avgScores[c, prev];
                double bestScore = double.NegativeInfinity;
                for (int t = 0; t < _templatePrepared.Length; t++)
                    if (avgScores[c, t] > bestScore) bestScore = avgScores[c, t];
                if (bestScore - prevScore < HysteresisMargin)
                {
                    labels[c] = prev;
                    clusterTaken[c] = true;
                    templateTaken[prev] = true;
                }
            }
        }

        int pairsToAssign = Math.Min(
            clusterCount - clusterTaken.Count(x => x),
            _templatePrepared.Length - templateTaken.Count(x => x));
        for (int step = 0; step < pairsToAssign; step++)
        {
            int bestC = -1, bestT = -1;
            double bestS = double.NegativeInfinity;
            for (int c = 0; c < clusterCount; c++)
            {
                if (clusterTaken[c]) continue;
                for (int t = 0; t < _templatePrepared.Length; t++)
                {
                    if (templateTaken[t]) continue;
                    if (avgScores[c, t] > bestS)
                    {
                        bestS = avgScores[c, t];
                        bestC = c;
                        bestT = t;
                    }
                }
            }
            if (bestC < 0) break;
            labels[bestC] = bestT;
            clusterTaken[bestC] = true;
            templateTaken[bestT] = true;
        }

        // Fallback: for any cluster left unlabeled (more clusters than templates), assign
        // it the label of the already-labeled cluster whose avg-score vector is closest.
        // This makes the leftover share a name with its likely visual sibling rather than
        // showing "(unknown)" — the borders still distinguish them, but the user gets a
        // best-guess name instead of nothing.
        for (int c = 0; c < clusterCount; c++)
        {
            if (labels[c] != -1) continue;
            if (cellsPerCluster[c] == 0) continue;
            int bestOther = -1;
            double bestSim = double.NegativeInfinity;
            for (int o = 0; o < clusterCount; o++)
            {
                if (o == c || labels[o] < 0 || cellsPerCluster[o] == 0) continue;
                // Negative L2 distance between score vectors → higher = more similar.
                double sumSq = 0;
                for (int t = 0; t < _templatePrepared.Length; t++)
                {
                    double d = avgScores[c, t] - avgScores[o, t];
                    sumSq += d * d;
                }
                double sim = -sumSq;
                if (sim > bestSim) { bestSim = sim; bestOther = o; }
            }
            if (bestOther >= 0) labels[c] = labels[bestOther];
        }

        _previousLabels = (int[])labels.Clone();

        // Only dump diagnostic log when labels actually changed (or first time). Otherwise the
        // file IO every frame adds latency to swap recommendations.
        bool labelsChanged = _previousLabelLogIds is null
            || _previousLabelLogIds.Length != labels.Length
            || !labels.SequenceEqual(_previousLabelLogIds);
        if (!labelsChanged) return labels;
        _previousLabelLogIds = (int[])labels.Clone();

        try
        {
            System.Text.StringBuilder lb = new();
            lb.AppendLine($"-- labeler dump @ {DateTime.Now:HH:mm:ss.fff}: {clusterCount} clusters, {_templatePrepared.Length} templates (weights: hue 0.20, lab 0.10, ncc 0.10, aspect 0.10, hu 0.0, chroma 0.50) --");
            for (int c = 0; c < clusterCount; c++)
            {
                if (cellsPerCluster[c] == 0) { lb.AppendLine($"cluster {c}: 0 cells (unused)"); continue; }
                lb.Append($"cluster {c}: {cellsPerCluster[c]} cells, label={labels[c]} total=[");
                for (int t = 0; t < _templatePrepared.Length; t++)
                {
                    lb.Append(avgScores[c, t].ToString("F3"));
                    if (t + 1 < _templatePrepared.Length) lb.Append(", ");
                }
                lb.AppendLine("]");
                int cnt = cellsPerCluster[c];
                lb.Append("  hue=[");
                for (int t = 0; t < _templatePrepared.Length; t++) { lb.Append((sumHue[c, t] / cnt).ToString("F2")); if (t + 1 < _templatePrepared.Length) lb.Append(", "); }
                lb.AppendLine("]");
                lb.Append("  lab=[");
                for (int t = 0; t < _templatePrepared.Length; t++) { lb.Append((sumLab[c, t] / cnt).ToString("F2")); if (t + 1 < _templatePrepared.Length) lb.Append(", "); }
                lb.AppendLine("]");
                lb.Append("  ncc=[");
                for (int t = 0; t < _templatePrepared.Length; t++) { lb.Append((sumNcc[c, t] / cnt).ToString("F2")); if (t + 1 < _templatePrepared.Length) lb.Append(", "); }
                lb.AppendLine("]");
                lb.Append("  asp=[");
                for (int t = 0; t < _templatePrepared.Length; t++) { lb.Append((sumAspect[c, t] / cnt).ToString("F2")); if (t + 1 < _templatePrepared.Length) lb.Append(", "); }
                lb.AppendLine("]");
                lb.Append("  hu =[");
                for (int t = 0; t < _templatePrepared.Length; t++) { lb.Append((sumHu[c, t] / cnt).ToString("F2")); if (t + 1 < _templatePrepared.Length) lb.Append(", "); }
                lb.AppendLine("]");
                lb.Append("  chr=[");
                for (int t = 0; t < _templatePrepared.Length; t++) { lb.Append((sumChroma[c, t] / cnt).ToString("F2")); if (t + 1 < _templatePrepared.Length) lb.Append(", "); }
                lb.AppendLine("]");
            }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "pg-loot-master-labeler.log"), lb.ToString());
        }
        catch { }

        return labels;
    }

    public int[] MatchCells(OpenCvMat bgrFrame, IReadOnlyList<OpenCvRect> cells)
    {
        int[] ids = new int[cells.Count];
        if (_templatePrepared.Length == 0)
        {
            for (int i = 0; i < ids.Length; i++) ids[i] = -1;
            return ids;
        }

        double[,] scoreBreakdown = new double[cells.Count, _templatePrepared.Length * 4];
        double[] combined = new double[cells.Count * _templatePrepared.Length];

        for (int i = 0; i < cells.Count; i++)
        {
            using PreparedCrop cellPrepared = PrepareCropFromRect(bgrFrame, cells[i], CellCenterCropFraction);

            double[] hueScores = new double[_templatePrepared.Length];
            double[] colorScores = new double[_templatePrepared.Length];
            double[] nccScores = new double[_templatePrepared.Length];

            for (int t = 0; t < _templatePrepared.Length; t++)
            {
                hueScores[t] = HueHistogramScore(cellPrepared, _templatePrepared[t]);
                colorScores[t] = LabColorScore(cellPrepared, _templatePrepared[t]);
                nccScores[t] = NccScore(cellPrepared, _templatePrepared[t]);
            }

            int bestIdx = 0;
            double bestScore = double.NegativeInfinity;
            for (int t = 0; t < _templatePrepared.Length; t++)
            {
                double s = 0.4 * hueScores[t] + 0.4 * colorScores[t] + 0.2 * nccScores[t];
                combined[i * _templatePrepared.Length + t] = s;
                scoreBreakdown[i, t * 4 + 0] = hueScores[t];
                scoreBreakdown[i, t * 4 + 1] = colorScores[t];
                scoreBreakdown[i, t * 4 + 2] = nccScores[t];
                scoreBreakdown[i, t * 4 + 3] = s;
                if (s > bestScore) { bestScore = s; bestIdx = t; }
            }
            ids[i] = bestIdx;
        }

        try
        {
            string path = Path.Combine(Path.GetTempPath(), "pg-loot-master-matcher.log");
            using StreamWriter w = new(path, append: false);
            w.WriteLine($"-- matcher dump @ {DateTime.Now:HH:mm:ss.fff}: {cells.Count} cells, {_templatePrepared.Length} templates (combined = 0.4*hue + 0.4*lab + 0.2*ncc) --");
            for (int i = 0; i < cells.Count; i++)
            {
                int r = i / 7;
                int c = i % 7;
                System.Text.StringBuilder sb = new();
                sb.Append($"cell[{r},{c}] best={ids[i]} combined=[");
                for (int t = 0; t < _templatePrepared.Length; t++)
                {
                    sb.Append(combined[i * _templatePrepared.Length + t].ToString("F3"));
                    if (t + 1 < _templatePrepared.Length) sb.Append(", ");
                }
                sb.Append(']');
                w.WriteLine(sb.ToString());
            }
        }
        catch { }

        return ids;
    }

    // Hue histogram correlation. Hue alone strongly discriminates color-distinct items
    // (red strawberry vs green glass) regardless of brightness.
    private static double HueHistogramScore(PreparedCrop cell, PreparedCrop template)
    {
        return Cv2.CompareHist(cell.HueHist, template.HueHist, HistCompMethods.Correl);
    }

    // Dominant-color similarity in LAB space (perceptually uniform).
    // Weight a*/b* more than L* — chrominance discriminates items better than luminance.
    private static double LabColorScore(PreparedCrop cell, PreparedCrop template)
    {
        double dL = cell.MeanLab.Val0 - template.MeanLab.Val0;
        double dA = cell.MeanLab.Val1 - template.MeanLab.Val1;
        double dB = cell.MeanLab.Val2 - template.MeanLab.Val2;
        double dist = Math.Sqrt(0.5 * dL * dL + 1.5 * dA * dA + 1.5 * dB * dB);
        return Math.Max(0, 1.0 - dist / 50.0);
    }

    // Aspect ratio similarity. Elongated fish vs square candy differ strongly here.
    private static double AspectRatioScore(PreparedCrop cell, PreparedCrop template)
    {
        double ratio = cell.AspectRatio / template.AspectRatio;
        if (ratio < 1) ratio = 1 / ratio;
        // ratio=1.0 (identical) → score 1.0. ratio=2.0 (one is 2x the other) → score 0.
        return Math.Max(0, 1.0 - (ratio - 1.0));
    }

    // 2D (a*, b*) chrominance-histogram correlation. Captures full color distribution.
    // For multi-color icons this discriminates better than mean color: same mean ≠ same
    // composition.
    private static double ChromaHistScore(PreparedCrop cell, PreparedCrop template)
    {
        return Math.Max(0, Cv2.CompareHist(cell.ChromaHist, template.ChromaHist, HistCompMethods.Correl));
    }

    // Hu moments shape similarity. Invariant to translation/scale/rotation.
    // Lower L2 distance between log-Hu vectors → more similar shape.
    private static double HuMomentScore(PreparedCrop cell, PreparedCrop template)
    {
        double sumSq = 0;
        for (int i = 0; i < 7; i++)
        {
            double d = cell.HuMoments[i] - template.HuMoments[i];
            sumSq += d * d;
        }
        double dist = Math.Sqrt(sumSq);
        // Distances commonly fall in 0..3. Map to similarity in [0, 1].
        return Math.Max(0, 1.0 - dist / 3.0);
    }

    // Normalized cross-correlation on grayscale image. Captures shape structure.
    private static double NccScore(PreparedCrop cell, PreparedCrop template)
    {
        if (cell.ImageGray.Size() != template.ImageGray.Size())
            return 0;
        try
        {
            using OpenCvMat result = new();
            Cv2.MatchTemplate(cell.ImageGray, template.ImageGray, result, TemplateMatchModes.CCorrNormed, template.Mask);
            float v = result.At<float>(0, 0);
            return Math.Max(0, v);
        }
        catch
        {
            return 0;
        }
    }

    private static PreparedCrop PrepareCrop(OpenCvMat src, double centerFraction)
    {
        int marginX = (int)(src.Width * (1.0 - centerFraction) / 2.0);
        int marginY = (int)(src.Height * (1.0 - centerFraction) / 2.0);
        OpenCvRect inner = new(marginX, marginY,
            src.Width - 2 * marginX, src.Height - 2 * marginY);
        if (inner.Width <= 0 || inner.Height <= 0)
        {
            return EmptyCrop();
        }
        using OpenCvMat crop = new(src, inner);
        return TightCropAndPrepare(crop);
    }

    private static PreparedCrop PrepareCropFromRect(OpenCvMat bgrFrame, OpenCvRect cell, double centerFraction)
    {
        int marginX = (int)(cell.Width * (1.0 - centerFraction) / 2.0);
        int marginY = (int)(cell.Height * (1.0 - centerFraction) / 2.0);
        OpenCvRect inner = new(cell.X + marginX, cell.Y + marginY,
            cell.Width - 2 * marginX, cell.Height - 2 * marginY);
        OpenCvRect safe = ClampRect(inner, bgrFrame.Cols, bgrFrame.Rows);
        if (safe.Width <= 0 || safe.Height <= 0)
        {
            return EmptyCrop();
        }
        using OpenCvMat crop = new(bgrFrame, safe);
        return TightCropAndPrepare(crop);
    }

    private static PreparedCrop EmptyCrop()
    {
        OpenCvMat bgr = new(MatchSize, MatchSize, MatType.CV_8UC3, Scalar.All(0));
        OpenCvMat gray = new(MatchSize, MatchSize, MatType.CV_8UC1, Scalar.All(0));
        OpenCvMat mask = new(MatchSize, MatchSize, MatType.CV_8UC1, Scalar.All(0));
        OpenCvMat hist = new(HueBins, 1, MatType.CV_32FC1, Scalar.All(0));
        return new PreparedCrop(bgr, gray, mask, hist, new Scalar(0, 0, 0), 1.0, new Vec3b[9], 0, new double[7], new OpenCvMat(16 * 16, 1, MatType.CV_32FC1, Scalar.All(0)));
    }

    private static PreparedCrop TightCropAndPrepare(OpenCvMat crop)
    {
        // Compute the fingerprint FIRST, from the un-tight-cropped center crop, at fixed
        // pixel positions. Tight-cropping varies with pulse state (mask grows/shrinks with
        // brightness), so sampling the tight crop would put the 9 points on different actual
        // icon pixels between pulse states. Using the original crop's center grid keeps the
        // sample positions stable across pulses.
        using OpenCvMat stableSample = new();
        Cv2.Resize(crop, stableSample, new Size(MatchSize, MatchSize), 0, 0, InterpolationFlags.Area);
        using OpenCvMat stableLab = new();
        Cv2.CvtColor(stableSample, stableLab, ColorConversionCodes.BGR2Lab);
        Vec3b[] fingerprintEarly = new Vec3b[9];
        int[] fpCoords = new[]
        {
            MatchSize / 3,
            MatchSize / 2,
            (2 * MatchSize) / 3,
        };
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                Vec3b labPx = stableLab.At<Vec3b>(fpCoords[y], fpCoords[x]);
                fingerprintEarly[y * 3 + x] = new Vec3b(0, labPx.Item1, labPx.Item2);
            }
        }

        using OpenCvMat hsv = new();
        Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
        using OpenCvMat satHigh = new();
        Cv2.InRange(hsv, new Scalar(0, SaturationCutoff, 0), new Scalar(180, 255, 255), satHigh);
        using OpenCvMat valLow = new();
        Cv2.InRange(hsv, new Scalar(0, 0, 0), new Scalar(180, 255, DarkValueCutoff), valLow);
        using OpenCvMat iconMask = new();
        Cv2.BitwiseOr(satHigh, valLow, iconMask);
        using OpenCvMat closeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(iconMask, iconMask, MorphTypes.Close, closeKernel);

        // Tight-crop to the largest contour whose center is in the left 60% of the crop
        // (icons live on the left in sidebar templates; text/numbers bleed in on the right).
        using OpenCvMat maskForContours = iconMask.Clone();
        Cv2.FindContours(maskForContours, out Point[][] contours, out _,
            RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        OpenCvRect tight;
        if (contours.Length == 0)
        {
            tight = new OpenCvRect(0, 0, crop.Cols, crop.Rows);
        }
        else
        {
            int leftLimit = (int)(crop.Cols * 0.6);
            OpenCvRect? best = null;
            long bestArea = 0;
            for (int i = 0; i < contours.Length; i++)
            {
                OpenCvRect b = Cv2.BoundingRect(contours[i]);
                if (b.X + b.Width / 2 > leftLimit) continue;
                long a = (long)b.Width * b.Height;
                if (a > bestArea) { bestArea = a; best = b; }
            }
            if (best is null)
            {
                // Fallback: just the overall largest contour.
                OpenCvRect overall = Cv2.BoundingRect(contours[0]);
                long overallArea = (long)overall.Width * overall.Height;
                for (int i = 1; i < contours.Length; i++)
                {
                    OpenCvRect b = Cv2.BoundingRect(contours[i]);
                    long a = (long)b.Width * b.Height;
                    if (a > overallArea) { overallArea = a; overall = b; }
                }
                best = overall;
            }
            tight = ClampRect(best.Value, crop.Cols, crop.Rows);
            if (tight.Width < 5 || tight.Height < 5)
                tight = new OpenCvRect(0, 0, crop.Cols, crop.Rows);
        }

        using OpenCvMat tightImage = new(crop, tight);
        using OpenCvMat tightMask = new(iconMask, tight);
        using OpenCvMat tightHsv = new(hsv, tight);

        // BGR resized
        OpenCvMat resizedBgr = new();
        Cv2.Resize(tightImage, resizedBgr, new Size(MatchSize, MatchSize), 0, 0, InterpolationFlags.Area);

        // Mask resized
        OpenCvMat resizedMask = new();
        Cv2.Resize(tightMask, resizedMask, new Size(MatchSize, MatchSize), 0, 0, InterpolationFlags.Nearest);

        // Grayscale
        OpenCvMat resizedGray = new();
        Cv2.CvtColor(resizedBgr, resizedGray, ColorConversionCodes.BGR2GRAY);

        // Hue histogram (on tight-cropped HSV, foreground only).
        OpenCvMat hueHist = new();
        OpenCvMat[] hsvArr = new[] { tightHsv };
        try
        {
            Cv2.CalcHist(hsvArr, new[] { 0 }, tightMask, hueHist,
                1, new[] { HueBins }, new[] { new Rangef(0, 180) });
            Cv2.Normalize(hueHist, hueHist, 1, 0, NormTypes.L1);
        }
        catch
        {
            hueHist = new OpenCvMat(HueBins, 1, MatType.CV_32FC1, Scalar.All(0));
        }

        // Mean LAB color on foreground pixels.
        using OpenCvMat lab = new();
        Cv2.CvtColor(tightImage, lab, ColorConversionCodes.BGR2Lab);
        Scalar meanLab = Cv2.Mean(lab, tightMask);

        // Aspect ratio of the tight bbox — captures elongated vs square shapes.
        double aspect = tight.Height > 0 ? (double)tight.Width / tight.Height : 1.0;

        // Max saturation among foreground pixels. Items with a small saturated accent
        // (Mercury's blue dot) have a high max-sat (~100+) even though their mean-sat
        // and mean-color are similar to fully desaturated items (Winterhue gray).
        int maxSat = 0;
        {
            using OpenCvMat tightHsvOnly = new();
            Cv2.CvtColor(tightImage, tightHsvOnly, ColorConversionCodes.BGR2HSV);
            OpenCvMat[] channels = Cv2.Split(tightHsvOnly);
            try
            {
                Cv2.MinMaxLoc(channels[1], out _, out double maxVal, out _, out _, tightMask);
                maxSat = (int)maxVal;
            }
            finally
            {
                foreach (OpenCvMat ch in channels) ch.Dispose();
            }
        }

        // Hu moments of the icon's shape (from resized mask). Translation/scale/rotation
        // invariant — captures shape regardless of orientation. Log-transform for stability.
        double[] huMoments = new double[7];
        try
        {
            Moments mom = Cv2.Moments(resizedMask, binaryImage: true);
            double[] rawHu = mom.HuMoments();
            for (int i = 0; i < 7; i++)
            {
                double abs = Math.Abs(rawHu[i]);
                huMoments[i] = -Math.Sign(rawHu[i]) * Math.Log10(abs + 1e-10);
            }
        }
        catch { /* leave as zeros */ }

        // 2D (a*, b*) chrominance histogram on foreground pixels. 16x16 bins captures
        // color distribution that mean-LAB cannot — e.g. multi-color icons that average
        // to similar mean but have very different color makeup.
        OpenCvMat chromaHist = new(16 * 16, 1, MatType.CV_32FC1, Scalar.All(0));
        try
        {
            using OpenCvMat lab2 = new();
            Cv2.CvtColor(tightImage, lab2, ColorConversionCodes.BGR2Lab);
            using OpenCvMat hist2D = new();
            OpenCvMat[] labArr = new[] { lab2 };
            Cv2.CalcHist(labArr, new[] { 1, 2 }, tightMask, hist2D,
                2, new[] { 16, 16 }, new[] { new Rangef(0, 256), new Rangef(0, 256) });
            Cv2.Normalize(hist2D, hist2D, 1, 0, NormTypes.L1);
            // Flatten 16×16 → 256×1 column for easy comparison.
            chromaHist.Dispose();
            chromaHist = hist2D.Reshape(0, 16 * 16).Clone();
        }
        catch
        {
            // Leave as zeros.
        }

        return new PreparedCrop(resizedBgr, resizedGray, resizedMask, hueHist, meanLab, aspect, fingerprintEarly, maxSat, huMoments, chromaHist);
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
