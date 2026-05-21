using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PgLootMaster.Vision;

namespace PgLootMaster;

/// <summary>
/// Phase-1 diagnostic UI for the labeler. Lets the user mark each cluster's assigned
/// label as Correct / Wrong / Skip and persists annotations to
/// %APPDATA%\PgLootMaster\labeler-annotations.json. Aggregate accuracy stats roll into
/// the bottom strip.
///
/// Doesn't change labeler behavior — it just measures. Future Phase-2 / Phase-3 changes
/// can be A/B tested against the annotated baseline.
/// </summary>
public partial class LabelerDebugWindow : Window
{
    private readonly LabelerAnnotationStore _store;
    private List<ClusterRow> _rows = new();

    public LabelerDebugWindow()
    {
        InitializeComponent();
        _store = LabelerAnnotationStore.Load();
        ClustersGrid.ItemsSource = _rows;
        UpdateStats();
    }

    /// <summary>
    /// Cluster color palette — keep in sync with OverlayWindow.ClusterColors. Drives both
    /// the on-board cell borders and the debug-window swatches so the user can match
    /// rows to board cells by color.
    /// </summary>
    private static readonly Color[] PaletteColors = new[]
    {
        Color.FromRgb(255, 64, 64),
        Color.FromRgb(64, 255, 64),
        Color.FromRgb(64, 128, 255),
        Color.FromRgb(255, 220, 0),
        Color.FromRgb(255, 0, 220),
        Color.FromRgb(0, 220, 220),
        Color.FromRgb(255, 140, 0),
        Color.FromRgb(180, 0, 220),
        Color.FromRgb(255, 255, 255),
        Color.FromRgb(128, 128, 128),
    };

    /// <summary>
    /// Push a fresh labeler snapshot from the overlay. Called every frame the labeler runs.
    /// </summary>
    public void Update(LabelDiagnostics? diag)
    {
        if (diag is null || diag.ClusterCount == 0 || diag.TemplateCount == 0)
        {
            _rows = new List<ClusterRow>();
            ClustersGrid.ItemsSource = null;
            ClustersGrid.ItemsSource = _rows;
            return;
        }

        List<ClusterRow> next = new();
        for (int c = 0; c < diag.ClusterCount; c++)
        {
            if (diag.CellsPerCluster[c] == 0) continue;
            int label = diag.Labels[c];
            string labelName = label >= 0 && label < diag.TemplateNames.Length
                ? diag.TemplateNames[label]
                : "(unassigned)";
            double bestScore = diag.BestScore(c);
            double confidence = diag.Confidence(c);

            // Find runner-up template name + score.
            int runnerUpIdx = -1;
            double runnerUpScore = double.NegativeInfinity;
            for (int t = 0; t < diag.TemplateCount; t++)
            {
                if (t == label) continue;
                double s = diag.AvgScore(c, t);
                if (s > runnerUpScore) { runnerUpScore = s; runnerUpIdx = t; }
            }
            string runnerUpName = runnerUpIdx >= 0 && runnerUpIdx < diag.TemplateNames.Length
                ? diag.TemplateNames[runnerUpIdx]
                : "—";

            string annotation = _store.GetAnnotation(labelName) ?? "—";
            bool locked = diag.IsLocked(c);
            ClusterRow row = new()
            {
                ClusterId = c,
                CellCount = diag.CellsPerCluster[c],
                AssignedLabel = locked ? $"🔒 {labelName}" : labelName,
                BestScoreText = locked ? "LOCKED" : bestScore.ToString("F3"),
                ConfidenceText = locked ? "—" : confidence.ToString("F3"),
                RunnerUpText = locked ? "(ground truth)" : $"{runnerUpName} ({runnerUpScore:F3})",
                ColorBrush = new SolidColorBrush(PaletteColors[c % PaletteColors.Length]),
                AnnotationText = annotation,
            };
            next.Add(row);
        }
        // Preserve selection across updates if possible.
        int? prevSelected = (ClustersGrid.SelectedItem as ClusterRow)?.ClusterId;
        _rows = next;
        ClustersGrid.ItemsSource = null;
        ClustersGrid.ItemsSource = _rows;
        if (prevSelected is int prev)
        {
            foreach (ClusterRow r in _rows)
            {
                if (r.ClusterId == prev)
                {
                    ClustersGrid.SelectedItem = r;
                    break;
                }
            }
        }
        UpdateStats();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (ClustersGrid.SelectedItem is not ClusterRow row) return;
        if (string.IsNullOrEmpty(row.AssignedLabel) || row.AssignedLabel == "(unassigned)") return;

        string? verdict = e.Key switch
        {
            Key.Y => "correct",
            Key.N => "wrong",
            Key.S => null,    // skip — but record explicitly as "skipped" if we want to count
            _ => "__unhandled__",
        };
        if (verdict == "__unhandled__") return;
        e.Handled = true;
        if (verdict is null)
        {
            // Skip: clear any existing annotation for this label.
            _store.Clear(row.AssignedLabel);
        }
        else
        {
            _store.Record(row.AssignedLabel, verdict);
        }
        row.AnnotationText = verdict ?? "—";
        ClustersGrid.Items.Refresh();
        UpdateStats();
    }

    private void UpdateStats()
    {
        var summary = _store.Summary();
        if (summary.Total == 0)
        {
            StatsText.Text = "(no annotations yet — select a row, press Y / N / S)";
            return;
        }
        double accuracy = summary.Total > 0 ? 100.0 * summary.Correct / summary.Total : 0;
        System.Text.StringBuilder sb = new();
        sb.AppendLine($"Total annotated: {summary.Total}    Correct: {summary.Correct}    Wrong: {summary.Wrong}    Accuracy: {accuracy:F1}%");
        // Per-label breakdown — show items with at least one annotation.
        bool firstPerLabel = true;
        foreach (var (name, c, w) in summary.PerLabel)
        {
            int tot = c + w;
            if (tot == 0) continue;
            if (firstPerLabel) { sb.Append("  "); firstPerLabel = false; } else sb.Append("  |  ");
            double pct = 100.0 * c / tot;
            sb.Append($"{name} {pct:F0}% ({c}/{tot})");
        }
        StatsText.Text = sb.ToString();
    }

    public sealed class ClusterRow : INotifyPropertyChanged
    {
        public int ClusterId { get; set; }
        public int CellCount { get; set; }
        public string AssignedLabel { get; set; } = "";
        public string BestScoreText { get; set; } = "";
        public string ConfidenceText { get; set; } = "";
        public string RunnerUpText { get; set; } = "";
        public Brush ColorBrush { get; set; } = Brushes.Gray;

        private string _annotationText = "—";
        public string AnnotationText
        {
            get => _annotationText;
            set { _annotationText = value; OnChanged(); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Persists per-label annotations (which sidebar items the labeler tagged correctly vs
/// not) across sessions. Keyed by the assigned label NAME (cluster IDs aren't stable
/// across frames — labels are).
/// </summary>
public sealed class LabelerAnnotationStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PgLootMaster");
    private static readonly string FilePath = Path.Combine(Dir, "labeler-annotations.json");

    // labelName → list of verdicts ("correct" / "wrong"). One entry per Y/N keypress.
    public Dictionary<string, List<string>> Verdicts { get; set; } = new();

    public static LabelerAnnotationStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                LabelerAnnotationStore? loaded = JsonSerializer.Deserialize<LabelerAnnotationStore>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch { }
        return new LabelerAnnotationStore();
    }

    public void Record(string labelName, string verdict)
    {
        if (!Verdicts.TryGetValue(labelName, out List<string>? list))
        {
            list = new List<string>();
            Verdicts[labelName] = list;
        }
        list.Add(verdict);
        Save();
    }

    public void Clear(string labelName)
    {
        if (Verdicts.Remove(labelName)) Save();
    }

    public string? GetAnnotation(string labelName)
    {
        if (!Verdicts.TryGetValue(labelName, out List<string>? list) || list.Count == 0) return null;
        // Most recent verdict wins.
        return list[^1];
    }

    public (int Total, int Correct, int Wrong, List<(string Name, int Correct, int Wrong)> PerLabel) Summary()
    {
        int total = 0, correct = 0, wrong = 0;
        List<(string, int, int)> per = new();
        foreach (var kv in Verdicts)
        {
            int c = kv.Value.Count(v => v == "correct");
            int w = kv.Value.Count(v => v == "wrong");
            total += c + w;
            correct += c;
            wrong += w;
            per.Add((kv.Key, c, w));
        }
        per.Sort((a, b) => (b.Item2 + b.Item3).CompareTo(a.Item2 + a.Item3));
        return (total, correct, wrong, per);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
