const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const tsdDir = __dirname;
const lifecycleJs = fs.readFileSync(path.join(tsdDir, "scanner-lifecycle-diagnostics.js"), "utf8");
const scannerJs = fs.readFileSync(path.join(tsdDir, "scanner.js"), "utf8");
const appJs = fs.readFileSync(path.join(tsdDir, "app.js"), "utf8");
const indexHtml = fs.readFileSync(path.join(tsdDir, "index.html"), "utf8");
const serviceWorkerJs = fs.readFileSync(path.join(tsdDir, "service-worker.js"), "utf8");
const appVersionJs = fs.readFileSync(path.join(tsdDir, "app-version.js"), "utf8");

function createFakeElement(tagName, id) {
  const listeners = {};
  const attrs = {};
  return {
    tagName: String(tagName || "DIV").toUpperCase(),
    id: id || "",
    className: "",
    value: "",
    disabled: false,
    isConnected: true,
    isContentEditable: false,
    setAttribute(name, value) {
      attrs[name] = String(value);
    },
    getAttribute(name) {
      return Object.prototype.hasOwnProperty.call(attrs, name) ? attrs[name] : null;
    },
    addEventListener(type, handler) {
      listeners[type] = listeners[type] || [];
      listeners[type].push(handler);
    },
    removeEventListener(type, handler) {
      listeners[type] = (listeners[type] || []).filter((entry) => entry !== handler);
    },
    dispatch(type, event) {
      const payload = Object.assign(
        {
          type,
          target: this,
          preventDefault() {
            payload.defaultPrevented = true;
          },
        },
        event || {}
      );
      (listeners[type] || []).slice().forEach((handler) => handler(payload));
      return payload;
    },
    focus() {
      if (this.ownerDocument) {
        this.ownerDocument.activeElement = this;
      }
    },
    getClientRects() {
      return [1];
    },
  };
}

function createFakeDocument() {
  const listeners = {};
  const body = createFakeElement("body", "body");
  const document = {
    body,
    activeElement: body,
    visibilityState: "visible",
    hidden: false,
    createElement(tag) {
      const element = createFakeElement(tag);
      element.ownerDocument = document;
      return element;
    },
    addEventListener(type, handler) {
      listeners[type] = listeners[type] || [];
      listeners[type].push(handler);
    },
    removeEventListener(type, handler) {
      listeners[type] = (listeners[type] || []).filter((entry) => entry !== handler);
    },
    dispatch(type, event) {
      const payload = Object.assign(
        {
          type,
          target: (event && event.target) || body,
          preventDefault() {
            payload.defaultPrevented = true;
          },
        },
        event || {}
      );
      (listeners[type] || []).slice().forEach((handler) => handler(payload));
      return payload;
    },
    hasFocus() {
      return true;
    },
    _listeners: listeners,
  };
  body.ownerDocument = document;
  return document;
}

function loadLifecycleContext(extra) {
  const context = Object.assign(
    {
      console,
      setTimeout,
      clearTimeout,
      TSD_PWA_VERSION: "55",
      navigator: { clipboard: { writeText: () => Promise.resolve(true) } },
      URL: { createObjectURL: () => "blob:lifecycle", revokeObjectURL() {} },
      Blob,
      document: createFakeDocument(),
    },
    extra || {}
  );
  context.window = context;
  context.self = context;
  vm.createContext(context);
  vm.runInContext(lifecycleJs, context, { filename: "scanner-lifecycle-diagnostics.js" });
  return context;
}

function loadScannerContext() {
  const timers = [];
  let timerId = 1;
  const context = {
    console,
    document: createFakeDocument(),
    FlowStockAndroidBridge: null,
    setTimeout(fn) {
      const id = timerId++;
      timers.push({ id, fn, canceled: false });
      return id;
    },
    clearTimeout(id) {
      const timer = timers.find((entry) => entry.id === id);
      if (timer) {
        timer.canceled = true;
      }
    },
  };
  context.window = context;
  context.self = context;
  vm.createContext(context);
  vm.runInContext(scannerJs, context, { filename: "scanner.js" });
  return { context, timers };
}

function testLifecycleReportMasksProductionPayloads() {
  const context = loadLifecycleContext();
  const diagnostics = context.FlowStockScannerLifecycleDiagnostics;
  diagnostics.setStateProvider(() => ({
    routeName: "fillingDoc",
    routeKey: "fillingDoc:120",
    routeGeneration: 7,
    scanHandlerActive: false,
    blockReason: "handler-off",
  }));
  diagnostics.record("raw-keydown", {
    key: "Enter",
    value: "HU-SECRET-123456",
    rawValue: "HU-SECRET-123456",
    target: {
      tagName: "INPUT",
      id: "fillingScanInput",
      scanAllow: "1",
      valueSnapshot: "HU-SECRET-123456",
    },
  });
  diagnostics.record("scan-emitted", { value: "HU-SECRET-123456", scanId: "scan-1" });
  diagnostics.record("manager-received", { value: "HU-SECRET-123456", scanId: "scan-1" });
  diagnostics.record("handler-missing", { value: "HU-SECRET-123456", scanId: "scan-1" });
  const report = diagnostics.buildReport();
  const json = JSON.stringify(report);
  assert.strictEqual(report.schema, "flowstock-scanner-lifecycle-report/v1");
  assert.strictEqual(report.appVersion, "55");
  assert.strictEqual(report.summary.classification, "C_HANDLER_MISSING");
  assert(!json.includes("HU-SECRET-123456"), "lifecycle report must not persist full production payload");
  assert(json.includes("valueLength"));
  assert(json.includes("valueHash"));
  assert(json.includes("valueMasked"));
}

function testLifecycleClassificationRawWithoutScan() {
  const context = loadLifecycleContext();
  const diagnostics = context.FlowStockScannerLifecycleDiagnostics;
  diagnostics.record("raw-input", { data: "ABC", target: { valueSnapshot: "ABC" } });
  const report = diagnostics.buildReport();
  assert.strictEqual(report.summary.classification, "B_PROVIDER_NO_SCAN");
}

function testClassificationIgnoresLifecycleEvents() {
  const context = loadLifecycleContext();
  const diagnostics = context.FlowStockScannerLifecycleDiagnostics;
  diagnostics.record("visibilitychange", { visibilityState: "visible" });
  diagnostics.record("window-focus", { documentHasFocus: true });
  diagnostics.record("focus-target-selected", { strategy: "preferred-target" });

  const lifecycleOnlyReport = diagnostics.buildReport();
  assert.strictEqual(lifecycleOnlyReport.summary.classification, "INSUFFICIENT_EVIDENCE");
  assert.strictEqual(lifecycleOnlyReport.summary.lastRawEventAt, null);

  diagnostics.record("raw-input", { data: "ABC", target: { valueSnapshot: "ABC" } });
  const rawReport = diagnostics.buildReport();
  assert.strictEqual(rawReport.summary.classification, "B_PROVIDER_NO_SCAN");
  assert(rawReport.summary.lastRawEventAt);
}

function testRecursivePayloadMasking() {
  const context = loadLifecycleContext();
  const diagnostics = context.FlowStockScannerLifecycleDiagnostics;
  diagnostics.record("raw-input", {
    providerType: "keyboard",
    attemptId: "attempt-1",
    scanId: "scan-1",
    payload: "SECRET-ROOT",
    nested: {
      data: "SECRET-DATA",
      code: "SECRET-CODE",
      ordinary: "visible technical note",
      deeper: {
        text: "SECRET-TEXT",
        scanData: "SECRET-SCAN",
      },
    },
  });

  const report = diagnostics.buildReport();
  const json = JSON.stringify(report);
  ["SECRET-ROOT", "SECRET-DATA", "SECRET-CODE", "SECRET-TEXT", "SECRET-SCAN"].forEach((secret) => {
    assert(!json.includes(secret), "report must not contain full secret payload " + secret);
  });

  const detail = report.events[0].detail;
  assert.strictEqual(detail.providerType, "keyboard");
  assert.strictEqual(detail.attemptId, "attempt-1");
  assert.strictEqual(detail.scanId, "scan-1");
  assert.strictEqual(detail.nested.ordinary, "visible technical note");
  assert.strictEqual(detail.payloadLength, "SECRET-ROOT".length);
  assert(detail.payloadHash);
  assert(detail.payloadMasked);
  assert.strictEqual(detail.nested.dataLength, "SECRET-DATA".length);
  assert(detail.nested.dataHash);
  assert(detail.nested.dataMasked);
  assert.strictEqual(detail.nested.codeLength, "SECRET-CODE".length);
  assert(detail.nested.codeHash);
  assert(detail.nested.codeMasked);
  assert.strictEqual(detail.nested.deeper.textLength, "SECRET-TEXT".length);
  assert(detail.nested.deeper.textHash);
  assert(detail.nested.deeper.textMasked);
  assert.strictEqual(detail.nested.deeper.scanDataLength, "SECRET-SCAN".length);
  assert(detail.nested.deeper.scanDataHash);
  assert(detail.nested.deeper.scanDataMasked);
}

function testUniqueSessionIds() {
  const first = loadLifecycleContext().FlowStockScannerLifecycleDiagnostics.buildReport();
  const second = loadLifecycleContext().FlowStockScannerLifecycleDiagnostics.buildReport();
  assert.notStrictEqual(first.sessionId, second.sessionId);
}

function testScannerLifecycleObserverReceivesTransportEvents() {
  const scanner = loadScannerContext();
  const manager = scanner.context.FlowStockScanner.createScannerManager({
    canScan: () => true,
    inputDelayMs: 1000,
    keyDelayMs: 1000,
  });
  const events = [];
  const scans = [];
  manager.setLifecycleObserver((event) => events.push(event));
  manager.setHandler((scan) => scans.push(scan));
  manager.start();

  "ABC".split("").forEach((key) => scanner.context.document.dispatch("keydown", { key }));
  scanner.context.document.dispatch("keydown", { key: "Enter" });

  assert.strictEqual(scans.length, 1);
  assert(events.some((event) => event.type === "raw-keydown"));
  assert(events.some((event) => event.type === "dedupe-accepted"));
  assert(events.some((event) => event.type === "scan-emitted"));
  assert(events.some((event) => event.type === "manager-received"));
  assert(events.some((event) => event.type === "handler-dispatched"));
}

function testShellAndAppIntegration() {
  assert(appVersionJs.includes('var version = "55"'));
  assert(indexHtml.indexOf("scanner-lifecycle-diagnostics.js") < indexHtml.indexOf("scanner.js"));
  assert(serviceWorkerJs.includes('"./scanner-lifecycle-diagnostics.js"'));
  assert(appJs.includes("routeRenderGeneration"));
  assert(appJs.includes("handleAppResume"));
  assert(appJs.includes("setLifecycleObserver"));
  assert(appJs.includes('id="scanLifecycleDownloadBtn"'));
  assert(appJs.includes('id="scanLifecycleCopyBtn"'));
  assert(appJs.includes('id="scanLifecycleClearBtn"'));
  assert(appJs.includes('id="scanLifecycleSnapshotBtn"'));
  assert(appJs.includes('handleAppResume("visibilitychange")'));
  assert(appJs.includes('handleAppResume("window-focus")'));
}

testLifecycleReportMasksProductionPayloads();
testLifecycleClassificationRawWithoutScan();
testClassificationIgnoresLifecycleEvents();
testRecursivePayloadMasking();
testUniqueSessionIds();
testScannerLifecycleObserverReceivesTransportEvents();
testShellAndAppIntegration();

console.log("scanner-lifecycle.test.js passed");
