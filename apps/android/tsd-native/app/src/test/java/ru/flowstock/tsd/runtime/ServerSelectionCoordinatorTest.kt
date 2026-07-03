package ru.flowstock.tsd.runtime

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import ru.flowstock.tsd.config.EndpointStore
import ru.flowstock.tsd.config.ServerEndpoint

class ServerSelectionCoordinatorTest {
    @Test
    fun noSavedEndpointRequiresSetup() {
        val coordinator = ServerSelectionCoordinator(FakeStore(null))

        assertNull(coordinator.start())
        assertEquals(ServerSelectionState.SetupRequired, coordinator.state)
    }

    @Test
    fun failedNewEndpointPreservesOldConfig() {
        val oldEndpoint = ServerEndpoint("https://old.local:7154")
        val store = FakeStore(oldEndpoint)
        val coordinator = ServerSelectionCoordinator(store)
        coordinator.start()

        coordinator.beginValidation()
        coordinator.failSwitch()

        assertEquals(ServerSelectionState.WebViewActive, coordinator.state)
        assertEquals(oldEndpoint, coordinator.activeEndpoint)
        assertEquals(oldEndpoint, store.saved)
    }

    @Test
    fun confirmSwitchSavesOnlyAfterCompleteSwitch() {
        val oldEndpoint = ServerEndpoint("https://old.local:7154")
        val newEndpoint = ServerEndpoint("https://new.local:7154")
        val store = FakeStore(oldEndpoint)
        val coordinator = ServerSelectionCoordinator(store)
        coordinator.start()

        coordinator.confirmSwitch(newEndpoint)
        assertEquals(oldEndpoint, coordinator.activeEndpoint)
        assertEquals(newEndpoint, coordinator.pendingEndpoint)
        assertEquals(oldEndpoint, store.saved)

        coordinator.completeSwitch()

        assertEquals(newEndpoint, coordinator.currentEndpoint)
        assertEquals(null, coordinator.pendingEndpoint)
        assertEquals(newEndpoint, store.saved)
        assertEquals(ServerSelectionState.WebViewActive, coordinator.state)
    }

    @Test
    fun staleCallbacksAreRejectedAfterSwitchGenerationChanges() {
        val coordinator = ServerSelectionCoordinator(FakeStore(ServerEndpoint("https://old.local:7154")))
        coordinator.start()
        val staleGeneration = coordinator.beginValidation()

        coordinator.confirmSwitch(ServerEndpoint("https://new.local:7154"))

        assertFalse(coordinator.callbacksAccepted(staleGeneration))
    }

    @Test
    fun cancelAfterPendingSwitchKeepsCurrentEndpointAvailable() {
        val oldEndpoint = ServerEndpoint("https://old.local:7154")
        val newEndpoint = ServerEndpoint("https://new.local:7154")
        val store = FakeStore(oldEndpoint)
        val coordinator = ServerSelectionCoordinator(store)
        coordinator.start()

        coordinator.beginServerChange()
        coordinator.confirmSwitch(newEndpoint)
        coordinator.cancelPending()

        assertEquals(oldEndpoint, coordinator.currentEndpoint)
        assertEquals(null, coordinator.pendingEndpoint)
        assertEquals(oldEndpoint, store.saved)
        assertEquals(ServerSelectionState.WebViewActive, coordinator.state)
    }

    @Test
    fun failureAfterSwitchPreparationKeepsOldEndpoint() {
        val oldEndpoint = ServerEndpoint("https://old.local:7154")
        val newEndpoint = ServerEndpoint("https://new.local:7154")
        val store = FakeStore(oldEndpoint)
        val coordinator = ServerSelectionCoordinator(store)
        coordinator.start()

        coordinator.confirmSwitch(newEndpoint)
        coordinator.failSwitch()

        assertEquals(oldEndpoint, coordinator.currentEndpoint)
        assertEquals(null, coordinator.pendingEndpoint)
        assertEquals(oldEndpoint, store.saved)
    }

    private class FakeStore(initial: ServerEndpoint?) : EndpointStore {
        var saved = initial
        override fun load(): ServerEndpoint? = saved
        override fun save(endpoint: ServerEndpoint) {
            saved = endpoint
        }
        override fun clear() {
            saved = null
        }
    }
}
