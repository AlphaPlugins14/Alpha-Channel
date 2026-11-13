using System.Diagnostics;
using System.Globalization;

namespace AlphaChannel.Plugin.Video;

internal partial class MpvRenderer
{
    // This state is local to this renderer. It never changes shared scene metadata.
    private VisualizerQuality _localVisualizerQuality;
    private AudioVisualizerMode _adaptiveMode;
    private AudioVisualizerTheme _adaptiveTheme;
    private readonly VisualizerPerformancePolicy _visualizerPolicy = new();
    private long _visualizerFrames, _visualizerRenderTicks;
    private long _previousVisualizerFrames, _previousVisualizerTicks;
    private double _visualizerWindowStart, _previousAudioPosition = double.NaN;
    private bool _adaptiveReductionRejected;
    private static double PerformanceTime => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // Caller holds _mpvLock. Render counters are atomic and require no render lock.
    private void ResetVisualizerMeasurement()
    {
        _visualizerWindowStart = PerformanceTime;
        _previousVisualizerFrames = Interlocked.Read(ref _visualizerFrames);
        _previousVisualizerTicks = Interlocked.Read(ref _visualizerRenderTicks);
        _previousAudioPosition = double.NaN;
        _adaptiveReductionRejected = false;
        _visualizerPolicy.Reset(_visualizerWindowStart);
    }

    private void CheckVisualizerPerformance()
    {
        // Run on the existing MPV event worker, never the game/Present thread.
        if (!_mpvLock.TryEnter()) return;
        try
        {
            if (_closed || !frameConsumersVisible || _mpvCtx == IntPtr.Zero || !_audioSpectrumEnabled ||
                _adaptiveReductionRejected || _localVisualizerQuality == VisualizerQuality.Minimal) return;
            var now = PerformanceTime;
            var elapsed = now - _visualizerWindowStart;
            if (elapsed < 5) return;
            var frames = Interlocked.Read(ref _visualizerFrames);
            var ticks = Interlocked.Read(ref _visualizerRenderTicks);
            var delivered = frames - _previousVisualizerFrames;
            var renderMs = delivered > 0 ? (ticks - _previousVisualizerTicks) * 1000d / Stopwatch.Frequency / delivered : 0;
            var positionKnown = double.TryParse(ReadPropertyString("time-pos"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var position) && double.IsFinite(position);
            var progress = position - _previousAudioPosition;
            var playing = ReadPropertyString("pause") == "no" && ReadPropertyString("paused-for-cache") == "no"
                && ReadPropertyString("idle-active") == "no" && positionKnown
                && progress >= elapsed * .75 && progress <= elapsed * 1.25;
            _visualizerWindowStart = now;
            _previousVisualizerFrames = frames;
            _previousVisualizerTicks = ticks;
            _previousAudioPosition = positionKnown ? position : double.NaN;
            var fps = AudioVisualizerFilter.GetFrameRate(_adaptiveMode, _localVisualizerQuality);
            if (!_visualizerPolicy.ShouldReduce(now, playing, delivered / elapsed, fps)) return;

            var next = _localVisualizerQuality == VisualizerQuality.Full ? VisualizerQuality.Reduced : VisualizerQuality.Minimal;
            var graph = AudioVisualizerFilter.GetGraph(_adaptiveMode, _adaptiveTheme, next);
            var result = mpv_command(_mpvCtx, ["set", "lavfi-complex", graph, null!]);
            if (result < 0)
            {
                // A failed lavfi replacement may discard the previous graph in MPV.
                // Restore it explicitly and suppress repeated failed attempts.
                var restored = _installedVisualizerGraph is { } previous
                    ? mpv_command(_mpvCtx, ["set", "lavfi-complex", previous, null!]) : -1;
                _adaptiveReductionRejected = true;
                AepLog.Warning("[AudioVisualizer] Local quality reduction rejected: rc=[redacted]; prior graph restore rc=[redacted].");
                return;
            }
            _localVisualizerQuality = next;
            _installedVisualizerGraph = graph;
            ResetVisualizerMeasurement();
            
        }
        finally { _mpvLock.Exit(); }
    }
}
