using PgLootMaster.Solver;

namespace PgLootMaster.Solver.Tests;

public class SolverTests
{
    [Fact]
    public void MatchFinder_FindsHorizontalThree()
    {
        int[,] grid = UniqueFilled();
        grid[0, 0] = 1; grid[0, 1] = 1; grid[0, 2] = 1;
        Board board = new(grid);

        IReadOnlyList<Match> matches = MatchFinder.Find(board);

        Assert.Single(matches);
        Assert.Equal(3, matches[0].Length);
        Assert.Equal(new Tile(1), matches[0].Tile);
    }

    [Fact]
    public void MatchFinder_FindsVerticalFour()
    {
        int[,] grid = UniqueFilled();
        grid[0, 3] = 2; grid[1, 3] = 2; grid[2, 3] = 2; grid[3, 3] = 2;
        Board board = new(grid);

        IReadOnlyList<Match> matches = MatchFinder.Find(board);

        Assert.Single(matches);
        Assert.Equal(4, matches[0].Length);
    }

    [Fact]
    public void MatchFinder_IgnoresUnknownTiles()
    {
        int[,] grid = Filled(filler: -1);
        grid[0, 0] = 1; grid[0, 1] = 1; grid[0, 2] = 1;
        Board board = new(grid);

        Assert.Single(MatchFinder.Find(board));
    }

    [Fact]
    public void CascadeSimulator_IllegalSwapWithNoMatch()
    {
        int[,] grid = AlternatingChecker();
        Board board = new(grid);

        CascadeResult result = CascadeSimulator.Resolve(board, new Swap(0, 0, 0, 1));

        Assert.False(result.SwapLegal);
    }

    [Fact]
    public void CascadeSimulator_LegalSwapProducesThreeMatch()
    {
        int[,] grid = UniqueFilled();
        grid[0, 0] = 2; grid[0, 1] = 1; grid[0, 2] = 2; grid[0, 3] = 2;
        Board board = new(grid);

        CascadeResult result = CascadeSimulator.Resolve(board, new Swap(0, 0, 0, 1));

        Assert.True(result.SwapLegal);
        Assert.Equal(3, result.TotalCellsMatched);
        Assert.Equal(3, result.MaxRunLength);
    }

    [Fact]
    public void Solver_PicksFourMatchOverThreeMatch()
    {
        int[,] grid = UniqueFilled();
        grid[0, 0] = 2; grid[0, 1] = 1; grid[0, 2] = 2; grid[0, 3] = 2;
        grid[2, 0] = 3; grid[2, 1] = 3; grid[2, 2] = 99; grid[2, 3] = 3; grid[2, 4] = 88;
        grid[3, 2] = 3;
        Board board = new(grid);

        SwapRecommendation? rec = Solver.FindBestSwap(board);

        Assert.NotNull(rec);
        Assert.Equal(4, rec.Cascade.MaxRunLength);
        Assert.Equal(new Swap(2, 2, 3, 2), rec.Swap);
    }

    private static int[,] Filled(int filler)
    {
        int[,] g = new int[7, 7];
        for (int r = 0; r < 7; r++)
            for (int c = 0; c < 7; c++)
                g[r, c] = filler;
        return g;
    }

    private static int[,] UniqueFilled()
    {
        int[,] g = new int[7, 7];
        for (int r = 0; r < 7; r++)
            for (int c = 0; c < 7; c++)
                g[r, c] = 100 + r * 7 + c;
        return g;
    }

    private static int[,] AlternatingChecker()
    {
        int[,] g = new int[7, 7];
        for (int r = 0; r < 7; r++)
            for (int c = 0; c < 7; c++)
                g[r, c] = (r + c) % 2;
        return g;
    }
}
