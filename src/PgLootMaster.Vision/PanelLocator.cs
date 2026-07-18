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

    // Last-found cache so we can do a fast local search around the previous hit instead
    // of re-scanning the entire 4K frame every tick. Cleared when the local search misses
    // (panel actually moved, vanished, or we've never found it).
    private OpenCvRect? _lastFoundRect;
    private int _lastFoundTemplateIdx = -1;
    // ±80 px around the last hit. Generous enough to absorb small window drift / scroll,
    // tight enough that the local match is ~1 ms vs ~600 ms full-frame.
    private const int LocalSearchPadding = 80;

    // Preprocessing before template match: BGR → grayscale → GaussianBlur(BlurKernel).
    // Why: raw color CCoeffNormed is fragile to sub-visible rendering differences (Unity
    // texture-filtering quality, ClearType/font-hinting settings, GPU driver differences).
    // At identical resolution and DPI, one machine can score 0.92 while another scores 0.52
    // on the same panel — same visible text, different pixels. Grayscale strips ClearType
    // colour fringes; the blur softens hinting/rasterisation differences. Tuned empirically
    // against the 2026-07-18 mismatch: Ultra-quality captures still score >0.9 while
    // downgraded-quality captures pass the 0.65 threshold with room to spare.
    private const int BlurKernel = 7;

    /// <summary>
    /// Construct from panel-title templates embedded in this assembly as manifest resources
    /// (the samples/templates/panel-title*.png files are compiled in via EmbeddedResource in
    /// the .csproj). Lets the app ship as a single self-contained .exe — no external
    /// Templates\ folder next to the exe. Each frame is matched against every template; the
    /// highest-confidence match above threshold wins.
    /// </summary>
    public PanelLocator(double matchThreshold = 0.65)
    {
        List<OpenCvMat> templates = new();
        List<string> names = new();

        System.Reflection.Assembly asm = typeof(PanelLocator).Assembly;
        // The LogicalName in the .csproj is "PgLootMaster.Vision.Templates.<filename>.png".
        const string ResourcePrefix = "PgLootMaster.Vision.Templates.";
        foreach (string resName in asm.GetManifestResourceNames())
        {
            if (!resName.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            if (!resName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;

            using Stream? s = asm.GetManifestResourceStream(resName);
            if (s is null) continue;
            using MemoryStream ms = new();
            s.CopyTo(ms);
            using OpenCvMat colorTpl = Cv2.ImDecode(ms.ToArray(), ImreadModes.Color);
            if (colorTpl.Empty()) continue;

            templates.Add(Preprocess(colorTpl));
            // Recover the file name minus extension: "panel-title-deluxe".
            string fileName = resName.Substring(ResourcePrefix.Length);
            int dot = fileName.LastIndexOf('.');
            names.Add(dot > 0 ? fileName.Substring(0, dot) : fileName);
        }

        if (templates.Count == 0)
            throw new InvalidOperationException(
                "No panel-title templates found in the assembly's embedded resources.");

        _templates = templates.ToArray();
        _templateNames = names.ToArray();
        _matchThreshold = matchThreshold;
    }

    /// <summary>
    /// Construct with a directory path or a single file path. Kept for the Vision tests,
    /// which point at samples/templates/ on disk. If a directory, loads every
    /// "panel-title*.png" template found inside. If a file, loads just that one.
    /// </summary>
    public PanelLocator(string templatePathOrDir, double matchThreshold = 0.65)
    {
        List<OpenCvMat> templates = new();
        List<string> names = new();

        if (Directory.Exists(templatePathOrDir))
        {
            foreach (string file in Directory.EnumerateFiles(templatePathOrDir, "panel-title*.png"))
            {
                using OpenCvMat colorTpl = Cv2.ImRead(file, ImreadModes.Color);
                if (!colorTpl.Empty())
                {
                    templates.Add(Preprocess(colorTpl));
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
                    using OpenCvMat colorTpl = Cv2.ImRead(file, ImreadModes.Color);
                    if (!colorTpl.Empty())
                    {
                        templates.Add(Preprocess(colorTpl));
                        names.Add(Path.GetFileNameWithoutExtension(file));
                    }
                }
            }
            else
            {
                using OpenCvMat colorTpl = Cv2.ImRead(templatePathOrDir, ImreadModes.Color);
                if (!colorTpl.Empty())
                {
                    templates.Add(Preprocess(colorTpl));
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

    /// <summary>BGR → grayscale → Gaussian blur. Both templates (at load) and each incoming
    /// frame (per TryLocate) run through this so the match sees the same preprocessed
    /// pixels on both sides. See BlurKernel constant for rationale.</summary>
    private static OpenCvMat Preprocess(OpenCvMat bgr)
    {
        OpenCvMat gray = new();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(BlurKernel, BlurKernel), 0);
        return gray;
    }

    public IReadOnlyList<string> TemplateNames => _templateNames;

    public PanelLocation? TryLocate(OpenCvMat frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Empty()) return null;

        // Preprocess the incoming frame the same way the templates were preprocessed at
        // load: BGR/BGRA → grayscale → GaussianBlur(BlurKernel). See BlurKernel field.
        OpenCvMat? converted = null;
        OpenCvMat frameForMatch;
        try
        {
            converted = new OpenCvMat();
            ColorConversionCodes toGray = frame.Channels() == 4
                ? ColorConversionCodes.BGRA2GRAY
                : ColorConversionCodes.BGR2GRAY;
            Cv2.CvtColor(frame, converted, toGray);
            Cv2.GaussianBlur(converted, converted, new OpenCvSharp.Size(BlurKernel, BlurKernel), 0);
            frameForMatch = converted;

            // Fast path: if we have a previous hit, search a ±LocalSearchPadding window
            // around it for the SAME template. ~1 ms vs ~600 ms full-frame.
            if (_lastFoundRect.HasValue && _lastFoundTemplateIdx >= 0
                && _lastFoundTemplateIdx < _templates.Length)
            {
                OpenCvMat tpl = _templates[_lastFoundTemplateIdx];
                OpenCvRect last = _lastFoundRect.Value;
                int roiX = Math.Max(0, last.X - LocalSearchPadding);
                int roiY = Math.Max(0, last.Y - LocalSearchPadding);
                int roiW = Math.Min(frameForMatch.Width - roiX, tpl.Width + 2 * LocalSearchPadding);
                int roiH = Math.Min(frameForMatch.Height - roiY, tpl.Height + 2 * LocalSearchPadding);
                if (roiW >= tpl.Width && roiH >= tpl.Height)
                {
                    OpenCvRect roi = new(roiX, roiY, roiW, roiH);
                    using OpenCvMat roiMat = new(frameForMatch, roi);
                    using OpenCvMat result = new();
                    Cv2.MatchTemplate(roiMat, tpl, result, TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);
                    if (maxVal >= _matchThreshold)
                    {
                        OpenCvRect found = new(maxLoc.X + roi.X, maxLoc.Y + roi.Y, tpl.Width, tpl.Height);
                        _lastFoundRect = found;
                        return new PanelLocation(found, maxVal, _templateNames[_lastFoundTemplateIdx]);
                    }
                }
                // Local search missed — fall through to full-frame search (panel may have
                // moved further than the window, or transitioned to a different template).
            }

            // Slow path: full-frame search against every template.
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

            if (bestIdx < 0 || bestVal < _matchThreshold)
            {
                _lastFoundRect = null;
                _lastFoundTemplateIdx = -1;
                return null;
            }
            OpenCvMat winner = _templates[bestIdx];
            OpenCvRect titleBar = new(bestLoc.X, bestLoc.Y, winner.Width, winner.Height);
            _lastFoundRect = titleBar;
            _lastFoundTemplateIdx = bestIdx;
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
