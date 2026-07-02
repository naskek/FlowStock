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

object ScanPayloadNormalizer {
    const val ATOL_ACTION = "com.xcheng.scanner.action.BARCODE_DECODING_BROADCAST"
    const val EXTRA_BARCODE = "EXTRA_BARCODE_DECODING_DATA"
    const val EXTRA_SYMBOLOGY = "EXTRA_BARCODE_DECODING_SYMBOLE"

    fun normalize(
        action: String?,
        barcode: String?,
        symbology: String?,
        timestamp: Long,
    ): ScanNormalizeResult {
        if (action != ATOL_ACTION) {
            return ScanNormalizeResult.Rejected(
                reason = "wrong-action",
                diagnostic = MaskedScanDiagnostics.fromValue(
                    barcode,
                    symbology,
                    state = "wrong-action",
                    timestamp = timestamp,
                ),
            )
        }

        val value = barcode.orEmpty()
        if (value.isEmpty()) {
            return ScanNormalizeResult.Rejected(
                reason = "empty-data",
                diagnostic = MaskedScanDiagnostics.fromValue(
                    barcode,
                    symbology,
                    state = "empty-data",
                    timestamp = timestamp,
                ),
            )
        }

        val cleanSymbology = symbology?.trim()?.takeIf { it.isNotEmpty() }
        val raw = JSONObject()
            .put("source", "atol-broadcast")
            .put("action", ATOL_ACTION)
            .put("barcodeExtra", EXTRA_BARCODE)
            .put("symbologyExtra", EXTRA_SYMBOLOGY)
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
