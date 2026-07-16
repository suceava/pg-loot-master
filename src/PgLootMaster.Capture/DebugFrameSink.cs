using OpenCvSharp;
using OpenCvMat = OpenCvSharp.Mat;

namespace PgLootMaster.Capture;

public sealed class DebugFrameSink
{
    private readonly string _directory;
    private readonly TimeSpan _interval;
    private readonly int _maxFrames;
    private DateTime _nextSaveUtc = DateTime.MinValue;
    private int _savedCount;
    private bool _loggedCap;

    public DebugFrameSink(string directory, TimeSpan interval, int maxFrames = 30)
    {
        _directory = directory;
        _interval = interval;
        _maxFrames = maxFrames;

        // Clear any frames left behind by previous sessions. The per-session cap only
        // protects THIS run — without cleanup, running the tool N times would leave
        // N × maxFrames frames on disk (a few GB after enough sessions, which shipped as
        // a real bug before this fix). Each session starts with an empty folder so total
        // disk usage stays bounded at roughly maxFrames × frame size.
        try
        {
            if (Directory.Exists(_directory))
            {
                int deleted = 0;
                foreach (string old in Directory.EnumerateFiles(_directory, "frame-*.png"))
                {
                    try { File.Delete(old); deleted++; } catch { }
                }
                if (deleted > 0)
                    DebugLog.Write($"DebugFrameSink: cleared {deleted} old frame(s) from {_directory}");
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write($"DebugFrameSink: cleanup failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Accept(OpenCvMat frame)
    {
        if (_savedCount >= _maxFrames)
        {
            if (!_loggedCap)
            {
                _loggedCap = true;
                DebugLog.Write($"DebugFrameSink: hit max-frames cap ({_maxFrames}), no more frames will be saved this session");
            }
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (now < _nextSaveUtc) return;
        _nextSaveUtc = now + _interval;

        try
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, $"frame-{now:yyyyMMdd-HHmmss}-{_savedCount:0000}.png");
            Cv2.ImWrite(path, frame);
            _savedCount++;
            DebugLog.Write($"DebugFrameSink saved {path} ({frame.Width}x{frame.Height}) [{_savedCount}/{_maxFrames}]");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"DebugFrameSink save error: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
