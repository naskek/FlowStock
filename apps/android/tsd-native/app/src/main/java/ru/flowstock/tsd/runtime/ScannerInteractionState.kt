package ru.flowstock.tsd.runtime

class ScannerInteractionState {
    var activityStarted: Boolean = false
        private set
    var hasController: Boolean = false
        private set
    var setupVisible: Boolean = false
        private set
    var modalVisible: Boolean = false
        private set
    var webInteractionActive: Boolean = false
        private set

    val scannerShouldRun: Boolean
        get() = activityStarted && hasController && webInteractionActive && !setupVisible && !modalVisible

    fun onActivityStarted() {
        activityStarted = true
    }

    fun onActivityStopped() {
        activityStarted = false
    }

    fun setControllerAvailable(available: Boolean) {
        hasController = available
        if (!available) {
            webInteractionActive = false
        }
    }

    fun setSetupVisible(visible: Boolean) {
        setupVisible = visible
    }

    fun setModalVisible(visible: Boolean) {
        modalVisible = visible
    }

    fun transitionModalToSetup() {
        setupVisible = true
        modalVisible = false
    }

    fun setWebInteractionActive(active: Boolean) {
        webInteractionActive = active
    }
}
