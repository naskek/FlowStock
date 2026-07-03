package ru.flowstock.tsd.runtime

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import ru.flowstock.tsd.config.ServerEndpoint

class FlowStockWebViewSessionSwitchOrchestratorTest {
    private val oldEndpoint = ServerEndpoint("https://flowstock.local:7154")
    private val sameHostNewPortEndpoint = ServerEndpoint("https://flowstock.local:7155")
    private val newEndpoint = ServerEndpoint("https://new.local:7154")

    @Test
    fun newControllerFactoryThrowsRestoresOldControllerAndCallsFailureOnce() {
        val operations = FakeSwitchOperations().also {
            it.throwFactoryFor += newEndpoint.rootUrl
        }
        val failures = mutableListOf<Boolean>()
        val orchestrator = FlowStockWebViewSessionSwitchOrchestrator(operations)

        orchestrator.switchOrigin(oldEndpoint, newEndpoint, onComplete = {}, onFailure = failures::add)
        operations.completeNeutral()
        operations.completeCookieCleanup(CookieCleanupResult.Cleared)
        operations.completeCookieCleanup(CookieCleanupResult.Cleared)

        assertEquals(listOf(true), failures)
        assertTrue(operations.events.contains("create:${oldEndpoint.rootUrl}"))
        assertTrue(operations.events.contains("load:${oldEndpoint.rootUrl}"))
        assertFalse(operations.events.contains("load:${newEndpoint.rootUrl}"))
    }

    @Test
    fun newControllerLoadThrowsRestoresOldControllerAndCallsFailureOnce() {
        val operations = FakeSwitchOperations().also {
            it.throwLoadFor += newEndpoint.rootUrl
        }
        val failures = mutableListOf<Boolean>()
        val orchestrator = FlowStockWebViewSessionSwitchOrchestrator(operations)

        orchestrator.switchOrigin(oldEndpoint, newEndpoint, onComplete = {}, onFailure = failures::add)
        operations.completeNeutral()
        operations.completeCookieCleanup(CookieCleanupResult.Cleared)

        assertEquals(listOf(true), failures)
        assertTrue(operations.events.contains("create:${oldEndpoint.rootUrl}"))
        assertTrue(operations.events.contains("load:${oldEndpoint.rootUrl}"))
    }

    @Test
    fun rollbackIsNotBlockedByControllerGenerationChanges() {
        val operations = FakeSwitchOperations().also {
            it.throwLoadFor += newEndpoint.rootUrl
        }
        val failures = mutableListOf<Boolean>()
        val orchestrator = FlowStockWebViewSessionSwitchOrchestrator(operations)

        orchestrator.switchOrigin(oldEndpoint, newEndpoint, onComplete = {}, onFailure = failures::add)
        operations.completeNeutral()
        operations.completeCookieCleanup(CookieCleanupResult.Cleared)

        assertEquals(2, operations.controllerGeneration)
        assertEquals(listOf(true), failures)
        assertEquals("load:${oldEndpoint.rootUrl}", operations.events.last())
    }

    @Test
    fun stalePreviousSwitchCallbacksDoNotAffectNewOperation() {
        val operations = FakeSwitchOperations()
        val failures = mutableListOf<Boolean>()
        val completions = mutableListOf<String>()
        val orchestrator = FlowStockWebViewSessionSwitchOrchestrator(operations)

        orchestrator.switchOrigin(oldEndpoint, newEndpoint, onComplete = { completions += "old" }, onFailure = failures::add)
        operations.completeNeutral()
        val staleCookie = operations.cookieCallbacks.last()
        orchestrator.switchOrigin(oldEndpoint, sameHostNewPortEndpoint, onComplete = { completions += "new" }, onFailure = failures::add)

        staleCookie.invoke(CookieCleanupResult.Cleared)
        operations.completeNeutral()
        operations.completeCookieCleanup(CookieCleanupResult.Cleared)

        assertEquals(listOf("new"), completions)
        assertTrue(failures.isEmpty())
        assertFalse(operations.events.contains("create:${newEndpoint.rootUrl}"))
        assertTrue(operations.events.contains("create:${sameHostNewPortEndpoint.rootUrl}"))
    }

    @Test
    fun rootEmptyButTsdCookieRemainingAbortsSwitchAndRestoresOldEndpoint() {
        val operations = FakeSwitchOperations()
        val failures = mutableListOf<Boolean>()
        val orchestrator = FlowStockWebViewSessionSwitchOrchestrator(operations)

        orchestrator.switchOrigin(oldEndpoint, newEndpoint, onComplete = {}, onFailure = failures::add)
        operations.completeNeutral()
        operations.completeCookieCleanup(
            CookieCleanupVerifier.verify(
                rootCookies = CookieLookupResult.Success(""),
                tsdCookies = CookieLookupResult.Success("flowstockNative=1"),
            ),
        )

        assertEquals(listOf(true), failures)
        assertFalse(operations.events.contains("create:${newEndpoint.rootUrl}"))
        assertTrue(operations.events.contains("create:${oldEndpoint.rootUrl}"))
    }

    @Test
    fun rootCookieLookupErrorAbortsSwitchAndDoesNotLoadNewOrigin() {
        val operations = FakeSwitchOperations()
        val failures = mutableListOf<Boolean>()
        val orchestrator = FlowStockWebViewSessionSwitchOrchestrator(operations)

        orchestrator.switchOrigin(oldEndpoint, newEndpoint, onComplete = {}, onFailure = failures::add)
        operations.completeNeutral()
        operations.completeCookieCleanup(
            CookieCleanupVerifier.verify(
                rootCookies = CookieLookupResult.Error("IllegalStateException"),
                tsdCookies = CookieLookupResult.Success(""),
            ),
        )

        assertEquals(listOf(true), failures)
        assertFalse(operations.events.contains("create:${newEndpoint.rootUrl}"))
        assertTrue(operations.events.contains("create:${oldEndpoint.rootUrl}"))
    }

    @Test
    fun tsdCookieLookupErrorAbortsSwitchAndDoesNotLoadNewOrigin() {
        val operations = FakeSwitchOperations()
        val failures = mutableListOf<Boolean>()
        val orchestrator = FlowStockWebViewSessionSwitchOrchestrator(operations)

        orchestrator.switchOrigin(oldEndpoint, newEndpoint, onComplete = {}, onFailure = failures::add)
        operations.completeNeutral()
        operations.completeCookieCleanup(
            CookieCleanupVerifier.verify(
                rootCookies = CookieLookupResult.Success(""),
                tsdCookies = CookieLookupResult.Error("IllegalStateException"),
            ),
        )

        assertEquals(listOf(true), failures)
        assertFalse(operations.events.contains("create:${newEndpoint.rootUrl}"))
        assertTrue(operations.events.contains("create:${oldEndpoint.rootUrl}"))
    }

    @Test
    fun rootAndTsdCookiesEmptyContinueToNewOrigin() {
        val operations = FakeSwitchOperations()
        val completions = mutableListOf("pending")
        val orchestrator = FlowStockWebViewSessionSwitchOrchestrator(operations)

        orchestrator.switchOrigin(oldEndpoint, newEndpoint, onComplete = { completions += "complete" }, onFailure = {})
        operations.completeNeutral()
        operations.completeCookieCleanup(
            CookieCleanupVerifier.verify(
                rootCookies = CookieLookupResult.Success(null),
                tsdCookies = CookieLookupResult.Success(""),
            ),
        )

        assertTrue(completions.contains("complete"))
        assertTrue(operations.events.contains("create:${newEndpoint.rootUrl}"))
        assertTrue(operations.events.contains("clearHistory"))
    }

    @Test
    fun sameHostnameDifferentPortDoesNotTransferTsdCookie() {
        val operations = FakeSwitchOperations()
        val failures = mutableListOf<Boolean>()
        val orchestrator = FlowStockWebViewSessionSwitchOrchestrator(operations)

        orchestrator.switchOrigin(oldEndpoint, sameHostNewPortEndpoint, onComplete = {}, onFailure = failures::add)
        operations.completeNeutral()
        operations.completeCookieCleanup(
            CookieCleanupVerifier.verify(
                rootCookies = CookieLookupResult.Success(""),
                tsdCookies = CookieLookupResult.Success("session=old"),
            ),
        )

        assertEquals(listOf(true), failures)
        assertFalse(operations.events.contains("create:${sameHostNewPortEndpoint.rootUrl}"))
        assertTrue(operations.events.contains("create:${oldEndpoint.rootUrl}"))
    }

    @Test
    fun timeoutAbortsSwitchRestoresOldAndDoesNotLoadNewOrigin() {
        val operations = FakeSwitchOperations()
        val failures = mutableListOf<Boolean>()
        val orchestrator = FlowStockWebViewSessionSwitchOrchestrator(operations)

        orchestrator.switchOrigin(oldEndpoint, newEndpoint, onComplete = {}, onFailure = failures::add)
        operations.completeNeutral()
        operations.completeCookieCleanup(CookieCleanupResult.Timeout)

        assertEquals(listOf(true), failures)
        assertFalse(operations.events.contains("create:${newEndpoint.rootUrl}"))
        assertTrue(operations.events.contains("create:${oldEndpoint.rootUrl}"))
    }

    @Test
    fun fallbackFailureReportsSetupErrorWithoutSavingPendingEndpoint() {
        val operations = FakeSwitchOperations().also {
            it.throwFactoryFor += newEndpoint.rootUrl
            it.throwFactoryFor += oldEndpoint.rootUrl
        }
        val failures = mutableListOf<Boolean>()
        val orchestrator = FlowStockWebViewSessionSwitchOrchestrator(operations)

        orchestrator.switchOrigin(oldEndpoint, newEndpoint, onComplete = {}, onFailure = failures::add)
        operations.completeNeutral()
        operations.completeCookieCleanup(CookieCleanupResult.Cleared)

        assertEquals(listOf(false), failures)
    }

    private class FakeSwitchOperations : FlowStockWebViewSessionSwitchOperations {
        val events = mutableListOf<String>()
        val neutralCallbacks = mutableListOf<Pair<() -> Unit, () -> Unit>>()
        val cookieCallbacks = mutableListOf<(CookieCleanupResult) -> Unit>()
        val throwFactoryFor = mutableSetOf<String>()
        val throwLoadFor = mutableSetOf<String>()
        var controllerGeneration = 0

        override fun stopScanner() {
            events += "stopScanner"
        }

        override fun disposeOldController() {
            events += "disposeOldController"
        }

        override fun stopLoading() {
            events += "stopLoading"
        }

        override fun loadNeutralDocument(onComplete: () -> Unit, onFailure: () -> Unit) {
            events += "about:blank"
            neutralCallbacks += onComplete to onFailure
        }

        override fun cleanupCookies(oldEndpoint: ServerEndpoint?, onResult: (CookieCleanupResult) -> Unit) {
            events += "cleanupCookies:${oldEndpoint?.rootUrl.orEmpty()}:${oldEndpoint?.tsdUrl().orEmpty()}"
            cookieCallbacks += onResult
        }

        override fun cleanupWebStorage(oldEndpoint: ServerEndpoint?) {
            events += "cleanupWebStorage:${oldEndpoint?.rootUrl.orEmpty()}"
        }

        override fun clearHistory() {
            events += "clearHistory"
        }

        override fun createAndLoadController(endpoint: ServerEndpoint) {
            controllerGeneration += 1
            events += "create:${endpoint.rootUrl}"
            if (endpoint.rootUrl in throwFactoryFor) {
                throw IllegalStateException("factory")
            }
            events += "load:${endpoint.rootUrl}"
            if (endpoint.rootUrl in throwLoadFor) {
                throw IllegalStateException("load")
            }
        }

        fun completeNeutral() {
            neutralCallbacks.last().first.invoke()
        }

        fun completeCookieCleanup(result: CookieCleanupResult) {
            cookieCallbacks.last().invoke(result)
        }
    }
}
