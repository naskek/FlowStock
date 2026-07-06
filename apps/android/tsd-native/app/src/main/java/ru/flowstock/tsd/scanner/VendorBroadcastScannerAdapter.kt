package ru.flowstock.tsd.scanner

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Build

class VendorBroadcastScannerAdapter(
    private val registrar: ReceiverRegistrar,
    private val onScan: ScanCallback,
    private val onDiagnostic: ScannerDiagnosticCallback,
    private val clock: () -> Long = { System.currentTimeMillis() },
) : ScannerAdapter {
    constructor(
        context: Context,
        onScan: ScanCallback,
        onDiagnostic: ScannerDiagnosticCallback,
        clock: () -> Long = { System.currentTimeMillis() },
    ) : this(ContextReceiverRegistrar(context), onScan, onDiagnostic, clock)

    private var receiverRegistered = false

    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            handleIntent(intent)
        }
    }

    override fun start() {
        if (receiverRegistered) {
            return
        }
        registrar.register(receiver, VendorBroadcastReceiverContract.intentFilter())
        receiverRegistered = true
    }

    override fun stop() {
        if (!receiverRegistered) {
            return
        }
        registrar.unregister(receiver)
        receiverRegistered = false
    }

    fun isStartedForTest(): Boolean = receiverRegistered

    fun handleBroadcast(
        action: String?,
        extras: ScanBroadcastExtras,
        timestamp: Long = clock(),
    ): ScanNormalizeResult {
        val result = ScanPayloadNormalizer.normalize(action, extras, timestamp)
        when (result) {
            is ScanNormalizeResult.Accepted -> {
                onDiagnostic(result.diagnostic)
                onScan(result.payload)
            }
            is ScanNormalizeResult.Rejected -> onDiagnostic(result.diagnostic)
        }
        return result
    }

    private fun handleIntent(intent: Intent?) {
        handleBroadcast(
            action = intent?.action,
            extras = intent?.let { IntentScanBroadcastExtras(it) } ?: EmptyScanBroadcastExtras,
            timestamp = clock(),
        )
    }
}

interface ReceiverRegistrar {
    fun register(receiver: BroadcastReceiver, filter: IntentFilter)
    fun unregister(receiver: BroadcastReceiver)
}

object VendorBroadcastReceiverContract {
    fun intentActions(): List<String> = ScanBroadcastContracts.all().map { it.action }

    fun intentFilter(): IntentFilter =
        IntentFilter().apply {
            intentActions().forEach { addAction(it) }
        }
}

object ReceiverRegistrationFlags {
    fun forSdk(sdkInt: Int): Int? =
        if (sdkInt >= 33) Context.RECEIVER_EXPORTED else null
}

private object EmptyScanBroadcastExtras : ScanBroadcastExtras {
    override fun getStringExtra(name: String): String? = null

    override fun hasExtra(name: String): Boolean = false
}

private class IntentScanBroadcastExtras(private val intent: Intent) : ScanBroadcastExtras {
    override fun getStringExtra(name: String): String? = intent.getStringExtra(name)

    override fun hasExtra(name: String): Boolean = intent.hasExtra(name)
}

private class ContextReceiverRegistrar(private val context: Context) : ReceiverRegistrar {
    override fun register(receiver: BroadcastReceiver, filter: IntentFilter) {
        val flags = ReceiverRegistrationFlags.forSdk(Build.VERSION.SDK_INT)
        if (flags != null) {
            context.registerReceiver(receiver, filter, flags)
        } else {
            @Suppress("DEPRECATION")
            context.registerReceiver(receiver, filter)
        }
    }

    override fun unregister(receiver: BroadcastReceiver) {
        context.unregisterReceiver(receiver)
    }
}
