package ru.flowstock.tsd.scanner

import android.content.BroadcastReceiver
import android.content.Context
import android.content.IntentFilter
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import ru.flowstock.tsd.diagnostics.MaskedScanDiagnostic

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [31])
class VendorBroadcastScannerAdapterTest {
    @Test
    fun registerAndUnregisterAreIdempotent() {
        val registrar = FakeRegistrar()
        val adapter = VendorBroadcastScannerAdapter(
            registrar = registrar,
            onScan = {},
            onDiagnostic = {},
            clock = { 1000L },
        )

        adapter.start()
        adapter.start()
        assertTrue(adapter.isStartedForTest())
        assertEquals(1, registrar.registerCount)
        assertEquals(
            listOf(ScanPayloadNormalizer.ATOL_ACTION, ScanPayloadNormalizer.UROVO_ACTION),
            VendorBroadcastReceiverContract.intentActions(),
        )
        assertTrue(registrar.lastFilter != null)
        assertTrue(registrar.lastFilter?.hasAction(ScanPayloadNormalizer.ATOL_ACTION) == true)
        assertTrue(registrar.lastFilter?.hasAction(ScanPayloadNormalizer.UROVO_ACTION) == true)
        assertEquals(2, registrar.lastFilter?.countActions())

        adapter.stop()
        adapter.stop()
        assertFalse(adapter.isStartedForTest())
        assertEquals(1, registrar.unregisterCount)
    }

    @Test
    fun exactAtolActionDispatchesScan() {
        val scans = mutableListOf<ScanPayload>()
        val diagnostics = mutableListOf<MaskedScanDiagnostic>()
        val adapter = VendorBroadcastScannerAdapter(
            registrar = FakeRegistrar(),
            onScan = { scans.add(it) },
            onDiagnostic = { diagnostics.add(it) },
            clock = { 1000L },
        )

        adapter.handleBroadcast(
            action = ScanPayloadNormalizer.ATOL_ACTION,
            extras = FakeScanBroadcastExtras(
                ScanPayloadNormalizer.EXTRA_BARCODE to "QR-1",
                ScanPayloadNormalizer.EXTRA_SYMBOLOGY to "QR_CODE",
            ),
        )

        assertEquals(1, scans.size)
        assertEquals("QR-1", scans.single().value)
        assertEquals("QR_CODE", scans.single().symbology)
        assertEquals("accepted", diagnostics.single().state)
    }

    @Test
    fun exactUrovoActionDispatchesScanWithNullSymbology() {
        val scans = mutableListOf<ScanPayload>()
        val diagnostics = mutableListOf<MaskedScanDiagnostic>()
        val adapter = VendorBroadcastScannerAdapter(
            registrar = FakeRegistrar(),
            onScan = { scans.add(it) },
            onDiagnostic = { diagnostics.add(it) },
            clock = { 1000L },
        )

        adapter.handleBroadcast(
            action = ScanPayloadNormalizer.UROVO_ACTION,
            extras = FakeScanBroadcastExtras(ScanPayloadNormalizer.UROVO_BARCODE_EXTRA to "UROVO-1"),
        )

        assertEquals(1, scans.size)
        assertEquals("UROVO-1", scans.single().value)
        assertNull(scans.single().symbology)
        assertEquals("urovo-broadcast", scans.single().raw.getString("source"))
        assertEquals("accepted", diagnostics.single().state)
    }

    @Test
    fun wrongActionAndEmptyDataDoNotDispatch() {
        val scans = mutableListOf<ScanPayload>()
        val diagnostics = mutableListOf<MaskedScanDiagnostic>()
        val adapter = VendorBroadcastScannerAdapter(
            registrar = FakeRegistrar(),
            onScan = { scans.add(it) },
            onDiagnostic = { diagnostics.add(it) },
            clock = { 1000L },
        )

        adapter.handleBroadcast("wrong", FakeScanBroadcastExtras(ScanPayloadNormalizer.EXTRA_BARCODE to "ABC"))
        adapter.handleBroadcast(
            ScanPayloadNormalizer.ATOL_ACTION,
            FakeScanBroadcastExtras(
                ScanPayloadNormalizer.EXTRA_BARCODE to "",
                ScanPayloadNormalizer.EXTRA_SYMBOLOGY to "EAN13",
            ),
        )

        assertTrue(scans.isEmpty())
        assertEquals(listOf("wrong-action", "empty-data"), diagnostics.map { it.state })
    }

    @Test
    fun repeatedAndFastDifferentScansArePassedThrough() {
        val scans = mutableListOf<ScanPayload>()
        val adapter = VendorBroadcastScannerAdapter(
            registrar = FakeRegistrar(),
            onScan = { scans.add(it) },
            onDiagnostic = {},
            clock = { 1000L },
        )

        adapter.handleBroadcast(ScanPayloadNormalizer.ATOL_ACTION, FakeScanBroadcastExtras(ScanPayloadNormalizer.EXTRA_BARCODE to "A"))
        adapter.handleBroadcast(ScanPayloadNormalizer.ATOL_ACTION, FakeScanBroadcastExtras(ScanPayloadNormalizer.EXTRA_BARCODE to "A"))
        adapter.handleBroadcast(ScanPayloadNormalizer.ATOL_ACTION, FakeScanBroadcastExtras(ScanPayloadNormalizer.EXTRA_BARCODE to "B"))

        assertEquals(listOf("A", "A", "B"), scans.map { it.value })
    }

    @Test
    fun receiverFlagsAreExportedOnlyForApi33AndNewer() {
        assertNull(ReceiverRegistrationFlags.forSdk(24))
        assertEquals(Context.RECEIVER_EXPORTED, ReceiverRegistrationFlags.forSdk(33))
    }

    private class FakeScanBroadcastExtras(vararg entries: Pair<String, Any?>) : ScanBroadcastExtras {
        private val values = entries.toMap()

        override fun getStringExtra(name: String): String? = values[name] as? String

        override fun hasExtra(name: String): Boolean = values.containsKey(name)
    }

    private class FakeRegistrar : ReceiverRegistrar {
        var registerCount = 0
        var unregisterCount = 0
        var lastFilter: IntentFilter? = null

        override fun register(receiver: BroadcastReceiver, filter: IntentFilter) {
            registerCount += 1
            lastFilter = filter
        }

        override fun unregister(receiver: BroadcastReceiver) {
            unregisterCount += 1
        }
    }
}
