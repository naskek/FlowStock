package ru.flowstock.tsd.scanner

import android.content.BroadcastReceiver
import android.content.Context
import android.content.IntentFilter
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import ru.flowstock.tsd.diagnostics.MaskedScanDiagnostic

class AtolBroadcastScannerAdapterTest {
    @Test
    fun registerAndUnregisterAreIdempotent() {
        val registrar = FakeRegistrar()
        val adapter = AtolBroadcastScannerAdapter(
            registrar = registrar,
            onScan = {},
            onDiagnostic = {},
            clock = { 1000L },
        )

        adapter.start()
        adapter.start()
        assertTrue(adapter.isStartedForTest())
        assertEquals(1, registrar.registerCount)
        assertEquals(ScanPayloadNormalizer.ATOL_ACTION, AtolBroadcastReceiverContract.intentAction())

        adapter.stop()
        adapter.stop()
        assertFalse(adapter.isStartedForTest())
        assertEquals(1, registrar.unregisterCount)
    }

    @Test
    fun exactAtolActionDispatchesScan() {
        val scans = mutableListOf<ScanPayload>()
        val diagnostics = mutableListOf<MaskedScanDiagnostic>()
        val adapter = AtolBroadcastScannerAdapter(
            registrar = FakeRegistrar(),
            onScan = { scans.add(it) },
            onDiagnostic = { diagnostics.add(it) },
            clock = { 1000L },
        )

        adapter.handleRawBroadcast(
            action = ScanPayloadNormalizer.ATOL_ACTION,
            barcode = "QR-1",
            symbology = "QR_CODE",
        )

        assertEquals(1, scans.size)
        assertEquals("QR-1", scans.single().value)
        assertEquals("QR_CODE", scans.single().symbology)
        assertEquals("accepted", diagnostics.single().state)
    }

    @Test
    fun wrongActionAndEmptyDataDoNotDispatch() {
        val scans = mutableListOf<ScanPayload>()
        val diagnostics = mutableListOf<MaskedScanDiagnostic>()
        val adapter = AtolBroadcastScannerAdapter(
            registrar = FakeRegistrar(),
            onScan = { scans.add(it) },
            onDiagnostic = { diagnostics.add(it) },
            clock = { 1000L },
        )

        adapter.handleRawBroadcast("wrong", "ABC", "EAN13")
        adapter.handleRawBroadcast(ScanPayloadNormalizer.ATOL_ACTION, "", "EAN13")

        assertTrue(scans.isEmpty())
        assertEquals(listOf("wrong-action", "empty-data"), diagnostics.map { it.state })
    }

    @Test
    fun repeatedAndFastDifferentScansArePassedThrough() {
        val scans = mutableListOf<ScanPayload>()
        val adapter = AtolBroadcastScannerAdapter(
            registrar = FakeRegistrar(),
            onScan = { scans.add(it) },
            onDiagnostic = {},
            clock = { 1000L },
        )

        adapter.handleRawBroadcast(ScanPayloadNormalizer.ATOL_ACTION, "A", "QR")
        adapter.handleRawBroadcast(ScanPayloadNormalizer.ATOL_ACTION, "A", "QR")
        adapter.handleRawBroadcast(ScanPayloadNormalizer.ATOL_ACTION, "B", "QR")

        assertEquals(listOf("A", "A", "B"), scans.map { it.value })
    }

    @Test
    fun receiverFlagsAreExportedOnlyForApi33AndNewer() {
        assertNull(ReceiverRegistrationFlags.forSdk(24))
        assertEquals(Context.RECEIVER_EXPORTED, ReceiverRegistrationFlags.forSdk(33))
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
