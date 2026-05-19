using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;
using OpenCvRect = OpenCvSharp.Rect;

namespace PgLootMaster.Vision;

public readonly record struct PanelLocation(OpenCvRect TitleBar, double Confidence, string TemplateName);

public sealed class PanelLocator : IDisposable
{
    private readonly OpenCvMat[] _templates;
    private readonly string[] _templateNames;
    private readonly double _matchThreshold;
    private bool _disposed;

    /// <summary>
    /// Construct with a directory path or a single file path. If a directory, loads every
    /// "panel-title*.png" template found inside. If a file, loads just that one. Each frame
    /// is matched against every template; the highest-confidence match above threshold wins.
    /// </summary>
    public PanelLocator(string templatePathOrDir, double matchThreshold = 0.7)
    {
        List<OpenCvMat> templates = new();
        List<string> names = new();

        if (Directory.Exists(templatePathOrDir))
        {
            foreach (string file in Directory.EnumerateFiles(templatePathOrDir, "panel-title*.png"))
            {
                OpenCvMat t = Cv2.ImRead(file, ImreadModes.Color);
                if (!t.Empty())
                {
                    templates.Add(t);
                    names.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
        }
        else if (File.Exists(templatePathOrDir))
        {
            // Single-file path. Also try sibling files matching panel-title*.png so callers
            // that just hand us one template still pick up Cashfall/Deluxe variants.
            string? dir = Path.GetDirectoryName(templatePathOrDir);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                foreach (string file in Directory.EnumerateFiles(dir, "panel-title*.png"))
                {
                    OpenCvMat t = Cv2.ImRead(file, ImreadModes.Color);
                    if (!t.Empty())
                    {
                        templates.Add(t);
                        names.Add(Path.GetFileNameWithoutExtension(file));
                    }
                }
            }
            else
            {
                OpenCvMat t = Cv2.ImRead(templatePathOrDir, ImreadModes.Color);
                if (!t.Empty())
                {
                    templates.Add(t);
                    names.Add(Path.GetFileNameWithoutExtension(templatePathOrDir));
                }
            }
        }

        if (templates.Count == 0)
            throw new FileNotFoundException($"No panel title templates found at {templatePathOrDir}");

        _templates = templates.ToArray();
        _templateNames = names.ToArray();
        _matchThreshold = matchThreshold;
    }

    public IReadOnlyList<string> TemplateNames => _templateNames;

    public PanelLocation? TryLocate(OpenCvMat frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Empty()) return null;

        OpenCvMat frameForMatch = frame;
        OpenCvMat? converted = null;
        try
        {
            if (frame.Channels() == 4)
            {
                converted = new OpenCvMat();
                Cv2.CvtColor(frame, converted, ColorConversionCodes.BGRA2BGR);
                frameForMatch = converted;
            }

            double bestVal = double.NegativeInfinity;
            OpenCvSharp.Point bestLoc = default;
            int bestIdx = -1;
            for (int i = 0; i < _templates.Length; i++)
            {
                OpenCvMat tpl = _templates[i];
                if (frameForMatch.Width < tpl.Width || frameForMatch.Height < tpl.Height) continue;
                using OpenCvMat result = new();
                Cv2.MatchTemplate(frameForMatch, tpl, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);
                if (maxVal > bestVal)
                {
                    bestVal = maxVal;
                    bestLoc = maxLoc;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0 || bestVal < _matchThreshold) return null;
            OpenCvMat winner = _templates[bestIdx];
            OpenCvRect titleBar = new(bestLoc.X, bestLoc.Y, winner.Width, winner.Height);
            return new PanelLocation(titleBar, bestVal, _templateNames[bestIdx]);
        }
        finally
        {
            converted?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (OpenCvMat t in _templates) t.Dispose();
    }
}
