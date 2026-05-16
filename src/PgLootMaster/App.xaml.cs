using System.Windows;

namespace PgLootMaster;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        OverlayWindow overlay = new();
        overlay.Show();

        ToolbarWindow toolbar = new();
        toolbar.Show();

        // Hook the overlay's sidebar-items callback to the toolbar's dropdown so target
        // choices stay in sync with the current sidebar state.
        overlay.OnSidebarItemsChanged = toolbar.RefreshTargetList;

        // Closing either the toolbar or the overlay shuts the app down.
        toolbar.Closed += (_, _) => Shutdown();
        overlay.Closed += (_, _) => Shutdown();
    }
}
