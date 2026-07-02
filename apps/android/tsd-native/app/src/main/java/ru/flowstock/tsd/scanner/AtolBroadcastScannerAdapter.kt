package ru.flowstock.tsd.scanner

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Build

class AtolBroadcastScannerAdapter(
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
        registrar.register(receiver, IntentFilter(AtolBroadcastReceiverContract.intentAction()))
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

    fun handleRawBroadcast(
        action: String?,
        barcode: String?,
        symbology: String?,
        timestamp: Long = clock(),
    ): ScanNormalizeResult {
        val result = ScanPayloadNormalizer.normalize(action, barcode, symbology, timestamp)
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
        handleRawBroadcast(
            action = intent?.action,
            barcode = intent?.getStringExtra(ScanPayloadNormalizer.EXTRA_BARCODE),
            symbology = intent?.getStringExtra(ScanPayloadNormalizer.EXTRA_SYMBOLOGY),
            timestamp = clock(),
        )
    }
}

interface ReceiverRegistrar {
    fun register(receiver: BroadcastReceiver, filter: IntentFilter)
    fun unregister(receiver: BroadcastReceiver)
}

object AtolBroadcastReceiverContract {
    fun intentAction(): String = ScanPayloadNormalizer.ATOL_ACTION
}

object ReceiverRegistrationFlags {
    fun forSdk(sdkInt: Int): Int? =
        if (sdkInt >= 33) Context.RECEIVER_EXPORTED else null
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
