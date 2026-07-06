package ru.flowstock.tsd

import android.app.Activity
import android.app.AlertDialog
import android.os.Bundle
import android.util.Log
import android.view.KeyEvent
import android.view.View
import android.webkit.WebView
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.ListView
import android.widget.ScrollView
import android.widget.TextView
import ru.flowstock.tsd.config.ServerEndpoint
import ru.flowstock.tsd.config.SharedPreferencesEndpointStore
import ru.flowstock.tsd.diagnostics.MaskedScanDiagnostic
import ru.flowstock.tsd.discovery.ActiveLanNetworkProvider
import ru.flowstock.tsd.discovery.EndpointValidationResult
import ru.flowstock.tsd.discovery.EndpointValidator
import ru.flowstock.tsd.discovery.ServerDiscoveryResult
import ru.flowstock.tsd.discovery.UdpServerDiscoveryClient
import ru.flowstock.tsd.runtime.ActivityOperationGate
import ru.flowstock.tsd.runtime.BackGestureAdapter
import ru.flowstock.tsd.runtime.ServerSelectionCoordinator
import ru.flowstock.tsd.runtime.WebViewSessionSwitchKind
import ru.flowstock.tsd.runtime.WebViewSessionSwitchPlanner
import ru.flowstock.tsd.scanner.VendorBroadcastScannerAdapter
import ru.flowstock.tsd.web.FlowStockWebViewSessionManager
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

class MainActivity : Activity() {
    private lateinit var scannerAdapter: VendorBroadcastScannerAdapter
    private lateinit var sessionManager: FlowStockWebViewSessionManager
    private lateinit var webView: WebView
    private lateinit var setupContainer: ScrollView
    private lateinit var endpointInput: EditText
    private lateinit var setupStatus: TextView
    private lateinit var validateButton: Button
    private lateinit var discoverButton: Button
    private lateinit var cancelDiscoveryButton: Button
    private lateinit var returnToCurrentServerButton: Button
    private lateinit var discoveryResultsList: ListView
    private lateinit var coordinator: ServerSelectionCoordinator
    private val endpointValidator = EndpointValidator()
    private val backgroundExecutor = Executors.newSingleThreadExecutor()
    private val backGestureAdapter = BackGestureAdapter()
    private val operationGate = ActivityOperationGate()
    private var discoveryCancellation: AtomicBoolean? = null
    private var validatedResults = emptyList<ServerEndpoint>()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        webView = findViewById(R.id.tsdWebView)
        setupContainer = findViewById(R.id.serverSetupContainer)
        endpointInput = findViewById(R.id.serverEndpointInput)
        setupStatus = findViewById(R.id.serverSetupStatus)
        validateButton = findViewById(R.id.validateServerButton)
        discoverButton = findViewById(R.id.discoverServersButton)
        cancelDiscoveryButton = findViewById(R.id.cancelDiscoveryButton)
        returnToCurrentServerButton = findViewById(R.id.returnToCurrentServerButton)
        discoveryResultsList = findViewById(R.id.discoveryResultsList)
        coordinator = ServerSelectionCoordinator(SharedPreferencesEndpointStore(this))

        scannerAdapter = VendorBroadcastScannerAdapter(
            context = this,
            onScan = { payload ->
                if (isSetupVisible()) {
                    false
                } else {
                    sessionManager.currentController()?.dispatchScan(payload) ?: false
                }
            },
            onDiagnostic = { diagnostic ->
                setStatus(formatScannerDiagnosticStatus(diagnostic))
            },
        )
        sessionManager = FlowStockWebViewSessionManager(
            webView = webView,
            scannerAdapter = scannerAdapter,
            onStatus = ::setStatus,
        )

        validateButton.setOnClickListener {
            validateAndSwitch(endpointInput.text?.toString().orEmpty())
        }
        discoverButton.setOnClickListener { discoverServers() }
        cancelDiscoveryButton.setOnClickListener {
            discoveryCancellation?.set(true)
            operationGate.cancelCurrent()
            setSetupStatus(getString(R.string.server_setup_discovery_stopped))
            showDiscoveryProgress(false)
        }
        returnToCurrentServerButton.setOnClickListener { returnToCurrentServerOrBack() }
        discoveryResultsList.setOnItemClickListener { _, _, position, _ ->
            validatedResults.getOrNull(position)?.let { applyValidatedEndpoint(it) }
        }

        val savedEndpoint = coordinator.start()
        if (savedEndpoint == null) {
            showSetup(getString(R.string.server_setup_first_launch))
        } else {
            endpointInput.setText(savedEndpoint.rootUrl)
            validateSavedEndpoint(savedEndpoint)
        }
    }

    override fun onStart() {
        super.onStart()
        sessionManager.onActivityStarted()
    }

    override fun onStop() {
        sessionManager.onActivityStopped()
        super.onStop()
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent?): Boolean {
        if (keyCode == KeyEvent.KEYCODE_BACK && event?.repeatCount == 0) {
            event.startTracking()
            return backGestureAdapter.onInitialBackDown()
        }
        return super.onKeyDown(keyCode, event)
    }

    override fun onKeyLongPress(keyCode: Int, event: KeyEvent?): Boolean {
        if (keyCode == KeyEvent.KEYCODE_BACK && backGestureAdapter.onLongPress()) {
            showServerChangeConfirmation()
            return true
        }
        return super.onKeyLongPress(keyCode, event)
    }

    override fun onKeyUp(keyCode: Int, event: KeyEvent?): Boolean {
        if (keyCode == KeyEvent.KEYCODE_BACK) {
            if (backGestureAdapter.shouldRunShortBackOnKeyUp(event?.isCanceled == true)) {
                handleShortBack()
            }
            return true
        }
        return super.onKeyUp(keyCode, event)
    }

    @Deprecated("Use KEYCODE_BACK handling while min platform requires legacy behavior.")
    override fun onBackPressed() {
        handleShortBack()
    }

    override fun onDestroy() {
        discoveryCancellation?.set(true)
        operationGate.destroy()
        sessionManager.disposeCurrent()
        backgroundExecutor.shutdownNow()
        super.onDestroy()
    }

    private fun setStatus(message: String) {
        if (BuildConfig.DEBUG) {
            Log.i(NATIVE_STATUS_LOG_TAG, message)
        }
    }

    private fun validateSavedEndpoint(endpoint: ServerEndpoint) {
        val operation = operationGate.begin()
        showSetup(getString(R.string.server_setup_checking_saved))
        backgroundExecutor.execute {
            val result = endpointValidator.validate(endpoint.rootUrl)
            postIfCurrent(operation) {
                when (result) {
                    is EndpointValidationResult.Success -> applyValidatedEndpoint(result.endpoint)
                    is EndpointValidationResult.Failure -> {
                        coordinator.failSwitch()
                    endpointInput.setText(endpoint.rootUrl)
                        showSetup(getString(R.string.server_setup_unavailable, result.reason))
                    }
                }
            }
        }
    }

    private fun validateAndSwitch(input: String) {
        coordinator.beginValidation()
        val operation = operationGate.begin()
        setSetupStatus(getString(R.string.server_setup_validating))
        setActionButtonsEnabled(false)
        backgroundExecutor.execute {
            val result = endpointValidator.validate(input)
            postIfCurrent(operation) {
                setActionButtonsEnabled(true)
                when (result) {
                    is EndpointValidationResult.Success -> applyValidatedEndpoint(result.endpoint)
                    is EndpointValidationResult.Failure -> {
                        setSetupStatus(getString(R.string.server_setup_validation_failed, result.reason))
                    }
                }
            }
        }
    }

    private fun discoverServers() {
        val cancellation = AtomicBoolean(false)
        discoveryCancellation = cancellation
        val operation = operationGate.begin()
        showDiscoveryProgress(true)
        setSetupStatus(getString(R.string.server_setup_searching))
        backgroundExecutor.execute {
            val client = UdpServerDiscoveryClient(
                networkProvider = ActiveLanNetworkProvider(this),
                validator = endpointValidator,
            )
            val results = client.discover(cancellation)
            postIfCurrent(operation) {
                if (discoveryCancellation !== cancellation || cancellation.get()) {
                    return@postIfCurrent
                }
                showDiscoveryProgress(false)
                val endpoints = results.filterIsInstance<ServerDiscoveryResult.Validated>().map { it.endpoint }
                validatedResults = endpoints
                discoveryResultsList.adapter = ArrayAdapter(
                    this,
                    android.R.layout.simple_list_item_1,
                    endpoints.map { endpoint ->
                        val name = endpoint.instanceName.ifBlank { "FlowStock" }
                        "$name\n${endpoint.rootUrl}\n${endpoint.applicationVersion}"
                    },
                )
                setSetupStatus(
                    if (endpoints.isEmpty()) {
                        getString(R.string.server_setup_not_found)
                    } else {
                        getString(R.string.server_setup_select_validated)
                    },
                )
            }
        }
    }

    private fun applyValidatedEndpoint(endpoint: ServerEndpoint) {
        val plan = WebViewSessionSwitchPlanner.plan(
            currentEndpoint = coordinator.currentEndpoint,
            targetEndpoint = endpoint,
            hasController = sessionManager.currentController() != null,
            setupVisible = isSetupVisible(),
        )

        when (plan.kind) {
            WebViewSessionSwitchKind.InitialLoad,
            WebViewSessionSwitchKind.SameOriginReload,
            -> {
                coordinator.confirmSwitch(endpoint)
                coordinator.completeSwitch()
                hideSetup()
                sessionManager.loadInitial(endpoint)
            }
            WebViewSessionSwitchKind.RestoreCurrent -> {
                coordinator.cancelPending()
                hideSetup()
                sessionManager.restoreCurrentSession()
            }
            WebViewSessionSwitchKind.OriginChange -> {
                val oldEndpoint = coordinator.currentEndpoint
                coordinator.confirmSwitch(endpoint)
                sessionManager.switchOrigin(
                    oldEndpoint = oldEndpoint,
                    newEndpoint = endpoint,
                    onComplete = {
                        coordinator.completeSwitch()
                        hideSetup()
                    },
                    onFailure = { restoredCurrentSession ->
                        coordinator.failSwitch()
                        if (restoredCurrentSession) {
                            hideSetup()
                        } else {
                            endpointInput.setText(switchFailureEndpointText(coordinator.currentEndpoint))
                            showSetup(getString(R.string.server_setup_switch_failed))
                            validateButton.setText(R.string.server_retry_current)
                        }
                    },
                )
            }
        }
    }

    private fun showSetup(message: String) {
        sessionManager.pauseScannerForSetup()
        validateButton.setText(R.string.server_validate_save)
        webView.visibility = View.GONE
        setupContainer.visibility = View.VISIBLE
        returnToCurrentServerButton.visibility =
            if (coordinator.currentEndpoint != null && sessionManager.currentController() != null) {
                View.VISIBLE
            } else {
                View.GONE
            }
        setSetupStatus(message)
    }

    private fun hideSetup() {
        setupContainer.visibility = View.GONE
        webView.visibility = View.VISIBLE
        sessionManager.restoreCurrentSession()
    }

    private fun setSetupStatus(message: String) {
        setupStatus.text = message
        setStatus("server setup: len=${message.length}")
    }

    private fun showDiscoveryProgress(active: Boolean) {
        discoverButton.isEnabled = !active
        validateButton.isEnabled = !active
        cancelDiscoveryButton.visibility = if (active) View.VISIBLE else View.GONE
        returnToCurrentServerButton.isEnabled = !active
    }

    private fun setActionButtonsEnabled(enabled: Boolean) {
        validateButton.isEnabled = enabled
        discoverButton.isEnabled = enabled
        returnToCurrentServerButton.isEnabled = enabled
    }

    private fun handleShortBack() {
        val controller = sessionManager.currentController()
        if (isSetupVisible()) {
            returnToCurrentServerOrBack()
            return
        }
        if (controller == null) {
            moveTaskToBack(true)
            return
        }
        controller.handleBack {
            moveTaskToBack(true)
        }
    }

    private fun showServerChangeConfirmation() {
        sessionManager.showServerChangeModal()
        AlertDialog.Builder(this)
            .setTitle(getString(R.string.server_change_title))
            .setMessage(getString(R.string.server_change_message))
            .setPositiveButton(getString(R.string.server_change_positive)) { _, _ ->
                sessionManager.transitionServerChangeModalToSetup()
                coordinator.beginServerChange()
                showSetup(getString(R.string.server_change_prompt))
            }
            .setNegativeButton(getString(R.string.server_change_negative)) { _, _ ->
                sessionManager.dismissServerChangeModal()
            }
            .setOnCancelListener {
                sessionManager.dismissServerChangeModal()
            }
            .show()
    }

    private fun returnToCurrentServerOrBack() {
        discoveryCancellation?.set(true)
        operationGate.cancelCurrent()
        coordinator.cancelPending()
        val canReturn = coordinator.currentEndpoint != null && sessionManager.currentController() != null
        if (!canReturn) {
            moveTaskToBack(true)
            return
        }
        hideSetup()
        sessionManager.restoreCurrentSession()
    }

    private fun isSetupVisible(): Boolean = setupContainer.visibility == View.VISIBLE

    private fun postIfCurrent(operation: Long, block: () -> Unit) {
        if (!operationGate.canApply(operation)) {
            return
        }
        runOnUiThread {
            if (operationGate.canApply(operation)) {
                block()
            }
        }
    }

    companion object {
        private const val NATIVE_STATUS_LOG_TAG = "FlowStockTsdNative"

        internal fun formatScannerDiagnosticStatus(diagnostic: MaskedScanDiagnostic): String =
            "scan ${diagnostic.state}: len=${diagnostic.length} hash=${diagnostic.hash} " +
                "masked=${diagnostic.maskedValue} sym=${diagnostic.symbology.orEmpty()}"

        internal fun switchFailureEndpointText(currentEndpoint: ServerEndpoint?): String =
            currentEndpoint?.rootUrl.orEmpty()
    }
}
