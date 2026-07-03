package ru.flowstock.tsd.runtime

import ru.flowstock.tsd.config.EndpointStore
import ru.flowstock.tsd.config.ServerEndpoint

enum class ServerSelectionState {
    SetupRequired,
    WebViewActive,
    SetupChangingServer,
    ValidatingNewEndpoint,
    SwitchingEndpoint,
    Error,
}

class ServerSelectionCoordinator(
    private val store: EndpointStore,
) {
    var state: ServerSelectionState = ServerSelectionState.SetupRequired
        private set
    var currentEndpoint: ServerEndpoint? = null
        private set
    var pendingEndpoint: ServerEndpoint? = null
        private set
    private var generation: Long = 0L
    val activeEndpoint: ServerEndpoint?
        get() = currentEndpoint

    fun start(): ServerEndpoint? {
        currentEndpoint = store.load()
        pendingEndpoint = null
        state = if (currentEndpoint == null) ServerSelectionState.SetupRequired else ServerSelectionState.WebViewActive
        generation += 1
        return currentEndpoint
    }

    fun beginServerChange(): Long {
        pendingEndpoint = null
        state = if (currentEndpoint == null) ServerSelectionState.SetupRequired else ServerSelectionState.SetupChangingServer
        generation += 1
        return generation
    }

    fun beginValidation(): Long {
        pendingEndpoint = null
        state = ServerSelectionState.ValidatingNewEndpoint
        generation += 1
        return generation
    }

    fun isCurrentGeneration(candidate: Long): Boolean = candidate == generation

    fun confirmSwitch(endpoint: ServerEndpoint) {
        pendingEndpoint = endpoint
        state = ServerSelectionState.SwitchingEndpoint
        generation += 1
    }

    fun completeSwitch() {
        val endpoint = pendingEndpoint ?: return
        store.save(endpoint)
        currentEndpoint = endpoint
        pendingEndpoint = null
        state = ServerSelectionState.WebViewActive
    }

    fun failSwitch() {
        pendingEndpoint = null
        state = if (currentEndpoint == null) ServerSelectionState.SetupRequired else ServerSelectionState.WebViewActive
    }

    fun cancelPending() {
        pendingEndpoint = null
        state = if (currentEndpoint == null) ServerSelectionState.SetupRequired else ServerSelectionState.WebViewActive
    }

    fun callbacksAccepted(callbackGeneration: Long): Boolean = callbackGeneration == generation
}
