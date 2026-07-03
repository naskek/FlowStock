package ru.flowstock.tsd.web

class FlowStockWebViewControllerLifecycle {
    private var generation: Long = 0L
    var disposed: Boolean = false
        private set

    fun beginGeneration(): Long {
        if (disposed) return generation
        generation += 1L
        return generation
    }

    fun dispose() {
        disposed = true
        generation += 1L
    }

    fun isActive(candidate: Long): Boolean =
        !disposed && candidate == generation

    fun canCallWebView(): Boolean = !disposed
}
