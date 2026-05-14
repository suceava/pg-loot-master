namespace PgLootMaster.Solver;

public readonly record struct Cell(int Row, int Col);

public sealed class Match
{
    public Tile Tile { get; }
    public IReadOnlyList<Cell> Cells { get; }
    public int Length => Cells.Count;

    public Match(Tile tile, IReadOnlyList<Cell> cells)
    {
        Tile = tile;
        Cells = cells;
    }
}

public static class MatchFinder
{
    public static IReadOnlyList<Match> Find(Board board)
    {
        List<Match> matches = new();
        bool[,] matched = new bool[Board.Dim, Board.Dim];

        for (int r = 0; r < Board.Dim; r++)
        {
            int c = 0;
            while (c < Board.Dim)
            {
                Tile t = board[r, c];
                if (!t.IsKnown) { c++; continue; }
                int runEnd = c + 1;
                while (runEnd < Board.Dim && board[r, runEnd] == t) runEnd++;
                int len = runEnd - c;
                if (len >= 3)
                {
                    List<Cell> cells = new(len);
                    for (int k = c; k < runEnd; k++)
                    {
                        cells.Add(new Cell(r, k));
                        matched[r, k] = true;
                    }
                    matches.Add(new Match(t, cells));
                }
                c = runEnd;
            }
        }

        for (int c = 0; c < Board.Dim; c++)
        {
            int r = 0;
            while (r < Board.Dim)
            {
                Tile t = board[r, c];
                if (!t.IsKnown) { r++; continue; }
                int runEnd = r + 1;
                while (runEnd < Board.Dim && board[runEnd, c] == t) runEnd++;
                int len = runEnd - r;
                if (len >= 3)
                {
                    List<Cell> cells = new(len);
                    for (int k = r; k < runEnd; k++)
                    {
                        cells.Add(new Cell(k, c));
                        matched[k, c] = true;
                    }
                    matches.Add(new Match(t, cells));
                }
                r = runEnd;
            }
        }

        return matches;
    }

    public static HashSet<Cell> CollectMatchedCells(IReadOnlyList<Match> matches)
    {
        HashSet<Cell> set = new();
        foreach (Match m in matches)
        {
            foreach (Cell cell in m.Cells) set.Add(cell);
        }
        return set;
    }
}
