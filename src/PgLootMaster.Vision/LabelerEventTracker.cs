namespace PgLootMaster.Vision;

/// <summary>
/// Phase 3 — event-based ground-truth labeling. Each frame, watches the diff between
/// the previous frame's sidebar item counts and cluster cell counts. When EXACTLY ONE
/// item's CaptureCount went up by N AND EXACTLY ONE cluster's cell count dropped by N,
/// we have ground truth: that cluster IS that item. Lock the mapping for the rest of
/// the session.
///
/// Visual matching (Phases 1–2) is the prior; this is the observation. Locked mappings
/// beat any visual score — when the user matches 5 cells of an item and that item's
/// count ticks up by 5, no visual feature can argue with that.
///
/// Limitations of the MVP:
/// - Only ONE-item / ONE-cluster correlations get locked. Cascades that match multiple
///   items in the same turn (rare but possible) leave ambiguous counts and we don't
///   learn anything from those frames.
/// - Cluster IDs aren't stable across canonical re-captures by CellClusterer. After a
///   re-capture, learned mappings may be wrong. The "Recompute clusters" button in
///   Settings calls Reset() which also clears these.
/// - Items captured (transitioned to ✓) cause their cells to be replaced. We invalidate
///   any learned mapping whose item just got captured, since the cluster ID now
///   represents a different item.
/// </summary>
public sealed class LabelerEventTracker
{
    private readonly Dictionary<string, int> _prevItemCounts = new();
    private readonly Dictionary<string, bool> _prevItemCaptured = new();
    private Dictionary<int, int> _prevClusterCellCounts = new();

    private readonly Dictionary<int, string> _learned = new();
    public IReadOnlyDictionary<int, string> Learned => _learned;

    /// <summary>
    /// Drives one frame's worth of correlation. Call after the clusterer has produced
    /// cluster IDs and after the sidebar OCR has updated <paramref name="sidebar"/>.
    /// </summary>
    public void OnFrame(IReadOnlyList<SidebarItem> sidebar, IReadOnlyList<int> clusterIds)
    {
        if (sidebar is null || clusterIds is null) return;

        // Count cells per cluster ID this frame.
        Dictionary<int, int> currentClusterCounts = new();
        for (int i = 0; i < clusterIds.Count; i++)
        {
            int cid = clusterIds[i];
            if (cid < 0) continue;
            currentClusterCounts.TryGetValue(cid, out int c);
            currentClusterCounts[cid] = c + 1;
        }

        // Diff item counts → list of (name, +delta) for those that went UP.
        List<(string name, int delta)> itemDeltas = new();
        foreach (SidebarItem it in sidebar)
        {
            if (string.IsNullOrEmpty(it.Name)) continue;
            if (it.CaptureCount is not int curr) continue;
            if (_prevItemCounts.TryGetValue(it.Name, out int prev) && curr > prev)
            {
                itemDeltas.Add((it.Name, curr - prev));
            }
        }

        // Diff cluster cell counts → list of (clusterId, -drop) for those that lost cells.
        List<(int cid, int drop)> clusterDrops = new();
        foreach (KeyValuePair<int, int> kv in _prevClusterCellCounts)
        {
            int curr = currentClusterCounts.GetValueOrDefault(kv.Key, 0);
            int drop = kv.Value - curr;
            if (drop > 0) clusterDrops.Add((kv.Key, drop));
        }

        // Detect captures (Captured: false → true). Invalidate any learned mapping whose
        // item just got captured — the cluster cells are about to be refilled with a
        // different item and the mapping becomes stale.
        List<string> justCaptured = new();
        foreach (SidebarItem it in sidebar)
        {
            if (string.IsNullOrEmpty(it.Name)) continue;
            bool prevCaptured = _prevItemCaptured.GetValueOrDefault(it.Name, false);
            if (it.Captured && !prevCaptured)
            {
                justCaptured.Add(it.Name);
            }
        }
        if (justCaptured.Count > 0)
        {
            // Drop any learned mapping pointing at a just-captured item.
            List<int> stale = new();
            foreach (KeyValuePair<int, string> kv in _learned)
            {
                if (justCaptured.Contains(kv.Value)) stale.Add(kv.Key);
            }
            foreach (int cid in stale) _learned.Remove(cid);
        }

        // MVP correlation: exactly one item ticked up by N AND exactly one cluster lost N cells.
        // That's ground truth → lock the mapping.
        if (itemDeltas.Count == 1 && clusterDrops.Count == 1
            && itemDeltas[0].delta == clusterDrops[0].drop)
        {
            string name = itemDeltas[0].name;
            int cid = clusterDrops[0].cid;
            // Don't overwrite if a different item is already locked to this cluster
            // (shouldn't happen with consistent gameplay; defensive).
            _learned[cid] = name;
        }
        // Multi-event cascades and ambiguous deltas are intentionally skipped in MVP.

        // Snapshot for next frame.
        _prevClusterCellCounts = currentClusterCounts;
        _prevItemCounts.Clear();
        _prevItemCaptured.Clear();
        foreach (SidebarItem it in sidebar)
        {
            if (string.IsNullOrEmpty(it.Name)) continue;
            if (it.CaptureCount is int c) _prevItemCounts[it.Name] = c;
            _prevItemCaptured[it.Name] = it.Captured;
        }
    }

    public void ResetForNewGame()
    {
        _prevItemCounts.Clear();
        _prevItemCaptured.Clear();
        _prevClusterCellCounts.Clear();
        _learned.Clear();
    }
}
