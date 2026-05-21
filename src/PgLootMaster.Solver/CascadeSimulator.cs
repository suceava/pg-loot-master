namespace PgLootMaster.Solver;

public sealed class CascadeResult
{
    public bool SwapLegal { get; }
    public IReadOnlyList<IReadOnlyList<Match>> Steps { get; }
    public Board? FinalBoard { get; }
    public int TotalCellsMatched { get; }
    public int MaxRunLength { get; }

    public CascadeResult(bool swapLegal, IReadOnlyList<IReadOnlyList<Match>> steps, Board? finalBoard)
    {
        SwapLegal = swapLegal;
        Steps = steps;
        FinalBoard = finalBoard;
        int total = 0;
        int maxLen = 0;
        foreach (IReadOnlyList<Match> step in steps)
        {
            foreach (Match m in step)
            {
                total += m.Length;
                if (m.Length > maxLen) maxLen = m.Length;
            }
        }
        TotalCellsMatched = total;
        MaxRunLength = maxLen;
    }

    public static CascadeResult Illegal { get; } = new(false, Array.Empty<IReadOnlyList<Match>>(), null);
}

public static class CascadeSimulator
{
    public static CascadeResult Resolve(Board board, Swap swap)
    {
        if (!swap.IsAdjacent) return CascadeResult.Illegal;

        // No-op swap: same value on both cells. PG won't accept this either, but our
        // mis-merged clusterer can produce adjacent cells with the same cluster ID even
        // when they're visually different items. Filter out so we don't credit fake
        // pre-existing matches that "involve" the swap trivially.
        if (board[swap.Row1, swap.Col1] == board[swap.Row2, swap.Col2])
            return CascadeResult.Illegal;

        Board working = board.WithSwap(swap);
        IReadOnlyList<Match> allInitial = MatchFinder.Find(working);

        // PG-correct legality: the swap is legal only if it CREATES a 3+ match
        // involving at least one of the two swapped cells. Pre-existing matches on
        // the board (which only exist in our view when the clusterer has mis-merged
        // two visually-distinct items into a single cluster ID) DO NOT count — PG
        // would have auto-cleared them before the player saw the board.
        //
        // Without this filter the simulator returns swapLegal=true for swaps that
        // leave a pre-existing fake match in place, even when the swapped cells
        // themselves don't participate in any real match.
        List<Match> swapCreated = new();
        foreach (Match m in allInitial)
        {
            bool involvesSwap = false;
            foreach (Cell c in m.Cells)
            {
                if ((c.Row == swap.Row1 && c.Col == swap.Col1) ||
                    (c.Row == swap.Row2 && c.Col == swap.Col2))
                {
                    involvesSwap = true;
                    break;
                }
            }
            if (involvesSwap) swapCreated.Add(m);
        }
        if (swapCreated.Count == 0) return CascadeResult.Illegal;

        List<IReadOnlyList<Match>> steps = new() { swapCreated };
        ApplyMatchesAndGravity(working, swapCreated);

        while (true)
        {
            // Cascade-step matches are valid regardless of swap involvement — gravity
            // pulls new tiles into position and they can form natural runs.
            IReadOnlyList<Match> next = MatchFinder.Find(working);
            if (next.Count == 0) break;
            steps.Add(next);
            ApplyMatchesAndGravity(working, next);
        }

        return new CascadeResult(swapLegal: true, steps, working);
    }

    private static void ApplyMatchesAndGravity(Board board, IReadOnlyList<Match> matches)
    {
        HashSet<Cell> matched = MatchFinder.CollectMatchedCells(matches);
        foreach (Cell cell in matched)
        {
            board[cell.Row, cell.Col] = Tile.Unknown;
        }

        for (int c = 0; c < Board.Dim; c++)
        {
            int writeRow = Board.Dim - 1;
            for (int r = Board.Dim - 1; r >= 0; r--)
            {
                Tile t = board[r, c];
                if (t.IsKnown)
                {
                    if (writeRow != r)
                    {
                        board[writeRow, c] = t;
                        board[r, c] = Tile.Unknown;
                    }
                    writeRow--;
                }
            }
        }
    }
}
