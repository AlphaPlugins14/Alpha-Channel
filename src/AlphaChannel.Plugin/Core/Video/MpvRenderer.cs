using System.Runtime.InteropServices;
using SharpDX.Direct3D11;

namespace AlphaChannel.Plugin.Video
{
	internal sealed record SubtitleTrack(
        int Id, string Language, string Name, bool IsExternal = false,
        int? FfmpegStreamIndex = null, string Codec = "");

	internal partial class MpvRenderer : IDisposable
	{
		private const string DLL = "libmpv-2";
		private const string AudioLevelFilter =
			"@alphavol:lavfi=[astats=metadata=1:reset=1:measure_perchannel=none:measure_overall=RMS_level]";
		private static Resources? _resources;

        private string _androidYtdlRawOptions =
    "hls-use-mpegts=,extractor-args=youtube:player_client=android";

        private string _poTokenYtdlRawOptions =
            "hls-use-mpegts=,extractor-args=youtube:player_client=android";

        private string _authenticatedPoTokenYtdlRawOptions =
            "hls-use-mpegts=,extractor-args=youtube:player_client=android";

        internal static Task PrepareAudioAsync() => _resources?.PrepareMediaAsync(false) ?? Task.CompletedTask;
        public static void Setup(Resources resources)
		{
			_resources = resources;
		}
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_create();
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_initialize(IntPtr ctx);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_set_option_string(IntPtr ctx, string name, string data);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_command(IntPtr ctx, string[] args);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_render_context_create(ref IntPtr res, IntPtr ctx, IntPtr parms);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_render_context_render(IntPtr ctx, IntPtr parms);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_render_context_free(IntPtr ctx);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_render_context_set_update_callback(IntPtr ctx, MpvRenderUpdateFn callback, IntPtr callback_ctx);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern ulong mpv_render_context_update(IntPtr ctx);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_wakeup(IntPtr ctx);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern int mpv_request_log_messages(IntPtr ctx, string min_level);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_terminate_destroy(IntPtr ctx);
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_get_property(
     IntPtr ctx,
     string name,
     int format,
     out double data);

        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mpv_get_property(
            IntPtr ctx,
            string name,
            int format,
            IntPtr data);
        [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr mpv_get_property_string(IntPtr ctx, string name);
		[DllImport(DLL, CallingConvention = CallingConvention.Cdecl)] private static extern void mpv_free(IntPtr data);

		[StructLayout(LayoutKind.Sequential)]
		private struct MpvRenderParam { public int Type; public IntPtr Data; }

		public delegate void MpvRenderUpdateFn(IntPtr callback_ctx);

        private static int nextRendererId;
        private volatile bool frameConsumersVisible = true;
        private int forceVisualRefresh;
        internal void SetFrameConsumersVisible(bool visible)
        {
            var previous = frameConsumersVisible; frameConsumersVisible = visible;
            if (visible && !previous && !_closed) try { Interlocked.Exchange(ref forceVisualRefresh,1); _frameReady.Set(); } catch (ObjectDisposedException) { }
            if (!visible) DxHandler.CancelRenderThreadWork(RenderKey);
        }
        private unsafe bool SkipHiddenFrame()
        {
            var skip = 1;
            MpvRenderParam* parameters = stackalloc MpvRenderParam[2];
            parameters[0] = new() { Type = 13, Data = (nint)(&skip) };
            parameters[1] = default;
            return mpv_render_context_render(_mpvRenderCtx,(nint)parameters) >= 0;
        }
        private readonly string RenderKey = $"mpv:{Interlocked.Increment(ref nextRendererId)}";

		private IntPtr _mpvCtx;
		private IntPtr _mpvRenderCtx;
		private IntPtr _bufferPtr;
		private IntPtr _snapA, _snapB;
		private bool _useSnapA = true;
		private int _frameBytes;
		private int _width, _height;
        private int _videoWidth, _videoHeight;
        private Texture2D? _videoTargetTexture, _visualizerTargetTexture;
        private int _outputGeneration;
		private CancellationToken _cancelToken;
		private IntPtr _renderParamsPtr;
		private IntPtr _sizePtr, _stridePtr, _formatPtr;
		private Texture2D? _targetTexture;
		private ManualResetEventSlim _frameReady = new ManualResetEventSlim(false);
		private MpvRenderUpdateFn? _updateCallback;
		private GCHandle _updateCallbackHandle;
		private volatile bool _closed = true;
        private int _stopRenderStarted;
		private Thread? _eventThread;
        private float _smoothedAudioLevel;
        private volatile bool _audioSpectrumEnabled;
        private bool _lavfiVisualizerGraphInstalled;
        private string? _installedVisualizerGraph;
        private LiveAudioFfmpegRelay? _liveAudioRelay;
        private bool _liveAudioPlayback;
        private bool _diagnoseLivePlayback;
        private DateTime _nextLiveSampleUtc, _nextLiveLogUtc;
        private int _liveBufferingSamples;
        private bool _liveEofReported, _liveFileLoaded, _liveUsesTls;
        private readonly Dictionary<string, DateTime> _pendingArchiveSubtitles = new(StringComparer.Ordinal);

        internal bool AudioSpectrumEnabled =>
            _audioSpectrumEnabled;
        // MPV log warnings/errors.
        //
        // These are useful diagnostics, but they are NOT by themselves proof that
        // playback has failed. MPV and yt-dlp can emit warnings during otherwise
        // successful playback.
        internal Action<string>? OnError;


        // Definitive terminal playback failure.
        //
        // This is fired from MPV_EVENT_END_FILE when mpv reports
        // MPV_END_FILE_REASON_ERROR.
        //
        // VideoEngine uses this to decide whether the automatic webpage resolver
        // should get its second chance.
        internal Action<string>? OnPlaybackFailed;


        internal Action? OnFrameRendered;
        internal Action? OnMediaLoaded;

		private IntPtr _latestSnapshot;

        public void Initialize(int width, int height, Texture2D? targetTexture, CancellationToken cancelToken,
			bool hardwareDecoding = false, int maxQualityHeight = 1080, bool allowInsecureDirectUrls = false,
			int initialVolume = 60, string? cookiesPath = null, Texture2D? visualizerTargetTexture = null,
            bool auxiliaryAudioOnly = false)
		{
            lock (_renderLock)
            lock (_mpvLock)
            {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopRenderStarted) != 0, this);
            cancelToken.ThrowIfCancellationRequested();

            _width = width;
            audioDiagnosticsEnabled = !auxiliaryAudioOnly;
			_height = height;
            _videoWidth = width;
            _videoHeight = height;
            _videoTargetTexture = targetTexture;
            _visualizerTargetTexture = visualizerTargetTexture;
			_cancelToken = cancelToken;
			_targetTexture = targetTexture;

			_frameBytes = width * height * 4;
            if (!auxiliaryAudioOnly)
            {
			    _bufferPtr = Marshal.AllocHGlobal(_frameBytes);
			    _snapA = Marshal.AllocHGlobal(_frameBytes);
			    _snapB = Marshal.AllocHGlobal(_frameBytes);
            }

			_mpvCtx = mpv_create();
            if (_mpvCtx == IntPtr.Zero) throw new InvalidOperationException("MPV could not create a playback context.");
			_ = mpv_set_option_string(_mpvCtx, "vo", auxiliaryAudioOnly ? "null" : "libmpv");
            if (auxiliaryAudioOnly)
            {
                _ = mpv_set_option_string(_mpvCtx, "vid", "no");
                _ = mpv_set_option_string(_mpvCtx, "audio-display", "no");
            }
			// Not measured on this project's Wine/RADV setup - mpv has no GPU render path here
			// either way, only decode could benefit. Off is the safe default; read fresh here so a
			// settings change takes effect on the next video, not the current one.
			_ = mpv_set_option_string(_mpvCtx, "hwdec", hardwareDecoding ? "auto-safe" : "no");
			_ = mpv_set_option_string(_mpvCtx, "profile", "sw-fast");
			_ = mpv_set_option_string(_mpvCtx, "ytdl", "yes");
            _ = mpv_set_option_string(
        _mpvCtx,
        "script-opts",
        $"ytdl_hook-ytdl_path={_resources?.GetLocationYTDLP(prepareIfMissing: false)}");

            var qualitySelector =
                $"bestvideo[height<={maxQualityHeight}]+bestaudio/" +
                $"best[height<={maxQualityHeight}]";

            _ = mpv_set_option_string(
                _mpvCtx,
                "ytdl-format",
                qualitySelector);

            _ = mpv_set_option_string(
                _mpvCtx,
                "terminal",
                "yes");
            _ = mpv_set_option_string(_mpvCtx, "volume", initialVolume.ToString(System.Globalization.CultureInfo.InvariantCulture));

			_ = mpv_set_option_string(_mpvCtx, "msg-level", "all=warn,ffmpeg=error");
            // force-ipv4 used to be set here too, but it only affects yt-dlp's own resolve
            // force-ipv4 used to be set here too, but it only affects yt-dlp's own
            // resolution request, not mpv's later fetch of the resolved media URL.
            //
            // Android remains the normal fast route. The PO-token configuration is
            // prepared separately and can be selected immediately before loadfile
            // when compatibility mode is active.
            var androidBaseOptions =
                _resources is not null
                    ? YouTubePoTokenSupport.BuildMpvRawOptions(
                        _resources,
                        usePoTokens: false)
                    : "hls-use-mpegts=," +
                      "extractor-args=youtube:player_client=android";

            _androidYtdlRawOptions =
                androidBaseOptions;

            _poTokenYtdlRawOptions =
                _resources is not null && YouTubePoTokenSupport.IsAvailable(_resources)
                    ? YouTubePoTokenSupport.BuildMpvRawOptions(
                        _resources,
                        usePoTokens: true)
                    : _androidYtdlRawOptions;

            SetYouTubeCookiesPath(
                cookiesPath);

            //
            // Start with Android. Play() may select PO-token mode immediately
            // before loading a particular YouTube video.
            //
            _ = mpv_set_option_string(
                _mpvCtx,
                "ytdl-raw-options",
                _androidYtdlRawOptions);

            _ = mpv_set_option_string(
                _mpvCtx,
                "idle",
                "yes");
			_ = mpv_set_option_string(_mpvCtx, "keep-open", "yes");

            // Default live playback starts at the newest available segment.
            // Browser broadcasts select a safer starting margin in Play().
            _ = mpv_set_option_string(
                _mpvCtx,
                "demuxer-lavf-o",
                "live_start_index=-1");

            // Measure the actual decoded audio level.
            //
            // "alphavol" is the filter label used later with
            // af-metadata/alphavol/... to retrieve RMS volume.
            //
            // reset=1 makes astats calculate a fresh value for each
            // incoming audio frame instead of accumulating statistics
            // across the entire track.
            if (!auxiliaryAudioOnly) _ = mpv_set_option_string(
                _mpvCtx,
                "af",
                AudioLevelFilter);

            // Do not inherit backend-dependent defaults or disable verification on Wine.
            var tlsOptionResult = mpv_set_option_string(_mpvCtx, "tls-verify", "yes");
            if (tlsOptionResult < 0) throw new InvalidOperationException("MPV could not enable TLS certificate verification.");
            if (mpv_set_option_string(_mpvCtx, "tls-ca-file", AlphaChannel.Media.MediaTlsTrust.BundlePath) < 0)
                throw new InvalidOperationException("MPV could not configure trusted media certificates.");
			_ = mpv_request_log_messages(_mpvCtx, "warn");
            var initializeResult = mpv_initialize(_mpvCtx);
            if (initializeResult < 0) throw new InvalidOperationException($"MPV initialization failed: rc={initializeResult}.");

            // Activity music and effects have no picture or level meter. They
            // need neither a libmpv video context nor its native frame buffers.
            if (!auxiliaryAudioOnly)
            {
			nint apiTypePtr = Marshal.StringToHGlobalAnsi("sw");

			IntPtr paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvRenderParam>() * 2);
			Marshal.StructureToPtr(new MpvRenderParam { Type = 1, Data = apiTypePtr }, paramsPtr, false);
			Marshal.StructureToPtr(new MpvRenderParam { Type = 0, Data = IntPtr.Zero }, paramsPtr + 16, false);

			int rc = mpv_render_context_create(ref _mpvRenderCtx, _mpvCtx, paramsPtr);

			Marshal.FreeHGlobal(apiTypePtr);
			Marshal.FreeHGlobal(paramsPtr);
            if (rc < 0 || _mpvRenderCtx == IntPtr.Zero)
                throw new InvalidOperationException($"MPV could not create its render context: rc={rc}.");

			_sizePtr = Marshal.AllocHGlobal(8);
			Marshal.WriteInt32(_sizePtr, _width);
			Marshal.WriteInt32(_sizePtr + 4, _height);

			_stridePtr = Marshal.AllocHGlobal(IntPtr.Size);
			Marshal.WriteIntPtr(_stridePtr, new IntPtr(_width * 4));

			_formatPtr = Marshal.StringToHGlobalAnsi("bgra");

			_renderParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvRenderParam>() * 5);
			Marshal.StructureToPtr(new MpvRenderParam { Type = 17, Data = _sizePtr }, _renderParamsPtr, false);
			Marshal.StructureToPtr(new MpvRenderParam { Type = 18, Data = _formatPtr }, _renderParamsPtr + 16, false);
			Marshal.StructureToPtr(new MpvRenderParam { Type = 19, Data = _stridePtr }, _renderParamsPtr + 32, false);
			Marshal.StructureToPtr(new MpvRenderParam { Type = 20, Data = _bufferPtr }, _renderParamsPtr + 48, false);
			Marshal.StructureToPtr(new MpvRenderParam { Type = 0, Data = IntPtr.Zero }, _renderParamsPtr + 64, false);

			_updateCallback = (ctx) => _frameReady.Set();
			_updateCallbackHandle = GCHandle.Alloc(_updateCallback);
			mpv_render_context_set_update_callback(_mpvRenderCtx, _updateCallback, IntPtr.Zero);
            }

			_eventThread = new Thread(EventLoop)
			{
				IsBackground = true,
				Name = "mpv-events"
			};

			_closed = false;
			_eventThread.Start();

			
            }
		}

       

        public bool RenderFrame()
		{
			try
			{
				_frameReady.Wait();
				_frameReady.Reset();
			}
			catch
			{
				
				return false;
			}
			if (_closed || _cancelToken.IsCancellationRequested)
			{  return false; }

            // Everything below touches state (_mpvRenderCtx, _bufferPtr, _snapA/_snapB,
            // _targetTexture) that StopRender frees/nulls under the same _renderLock.
            // Native rendering and teardown use _renderLock. Snapshot publication,
            // uploads and snapshot-buffer destruction use _frameUploadLock, so
            // Present never waits for the native software-render call.
            //
            // This is a separate lock from _mpvLock (which guards _mpvCtx command/property
            // calls) on purpose - RenderFrame runs once per mpv frame and holds this for
            // the actual native render+copy, so sharing it with _mpvLock would make every
            // UI-thread property poll (GetProperties, IsEofReached, ...) queue up behind
            // that native call and stall the game's own frame rate.
            lock (_renderLock)
			{
                if (_closed || _mpvRenderCtx == IntPtr.Zero)
				{
					return false;
				}

				ulong flags = mpv_render_context_update(_mpvRenderCtx);
				var refresh = Interlocked.Exchange(ref forceVisualRefresh,0) != 0;
				if ((flags & 1) == 0 && !refresh)
				{
					return true;
				}

                if (!frameConsumersVisible) return SkipHiddenFrame();
                UpdateRenderOutput();
                var renderedLoad = Volatile.Read(ref audioDiagnosticLoad);
				try
				{
                    var renderStarted = System.Diagnostics.Stopwatch.GetTimestamp();
					int rc = mpv_render_context_render(_mpvRenderCtx, _renderParamsPtr);
                    PlaybackRenderDiagnostics.RecordNativeRender(System.Diagnostics.Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds);

					if (_closed || _cancelToken.IsCancellationRequested)
					{
						return false;
					}

					if (rc == 0 && _bufferPtr != IntPtr.Zero &&
						_snapA != IntPtr.Zero && _snapB != IntPtr.Zero)
					{
                        lock (_frameUploadLock)
                        {
						if (_closed || _cancelToken.IsCancellationRequested) return false;
						IntPtr snapshot = _useSnapA ? _snapA : _snapB;
						_useSnapA = !_useSnapA;

						unsafe
						{
							System.Buffer.MemoryCopy((void*)_bufferPtr, (void*)snapshot, _frameBytes, _frameBytes);
						}

                        _latestSnapshot = snapshot;

						Texture2D? texture = _targetTexture;
						int width = _width;
                        int outputGeneration = _outputGeneration;
                        if (texture is not null)
                            DxHandler.RunOnRenderThread(RenderKey, () => UploadFrame(texture, snapshot, width, outputGeneration));
                        }

                        if (_audioSpectrumEnabled)
                        {
                            Interlocked.Increment(ref _visualizerFrames);
                            Interlocked.Add(ref _visualizerRenderTicks, System.Diagnostics.Stopwatch.GetTimestamp() - renderStarted);
                        }
                        if (renderedLoad == Volatile.Read(ref audioDiagnosticLoad) &&
                            renderedLoad == Volatile.Read(ref mediaLoadedDiagnosticLoad))
                        {
                            Volatile.Write(ref videoFrameLoad, renderedLoad);
                            OnFrameRendered?.Invoke();
                        }

                        return true;
                    }
					else
					{
						AepLog.Warning("[MPV] Error rendering frame: RC: [redacted] Texture.");
					}
				}
				catch (Exception)
				{
					AepLog.Warning("[MPV] Error rendering frame: [redacted] [redacted]");
				}
				return false;
			}
		}
        // Matches the loopback URL generated by StudioPreview for its PCM feed.
        internal static bool IsStudioPreview(Uri? uri) =>
            uri is { Scheme: "http", Host: "127.0.0.1", IsDefaultPort: false, Query.Length: 0, UserInfo.Length: 0 } &&
            uri.AbsolutePath is { Length: 37 } path &&
            path.EndsWith(".wav", StringComparison.Ordinal) &&
            Guid.TryParseExact(path.AsSpan(1, 32), "N", out _);

        private void UploadFrame(Texture2D texture, IntPtr snapshot, int width, int outputGeneration)
        {
            // Only publication/copy/freeing use this short lock. Never make
            // Present wait for the native MPV software-rendering call.
            // Shutdown and device failure must not submit new video uploads.
            // Losing window focus leaves playback and rendering enabled.
            if (_closed || !frameConsumersVisible || !DxHandler.CanUploadVideoFrames) return;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (_frameUploadLock)
            {
                if (_closed || _targetTexture != texture || _outputGeneration != outputGeneration) return;
                var acquired = System.Diagnostics.Stopwatch.GetTimestamp();
                DxHandler.Device?.ImmediateContext.UpdateSubresource(texture, 0, null, snapshot, width * 4, 0);
                PlaybackRenderDiagnostics.RecordUpload(
                    System.Diagnostics.Stopwatch.GetElapsedTime(started, acquired).TotalMilliseconds,
                    System.Diagnostics.Stopwatch.GetElapsedTime(acquired).TotalMilliseconds);
            }
        }
        // Called only under _renderLock, never while holding _mpvLock. Resize
        // the software-render output itself, not just FFmpeg's input frames.
        private void UpdateRenderOutput()
        {
            var reduced = _audioSpectrumEnabled &&
                (_visualizerTargetTexture is not null || _videoTargetTexture is null);
            var width = reduced ? AudioVisualizerFilter.FrameWidth : _videoWidth;
            var height = reduced ? AudioVisualizerFilter.FrameHeight : _videoHeight;
            if (_width == width && _height == height) return;

            var bytes = checked(width * height * 4);
            IntPtr buffer = IntPtr.Zero, snapA = IntPtr.Zero, snapB = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal(bytes);
                snapA = Marshal.AllocHGlobal(bytes);
                snapB = Marshal.AllocHGlobal(bytes);
            }
            catch
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (snapA != IntPtr.Zero) Marshal.FreeHGlobal(snapA);
                if (snapB != IntPtr.Zero) Marshal.FreeHGlobal(snapB);
                throw;
            }

            lock (_frameUploadLock)
            {
            DxHandler.CancelRenderThreadWork(RenderKey);
            _outputGeneration++; // Also invalidates an upload already picked up by Present.
            _latestSnapshot = IntPtr.Zero;
            Marshal.FreeHGlobal(_bufferPtr);
            Marshal.FreeHGlobal(_snapA);
            Marshal.FreeHGlobal(_snapB);
            _bufferPtr = buffer;
            _snapA = snapA;
            _snapB = snapB;
            _width = width;
            _height = height;
            _frameBytes = bytes;
            _targetTexture = reduced ? _visualizerTargetTexture : _videoTargetTexture;
            Marshal.WriteInt32(_sizePtr, width);
            Marshal.WriteInt32(_sizePtr + 4, height);
            Marshal.WriteIntPtr(_stridePtr, new IntPtr(width * 4));
            Marshal.StructureToPtr(new MpvRenderParam { Type = 20, Data = buffer }, _renderParamsPtr + 48, false);
            }
        }

        internal bool IsClosed => _closed || Volatile.Read(ref _stopRenderStarted) != 0;
		private readonly Lock _mpvLock = new();
		// Guards _mpvRenderCtx/_bufferPtr/_snapA/_snapB/_targetTexture specifically - see the
		// comment in RenderFrame for why this is kept separate from _mpvLock.
		private readonly Lock _renderLock = new();
        // Lock order: _renderLock -> _frameUploadLock. Present and snapshot
        // readers take only _frameUploadLock; no native render call holds it.
        private readonly Lock _frameUploadLock = new();
        internal void SetYouTubeCookiesPath(
            string? cookiesPath)
        {
            _youTubeCookiesPath = cookiesPath;
            var baseOptions =
                _poTokenYtdlRawOptions;

            _authenticatedPoTokenYtdlRawOptions =
                !string.IsNullOrWhiteSpace(cookiesPath) &&
                File.Exists(cookiesPath)
                    ? baseOptions +
                      ",cookies=" +
                      cookiesPath.Replace('\\', '/')
                    : baseOptions;
        }

        private string? _youTubeCookiesPath;

        public void Play(
            string url,
            double playbackPosition,
            bool isPlaying,
            bool useYouTubePoTokens = false,
            bool useYouTubeCookies = false,
            AudioVisualizerMode? preparedVisualizerMode = null,
            AudioVisualizerTheme preparedVisualizerTheme =
                AudioVisualizerTheme.AlphaPurple,
            bool expectedAudioOnly = false,
            bool bufferBrowserInitially = false,
            IReadOnlyDictionary<string, string>? resolvedHttpHeaders = null,
            bool youtubePlayback = false)
        {
            InvalidateProperties();
            if (_closed)
            {
                return;
            }

            _smoothedAudioLevel = 0f;

            

            lock (_mpvLock)
            {
                if (_closed || _mpvCtx == IntPtr.Zero) return;
                if (!ResolvedMediaRequest.ApplyHeaders(resolvedHttpHeaders, args => mpv_command(_mpvCtx, args)))
                    throw new InvalidOperationException("Could not configure media request headers.");
                ResetAudioTracksForLoad(url);
                _pendingArchiveSubtitles.Clear();
                _liveAudioRelay?.Dispose();
                _liveAudioRelay = null;

                Uri.TryCreate(url, UriKind.Absolute, out var audioUri);
                _liveAudioPlayback = expectedAudioOnly && audioUri?.Scheme is "http" or "https";
                var playbackUrl = url;

                // Studio's private preview already streams PCM WAV. Opening it
                // directly avoids a second decoder and loopback connection.
                if (expectedAudioOnly &&
                    !IsStudioPreview(audioUri) &&
                    _resources?.GetLocationFFmpeg() is { } ffmpegPath)
                {
                    _liveAudioRelay =
                        LiveAudioFfmpegRelay.TryStart(
                            ffmpegPath,
                            url,
                            AlphaChannel.Plugin.Net.PluginHttpClients.CreateProbeClient);

                    if (_liveAudioRelay is not null)
                    {
                        playbackUrl =
                            _liveAudioRelay.PlaybackUrl;
                    }
                    else
                    {
                        AepLog.Warning(
                            "[LIVE-AUDIO-RELAY] Falling back to direct libmpv playback.");
                    }
                }

                //
                // When switching from video to an audio stream, install the
                // requested visualizer before loadfile. This lets mpv build
                // the new media pipeline with a video output from the start,
                // instead of leaving the previous video's final frame visible.
                //
                // A saved radio visualizer selection is not evidence that the
                // next file is audio-only. Installing its graph on a video can
                // replace the real video output and leave the TV loading.
                if (expectedAudioOnly && preparedVisualizerMode is { } visualizerMode &&
                    visualizerMode != AudioVisualizerMode.ClassicBars &&
                    _mpvCtx != IntPtr.Zero)
                {
                    var graph =
                        AudioVisualizerFilter.GetGraph(
                            visualizerMode,
                            preparedVisualizerTheme, _localVisualizerQuality);

                    var visualizerResult =
                        mpv_command(
                            _mpvCtx,
                            [
                                "set",
                                "lavfi-complex",
                                graph,
                                null!
                            ]);

                    if (visualizerResult < 0)
                    {
                        _audioSpectrumEnabled =
                            false;

                        AepLog.Warning(
                            "[AudioVisualizer] Could not prepare the [redacted] graph before loading media: rc=[redacted]");
                    }
                    else
                    {
                        _audioSpectrumEnabled =
                            true;

                        _lavfiVisualizerGraphInstalled =
                            true;
                        _installedVisualizerGraph = graph;
                        _adaptiveMode = visualizerMode;
                        _adaptiveTheme = preparedVisualizerTheme;
                        ResetVisualizerMeasurement();

                        
                    }
                }
                else if (_lavfiVisualizerGraphInstalled &&
                         _mpvCtx != IntPtr.Zero)
                {
                    //
                    // The next item is an ordinary video or uses Classic Bars.
                    // Remove any FFmpeg video-generating graph left by the
                    // previous audio stream.
                    //
                    var clearResult =
                        mpv_command(
                            _mpvCtx,
                            [
                                "set",
                                "lavfi-complex",
                                string.Empty,
                                null!
                            ]);

                    if (clearResult < 0)
                    {
                        AepLog.Warning(
                            "[AudioVisualizer] Could not clear the previous spectrum graph: rc=[redacted]");
                    }

                    _audioSpectrumEnabled = false;
                    _lavfiVisualizerGraphInstalled =
                        clearResult < 0;
                    if (clearResult >= 0) _installedVisualizerGraph = null;
                }

                if (url == string.Empty)
                {
                    Stop();
                    return;
                }

                //
                // Select the yt-dlp route immediately before loadfile.
                //
#if DEBUG
                playbackUrl = LiveBroadcastStream.DebugPlaybackUrl(playbackUrl);
#endif
                var isAlphaChannelLiveHls = LiveBroadcastStream.TryParse(playbackUrl, out var mediaUri);
                _liveUsesTls = isAlphaChannelLiveHls && mediaUri!.Scheme == "https";

                _diagnoseLivePlayback = isAlphaChannelLiveHls;
                ResetAudioDiagnostics(url, youtubePlayback, paused: !isPlaying);
                if (!YouTubeHttpPlaybackPolicy.Apply(audioDiagnosticSource == PlaybackSourceKind.YouTube,
                        args => mpv_command(_mpvCtx, args)))
                    AepLog.Warning("[YouTube] Could not configure bounded media requests on this player build.");
                diagnosticPlaybackRoute = youtubePlayback ? PlaybackAttemptRoute.ResolvedUrl : audioDiagnosticSource == PlaybackSourceKind.YouTube
                    ? useYouTubePoTokens ? PlaybackAttemptRoute.PoToken : PlaybackAttemptRoute.Android
                    : PlaybackAttemptRoute.Native;
                diagnosticAccount = useYouTubeCookies;
                AepLog.PlaybackAttempt(new(PlaybackAttemptStage.LoadRequested, diagnosticPlaybackRoute,
                    0, audioDiagnosticId, audioDiagnosticLoad, diagnosticAccount, null,
                    audioDiagnosticLoad > 1, false));
                _nextLiveSampleUtc = DateTime.UtcNow.AddSeconds(1);
                _nextLiveLogUtc = DateTime.UtcNow.AddSeconds(10);
                _liveBufferingSamples = 0;
                _liveEofReported = _liveFileLoaded = false;

                // A two-segment live start could drain below half a second
                // and repeatedly rebuffer before gradually settling. Ordinary
                // broadcasts need a complete reserve before their first frame.
                // Interactive game and bounded browser startup retain their policy.
                var buffering = LiveBroadcastBufferPolicy.For(playbackUrl,
                    Plugin.Cfg.LiveBroadcastExtraBufferSeconds, bufferBrowserInitially);
                var liveBufferOptions = buffering.LoadOptions;
                _ = mpv_command(_mpvCtx, ["set", "demuxer-lavf-o",
                    "live_start_index=" + buffering.StartIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), null!]);

                // A temporarily missing live playlist must fail promptly so
                // the Watch Party retry loop can try it again. Letting
                // ytdl_hook inspect a relay URL turns a simple startup 404 into
                // a long generic webpage lookup and prevents timely retries.
                var directLiveMedia =
                    isAlphaChannelLiveHls ||
                    expectedAudioOnly;

                var ytdlResult =
                    mpv_command(
                        _mpvCtx,
                        [
                            "set",
                            "ytdl",
                            directLiveMedia ? "no" : "yes",
                            null!
                        ]);

                if (ytdlResult < 0)
                {
                    AepLog.Warning(
                        "[MPV] Could not configure yt-dlp for this media: rc=[redacted]");
                }

                if (useYouTubePoTokens && _resources is not null)
                {
                    // A renderer may have been initialized before the background
                    // installer finished. Never reuse cached Android options here.
                    _poTokenYtdlRawOptions = YouTubePoTokenSupport.BuildMpvRawOptions(_resources, true);
                    SetYouTubeCookiesPath(_youTubeCookiesPath);
                }

                var selectedYtdlOptions =
                    useYouTubePoTokens && useYouTubeCookies
                        ? _authenticatedPoTokenYtdlRawOptions
                        : useYouTubePoTokens
                        ? _poTokenYtdlRawOptions
                        : _androidYtdlRawOptions;

                if (VideoUrlResolver.IsYouTubeUrl(playbackUrl))
                {
                    // With --sub-langs=all, yt-dlp exposes every YouTube
                    // machine translation as an external MPV subtitle track.
                    // On some videos this is 150+ tracks and starts many MPV
                    // demux workers. The installed yt-dlp's
                    // skip=translated_subs extractor argument does not trim
                    // requested_subtitles, so request only the original ASR
                    // track until caption discovery can select uploaded and
                    // original tracks separately.
                    selectedYtdlOptions +=
                        ",write-auto-subs=,sub-langs=.*-orig";
                    _ = mpv_command(_mpvCtx, ["set", "sid", "no", null!]);
                }
                else if (VideoUrlResolver.IsDailymotionUrl(playbackUrl))
                {
                    // Dailymotion supplies caption files in yt-dlp's subtitles
                    // metadata, including some automatically generated tracks.
                    selectedYtdlOptions += ",write-subs=,sub-langs=all";
                    _ = mpv_command(_mpvCtx, ["set", "sid", "no", null!]);
                }

                var optionsResult = directLiveMedia
                    ? 0
                    :
                    mpv_command(
                        _mpvCtx,
                        [
                            "set",
                            "ytdl-raw-options",
                            selectedYtdlOptions,
                            null!
                        ]);

                if (optionsResult < 0)
                {
                    AepLog.Warning(
                        "[YouTube/Policy] Could not change mpv's yt-dlp configuration: rc=[redacted]");
                }
                else if (!directLiveMedia)
                {
                    
                }

                if (playbackPosition > 0 && !_liveAudioPlayback)
                {
                    var startStr =
                        ((int)playbackPosition).ToString(
                            System.Globalization.CultureInfo.InvariantCulture);

                    var pauseStr =
                        !isPlaying
                            ? ",pause=yes"
                            : string.Empty;

                    _ = mpv_command(
                        _mpvCtx,
                        [
                            "loadfile",
                            playbackUrl,
                            "replace",
                            "0",
                            $"start={startStr}{pauseStr},{liveBufferOptions}",
                            null!
                        ]);
                }
                else if (!isPlaying)
                {
                    _ = mpv_command(
                        _mpvCtx,
                        [
                            "loadfile",
                            playbackUrl,
                            "replace",
                            "0",
                            "pause=yes," + liveBufferOptions,
                            null!
                        ]);
                }
                else
                {
                    _ = mpv_command(
                        _mpvCtx,
                        [
                            "loadfile",
                            playbackUrl,
                            "replace",
                            "0",
                            liveBufferOptions,
                            null!
                        ]);
                }
            }
        }

        public void SetFileLoop(bool loop)
        {
            if (_closed) return;
            lock (_mpvLock)
            {
                if (_mpvCtx != IntPtr.Zero)
                    SetPlaybackOption("loop-file", loop ? "inf" : "no");
            }
        }

        private void SetPlaybackOption(
            string name,
            string value)
        {
            var result =
                mpv_command(
                    _mpvCtx,
                    [
                        "set",
                        name,
                        value,
                        null!
                    ]);

            if (result < 0)
            {
                AepLog.Warning(
                    "[MPV] Could not set [redacted]=[redacted]: rc=[redacted]");
            }
        }

        public void Stop()
		{
			if (!_closed)
			{
				lock (_mpvLock)
				{
					if (_closed || _mpvCtx == IntPtr.Zero) return;
					_pendingArchiveSubtitles.Clear();
					propertySnapshot = null; pausedSnapshot = null;
                    _ = mpv_command(_mpvCtx, ["stop", null!]);
					_liveAudioRelay?.Dispose();
					_liveAudioRelay = null;
					_closed = true;
					_frameReady?.Set();
				}
			}
		}

		public bool GetPaused()
		{
			if (_closed)
			{
				return true;
			}

			lock (_mpvLock)
			{
				if (_mpvCtx == IntPtr.Zero)
				{
					return true;
				}

                if (pausedSnapshot is { } cached && propertySnapshot is not null && propertySnapshotEpoch == Volatile.Read(ref propertiesEpoch) &&
                    System.Diagnostics.Stopwatch.GetTimestamp() - propertySnapshotAt < System.Diagnostics.Stopwatch.Frequency / 30) return cached;
                unsafe
                {
                    int paused = 1;
                    var result = mpv_get_property(_mpvCtx, "pause", 3, (IntPtr)(&paused));
                    return (pausedSnapshot = result < 0 || paused == 1).Value;
                }
			}
		}
		// A managed copy of the latest decoded frame - for CPU-side consumers (the local-video broadcaster
		// and the plain screen-window fallback) alongside the GPU texture upload RenderFrame
		// already does. Not on the hot path: only copied when actually asked for.
		public byte[]? TryGetFrame(out int width, out int height)
		{
			lock (_frameUploadLock)
			{
                width = _width;
                height = _height;
				if (_closed || _latestSnapshot == IntPtr.Zero || _frameBytes == 0)
				{
					return null;
				}

				var frame = new byte[_frameBytes];
				Marshal.Copy(_latestSnapshot, frame, 0, _frameBytes);
				return frame;
			}
		}

        private double[]? propertySnapshot;
        private long propertySnapshotAt;
        private bool? pausedSnapshot;
        private int propertiesEpoch, propertySnapshotEpoch;
        internal void InvalidateProperties() => Interlocked.Increment(ref propertiesEpoch);

		public double[] GetProperties()
		{
			if (_closed)
			{
				return [0, 0, 100, 0, 0];
			}

			lock (_mpvLock)
			{
				if (_mpvCtx == IntPtr.Zero)
				{
					return [0, 0, 100, 0, 0];
				}

                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                var epoch = Volatile.Read(ref propertiesEpoch);
                if (propertySnapshot is not null && propertySnapshotEpoch == epoch && now - propertySnapshotAt < System.Diagnostics.Stopwatch.Frequency / 30)
                    return propertySnapshot;
				_ = mpv_get_property(_mpvCtx, "time-pos", 5, out double position);
				_ = mpv_get_property(_mpvCtx, "duration", 5, out double duration);
				_ = mpv_get_property(_mpvCtx, "volume", 5, out double volume);
				// dwidth/dheight are the stream's actual decoded resolution (post yt-dlp format
				// selection), not this renderer's fixed offscreen texture size - what the Player
				// tab shows so players can tell what quality they're actually getting.
				_ = mpv_get_property(_mpvCtx, "dwidth", 5, out double streamWidth);
				_ = mpv_get_property(_mpvCtx, "dheight", 5, out double streamHeight);
				pausedSnapshot = null;
                propertySnapshotEpoch = epoch;
                propertySnapshotAt = now;
                return propertySnapshot = [position, duration, volume, streamWidth, streamHeight];
			}
		}

		public void Pause(bool pause)
		{
			if (!_closed)
			{
				lock (_mpvLock)
				{
					if (_closed || _mpvCtx == IntPtr.Zero) return;
					propertySnapshot = null; pausedSnapshot = null;
                    youtubeAccessRecovery.SetPaused(pause, DateTime.UtcNow);
                    _ = mpv_command(_mpvCtx, ["set", "pause", pause ? "yes" : "no", null!]);
				}
			}
		}
		
		public void SetVolume(int volume)
		{
			if (!_closed)
			{
				lock (_mpvLock)
				{
					if (_closed || _mpvCtx == IntPtr.Zero) return;
					propertySnapshot = null; pausedSnapshot = null;
                    _ = mpv_command(_mpvCtx, ["set", "volume", volume.ToString(System.Globalization.CultureInfo.InvariantCulture), null!]);
				}
			}
		}

		public void SetMuted(bool muted)
		{
			if (!_closed)
			{
				lock (_mpvLock)
				{
					if (_closed || _mpvCtx == IntPtr.Zero) return;
					// mpv's mute property silences the audio device without reducing the
					// decoded signal used by astats and the audio visualizer filters.
                    var result = mpv_command(_mpvCtx, ["set", "mute", muted ? "yes" : "no", null!]);
                    if (result < 0)
                        AepLog.Warning("[MPV] Output mute change rejected: muted=[redacted], rc=[redacted]");
				}
			}
		}

		public void Seek(int seconds)
		{
			if (_closed)
			{
				
				return;
			}

			lock (_mpvLock)
			{
				if (_mpvCtx == IntPtr.Zero)
				{
					
					return;
				}

				if (_liveAudioPlayback) return;

				propertySnapshot = null; pausedSnapshot = null;
                    int rc = mpv_command(_mpvCtx, ["seek", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute", null!]);
				if (rc < 0)
				{
					AepLog.Warning("[MPV] Seek to [redacted]s failed: rc=[redacted]");
				}
			}
		}

        public bool HasVideoTrack()
        {
            if (_closed)
            {
                return false;
            }

            lock (_mpvLock)
            {
                if (_mpvCtx == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr ptr =
                    mpv_get_property_string(
                        _mpvCtx,
                        "vid");

                if (ptr == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    var value =
                        Marshal.PtrToStringUTF8(ptr);

                    return !string.IsNullOrWhiteSpace(value) &&
                           !value.Equals(
                               "no",
                               StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    mpv_free(ptr);
                }
            }
        }

        public bool HasAudioTrack()
        {
            if (_closed)
            {
                return false;
            }

            lock (_mpvLock)
            {
                if (_mpvCtx == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr ptr =
                    mpv_get_property_string(
                        _mpvCtx,
                        "aid");

                if (ptr == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    var value =
                        Marshal.PtrToStringUTF8(ptr);

                    return !string.IsNullOrWhiteSpace(value) &&
                           !value.Equals(
                               "no",
                               StringComparison.OrdinalIgnoreCase);
                }
                finally
                {
                    mpv_free(ptr);
                }
            }
        }

        public bool SetAudioSpectrumEnabled(
            bool enabled,
            AudioVisualizerMode mode =
                AudioVisualizerMode.FfmpegSpectrum,
            AudioVisualizerTheme theme =
                AudioVisualizerTheme.AlphaPurple)
        {
            if (_closed)
            {
                return false;
            }

            lock (_mpvLock)
            {
                if (_mpvCtx == IntPtr.Zero)
                {
                    return false;
                }

                if (!enabled && !_lavfiVisualizerGraphInstalled)
                {
                    _audioSpectrumEnabled = false;
                    return true;
                }

                _adaptiveMode = mode;
                _adaptiveTheme = theme;

                // Keep [ao] connected for live radio, but remove the spectrum,
                // waveform and other video work when returning to Classic Bars.
                var graph = !enabled
                    ? AudioVisualizerFilter.AudioPassthroughGraph
                    : AudioVisualizerFilter.GetGraph(
                        mode,
                        theme, _localVisualizerQuality);

                // Re-applying an identical graph rebuilds MPV's live pipeline
                // and can provoke unnecessary seeks or demuxer reconnects.
                if (string.Equals(_installedVisualizerGraph, graph, StringComparison.Ordinal))
                {
                    _audioSpectrumEnabled = enabled;
                    return true;
                }
                var result =
                    mpv_command(
                        _mpvCtx,
                        [
                            "set",
                    "lavfi-complex",
                    graph,
                    null!
                        ]);

                if (result < 0)
                {
                    _audioSpectrumEnabled =
                        false;

                    AepLog.Warning(
                        "[AudioVisualizer] MPV rejected the [redacted] graph: rc=[redacted]");

                    return false;
                }

                //
                _audioSpectrumEnabled = enabled;
                _lavfiVisualizerGraphInstalled = true;
                _installedVisualizerGraph = graph;
                ResetVisualizerMeasurement();

                return true;
            }
        }

        public float GetAudioLevel()
        {
            if (_closed)
            {
                return 0f;
            }

            lock (_mpvLock)
            {
                if (_mpvCtx == IntPtr.Zero)
                {
                    return 0f;
                }

                IntPtr ptr =
                    mpv_get_property_string(
                        _mpvCtx,
                        "af-metadata/alphavol/by-key/lavfi.astats.Overall.RMS_level");

                if (ptr == IntPtr.Zero)
                {
                    // Metadata may not have arrived yet.
                    // Let the previous value decay naturally instead
                    // of snapping the visualizer instantly to zero.
                    _smoothedAudioLevel *= 0.88f;

                    return _smoothedAudioLevel;
                }

                try
                {
                    string? value =
                        Marshal.PtrToStringUTF8(ptr);

                    if (string.IsNullOrWhiteSpace(value) ||
                        value.Equals(
                            "-inf",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _smoothedAudioLevel *= 0.82f;

                        return _smoothedAudioLevel;
                    }

                    if (!double.TryParse(
                            value,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double db))
                    {
                        return _smoothedAudioLevel;
                    }

                    //
                    // astats returns RMS in dB.
                    //
                    // For the visualizer:
                    //
                    // -60 dB = effectively silent
                    //   0 dB = maximum
                    //
                    float target =
                        (float)Math.Clamp(
                            (db + 60.0) / 60.0,
                            0.0,
                            1.0);

                    //
                    // Fast attack, slower release.
                    //
                    // Loud transients should make the bars jump quickly,
                    // while drops should fall smoothly rather than flicker.
                    //
                    float smoothing =
                        target > _smoothedAudioLevel
                            ? 0.45f
                            : 0.12f;

                    _smoothedAudioLevel +=
                        (target - _smoothedAudioLevel) *
                        smoothing;

                    return _smoothedAudioLevel;
                }
                finally
                {
                    mpv_free(ptr);
                }
            }
        }

        public string? GetMediaTitle()
		{
			if (_closed)
			{
				return null;
			}

			lock (_mpvLock)
			{
				if (_mpvCtx == IntPtr.Zero)
				{
					return null;
				}

				IntPtr ptr = mpv_get_property_string(_mpvCtx, "media-title");
				if (ptr != IntPtr.Zero)
				{
					try
					{
						return Marshal.PtrToStringUTF8(ptr);
					}
					finally
					{
						mpv_free(ptr);
					}
				}
				return null;
			}
		}

		public string? GetCurrentUrl()
		{
			if (_closed) return null;
			lock (_mpvLock)
			{
				return ReadPropertyString("path");
			}
		}

		private string? ReadPropertyString(string property)
		{
			if (_mpvCtx == IntPtr.Zero) return null;
			var ptr = mpv_get_property_string(_mpvCtx, property);
			if (ptr == IntPtr.Zero) return null;
			try { return Marshal.PtrToStringUTF8(ptr); }
			finally { mpv_free(ptr); }
		}

		public IReadOnlyList<SubtitleTrack> GetSubtitleTracks()
		{
			if (_closed) return [];
			lock (_mpvLock)
			{
				if (!int.TryParse(ReadPropertyString("track-list/count"), out var count)) return [];
				var tracks = new List<SubtitleTrack>();
				for (var i = 0; i < Math.Min(count, 128); i++)
				{
					var prefix = $"track-list/{i}/";
					if (ReadPropertyString(prefix + "type") != "sub" ||
					    !int.TryParse(ReadPropertyString(prefix + "id"), out var id)) continue;
					var ffIndex = int.TryParse(ReadPropertyString(prefix + "ff-index"),
                        out var parsedIndex) ? parsedIndex : (int?)null;
					tracks.Add(new SubtitleTrack(id,
					    ReadPropertyString(prefix + "lang") ?? string.Empty,
					    ReadPropertyString(prefix + "title") ?? string.Empty,
					    string.Equals(ReadPropertyString(prefix + "external"), "yes",
					        StringComparison.OrdinalIgnoreCase),
                        ffIndex, ReadPropertyString(prefix + "codec") ?? string.Empty));
				}
				return tracks;
			}
		}

		public bool SelectSubtitle(string? language, string? name)
		{
			if (_closed) return false;
			lock (_mpvLock)
			{
				if (_mpvCtx == IntPtr.Zero) return false;
				if (language is null)
					return mpv_command(_mpvCtx, ["set", "sid", "no", null!]) >= 0;
				var track = GetSubtitleTracks().FirstOrDefault(t =>
					string.Equals(t.Language, language, StringComparison.OrdinalIgnoreCase) &&
					string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
				if (track is null) return false;
				return mpv_command(_mpvCtx,
					["set", "sid", track.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), null!]) >= 0;
			}
		}

        public bool SelectSubtitleTrack(int id)
        {
            if (_closed) return false;
            lock (_mpvLock)
            {
                if (_mpvCtx == IntPtr.Zero || !GetSubtitleTracks().Any(track => track.Id == id))
                    return false;
                return mpv_command(_mpvCtx, ["set", "sid",
                    id.ToString(System.Globalization.CultureInfo.InvariantCulture), null!]) >= 0;
            }
        }

        public bool SelectArchiveSubtitle(InternetArchiveSubtitle track)
        {
            if (_closed) return false;
            lock (_mpvLock)
            {
                if (_mpvCtx == IntPtr.Zero) return false;
                var existing = GetSubtitleTracks().FirstOrDefault(candidate =>
                    string.Equals(candidate.Language, track.Language, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(candidate.Name, track.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    _pendingArchiveSubtitles.Remove(track.Url);
                    return mpv_command(_mpvCtx, ["set", "sid", existing.Id.ToString(
                        System.Globalization.CultureInfo.InvariantCulture), null!]) >= 0;
                }
                if (_pendingArchiveSubtitles.TryGetValue(track.Url, out var lastAttempt) &&
                    DateTime.UtcNow - lastAttempt < TimeSpan.FromSeconds(15))
                    return true;
                var added = mpv_command(_mpvCtx, ["sub-add", track.Url, "select", track.Name,
                    track.Language, null!]) >= 0;
                if (added) _pendingArchiveSubtitles[track.Url] = DateTime.UtcNow;
                return added;
            }
        }

		public string? GetSelectedSubtitleId()
		{
			if (_closed) return null;
			lock (_mpvLock)
            {
                // sid can remain "auto" when MPV picked an embedded default.
                // The current track reports the actual selected stream.
                return ReadPropertyString("current-tracks/sub/id") ??
                       ReadPropertyString("sid");
            }
		}

		public bool IsIdle()
		{
			if (_closed)
			{
				return true;
			}

			lock (_mpvLock)
			{
				if (_mpvCtx == IntPtr.Zero)
				{
					return true;
				}

				IntPtr ptr = Marshal.AllocHGlobal(4);
				try
				{
					int rc = mpv_get_property(_mpvCtx, "idle-active", 3, ptr);
					if (rc < 0)
					{
						return true;
					}

					return Marshal.ReadInt32(ptr) == 1;
				}
				finally
				{
					Marshal.FreeHGlobal(ptr);
				}
			}
		}

		public bool IsEofReached()
		{
			if (_closed)
			{
				return true;
			}

			lock (_mpvLock)
			{
				if (_mpvCtx == IntPtr.Zero)
				{
					return true;
				}

				IntPtr ptr = Marshal.AllocHGlobal(4);
				try
				{
					int rc = mpv_get_property(_mpvCtx, "eof-reached", 3, ptr);
					if (rc < 0)
					{
						return false;
					}

					return Marshal.ReadInt32(ptr) == 1;
				}
				finally
				{
					Marshal.FreeHGlobal(ptr);
				}
			}
		}

        public void Dispose()
        {
            StopRender();

            // A concurrent disposer may still be waking the render worker or
            // draining initialization's native update callback. Keep this managed
            // signal alive until those references are collected; disposing it here
            // could throw across an unmanaged callback. No WaitHandle is requested.

            GC.SuppressFinalize(this);
        }

        private void ReportLivePlaybackDiagnostics()
        {
            // Sample off the game/render threads and skip busy commands or teardown.
            if (!_mpvLock.TryEnter()) return;
            MpvLiveDiagnostic? diagnostic = null;
            Action<string>? ended = null;
            var observedLoad = 0;
            try
            {
                var now = DateTime.UtcNow;
                if (_closed || !_diagnoseLivePlayback || now < _nextLiveSampleUtc) return;
                observedLoad = audioDiagnosticLoad;
                _nextLiveSampleUtc = now.AddSeconds(1);
                var buffering = ReadPropertyString("paused-for-cache") ?? "n/a";
                if (buffering == "yes") _liveBufferingSamples++;
                // keep-open can retain EOF without emitting END_FILE. Wait
                // for FILE_LOADED so a replacement does not inherit old EOF.
                if (_liveFileLoaded && !_liveEofReported && DiagnosticFlag("eof-reached") == true)
                {
                    _liveEofReported = true;
                    ended = OnPlaybackFailed;
                }
                if (now >= _nextLiveLogUtc)
                {
                _nextLiveLogUtc = now.AddSeconds(10);
                diagnostic = new(audioDiagnosticId, audioDiagnosticLoad, _liveUsesTls, _liveBufferingSamples,
                    DiagnosticNumber("time-pos", 0, 604800), DiagnosticNumber("demuxer-cache-duration", 0, 604800),
                    DiagnosticNumber("video-pts", -60, 604800), DiagnosticNumber("audio-pts", -60, 604800), DiagnosticFlag("eof-reached"));
                _liveBufferingSamples = 0;
                }
            }
            finally { _mpvLock.Exit(); }
            if (diagnostic is not null)
            {
                PlaybackRenderDiagnostics.RecordLivePlayback(diagnostic.Format());
                AepLog.PlaybackLive(diagnostic);
            }
            if (!_closed && observedLoad == Volatile.Read(ref audioDiagnosticLoad))
                ended?.Invoke("Live broadcast ended; reconnecting to the publisher.");
        }

        private void EventLoop()
        {
            IntPtr context = _mpvCtx;
            


            try
            {
                while (!_closed)
                {
                    IntPtr ev =
                        mpv_wait_event(
                            context,
                            1);

                    if (_closed)
                    {
                        break;
                    }

                    ReportLivePlaybackDiagnostics();
                    ReportYouTubeAccessRecovery();
                    ReportAudioDiagnostics();
                    CheckVisualizerPerformance();


                    if (ev == IntPtr.Zero)
                    {
                        continue;
                    }


                    int eventId =
                        Marshal.ReadInt32(
                            ev);


                    switch (eventId)
                    {
                        //
                        // =====================================================
                        // Timeout
                        // =====================================================
                        //

                        case 0:
                            continue;


                        //
                        // =====================================================
                        // Shutdown
                        // =====================================================
                        //

                        case 1:
                            

                            return;


                        //
                        // =====================================================
                        // Log message
                        // =====================================================
                        //
                        // Warnings and errors are recorded for diagnostics.
                        //
                        // They DO NOT automatically mean the media failed.
                        // yt-dlp/mpv can emit warnings while still successfully
                        // continuing playback.
                        //

                        case 2:
                            {
                                IntPtr dataPtr =
                                    Marshal.ReadIntPtr(
                                        ev + 16);


                                if (dataPtr != IntPtr.Zero &&
                                    dataPtr.ToInt64() > 65536)
                                {
                                    string? prefix =
                                        Marshal.PtrToStringAnsi(
                                            Marshal.ReadIntPtr(
                                                dataPtr));


                                    string? level =
                                        Marshal.PtrToStringAnsi(
                                            Marshal.ReadIntPtr(
                                                dataPtr + 8));


                                    string? text =
                                        Marshal.PtrToStringAnsi(
                                            Marshal.ReadIntPtr(
                                                dataPtr + 16));


                                    if ((level == "error" ||
                                         level == "warn") &&
                                        text != null)
                                    {
                                        var message =
                                            text.Trim();

                                        // Some MediaMTX fMP4 segments repeat a
                                        // MOOV atom. FFmpeg skips it and playback
                                        // continues; reporting every segment twice
                                        // floods the plugin log.
                                        if (message.Contains("Found duplicated MOOV Atom. Skipped it",
                                                StringComparison.Ordinal))
                                            break;


                                        AepLog.MpvWarning(MpvWarningDiagnostic.Classify(audioDiagnosticId, audioDiagnosticSource, prefix, message, audioDiagnosticLoad));
                                        lock (_mpvLock)
                                        {
                                            audioDiagnosticWarningPending = true;
                                            if (audioDiagnosticSource == PlaybackSourceKind.YouTube)
                                                youtubeAccessRecovery.AccessDenied(PlaybackAccessDiagnostic.HttpStatus(message), DateTime.UtcNow);
                                        }

                                        OnError?.Invoke(
                                            message);
                                    }


                                    
                                }


                                break;
                            }


                        case 3:
                            

                            break;


                        case 4:
                            

                            break;


                        case 5:
                            

                            break;


                        //
                        // =====================================================
                        // Start file
                        // =====================================================
                        //

                        case 6:
                            

                            break;


                        //
                        // =====================================================
                        // End file
                        // =====================================================
                        //
                        // mpv_event_end_file begins with:
                        //
                        // int reason;
                        // int error;
                        //
                        // MPV_END_FILE_REASON_ERROR == 4.
                        //
                        // This is the authoritative signal that loading/playback
                        // actually failed.
                        //

                        case 7:
                            {
                                const int MpvEndFileReasonError =
                                    4;


                                IntPtr dataPtr =
                                    Marshal.ReadIntPtr(
                                        ev + 16);


                                if (dataPtr == IntPtr.Zero)
                                {
                                    AepLog.Warning(
                                        "[MPV] END_FILE received without event data.");

                                    break;
                                }


                                int reason =
                                    Marshal.ReadInt32(
                                        dataPtr);


                                int error =
                                    Marshal.ReadInt32(
                                        dataPtr + 4);


                                


                                if (reason ==
                                    MpvEndFileReasonError)
                                {
                                    var message =
                                        $"MPV playback failed " +
                                        $"(END_FILE error {error}).";


                                    AepLog.Warning(
                                        "[MPV] Definitive playback failure.");


                                    OnPlaybackFailed?.Invoke(
                                        message);
                                }


                                break;
                            }


                        //
                        // =====================================================
                        // File loaded
                        // =====================================================
                        //

                        case 8:
                            lock (_mpvLock)
                            {
                                _liveFileLoaded = true;
                                Volatile.Write(ref mediaLoadedDiagnosticLoad, audioDiagnosticLoad);
                                Volatile.Write(ref videoFrameLoad, 0);
                                youtubeAccessRecovery.MediaLoaded();
                            }
                            


                            AepLog.PlaybackAttempt(new(PlaybackAttemptStage.MediaLoaded, diagnosticPlaybackRoute,
                                0, audioDiagnosticId, audioDiagnosticLoad, diagnosticAccount, null, audioDiagnosticLoad > 1, true));
                            OnMediaLoaded?.Invoke();


                            break;


                        case 14:
                            

                            break;


                        case 15:
                            

                            break;


                        case 16:
                            

                            break;


                        case 17:
                            

                            break;


                        case 18:
                            

                            break;


                        case 19:
                            

                            break;


                        case 22:
                            

                            break;


                        default:
                            break;
                    }
                }
            }
            catch (Exception)
            {
                AepLog.Warning(
                    "[MPV] event loop crashed: [redacted] [redacted]");
            }
            finally
            {
                
            }
        }
    }
}
