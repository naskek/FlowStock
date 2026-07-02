package ru.flowstock.tsd.scanner

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ScanPayloadNormalizerTest {
    @Test
    fun normalizesValidAtolBroadcast() {
        val result = ScanPayloadNormalizer.normalize(
            action = ScanPayloadNormalizer.ATOL_ACTION,
            barcode = "4601234567890",
            symbology = "EAN13",
            timestamp = 1000L,
        )

        assertTrue(result is ScanNormalizeResult.Accepted)
        val accepted = result as ScanNormalizeResult.Accepted
        assertEquals("4601234567890", accepted.payload.value)
        assertEquals("EAN13", accepted.payload.symbology)
        assertEquals(1000L, accepted.payload.ts)
        assertEquals("atol-broadcast", accepted.payload.raw.getString("source"))
        assertEquals(13, accepted.diagnostic.length)
        assertEquals("accepted", accepted.diagnostic.state)
    }

    @Test
    fun rejectsWrongAction() {
        val result = ScanPayloadNormalizer.normalize(
            action = "other",
            barcode = "ABC",
            symbology = "QR",
            timestamp = 1000L,
        )

        assertTrue(result is ScanNormalizeResult.Rejected)
        val rejected = result as ScanNormalizeResult.Rejected
        assertEquals("wrong-action", rejected.reason)
        assertEquals("wrong-action", rejected.diagnostic.state)
    }

    @Test
    fun rejectsMissingOrEmptyData() {
        listOf(null, "").forEach { value ->
            val result = ScanPayloadNormalizer.normalize(
                action = ScanPayloadNormalizer.ATOL_ACTION,
                barcode = value,
                symbology = null,
                timestamp = 1000L,
            )

            assertTrue(result is ScanNormalizeResult.Rejected)
            assertEquals("empty-data", (result as ScanNormalizeResult.Rejected).reason)
        }
    }

    @Test
    fun preservesGsControlCharacterAndEscapableCharacters() {
        val value = "GS1\"A\\B\u001D99"
        val result = ScanPayloadNormalizer.normalize(
            action = ScanPayloadNormalizer.ATOL_ACTION,
            barcode = value,
            symbology = "DATAMATRIX",
            timestamp = 1000L,
        ) as ScanNormalizeResult.Accepted

        assertEquals(value, result.payload.value)
        assertEquals(value, result.payload.toJsonObject().getString("value"))
        assertEquals("DATAMATRIX", result.payload.toJsonObject().getString("symbology"))
    }

    @Test
    fun blankSymbologyBecomesNull() {
        val result = ScanPayloadNormalizer.normalize(
            action = ScanPayloadNormalizer.ATOL_ACTION,
            barcode = "ABC",
            symbology = "  ",
            timestamp = 1000L,
        ) as ScanNormalizeResult.Accepted

        assertNull(result.payload.symbology)
    }
}
