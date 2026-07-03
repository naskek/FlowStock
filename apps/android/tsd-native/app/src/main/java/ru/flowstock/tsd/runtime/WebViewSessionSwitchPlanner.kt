package ru.flowstock.tsd.runtime

import ru.flowstock.tsd.config.ServerEndpoint
import ru.flowstock.tsd.config.ServerEndpointNormalizer

enum class WebViewSessionSwitchKind {
    InitialLoad,
    RestoreCurrent,
    SameOriginReload,
    OriginChange,
}

data class WebViewSessionSwitchPlan(
    val kind: WebViewSessionSwitchKind,
    val requiresCleanup: Boolean,
)

object WebViewSessionSwitchPlanner {
    fun plan(
        currentEndpoint: ServerEndpoint?,
        targetEndpoint: ServerEndpoint,
        hasController: Boolean,
        setupVisible: Boolean,
    ): WebViewSessionSwitchPlan {
        if (currentEndpoint == null) {
            return WebViewSessionSwitchPlan(WebViewSessionSwitchKind.InitialLoad, requiresCleanup = false)
        }

        val sameRoot = ServerEndpointNormalizer.sameRoot(currentEndpoint.rootUrl, targetEndpoint.rootUrl)
        if (sameRoot && hasController && setupVisible) {
            return WebViewSessionSwitchPlan(WebViewSessionSwitchKind.RestoreCurrent, requiresCleanup = false)
        }

        if (sameRoot) {
            return WebViewSessionSwitchPlan(WebViewSessionSwitchKind.SameOriginReload, requiresCleanup = false)
        }

        return WebViewSessionSwitchPlan(WebViewSessionSwitchKind.OriginChange, requiresCleanup = true)
    }
}

class ActivityOperationGate {
    private var generation = 0L
    private var destroyed = false

    fun begin(): Long {
        generation += 1
        return generation
    }

    fun destroy() {
        destroyed = true
        generation += 1
    }

    fun cancelCurrent() {
        generation += 1
    }

    fun canApply(candidate: Long): Boolean = !destroyed && candidate == generation
}
