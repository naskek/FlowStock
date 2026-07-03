package ru.flowstock.tsd.runtime

import ru.flowstock.tsd.config.ServerEndpoint

sealed class CookieCleanupResult {
    object Cleared : CookieCleanupResult()
    object CookiesRemain : CookieCleanupResult()
    object Timeout : CookieCleanupResult()
    data class Error(val kind: String) : CookieCleanupResult()
}

sealed class CookieLookupResult {
    data class Success(val value: String?) : CookieLookupResult()
    data class Error(val kind: String) : CookieLookupResult()
}

object CookieCleanupVerifier {
    fun verify(rootCookies: CookieLookupResult, tsdCookies: CookieLookupResult): CookieCleanupResult {
        if (rootCookies is CookieLookupResult.Error) {
            return CookieCleanupResult.Error("root-cookie-lookup-${rootCookies.kind}")
        }
        if (tsdCookies is CookieLookupResult.Error) {
            return CookieCleanupResult.Error("tsd-cookie-lookup-${tsdCookies.kind}")
        }

        val rootValue = (rootCookies as CookieLookupResult.Success).value.orEmpty()
        val tsdValue = (tsdCookies as CookieLookupResult.Success).value.orEmpty()
        return if (rootValue.isBlank() && tsdValue.isBlank()) {
            CookieCleanupResult.Cleared
        } else {
            CookieCleanupResult.CookiesRemain
        }
    }
}

interface FlowStockWebViewSessionSwitchOperations {
    fun stopScanner()
    fun disposeOldController()
    fun stopLoading()
    fun loadNeutralDocument(onComplete: () -> Unit, onFailure: () -> Unit)
    fun cleanupCookies(oldEndpoint: ServerEndpoint?, onResult: (CookieCleanupResult) -> Unit)
    fun cleanupWebStorage(oldEndpoint: ServerEndpoint?)
    fun clearHistory()
    fun createAndLoadController(endpoint: ServerEndpoint)
}

class FlowStockWebViewSessionSwitchOrchestrator(
    private val operations: FlowStockWebViewSessionSwitchOperations,
    private val stateMachineFactory: () -> WebViewSessionSwitchStateMachine = { WebViewSessionSwitchStateMachine() },
) {
    private var activeSwitchGeneration = 0L

    fun cancelActiveSwitch() {
        activeSwitchGeneration += 1L
    }

    fun switchOrigin(
        oldEndpoint: ServerEndpoint?,
        newEndpoint: ServerEndpoint,
        onComplete: () -> Unit,
        onFailure: (restoredCurrentSession: Boolean) -> Unit,
    ) {
        val switchGeneration = ++activeSwitchGeneration
        val stateMachine = stateMachineFactory()
        var completed = false

        fun isCurrent(): Boolean = switchGeneration == activeSwitchGeneration && !completed

        fun completeOnce() {
            if (!isCurrent()) return
            completed = true
            stateMachine.newSessionCreated()
            onComplete()
        }

        fun failOnce(restoredCurrentSession: Boolean) {
            if (!isCurrent()) return
            completed = true
            onFailure(restoredCurrentSession)
        }

        fun restoreOldSession() {
            if (!isCurrent()) return
            stateMachine.newSessionFailed()
            if (oldEndpoint == null) {
                stateMachine.oldSessionRestoreFailed()
                failOnce(false)
                return
            }

            runCatching {
                operations.createAndLoadController(oldEndpoint)
            }.onSuccess {
                stateMachine.oldSessionRestored()
                failOnce(true)
            }.onFailure {
                stateMachine.oldSessionRestoreFailed()
                failOnce(false)
            }
        }

        fun continueAfterCookieCleanup(result: CookieCleanupResult) {
            if (!isCurrent()) return
            when (result) {
                CookieCleanupResult.Cleared -> {
                    stateMachine.cookieCleanupFinished(cookiesAbsent = true)
                    operations.cleanupWebStorage(oldEndpoint)
                    operations.clearHistory()
                    runCatching {
                        operations.createAndLoadController(newEndpoint)
                    }.onSuccess {
                        completeOnce()
                    }.onFailure {
                        restoreOldSession()
                    }
                }
                CookieCleanupResult.CookiesRemain,
                CookieCleanupResult.Timeout,
                is CookieCleanupResult.Error,
                -> {
                    stateMachine.cookieCleanupFinished(cookiesAbsent = false)
                    restoreOldSession()
                }
            }
        }

        stateMachine.begin()
        operations.stopScanner()
        operations.disposeOldController()
        operations.stopLoading()
        operations.loadNeutralDocument(
            onComplete = {
                if (!isCurrent()) return@loadNeutralDocument
                stateMachine.neutralLoaded()
                operations.cleanupCookies(oldEndpoint, ::continueAfterCookieCleanup)
            },
            onFailure = {
                restoreOldSession()
            },
        )
    }
}
