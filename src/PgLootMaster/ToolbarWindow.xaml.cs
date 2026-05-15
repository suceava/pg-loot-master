using System.Windows;
using System.Windows.Input;

namespace PgLootMaster;

public partial class ToolbarWindow : Window
{
    private SettingsWindow? _settingsWindow;

    public ToolbarWindow()
    {
        InitializeComponent();
        Left = OverlaySettings.Instance.ToolbarLeft;
        Top = OverlaySettings.Instance.ToolbarTop;
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
