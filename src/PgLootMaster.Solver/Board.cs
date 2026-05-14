namespace PgLootMaster.Solver;

public readonly record struct Tile(int TypeId)
{
    public static readonly Tile Unknown = new(-1);
    public bool IsKnown => TypeId >= 0;
}

public sealed class Board
{
    public const int Dim = 7;
    private readonly Tile[,] _cells;

    public Board()
    {
        _cells = new Tile[Dim, Dim];
        for (int r = 0; r < Dim; r++)
            for (int c = 0; c < Dim; c++)
                _cells[r, c] = Tile.Unknown;
    }

    public Board(int[,] typeIds)
    {
        if (typeIds.GetLength(0) != Dim || typeIds.GetLength(1) != Dim)
            throw new ArgumentException($"Board must be {Dim}x{Dim}");
        _cells = new Tile[Dim, Dim];
        for (int r = 0; r < Dim; r++)
            for (int c = 0; c < Dim; c++)
                _cells[r, c] = new Tile(typeIds[r, c]);
    }

    private Board(Tile[,] cells)
    {
        _cells = cells;
    }

    public Tile this[int row, int col]
    {
        get => _cells[row, col];
        set => _cells[row, col] = value;
    }

    public Board Clone()
    {
        Tile[,] copy = new Tile[Dim, Dim];
        Array.Copy(_cells, copy, _cells.Length);
        return new Board(copy);
    }

    public Board WithSwap(Swap swap)
    {
        Board next = Clone();
        Tile a = next[swap.Row1, swap.Col1];
        next[swap.Row1, swap.Col1] = next[swap.Row2, swap.Col2];
        next[swap.Row2, swap.Col2] = a;
        return next;
    }
}

public readonly record struct Swap(int Row1, int Col1, int Row2, int Col2)
{
    public bool IsAdjacent =>
        (Math.Abs(Row1 - Row2) == 1 && Col1 == Col2)
        || (Math.Abs(Col1 - Col2) == 1 && Row1 == Row2);

    public static IEnumerable<Swap> AllAdjacent()
    {
        for (int r = 0; r < Board.Dim; r++)
        {
            for (int c = 0; c < Board.Dim; c++)
            {
                if (c + 1 < Board.Dim) yield return new Swap(r, c, r, c + 1);
                if (r + 1 < Board.Dim) yield return new Swap(r, c, r + 1, c);
            }
        }
    }
}
