package ru.flowstock.tsd.web

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class FlowStockWebViewControllerLifecycleTest {
    @Test
    fun lateCookieCallbackAfterDisposeIsIgnored() {
        val lifecycle = FlowStockWebViewControllerLifecycle()
        val generation = lifecycle.beginGeneration()

        lifecycle.dispose()

        assertFalse(lifecycle.isActive(generation))
        assertFalse(lifecycle.canCallWebView())
    }

    @Test
    fun lateCookieTimeoutAfterDisposeIsIgnored() {
        val lifecycle = FlowStockWebViewControllerLifecycle()
        val generation = lifecycle.beginGeneration()

        lifecycle.dispose()

        assertFalse(lifecycle.isActive(generation))
    }

    @Test
    fun lateBridgeProbeAfterNewGenerationIsIgnored() {
        val lifecycle = FlowStockWebViewControllerLifecycle()
        val oldGeneration = lifecycle.beginGeneration()
        val newGeneration = lifecycle.beginGeneration()

        assertFalse(lifecycle.isActive(oldGeneration))
        assertTrue(lifecycle.isActive(newGeneration))
    }

    @Test
    fun disposedControllerCannotDispatchOrTouchWebView() {
        val lifecycle = FlowStockWebViewControllerLifecycle()

        lifecycle.dispose()

        assertFalse(lifecycle.canCallWebView())
    }
}
