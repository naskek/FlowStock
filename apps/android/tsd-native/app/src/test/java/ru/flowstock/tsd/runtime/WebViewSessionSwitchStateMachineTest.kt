package ru.flowstock.tsd.runtime

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class WebViewSessionSwitchStateMachineTest {
    @Test
    fun successfulSwitchUsesNeutralDocumentThenCleanupThenNewSession() {
        val state = WebViewSessionSwitchStateMachine()

        assertEquals(
            listOf(
                WebViewSwitchCommand.StopScanner,
                WebViewSwitchCommand.DisposeOldController,
                WebViewSwitchCommand.StopLoading,
                WebViewSwitchCommand.LoadNeutralDocument,
            ),
            state.begin(),
        )
        assertEquals(WebViewSwitchState.WaitingForNeutralDocument, state.state)

        assertEquals(listOf(WebViewSwitchCommand.CleanupCookies), state.neutralLoaded())
        assertEquals(WebViewSwitchState.WaitingForCookieCleanup, state.state)

        assertEquals(
            listOf(
                WebViewSwitchCommand.CleanupWebStorage,
                WebViewSwitchCommand.ClearHistory,
                WebViewSwitchCommand.CreateNewController,
                WebViewSwitchCommand.LoadNewOrigin,
            ),
            state.cookieCleanupFinished(cookiesAbsent = true),
        )
        state.newSessionCreated()

        assertEquals(WebViewSwitchState.Complete, state.state)
    }

    @Test
    fun cookieTimeoutRestoresOldSessionAndDoesNotLoadNewOrigin() {
        val state = WebViewSessionSwitchStateMachine()
        state.begin()
        state.neutralLoaded()

        val commands = state.cookieCleanupTimedOut()

        assertEquals(WebViewSwitchState.RestoringOldSession, state.state)
        assertTrue(commands.contains(WebViewSwitchCommand.RestoreOldController))
        assertTrue(commands.contains(WebViewSwitchCommand.LoadOldOrigin))
        assertTrue(!commands.contains(WebViewSwitchCommand.LoadNewOrigin))
    }

    @Test
    fun cookiesRemainingRestoresOldSession() {
        val state = WebViewSessionSwitchStateMachine()
        state.begin()
        state.neutralLoaded()

        val commands = state.cookieCleanupFinished(cookiesAbsent = false)

        assertEquals(WebViewSwitchState.RestoringOldSession, state.state)
        assertTrue(commands.contains(WebViewSwitchCommand.RestoreOldController))
        assertTrue(!commands.contains(WebViewSwitchCommand.LoadNewOrigin))
    }

    @Test
    fun newControllerFailureRestoresOldSessionAndFallbackFailureShowsSetup() {
        val state = WebViewSessionSwitchStateMachine()
        state.begin()
        state.neutralLoaded()
        state.cookieCleanupFinished(cookiesAbsent = true)

        val restoreCommands = state.newSessionFailed()
        assertTrue(restoreCommands.contains(WebViewSwitchCommand.RestoreOldController))

        val failedCommands = state.oldSessionRestoreFailed()
        assertEquals(WebViewSwitchState.FailedSetup, state.state)
        assertEquals(listOf(WebViewSwitchCommand.ShowSetupError), failedCommands)
    }
}
