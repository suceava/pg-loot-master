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

        Board working = board.WithSwap(swap);
        IReadOnlyList<Match> initial = MatchFinder.Find(working);
        if (initial.Count == 0) return CascadeResult.Illegal;

        List<IReadOnlyList<Match>> steps = new() { initial };
        ApplyMatchesAndGravity(working, initial);

        while (true)
        {
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
