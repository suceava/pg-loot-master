using OpenCvSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using OpenCvMat = OpenCvSharp.Mat;
using OpenCvRect = OpenCvSharp.Rect;

namespace PgLootMaster.Vision;

public sealed record OcrLine(string Text, OpenCvRect Bbox);

public sealed class SidebarOcr
{
    private readonly OcrEngine? _engine;

    public SidebarOcr()
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages();
    }

    public bool IsAvailable => _engine is not null;

    public IReadOnlyList<OcrLine> Recognize(OpenCvMat bgrCrop)
    {
        if (_engine is null || bgrCrop.Empty()) return Array.Empty<OcrLine>();

        Cv2.ImEncode(".png", bgrCrop, out byte[] pngBytes);

        OcrResult result = RunOcrAsync(pngBytes, _engine).GetAwaiter().GetResult();

        List<OcrLine> lines = new();
        foreach (Windows.Media.Ocr.OcrLine line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (OcrWord word in line.Words)
            {
                Windows.Foundation.Rect r = word.BoundingRect;
                if (r.X < minX) minX = r.X;
                if (r.Y < minY) minY = r.Y;
                if (r.X + r.Width > maxX) maxX = r.X + r.Width;
                if (r.Y + r.Height > maxY) maxY = r.Y + r.Height;
            }
            OpenCvRect bbox = new(
                (int)minX,
                (int)minY,
                (int)(maxX - minX),
                (int)(maxY - minY));
            lines.Add(new OcrLine(line.Text, bbox));
        }
        return lines;
    }

    private static async Task<OcrResult> RunOcrAsync(byte[] pngBytes, OcrEngine engine)
    {
        using InMemoryRandomAccessStream stream = new();
        using DataWriter writer = new(stream);
        writer.WriteBytes(pngBytes);
        await writer.StoreAsync();
        await writer.FlushAsync();
        writer.DetachStream();
        stream.Seek(0);

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        using SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync();
        return await engine.RecognizeAsync(softwareBitmap);
    }
}
