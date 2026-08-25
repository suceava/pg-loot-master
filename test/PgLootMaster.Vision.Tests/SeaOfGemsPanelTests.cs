using OpenCvSharp;

namespace PgLootMaster.Vision.Tests;

/// <summary>
/// Verifies the Sea of Gems variant works through the existing detection pipeline with no
/// code changes beyond the new <c>panel-title-seaofgems.png</c> template (plus the display
/// name in OverlayWindow.MapTemplateToStyle). Runs against a real capture of the Sea of
/// Gems minigame at the Red Wing Casino vendor.
/// </summary>
public class SeaOfGemsPanelTests
{
    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PgLootMaster.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("Could not locate repo root (PgLootMaster.sln).");
    }

    private static string TemplateDir => Path.Combine(RepoRoot(), "samples", "templates");
    private static string Fixture(string name) =>
        Path.Combine(RepoRoot(), "samples", "screenshots", name);

    [Fact]
    public void SeaOfGems_panel_is_located_by_its_own_template()
    {
        using Mat frame = Cv2.ImRead(Fixture("seaofgems-panel-fixture.png"), ImreadModes.Color);
        Assert.False(frame.Empty());

        using var locator = new PanelLocator(TemplateDir);
        PanelLocation? loc = locator.TryLocate(frame);

        Assert.NotNull(loc);
        Assert.Equal("panel-title-seaofgems", loc!.Value.TemplateName);
        Assert.True(loc.Value.Confidence > 0.9, $"low confidence: {loc.Value.Confidence:F3}");
    }

    [Fact]
    public void SeaOfGems_board_extracts_a_full_7x7_grid()
    {
        using Mat frame = Cv2.ImRead(Fixture("seaofgems-panel-fixture.png"), ImreadModes.Color);
        using var locator = new PanelLocator(TemplateDir);
        PanelLocation? loc = locator.TryLocate(frame);
        Assert.NotNull(loc);

        IReadOnlyList<Rect> cells = new BoardExtractor().TryDetectCells(frame, loc!.Value.TitleBar);

        Assert.Equal(BoardExtractor.GridDim * BoardExtractor.GridDim, cells.Count);
    }

    [Fact]
    public void SeaOfGems_template_shares_the_lootmaster_left_and_top_margin()
    {
        // Every panel offset (sidebar, board, score region) is measured from the matched
        // template's top-left corner, so all panel-title*.png must crop the title text at
        // the same margin or the sidebar crop shifts and clips the OCR'd labels.
        (int lx, int ly) = FirstTextPixel(Path.Combine(TemplateDir, "panel-title.png"));
        (int sx, int sy) = FirstTextPixel(Path.Combine(TemplateDir, "panel-title-seaofgems.png"));
        Assert.True(Math.Abs(lx - sx) <= 4,
            $"template left-margin mismatch: panel-title.png text@{lx}, panel-title-seaofgems.png text@{sx}");
        Assert.True(Math.Abs(ly - sy) <= 4,
            $"template top-margin mismatch: panel-title.png text@{ly}, panel-title-seaofgems.png text@{sy}");
    }

    /// <summary>Top-left corner of the gold title text (R high, R≫B).</summary>
    private static (int X, int Y) FirstTextPixel(string templatePath)
    {
        using Mat t = Cv2.ImRead(templatePath, ImreadModes.Color);
        int minX = int.MaxValue, minY = int.MaxValue;
        for (int x = 0; x < t.Width; x++)
            for (int y = 0; y < t.Height; y++)
            {
                Vec3b p = t.At<Vec3b>(y, x);   // BGR
                if (p.Item2 > 120 && p.Item1 > 90 && p.Item2 > p.Item0 + 30)
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                }
            }
        return (minX, minY);
    }

    [Fact]
    public void SeaOfGems_sidebar_reads_the_four_starting_items()
    {
        using Mat frame = Cv2.ImRead(Fixture("seaofgems-panel-fixture.png"), ImreadModes.Color);
        using var locator = new PanelLocator(TemplateDir);
        PanelLocation? loc = locator.TryLocate(frame);
        Assert.NotNull(loc);

        IReadOnlyList<SidebarItem> items = new SidebarReader().ReadItems(frame, loc!.Value.TitleBar);

        // The board starts with 4 item types, so a fresh game shows exactly 4 sidebar rows.
        Assert.Equal(4, items.Count);
        Assert.All(items, i => Assert.False(i.Icon.Empty()));
    }

    [Fact]
    public void Embedded_templates_include_all_four_variants()
    {
        // The single-exe build loads templates from manifest resources, not samples/templates/
        // — the .csproj glob has to have picked the new file up or the shipped exe never sees
        // Sea of Gems even though the on-disk tests above pass.
        using var embedded = new PanelLocator();
        Assert.Contains("panel-title", embedded.TemplateNames);
        Assert.Contains("panel-title-cashfall", embedded.TemplateNames);
        Assert.Contains("panel-title-deluxe", embedded.TemplateNames);
        Assert.Contains("panel-title-seaofgems", embedded.TemplateNames);
    }

    [Fact]
    public void Adding_seaofgems_template_does_not_break_the_other_variants()
    {
        using var locator = new PanelLocator(TemplateDir);

        using Mat lootmaster = Cv2.ImRead(Fixture("lootmaster-panel-fixture.png"), ImreadModes.Color);
        Assert.Equal("panel-title", locator.TryLocate(lootmaster)?.TemplateName);

        using Mat deluxe = Cv2.ImRead(Fixture("deluxe-panel-fixture.png"), ImreadModes.Color);
        Assert.Equal("panel-title-deluxe", locator.TryLocate(deluxe)?.TemplateName);
    }
}
