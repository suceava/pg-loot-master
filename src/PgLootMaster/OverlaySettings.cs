using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PgLootMaster;

public sealed class OverlaySettings : INotifyPropertyChanged
{
    public static OverlaySettings Instance { get; } = new();

    private bool _showBoardOverlay = true;
    public bool ShowBoardOverlay
    {
        get => _showBoardOverlay;
        set => Set(ref _showBoardOverlay, value);
    }

    private bool _showDebugTextWindow = true;
    public bool ShowDebugTextWindow
    {
        get => _showDebugTextWindow;
        set => Set(ref _showDebugTextWindow, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
