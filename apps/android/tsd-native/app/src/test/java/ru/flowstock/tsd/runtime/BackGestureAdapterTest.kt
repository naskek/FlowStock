package ru.flowstock.tsd.runtime

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class BackGestureAdapterTest {
    @Test
    fun shortBackRunsWhenNoLongPressOccurred() {
        val adapter = BackGestureAdapter()

        assertTrue(adapter.onInitialBackDown())

        assertTrue(adapter.shouldRunShortBackOnKeyUp(isCanceled = false))
    }

    @Test
    fun longBackConsumesGestureAndSuppressesShortBack() {
        val adapter = BackGestureAdapter()

        adapter.onInitialBackDown()
        assertTrue(adapter.onLongPress())

        assertFalse(adapter.shouldRunShortBackOnKeyUp(isCanceled = false))
    }

    @Test
    fun canceledBackDoesNotRunShortBack() {
        val adapter = BackGestureAdapter()

        adapter.onInitialBackDown()

        assertFalse(adapter.shouldRunShortBackOnKeyUp(isCanceled = true))
    }
}
