package ru.flowstock.tsd.web

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.json.JSONObject

class FlowStockWebViewControllerTest {
    @Test
    fun originRequiresHttpsAndMatchesPort() {
        val trusted = "https://example.test:7154"

        assertEquals(trusted, FlowStockWebViewController.originOf("https://example.test:7154/tsd/"))
        assertTrue(FlowStockWebViewController.isTrustedUrl("https://example.test:7154/tsd/#/hu", trusted))
        assertTrue(FlowStockWebViewController.isTrustedUrl("https://example.test:7154/tsd/?flowstockNative=1#/hu", trusted))
        assertFalse(FlowStockWebViewController.isTrustedUrl("http://example.test:7154/tsd/", trusted))
        assertFalse(FlowStockWebViewController.isTrustedUrl("https://evil.local:7154/tsd/", trusted))
        assertFalse(FlowStockWebViewController.isTrustedUrl("https://example.test:7155/tsd/", trusted))
    }

    @Test
    fun nativeBridgeUrlAddsFlowstockNativeQueryMarker() {
        assertEquals(
            "https://example.test:7154/tsd/?flowstockNative=1",
            FlowStockWebViewController.withNativeBridgeQueryMarker("https://example.test:7154/tsd/"),
        )
        assertEquals(
            "https://example.test:7154/tsd/?a=1&flowstockNative=1",
            FlowStockWebViewController.withNativeBridgeQueryMarker("https://example.test:7154/tsd/?a=1"),
        )
        assertEquals(
            "https://example.test:7154/tsd/?a=1&flowstockNative=1&b=2",
            FlowStockWebViewController.withNativeBridgeQueryMarker(
                "https://example.test:7154/tsd/?a=1&flowstockNative=0&b=2",
            ),
        )
        assertEquals(
            "https://example.test:7154/tsd/?flowstockNative=1#/orders",
            FlowStockWebViewController.withNativeBridgeQueryMarker("https://example.test:7154/tsd/#/orders"),
        )
    }

    @Test
    fun exactNativeBridgeQueryMarkerIsDetectedOnlyInQuery() {
        assertTrue(
            FlowStockWebViewController.hasExactNativeBridgeQueryMarker(
                "https://example.test:7154/tsd/?flowstockNative=1",
            ),
        )
        assertTrue(
            FlowStockWebViewController.hasExactNativeBridgeQueryMarker(
                "https://example.test:7154/tsd/?flowstockNative=1&a=2",
            ),
        )
        assertTrue(
            FlowStockWebViewController.hasExactNativeBridgeQueryMarker(
                "https://example.test:7154/tsd/?a=2&flowstockNative=1&b=3",
            ),
        )
        assertTrue(
            FlowStockWebViewController.hasExactNativeBridgeQueryMarker(
                "https://example.test:7154/tsd/?a=2&flowstockNative=1#/route?flowstockNative=0",
            ),
        )

        assertFalse(
            FlowStockWebViewController.hasExactNativeBridgeQueryMarker(
                "https://example.test:7154/tsd/flowstockNative=1",
            ),
        )
        assertFalse(
            FlowStockWebViewController.hasExactNativeBridgeQueryMarker(
                "https://example.test:7154/tsd/#/route?flowstockNative=1",
            ),
        )
        assertFalse(
            FlowStockWebViewController.hasExactNativeBridgeQueryMarker(
                "https://example.test:7154/tsd/?flowstockNative=0",
            ),
        )
        assertFalse(
            FlowStockWebViewController.hasExactNativeBridgeQueryMarker(
                "https://example.test:7154/tsd/?flowstockNative=10",
            ),
        )
        assertFalse(
            FlowStockWebViewController.hasExactNativeBridgeQueryMarker(
                "https://example.test:7154/tsd/?xflowstockNative=1",
            ),
        )
    }

    @Test
    fun nativeBridgeCookiePathUsesTsdDirectory() {
        assertEquals(
            "/tsd/",
            FlowStockWebViewController.nativeBridgeCookiePath("https://example.test:7154/tsd/"),
        )
        assertEquals(
            "/tsd/",
            FlowStockWebViewController.nativeBridgeCookiePath("https://example.test:7154/tsd/index.html"),
        )
    }

    @Test
    fun nativeBridgeCookieIsHostOnlySecureSessionCookie() {
        val cookie = FlowStockWebViewController.buildNativeBridgeSessionCookie(
            "https://example.test:7154/tsd/index.html",
        )

        assertTrue(cookie.contains("flowstockNative=1"))
        assertTrue(cookie.contains("Path=/tsd/"))
        assertTrue(cookie.contains("Secure"))
        assertFalse(cookie.contains("Domain"))
        assertFalse(cookie.contains("Expires"))
        assertFalse(cookie.contains("Max-Age"))
        assertEquals(1, Regex("flowstockNative=1").findAll(cookie).count())
    }

    @Test
    fun nativeBridgeCookieMarkerIsDetectedOnlyAsExactCookiePair() {
        assertTrue(FlowStockWebViewController.hasExactNativeBridgeCookieMarker("a=1; flowstockNative=1; b=2"))
        assertFalse(FlowStockWebViewController.hasExactNativeBridgeCookieMarker(null))
        assertFalse(FlowStockWebViewController.hasExactNativeBridgeCookieMarker(""))
        assertFalse(FlowStockWebViewController.hasExactNativeBridgeCookieMarker("flowstockNative=0"))
        assertFalse(FlowStockWebViewController.hasExactNativeBridgeCookieMarker("flowstockNative=10"))
        assertFalse(FlowStockWebViewController.hasExactNativeBridgeCookieMarker("xflowstockNative=1"))
        assertFalse(FlowStockWebViewController.hasExactNativeBridgeCookieMarker("other=flowstockNative=1"))
    }

    @Test
    fun loadCoordinatorAllowsExactlyOneCompletionPerGeneration() {
        val coordinator = OneShotGenerationLoadCoordinator()

        assertTrue(coordinator.tryComplete(targetGeneration = 1, currentGeneration = 1))
        assertFalse(coordinator.tryComplete(targetGeneration = 1, currentGeneration = 1))
        assertFalse(coordinator.tryComplete(targetGeneration = 1, currentGeneration = 2))
        assertTrue(coordinator.tryComplete(targetGeneration = 2, currentGeneration = 2))
    }

    @Test
    fun loadCoordinatorLetsCallbackWinBeforeTimeout() {
        val coordinator = OneShotGenerationLoadCoordinator()

        assertTrue(coordinator.tryComplete(targetGeneration = 10, currentGeneration = 10))
        assertFalse(coordinator.tryComplete(targetGeneration = 10, currentGeneration = 10))
    }

    @Test
    fun loadCoordinatorLetsTimeoutWinBeforeCallback() {
        val coordinator = OneShotGenerationLoadCoordinator()

        assertTrue(coordinator.tryComplete(targetGeneration = 10, currentGeneration = 10))
        assertFalse(coordinator.tryComplete(targetGeneration = 10, currentGeneration = 10))
    }

    @Test
    fun loadCoordinatorRejectsLosingCompletionAsNotCurrent() {
        val coordinator = OneShotGenerationLoadCoordinator()

        assertFalse(coordinator.tryComplete(targetGeneration = 10, currentGeneration = 11))
        assertTrue(coordinator.tryComplete(targetGeneration = 11, currentGeneration = 11))
        assertFalse(coordinator.tryComplete(targetGeneration = 10, currentGeneration = 11))
    }

    @Test
    fun loadCoordinatorAllowsNewGenerationAfterPreviousCompletion() {
        val coordinator = OneShotGenerationLoadCoordinator()

        assertTrue(coordinator.tryComplete(targetGeneration = 10, currentGeneration = 10))
        assertTrue(coordinator.tryComplete(targetGeneration = 11, currentGeneration = 11))
        assertFalse(coordinator.tryComplete(targetGeneration = 10, currentGeneration = 11))
        assertFalse(coordinator.tryComplete(targetGeneration = 11, currentGeneration = 11))
    }

    @Test
    fun appendsNativeUserAgentTokenOnce() {
        assertEquals(
            "Mozilla FlowStockTsdNative/1",
            FlowStockWebViewController.appendNativeUserAgentToken("Mozilla"),
        )
        assertEquals(
            "Mozilla FlowStockTsdNative/1",
            FlowStockWebViewController.appendNativeUserAgentToken("Mozilla FlowStockTsdNative/1"),
        )
        assertEquals(
            "Mozilla FlowStockTsdNative/1",
            FlowStockWebViewController.appendNativeUserAgentToken(
                "Mozilla FlowStockTsdNative/1 FlowStockTsdNative/1",
            ),
        )
        assertEquals(
            "FlowStockTsdNative/1",
            FlowStockWebViewController.appendNativeUserAgentToken(null),
        )
        assertEquals(
            "FlowStockTsdNative/1",
            FlowStockWebViewController.appendNativeUserAgentToken("   "),
        )
    }

    @Test
    fun nativeUserAgentFallsBackToCurrentSettingsWhenDefaultIsBlank() {
        assertEquals(
            "DefaultUA FlowStockTsdNative/1",
            FlowStockWebViewController.buildNativeUserAgent("DefaultUA", "CurrentUA"),
        )
        assertEquals(
            "CurrentUA FlowStockTsdNative/1",
            FlowStockWebViewController.buildNativeUserAgent("", "CurrentUA"),
        )
        assertEquals(
            "CurrentUA FlowStockTsdNative/1",
            FlowStockWebViewController.buildNativeUserAgent(null, "CurrentUA FlowStockTsdNative/1"),
        )
    }

    @Test
    fun userAgentDiagnosticsDoNotExposeFullUserAgent() {
        val userAgent = "Mozilla/5.0 SECRET-UA FlowStockTsdNative/1"
        val diagnostic = FlowStockWebViewController.createNativeUserAgentDiagnostic(userAgent)

        assertTrue(diagnostic.hasNativeUserAgentToken)
        assertEquals(userAgent.length, diagnostic.length)

        val setupStatus = FlowStockWebViewController.formatNativeUserAgentStatus(diagnostic)
        assertTrue(setupStatus.contains("nativeUaToken=true"))
        assertFalse(setupStatus.contains("SECRET-UA"))
        assertFalse(setupStatus.contains("Mozilla/5.0"))

        val probeStatus = FlowStockWebViewController.formatBridgeProbeStatus(
            state = BridgeState.PageLoaded,
            probe = BridgeProbeResult(
                parseSuccess = true,
                resultState = "ok",
                rawResultLength = 42,
                rawResultHash = "abcd1234",
                hasNativeUserAgentToken = true,
                hasSearchMarker = true,
                hasCookieMarker = true,
                pageUaAccess = "ok",
                pageQueryAccess = "error",
                pageCookieAccess = "ok",
                pageQueryMarker = true,
                pageCookieMarker = true,
                activationSource = "cookie",
                nativeCookieStoreMarker = true,
                hasBridgeObject = true,
                dispatchType = "function",
                bridgeReady = true,
                activeScanSubscriptionCount = 1,
                serviceWorkerState = "available",
            ),
            nativeUserAgent = diagnostic,
            navigation = NavigationMarkerDiagnostics(
                requested = UrlMarkerDiagnostic(true, 45, "11112222", true),
                started = UrlMarkerDiagnostic(true, 45, "11112222", true),
                finished = UrlMarkerDiagnostic(true, 45, "11112222", true),
                startedCount = 1,
                finishedCount = 1,
            ),
            cookie = CookieMarkerDiagnostic(
                cookieAcceptEnabled = true,
                cookieSet = true,
                cookieSetResult = "ok",
                nativeCookieStoreMarker = true,
                nativeCookieStoreReadResult = "ok",
            ),
        )
        assertTrue(probeStatus.contains("nativeUaToken=true"))
        assertTrue(probeStatus.contains("uaToken=true"))
        assertTrue(probeStatus.contains("requestedMarker=true"))
        assertTrue(probeStatus.contains("startedMarker=true"))
        assertTrue(probeStatus.contains("finishedMarker=true"))
        assertTrue(probeStatus.contains("cookieAcceptEnabled=true"))
        assertTrue(probeStatus.contains("cookieSet=true"))
        assertTrue(probeStatus.contains("cookieSetResult=ok"))
        assertTrue(probeStatus.contains("nativeCookieStoreMarker=true"))
        assertTrue(probeStatus.contains("nativeCookieStoreReadResult=ok"))
        assertTrue(probeStatus.contains("pageUaAccess=ok"))
        assertTrue(probeStatus.contains("pageQueryAccess=error"))
        assertTrue(probeStatus.contains("pageCookieAccess=ok"))
        assertTrue(probeStatus.contains("pageQueryMarker=true"))
        assertTrue(probeStatus.contains("pageCookieMarker=true"))
        assertTrue(probeStatus.contains("activationSource=cookie"))
        assertTrue(probeStatus.contains("cookieMarker=true"))
        assertFalse(probeStatus.contains("SECRET-UA"))
        assertFalse(probeStatus.contains("Mozilla/5.0"))
    }

    @Test
    fun debugLifecycleDiagnosticsAreAllowlisted() {
        assertTrue(FlowStockWebViewController.isDebugLifecycleDiagnostic("cookie marker: cookieSet=true"))
        assertTrue(FlowStockWebViewController.isDebugLifecycleDiagnostic("bridge probe: state=PageLoaded"))
        assertTrue(FlowStockWebViewController.isDebugLifecycleDiagnostic("page started: state=Loading"))
        assertTrue(FlowStockWebViewController.isDebugLifecycleDiagnostic("page finished: state=PageLoaded"))
        assertTrue(FlowStockWebViewController.isDebugLifecycleDiagnostic("ssl error: cancelled state=Error"))
        assertTrue(FlowStockWebViewController.isDebugLifecycleDiagnostic("main-frame error: state=Error code=-2"))
        assertTrue(FlowStockWebViewController.isDebugLifecycleDiagnostic("main-frame http error: 404"))
        assertTrue(
            FlowStockWebViewController.isDebugLifecycleDiagnostic(
                "cookie marker ignored: completed-or-stale result=timeout",
            ),
        )
        assertTrue(FlowStockWebViewController.isDebugLifecycleDiagnostic("bridge probe ignored: stale generation"))

        assertFalse(FlowStockWebViewController.isDebugLifecycleDiagnostic("scan delivered: len=8 hash=abc"))
        assertFalse(FlowStockWebViewController.isDebugLifecycleDiagnostic("console LOG: len=8 hash=abc"))
        assertFalse(FlowStockWebViewController.isDebugLifecycleDiagnostic("webview ua: nativeUaToken=true"))
        assertFalse(FlowStockWebViewController.isDebugLifecycleDiagnostic("load url: requestedMarker=true"))
    }

    @Test
    fun javaScriptStringLiteralEscapesQuotesBackslashAndControls() {
        val input = "A\"B\\C\n\r\t\u001D\u2028"
        val literal = FlowStockWebViewController.toJavaScriptStringLiteral(input)

        assertEquals("\"A\\\"B\\\\C\\n\\r\\t\\u001d\\u2028\"", literal)
    }

    @Test
    fun decodeEvaluateJavascriptStringHandlesQuotedResult() {
        assertEquals(
            "{\"bridgeReady\":true}",
            FlowStockWebViewController.decodeEvaluateJavascriptString("\"{\\\"bridgeReady\\\":true}\""),
        )
        assertEquals(
            "{\"bridgeReady\":true}",
            FlowStockWebViewController.decodeEvaluateJavascriptString("\"\\\"{\\\\\\\"bridgeReady\\\\\\\":true}\\\"\""),
        )
        assertEquals("", FlowStockWebViewController.decodeEvaluateJavascriptString("null"))
    }

    @Test
    fun parseBridgeProbeResultHandlesReadyBridgeWithSubscribers() {
        val result = FlowStockWebViewController.parseBridgeProbeResult(
            jsonString(probeLine(uaToken = true, bridgeObject = true, bridgeReady = true, subscribers = 2)),
        )

        assertTrue(result.parseSuccess)
        assertEquals("ok", result.resultState)
        assertTrue(result.hasNativeUserAgentToken)
        assertTrue(result.hasSearchMarker)
        assertTrue(result.hasCookieMarker)
        assertEquals("ok", result.pageUaAccess)
        assertEquals("ok", result.pageQueryAccess)
        assertEquals("ok", result.pageCookieAccess)
        assertTrue(result.pageQueryMarker)
        assertTrue(result.pageCookieMarker)
        assertEquals("cookie", result.activationSource)
        assertTrue(result.nativeCookieStoreMarker)
        assertTrue(result.hasBridgeObject)
        assertEquals("function", result.dispatchType)
        assertTrue(result.bridgeReady)
        assertEquals(2, result.activeScanSubscriptionCount)
        assertEquals("available", result.serviceWorkerState)
    }

    @Test
    fun parseBridgeProbeResultHandlesBridgeMissingAndUaMissing() {
        val bridgeMissing = FlowStockWebViewController.parseBridgeProbeResult(
            jsonString(probeLine(uaToken = true, bridgeObject = false, dispatchType = "undefined")),
        )
        assertTrue(bridgeMissing.parseSuccess)
        assertTrue(bridgeMissing.hasNativeUserAgentToken)
        assertTrue(bridgeMissing.hasSearchMarker)
        assertTrue(bridgeMissing.hasCookieMarker)
        assertFalse(bridgeMissing.hasBridgeObject)
        assertFalse(bridgeMissing.bridgeReady)
        assertEquals(0, bridgeMissing.activeScanSubscriptionCount)

        val uaMissing = FlowStockWebViewController.parseBridgeProbeResult(
            jsonString(probeLine(uaToken = false, bridgeObject = true, bridgeReady = true, subscribers = 1)),
        )
        assertTrue(uaMissing.parseSuccess)
        assertFalse(uaMissing.hasNativeUserAgentToken)
        assertTrue(uaMissing.hasSearchMarker)
        assertTrue(uaMissing.hasCookieMarker)
        assertTrue(uaMissing.hasBridgeObject)
        assertTrue(uaMissing.bridgeReady)
    }

    @Test
    fun parseBridgeProbeResultHandlesNullEmptyAndEncodingVariants() {
        val nullResult = FlowStockWebViewController.parseBridgeProbeResult(null)
        assertFalse(nullResult.parseSuccess)
        assertEquals("null", nullResult.resultState)

        val emptyResult = FlowStockWebViewController.parseBridgeProbeResult("")
        assertFalse(emptyResult.parseSuccess)
        assertEquals("empty", emptyResult.resultState)

        val line = probeLine(uaToken = true, bridgeObject = true, bridgeReady = true, subscribers = 1)
        val singleEncoded = FlowStockWebViewController.parseBridgeProbeResult(jsonString(line))
        assertTrue(singleEncoded.parseSuccess)
        assertEquals(1, singleEncoded.activeScanSubscriptionCount)

        val doubleEncoded = FlowStockWebViewController.parseBridgeProbeResult(jsonString(jsonString(line)))
        assertTrue(doubleEncoded.parseSuccess)
        assertEquals(1, doubleEncoded.activeScanSubscriptionCount)
        assertTrue(doubleEncoded.bridgeReady)
    }

    @Test
    fun parseBridgeProbeResultHandlesJsExceptionAsDiagnosticResult() {
        val result = FlowStockWebViewController.parseBridgeProbeResult(
            jsonString(
                probeLine(
                    uaToken = true,
                    bridgeObject = true,
                    bridgeReady = false,
                    subscribers = 0,
                    serviceWorker = "error",
                    jsError = "TypeError",
                ),
            ),
        )

        assertTrue(result.parseSuccess)
        assertFalse(result.bridgeReady)
        assertEquals("error", result.serviceWorkerState)
        assertEquals("TypeError", result.jsErrorName)
    }

    @Test
    fun parseBridgeProbeResultRejectsMalformedPipeFields() {
        val shortResult = FlowStockWebViewController.parseBridgeProbeResult(
            jsonString("FS_PROBE_V5|1|ok|1|ok|1|ok|cookie|1|1|function|1|1|available"),
        )
        assertFalse(shortResult.parseSuccess)
        assertFalse(shortResult.bridgeReady)
        assertEquals("BadFieldCount", shortResult.parseErrorName)

        val extraResult = FlowStockWebViewController.parseBridgeProbeResult(
            jsonString("FS_PROBE_V5|1|ok|1|ok|1|ok|cookie|1|1|function|1|1|available||extra"),
        )
        assertFalse(extraResult.parseSuccess)
        assertFalse(extraResult.bridgeReady)
        assertEquals("BadFieldCount", extraResult.parseErrorName)
    }

    @Test
    fun contradictoryProbeDoesNotBecomeBridgeReady() {
        val noObject = FlowStockWebViewController.parseBridgeProbeResult(
            jsonString(probeLine(bridgeObject = false, dispatchType = "function", bridgeReady = true)),
        )
        assertTrue(noObject.parseSuccess)
        assertFalse(noObject.bridgeReady)

        val noDispatch = FlowStockWebViewController.parseBridgeProbeResult(
            jsonString(probeLine(bridgeObject = true, dispatchType = "undefined", bridgeReady = true)),
        )
        assertTrue(noDispatch.parseSuccess)
        assertFalse(noDispatch.bridgeReady)
    }

    @Test
    fun legacyJsonProbeIsAlsoFailClosed() {
        val contradictory = FlowStockWebViewController.parseBridgeProbeResult(
            JSONObject(
                mapOf(
                    "bridgeReady" to true,
                    "hasBridgeObject" to false,
                    "dispatchType" to "function",
                    "activeScanSubscriptionCount" to 1,
                ),
            ).toString(),
        )

        assertTrue(contradictory.parseSuccess)
        assertFalse(contradictory.bridgeReady)

        val noDispatch = FlowStockWebViewController.parseBridgeProbeResult(
            JSONObject(
                mapOf(
                    "bridgeReady" to true,
                    "hasBridgeObject" to true,
                    "dispatchType" to "undefined",
                    "activeScanSubscriptionCount" to 1,
                ),
            ).toString(),
        )

        assertTrue(noDispatch.parseSuccess)
        assertFalse(noDispatch.bridgeReady)
    }

    @Test
    fun readinessStillRequiresSubscribersAfterProbeParsing() {
        val readiness = BridgeReadinessState()
        val generation = readiness.onPageStarted(trusted = true)
        readiness.onPageFinished(trusted = true)
        val noSubscribers = FlowStockWebViewController.parseBridgeProbeResult(
            jsonString(probeLine(bridgeObject = true, bridgeReady = true, subscribers = 0)),
        )

        readiness.onBridgeProbe(
            generation,
            bridgeReady = noSubscribers.bridgeReady,
            activeScanSubscriptionCount = noSubscribers.activeScanSubscriptionCount,
        )
        assertEquals(BridgeState.BridgeScriptReady, readiness.state)
        assertFalse(readiness.canDispatch())

        val withSubscriber = FlowStockWebViewController.parseBridgeProbeResult(
            jsonString(probeLine(bridgeObject = true, bridgeReady = true, subscribers = 1)),
        )
        readiness.onBridgeProbe(
            generation,
            bridgeReady = withSubscriber.bridgeReady,
            activeScanSubscriptionCount = withSubscriber.activeScanSubscriptionCount,
        )
        assertEquals(BridgeState.ScannerIntentReady, readiness.state)
        assertTrue(readiness.canDispatch())
    }

    @Test
    fun bridgeReadinessRequiresActiveScanSubscription() {
        val readiness = BridgeReadinessState()

        val generation = readiness.onPageStarted(trusted = true)
        assertEquals(BridgeState.Loading, readiness.state)
        assertFalse(readiness.canDispatch())

        readiness.onPageFinished(trusted = true)
        assertTrue(readiness.onBridgeProbe(generation, bridgeReady = true, activeScanSubscriptionCount = 0))
        assertEquals(BridgeState.BridgeScriptReady, readiness.state)
        assertFalse(readiness.canDispatch())

        assertTrue(readiness.onBridgeProbe(generation, bridgeReady = true, activeScanSubscriptionCount = 1))
        assertEquals(BridgeState.ScannerIntentReady, readiness.state)
        assertTrue(readiness.canDispatch())

        readiness.onReloading()
        assertEquals(BridgeState.Reloading, readiness.state)
        assertFalse(readiness.canDispatch())
    }

    @Test
    fun bridgeExistenceWithoutSubscriberDoesNotAllowDispatch() {
        val readiness = BridgeReadinessState()
        val generation = readiness.onPageStarted(trusted = true)

        readiness.onPageFinished(trusted = true)
        readiness.onBridgeProbe(generation, bridgeReady = true, activeScanSubscriptionCount = 0)

        assertEquals(BridgeState.BridgeScriptReady, readiness.state)
        assertFalse(readiness.canDispatch())
    }

    @Test
    fun staleProbeResultAfterReloadIsIgnored() {
        val readiness = BridgeReadinessState()
        val firstGeneration = readiness.onPageStarted(trusted = true)
        readiness.onPageFinished(trusted = true)

        readiness.onPageStarted(trusted = true)

        assertFalse(
            readiness.onBridgeProbe(
                firstGeneration,
                bridgeReady = true,
                activeScanSubscriptionCount = 1,
            ),
        )
        assertEquals(BridgeState.Loading, readiness.state)
        assertFalse(readiness.canDispatch())
    }

    @Test
    fun readinessProbeRetryIsFinite() {
        val readiness = BridgeReadinessState()
        val generation = readiness.onPageStarted(trusted = true)
        readiness.onPageFinished(trusted = true)
        readiness.onBridgeProbe(generation, bridgeReady = true, activeScanSubscriptionCount = 0)

        assertTrue(
            readiness.shouldRetryProbe(
                generation,
                completedAttempt = FlowStockWebViewController.BRIDGE_PROBE_MAX_ATTEMPTS - 1,
                maxAttempts = FlowStockWebViewController.BRIDGE_PROBE_MAX_ATTEMPTS,
            ),
        )
        assertFalse(
            readiness.shouldRetryProbe(
                generation,
                completedAttempt = FlowStockWebViewController.BRIDGE_PROBE_MAX_ATTEMPTS,
                maxAttempts = FlowStockWebViewController.BRIDGE_PROBE_MAX_ATTEMPTS,
            ),
        )
    }

    @Test
    fun untrustedPageBlocksReadiness() {
        val readiness = BridgeReadinessState()
        readiness.onPageStarted(trusted = false)
        assertEquals(BridgeState.BlockedOrigin, readiness.state)
        assertFalse(readiness.canDispatch())
    }

    private fun jsonString(value: String): String = JSONObject.quote(value)

    private fun probeLine(
        uaToken: Boolean = true,
        searchMarker: Boolean = true,
        cookieMarker: Boolean = true,
        uaAccess: String = "ok",
        queryAccess: String = "ok",
        cookieAccess: String = "ok",
        activationSource: String = "cookie",
        nativeCookieStoreMarker: Boolean = true,
        bridgeObject: Boolean = true,
        dispatchType: String = "function",
        bridgeReady: Boolean = true,
        subscribers: Int = 0,
        serviceWorker: String = "available",
        jsError: String = "",
    ): String = listOf(
        "FS_PROBE_V5",
        if (uaToken) "1" else "0",
        uaAccess,
        if (searchMarker) "1" else "0",
        queryAccess,
        if (cookieMarker) "1" else "0",
        cookieAccess,
        activationSource,
        if (nativeCookieStoreMarker) "1" else "0",
        if (bridgeObject) "1" else "0",
        dispatchType,
        if (bridgeReady) "1" else "0",
        subscribers.toString(),
        serviceWorker,
        jsError,
    ).joinToString("|")
}
