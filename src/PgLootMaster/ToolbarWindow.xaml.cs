using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PgLootMaster.Vision;

namespace PgLootMaster;

public partial class ToolbarWindow : Window
{
    private SettingsWindow? _settingsWindow;
    private bool _suppressTargetChanged;

    public ToolbarWindow()
    {
        InitializeComponent();
        Left = OverlaySettings.Instance.ToolbarLeft;
        Top = OverlaySettings.Instance.ToolbarTop;
        // Start with just the placeholder; the OverlayWindow will push updates as sidebar
        // items get detected.
        RefreshTargetList(System.Array.Empty<SidebarItem>());
    }

    /// <summary>
    /// Refreshes the target dropdown with the current uncaptured sidebar items.
    /// Preserves the selected target by name across calls (cluster IDs shift each round).
    /// Call from the overlay's frame pipeline whenever the sidebar items list changes.
    /// </summary>
    public void RefreshTargetList(System.Collections.Generic.IReadOnlyList<SidebarItem> items)
    {
        _suppressTargetChanged = true;
        try
        {
            string? currentTarget = OverlaySettings.Instance.TargetItemName;
            TargetComboBox.Items.Clear();
            TargetComboBox.Items.Add("(no target)");
            int selectIndex = 0;
            foreach (SidebarItem it in items)
            {
                if (it.Captured) continue;
                if (string.IsNullOrEmpty(it.Name)) continue;
                int idx = TargetComboBox.Items.Add(it.Name);
                if (it.Name == currentTarget) selectIndex = idx;
            }
            TargetComboBox.SelectedIndex = selectIndex;
        }
        finally
        {
            _suppressTargetChanged = false;
        }
    }

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTargetChanged) return;
        if (TargetComboBox.SelectedIndex <= 0)
        {
            OverlaySettings.Instance.TargetItemName = null;
        }
        else
        {
            OverlaySettings.Instance.TargetItemName = TargetComboBox.SelectedItem?.ToString();
        }
    }

    private void OnDragMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
            OverlaySettings.Instance.ToolbarLeft = Left;
            OverlaySettings.Instance.ToolbarTop = Top;
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow { Owner = this };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.Activate();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}
