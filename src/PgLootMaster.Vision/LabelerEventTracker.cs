namespace PgLootMaster.Vision;

/// <summary>
/// Phase 3 — event-based ground-truth labeling. Holds cluster→item-name mappings learned
/// from gameplay.
///
/// The correlation that PRODUCES a mapping lives in OverlayWindow (`CorrelateTurn`),
/// which has the inputs it needs: the pre-move board, the swap the player made (the user
/// always plays the recommended swap), and the sidebar capture-count diff. When a swap
/// matches an M-cell run of cluster C and exactly one item's count rises by M, cluster C
/// IS that item — unambiguous ground truth. This class just stores those mappings and
/// exposes them to <see cref="ItemMatcher"/>, where a locked mapping beats any visual
/// score.
///
/// (The earlier MVP correlated by diffing per-frame cluster cell-counts. That silently
/// stopped working once CellClusterer began freezing IDs and committing once per cascade —
/// the only diff it could ever see was a whole reshuffled board. Correlation is now
/// turn-based instead.)
/// </summary>
public sealed class LabelerEventTracker
{
    private readonly Dictionary<int, string> _learned = new();

    /// <summary>Ground-truth cluster-ID → item-name mappings learned so far this game.</summary>
    public IReadOnlyDictionary<int, string> Learned => _learned;

    /// <summary>Record a ground-truth mapping established by turn correlation.</summary>
    public void Learn(int clusterId, string itemName)
    {
        if (clusterId < 0 || string.IsNullOrEmpty(itemName)) return;
        _learned[clusterId] = itemName;
    }

    public void ResetForNewGame() => _learned.Clear();
}
