package ru.flowstock.tsd.scanner

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ScanPayloadNormalizerTest {
    @Test
    fun extractsAtolIntentExtrasBeforeNormalizing() {
        val extras = FakeScanBroadcastExtras(
            ScanPayloadNormalizer.EXTRA_BARCODE to "4601234567890",
            ScanPayloadNormalizer.EXTRA_SYMBOLOGY to "EAN13",
        )

        val extracted = ScanBroadcastExtractor.extract(
            action = ScanPayloadNormalizer.ATOL_ACTION,
            extras = extras,
        )

        assertTrue(extracted is ScanBroadcastExtraction.Accepted)
        val accepted = extracted as ScanBroadcastExtraction.Accepted
        assertEquals("atol", accepted.contract.vendor)
        assertEquals("4601234567890", accepted.value)
        assertEquals("EAN13", accepted.symbology)
        assertEquals(false, accepted.rawExtraPresent)
    }

    @Test
    fun extractsUrovoBarcodeStringAndDoesNotReadRawBarcodeExtra() {
        val extras = FakeScanBroadcastExtras(
            ScanPayloadNormalizer.UROVO_BARCODE_EXTRA to "UROVO-1",
            ScanPayloadNormalizer.UROVO_RAW_EXTRA to ByteArray(3),
        )

        val extracted = ScanBroadcastExtractor.extract(
            action = ScanPayloadNormalizer.UROVO_ACTION,
            extras = extras,
        )

        assertTrue(extracted is ScanBroadcastExtraction.Accepted)
        val accepted = extracted as ScanBroadcastExtraction.Accepted
        assertEquals("urovo", accepted.contract.vendor)
        assertEquals("UROVO-1", accepted.value)
        assertNull(accepted.symbology)
        assertEquals(true, accepted.rawExtraPresent)
        assertEquals(listOf(ScanPayloadNormalizer.UROVO_BARCODE_EXTRA), extras.stringReads)
        assertEquals(listOf(ScanPayloadNormalizer.UROVO_RAW_EXTRA), extras.hasExtraReads)
    }

    @Test
    fun rejectsUrovoRawExtraWithoutBarcodeString() {
        val result = ScanPayloadNormalizer.normalize(
            action = ScanPayloadNormalizer.UROVO_ACTION,
            extras = FakeScanBroadcastExtras(ScanPayloadNormalizer.UROVO_RAW_EXTRA to ByteArray(3)),
            timestamp = 1000L,
        )

        assertTrue(result is ScanNormalizeResult.Rejected)
        assertEquals("empty-data", (result as ScanNormalizeResult.Rejected).reason)
    }

    @Test
    fun rejectsCrossVendorExtras() {
        val urovoWithAtolExtra = ScanPayloadNormalizer.normalize(
            action = ScanPayloadNormalizer.UROVO_ACTION,
            extras = FakeScanBroadcastExtras(ScanPayloadNormalizer.EXTRA_BARCODE to "ATOL-ONLY"),
            timestamp = 1000L,
        )
        val atolWithUrovoExtra = ScanPayloadNormalizer.normalize(
            action = ScanPayloadNormalizer.ATOL_ACTION,
            extras = FakeScanBroadcastExtras(ScanPayloadNormalizer.UROVO_BARCODE_EXTRA to "UROVO-ONLY"),
            timestamp = 1000L,
        )

        assertTrue(urovoWithAtolExtra is ScanNormalizeResult.Rejected)
        assertEquals("empty-data", (urovoWithAtolExtra as ScanNormalizeResult.Rejected).reason)
        assertTrue(atolWithUrovoExtra is ScanNormalizeResult.Rejected)
        assertEquals("empty-data", (atolWithUrovoExtra as ScanNormalizeResult.Rejected).reason)
    }

    @Test
    fun unknownActionRejectsBeforeReadingExtras() {
        val extras = FakeScanBroadcastExtras(ScanPayloadNormalizer.EXTRA_BARCODE to "SHOULD-NOT-READ")

        val result = ScanPayloadNormalizer.normalize(
            action = "other",
            extras = extras,
            timestamp = 1000L,
        )

        assertTrue(result is ScanNormalizeResult.Rejected)
        assertEquals("wrong-action", (result as ScanNormalizeResult.Rejected).reason)
        assertTrue(extras.stringReads.isEmpty())
        assertTrue(extras.hasExtraReads.isEmpty())
    }

    @Test
    fun normalizesValidAtolBroadcast() {
        val result = ScanPayloadNormalizer.normalize(
            action = ScanPayloadNormalizer.ATOL_ACTION,
            extras = FakeScanBroadcastExtras(
                ScanPayloadNormalizer.EXTRA_BARCODE to "4601234567890",
                ScanPayloadNormalizer.EXTRA_SYMBOLOGY to "EAN13",
            ),
            timestamp = 1000L,
        )

        assertTrue(result is ScanNormalizeResult.Accepted)
        val accepted = result as ScanNormalizeResult.Accepted
        assertEquals("4601234567890", accepted.payload.value)
        assertEquals("EAN13", accepted.payload.symbology)
        assertEquals(1000L, accepted.payload.ts)
        assertEquals("atol-broadcast", accepted.payload.raw.getString("source"))
        assertEquals("atol", accepted.payload.raw.getString("vendor"))
        assertEquals(ScanPayloadNormalizer.EXTRA_BARCODE, accepted.payload.raw.getString("barcodeExtra"))
        assertEquals(ScanPayloadNormalizer.EXTRA_SYMBOLOGY, accepted.payload.raw.getString("symbologyExtra"))
        assertEquals(13, accepted.diagnostic.length)
        assertEquals("accepted", accepted.diagnostic.state)
    }

    @Test
    fun normalizesValidUrovoBroadcastWithNullSymbology() {
        val result = ScanPayloadNormalizer.normalize(
            action = ScanPayloadNormalizer.UROVO_ACTION,
            extras = FakeScanBroadcastExtras(
                ScanPayloadNormalizer.UROVO_BARCODE_EXTRA to "UROVO-1",
                ScanPayloadNormalizer.UROVO_RAW_EXTRA to ByteArray(3),
            ),
            timestamp = 1000L,
        )

        assertTrue(result is ScanNormalizeResult.Accepted)
        val accepted = result as ScanNormalizeResult.Accepted
        assertEquals("UROVO-1", accepted.payload.value)
        assertNull(accepted.payload.symbology)
        assertEquals("urovo-broadcast", accepted.payload.raw.getString("source"))
        assertEquals("urovo", accepted.payload.raw.getString("vendor"))
        assertEquals(ScanPayloadNormalizer.UROVO_BARCODE_EXTRA, accepted.payload.raw.getString("barcodeExtra"))
        assertTrue(accepted.payload.raw.isNull("symbologyExtra"))
        assertEquals(ScanPayloadNormalizer.UROVO_RAW_EXTRA, accepted.payload.raw.getString("rawExtra"))
        assertEquals(true, accepted.payload.raw.getBoolean("rawExtraPresent"))
        assertTrue(accepted.payload.raw.toString().contains("UROVO-1").not())
    }

    @Test
    fun rejectsMissingOrEmptyData() {
        listOf(null, "").forEach { value ->
            val result = ScanPayloadNormalizer.normalize(
                action = ScanPayloadNormalizer.ATOL_ACTION,
                extras = FakeScanBroadcastExtras(ScanPayloadNormalizer.EXTRA_BARCODE to value),
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
            extras = FakeScanBroadcastExtras(
                ScanPayloadNormalizer.EXTRA_BARCODE to value,
                ScanPayloadNormalizer.EXTRA_SYMBOLOGY to "DATAMATRIX",
            ),
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
            extras = FakeScanBroadcastExtras(
                ScanPayloadNormalizer.EXTRA_BARCODE to "ABC",
                ScanPayloadNormalizer.EXTRA_SYMBOLOGY to "  ",
            ),
            timestamp = 1000L,
        ) as ScanNormalizeResult.Accepted

        assertNull(result.payload.symbology)
    }

    private class FakeScanBroadcastExtras(vararg entries: Pair<String, Any?>) : ScanBroadcastExtras {
        private val values = entries.toMap()
        val stringReads = mutableListOf<String>()
        val hasExtraReads = mutableListOf<String>()

        override fun getStringExtra(name: String): String? {
            stringReads.add(name)
            return values[name] as? String
        }

        override fun hasExtra(name: String): Boolean {
            hasExtraReads.add(name)
            return values.containsKey(name)
        }
    }
}
