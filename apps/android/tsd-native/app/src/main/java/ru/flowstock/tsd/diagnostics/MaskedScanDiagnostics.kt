package ru.flowstock.tsd.diagnostics

import java.security.MessageDigest

data class MaskedScanDiagnostic(
    val length: Int,
    val hash: String,
    val maskedValue: String,
    val symbology: String?,
    val timestamp: Long,
    val state: String,
)

object MaskedScanDiagnostics {
    fun fromValue(
        value: String?,
        symbology: String? = null,
        state: String,
        timestamp: Long = System.currentTimeMillis(),
    ): MaskedScanDiagnostic {
        val text = value.orEmpty()
        return MaskedScanDiagnostic(
            length = text.length,
            hash = hash(text),
            maskedValue = mask(text),
            symbology = symbology?.takeIf { it.isNotBlank() },
            timestamp = timestamp,
            state = state,
        )
    }

    fun mask(value: String?): String {
        val text = value.orEmpty()
        if (text.isEmpty()) return ""
        if (text.length <= 6) return text.take(1) + "..."
        return text.take(3) + "..." + text.takeLast(4)
    }

    fun hash(value: String?): String {
        val bytes = MessageDigest.getInstance("SHA-256").digest(value.orEmpty().toByteArray(Charsets.UTF_8))
        return bytes.take(8).joinToString("") { "%02x".format(it) }
    }
}
