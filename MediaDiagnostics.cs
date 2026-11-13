using System;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.Handler;

namespace Apolion
{
#if DEBUG
    /// <summary>
    /// Debug-build-only diagnostics for media playback (Instagram, YouTube, etc.).
    /// Everything in this file is compiled out of Release builds, so no extra
    /// logging or console hooks are present in shipped binaries.
    /// </summary>
    internal static class MediaDiagnostics
    {
        internal const string Prefix = "[MEDIA-DIAG]";

        /// <summary>
        /// Hooks console messages and probes codec support after each main-frame load.
        /// </summary>
        public static void Attach(IWebBrowser browser)
        {
            browser.ConsoleMessage += (_, e) =>
            {
                string message = e.Message ?? "";
                // Only surface our own markers; Instagram logs a lot of unrelated noise.
                if (message.StartsWith("[MEDIA", StringComparison.Ordinal))
                {
                    AppLogger.Info($"{Prefix} console {e.Source}:{e.Line} {message}");
                }
            };

            browser.FrameLoadEnd += (_, e) =>
            {
                if (!e.Frame.IsMain) return;

                _ = ProbeAsync(e.Frame, "load");

                // Video sites create their players after the load event, so probe
                // again once the page has had time to attach a <video> element.
                if (IsVideoSite(e.Frame.Url))
                {
                    _ = DelayedProbeAsync(e.Frame, "load+5s", 5000);
                    _ = DelayedProbeAsync(e.Frame, "load+12s", 12000);
                }
            };
        }

        private static bool IsVideoSite(string url) =>
            url.Contains("instagram.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("facebook.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("vimeo.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("twitter.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("x.com", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns a request handler that logs media downloads and media-CDN
        /// failures, or null when the request is unrelated. Debug builds only.
        /// </summary>
        public static IResourceRequestHandler? CreateRequestHandler(IRequest request)
        {
            try
            {
                if (request.ResourceType == ResourceType.Media)
                {
                    return new MediaResourceRequestHandler();
                }

                // Adaptive streaming (MSE/DASH) fetches segments via XHR. Watch
                // known video CDNs, but successful segment traffic is not logged;
                // only failures are (see MediaResourceRequestHandler).
                if (request.ResourceType == ResourceType.Xhr && IsMediaCdn(request.Url))
                {
                    return new MediaResourceRequestHandler();
                }
            }
            catch
            {
                // Diagnostics must never affect request handling.
            }

            return null;
        }

        private static bool IsMediaCdn(string? url)
        {
            if (string.IsNullOrEmpty(url)) return false;

            return url.Contains("cdninstagram.com", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("fbcdn.net", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("tiktokcdn.com", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("vimeocdn.com", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("video.twimg.com", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Installs error/playback observers in a frame before page scripts run.
        /// </summary>
        public static void ObserveFrame(IFrame frame)
        {
            try
            {
                frame.ExecuteJavaScriptAsync(ObserverScript);
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"{Prefix} observer injection skipped: {ex.Message}");
            }
        }

        private static async Task DelayedProbeAsync(IFrame frame, string phase, int delayMs)
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                await ProbeAsync(frame, phase).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"{Prefix} {phase} probe skipped: {ex.Message}");
            }
        }

        private static async Task ProbeAsync(IFrame frame, string phase)
        {
            try
            {
                var response = await frame.EvaluateScriptAsync(ProbeScript);
                if (response.Success && response.Result != null)
                {
                    AppLogger.Info($"{Prefix} {phase} {frame.Url} -> {response.Result}");
                }
                else
                {
                    AppLogger.Info($"{Prefix} {phase} {frame.Url} -> probe failed: {response.Message}");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"{Prefix} {phase} probe skipped: {ex.Message}");
            }
        }

        private const string ObserverScript = """
            (function () {
                try {
                    if (window.__apolionMediaObserverInstalled) return;
                    window.__apolionMediaObserverInstalled = true;

                    function isMedia(el) {
                        return el && (el.tagName === 'VIDEO' || el.tagName === 'AUDIO' || el.tagName === 'SOURCE');
                    }

                    function describe(el, phase, extra) {
                        var src = el.currentSrc || el.src || '';
                        if (!src && el.querySelector) {
                            var source = el.querySelector('source');
                            if (source) src = source.src || '';
                        }
                        return JSON.stringify({
                            phase: phase,
                            tag: el.tagName,
                            src: String(src).slice(0, 300),
                            paused: el.paused,
                            ended: el.ended,
                            currentTime: typeof el.currentTime === 'number' ? Math.round(el.currentTime * 100) / 100 : null,
                            duration: isFinite(el.duration) ? Math.round(el.duration * 100) / 100 : null,
                            code: el.error ? el.error.code : null,
                            message: el.error ? el.error.message : null,
                            networkState: el.networkState,
                            readyState: el.readyState,
                            detail: extra || null
                        });
                    }

                    function report(el, phase, extra) {
                        try {
                            console.log('[MEDIA-ERROR] ' + describe(el, phase, extra));
                        } catch (e) { }
                    }

                    function state(el, phase) {
                        try {
                            console.log('[MEDIA-STATE] ' + describe(el, phase));
                        } catch (e) { }
                    }

                    document.addEventListener('error', function (ev) {
                        if (isMedia(ev.target)) report(ev.target, 'error-event');
                    }, true);

                    document.addEventListener('stalled', function (ev) {
                        if (isMedia(ev.target)) report(ev.target, 'stalled');
                    }, true);

                    ['play', 'playing', 'pause', 'ended', 'waiting', 'loadedmetadata', 'canplay'].forEach(function (name) {
                        document.addEventListener(name, function (ev) {
                            if (isMedia(ev.target)) state(ev.target, name);
                        }, true);
                    });

                    // One playback heartbeat per second is enough to prove progress.
                    var lastHeartbeat = 0;
                    document.addEventListener('timeupdate', function (ev) {
                        var now = Date.now();
                        if (!isMedia(ev.target) || now - lastHeartbeat < 1000) return;
                        lastHeartbeat = now;
                        state(ev.target, 'timeupdate');
                    }, true);

                    var originalPlay = HTMLMediaElement.prototype.play;
                    HTMLMediaElement.prototype.play = function () {
                        var element = this;
                        var promise = originalPlay.apply(element, arguments);
                        if (promise && promise.catch) {
                            promise.catch(function (e) {
                                report(element, 'play-rejected', (e && e.name) + ': ' + (e && e.message));
                            });
                        }
                        return promise;
                    };

                    console.log('[MEDIA-OBSERVER] installed on ' + location.href);
                } catch (e) {
                    console.log('[MEDIA-OBSERVER] failed: ' + (e && e.message));
                }
            })();
            """;

        private const string ProbeScript = """
            (function () {
                try {
                    var types = {
                        h264_aac_mp4: 'video/mp4; codecs="avc1.42E01E,mp4a.40.2"',
                        h264_mp4: 'video/mp4; codecs="avc1.42E01E"',
                        aac_mp4: 'audio/mp4; codecs="mp4a.40.2"',
                        vp9_opus_webm: 'video/webm; codecs="vp9,opus"',
                        av1_mp4: 'video/mp4; codecs="av01.0.05M.08"'
                    };
                    var video = document.createElement('video');
                    var result = {
                        url: location.href,
                        ua: navigator.userAgent,
                        mse: typeof MediaSource !== 'undefined',
                        canPlayType: {},
                        isTypeSupported: {},
                        videos: []
                    };
                    for (var key in types) {
                        result.canPlayType[key] = video.canPlayType(types[key]) || 'no';
                        result.isTypeSupported[key] = result.mse ? MediaSource.isTypeSupported(types[key]) : false;
                    }
                    var elements = document.querySelectorAll('video,audio');
                    for (var i = 0; i < elements.length && i < 10; i++) {
                        var el = elements[i];
                        result.videos.push({
                            tag: el.tagName,
                            src: String(el.currentSrc || el.src || '').slice(0, 300),
                            readyState: el.readyState,
                            networkState: el.networkState,
                            paused: el.paused,
                            ended: el.ended,
                            currentTime: typeof el.currentTime === 'number' ? Math.round(el.currentTime * 100) / 100 : null,
                            duration: isFinite(el.duration) ? Math.round(el.duration * 100) / 100 : null,
                            error: el.error ? { code: el.error.code, message: el.error.message } : null
                        });
                    }
                    return JSON.stringify(result);
                } catch (e) {
                    return 'probe exception: ' + (e && e.message);
                }
            })();
            """;
    }

    /// <summary>
    /// Logs media downloads and media-CDN failures so playback problems can be
    /// traced. Successful segment traffic is deliberately not logged: Instagram
    /// issues dozens of short-range requests per second while a reel plays.
    /// Debug builds only.
    /// </summary>
    internal sealed class MediaResourceRequestHandler : ResourceRequestHandler
    {
        protected override CefReturnValue OnBeforeResourceLoad(
            IWebBrowser chromiumWebBrowser,
            IBrowser browser,
            IFrame frame,
            IRequest request,
            IRequestCallback callback)
        {
            try
            {
                if (request.ResourceType == ResourceType.Media)
                {
                    AppLogger.Info(
                        $"{MediaDiagnostics.Prefix} request type={request.ResourceType} " +
                        $"url='{request.Url}' referrer='{request.ReferrerUrl}'");
                }
            }
            catch
            {
                // Ignore logging failures.
            }

            return CefReturnValue.Continue;
        }

        protected override bool OnResourceResponse(
            IWebBrowser chromiumWebBrowser,
            IBrowser browser,
            IFrame frame,
            IRequest request,
            IResponse response)
        {
            try
            {
                bool failed = response.StatusCode >= 400 || response.ErrorCode != CefErrorCode.None;
                if (request.ResourceType == ResourceType.Media || failed)
                {
                    AppLogger.Info(
                        $"{MediaDiagnostics.Prefix} response type={request.ResourceType} " +
                        $"status={response.StatusCode} mime='{response.MimeType}' " +
                        $"error={response.ErrorCode} url='{request.Url}'");
                }
            }
            catch
            {
                // Ignore logging failures.
            }

            return false;
        }

        protected override void OnResourceRedirect(
            IWebBrowser chromiumWebBrowser,
            IBrowser browser,
            IFrame frame,
            IRequest request,
            IResponse response,
            ref string newUrl)
        {
            try
            {
                if (request.ResourceType == ResourceType.Media)
                {
                    AppLogger.Info(
                        $"{MediaDiagnostics.Prefix} redirect status={response.StatusCode} " +
                        $"from='{request.Url}' to='{newUrl}'");
                }
            }
            catch
            {
                // Ignore logging failures.
            }
        }

        protected override void OnResourceLoadComplete(
            IWebBrowser chromiumWebBrowser,
            IBrowser browser,
            IFrame frame,
            IRequest request,
            IResponse response,
            UrlRequestStatus status,
            long receivedContentLength)
        {
            try
            {
                bool failed = status != UrlRequestStatus.Success ||
                              (response != null && response.StatusCode >= 400);

                if (request.ResourceType == ResourceType.Media || failed)
                {
                    AppLogger.Info(
                        $"{MediaDiagnostics.Prefix} load-complete type={request.ResourceType} " +
                        $"status={status} bytes={receivedContentLength} " +
                        $"http={response?.StatusCode} url='{request.Url}'");
                }
            }
            catch
            {
                // Ignore logging failures.
            }
        }
    }
#endif
}
