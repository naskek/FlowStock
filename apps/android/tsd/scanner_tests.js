(function () {
  "use strict";

  var logEl = document.getElementById("testLog");
  var results = [];

  function log(line) {
    results.push(line);
    if (logEl) {
      logEl.textContent = results.join("\n");
    }
  }

  function assert(name, condition) {
    if (!condition) {
      throw new Error("FAIL: " + name);
    }
    log("OK: " + name);
  }

  function run() {
    if (!window.FlowStockScanner || !window.FlowStockScanner._test) {
      log("Scanner module not available.");
      return;
    }
    var test = window.FlowStockScanner._test;

    var payload = test.normalizeScanPayload(" 123 ");
    assert("normalize string", payload.value === "123");

    var payloadObj = test.normalizeScanPayload({
      data: "46001234",
      symbology: "EAN13",
      ts: 123,
    });
    assert("normalize object", payloadObj.value === "46001234");
    assert("normalize symbology", payloadObj.symbology === "EAN13");
    assert("normalize ts", payloadObj.ts === 123);

    var dedupe = test.createDedupe(120);
    assert("dedupe first", dedupe("ABC", 0) === true);
    assert("dedupe repeat", dedupe("ABC", 100) === false);
    assert("dedupe later", dedupe("ABC", 200) === true);

    var savedBridge = window.FlowStockAndroidBridge;
    window.FlowStockAndroidBridge = null;
    assert("provider auto -> keyboard", test.selectProvider("auto") === "keyboard");
    assert("provider keyboard", test.selectProvider("keyboard") === "keyboard");
    assert("provider intent fallback", test.selectProvider("intent") === "keyboard");
    window.FlowStockAndroidBridge = savedBridge;

    log("All tests passed.");
  }

  try {
    run();
  } catch (error) {
    log(String(error && error.message ? error.message : error));
  }
})();
