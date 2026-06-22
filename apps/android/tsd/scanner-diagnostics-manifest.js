(function (root) {
  "use strict";

  var steps = [
    {
      id: "itf14",
      number: 1,
      title: "ITF-14 / GTIN-14",
      expectedValue: "04601234567893",
      expectedSymbologies: [
        "ITF14",
        "ITF-14",
        "EAN14",
        "EAN-14",
        "INTERLEAVED_2_OF_5",
      ],
    },
    {
      id: "datamatrix",
      number: 2,
      title: "Data Matrix",
      expectedValue:
        "FLOWSTOCK|SCANNER-DIAG|V1|DM|0123456789|ABCDEFGHIJKLMNOPQRSTUVWXYZ",
      expectedSymbologies: ["DATAMATRIX", "DATA_MATRIX", "DM"],
    },
    {
      id: "qrcode",
      number: 3,
      title: "QR Code",
      expectedValue:
        "FLOWSTOCK|SCANNER-DIAG|V1|QR|0123456789|ABCDEFGHIJKLMNOPQRSTUVWXYZ",
      expectedSymbologies: ["QRCODE", "QR_CODE", "QR"],
    },
  ].map(function (step) {
    step.expectedLength = step.expectedValue.length;
    return step;
  });

  root.FlowStockScannerDiagnosticManifest = {
    schemaVersion: 1,
    setId: "FS-SCANDIAG-V1",
    printTitle: "FLOWSTOCK SCANNER DIAGNOSTICS V1",
    printerArtifact: "flowstock_scanner_diagnostics_v1_TSC_TE210_100x72_working.prn",
    label: {
      widthMm: 100,
      heightMm: 72,
      gapMm: 3,
      printerModel: "TSC TE210",
      dpi: 203,
    },
    steps: steps,
  };
})(typeof self !== "undefined" ? self : window);
