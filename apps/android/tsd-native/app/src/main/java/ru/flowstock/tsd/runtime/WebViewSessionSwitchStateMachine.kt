package ru.flowstock.tsd.runtime

sealed class WebViewSwitchCommand {
    object StopScanner : WebViewSwitchCommand()
    object DisposeOldController : WebViewSwitchCommand()
    object StopLoading : WebViewSwitchCommand()
    object LoadNeutralDocument : WebViewSwitchCommand()
    object CleanupCookies : WebViewSwitchCommand()
    object CleanupWebStorage : WebViewSwitchCommand()
    object ClearHistory : WebViewSwitchCommand()
    object CreateNewController : WebViewSwitchCommand()
    object LoadNewOrigin : WebViewSwitchCommand()
    object RestoreOldController : WebViewSwitchCommand()
    object LoadOldOrigin : WebViewSwitchCommand()
    object ShowSetupError : WebViewSwitchCommand()
}

enum class WebViewSwitchState {
    Idle,
    WaitingForNeutralDocument,
    WaitingForCookieCleanup,
    CreatingNewSession,
    Complete,
    RestoringOldSession,
    FailedSetup,
}

class WebViewSessionSwitchStateMachine {
    var state: WebViewSwitchState = WebViewSwitchState.Idle
        private set

    fun begin(): List<WebViewSwitchCommand> {
        state = WebViewSwitchState.WaitingForNeutralDocument
        return listOf(
            WebViewSwitchCommand.StopScanner,
            WebViewSwitchCommand.DisposeOldController,
            WebViewSwitchCommand.StopLoading,
            WebViewSwitchCommand.LoadNeutralDocument,
        )
    }

    fun neutralLoaded(): List<WebViewSwitchCommand> {
        state = WebViewSwitchState.WaitingForCookieCleanup
        return listOf(WebViewSwitchCommand.CleanupCookies)
    }

    fun cookieCleanupFinished(cookiesAbsent: Boolean): List<WebViewSwitchCommand> {
        if (!cookiesAbsent) {
            return restoreOld()
        }
        state = WebViewSwitchState.CreatingNewSession
        return listOf(
            WebViewSwitchCommand.CleanupWebStorage,
            WebViewSwitchCommand.ClearHistory,
            WebViewSwitchCommand.CreateNewController,
            WebViewSwitchCommand.LoadNewOrigin,
        )
    }

    fun cookieCleanupTimedOut(): List<WebViewSwitchCommand> = restoreOld()

    fun newSessionCreated(): List<WebViewSwitchCommand> {
        state = WebViewSwitchState.Complete
        return emptyList()
    }

    fun newSessionFailed(): List<WebViewSwitchCommand> = restoreOld()

    fun oldSessionRestored(): List<WebViewSwitchCommand> {
        state = WebViewSwitchState.Complete
        return emptyList()
    }

    fun oldSessionRestoreFailed(): List<WebViewSwitchCommand> {
        state = WebViewSwitchState.FailedSetup
        return listOf(WebViewSwitchCommand.ShowSetupError)
    }

    private fun restoreOld(): List<WebViewSwitchCommand> {
        state = WebViewSwitchState.RestoringOldSession
        return listOf(
            WebViewSwitchCommand.RestoreOldController,
            WebViewSwitchCommand.LoadOldOrigin,
        )
    }
}
