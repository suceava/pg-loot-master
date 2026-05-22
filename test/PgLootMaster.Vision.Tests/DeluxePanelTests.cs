using OpenCvSharp;

namespace PgLootMaster.Vision.Tests;

/// <summary>
/// Verifies the Lootmaster Deluxe variant works through the existing detection pipeline
/// with no code changes — only the new <c>panel-title-deluxe.png</c> template added.
/// Runs against a real full-screen fixture capture of the Deluxe minigame.
/// </summary>
public class DeluxePanelTests
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
    public void Deluxe_panel_is_located_by_its_own_template()
    {
        using Mat frame = Cv2.ImRead(Fixture("deluxe-panel-fixture.png"), ImreadModes.Color);
        Assert.False(frame.Empty());

        using var locator = new PanelLocator(TemplateDir);
        PanelLocation? loc = locator.TryLocate(frame);

        Assert.NotNull(loc);
        Assert.Equal("panel-title-deluxe", loc!.Value.TemplateName);
        Assert.True(loc.Value.Confidence > 0.9, $"low confidence: {loc.Value.Confidence:F3}");
    }

    [Fact]
    public void Deluxe_board_extracts_a_full_7x7_grid()
    {
        using Mat frame = Cv2.ImRead(Fixture("deluxe-panel-fixture.png"), ImreadModes.Color);
        using var locator = new PanelLocator(TemplateDir);
        PanelLocation? loc = locator.TryLocate(frame);
        Assert.NotNull(loc);

        IReadOnlyList<Rect> cells = new BoardExtractor().TryDetectCells(frame, loc!.Value.TitleBar);

        Assert.Equal(BoardExtractor.GridDim * BoardExtractor.GridDim, cells.Count);
    }

    [Fact]
    public void Adding_deluxe_template_does_not_break_lootmaster_detection()
    {
        using Mat frame = Cv2.ImRead(Fixture("lootmaster-panel-fixture.png"), ImreadModes.Color);
        using var locator = new PanelLocator(TemplateDir);
        PanelLocation? loc = locator.TryLocate(frame);

        Assert.NotNull(loc);
        Assert.Equal("panel-title", loc!.Value.TemplateName);
    }
}
