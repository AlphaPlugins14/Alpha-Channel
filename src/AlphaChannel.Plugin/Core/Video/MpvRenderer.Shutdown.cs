using System.Runtime.InteropServices;

namespace AlphaChannel.Plugin.Video;

internal partial class MpvRenderer
{
        internal void RequestShutdown()
        {
            _closed = true;
            DxHandler.CancelRenderThreadWork(RenderKey);
            _frameReady.Set();
        }

        public void StopRender()
        {
            // Render-loop completion and plugin teardown can both dispose it.
            // Only one caller may wake/join/destroy the native event context.
            if (Interlocked.Exchange(ref _stopRenderStarted, 1) != 0) return;
            _closed = true;

            // Wake RenderFrame if it is currently blocked in _frameReady.Wait().
            try
            {
                _frameReady.Set();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by an earlier cleanup.
            }

            // This MUST be synchronous.
            //
            // RenderFrame queues UpdateSubresource callbacks while holding
            // _renderLock. Taking the same lock here guarantees that either:
            //
            // 1. RenderFrame finishes queueing first, then we cancel that work; or
            // 2. shutdown gets the lock first, after which RenderFrame sees
            //    _closed / the cleared render context and exits.
            //
            // Most importantly, StopRender cannot return while a queued GPU upload
            // still references the texture or native snapshot buffers.
            lock (_renderLock)
            {
                _closed = true; // Initialization may have finished while shutdown waited for this lock.
                DxHandler.CancelRenderThreadWork(RenderKey);
                if (_mpvRenderCtx != IntPtr.Zero)
                {
                    mpv_render_context_free(_mpvRenderCtx);
                    _mpvRenderCtx = IntPtr.Zero;
                }

                if (_updateCallbackHandle.IsAllocated)
                {
                    _updateCallbackHandle.Free();
                }

                if (_bufferPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_bufferPtr);
                    _bufferPtr = IntPtr.Zero;
                }

                lock (_frameUploadLock)
                {
                _latestSnapshot = IntPtr.Zero;
                if (_snapA != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_snapA);
                    _snapA = IntPtr.Zero;
                }

                if (_snapB != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_snapB);
                    _snapB = IntPtr.Zero;
                }
                _targetTexture = null;
                _videoTargetTexture = null;
                _visualizerTargetTexture = null;
                }

                if (_sizePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_sizePtr);
                    _sizePtr = IntPtr.Zero;
                }

                if (_stridePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_stridePtr);
                    _stridePtr = IntPtr.Zero;
                }

                if (_formatPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_formatPtr);
                    _formatPtr = IntPtr.Zero;
                }

                if (_renderParamsPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_renderParamsPtr);
                    _renderParamsPtr = IntPtr.Zero;
                }

            }

            // GPU uploads and buffers are detached synchronously above. Native

            // event shutdown may block indefinitely in a driver/decoder: finish

            // it off the framework thread, retaining this instance and its

            // native context until the event thread has actually exited.

            _ = Task.Run(() =>
            {
                try { FinishNativeShutdown(); }
                catch { AepLog.Warning("[MPV] Native shutdown failed; remaining native resources are retained."); }
            });

        }



        private void FinishNativeShutdown()

        {

            // The event thread may still be blocked inside mpv_wait_event. Keep the
            // native handle alive until it has left that call and finished processing.
            // Do not hold _mpvLock while joining: event handlers can call MPV APIs.
            if (_eventThread is not null &&
                _eventThread != Thread.CurrentThread)
            {
                if (_mpvCtx != IntPtr.Zero)
                {
                    mpv_wakeup(_mpvCtx);
                }

                _eventThread.Join();
            }

            _eventThread = null;

            lock (_mpvLock)
            {
                _audioSpectrumEnabled =
                    false;

                _lavfiVisualizerGraphInstalled =
                    false;
                _installedVisualizerGraph = null;

                if (_mpvCtx != IntPtr.Zero)
                {
                    mpv_terminate_destroy(_mpvCtx);
                    _mpvCtx = IntPtr.Zero;
                }

                _liveAudioRelay?.Dispose();
                _liveAudioRelay = null;
            }

        }

}
