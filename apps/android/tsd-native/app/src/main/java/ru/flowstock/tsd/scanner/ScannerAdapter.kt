package ru.flowstock.tsd.scanner

import ru.flowstock.tsd.diagnostics.MaskedScanDiagnostic

interface ScannerAdapter {
    fun start()
    fun stop()
}

typealias ScanCallback = (ScanPayload) -> Unit
typealias ScannerDiagnosticCallback = (MaskedScanDiagnostic) -> Unit
