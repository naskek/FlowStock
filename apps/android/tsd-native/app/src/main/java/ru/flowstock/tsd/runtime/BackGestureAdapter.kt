package ru.flowstock.tsd.runtime

class BackGestureAdapter {
    private var tracking = false
    private var longPressConsumed = false

    fun onInitialBackDown(): Boolean {
        tracking = true
        longPressConsumed = false
        return true
    }

    fun onLongPress(): Boolean {
        if (!tracking) {
            return false
        }
        longPressConsumed = true
        return true
    }

    fun shouldRunShortBackOnKeyUp(isCanceled: Boolean): Boolean {
        val runShort = tracking && !longPressConsumed && !isCanceled
        tracking = false
        longPressConsumed = false
        return runShort
    }
}
