using System.Windows;

namespace PgLootMaster;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        ShowBoardOverlayCheckBox.IsChecked = OverlaySettings.Instance.ShowBoardOverlay;
        ShowDebugTextWindowCheckBox.IsChecked = OverlaySettings.Instance.ShowDebugTextWindow;
    }

    private void OnShowBoardOverlayChanged(object sender, RoutedEventArgs e)
    {
        OverlaySettings.Instance.ShowBoardOverlay = ShowBoardOverlayCheckBox.IsChecked == true;
    }

    private void OnShowDebugTextWindowChanged(object sender, RoutedEventArgs e)
    {
        OverlaySettings.Instance.ShowDebugTextWindow = ShowDebugTextWindowCheckBox.IsChecked == true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
