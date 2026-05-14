using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;
using OpenCvRect = OpenCvSharp.Rect;

namespace PgLootMaster.Vision;

public readonly record struct PanelLocation(OpenCvRect TitleBar, double Confidence);

public sealed class PanelLocator : IDisposable
{
    private readonly OpenCvMat _template;
    private readonly double _matchThreshold;
    private bool _disposed;

    public PanelLocator(string templatePath, double matchThreshold = 0.7)
    {
        if (!File.Exists(templatePath))
            throw new FileNotFoundException($"Panel title template not found at {templatePath}", templatePath);

        OpenCvMat template = Cv2.ImRead(templatePath, ImreadModes.Color);
        if (template.Empty())
            throw new InvalidDataException($"Failed to decode panel template at {templatePath}");

        _template = template;
        _matchThreshold = matchThreshold;
    }

    public PanelLocation? TryLocate(OpenCvMat frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Empty()) return null;
        if (frame.Width < _template.Width || frame.Height < _template.Height) return null;

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

            using OpenCvMat result = new();
            Cv2.MatchTemplate(frameForMatch, _template, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);

            if (maxVal < _matchThreshold) return null;
            OpenCvRect titleBar = new(maxLoc.X, maxLoc.Y, _template.Width, _template.Height);
            return new PanelLocation(titleBar, maxVal);
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
        _template.Dispose();
    }
}
