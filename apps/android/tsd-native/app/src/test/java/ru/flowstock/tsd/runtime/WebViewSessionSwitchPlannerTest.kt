package ru.flowstock.tsd.runtime

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import ru.flowstock.tsd.config.ServerEndpoint

class WebViewSessionSwitchPlannerTest {
    @Test
    fun savedEndpointStartupDoesNotRequireCleanup() {
        val endpoint = ServerEndpoint("https://flowstock.local:7154")

        val plan = WebViewSessionSwitchPlanner.plan(
            currentEndpoint = endpoint,
            targetEndpoint = endpoint,
            hasController = false,
            setupVisible = true,
        )

        assertEquals(WebViewSessionSwitchKind.SameOriginReload, plan.kind)
        assertFalse(plan.requiresCleanup)
    }

    @Test
    fun actualOriginChangeRequiresCleanup() {
        val plan = WebViewSessionSwitchPlanner.plan(
            currentEndpoint = ServerEndpoint("https://old.local:7154"),
            targetEndpoint = ServerEndpoint("https://new.local:7154"),
            hasController = true,
            setupVisible = true,
        )

        assertEquals(WebViewSessionSwitchKind.OriginChange, plan.kind)
        assertTrue(plan.requiresCleanup)
    }

    @Test
    fun sameHostnameDifferentPortIsOriginChangeAndRequiresCleanup() {
        val plan = WebViewSessionSwitchPlanner.plan(
            currentEndpoint = ServerEndpoint("https://flowstock.local:7154"),
            targetEndpoint = ServerEndpoint("https://flowstock.local:7155"),
            hasController = true,
            setupVisible = true,
        )

        assertEquals(WebViewSessionSwitchKind.OriginChange, plan.kind)
        assertTrue(plan.requiresCleanup)
    }

    @Test
    fun setupWithCurrentControllerRestoresCurrentSession() {
        val endpoint = ServerEndpoint("https://flowstock.local:7154")

        val plan = WebViewSessionSwitchPlanner.plan(
            currentEndpoint = endpoint,
            targetEndpoint = endpoint,
            hasController = true,
            setupVisible = true,
        )

        assertEquals(WebViewSessionSwitchKind.RestoreCurrent, plan.kind)
        assertFalse(plan.requiresCleanup)
    }

    @Test
    fun destroyedActivityIgnoresAsyncCallbacks() {
        val gate = ActivityOperationGate()
        val operation = gate.begin()

        gate.destroy()

        assertFalse(gate.canApply(operation))
    }

    @Test
    fun newerOperationWinsOverSavedEndpointValidation() {
        val gate = ActivityOperationGate()
        val savedValidation = gate.begin()
        val manualValidation = gate.begin()

        assertFalse(gate.canApply(savedValidation))
        assertTrue(gate.canApply(manualValidation))
    }
}
