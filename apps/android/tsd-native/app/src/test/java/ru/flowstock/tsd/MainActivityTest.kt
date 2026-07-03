package ru.flowstock.tsd

import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import org.junit.Assert.assertFalse
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import ru.flowstock.tsd.config.ServerEndpoint
import ru.flowstock.tsd.diagnostics.MaskedScanDiagnostics

class MainActivityTest {
    @Test
    fun activityLayoutHasNoVisibleNativeStatusPanel() {
        val layout = readSourceFile("src/main/res/layout/activity_main.xml")

        assertFalse(layout.contains("nativeStatus"))
        assertTrue(layout.contains("android:id=\"@+id/tsdWebView\""))
        assertTrue(layout.contains("android:layout_width=\"match_parent\""))
        assertTrue(layout.contains("android:layout_height=\"match_parent\""))
        assertTrue(layout.contains("android:id=\"@+id/serverSetupContainer\""))
        assertTrue(layout.contains("android:visibility=\"gone\""))
    }

    @Test
    fun statusPlumbingDoesNotDependOnVisibleStatusView() {
        val activity = readSourceFile("src/main/java/ru/flowstock/tsd/MainActivity.kt")

        assertFalse(activity.contains("R.id.nativeStatus"))
        assertTrue(activity.contains("webView = findViewById(R.id.tsdWebView)"))
        assertTrue(activity.contains("Log.i(NATIVE_STATUS_LOG_TAG, message)"))
    }

    @Test
    fun activityNoLongerUsesBuildTimeTsdUrl() {
        val activity = readSourceFile("src/main/java/ru/flowstock/tsd/MainActivity.kt")
        val build = readSourceFile("build.gradle.kts")

        assertFalse(activity.contains("BuildConfig.DEFAULT_TSD_URL"))
        assertFalse(build.contains("flowstockTsdUrl"))
        assertFalse(build.contains("DEFAULT_TSD_URL"))
    }

    @Test
    fun nativeSourceAndResourcesDoNotContainMojibake() {
        val files = listOf(
            "src/main/java/ru/flowstock/tsd/MainActivity.kt",
            "src/main/res/layout/activity_main.xml",
            "src/main/res/values/strings.xml",
        )

        for (file in files) {
            val text = readSourceFile(file)
            assertFalse("$file contains mojibake", text.contains("╨"))
            assertFalse("$file contains mojibake", text.contains("╤"))
            assertFalse("$file contains mojibake", text.contains("тА"))
        }
    }

    @Test
    fun setupLayoutUsesStringResourcesForOperatorText() {
        val layout = readSourceFile("src/main/res/layout/activity_main.xml")
        val activity = readSourceFile("src/main/java/ru/flowstock/tsd/MainActivity.kt")
        val strings = readSourceFile("src/main/res/values/strings.xml")

        assertTrue(layout.contains("@string/server_setup_title"))
        assertTrue(layout.contains("@string/server_return_current"))
        assertTrue(activity.contains("getString(R.string.server_change_title)"))
        assertTrue(activity.contains("getString(R.string.server_setup_first_launch)"))
        assertTrue(strings.contains("server_retry_current"))
    }

    @Test
    fun setupStateStopsScannerAndBlocksHiddenWebViewDispatch() {
        val activity = readSourceFile("src/main/java/ru/flowstock/tsd/MainActivity.kt")

        assertTrue(activity.contains("if (isSetupVisible())"))
        assertTrue(activity.contains("false"))
        assertTrue(activity.contains("sessionManager.pauseScannerForSetup()"))
        assertTrue(activity.contains("sessionManager.restoreCurrentSession()"))
        assertTrue(activity.contains("returnToCurrentServerButton"))
    }

    @Test
    fun scannerDiagnosticStatusUsesMaskedValueOnly() {
        val fullBarcode = "4601234567890"
        val diagnostic = MaskedScanDiagnostics.fromValue(
            fullBarcode,
            symbology = "EAN13",
            state = "accepted",
            timestamp = 1000L,
        )

        val status = MainActivity.formatScannerDiagnosticStatus(diagnostic)

        assertTrue(status.contains("len=13"))
        assertTrue(status.contains("hash=" + diagnostic.hash))
        assertTrue(status.contains("masked=460...7890"))
        assertTrue(status.contains("sym=EAN13"))
        assertFalse(status.contains(fullBarcode))
    }

    @Test
    fun switchFailureFallbackShowsCurrentEndpointInsteadOfFailedEndpoint() {
        val currentEndpoint = ServerEndpoint("https://flowstock.local:7154")

        assertEquals("https://flowstock.local:7154", MainActivity.switchFailureEndpointText(currentEndpoint))
        assertEquals("", MainActivity.switchFailureEndpointText(null))
    }

    private fun readSourceFile(relativePath: String): String {
        val path = sourceFile(relativePath)
        return String(Files.readAllBytes(path), Charsets.UTF_8)
    }

    private fun sourceFile(relativePath: String): Path {
        val candidates = listOf(
            Paths.get(relativePath),
            Paths.get("app").resolve(relativePath),
        )
        return candidates.firstOrNull { Files.exists(it) }
            ?: error("Source file not found: $relativePath")
    }
}
