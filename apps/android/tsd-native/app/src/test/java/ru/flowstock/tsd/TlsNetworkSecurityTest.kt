package ru.flowstock.tsd

import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TlsNetworkSecurityTest {
    @Test
    fun releaseNetworkSecurityTrustsSystemAndUserStoresOnly() {
        val config = readSourceFile("src/main/res/xml/network_security_config.xml")

        assertTrue(config.contains("""<certificates src="system" />"""))
        assertTrue(config.contains("""<certificates src="user" />"""))
        assertFalse(config.contains("flowstock_dev_ca"))
        assertFalse(config.contains("cleartextTrafficPermitted=\"true\""))
    }

    private fun readSourceFile(relativePath: String): String {
        val candidates = listOf(
            Paths.get(relativePath),
            Paths.get("app").resolve(relativePath),
        )
        val path: Path = candidates.firstOrNull { Files.exists(it) }
            ?: error("Source file not found: $relativePath")
        return String(Files.readAllBytes(path), Charsets.UTF_8)
    }
}
