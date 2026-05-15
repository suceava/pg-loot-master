using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PgLootMaster;

public sealed class OverlaySettings : INotifyPropertyChanged
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PgLootMaster");
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public static OverlaySettings Instance { get; } = Load();

    private bool _suppressSave;

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

    private double _toolbarLeft = 40;
    public double ToolbarLeft
    {
        get => _toolbarLeft;
        set => Set(ref _toolbarLeft, value);
    }

    private double _toolbarTop = 40;
    public double ToolbarTop
    {
        get => _toolbarTop;
        set => Set(ref _toolbarTop, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (!_suppressSave) Save();
    }

    private static OverlaySettings Load()
    {
        OverlaySettings settings = new() { _suppressSave = true };
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                OverlaySettings? loaded = JsonSerializer.Deserialize<OverlaySettings>(json);
                if (loaded is not null)
                {
                    settings.ShowBoardOverlay = loaded.ShowBoardOverlay;
                    settings.ShowDebugTextWindow = loaded.ShowDebugTextWindow;
                    settings.ToolbarLeft = loaded.ToolbarLeft;
                    settings.ToolbarTop = loaded.ToolbarTop;
                }
            }
        }
        catch
        {
            // Fall back to defaults on any read/parse failure — don't crash startup.
        }
        settings._suppressSave = false;
        return settings;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Swallow — losing persistence is preferable to crashing on a write failure.
        }
    }
}
