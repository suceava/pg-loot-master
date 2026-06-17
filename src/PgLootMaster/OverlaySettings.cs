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

    private bool _showBoardOverlay = false;
    public bool ShowBoardOverlay
    {
        get => _showBoardOverlay;
        set => Set(ref _showBoardOverlay, value);
    }

    private bool _showDebugTextWindow = false;
    public bool ShowDebugTextWindow
    {
        get => _showDebugTextWindow;
        set => Set(ref _showDebugTextWindow, value);
    }

    // Toggle for the pink swap-tile highlight drawn on the suggested swap. Default on.
    // Players who want to play unaided (but still have games tracked in history) can flip
    // this off without disabling the rest of the overlay.
    private bool _showSwapHighlight = true;
    public bool ShowSwapHighlight
    {
        get => _showSwapHighlight;
        set => Set(ref _showSwapHighlight, value);
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

    // Name of the item the user has chosen to optimize captures for. The solver applies a
    // 5× score multiplier to matches of this item and penalizes swaps that would capture
    // a different item first. Null or empty means general (score-maximizing) optimization.
    private string? _targetItemName;
    public string? TargetItemName
    {
        get => _targetItemName;
        set => Set(ref _targetItemName, value);
    }

    // Solver strategy preset. 0=Safe, 1=Cascade Hunter, 2=Speed, 3=Target Hunter,
    // 4=Empirical, 5=Cascade Aggressive. Default is Cascade Aggressive (5): in the 2026-06
    // sample it took the top Deluxe score (1753) and beat Empirical on average (1201 vs
    // 1168), avg/min, and top/min with n=34. LM data still pending — we're betting the LM
    // Tier Hold (C≥2, same logic as Deluxe at C≥3) generalises and the aggression tuning
    // is just amplified Cascade Hunter, which historically held the LM peak. Safe and
    // Speed are retired from the picker (hidden ComboBoxItems) but kept as enum values for
    // back-compat with historical records and any future re-enablement. Stored as int so
    // OverlaySettings doesn't take a dependency on PgLootMaster.Solver (layering).
    private int _solverStrategy = 5;
    public int SolverStrategy
    {
        get => _solverStrategy;
        set => Set(ref _solverStrategy, value);
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
                    settings.ShowSwapHighlight = loaded.ShowSwapHighlight;
                    settings.ToolbarLeft = loaded.ToolbarLeft;
                    settings.ToolbarTop = loaded.ToolbarTop;
                    settings.TargetItemName = loaded.TargetItemName;
                    settings.SolverStrategy = loaded.SolverStrategy;
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
