namespace PgLootMaster.Solver;

public sealed record SwapRecommendation(
    Swap Swap,
    double Score,
    double ImmediateScore,
    double LookaheadScore,
    CascadeResult Cascade);

public static class Solver
{
    private const double VerticalBonus = 1.2;
    private const double BottomBonusPerRow = 0.5;
    private const double LTShapeBonus = 12.0;
    private const double LookaheadDiscount = 0.3;
    private const double FourMatchTurnBonus = 200.0;
    private const double FiveMatchTurnBonus = 500.0;

    public static SwapRecommendation? FindBestSwap(Board board, out List<SwapRecommendation> topCandidates)
    {
        List<SwapRecommendation> all = new();
        foreach (Swap swap in Swap.AllAdjacent())
        {
            CascadeResult result = CascadeSimulator.Resolve(board, swap);
            if (!result.SwapLegal) continue;

            double immediateScore = ScoreCascade(result);

            double lookaheadScore = 0;
            if (result.FinalBoard is not null)
            {
                foreach (Swap nextSwap in Swap.AllAdjacent())
                {
                    CascadeResult nextResult = CascadeSimulator.Resolve(result.FinalBoard, nextSwap);
                    if (!nextResult.SwapLegal) continue;
                    double nextScore = ScoreCascade(nextResult);
                    if (nextScore > lookaheadScore) lookaheadScore = nextScore;
                }
            }

            double totalScore = immediateScore + LookaheadDiscount * lookaheadScore;
            all.Add(new SwapRecommendation(swap, totalScore, immediateScore, lookaheadScore, result));
        }
        all.Sort((a, b) => b.Score.CompareTo(a.Score));
        topCandidates = all.Take(15).ToList();
        return topCandidates.Count > 0 ? topCandidates[0] : null;
    }

    public static SwapRecommendation? FindBestSwap(Board board) => FindBestSwap(board, out _);

    public static double ScoreCascade(CascadeResult result)
    {
        double score = 0;
        bool firstStepHasFour = false;
        bool firstStepHasFive = false;
        for (int stepIdx = 0; stepIdx < result.Steps.Count; stepIdx++)
        {
            IReadOnlyList<Match> step = result.Steps[stepIdx];
            double stepWeight = stepIdx == 0 ? 1.0 : 0.3 * Math.Pow(0.7, stepIdx - 1);
            foreach (Match m in step)
            {
                score += ScoreSingleMatch(m) * stepWeight;
                if (stepIdx == 0)
                {
                    if (m.Length >= 5) firstStepHasFive = true;
                    else if (m.Length >= 4) firstStepHasFour = true;
                }
            }
            score += CountLTOverlapCells(step) * LTShapeBonus * stepWeight;
        }
        if (firstStepHasFive) score += FiveMatchTurnBonus;
        else if (firstStepHasFour) score += FourMatchTurnBonus;
        return score;
    }

    private static double ScoreSingleMatch(Match m)
    {
        double baseScore = m.Length switch
        {
            3 => 3,
            4 => 50,
            5 => 150,
            _ => m.Length * 30,
        };

        double bottomBonus = 0;
        foreach (Cell cell in m.Cells)
        {
            bottomBonus += cell.Row * BottomBonusPerRow;
        }

        bool isVertical = IsVerticalMatch(m);
        double multiplier = isVertical ? VerticalBonus : 1.0;

        return (baseScore + bottomBonus) * multiplier;
    }

    private static bool IsVerticalMatch(Match m)
    {
        if (m.Cells.Count < 2) return false;
        int col = m.Cells[0].Col;
        for (int i = 1; i < m.Cells.Count; i++)
        {
            if (m.Cells[i].Col != col) return false;
        }
        return true;
    }

    private static int CountLTOverlapCells(IReadOnlyList<Match> matchesInStep)
    {
        HashSet<Cell> seen = new();
        HashSet<Cell> overlapping = new();
        foreach (Match m in matchesInStep)
        {
            foreach (Cell cell in m.Cells)
            {
                if (!seen.Add(cell))
                {
                    overlapping.Add(cell);
                }
            }
        }
        return overlapping.Count;
    }
}
