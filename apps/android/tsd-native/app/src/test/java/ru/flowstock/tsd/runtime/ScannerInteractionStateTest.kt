package ru.flowstock.tsd.runtime

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ScannerInteractionStateTest {
    @Test
    fun setupVisibleBlocksScannerEvenWhenActivityAndControllerAreActive() {
        val state = ScannerInteractionState()
        state.onActivityStarted()
        state.setControllerAvailable(true)
        state.setWebInteractionActive(true)

        state.setSetupVisible(true)

        assertFalse(state.scannerShouldRun)
    }

    @Test
    fun lockUnlockDuringSetupDoesNotStartScanner() {
        val state = ScannerInteractionState()
        state.setControllerAvailable(true)
        state.setWebInteractionActive(true)
        state.setSetupVisible(true)

        state.onActivityStarted()
        state.onActivityStopped()
        state.onActivityStarted()

        assertFalse(state.scannerShouldRun)
    }

    @Test
    fun modalStopsScannerAndDismissRestoresIt() {
        val state = ScannerInteractionState()
        state.onActivityStarted()
        state.setControllerAvailable(true)
        state.setWebInteractionActive(true)

        state.setModalVisible(true)
        assertFalse(state.scannerShouldRun)

        state.setModalVisible(false)
        assertTrue(state.scannerShouldRun)
    }

    @Test
    fun positiveServerChangeKeepsScannerStoppedBecauseSetupIsVisible() {
        val state = ScannerInteractionState()
        state.onActivityStarted()
        state.setControllerAvailable(true)
        state.setWebInteractionActive(true)

        state.setModalVisible(true)
        state.transitionModalToSetup()

        assertFalse(state.scannerShouldRun)
        assertTrue(state.setupVisible)
        assertFalse(state.modalVisible)
    }
}
