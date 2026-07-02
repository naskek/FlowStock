package ru.flowstock.tsd.web

import android.annotation.SuppressLint
import android.graphics.Bitmap
import android.net.http.SslError
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.webkit.ConsoleMessage
import android.webkit.CookieManager
import android.webkit.ServiceWorkerController
import android.webkit.SslErrorHandler
import android.webkit.WebChromeClient
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebViewClient
import org.json.JSONObject
import org.json.JSONTokener
import ru.flowstock.tsd.BuildConfig
import ru.flowstock.tsd.diagnostics.MaskedScanDiagnostics
import ru.flowstock.tsd.scanner.ScanPayload
import java.net.URI

enum class BridgeState {
    NoPage,
    Loading,
    PageLoaded,
    BridgeScriptReady,
    ScannerIntentReady,
    BlockedOrigin,
    Reloading,
    Error,
}

class BridgeReadinessState {
    var state: BridgeState = BridgeState.NoPage
        private set
    private var generation: Long = 0L

    fun onPageStarted(trusted: Boolean): Long {
        generation += 1
        state = if (trusted) BridgeState.Loading else BridgeState.BlockedOrigin
        return generation
    }

    fun onPageFinished(trusted: Boolean): Long {
        state = if (trusted) BridgeState.PageLoaded else BridgeState.BlockedOrigin
        return generation
    }

    fun onBridgeProbe(
        probeGeneration: Long,
        bridgeReady: Boolean,
        activeScanSubscriptionCount: Int,
    ): Boolean {
        if (probeGeneration != generation) {
            return false
        }
        val scannerIntentReady = bridgeReady && activeScanSubscriptionCount > 0
        state = when {
            scannerIntentReady -> BridgeState.ScannerIntentReady
            bridgeReady -> BridgeState.BridgeScriptReady
            else -> BridgeState.PageLoaded
        }
        return true
    }

    fun onReloading(): Long {
        generation += 1
        state = BridgeState.Reloading
        return generation
    }

    fun onError(): Long {
        generation += 1
        state = BridgeState.Error
        return generation
    }

    fun canDispatch(): Boolean = state == BridgeState.ScannerIntentReady

    fun isCurrentGeneration(probeGeneration: Long): Boolean =
        probeGeneration == generation

    fun currentGenerationForTest(): Long = generation

    fun currentGeneration(): Long = generation

    fun shouldRetryProbe(
        probeGeneration: Long,
        completedAttempt: Int,
        maxAttempts: Int,
    ): Boolean {
        if (!isCurrentGeneration(probeGeneration) || completedAttempt >= maxAttempts) {
            return false
        }
        return state == BridgeState.PageLoaded || state == BridgeState.BridgeScriptReady
    }
}

data class BridgeProbeResult(
    val parseSuccess: Boolean,
    val resultState: String,
    val rawResultLength: Int,
    val rawResultHash: String,
    val hasNativeUserAgentToken: Boolean = false,
    val hasSearchMarker: Boolean = false,
    val hasHrefMarker: Boolean = false,
    val hasDocumentUrlMarker: Boolean = false,
    val hasCookieMarker: Boolean = false,
    val pageUaAccess: String = "",
    val pageQueryAccess: String = "",
    val pageCookieAccess: String = "",
    val pageQueryMarker: Boolean = false,
    val pageCookieMarker: Boolean = false,
    val activationSource: String = "",
    val nativeCookieStoreMarker: Boolean = false,
    val searchType: String = "",
    val searchLength: Int = 0,
    val searchHash: String = "",
    val hasBridgeObject: Boolean = false,
    val dispatchType: String = "",
    val bridgeReady: Boolean = false,
    val activeScanSubscriptionCount: Int = 0,
    val serviceWorkerState: String = "",
    val jsErrorName: String = "",
    val parseErrorName: String = "",
)

data class NativeUserAgentDiagnostic(
    val hasNativeUserAgentToken: Boolean,
    val length: Int,
    val hash: String,
)

data class UrlMarkerDiagnostic(
    val hasNativeQueryMarker: Boolean,
    val length: Int,
    val hash: String,
    val trustedOrigin: Boolean = false,
)

data class NavigationMarkerDiagnostics(
    val requested: UrlMarkerDiagnostic,
    val started: UrlMarkerDiagnostic = UrlMarkerDiagnostic(false, 0, ""),
    val finished: UrlMarkerDiagnostic = UrlMarkerDiagnostic(false, 0, ""),
    val startedCount: Int = 0,
    val finishedCount: Int = 0,
)

data class CookieMarkerDiagnostic(
    val cookieAcceptEnabled: Boolean,
    val cookieSet: Boolean,
    val cookieSetResult: String,
    val nativeCookieStoreMarker: Boolean,
    val nativeCookieStoreReadResult: String,
    val errorName: String = "",
)

data class NativeCookieStoreDiagnostic(
    val marker: Boolean,
    val readResult: String,
)

class OneShotGenerationLoadCoordinator {
    private var completedGeneration: Long? = null

    fun tryComplete(targetGeneration: Long, currentGeneration: Long): Boolean {
        if (targetGeneration != currentGeneration) {
            return false
        }
        if (completedGeneration == targetGeneration) {
            return false
        }
        completedGeneration = targetGeneration
        return true
    }
}

class FlowStockWebViewController(
    private val webView: WebView,
    private val initialUrl: String,
    private val onStatus: (String) -> Unit,
) {
    private val trustedOrigin: String = requireNotNull(originOf(initialUrl)) {
        "Initial TSD URL must be an absolute HTTPS URL"
    }
    private val readiness = BridgeReadinessState()
    private val mainHandler = Handler(Looper.getMainLooper())
    private var currentMainFrameUrl: String? = null
    private var nativeUserAgentDiagnostic = createNativeUserAgentDiagnostic(null)
    private val loadUrl = withNativeBridgeQueryMarker(initialUrl)
    private val loadCoordinator = OneShotGenerationLoadCoordinator()
    private var cookieMarkerDiagnostic = CookieMarkerDiagnostic(false, false, "not-started", false, "empty")
    private var navigationDiagnostics = NavigationMarkerDiagnostics(
        requested = createUrlMarkerDiagnostic(loadUrl, trustedOrigin),
    )

    init {
        configureWebView()
    }

    fun load() {
        val generation = readiness.onReloading()
        navigationDiagnostics = NavigationMarkerDiagnostics(
            requested = createUrlMarkerDiagnostic(loadUrl, trustedOrigin),
        )
        cookieMarkerDiagnostic = CookieMarkerDiagnostic(false, false, "pending", false, "empty")
        onStatus(formatRequestedUrlStatus(navigationDiagnostics.requested))
        setNativeSessionCookieThenLoad(generation)
    }

    private fun setNativeSessionCookieThenLoad(generation: Long) {
        val cookie = buildNativeBridgeSessionCookie(loadUrl)
        val cookieManager = CookieManager.getInstance()
        lateinit var timeoutRunnable: Runnable
        val cookieAcceptEnabled = runCatching {
            cookieManager.setAcceptCookie(true)
            cookieManager.acceptCookie()
        }.getOrDefault(false)

        fun readNativeCookieStore(): NativeCookieStoreDiagnostic =
            runCatching { cookieManager.getCookie(loadUrl) }
                .fold(
                    onSuccess = { cookieText ->
                        if (cookieText.isNullOrBlank()) {
                            NativeCookieStoreDiagnostic(false, "empty")
                        } else {
                            NativeCookieStoreDiagnostic(
                                marker = hasExactNativeBridgeCookieMarker(cookieText),
                                readResult = "ok",
                            )
                        }
                    },
                    onFailure = { NativeCookieStoreDiagnostic(false, "error") },
                )

        fun finish(
            result: String,
            cookieSet: Boolean,
            errorName: String = "",
            store: NativeCookieStoreDiagnostic = readNativeCookieStore(),
        ) {
            if (!loadCoordinator.tryComplete(generation, readiness.currentGeneration())) {
                emitDebugLifecycleDiagnostic(
                    "cookie marker ignored: completed-or-stale result=${sanitizeProbeToken(result)}",
                )
                return
            }
            if (result != "timeout") {
                mainHandler.removeCallbacks(timeoutRunnable)
            }
            cookieMarkerDiagnostic = CookieMarkerDiagnostic(
                cookieAcceptEnabled = cookieAcceptEnabled,
                cookieSet = cookieSet,
                cookieSetResult = sanitizeProbeToken(result),
                nativeCookieStoreMarker = store.marker,
                nativeCookieStoreReadResult = sanitizeProbeToken(store.readResult),
                errorName = sanitizeProbeToken(errorName),
            )
            if (cookieSet) {
                runCatching { CookieManager.getInstance().flush() }
            }
            emitLifecycleStatus(formatCookieMarkerStatus(cookieMarkerDiagnostic))
            webView.loadUrl(loadUrl)
        }

        timeoutRunnable = Runnable { finish(result = "timeout", cookieSet = false) }
        mainHandler.postDelayed(timeoutRunnable, COOKIE_SET_TIMEOUT_MS)

        runCatching {
            cookieManager.setCookie(loadUrl, cookie) { success ->
                mainHandler.post {
                    finish(
                        result = if (success == true) "ok" else "false",
                        cookieSet = success == true,
                    )
                }
            }
        }.onFailure { error ->
            finish(result = "error", cookieSet = false, errorName = error.javaClass.simpleName)
        }
    }

    fun dispatchScan(payload: ScanPayload): Boolean {
        if (!readiness.canDispatch() || !isTrustedUrl(currentMainFrameUrl, trustedOrigin)) {
            val diagnostic = MaskedScanDiagnostics.fromValue(
                payload.value,
                payload.symbology,
                state = "bridge-not-ready",
                timestamp = payload.ts,
            )
            onStatus(
                "scan rejected: ${diagnostic.state} len=${diagnostic.length} hash=${diagnostic.hash} " +
                    "masked=${diagnostic.maskedValue} state=${readiness.state}",
            )
            return false
        }

        val jsonText = payload.toJsonObject().toString()
        val script = """
            (function () {
              var bridge = window.FlowStockAndroidBridge;
              if (!bridge || typeof bridge.__dispatchFromNative !== "function") {
                return false;
              }
              return bridge.__dispatchFromNative(${toJavaScriptStringLiteral(jsonText)});
            })();
        """.trimIndent()

        webView.evaluateJavascript(script) { result ->
            val delivered = result == "true"
            val diagnostic = MaskedScanDiagnostics.fromValue(
                payload.value,
                payload.symbology,
                state = if (delivered) "delivered" else "dispatch-rejected",
                timestamp = payload.ts,
            )
            onStatus(
                "scan ${diagnostic.state}: len=${diagnostic.length} hash=${diagnostic.hash} " +
                    "masked=${diagnostic.maskedValue} sym=${diagnostic.symbology.orEmpty()}",
            )
        }
        return true
    }

    fun handleBack(fallback: () -> Unit) {
        val script = """
            (function () {
              var bridge = window.FlowStockAndroidBridge;
              if (!bridge || typeof bridge.handleBack !== "function") {
                return false;
              }
              return bridge.handleBack() === true;
            })();
        """.trimIndent()

        webView.evaluateJavascript(script) { result ->
            if (result == "true") {
                return@evaluateJavascript
            }
            if (webView.canGoBack()) {
                webView.goBack()
            } else {
                fallback()
            }
        }
    }

    fun stateForTest(): BridgeState = readiness.state

    @SuppressLint("SetJavaScriptEnabled")
    private fun configureWebView() {
        val settings = webView.settings
        settings.javaScriptEnabled = true
        settings.domStorageEnabled = true
        settings.cacheMode = WebSettings.LOAD_DEFAULT
        settings.allowFileAccess = false
        settings.allowContentAccess = false
        settings.setSupportZoom(false)
        settings.builtInZoomControls = false
        settings.displayZoomControls = false
        if (Build.VERSION.SDK_INT >= 21) {
            settings.mixedContentMode = WebSettings.MIXED_CONTENT_NEVER_ALLOW
        }
        val defaultUserAgent = runCatching {
            WebSettings.getDefaultUserAgent(webView.context)
        }.getOrNull()
        settings.userAgentString = buildNativeUserAgent(defaultUserAgent, settings.userAgentString)
        nativeUserAgentDiagnostic = createNativeUserAgentDiagnostic(settings.userAgentString)
        onStatus(formatNativeUserAgentStatus(nativeUserAgentDiagnostic))

        if (Build.VERSION.SDK_INT >= 24) {
            runCatching {
                ServiceWorkerController.getInstance().serviceWorkerWebSettings.apply {
                    allowContentAccess = false
                    allowFileAccess = false
                    blockNetworkLoads = false
                }
            }.onFailure { error ->
                onStatus("service worker settings unavailable: ${error.javaClass.simpleName}")
            }
        }

        webView.webChromeClient = object : WebChromeClient() {
            override fun onConsoleMessage(consoleMessage: ConsoleMessage?): Boolean {
                val message = consoleMessage?.message().orEmpty()
                val diagnostic = MaskedScanDiagnostics.fromValue(message, state = "console")
                onStatus(
                    "console ${consoleMessage?.messageLevel()}: len=${diagnostic.length} " +
                        "hash=${diagnostic.hash} line=${consoleMessage?.lineNumber() ?: 0}",
                )
                return true
            }
        }

        webView.webViewClient = object : WebViewClient() {
            override fun shouldOverrideUrlLoading(view: WebView?, request: WebResourceRequest?): Boolean {
                val url = request?.url?.toString()
                return !isTrustedUrl(url, trustedOrigin)
            }

            @Suppress("DEPRECATION")
            @Deprecated("Legacy WebView callback kept for API compatibility.")
            override fun shouldOverrideUrlLoading(view: WebView?, url: String?): Boolean =
                !isTrustedUrl(url, trustedOrigin)

            override fun onPageStarted(view: WebView?, url: String?, favicon: Bitmap?) {
                currentMainFrameUrl = url
                val trusted = isTrustedUrl(url, trustedOrigin)
                readiness.onPageStarted(trusted)
                navigationDiagnostics = navigationDiagnostics.copy(
                    started = createUrlMarkerDiagnostic(url, trustedOrigin),
                    finished = UrlMarkerDiagnostic(false, 0, ""),
                    startedCount = 1,
                    finishedCount = 0,
                )
                emitLifecycleStatus(
                    "page started: state=${readiness.state} " +
                        "startedMarker=${navigationDiagnostics.started.hasNativeQueryMarker} " +
                        "startedLen=${navigationDiagnostics.started.length} " +
                        "startedHash=${navigationDiagnostics.started.hash} " +
                        "startedTrusted=${navigationDiagnostics.started.trustedOrigin} " +
                        "startedCount=${navigationDiagnostics.startedCount}",
                )
            }

            override fun onPageFinished(view: WebView?, url: String?) {
                currentMainFrameUrl = url
                val trusted = isTrustedUrl(url, trustedOrigin)
                val generation = readiness.onPageFinished(trusted)
                navigationDiagnostics = navigationDiagnostics.copy(
                    finished = createUrlMarkerDiagnostic(url, trustedOrigin),
                    finishedCount = navigationDiagnostics.finishedCount + 1,
                )
                emitLifecycleStatus(
                    "page finished: state=${readiness.state} " +
                        "finishedMarker=${navigationDiagnostics.finished.hasNativeQueryMarker} " +
                        "finishedLen=${navigationDiagnostics.finished.length} " +
                        "finishedHash=${navigationDiagnostics.finished.hash} " +
                        "finishedTrusted=${navigationDiagnostics.finished.trustedOrigin} " +
                        "finishedCount=${navigationDiagnostics.finishedCount}",
                )
                if (readiness.state == BridgeState.PageLoaded) {
                    probeBridgeReadiness(generation, completedAttempt = 0)
                }
            }

            override fun onReceivedSslError(view: WebView?, handler: SslErrorHandler?, error: SslError?) {
                handler?.cancel()
                readiness.onError()
                emitLifecycleStatus("ssl error: cancelled state=${readiness.state}")
            }

            @Suppress("DEPRECATION")
            override fun onReceivedError(
                view: WebView?,
                request: WebResourceRequest?,
                error: WebResourceError?,
            ) {
                if (request?.isForMainFrame == true) {
                    readiness.onError()
                    emitLifecycleStatus("main-frame error: state=${readiness.state} code=${error?.errorCode ?: 0}")
                }
            }

            override fun onReceivedHttpError(
                view: WebView?,
                request: WebResourceRequest?,
                errorResponse: WebResourceResponse?,
            ) {
                if (request?.isForMainFrame == true) {
                    readiness.onError()
                    emitLifecycleStatus("main-frame http error: ${errorResponse?.statusCode ?: 0}")
                }
            }
        }
    }

    private fun probeBridgeReadiness(generation: Long, completedAttempt: Int) {
        if (!readiness.isCurrentGeneration(generation)) {
            return
        }
        val nativeCookieStoreMarker = if (cookieMarkerDiagnostic.nativeCookieStoreMarker) "true" else "false"
        val script = """
            (function () {
              function bit(value) {
                return value ? "1" : "0";
              }
              function clean(value) {
                return String(value || "")
                  .replace(/[^A-Za-z0-9_.-]/g, "")
                  .slice(0, 32);
              }
              function cleanAccess(value) {
                return value === "ok" || value === "error" ? value : "error";
              }
              function cleanActivationSource(value) {
                return value === "ua" || value === "query" || value === "cookie" || value === "none"
                  ? value
                  : "none";
              }
              function swState() {
                try {
                  if (!navigator || !navigator.serviceWorker) {
                    return "unsupported";
                  }
                  return navigator.serviceWorker.controller ? "controlled" : "available";
                } catch (swError) {
                  return "error";
                }
              }
              try {
                var activation = window.FlowStockNativeActivationDiagnostic || {};
                var bridge = window.FlowStockAndroidBridge;
                var dispatchType = bridge ? typeof bridge.__dispatchFromNative : "undefined";
                var bridgeReady = !!(bridge &&
                  bridge.__flowstockNativeDispatchReady === true &&
                  dispatchType === "function");
                var activeScanSubscriptionCount = 0;
                if (bridge && typeof bridge.__getActiveScanSubscriptionCount === "function") {
                  activeScanSubscriptionCount = bridge.__getActiveScanSubscriptionCount();
                } else if (bridge && typeof bridge.__hasActiveScanSubscribers === "function") {
                  activeScanSubscriptionCount = bridge.__hasActiveScanSubscribers() ? 1 : 0;
                }
                return [
                  "FS_PROBE_V5",
                  bit(activation.uaMarker === true),
                  cleanAccess(activation.uaAccess),
                  bit(activation.queryMarker === true),
                  cleanAccess(activation.queryAccess),
                  bit(activation.cookieMarker === true),
                  cleanAccess(activation.cookieAccess),
                  cleanActivationSource(activation.activationSource),
                  bit($nativeCookieStoreMarker),
                  bit(!!bridge),
                  clean(dispatchType),
                  bit(bridgeReady),
                  String(activeScanSubscriptionCount || 0),
                  clean(swState()),
                  ""
                ].join("|");
              } catch (error) {
                return [
                  "FS_PROBE_V5",
                  "0",
                  "error",
                  "0",
                  "error",
                  "0",
                  "error",
                  "none",
                  bit($nativeCookieStoreMarker),
                  "0",
                  "undefined",
                  "0",
                  "0",
                  "error",
                  clean(error && error.name ? error.name : "Error")
                ].join("|");
              }
            })();
        """.trimIndent()

        webView.evaluateJavascript(script) { result ->
            val probe = parseBridgeProbeResult(result)
            val bridgeReady = probe.bridgeReady
            val activeScanSubscriptionCount = probe.activeScanSubscriptionCount
            if (!readiness.onBridgeProbe(generation, bridgeReady, activeScanSubscriptionCount)) {
                emitDebugLifecycleDiagnostic("bridge probe ignored: stale generation")
                return@evaluateJavascript
            }
            emitLifecycleStatus(
                formatBridgeProbeStatus(
                    readiness.state,
                    probe,
                    nativeUserAgentDiagnostic,
                    navigationDiagnostics,
                    cookieMarkerDiagnostic,
                ),
            )
            val nextAttempt = completedAttempt + 1
            if (readiness.shouldRetryProbe(generation, nextAttempt, BRIDGE_PROBE_MAX_ATTEMPTS)) {
                mainHandler.postDelayed(
                    { probeBridgeReadiness(generation, completedAttempt = nextAttempt) },
                    BRIDGE_PROBE_RETRY_DELAY_MS,
                )
            }
        }
    }

    private fun emitLifecycleStatus(message: String) {
        emitDebugLifecycleDiagnostic(message)
        onStatus(message)
    }

    private fun emitDebugLifecycleDiagnostic(message: String) {
        if (BuildConfig.DEBUG && isDebugLifecycleDiagnostic(message)) {
            Log.i(PROBE_LOG_TAG, message)
        }
    }

    companion object {
        private const val PROBE_LOG_TAG = "FlowStockTsdProbe"
        private const val NATIVE_UA_TOKEN = "FlowStockTsdNative/1"
        private const val NATIVE_QUERY_MARKER_NAME = "flowstockNative"
        private const val NATIVE_QUERY_MARKER_VALUE = "1"
        internal const val BRIDGE_PROBE_MAX_ATTEMPTS = 8
        private const val BRIDGE_PROBE_RETRY_DELAY_MS = 150L
        private const val COOKIE_SET_TIMEOUT_MS = 1_000L

        fun withNativeBridgeQueryMarker(url: String): String {
            val fragmentStart = url.indexOf('#')
            val beforeFragment = if (fragmentStart >= 0) url.substring(0, fragmentStart) else url
            val fragment = if (fragmentStart >= 0) url.substring(fragmentStart) else ""
            val queryStart = beforeFragment.indexOf('?')
            val base = if (queryStart >= 0) beforeFragment.substring(0, queryStart) else beforeFragment
            val query = if (queryStart >= 0) beforeFragment.substring(queryStart + 1) else ""
            val marker = "$NATIVE_QUERY_MARKER_NAME=$NATIVE_QUERY_MARKER_VALUE"
            var markerWritten = false
            val parameters = query
                .split('&')
                .filter { it.isNotEmpty() }
                .mapNotNull { parameter ->
                    if (parameterNameOf(parameter) != NATIVE_QUERY_MARKER_NAME) {
                        parameter
                    } else if (!markerWritten) {
                        markerWritten = true
                        marker
                    } else {
                        null
                    }
                }
            val nextQuery = (if (markerWritten) parameters else parameters + marker).joinToString("&")
            return "$base?$nextQuery$fragment"
        }

        fun nativeBridgeCookiePath(url: String): String {
            val uri = runCatching { URI(url) }.getOrNull() ?: return "/"
            val path = uri.rawPath?.takeIf { it.startsWith("/") && it.isNotBlank() } ?: return "/"
            if (path.endsWith("/")) {
                return path
            }
            val slash = path.lastIndexOf('/')
            return if (slash <= 0) "/" else path.substring(0, slash + 1)
        }

        fun buildNativeBridgeSessionCookie(url: String): String =
            "$NATIVE_QUERY_MARKER_NAME=$NATIVE_QUERY_MARKER_VALUE; Path=${nativeBridgeCookiePath(url)}; Secure"

        fun hasExactNativeBridgeQueryMarker(url: String?): Boolean {
            if (url.isNullOrBlank()) return false
            val fragmentStart = url.indexOf('#')
            val beforeFragment = if (fragmentStart >= 0) url.substring(0, fragmentStart) else url
            val queryStart = beforeFragment.indexOf('?')
            if (queryStart < 0) return false
            val query = beforeFragment.substring(queryStart + 1)
            if (query.isEmpty()) return false
            return query.split('&').any { parameter ->
                val equalsAt = parameter.indexOf('=')
                val name = if (equalsAt >= 0) parameter.substring(0, equalsAt) else parameter
                val value = if (equalsAt >= 0) parameter.substring(equalsAt + 1) else ""
                name == NATIVE_QUERY_MARKER_NAME && value == NATIVE_QUERY_MARKER_VALUE
            }
        }

        fun hasExactNativeBridgeCookieMarker(cookieText: String?): Boolean {
            if (cookieText.isNullOrBlank()) return false
            return cookieText.split(';').any { cookie ->
                val part = cookie.trim()
                val equalsAt = part.indexOf('=')
                val name = if (equalsAt >= 0) part.substring(0, equalsAt) else part
                val value = if (equalsAt >= 0) part.substring(equalsAt + 1) else ""
                name == NATIVE_QUERY_MARKER_NAME && value == NATIVE_QUERY_MARKER_VALUE
            }
        }

        fun createUrlMarkerDiagnostic(url: String?, trustedOrigin: String? = null): UrlMarkerDiagnostic {
            val text = url.orEmpty()
            return UrlMarkerDiagnostic(
                hasNativeQueryMarker = hasExactNativeBridgeQueryMarker(text),
                length = text.length,
                hash = MaskedScanDiagnostics.hash(text),
                trustedOrigin = trustedOrigin != null && isTrustedUrl(text, trustedOrigin),
            )
        }

        fun formatRequestedUrlStatus(diagnostic: UrlMarkerDiagnostic): String =
            "load url: requestedMarker=${diagnostic.hasNativeQueryMarker} " +
                "requestedLen=${diagnostic.length} requestedHash=${diagnostic.hash} " +
                "requestedTrusted=${diagnostic.trustedOrigin}"

        fun formatCookieMarkerStatus(diagnostic: CookieMarkerDiagnostic): String =
            "cookie marker: cookieAcceptEnabled=${diagnostic.cookieAcceptEnabled} " +
                "cookieSet=${diagnostic.cookieSet} cookieSetResult=${diagnostic.cookieSetResult} " +
                "nativeCookieStoreMarker=${diagnostic.nativeCookieStoreMarker} " +
                "nativeCookieStoreReadResult=${diagnostic.nativeCookieStoreReadResult} " +
                "cookieSetError=${diagnostic.errorName}"

        fun buildNativeUserAgent(defaultUserAgent: String?, currentUserAgent: String?): String {
            val base = defaultUserAgent?.takeIf { it.isNotBlank() } ?: currentUserAgent
            return appendNativeUserAgentToken(base)
        }

        fun appendNativeUserAgentToken(userAgent: String?): String {
            val existingTokens = userAgent.orEmpty()
                .trim()
                .split(Regex("\\s+"))
                .filter { it.isNotBlank() && it != NATIVE_UA_TOKEN }
            return (existingTokens + NATIVE_UA_TOKEN).joinToString(" ")
        }

        fun createNativeUserAgentDiagnostic(userAgent: String?): NativeUserAgentDiagnostic {
            val text = userAgent.orEmpty()
            return NativeUserAgentDiagnostic(
                hasNativeUserAgentToken = text.split(Regex("\\s+")).any { it == NATIVE_UA_TOKEN },
                length = text.length,
                hash = MaskedScanDiagnostics.hash(text),
            )
        }

        fun formatNativeUserAgentStatus(diagnostic: NativeUserAgentDiagnostic): String =
            "webview ua: nativeUaToken=${diagnostic.hasNativeUserAgentToken} " +
                "len=${diagnostic.length} hash=${diagnostic.hash}"

        fun formatBridgeProbeStatus(
            state: BridgeState,
            probe: BridgeProbeResult,
            nativeUserAgent: NativeUserAgentDiagnostic,
            navigation: NavigationMarkerDiagnostics,
            cookie: CookieMarkerDiagnostic,
        ): String {
            return "bridge probe: state=$state parse=${probe.resultState} " +
                "len=${probe.rawResultLength} hash=${probe.rawResultHash} " +
                "nativeUaToken=${nativeUserAgent.hasNativeUserAgentToken} " +
                "nativeUaLen=${nativeUserAgent.length} nativeUaHash=${nativeUserAgent.hash} " +
                "cookieAcceptEnabled=${cookie.cookieAcceptEnabled} " +
                "cookieSet=${cookie.cookieSet} cookieSetResult=${cookie.cookieSetResult} " +
                "nativeCookieStoreMarker=${cookie.nativeCookieStoreMarker} " +
                "nativeCookieStoreReadResult=${cookie.nativeCookieStoreReadResult} " +
                "requestedMarker=${navigation.requested.hasNativeQueryMarker} " +
                "requestedLen=${navigation.requested.length} requestedHash=${navigation.requested.hash} " +
                "startedMarker=${navigation.started.hasNativeQueryMarker} " +
                "startedLen=${navigation.started.length} startedHash=${navigation.started.hash} " +
                "startedTrusted=${navigation.started.trustedOrigin} startedCount=${navigation.startedCount} " +
                "finishedMarker=${navigation.finished.hasNativeQueryMarker} " +
                "finishedLen=${navigation.finished.length} finishedHash=${navigation.finished.hash} " +
                "finishedTrusted=${navigation.finished.trustedOrigin} finishedCount=${navigation.finishedCount} " +
                "uaToken=${probe.hasNativeUserAgentToken} pageUaAccess=${probe.pageUaAccess} " +
                "pageQueryAccess=${probe.pageQueryAccess} pageCookieAccess=${probe.pageCookieAccess} " +
                "pageQueryMarker=${probe.pageQueryMarker} pageCookieMarker=${probe.pageCookieMarker} " +
                "activationSource=${probe.activationSource} " +
                "searchMarker=${probe.hasSearchMarker} " +
                "hrefMarker=${probe.hasHrefMarker} documentUrlMarker=${probe.hasDocumentUrlMarker} " +
                "cookieMarker=${probe.hasCookieMarker} " +
                "searchType=${probe.searchType} searchLen=${probe.searchLength} searchHash=${probe.searchHash} " +
                "bridgeObject=${probe.hasBridgeObject} " +
                "dispatch=${probe.dispatchType} bridge=${probe.bridgeReady} " +
                "subscribers=${probe.activeScanSubscriptionCount} sw=${probe.serviceWorkerState} " +
                "jsError=${probe.jsErrorName} parseError=${probe.parseErrorName}"
        }

        fun isDebugLifecycleDiagnostic(message: String): Boolean =
            message.startsWith("cookie marker: ") ||
                message.startsWith("bridge probe: ") ||
                message.startsWith("page started: ") ||
                message.startsWith("page finished: ") ||
                message.startsWith("ssl error: ") ||
                message.startsWith("main-frame error: ") ||
                message.startsWith("main-frame http error: ") ||
                message.startsWith("cookie marker ignored: ") ||
                message.startsWith("bridge probe ignored: ")

        fun originOf(url: String?): String? {
            if (url.isNullOrBlank()) return null
            val uri = runCatching { URI(url) }.getOrNull() ?: return null
            val scheme = uri.scheme?.lowercase() ?: return null
            val host = uri.host?.lowercase() ?: return null
            if (scheme != "https") return null
            val port = if (uri.port >= 0) ":${uri.port}" else ""
            return "$scheme://$host$port"
        }

        fun isTrustedUrl(url: String?, trustedOrigin: String): Boolean =
            originOf(url) == trustedOrigin

        fun toJavaScriptStringLiteral(value: String): String {
            val builder = StringBuilder(value.length + 2)
            builder.append('"')
            value.forEach { char ->
                when (char) {
                    '\\' -> builder.append("\\\\")
                    '"' -> builder.append("\\\"")
                    '\b' -> builder.append("\\b")
                    '\u000C' -> builder.append("\\f")
                    '\n' -> builder.append("\\n")
                    '\r' -> builder.append("\\r")
                    '\t' -> builder.append("\\t")
                    '\u2028', '\u2029' -> builder.append("\\u%04x".format(char.code))
                    else -> {
                        if (char.code < 0x20) {
                            builder.append("\\u%04x".format(char.code))
                        } else {
                            builder.append(char)
                        }
                    }
                }
            }
            builder.append('"')
            return builder.toString()
        }

        fun decodeEvaluateJavascriptString(result: String?): String {
            var text = result?.trim().orEmpty()
            if (text == "null" || text.isBlank()) return ""
            repeat(4) {
                val decoded = decodeJsonCallbackValue(text) ?: return text
                if (decoded == text) {
                    return text
                }
                text = decoded.trim()
                if (text == "null" || text.isBlank()) {
                    return ""
                }
            }
            return text
        }

        fun parseBridgeProbeResult(result: String?): BridgeProbeResult {
            val rawText = result.orEmpty()
            val rawLength = rawText.length
            val rawHash = MaskedScanDiagnostics.hash(rawText)
            if (result == null) {
                return BridgeProbeResult(
                    parseSuccess = false,
                    resultState = "null",
                    rawResultLength = rawLength,
                    rawResultHash = rawHash,
                )
            }
            if (rawText.isBlank()) {
                return BridgeProbeResult(
                    parseSuccess = false,
                    resultState = "empty",
                    rawResultLength = rawLength,
                    rawResultHash = rawHash,
                )
            }

            val decoded = decodeEvaluateJavascriptString(result)
            if (decoded.isBlank()) {
                return BridgeProbeResult(
                    parseSuccess = false,
                    resultState = "decoded-empty",
                    rawResultLength = rawLength,
                    rawResultHash = rawHash,
                )
            }

            parsePipeProbe(decoded, rawLength, rawHash)?.let { return it }
            parseLegacyJsonProbe(decoded, rawLength, rawHash)?.let { return it }

            return BridgeProbeResult(
                parseSuccess = false,
                resultState = "parse-failed",
                rawResultLength = rawLength,
                rawResultHash = rawHash,
                parseErrorName = "UnknownFormat",
            )
        }

        private fun decodeJsonCallbackValue(text: String): String? {
            return runCatching {
                val value = JSONTokener(text).nextValue()
                when (value) {
                    JSONObject.NULL -> ""
                    is String -> value
                    else -> value.toString()
                }
            }.getOrNull()
        }

        private fun parsePipeProbe(
            decoded: String,
            rawLength: Int,
            rawHash: String,
        ): BridgeProbeResult? {
            if (!decoded.startsWith("FS_PROBE_V5|")) return null
            val parts = decoded.split("|")
            if (parts.size != 15) {
                return BridgeProbeResult(
                    parseSuccess = false,
                    resultState = "parse-failed",
                    rawResultLength = rawLength,
                    rawResultHash = rawHash,
                    parseErrorName = "BadFieldCount",
                )
            }
            val uaAccess = sanitizeProbeToken(parts[2])
            val queryAccess = sanitizeProbeToken(parts[4])
            val cookieAccess = sanitizeProbeToken(parts[6])
            val activationSource = sanitizeProbeToken(parts[7])
            val nativeCookieStoreMarker = parts[8] == "1"
            val hasBridgeObject = parts[9] == "1"
            val dispatchType = sanitizeProbeToken(parts[10])
            val reportedBridgeReady = parts[11] == "1"
            val subscribers = parts[12].toIntOrNull()?.coerceAtLeast(0) ?: 0
            val bridgeReady = reportedBridgeReady && hasBridgeObject && dispatchType == "function"
            return BridgeProbeResult(
                parseSuccess = true,
                resultState = "ok",
                rawResultLength = rawLength,
                rawResultHash = rawHash,
                hasNativeUserAgentToken = parts[1] == "1",
                hasSearchMarker = parts[3] == "1",
                hasCookieMarker = parts[5] == "1",
                pageUaAccess = uaAccess,
                pageQueryAccess = queryAccess,
                pageCookieAccess = cookieAccess,
                pageQueryMarker = parts[3] == "1",
                pageCookieMarker = parts[5] == "1",
                activationSource = activationSource,
                nativeCookieStoreMarker = nativeCookieStoreMarker,
                hasBridgeObject = hasBridgeObject,
                dispatchType = dispatchType,
                bridgeReady = bridgeReady,
                activeScanSubscriptionCount = subscribers,
                serviceWorkerState = sanitizeProbeToken(parts[13]),
                jsErrorName = sanitizeProbeToken(parts[14]),
            )
        }

        private fun parseLegacyJsonProbe(
            decoded: String,
            rawLength: Int,
            rawHash: String,
        ): BridgeProbeResult? {
            val json = runCatching { JSONObject(decoded) }.getOrNull() ?: return null
            val reportedBridgeReady = json.optBoolean("bridgeReady", false)
            val subscribers = json.optInt("activeScanSubscriptionCount", 0).coerceAtLeast(0)
            val hasBridgeObject = json.optBoolean("hasBridgeObject", reportedBridgeReady)
            val dispatchType = sanitizeProbeToken(json.optString("dispatchType", ""))
            val bridgeReady = reportedBridgeReady && hasBridgeObject && dispatchType == "function"
            return BridgeProbeResult(
                parseSuccess = true,
                resultState = "ok-legacy-json",
                rawResultLength = rawLength,
                rawResultHash = rawHash,
                hasNativeUserAgentToken = json.optBoolean("hasNativeUserAgentToken", false),
                hasSearchMarker = json.optBoolean("hasNativeQueryMarker", false),
                hasHrefMarker = json.optBoolean("hasNativeQueryMarker", false),
                hasDocumentUrlMarker = json.optBoolean("hasNativeQueryMarker", false),
                hasCookieMarker = json.optBoolean("hasNativeCookieMarker", false),
                pageQueryMarker = json.optBoolean("hasNativeQueryMarker", false),
                pageCookieMarker = json.optBoolean("hasNativeCookieMarker", false),
                hasBridgeObject = hasBridgeObject,
                dispatchType = dispatchType,
                bridgeReady = bridgeReady,
                activeScanSubscriptionCount = subscribers,
                serviceWorkerState = sanitizeProbeToken(json.optString("serviceWorker", "")),
                jsErrorName = sanitizeProbeToken(json.optString("jsErrorName", "")),
            )
        }

        private fun sanitizeProbeToken(value: String?): String =
            value.orEmpty()
                .filter { it.isLetterOrDigit() || it == '_' || it == '.' || it == '-' }
                .take(32)

        private fun parameterNameOf(parameter: String): String {
            val equalsAt = parameter.indexOf('=')
            return if (equalsAt >= 0) parameter.substring(0, equalsAt) else parameter
        }
    }
}
