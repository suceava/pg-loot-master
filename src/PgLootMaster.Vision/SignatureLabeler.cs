using System.Linq;
using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;

namespace PgLootMaster.Vision;

/// <summary>
/// Names each board cluster with a sidebar item.
///
/// The board tile and the sidebar icon are the SAME artwork — the sidebar just renders it
/// smaller and on a darker background. So the clusterer's NCC-structure + LAB-chroma
/// signature, which tells board tiles apart perfectly, also matches a cluster's canonical
/// rep to its sidebar icon: resizing normalizes the size, NCC structure is brightness-
/// invariant (the darker background doesn't matter), and a tight crop keeps the differing
/// backgrounds out of the chroma term.
///
/// This works from frame 0 with no game events — unlike event-based convergence, which
/// can't be correct before the first move. Labelling is PURELY visual: the old
/// turn-correlation "ground-truth" locks were removed because they repeatedly locked the
/// wrong cluster→item and then overrode a correct visual match with it.
///
/// The labeler also builds a comparison montage (<see cref="LabelDiagnostics.ComparisonMontagePng"/>):
/// one row per cluster showing the EXACT crops it matched — the board tile, the assigned
/// sidebar icon, and the runner-up — so a mis-label can be eyeballed instead of guessed at.
/// </summary>
public sealed class SignatureLabeler
{
    // The sidebar icon sits smaller and on a darker background than a board tile. Crop its
    // centre so the (differing) background contributes as little as possible to the match.
    // Tunable against the LABELER-MEASURE accuracy in the log.
    private const double SidebarIconCropFraction = 0.72;

    // Comparison-montage geometry.
    private const int Tile = 96;
    private const int SwatchW = 30;
    private const int HeaderH = 26;

    // Cluster colours in BGR — the RGB reverse of LabelerDebugWindow.PaletteColors, so a
    // montage row's swatch matches that cluster's debug-grid swatch and on-board border.
    private static readonly Scalar[] PaletteBgr =
    {
        new(64, 64, 255),    // red
        new(64, 255, 64),    // green
        new(255, 128, 64),   // blue
        new(0, 220, 255),    // yellow
        new(220, 0, 255),    // magenta
        new(220, 220, 0),    // cyan
        new(0, 140, 255),    // orange
        new(220, 0, 180),    // purple
        new(255, 255, 255),  // white
        new(128, 128, 128),  // gray
    };

    /// <summary>
    /// Returns labels[clusterId] = sidebar item index (or -1 when unmatched). Also yields a
    /// <see cref="LabelDiagnostics"/> snapshot for the debug window.
    /// </summary>
    public int[] Label(CellClusterer clusterer, IReadOnlyList<SidebarItem> items,
        IReadOnlyList<int> clusterIds, out LabelDiagnostics? diag)
    {
        diag = null;
        IReadOnlyList<CellSignature>? reps = clusterer.CanonicalReps;
        int templateCount = items.Count;
        if (reps is null || reps.Count == 0 || templateCount == 0) return Array.Empty<int>();
        int clusterCount = reps.Count;

        int[] cellsPerCluster = new int[clusterCount];
        foreach (int cid in clusterIds)
            if (cid >= 0 && cid < clusterCount) cellsPerCluster[cid]++;

        // Signature for each sidebar icon — same metric the clusterer uses for board tiles.
        CellSignature?[] iconSig = new CellSignature?[templateCount];
        for (int t = 0; t < templateCount; t++)
        {
            OpenCvMat? icon = items[t].Icon;
            if (icon is not null && !icon.Empty())
                iconSig[t] = CellClusterer.ComputeSignature(icon, SidebarIconCropFraction);
        }

        // score[c*T + t] = -distance (the debug UI treats higher as better).
        double[] score = new double[clusterCount * templateCount];
        for (int c = 0; c < clusterCount; c++)
            for (int t = 0; t < templateCount; t++)
                score[c * templateCount + t] = iconSig[t] is null
                    ? double.NegativeInfinity
                    : -CellClusterer.Distance(reps[c], iconSig[t]!);

        int[] labels = new int[clusterCount];
        for (int i = 0; i < labels.Length; i++) labels[i] = -1;
        bool[] clusterTaken = new bool[clusterCount];
        bool[] templateTaken = new bool[templateCount];
        // No event-correlation locks: turn-correlation "ground truth" proved unreliable —
        // it repeatedly locked the WRONG cluster→item, then overrode the correct visual
        // match (forcing e.g. a non-apple cluster onto the apple icon at d=172 while the
        // real apple cluster, d=32, was shut out). Labelling is now purely visual.
        HashSet<int> locked = new();

        // Optimal assignment of all clusters to icons — a min-cost bipartite
        // matching. Greedy assignment made bad early commits: it would hand an icon to a
        // so-so cluster, then FORCE the icon's true owner onto whatever was left (a
        // d=165 "match" with the real d=37 icon showing as runner-up). Optimal weighs
        // the whole assignment at once, so each icon goes to its genuine owner.
        AssignOptimal(score, labels, clusterTaken, templateTaken,
            cellsPerCluster, clusterCount, templateCount);

        LogAssignment(score, labels, cellsPerCluster, clusterCount, templateCount, items, locked);

        byte[]? montage = BuildComparisonMontage(
            clusterer, items, labels, cellsPerCluster, score, templateCount);

        diag = new LabelDiagnostics(
            (int[])labels.Clone(), cellsPerCluster, score,
            clusterCount, templateCount,
            items.Select(it => it.Name).ToArray(), locked, montage);
        return labels;
    }

    private static readonly string LogPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pg-loot-master.log");

    /// <summary>
    /// Dump the full cluster→template distance matrix and the chosen assignment to the
    /// log, so a scrambled match can be diagnosed from the actual numbers.
    /// </summary>
    private static void LogAssignment(double[] score, int[] labels, int[] cellsPerCluster,
        int clusterCount, int templateCount, IReadOnlyList<SidebarItem> items, HashSet<int> locked)
    {
        try
        {
            System.Text.StringBuilder sb = new();
            sb.Append($"{DateTime.Now:HH:mm:ss.fff} LABELER: {clusterCount} clusters, ")
              .Append($"{templateCount} templates, locks={locked.Count}");
            for (int c = 0; c < clusterCount; c++)
            {
                if (c >= cellsPerCluster.Length || cellsPerCluster[c] == 0) continue;
                int mt = labels[c];
                sb.Append(Environment.NewLine).Append($"   c{c}");
                if (locked.Contains(c)) sb.Append("(LOCK)");
                if (mt >= 0)
                {
                    string nm = mt < items.Count ? items[mt].Name : "?";
                    sb.Append($" -> t{mt} '{nm}' d={-score[c * templateCount + mt]:F1}  argmin=");
                    int am = -1; double amd = double.MaxValue;
                    for (int t = 0; t < templateCount; t++)
                    {
                        double d = -score[c * templateCount + t];
                        if (d < amd) { amd = d; am = t; }
                    }
                    sb.Append($"t{am} d={amd:F1}");
                }
                else sb.Append(" -> (none)");
                sb.Append("  | ");
                for (int t = 0; t < templateCount; t++)
                    sb.Append($"t{t}={-score[c * templateCount + t]:F0} ");
            }
            System.IO.File.AppendAllText(LogPath, sb.ToString() + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>
    /// Min-cost bipartite matching of clusters to icons, solved with a DP over the
    /// bitmask of used icons. Each cluster takes at most one icon and vice versa. The
    /// per-unassigned penalty dwarfs any distance, so the DP matches as MANY clusters as
    /// possible first, then minimises total distance — a cluster is left unlabelled only
    /// when there are genuinely fewer free icons than clusters. Mutates
    /// <paramref name="labels"/> and <paramref name="templateTaken"/> for the non-locked
    /// clusters. Falls back to greedy only if there are too many icons to bitmask.
    /// </summary>
    private static void AssignOptimal(double[] score, int[] labels,
        bool[] clusterTaken, bool[] templateTaken, int[] cellsPerCluster,
        int clusterCount, int templateCount)
    {
        List<int> act = new();
        for (int c = 0; c < clusterCount; c++)
            if (!clusterTaken[c] && cellsPerCluster[c] > 0) act.Add(c);
        if (act.Count == 0) return;

        if (templateCount > 16)
        {
            AssignGreedy(score, labels, act, templateTaken, templateCount);
            return;
        }

        int a = act.Count, t = templateCount;
        const double inf = double.MaxValue / 8;
        const double penalty = 1_000_000.0;   // >> any real distance: maximise matches first

        double[,] cost = new double[a, t];
        for (int i = 0; i < a; i++)
            for (int j = 0; j < t; j++)
            {
                if (templateTaken[j]) { cost[i, j] = inf; continue; }
                double d = -score[act[i] * templateCount + j];   // distance
                cost[i, j] = (double.IsNaN(d) || double.IsInfinity(d)) ? inf : d;
            }

        int masks = 1 << t;
        double[,] dp = new double[a + 1, masks];
        int[,] pick = new int[a + 1, masks];
        for (int i = a - 1; i >= 0; i--)
            for (int m = 0; m < masks; m++)
            {
                double best = dp[i + 1, m] + penalty;   // leave cluster i unlabelled
                int bj = -1;
                for (int j = 0; j < t; j++)
                {
                    if ((m & (1 << j)) != 0) continue;
                    double c = cost[i, j];
                    if (c >= inf) continue;
                    double v = dp[i + 1, m | (1 << j)] + c;
                    if (v < best) { best = v; bj = j; }
                }
                dp[i, m] = best;
                pick[i, m] = bj;
            }

        int mask = 0;
        for (int i = 0; i < a; i++)
        {
            int j = pick[i, mask];
            if (j < 0) continue;
            labels[act[i]] = j;
            templateTaken[j] = true;
            mask |= 1 << j;
        }
    }

    /// <summary>Greedy fallback for the (never-in-practice) huge-icon-count case.</summary>
    private static void AssignGreedy(double[] score, int[] labels, List<int> act,
        bool[] templateTaken, int templateCount)
    {
        bool[] used = new bool[act.Count];
        while (true)
        {
            int bi = -1, bt = -1;
            double bs = double.NegativeInfinity;
            for (int i = 0; i < act.Count; i++)
            {
                if (used[i]) continue;
                for (int j = 0; j < templateCount; j++)
                {
                    if (templateTaken[j]) continue;
                    double s = score[act[i] * templateCount + j];
                    if (s > bs) { bs = s; bi = i; bt = j; }
                }
            }
            if (bi < 0) break;
            labels[act[bi]] = bt;
            templateTaken[bt] = true;
            used[bi] = true;
        }
    }

    /// <summary>
    /// One row per cluster-with-cells: [colour swatch + id] [board tile] [assigned sidebar
    /// icon] [runner-up sidebar icon]. The board tile is the inner-58% crop the clusterer
    /// fed into the signature; the icons are cropped to <see cref="SidebarIconCropFraction"/>
    /// exactly as the matcher consumed them — so what you SEE is what was compared. Each
    /// icon tile is tagged with its signature distance (lower = closer).
    /// </summary>
    private static byte[]? BuildComparisonMontage(
        CellClusterer clusterer, IReadOnlyList<SidebarItem> items,
        int[] labels, int[] cellsPerCluster, double[] score, int templateCount)
    {
        try
        {
            IReadOnlyList<OpenCvMat?>? boardCrops = clusterer.CanonicalRepCrops;

            List<int> rows = new();
            for (int c = 0; c < labels.Length; c++)
                if (cellsPerCluster[c] > 0) rows.Add(c);
            if (rows.Count == 0) return null;

            int width = SwatchW + Tile * 3;
            int height = HeaderH + rows.Count * Tile;
            using OpenCvMat montage = new(height, width, MatType.CV_8UC3, Scalar.All(28));

            Scalar headerCol = new(200, 200, 200);
            Cv2.PutText(montage, "board", new Point(SwatchW + 14, 18),
                HersheyFonts.HersheySimplex, 0.5, headerCol, 1);
            Cv2.PutText(montage, "match", new Point(SwatchW + Tile + 14, 18),
                HersheyFonts.HersheySimplex, 0.5, headerCol, 1);
            Cv2.PutText(montage, "runner", new Point(SwatchW + Tile * 2 + 10, 18),
                HersheyFonts.HersheySimplex, 0.5, headerCol, 1);

            for (int r = 0; r < rows.Count; r++)
            {
                int c = rows[r];
                int y0 = HeaderH + r * Tile;

                // Cluster colour swatch + ID — cross-references the debug grid / board border.
                Scalar col = PaletteBgr[c % PaletteBgr.Length];
                Cv2.Rectangle(montage, new Rect(0, y0, SwatchW, Tile), col, -1);
                Cv2.PutText(montage, c.ToString(), new Point(7, y0 + Tile / 2 + 6),
                    HersheyFonts.HersheySimplex, 0.7, new Scalar(0, 0, 0), 2);

                // Board tile — already the inner-58% crop; draw it as-is.
                OpenCvMat? boardCrop = boardCrops is not null && c < boardCrops.Count
                    ? boardCrops[c] : null;
                DrawCell(montage, boardCrop, SwatchW, y0, 1.0, null);

                // Assigned sidebar icon — crop to the fraction the matcher used.
                int mt = labels[c];
                OpenCvMat? matchIcon = mt >= 0 && mt < items.Count ? items[mt].Icon : null;
                double matchDist = mt >= 0 ? -score[c * templateCount + mt] : double.NaN;
                DrawCell(montage, matchIcon, SwatchW + Tile, y0, SidebarIconCropFraction, matchDist);

                // Runner-up sidebar icon — the closest template that ISN'T the assigned one.
                int ru = -1;
                double ruScore = double.NegativeInfinity;
                for (int t = 0; t < templateCount; t++)
                {
                    if (t == mt) continue;
                    double s = score[c * templateCount + t];
                    if (s > ruScore) { ruScore = s; ru = t; }
                }
                OpenCvMat? ruIcon = ru >= 0 && ru < items.Count ? items[ru].Icon : null;
                double ruDist = ru >= 0 ? -ruScore : double.NaN;
                DrawCell(montage, ruIcon, SwatchW + Tile * 2, y0, SidebarIconCropFraction, ruDist);
            }

            Cv2.ImEncode(".png", montage, out byte[] png);
            try
            {
                System.IO.File.WriteAllBytes(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pg-loot-master-labeler.png"),
                    png);
            }
            catch { }
            return png;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Draw one image into a montage tile. <paramref name="cropFraction"/> &lt; 1 crops the
    /// source centre first (used to show the sidebar icon at the matcher's crop). When
    /// <paramref name="dist"/> is set, the signature distance is stamped on the tile.
    /// </summary>
    private static void DrawCell(OpenCvMat montage, OpenCvMat? src, int x, int y,
        double cropFraction, double? dist)
    {
        Rect dst = new(x, y, Tile, Tile);
        if (src is null || src.Empty())
        {
            Cv2.Rectangle(montage, dst, new Scalar(50, 50, 50), -1);
            Cv2.PutText(montage, "?", new Point(x + Tile / 2 - 8, y + Tile / 2 + 8),
                HersheyFonts.HersheySimplex, 0.8, new Scalar(90, 90, 90), 2);
        }
        else
        {
            OpenCvMat use = src;
            OpenCvMat? cropped = null;
            if (cropFraction < 0.999)
            {
                int mx = (int)(src.Cols * (1.0 - cropFraction) / 2.0);
                int my = (int)(src.Rows * (1.0 - cropFraction) / 2.0);
                Rect inner = new(mx, my, src.Cols - 2 * mx, src.Rows - 2 * my);
                if (inner.Width > 0 && inner.Height > 0)
                {
                    cropped = new OpenCvMat(src, inner);
                    use = cropped;
                }
            }
            using (OpenCvMat resized = new())
            {
                Cv2.Resize(use, resized, new Size(Tile, Tile), 0, 0, InterpolationFlags.Area);
                resized.CopyTo(montage[dst]);
            }
            cropped?.Dispose();
        }
        Cv2.Rectangle(montage, dst, new Scalar(70, 70, 70), 1);

        if (dist is double d && !double.IsNaN(d) && !double.IsInfinity(d))
        {
            Cv2.Rectangle(montage, new Rect(x + 2, y + Tile - 19, Tile - 4, 17),
                new Scalar(0, 0, 0), -1);
            Cv2.PutText(montage, $"d={d:F1}", new Point(x + 6, y + Tile - 6),
                HersheyFonts.HersheySimplex, 0.45, new Scalar(80, 255, 255), 1);
        }
    }
}
