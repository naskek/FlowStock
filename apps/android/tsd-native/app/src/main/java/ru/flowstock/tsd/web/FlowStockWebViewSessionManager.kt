package ru.flowstock.tsd.web

import android.os.Handler
import android.os.Looper
import android.webkit.CookieManager
import android.webkit.WebStorage
import android.webkit.WebView
import android.webkit.WebViewClient
import ru.flowstock.tsd.config.ServerEndpoint
import ru.flowstock.tsd.runtime.CookieLookupResult
import ru.flowstock.tsd.runtime.CookieCleanupResult
import ru.flowstock.tsd.runtime.CookieCleanupVerifier
import ru.flowstock.tsd.runtime.FlowStockWebViewSessionSwitchOperations
import ru.flowstock.tsd.runtime.FlowStockWebViewSessionSwitchOrchestrator
import ru.flowstock.tsd.runtime.ScannerInteractionState
import ru.flowstock.tsd.scanner.ScannerAdapter

class FlowStockWebViewSessionManager(
    private val webView: WebView,
    private val scannerAdapter: ScannerAdapter,
    private val onStatus: (String) -> Unit,
    private val controllerFactory: (
        webView: WebView,
        initialUrl: String,
        onStatus: (String) -> Unit,
    ) -> FlowStockWebViewController = { view, initialUrl, status ->
        FlowStockWebViewController(view, initialUrl, status)
    },
    private val mainHandler: Handler = Handler(Looper.getMainLooper()),
) {
    private var controller: FlowStockWebViewController? = null
    private var activeEndpoint: ServerEndpoint? = null
    private val scannerState = ScannerInteractionState()
    private var controllerGeneration = 0L
    private val switchOrchestrator = FlowStockWebViewSessionSwitchOrchestrator(
        object : FlowStockWebViewSessionSwitchOperations {
            override fun stopScanner() {
                scannerState.setWebInteractionActive(false)
                syncScanner()
            }

            override fun disposeOldController() {
                controller?.dispose()
                controller = null
                activeEndpoint = null
                scannerState.setControllerAvailable(false)
                syncScanner()
            }

            override fun stopLoading() {
                webView.stopLoading()
            }

            override fun loadNeutralDocument(onComplete: () -> Unit, onFailure: () -> Unit) {
                this@FlowStockWebViewSessionManager.loadNeutralDocument(onComplete, onFailure)
            }

            override fun cleanupCookies(
                oldEndpoint: ServerEndpoint?,
                onResult: (CookieCleanupResult) -> Unit,
            ) {
                removeCookiesWithTimeout(oldEndpoint, onResult)
            }

            override fun cleanupWebStorage(oldEndpoint: ServerEndpoint?) {
                oldEndpoint?.rootUrl?.let { rootUrl ->
                    runCatching { WebStorage.getInstance().deleteOrigin(rootUrl) }
                        .onFailure { onStatus("webstorage cleanup failed: ${it.javaClass.simpleName}") }
                }
            }

            override fun clearHistory() {
                webView.clearHistory()
            }

            override fun createAndLoadController(endpoint: ServerEndpoint) {
                createAndLoad(endpoint)
            }
        },
    )

    fun currentController(): FlowStockWebViewController? = controller

    fun onActivityStarted() {
        scannerState.onActivityStarted()
        syncScanner()
    }

    fun onActivityStopped() {
        scannerState.onActivityStopped()
        syncScanner()
    }

    fun pauseScannerForSetup() {
        scannerState.setSetupVisible(true)
        syncScanner()
    }

    fun restoreCurrentSession() {
        scannerState.setSetupVisible(false)
        scannerState.setModalVisible(false)
        scannerState.setWebInteractionActive(controller != null)
        syncScanner()
    }

    fun showServerChangeModal() {
        scannerState.setModalVisible(true)
        syncScanner()
    }

    fun dismissServerChangeModal() {
        scannerState.setModalVisible(false)
        syncScanner()
    }

    fun transitionServerChangeModalToSetup() {
        scannerState.transitionModalToSetup()
        syncScanner()
    }

    fun loadInitial(endpoint: ServerEndpoint) {
        scannerState.setSetupVisible(false)
        createAndLoad(endpoint)
    }

    fun switchOrigin(
        oldEndpoint: ServerEndpoint?,
        newEndpoint: ServerEndpoint,
        onComplete: () -> Unit,
        onFailure: (restoredCurrentSession: Boolean) -> Unit,
    ) {
        switchOrchestrator.switchOrigin(oldEndpoint, newEndpoint, onComplete, onFailure)
    }

    fun disposeCurrent() {
        switchOrchestrator.cancelActiveSwitch()
        controllerGeneration += 1L
        scannerState.setWebInteractionActive(false)
        syncScanner()
        controller?.dispose()
        controller = null
        activeEndpoint = null
        scannerState.setControllerAvailable(false)
        webView.stopLoading()
    }

    private fun createAndLoad(endpoint: ServerEndpoint) {
        val generation = ++controllerGeneration
        val nextController = controllerFactory(webView, endpoint.tsdUrl()) { message ->
            if (generation == controllerGeneration) {
                onStatus(message)
            }
        }
        controller = nextController
        activeEndpoint = endpoint
        scannerState.setControllerAvailable(true)
        scannerState.setWebInteractionActive(true)
        syncScanner()
        nextController.load()
    }

    private fun loadNeutralDocument(
        onComplete: () -> Unit,
        onFailure: () -> Unit,
    ) {
        var completed = false
        lateinit var timeout: Runnable

        fun finish(success: Boolean) {
            if (completed) {
                return
            }
            completed = true
            mainHandler.removeCallbacks(timeout)
            if (success) {
                onComplete()
            } else {
                onFailure()
            }
        }

        timeout = Runnable { finish(false) }
        mainHandler.postDelayed(timeout, NEUTRAL_LOAD_TIMEOUT_MS)
        webView.webViewClient = object : WebViewClient() {
            override fun onPageFinished(view: WebView?, url: String?) {
                if (url == NEUTRAL_URL) {
                    finish(true)
                }
            }
        }
        runCatching { webView.loadUrl(NEUTRAL_URL) }
            .onFailure { finish(false) }
    }

    private fun removeCookiesWithTimeout(
        oldEndpoint: ServerEndpoint?,
        onResult: (CookieCleanupResult) -> Unit,
    ) {
        val cookieManager = CookieManager.getInstance()
        var completed = false
        lateinit var timeout: Runnable

        fun finish(result: CookieCleanupResult) {
            if (completed) {
                return
            }
            completed = true
            mainHandler.removeCallbacks(timeout)
            runCatching { cookieManager.flush() }
            onResult(result)
        }

        fun finishAfterCallback() {
            val rootCookies = oldEndpoint?.rootUrl
                ?.let { safeCookieLookup(cookieManager, it) }
                ?: CookieLookupResult.Success(null)
            val tsdCookies = oldEndpoint?.tsdUrl()
                ?.let { safeCookieLookup(cookieManager, it) }
                ?: CookieLookupResult.Success(null)
            finish(CookieCleanupVerifier.verify(rootCookies, tsdCookies))
        }

        timeout = Runnable { finish(CookieCleanupResult.Timeout) }
        mainHandler.postDelayed(timeout, COOKIE_CLEANUP_TIMEOUT_MS)
        runCatching {
            cookieManager.removeAllCookies {
                mainHandler.post { finishAfterCallback() }
            }
        }.onFailure {
            mainHandler.removeCallbacks(timeout)
            onStatus("cookie cleanup failed: ${it.javaClass.simpleName}")
            finish(CookieCleanupResult.Error(it.javaClass.simpleName))
        }
    }

    private fun safeCookieLookup(cookieManager: CookieManager, url: String): CookieLookupResult =
        runCatching { CookieLookupResult.Success(cookieManager.getCookie(url)) }
            .getOrElse { CookieLookupResult.Error(it.javaClass.simpleName) }

    private fun syncScanner() {
        if (scannerState.scannerShouldRun) {
            scannerAdapter.start()
        } else {
            scannerAdapter.stop()
        }
    }

    companion object {
        private const val COOKIE_CLEANUP_TIMEOUT_MS = 2_000L
        private const val NEUTRAL_LOAD_TIMEOUT_MS = 1_000L
        private const val NEUTRAL_URL = "about:blank"
    }
}
