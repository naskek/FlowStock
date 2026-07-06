package ru.flowstock.tsd.scanner

import org.json.JSONObject
import ru.flowstock.tsd.diagnostics.MaskedScanDiagnostic
import ru.flowstock.tsd.diagnostics.MaskedScanDiagnostics

data class ScanPayload(
    val value: String,
    val symbology: String?,
    val raw: JSONObject,
    val ts: Long,
) {
    fun toJsonObject(): JSONObject =
        JSONObject()
            .put("value", value)
            .put("symbology", symbology ?: JSONObject.NULL)
            .put("raw", raw)
            .put("ts", ts)
}

sealed class ScanNormalizeResult {
    data class Accepted(
        val payload: ScanPayload,
        val diagnostic: MaskedScanDiagnostic,
    ) : ScanNormalizeResult()

    data class Rejected(
        val reason: String,
        val diagnostic: MaskedScanDiagnostic,
    ) : ScanNormalizeResult()
}

data class ScanBroadcastContract(
    val vendor: String,
    val source: String,
    val action: String,
    val barcodeExtra: String,
    val symbologyExtra: String?,
    val rawExtra: String?,
)

interface ScanBroadcastExtras {
    fun getStringExtra(name: String): String?
    fun hasExtra(name: String): Boolean
}

sealed class ScanBroadcastExtraction {
    data class Accepted(
        val contract: ScanBroadcastContract,
        val value: String,
        val symbology: String?,
        val rawExtraPresent: Boolean,
    ) : ScanBroadcastExtraction()

    data class Rejected(
        val reason: String,
        val diagnosticValue: String?,
        val diagnosticSymbology: String?,
    ) : ScanBroadcastExtraction()
}

object ScanBroadcastContracts {
    const val ATOL_ACTION = "com.xcheng.scanner.action.BARCODE_DECODING_BROADCAST"
    const val ATOL_BARCODE_EXTRA = "EXTRA_BARCODE_DECODING_DATA"
    const val ATOL_SYMBOLOGY_EXTRA = "EXTRA_BARCODE_DECODING_SYMBOLE"
    const val UROVO_ACTION = "android.intent.ACTION_DECODE_DATA"
    const val UROVO_BARCODE_EXTRA = "barcode_string"
    const val UROVO_RAW_EXTRA = "barcode"

    val ATOL = ScanBroadcastContract(
        vendor = "atol",
        source = "atol-broadcast",
        action = ATOL_ACTION,
        barcodeExtra = ATOL_BARCODE_EXTRA,
        symbologyExtra = ATOL_SYMBOLOGY_EXTRA,
        rawExtra = null,
    )

    val UROVO = ScanBroadcastContract(
        vendor = "urovo",
        source = "urovo-broadcast",
        action = UROVO_ACTION,
        barcodeExtra = UROVO_BARCODE_EXTRA,
        symbologyExtra = null,
        rawExtra = UROVO_RAW_EXTRA,
    )

    fun all(): List<ScanBroadcastContract> = listOf(ATOL, UROVO)

    fun forAction(action: String?): ScanBroadcastContract? =
        all().firstOrNull { it.action == action }
}

object ScanBroadcastExtractor {
    fun extract(action: String?, extras: ScanBroadcastExtras): ScanBroadcastExtraction {
        val contract = ScanBroadcastContracts.forAction(action)
            ?: return ScanBroadcastExtraction.Rejected(
                reason = "wrong-action",
                diagnosticValue = null,
                diagnosticSymbology = null,
            )

        val value = extras.getStringExtra(contract.barcodeExtra).orEmpty()
        val symbology = contract.symbologyExtra?.let { extras.getStringExtra(it) }
        val rawExtraPresent = contract.rawExtra?.let { extras.hasExtra(it) } ?: false
        if (value.isEmpty()) {
            return ScanBroadcastExtraction.Rejected(
                reason = "empty-data",
                diagnosticValue = value,
                diagnosticSymbology = symbology,
            )
        }

        return ScanBroadcastExtraction.Accepted(
            contract = contract,
            value = value,
            symbology = symbology,
            rawExtraPresent = rawExtraPresent,
        )
    }
}

object ScanPayloadNormalizer {
    const val ATOL_ACTION = ScanBroadcastContracts.ATOL_ACTION
    const val EXTRA_BARCODE = ScanBroadcastContracts.ATOL_BARCODE_EXTRA
    const val EXTRA_SYMBOLOGY = ScanBroadcastContracts.ATOL_SYMBOLOGY_EXTRA
    const val UROVO_ACTION = ScanBroadcastContracts.UROVO_ACTION
    const val UROVO_BARCODE_EXTRA = ScanBroadcastContracts.UROVO_BARCODE_EXTRA
    const val UROVO_RAW_EXTRA = ScanBroadcastContracts.UROVO_RAW_EXTRA

    fun normalize(
        action: String?,
        extras: ScanBroadcastExtras,
        timestamp: Long,
    ): ScanNormalizeResult {
        val extracted = ScanBroadcastExtractor.extract(action, extras)
        if (extracted is ScanBroadcastExtraction.Rejected) {
            return ScanNormalizeResult.Rejected(
                reason = extracted.reason,
                diagnostic = MaskedScanDiagnostics.fromValue(
                    extracted.diagnosticValue,
                    extracted.diagnosticSymbology,
                    state = extracted.reason,
                    timestamp = timestamp,
                ),
            )
        }

        extracted as ScanBroadcastExtraction.Accepted
        val contract = extracted.contract
        val value = extracted.value
        val symbology = extracted.symbology
        val cleanSymbology = symbology?.trim()?.takeIf { it.isNotEmpty() }
        val raw = JSONObject()
            .put("source", contract.source)
            .put("vendor", contract.vendor)
            .put("action", contract.action)
            .put("barcodeExtra", contract.barcodeExtra)
            .put("symbologyExtra", contract.symbologyExtra ?: JSONObject.NULL)
            .put("rawExtra", contract.rawExtra ?: JSONObject.NULL)
            .put("rawExtraPresent", extracted.rawExtraPresent)
            .put("symbology", cleanSymbology ?: JSONObject.NULL)

        return ScanNormalizeResult.Accepted(
            payload = ScanPayload(
                value = value,
                symbology = cleanSymbology,
                raw = raw,
                ts = timestamp,
            ),
            diagnostic = MaskedScanDiagnostics.fromValue(
                value,
                cleanSymbology,
                state = "accepted",
                timestamp = timestamp,
            ),
        )
    }
}
