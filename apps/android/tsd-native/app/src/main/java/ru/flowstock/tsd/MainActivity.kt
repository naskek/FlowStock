package ru.flowstock.tsd

import android.app.Activity
import android.os.Bundle
import android.webkit.WebView
import android.widget.TextView
import ru.flowstock.tsd.scanner.AtolBroadcastScannerAdapter
import ru.flowstock.tsd.web.FlowStockWebViewController

class MainActivity : Activity() {
    private lateinit var statusView: TextView
    private lateinit var webController: FlowStockWebViewController
    private lateinit var scannerAdapter: AtolBroadcastScannerAdapter

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        statusView = findViewById(R.id.nativeStatus)
        val webView = findViewById<WebView>(R.id.tsdWebView)

        webController = FlowStockWebViewController(
            webView = webView,
            initialUrl = BuildConfig.DEFAULT_TSD_URL,
            onStatus = ::setStatus,
        )
        scannerAdapter = AtolBroadcastScannerAdapter(
            context = this,
            onScan = { payload -> webController.dispatchScan(payload) },
            onDiagnostic = { diagnostic ->
                setStatus(
                    "scan ${diagnostic.state}: len=${diagnostic.length} hash=${diagnostic.hash} " +
                        "masked=${diagnostic.maskedValue} sym=${diagnostic.symbology.orEmpty()}",
                )
            },
        )

        webController.load()
    }

    override fun onStart() {
        super.onStart()
        scannerAdapter.start()
    }

    override fun onStop() {
        scannerAdapter.stop()
        super.onStop()
    }

    @Deprecated("Use platform back dispatcher when min platform no longer requires legacy override.")
    override fun onBackPressed() {
        webController.handleBack {
            moveTaskToBack(true)
        }
    }

    private fun setStatus(message: String) {
        runOnUiThread {
            statusView.text = message
        }
    }
}
