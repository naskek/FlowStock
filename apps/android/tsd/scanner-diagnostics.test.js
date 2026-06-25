const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const tsdDir = __dirname;
const repoRoot = path.resolve(tsdDir, "../../..");
const prnPath = path.join(
  repoRoot,
  "flowstock_scanner_diagnostics_v1_TSC_TE210_100x72_working.prn"
);
const manifestJs = fs.readFileSync(path.join(tsdDir, "scanner-diagnostics-manifest.js"), "utf8");
const diagnosticsStoreJs = fs.readFileSync(
  path.join(tsdDir, "scanner-diagnostics-store.js"),
  "utf8"
);
const diagnosticsJs = fs.readFileSync(path.join(tsdDir, "scanner-diagnostics.js"), "utf8");
const scannerJs = fs.readFileSync(path.join(tsdDir, "scanner.js"), "utf8");
const storageJs = fs.readFileSync(path.join(tsdDir, "storage.js"), "utf8");
const appJs = fs.readFileSync(path.join(tsdDir, "app.js"), "utf8");
const indexHtml = fs.readFileSync(path.join(tsdDir, "index.html"), "utf8");
const serviceWorkerJs = fs.readFileSync(path.join(tsdDir, "service-worker.js"), "utf8");
const appVersionJs = fs.readFileSync(path.join(tsdDir, "app-version.js"), "utf8");

function loadDiagnosticsContext(extra) {
  const context = Object.assign(
    {
      console,
      setTimeout,
      clearTimeout,
      Blob,
      URL: {
        createObjectURL() {
          return "blob:scanner-diagnostics";
        },
        revokeObjectURL() {},
      },
      navigator: {
        onLine: true,
        userAgent: "Node Scanner Diagnostics",
        platform: "test",
        language: "ru-RU",
        maxTouchPoints: 1,
        clipboard: {
          writes: [],
          writeText(text) {
            this.writes.push(text);
            return Promise.resolve();
          },
        },
      },
      screen: { width: 360, height: 640 },
      devicePixelRatio: 2,
      location: { hash: "" },
      TSD_PWA_VERSION: "55",
      document: createFakeDocument(),
    },
    extra || {}
  );
  context.window = context;
  context.self = context;
  vm.createContext(context);
  vm.runInContext(manifestJs, context, { filename: "scanner-diagnostics-manifest.js" });
  vm.runInContext(diagnosticsStoreJs, context, { filename: "scanner-diagnostics-store.js" });
  vm.runInContext(diagnosticsJs, context, { filename: "scanner-diagnostics.js" });
  return context;
}

function createFakeElement(tagName, id) {
  const listeners = {};
  const attrs = {};
  return {
    tagName: String(tagName || "DIV").toUpperCase(),
    id: id || "",
    className: "",
    value: "",
    readOnly: false,
    isContentEditable: false,
    parentNode: null,
    children: [],
    style: {},
    href: "",
    download: "",
    textContent: "",
    innerHTML: "",
    hidden: false,
    disabled: false,
    setAttribute(name, value) {
      attrs[name] = String(value);
    },
    getAttribute(name) {
      return Object.prototype.hasOwnProperty.call(attrs, name) ? attrs[name] : null;
    },
    hasAttribute(name) {
      return Object.prototype.hasOwnProperty.call(attrs, name);
    },
    removeAttribute(name) {
      delete attrs[name];
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
    click() {
      this.dispatch("click", { target: this });
    },
    focus() {
      if (this.ownerDocument) {
        this.ownerDocument.activeElement = this;
      }
    },
    getClientRects() {
      return [1];
    },
    appendChild(child) {
      child.parentNode = this;
      this.children.push(child);
    },
    removeChild(child) {
      this.children = this.children.filter((entry) => entry !== child);
      child.parentNode = null;
    },
    _attrs: attrs,
    _listeners: listeners,
  };
}

function createFakeDocument() {
  const listeners = {};
  const elements = {};
  const body = createFakeElement("body", "body");
  const document = {
    body,
    activeElement: body,
    visibilityState: "visible",
    createElement(tag) {
      const element = createFakeElement(tag);
      element.ownerDocument = document;
      return element;
    },
    getElementById(id) {
      return elements[id] || null;
    },
    register(element) {
      element.ownerDocument = document;
      if (element.id) {
        elements[element.id] = element;
      }
      return element;
    },
    querySelectorAll(selector) {
      const values = Object.keys(elements).map((key) => elements[key]);
      const attrMatch = /^\[([^=\]]+)(?:="([^"]*)")?\]$/.exec(selector || "");
      if (!attrMatch) {
        return [];
      }
      return values.filter((element) => {
        const value = element.getAttribute(attrMatch[1]);
        if (value == null) {
          return false;
        }
        return attrMatch[2] == null || value === attrMatch[2];
      });
    },
    addEventListener(type, handler) {
      listeners[type] = listeners[type] || [];
      listeners[type].push(handler);
    },
    removeEventListener(type, handler) {
      listeners[type] = (listeners[type] || []).filter((entry) => entry !== handler);
    },
    dispatch(type, event) {
      const target = (event && event.target) || body;
      const payload = Object.assign(
        {
          type,
          target,
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
    _elements: elements,
  };
  body.ownerDocument = document;
  return document;
}

function createFakeApp(document) {
  let html = "";
  let mountedElements = [];
  function detachMounted() {
    mountedElements.forEach((element) => {
      element.isConnected = false;
      if (element.id && document._elements[element.id] === element) {
        delete document._elements[element.id];
      }
    });
    mountedElements = [];
  }
  function mountScannerDiagScanInput(nextHtml) {
    if (!nextHtml.includes('id="scannerDiagScanInput"')) {
      return;
    }
    const input = createFakeElement("input", "scannerDiagScanInput");
    input.className = "form-input filling-scan-input tsd-scan-input-hidden";
    input.isConnected = true;
    input.setAttribute("type", "text");
    input.setAttribute("autocomplete", "off");
    input.setAttribute("autocapitalize", "off");
    input.setAttribute("autocorrect", "off");
    input.setAttribute("spellcheck", "false");
    input.setAttribute("data-scan-allow", "1");
    document.register(input);
    mountedElements.push(input);
  }
  return {
    get innerHTML() {
      return html;
    },
    set innerHTML(value) {
      html = String(value || "");
      detachMounted();
      mountScannerDiagScanInput(html);
    },
  };
}

function createMemoryStorage() {
  return {
    draft: null,
    reports: [],
    saveActiveSession(session) {
      this.draft = JSON.parse(JSON.stringify(session));
      return Promise.resolve(session);
    },
    loadActiveSession() {
      return Promise.resolve(this.draft);
    },
    clearActiveSession() {
      this.draft = null;
      return Promise.resolve(true);
    },
    saveReport(report) {
      this.reports = this.reports.filter((entry) => entry.sessionId !== report.sessionId);
      this.reports.push(JSON.parse(JSON.stringify(report)));
      this.reports.sort((a, b) => Date.parse(b.finishedAt || b.startedAt || "") - Date.parse(a.finishedAt || a.startedAt || ""));
      this.reports = this.reports.slice(0, 20);
      return Promise.resolve(report);
    },
    listReports() {
      return Promise.resolve(this.reports.slice());
    },
    getReport(id) {
      return Promise.resolve(this.reports.find((entry) => entry.sessionId === id) || null);
    },
    deleteReport(id) {
      this.reports = this.reports.filter((entry) => entry.sessionId !== id);
      return Promise.resolve(true);
    },
  };
}

function createControllerFixture() {
  const context = loadDiagnosticsContext();
  const storage = createMemoryStorage();
  const app = createFakeApp(context.document);
  let observer = null;
  let scanHandler = null;
  const preferredTargets = [];
  const renderEvents = [];
  const deps = {
    app,
    diagnosticStore: storage,
    scannerManager: {
      setDiagnosticObserver(handler) {
        observer = handler;
      },
      clearDiagnosticObserver() {
        observer = null;
      },
      getSettings() {
        return {
          mode: "auto",
          providerType: "keyboard",
          dedupeMs: 120,
          inputDelayMs: 60,
          keyDelayMs: 80,
          androidBridgeAvailable: false,
        };
      },
    },
    setScanHandler(handler) {
      scanHandler = handler;
    },
    setPreferredScanTarget(target) {
      preferredTargets.push(target);
      renderEvents.push({ type: "preferred-target", target });
    },
    finishRouteRender() {
      renderEvents.push({ type: "finish-render" });
    },
    navigate(path) {
      deps.lastNavigation = path;
    },
    currentRoute: { name: "scannerDiagnostics" },
  };
  const controller = context.FlowStockScannerDiagnostics._test.createController(deps);
  return {
    context,
    storage,
    app,
    deps,
    controller,
    getObserver: () => observer,
    getScanHandler: () => scanHandler,
    getPreferredTargets: () => preferredTargets,
    getRenderEvents: () => renderEvents,
  };
}

function createScannerContext(options) {
  options = options || {};
  const document = createFakeDocument();
  const timers = [];
  let nextTimer = 1;
  const context = {
    console,
    document,
    FlowStockAndroidBridge: options.bridge || null,
    setTimeout(fn) {
      const id = nextTimer++;
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
  return {
    context,
    document,
    timers,
    runTimers() {
      while (timers.length) {
        const timer = timers.shift();
        if (!timer.canceled) {
          timer.fn();
        }
      }
    },
  };
}

function createDeferred() {
  let resolve;
  let reject;
  const promise = new Promise((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

function flushMicrotasks() {
  return Promise.resolve().then(() => Promise.resolve());
}

function dispatchKeyboardScan(document, value) {
  String(value)
    .split("")
    .forEach((key) => document.dispatch("keydown", { key }));
  document.dispatch("keydown", { key: "Enter" });
}

function createScanAllowedInput(document, id) {
  const input = document.register(createFakeElement("input", id || "scanAllowedInput"));
  input.setAttribute("data-scan-allow", "1");
  return input;
}

function countEvents(events, type) {
  return events.filter((event) => event.type === type).length;
}

function eventsOfType(events, type) {
  return events.filter((event) => event.type === type);
}

function createFakeIndexedDb() {
  const databases = {};
  function createRequest() {
    return { onsuccess: null, onerror: null, result: undefined, error: null };
  }
  function asyncCall(fn) {
    setTimeout(fn, 0);
  }
  function createDomStringList(values) {
    return {
      contains(name) {
        return values.indexOf(name) !== -1;
      },
      push(name) {
        if (values.indexOf(name) === -1) {
          values.push(name);
        }
      },
    };
  }
  function createDatabase(name) {
    const stores = {};
    const storeNames = [];
    const db = {
      name,
      stores,
      objectStoreNames: createDomStringList(storeNames),
      createObjectStore(storeName, options) {
        const store = createStore(storeName, options || {});
        stores[storeName] = store;
        db.objectStoreNames.push(storeName);
        return store.api;
      },
      transaction(storeName) {
        const names = Array.isArray(storeName) ? storeName : [storeName];
        const tx = {
          error: null,
          oncomplete: null,
          onerror: null,
          objectStore(name) {
            if (!stores[name]) {
              stores[name] = createStore(name, { keyPath: "id" });
              db.objectStoreNames.push(name);
            }
            return stores[name].api;
          },
        };
        asyncCall(() => {
          asyncCall(() => {
            if (tx.oncomplete) {
              tx.oncomplete();
            }
          });
        });
        names.forEach((entry) => {
          if (!stores[entry]) {
            stores[entry] = createStore(entry, { keyPath: "id" });
            db.objectStoreNames.push(entry);
          }
        });
        return tx;
      },
    };
    return db;
  }
  function createStore(name, options) {
    const rows = new Map();
    let autoId = 1;
    const indexNames = [];
    const api = {
      name,
      keyPath: options.keyPath || null,
      indexNames: createDomStringList(indexNames),
      createIndex(indexName) {
        api.indexNames.push(indexName);
        return {};
      },
      index() {
        return api;
      },
      put(value) {
        const request = createRequest();
        asyncCall(() => {
          const record = JSON.parse(JSON.stringify(value));
          const key = api.keyPath ? record[api.keyPath] : autoId++;
          rows.set(String(key), record);
          request.result = key;
          if (request.onsuccess) {
            request.onsuccess({ target: request });
          }
        });
        return request;
      },
      get(key) {
        const request = createRequest();
        asyncCall(() => {
          request.result = rows.get(String(key));
          if (request.onsuccess) {
            request.onsuccess({ target: request });
          }
        });
        return request;
      },
      delete(key) {
        const request = createRequest();
        asyncCall(() => {
          rows.delete(String(key));
          if (request.onsuccess) {
            request.onsuccess({ target: request });
          }
        });
        return request;
      },
      openCursor() {
        const request = createRequest();
        const values = Array.from(rows.entries());
        let index = 0;
        function emit() {
          const entry = values[index];
          request.result = entry
            ? {
                key: entry[0],
                value: entry[1],
                continue() {
                  index += 1;
                  asyncCall(emit);
                },
              }
            : null;
          if (request.onsuccess) {
            request.onsuccess({ target: request });
          }
        }
        asyncCall(emit);
        return request;
      },
      _rows: rows,
    };
    return { api, rows };
  }
  return {
    open(name, version) {
      const request = createRequest();
      asyncCall(() => {
        let db = databases[name];
        const oldVersion = db ? db.version || 1 : 0;
        if (!db) {
          db = createDatabase(name);
          databases[name] = db;
        }
        db.version = version;
        request.result = db;
        if (version > oldVersion && request.onupgradeneeded) {
          request.onupgradeneeded({
            target: request,
            currentTarget: {
              transaction: db.transaction(Object.keys(db.stores)),
            },
            oldVersion,
          });
        }
        if (request.onsuccess) {
          request.onsuccess({ target: request });
        }
      });
      return request;
    },
  };
}

async function testManifestAndPrn() {
  assert(fs.existsSync(prnPath), "PRN should exist in repository root");
  const prn = fs.readFileSync(prnPath, "utf8");
  const context = loadDiagnosticsContext();
  const manifest = context.FlowStockScannerDiagnosticManifest;
  assert(prn.includes("SIZE 100 mm,72 mm"));
  assert(prn.includes("GAP 3 mm,0 mm"));
  assert.strictEqual((prn.match(/^PRINT\b/gm) || []).length, 3);
  assert.deepStrictEqual(
    ["ITF14", "DMATRIX", "QRCODE"].map((token) => prn.indexOf(token) >= 0),
    [true, true, true]
  );
  assert(prn.indexOf("ITF14") < prn.indexOf("DMATRIX"));
  assert(prn.indexOf("DMATRIX") < prn.indexOf("QRCODE"));
  assert(prn.includes('"0460123456789"'));
  assert.strictEqual(manifest.steps[0].expectedValue, "04601234567893");
  assert(prn.includes(manifest.printTitle));
  assert.strictEqual(manifest.steps[1].expectedLength, manifest.steps[1].expectedValue.length);
  assert.strictEqual(manifest.steps[2].expectedLength, manifest.steps[2].expectedValue.length);
  assert(prn.includes('"' + manifest.steps[1].expectedValue + '"'));
  assert(prn.includes('"' + manifest.steps[2].expectedValue + '"'));
  assert.strictEqual(manifest.printTitle, "FLOWSTOCK SCANNER DIAGNOSTICS V1");
  assert.strictEqual(manifest.setId, "FS-SCANDIAG-V1");
  assert.strictEqual(manifest.printerArtifact, path.basename(prnPath));
}

async function testWizardFlow() {
  const fixture = createControllerFixture();
  const manifest = fixture.context.FlowStockScannerDiagnosticManifest;
  await fixture.controller.startSession();
  assert.strictEqual(fixture.controller.getSession().steps[0].stepId, "itf14");
  assert.strictEqual(fixture.controller.getSession().currentStepIndex, 0);
  assert.strictEqual(typeof fixture.getObserver(), "function");
  assert.strictEqual(typeof fixture.getScanHandler(), "function");

  await fixture.getScanHandler()({ value: manifest.steps[0].expectedValue, source: "keyboard" });
  assert.strictEqual(fixture.controller.getSession().steps[0].status, "PASS");
  assert.strictEqual(fixture.controller.getSession().currentStepIndex, 1);
  await fixture.getScanHandler()({ value: manifest.steps[1].expectedValue, source: "keyboard" });
  assert.strictEqual(fixture.controller.getSession().currentStepIndex, 2);
  await fixture.getScanHandler()({ value: manifest.steps[2].expectedValue, source: "keyboard" });

  assert.strictEqual(fixture.controller.getSession(), null);
  assert.strictEqual(fixture.storage.reports.length, 1);
  assert.strictEqual(fixture.storage.reports[0].schema, "flowstock-scanner-diagnostic-report/v1");
  assert.strictEqual(fixture.storage.reports[0].summary.overallStatus, "PASS");
  assert.strictEqual(fixture.getObserver(), null);
  assert.strictEqual(fixture.getScanHandler(), null);
}

async function testDiagnosticsStepPreferredScanTarget() {
  const fixture = createControllerFixture();
  await fixture.controller.startSession();

  const input = fixture.context.document.getElementById("scannerDiagScanInput");
  assert(input, "diagnostics step should mount hidden scan input");
  assert(fixture.app.innerHTML.includes('id="scannerDiagScanInput"'));
  assert.strictEqual(input.getAttribute("data-scan-allow"), "1");
  assert(input.className.split(/\s+/).includes("form-input"));
  assert(input.className.split(/\s+/).includes("filling-scan-input"));
  assert(input.className.split(/\s+/).includes("tsd-scan-input-hidden"));
  assert.strictEqual(input.getAttribute("type"), "text");
  assert.strictEqual(input.getAttribute("autocomplete"), "off");
  assert.strictEqual(input.getAttribute("autocapitalize"), "off");
  assert.strictEqual(input.getAttribute("autocorrect"), "off");
  assert.strictEqual(input.getAttribute("spellcheck"), "false");

  const events = fixture.getRenderEvents();
  const preferredIndex = events.findIndex((event) => event.type === "preferred-target" && event.target === input);
  const finishIndex = events.findIndex((event) => event.type === "finish-render");
  assert(preferredIndex >= 0, "diagnostics should assign preferred scan target");
  assert(finishIndex >= 0, "diagnostics should finish route render");
  assert(preferredIndex < finishIndex, "preferred target must be assigned before finishRouteRender");
  assert.strictEqual(fixture.getPreferredTargets().slice(-1)[0], input);

  const draft = JSON.parse(JSON.stringify(fixture.controller.getSession()));
  await fixture.controller.restoreDraft(draft);
  const restoredInput = fixture.context.document.getElementById("scannerDiagScanInput");
  assert(restoredInput);
  assert.notStrictEqual(restoredInput, input, "restoreDraft should mount a fresh scan input");
  assert.strictEqual(input.isConnected, false, "old scan input should be detached after rerender");
  assert.strictEqual(fixture.getPreferredTargets().slice(-1)[0], restoredInput);

  fixture.controller.cleanupRoute();
  assert.strictEqual(fixture.getPreferredTargets().slice(-1)[0], null);
}

async function testDiagnosticsRouteCleanupClearsPreferredTargetOnHistoryReportAndStart() {
  const fixture = createControllerFixture();
  await fixture.controller.startSession();
  assert(fixture.getPreferredTargets().slice(-1)[0]);

  fixture.controller.render({ name: "scannerDiagnosticsHistory" });
  assert.strictEqual(fixture.getPreferredTargets().slice(-1)[0], null, "history route should clear step scan target");

  await fixture.controller.startSession();
  const report = fixture.controller.buildReport(true);
  fixture.storage.reports.push(report);
  fixture.controller.render({ name: "scannerDiagnosticsReport", id: report.sessionId });
  assert.strictEqual(fixture.getPreferredTargets().slice(-1)[0], null, "report route should clear step scan target");

  await fixture.controller.startSession();
  fixture.controller.cleanupRoute();
  fixture.controller.setSession(null);
  fixture.storage.draft = null;
  fixture.controller.render({ name: "scannerDiagnostics" });
  await flushMicrotasks();
  assert.strictEqual(fixture.getPreferredTargets().slice(-1)[0], null, "start screen should not keep stale scan target");
}

async function testWizardRejectRetryAbortAndRestore() {
  const fixture = createControllerFixture();
  const manifest = fixture.context.FlowStockScannerDiagnosticManifest;
  await fixture.controller.startSession();

  await fixture.controller.handleScan({ value: manifest.steps[2].expectedValue, source: "keyboard" });
  assert.strictEqual(fixture.controller.getSession().currentStepIndex, 0);
  assert.strictEqual(fixture.controller.getSession().steps[0].attempts[0].kind, "wrong-step");
  assert(fixture.app.innerHTML.includes("Получен код шага 3"));

  await fixture.controller.handleScan({ value: "046012345678", source: "keyboard" });
  assert.strictEqual(fixture.controller.getSession().steps[0].attempts.slice(-1)[0].kind, "damaged");
  assert(fixture.app.innerHTML.includes("Первое расхождение"));

  await fixture.controller.handleScan({ value: "NOT-A-FLOWSTOCK-DIAGNOSTIC-CODE", source: "keyboard" });
  assert.strictEqual(fixture.controller.getSession().steps[0].attempts.slice(-1)[0].kind, "unknown");
  assert(fixture.app.innerHTML.includes("не относится к комплекту"));

  await fixture.controller.retryStep();
  assert.strictEqual(fixture.controller.getSession().steps[0].attempts.length, 0);

  const draft = JSON.parse(JSON.stringify(fixture.controller.getSession()));
  const restoreFixture = createControllerFixture();
  await restoreFixture.controller.restoreDraft(draft);
  assert.strictEqual(restoreFixture.controller.getSession().sessionId, draft.sessionId);
  assert(restoreFixture.app.innerHTML.includes("Незавершённая диагностика восстановлена"));

  await restoreFixture.controller.abortSession();
  assert.strictEqual(restoreFixture.storage.reports.length, 1);
  assert.strictEqual(restoreFixture.storage.reports[0].summary.overallStatus, "NOT_COMPLETED");
}

async function testKeyboardTailEventsAreSavedBeforeDraftAndReport() {
  const fixture = createControllerFixture();
  const manifest = fixture.context.FlowStockScannerDiagnosticManifest;
  await fixture.controller.startSession();

  let saveDuringHandler = 0;
  const originalSave = fixture.storage.saveActiveSession.bind(fixture.storage);
  fixture.storage.saveActiveSession = (session) => {
    if (saveDuringHandler) {
      throw new Error("saveActiveSession ran inside scanner handler call stack");
    }
    return originalSave(session);
  };

  const scanner = createScannerContext();
  const manager = scanner.context.FlowStockScanner.createScannerManager({
    canScan: () => true,
    inputDelayMs: 1000,
    keyDelayMs: 1000,
  });
  let pendingScanPromise = null;
  manager.setDiagnosticObserver(fixture.getObserver());
  manager.setHandler((scan) => {
    saveDuringHandler += 1;
    pendingScanPromise = fixture.getScanHandler()(scan);
    saveDuringHandler -= 1;
  });
  manager.start();

  dispatchKeyboardScan(scanner.document, manifest.steps[0].expectedValue);
  await pendingScanPromise;
  assert(
    fixture.storage.draft.steps[0].pipelineEvents.some((event) => event.type === "flush-completed"),
    "intermediate keyboard draft should include real flush-completed"
  );

  dispatchKeyboardScan(scanner.document, manifest.steps[1].expectedValue);
  await pendingScanPromise;
  dispatchKeyboardScan(scanner.document, manifest.steps[2].expectedValue);
  await pendingScanPromise;

  assert.strictEqual(fixture.storage.reports.length, 1);
  const report = fixture.storage.reports[0];
  assert(
    report.steps[2].pipelineEvents.some((event) => event.type === "flush-completed"),
    "final keyboard QR report should include real flush-completed"
  );
  assert(
    report.steps[2].pipelineEvents.some((event) => event.type === "handler-dispatched") &&
      report.steps[2].pipelineEvents.findIndex((event) => event.type === "handler-dispatched") <
        report.steps[2].pipelineEvents.findIndex((event) => event.type === "flush-completed"),
    "keyboard flush-completed should be recorded after handler-dispatched"
  );
}

async function testIntentTailEventsAreProviderSpecificAndDeferred() {
  const fixture = createControllerFixture();
  const manifest = fixture.context.FlowStockScannerDiagnosticManifest;
  await fixture.controller.startSession();

  let saveInsideHandler = false;
  const originalSave = fixture.storage.saveActiveSession.bind(fixture.storage);
  fixture.storage.saveActiveSession = (session) => {
    assert.strictEqual(saveInsideHandler, false, "intent saveActiveSession must be deferred");
    return originalSave(session);
  };

  let intentCallback = null;
  const intent = createScannerContext({
    bridge: {
      subscribeScans(callback) {
        intentCallback = callback;
        return () => {};
      },
    },
  });
  const manager = intent.context.FlowStockScanner.createScannerManager({
    mode: "intent",
    canScan: () => true,
  });
  let pendingScanPromise = null;
  manager.setDiagnosticObserver(fixture.getObserver());
  manager.setHandler((scan) => {
    saveInsideHandler = true;
    pendingScanPromise = fixture.getScanHandler()(scan);
    saveInsideHandler = false;
  });
  manager.start();

  intentCallback({ value: manifest.steps[0].expectedValue, symbology: "ITF14" });
  await pendingScanPromise;
  assert(fixture.storage.draft.steps[0].rawEvents.some((event) => event.type === "raw-input"));
  assert(fixture.storage.draft.steps[0].pipelineEvents.some((event) => event.type === "dedupe-accepted"));
  assert(fixture.storage.draft.steps[0].pipelineEvents.some((event) => event.type === "scan-emitted"));
  assert(fixture.storage.draft.steps[0].pipelineEvents.some((event) => event.type === "manager-received"));
  assert(fixture.storage.draft.steps[0].pipelineEvents.some((event) => event.type === "handler-dispatched"));
  assert(
    !fixture.storage.draft.steps[0].pipelineEvents.some((event) => event.type === "flush-completed"),
    "intent draft must not contain fake flush-completed"
  );

  intentCallback({ value: manifest.steps[1].expectedValue, symbology: "DATA_MATRIX" });
  await pendingScanPromise;
  intentCallback({ value: manifest.steps[2].expectedValue, symbology: "QR_CODE" });
  await pendingScanPromise;
  const report = fixture.storage.reports[0];
  assert(report.steps[2].pipelineEvents.some((event) => event.type === "handler-dispatched"));
  assert(
    !report.steps[2].pipelineEvents.some((event) => event.type === "flush-completed"),
    "intent report must not contain fake flush-completed"
  );
}

async function testAttemptCorrelationAndDuplicateDispatch() {
  const fixture = createControllerFixture();
  const manifest = fixture.context.FlowStockScannerDiagnosticManifest;
  await fixture.controller.startSession();
  const observer = fixture.getObserver();
  const firstAttempt = "keyboard-physical-1";
  const firstScan = "keyboard-physical-1-scan";

  observer({
    type: "raw-keydown",
    timestamp: 1000,
    detail: { attemptId: firstAttempt, key: "0" },
  });
  observer({
    type: "handler-dispatched",
    timestamp: 1010,
    detail: { attemptId: firstAttempt, scanId: firstScan, value: manifest.steps[0].expectedValue },
  });
  await fixture.getScanHandler()({
    value: manifest.steps[0].expectedValue,
    source: "keyboard",
    attemptId: firstAttempt,
    scanId: firstScan,
  });
  assert.strictEqual(fixture.controller.getSession().currentStepIndex, 1);

  observer({
    type: "flush-completed",
    timestamp: 1020,
    detail: {
      attemptId: firstAttempt,
      scanId: firstScan,
      value: manifest.steps[0].expectedValue,
      terminator: "Enter",
    },
  });
  const duplicateAttempt = "keyboard-physical-2";
  const duplicateScan = "keyboard-physical-2-scan";
  observer({
    type: "dedupe-rejected",
    timestamp: 1030,
    detail: {
      attemptId: duplicateAttempt,
      scanId: duplicateScan,
      duplicateOfScanId: firstScan,
      dedupeAccepted: false,
      value: manifest.steps[0].expectedValue,
    },
  });

  const session = fixture.controller.getSession();
  assert(session.steps[0].pipelineEvents.some((event) => event.type === "flush-completed"));
  assert.strictEqual(session.steps[0].duplicates, 1);
  assert.strictEqual(session.steps[0].status, "WARNING");
  assert.strictEqual(session.steps[1].warnings.length, 0, "late ITF events must not warn Data Matrix step");
  assert.strictEqual(session.steps[1].attempts.length, 0, "duplicate ITF dispatch must not become wrong-step");
  assert.strictEqual(
    session.steps[0].pipelineEvents.find((event) => event.type === "dedupe-rejected").detail.duplicateOfScanId,
    firstScan
  );
  assert.strictEqual(
    fixture.context.FlowStockScannerDiagnostics._test.summarizeSteps(session.steps).duplicateDispatchCount,
    0,
    "physical duplicate must not increase duplicateDispatchCount"
  );

  observer({
    type: "handler-dispatched",
    timestamp: 1040,
    detail: { attemptId: firstAttempt, scanId: firstScan, value: manifest.steps[0].expectedValue },
  });
  assert.strictEqual(session.steps[0].dispatchCount, 2);
  assert.strictEqual(session.steps[0].status, "FAIL");
  assert(session.steps[0].errors.some((message) => message.includes("дважды")));
  assert.strictEqual(session.steps[1].attempts.length, 0);
  assert.strictEqual(
    fixture.context.FlowStockScannerDiagnostics._test.summarizeSteps(session.steps).duplicateDispatchCount,
    1
  );
}

async function testDuplicateDispatchAttemptDoesNotSkipStep() {
  const fixture = createControllerFixture();
  const manifest = fixture.context.FlowStockScannerDiagnosticManifest;
  await fixture.controller.startSession();
  const attemptId = "duplicate-dispatch-active";
  const scanId = "duplicate-dispatch-active-scan";

  fixture.getObserver()({
    type: "handler-dispatched",
    timestamp: 3000,
    detail: { attemptId, scanId, value: manifest.steps[0].expectedValue },
  });
  const firstPromise = fixture.getScanHandler()({
    value: manifest.steps[0].expectedValue,
    source: "keyboard",
    attemptId,
    scanId,
  });
  fixture.getObserver()({
    type: "handler-dispatched",
    timestamp: 3001,
    detail: { attemptId, scanId, value: manifest.steps[0].expectedValue },
  });
  const secondPromise = fixture.getScanHandler()({
    value: manifest.steps[0].expectedValue,
    source: "keyboard",
    attemptId,
    scanId,
  });

  assert.strictEqual(secondPromise, firstPromise, "active duplicate attempt should reuse first post-scan promise");
  await Promise.all([firstPromise, secondPromise]);
  const session = fixture.controller.getSession();
  assert.strictEqual(session.currentStepIndex, 1, "duplicate dispatch must not skip Data Matrix");
  assert.strictEqual(session.steps[0].status, "FAIL");
  assert.strictEqual(session.steps[1].status, "NOT_COMPLETED");
  assert.strictEqual(session.steps[2].status, "NOT_COMPLETED");
}

async function testLateDuplicateDispatchAttemptPersistsFailWithoutStepChange() {
  const fixture = createControllerFixture();
  const manifest = fixture.context.FlowStockScannerDiagnosticManifest;
  await fixture.controller.startSession();
  const attemptId = "duplicate-dispatch-late";
  const scanId = "duplicate-dispatch-late-scan";

  fixture.getObserver()({
    type: "handler-dispatched",
    timestamp: 3100,
    detail: { attemptId, scanId, value: manifest.steps[0].expectedValue },
  });
  await fixture.getScanHandler()({
    value: manifest.steps[0].expectedValue,
    source: "keyboard",
    attemptId,
    scanId,
  });
  assert.strictEqual(fixture.controller.getSession().currentStepIndex, 1);
  assert.strictEqual(fixture.storage.draft.steps[0].status, "PASS");

  fixture.getObserver()({
    type: "handler-dispatched",
    timestamp: 3101,
    detail: { attemptId, scanId, value: manifest.steps[0].expectedValue },
  });
  await fixture.getScanHandler()({
    value: manifest.steps[0].expectedValue,
    source: "keyboard",
    attemptId,
    scanId,
  });
  await flushMicrotasks();

  const session = fixture.controller.getSession();
  assert.strictEqual(session.currentStepIndex, 1, "late duplicate attempt must not advance again");
  assert.strictEqual(session.steps[0].status, "FAIL");
  assert.strictEqual(fixture.storage.reports.length, 0);
  assert.strictEqual(fixture.storage.draft.steps[0].status, "FAIL", "late duplicate FAIL must be persisted");

  const restoreFixture = createControllerFixture();
  await restoreFixture.controller.restoreDraft(fixture.storage.draft);
  assert.strictEqual(restoreFixture.controller.getSession().steps[0].status, "FAIL");
  assert.strictEqual(restoreFixture.controller.getSession().currentStepIndex, 1);
}

async function testOrderedDraftPersistenceKeepsLatestDuplicateFail() {
  const fixture = createControllerFixture();
  const manifest = fixture.context.FlowStockScannerDiagnosticManifest;
  await fixture.controller.startSession();
  const attemptId = "ordered-draft-attempt";
  const scanId = "ordered-draft-scan";
  const saves = [];

  fixture.storage.saveActiveSession = (session) => {
    const deferred = createDeferred();
    const snapshot = JSON.parse(JSON.stringify(session));
    saves.push({ deferred, snapshot });
    return deferred.promise.then(() => {
      fixture.storage.draft = snapshot;
      return snapshot;
    });
  };

  fixture.getObserver()({
    type: "handler-dispatched",
    timestamp: 3200,
    detail: { attemptId, scanId, value: manifest.steps[0].expectedValue },
  });
  const scanPromise = fixture.getScanHandler()({
    value: manifest.steps[0].expectedValue,
    source: "keyboard",
    attemptId,
    scanId,
  });
  await flushMicrotasks();
  assert.strictEqual(saves.length, 1);
  assert.strictEqual(saves[0].snapshot.steps[0].status, "PASS");

  fixture.getObserver()({
    type: "handler-dispatched",
    timestamp: 3201,
    detail: { attemptId, scanId, value: manifest.steps[0].expectedValue },
  });
  await flushMicrotasks();
  assert.strictEqual(saves.length, 1, "coalesced save must wait for older queued save");

  saves[0].deferred.resolve();
  await scanPromise;
  await flushMicrotasks();
  assert.strictEqual(saves.length, 2);
  assert.strictEqual(saves[1].snapshot.steps[0].status, "FAIL");
  saves[1].deferred.resolve();
  await flushMicrotasks();

  assert.strictEqual(fixture.storage.draft.steps[0].status, "FAIL");
  const restoreFixture = createControllerFixture();
  await restoreFixture.controller.restoreDraft(fixture.storage.draft);
  assert.strictEqual(restoreFixture.controller.getSession().steps[0].status, "FAIL");
}

async function testTabTerminatorReportMeasurement() {
  const keyboard = createScannerContext();
  const manager = keyboard.context.FlowStockScanner.createScannerManager({
    canScan: () => true,
    inputDelayMs: 100,
    keyDelayMs: 100,
  });
  const scans = [];
  const events = [];
  manager.setHandler((scan) => scans.push(scan));
  manager.setDiagnosticObserver((event) => events.push(event));
  manager.start();

  "TAB".split("").forEach((key) => keyboard.document.dispatch("keydown", { key }));
  const tabEvent = keyboard.document.dispatch("keydown", { key: "Tab" });
  assert.strictEqual(scans.length, 0, "Tab should not flush immediately");
  assert.notStrictEqual(tabEvent.defaultPrevented, true, "Tab should keep normal browser behavior");
  keyboard.runTimers();
  assert.strictEqual(scans.length, 1);
  assert.strictEqual(scans[0].value, "TAB");
  assert.strictEqual(scans[0].raw.terminator, "Tab");
  assert(
    events.some((event) => event.type === "scan-emitted" && event.detail.terminator === "Tab"),
    "scan-emitted should carry observed Tab terminator"
  );

  const fixture = createControllerFixture();
  const manifest = fixture.context.FlowStockScannerDiagnosticManifest;
  await fixture.controller.startSession();
  fixture.getObserver()({
    type: "scan-emitted",
    timestamp: 2000,
    detail: {
      attemptId: "keyboard-tab-1",
      scanId: "keyboard-tab-1-scan",
      value: manifest.steps[0].expectedValue,
      normalizedValue: manifest.steps[0].expectedValue,
      flushReason: "keydown-timeout",
      terminator: "Tab",
      inputDurationMs: 100,
    },
  });
  await fixture.getScanHandler()({
    value: manifest.steps[0].expectedValue,
    source: "keyboard",
    attemptId: "keyboard-tab-1",
    scanId: "keyboard-tab-1-scan",
  });
  const report = fixture.controller.buildReport(false);
  assert.strictEqual(report.steps[0].terminator, "Tab");
  assert.strictEqual(report.summary.dominantTerminator, "Tab");
}

async function testScannerObserverOverheadGuard() {
  assert(
    scannerJs.includes('var lifecycleObserver = getLifecycleObserver();') &&
      scannerJs.includes('if (typeof observer !== "function" && typeof lifecycleObserver !== "function")') &&
      scannerJs.includes("var detail = eventTelemetry(event, extra);"),
    "raw event telemetry should be guarded by diagnostic/lifecycle observer presence"
  );

  const keyboard = createScannerContext();
  const manager = keyboard.context.FlowStockScanner.createScannerManager({
    canScan: () => true,
    inputDelayMs: 10,
    keyDelayMs: 10,
  });
  const scans = [];
  manager.setHandler((scan) => scans.push(scan));
  manager.start();
  "NOOBS".split("").forEach((key) => keyboard.document.dispatch("keydown", { key }));
  keyboard.document.dispatch("keydown", { key: "Enter" });
  assert.strictEqual(scans.length, 1);

  const events = [];
  manager.setDiagnosticObserver((event) => events.push(event));
  "OBS".split("").forEach((key) => keyboard.document.dispatch("keydown", { key }));
  keyboard.document.dispatch("keydown", { key: "Enter" });
  assert(events.length > 0, "observer should receive future diagnostics only after being installed");
  assert(!events.some((event) => event.detail && event.detail.value === "NOOBS"));
}

async function testAsyncRouteTokenAndInvalidDraft() {
  let resolveDraft;
  let observer = null;
  let scanHandler = null;
  const app = { innerHTML: "" };
  const delayedDraft = new Promise((resolve) => {
    resolveDraft = resolve;
  });
  const context = loadDiagnosticsContext();
  const deps = {
    app,
    diagnosticStore: {
      loadActiveSession() {
        return delayedDraft;
      },
      clearActiveSession() {
        return Promise.resolve(true);
      },
    },
    scannerManager: {
      setDiagnosticObserver(handler) {
        observer = handler;
      },
      clearDiagnosticObserver() {
        observer = null;
      },
    },
    setScanHandler(handler) {
      scanHandler = handler;
    },
    finishRouteRender() {},
    currentRoute: { name: "scannerDiagnostics" },
  };
  const controller = context.FlowStockScannerDiagnostics._test.createController(deps);
  controller.render({ name: "scannerDiagnostics" });
  const loadingHtml = app.innerHTML;
  controller.cleanupRoute();
  resolveDraft(context.FlowStockScannerDiagnostics._test.createSession(context.FlowStockScannerDiagnosticManifest));
  await delayedDraft;
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.strictEqual(app.innerHTML, loadingHtml, "stale loadDraft must not render after cleanup");
  assert.strictEqual(observer, null);
  assert.strictEqual(scanHandler, null);

  let cleared = false;
  const corruptFixture = createControllerFixture();
  corruptFixture.deps.diagnosticStore = {
    loadActiveSession() {
      return Promise.resolve({ schema: "broken", sessionId: "bad" });
    },
    clearActiveSession() {
      cleared = true;
      return Promise.resolve(true);
    },
  };
  corruptFixture.controller.render({ name: "scannerDiagnostics" });
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.strictEqual(cleared, true);
  assert.strictEqual(corruptFixture.getObserver(), null);
  assert.strictEqual(corruptFixture.getScanHandler(), null);
  assert(corruptFixture.app.innerHTML.includes("повреждена"));
}

async function testDelayedSaveDoesNotRenderAfterRouteCleanup() {
  async function assertDelayedSave(operationName, runOperation) {
    const fixture = createControllerFixture();
    const deferred = createDeferred();
    let delayed = false;
    const originalSave = fixture.storage.saveActiveSession.bind(fixture.storage);

    if (operationName !== "start") {
      await fixture.controller.startSession();
    }
    fixture.app.innerHTML = "OTHER_ROUTE";
    fixture.storage.saveActiveSession = (session) => {
      if (!delayed) {
        return originalSave(session);
      }
      return deferred.promise.then(() => originalSave(session));
    };

    delayed = true;
    const promise = runOperation(fixture);
    await Promise.resolve();
    fixture.controller.cleanupRoute();
    fixture.app.innerHTML = "OTHER_ROUTE";
    deferred.resolve();
    await promise;
    await flushMicrotasks();
    assert.strictEqual(fixture.app.innerHTML, "OTHER_ROUTE", operationName + " must not render after cleanup");
    assert.strictEqual(fixture.getObserver(), null, operationName + " must not reattach observer");
    assert.strictEqual(fixture.getScanHandler(), null, operationName + " must not reattach scan handler");
  }

  await assertDelayedSave("start", (fixture) => fixture.controller.startSession());
  await assertDelayedSave("retry", (fixture) => fixture.controller.retryStep());
  await assertDelayedSave("accepted-scan", (fixture) =>
    fixture.controller.handleScan({
      value: fixture.context.FlowStockScannerDiagnosticManifest.steps[0].expectedValue,
      source: "keyboard",
      attemptId: "accepted-delayed",
      scanId: "accepted-delayed-scan",
    })
  );
  await assertDelayedSave("rejected-scan", (fixture) =>
    fixture.controller.handleScan({
      value: "NOT-A-DIAGNOSTIC-CODE",
      source: "keyboard",
      attemptId: "rejected-delayed",
      scanId: "rejected-delayed-scan",
    })
  );

  const finishFixture = createControllerFixture();
  await finishFixture.controller.startSession();
  finishFixture.app.innerHTML = "OTHER_ROUTE";
  const clearDeferred = createDeferred();
  finishFixture.storage.clearActiveSession = () =>
    clearDeferred.promise.then(() => {
      finishFixture.storage.draft = null;
      return true;
    });
  const finishPromise = finishFixture.controller.abortSession();
  await flushMicrotasks();
  finishFixture.controller.cleanupRoute();
  finishFixture.app.innerHTML = "OTHER_ROUTE";
  clearDeferred.resolve();
  await finishPromise;
  await flushMicrotasks();
  assert.strictEqual(finishFixture.app.innerHTML, "OTHER_ROUTE", "finishSession must not render after cleanup");
  assert.strictEqual(finishFixture.controller.getSession(), null, "finished session must be cleared after persistence");
  assert.strictEqual(finishFixture.getObserver(), null);
  assert.strictEqual(finishFixture.getScanHandler(), null);
  finishFixture.controller.render({ name: "scannerDiagnostics" });
  await flushMicrotasks();
  assert(finishFixture.app.innerHTML.includes("Начать диагностику"));
}

async function testBusinessIsolation() {
  const fixture = createControllerFixture();
  let businessCalls = 0;
  fixture.storage.apiScanProductionPallet = () => {
    businessCalls += 1;
  };
  fixture.storage.apiFillProductionPallet = () => {
    businessCalls += 1;
  };
  fixture.storage.apiScanOutboundPickingHu = () => {
    businessCalls += 1;
  };
  fixture.storage.apiResolveHu = () => {
    businessCalls += 1;
  };
  fixture.storage.apiScanWarehouseTask = () => {
    businessCalls += 1;
  };
  await fixture.controller.startSession();
  await fixture.controller.handleScan({
    value: fixture.context.FlowStockScannerDiagnosticManifest.steps[0].expectedValue,
    source: "keyboard",
  });
  assert.strictEqual(businessCalls, 0, "diagnostic scan must not call business APIs");
  fixture.controller.cleanupRoute();
  assert.strictEqual(fixture.getScanHandler(), null);
  fixture.deps.setScanHandler(() => "business");
  assert.strictEqual(typeof fixture.getScanHandler(), "function");
}

async function testScannerObserverTransports() {
  const keyboard = createScannerContext();
  const scanSink = keyboard.document.register(createFakeElement("input", "scanSink"));
  scanSink.setAttribute("data-scan-allow", "1");
  const manager = keyboard.context.FlowStockScanner.createScannerManager({
    scanSink,
    canScan: () => true,
    inputDelayMs: 1000,
    keyDelayMs: 1000,
  });
  const events = [];
  const scans = [];
  manager.setDiagnosticObserver((event) => events.push(event));
  manager.setHandler((scan) => scans.push(scan));
  manager.start();
  "ABC".split("").forEach((key) => keyboard.document.dispatch("keydown", { key }));
  keyboard.document.dispatch("keydown", { key: "Enter" });
  assert.strictEqual(scans[0].value, "ABC");
  assert(events.some((event) => event.type === "raw-keydown"));
  assert(events.some((event) => event.type === "buffer-append"));
  assert(events.some((event) => event.type === "scan-emitted"));
  assert(events.some((event) => event.type === "handler-dispatched"));

  const beforeTabScans = scans.length;
  "XY".split("").forEach((key) => keyboard.document.dispatch("keydown", { key }));
  keyboard.document.dispatch("keydown", { key: "Tab" });
  assert.strictEqual(scans.length, beforeTabScans, "Tab should be observed without changing normal flush behavior");
  assert(events.some((event) => event.type === "raw-keydown" && event.detail.tabReceived === true));

  scanSink.value = "BULK";
  scanSink.dispatch("input", { target: scanSink });
  keyboard.runTimers();
  assert(scans.some((scan) => scan.value === "BULK"));

  const imeTarget = keyboard.document.register(createFakeElement("input", "imeTarget"));
  imeTarget.setAttribute("data-scan-allow", "1");
  imeTarget.value = "IME";
  keyboard.document.dispatch("keydown", {
    key: "Unidentified",
    keyCode: 229,
    which: 229,
    target: imeTarget,
  });
  keyboard.runTimers();
  assert(scans.some((scan) => scan.value === "IME"));

  let intentCallback = null;
  const intent = createScannerContext({
    bridge: {
      subscribeScans(callback) {
        intentCallback = callback;
        return () => {};
      },
    },
  });
  const intentManager = intent.context.FlowStockScanner.createScannerManager({
    mode: "intent",
    canScan: () => true,
  });
  const intentEvents = [];
  const intentScans = [];
  intentManager.setDiagnosticObserver((event) => intentEvents.push(event));
  intentManager.setHandler((scan) => intentScans.push(scan));
  intentManager.start();
  intentCallback("INTENT-STRING");
  intentCallback({ scanData: "INTENT-OBJECT", symbology: "QR_CODE" });
  assert(intentScans.some((scan) => scan.value === "INTENT-STRING"));
  assert(intentScans.some((scan) => scan.value === "INTENT-OBJECT" && scan.symbology === "QR_CODE"));
  assert(!intentEvents.some((event) => event.type === "flush-completed"));

  intentCallback({ value: "DUP", ts: 1000 });
  intentCallback({ value: "DUP", ts: 1050 });
  const duplicateEvent = intentEvents.find((event) => event.type === "dedupe-rejected");
  assert(duplicateEvent);
  assert(duplicateEvent.detail.scanId);
  assert(duplicateEvent.detail.duplicateOfScanId);
  assert.notStrictEqual(duplicateEvent.detail.scanId, duplicateEvent.detail.duplicateOfScanId);
  assert.strictEqual(duplicateEvent.detail.dedupeAccepted, false);

  intentManager.setHandler(null);
  intentCallback("NO-HANDLER");
  assert(intentEvents.some((event) => event.type === "handler-missing"));

  const noBridge = createScannerContext({
    bridge: {
      subscribeScans() {
        throw new Error("BRIDGE_SUBSCRIBE_FAILED");
      },
    },
  });
  const noBridgeManager = noBridge.context.FlowStockScanner.createScannerManager({
    mode: "intent",
    canScan: () => true,
  });
  const noBridgeEvents = [];
  noBridgeManager.setDiagnosticObserver((event) => noBridgeEvents.push(event));
  noBridgeManager.start();
  assert(noBridgeEvents.some((event) => event.type === "scanner-error"));
}

async function testAndroidImeEnterCancelsStaleTargetRead() {
  const keyboard = createScannerContext();
  const imeTarget = createScanAllowedInput(keyboard.document, "imeEnterTarget");
  const manager = keyboard.context.FlowStockScanner.createScannerManager({
    canScan: () => true,
    inputDelayMs: 1000,
    keyDelayMs: 1000,
  });
  const events = [];
  const scans = [];
  manager.setDiagnosticObserver((event) => events.push(event));
  manager.setHandler((scan) => scans.push(scan));
  manager.start();

  keyboard.document.dispatch("keydown", {
    key: "Unidentified",
    keyCode: 229,
    which: 229,
    target: imeTarget,
  });
  imeTarget.value = "IME-ENTER-PAYLOAD";
  keyboard.document.dispatch("beforeinput", {
    target: imeTarget,
    data: "IME-ENTER-PAYLOAD",
    inputType: "insertText",
  });
  keyboard.document.dispatch("input", { target: imeTarget });
  keyboard.document.dispatch("keydown", { key: "Enter", target: imeTarget });

  assert.strictEqual(scans.length, 1);
  assert.strictEqual(scans[0].value, "IME-ENTER-PAYLOAD");
  assert.strictEqual(scans[0].raw.reason, "enter");
  assert.strictEqual(countEvents(events, "dedupe-accepted"), 1);
  assert.strictEqual(countEvents(events, "dedupe-rejected"), 0);
  assert.strictEqual(countEvents(events, "scan-emitted"), 1);
  assert.strictEqual(countEvents(events, "manager-received"), 1);
  assert.strictEqual(countEvents(events, "handler-dispatched"), 1);
  assert.strictEqual(countEvents(events, "buffer-replace"), 1);
  assert.strictEqual(countEvents(events, "flush-requested"), 0);
  assert.strictEqual(countEvents(events, "flush-completed"), 0);

  const accepted = eventsOfType(events, "dedupe-accepted")[0].detail;
  const emitted = eventsOfType(events, "scan-emitted")[0].detail;
  assert.strictEqual(accepted.attemptId, emitted.attemptId);
  assert.strictEqual(accepted.scanId, emitted.scanId);
  assert.strictEqual(scans[0].attemptId, emitted.attemptId);
  assert.strictEqual(scans[0].scanId, emitted.scanId);

  const eventCountBeforeStaleCallbacks = events.length;
  keyboard.timers.slice().forEach((timer) => timer.fn());
  assert.strictEqual(events.length, eventCountBeforeStaleCallbacks, "stale target-read callbacks must be no-op");
  assert.strictEqual(scans.length, 1);
}

async function testAndroidImeTimeoutWithoutEnterStillScans() {
  const keyboard = createScannerContext();
  const imeTarget = createScanAllowedInput(keyboard.document, "imeTimeoutTarget");
  const manager = keyboard.context.FlowStockScanner.createScannerManager({
    canScan: () => true,
    inputDelayMs: 1000,
    keyDelayMs: 1000,
  });
  const events = [];
  const scans = [];
  manager.setDiagnosticObserver((event) => events.push(event));
  manager.setHandler((scan) => scans.push(scan));
  manager.start();

  imeTarget.value = "IME-TIMEOUT-PAYLOAD";
  keyboard.document.dispatch("keydown", {
    key: "Unidentified",
    keyCode: 229,
    which: 229,
    target: imeTarget,
  });
  keyboard.document.dispatch("beforeinput", {
    target: imeTarget,
    data: "IME-TIMEOUT-PAYLOAD",
    inputType: "insertText",
  });
  keyboard.document.dispatch("input", { target: imeTarget });
  keyboard.runTimers();

  assert.strictEqual(scans.length, 1);
  assert.strictEqual(scans[0].value, "IME-TIMEOUT-PAYLOAD");
  assert.strictEqual(scans[0].raw.reason, "ime");
  assert.strictEqual(scans[0].raw.flushReason, "ime");
  assert.strictEqual(countEvents(events, "dedupe-accepted"), 1);
  assert.strictEqual(countEvents(events, "dedupe-rejected"), 0);
  assert.strictEqual(countEvents(events, "handler-dispatched"), 1);
}

async function testKeyboardWedgeCharsEnterStillScansOnce() {
  const keyboard = createScannerContext();
  const manager = keyboard.context.FlowStockScanner.createScannerManager({
    canScan: () => true,
    inputDelayMs: 1000,
    keyDelayMs: 1000,
  });
  const events = [];
  const scans = [];
  manager.setDiagnosticObserver((event) => events.push(event));
  manager.setHandler((scan) => scans.push(scan));
  manager.start();

  dispatchKeyboardScan(keyboard.document, "WEDGE-ENTER");

  assert.strictEqual(scans.length, 1);
  assert.strictEqual(scans[0].value, "WEDGE-ENTER");
  assert.strictEqual(scans[0].raw.terminator, "Enter");
  assert.strictEqual(countEvents(events, "dedupe-accepted"), 1);
  assert.strictEqual(countEvents(events, "dedupe-rejected"), 0);
  assert.strictEqual(countEvents(events, "scan-emitted"), 1);
  assert.strictEqual(countEvents(events, "handler-dispatched"), 1);
}

async function testAndroidImeEnterPhysicalDuplicateKeepsDedupeSemantics() {
  const keyboard = createScannerContext();
  const imeTarget = createScanAllowedInput(keyboard.document, "imeDuplicateTarget");
  const manager = keyboard.context.FlowStockScanner.createScannerManager({
    canScan: () => true,
    inputDelayMs: 1000,
    keyDelayMs: 1000,
  });
  const events = [];
  const scans = [];
  manager.setDiagnosticObserver((event) => events.push(event));
  manager.setHandler((scan) => scans.push(scan));
  manager.start();

  function dispatchImeEnter(value) {
    keyboard.document.dispatch("keydown", {
      key: "Unidentified",
      keyCode: 229,
      which: 229,
      target: imeTarget,
    });
    imeTarget.value = value;
    keyboard.document.dispatch("beforeinput", {
      target: imeTarget,
      data: value,
      inputType: "insertText",
    });
    keyboard.document.dispatch("input", { target: imeTarget });
    keyboard.document.dispatch("keydown", { key: "Enter", target: imeTarget });
    keyboard.timers.slice().forEach((timer) => timer.fn());
  }

  dispatchImeEnter("IME-DUPLICATE");
  dispatchImeEnter("IME-DUPLICATE");

  assert.strictEqual(scans.length, 1, "dedupe-rejected physical duplicate must not reach handler");
  assert.strictEqual(countEvents(events, "dedupe-accepted"), 1);
  assert.strictEqual(countEvents(events, "dedupe-rejected"), 1);
  assert.strictEqual(countEvents(events, "scan-emitted"), 1);
  assert.strictEqual(countEvents(events, "handler-dispatched"), 1);

  const accepted = eventsOfType(events, "dedupe-accepted")[0].detail;
  const rejected = eventsOfType(events, "dedupe-rejected")[0].detail;
  assert(rejected.attemptId);
  assert(rejected.scanId);
  assert.notStrictEqual(rejected.attemptId, accepted.attemptId);
  assert.notStrictEqual(rejected.scanId, accepted.scanId);
  assert.strictEqual(rejected.duplicateOfScanId, accepted.scanId);
  assert.strictEqual(rejected.dedupeAccepted, false);
}

async function testKeyboardProviderStopCancelsPendingTargetRead() {
  const keyboard = createScannerContext();
  const imeTarget = createScanAllowedInput(keyboard.document, "imeStopTarget");
  const manager = keyboard.context.FlowStockScanner.createScannerManager({
    canScan: () => true,
    inputDelayMs: 1000,
    keyDelayMs: 1000,
  });
  const events = [];
  const scans = [];
  manager.setDiagnosticObserver((event) => events.push(event));
  manager.setHandler((scan) => scans.push(scan));
  manager.start();
  events.length = 0;

  imeTarget.value = "IME-STOP-PAYLOAD";
  keyboard.document.dispatch("keydown", {
    key: "Unidentified",
    keyCode: 229,
    which: 229,
    target: imeTarget,
  });
  const pendingCallbacks = keyboard.timers.slice();
  manager.stop();
  pendingCallbacks.forEach((timer) => timer.fn());

  assert.strictEqual(scans.length, 0);
  assert.strictEqual(countEvents(events, "buffer-replace"), 0);
  assert.strictEqual(countEvents(events, "dedupe-accepted"), 0);
  assert.strictEqual(countEvents(events, "dedupe-rejected"), 0);
  assert.strictEqual(countEvents(events, "scan-emitted"), 0);
  assert.strictEqual(countEvents(events, "manager-received"), 0);
  assert.strictEqual(countEvents(events, "handler-dispatched"), 0);
}

async function testIntentPayloadCloneIsObserverGated() {
  let callbackWithoutObserver = null;
  let cloneCounter = 0;
  const noObserver = createScannerContext({
    bridge: {
      subscribeScans(callback) {
        callbackWithoutObserver = callback;
        return () => {};
      },
    },
  });
  const noObserverManager = noObserver.context.FlowStockScanner.createScannerManager({
    mode: "intent",
    canScan: () => true,
  });
  const scans = [];
  noObserverManager.setHandler((scan) => scans.push(scan));
  noObserverManager.start();
  callbackWithoutObserver({
    value: "NO-OBSERVER-PAYLOAD",
    toJSON() {
      cloneCounter += 1;
      return { value: "NO-OBSERVER-PAYLOAD" };
    },
  });
  assert.strictEqual(scans[0].value, "NO-OBSERVER-PAYLOAD");
  assert.strictEqual(cloneCounter, 0, "intent payload should not be cloned without observer");

  let callbackWithObserver = null;
  const withObserver = createScannerContext({
    bridge: {
      subscribeScans(callback) {
        callbackWithObserver = callback;
        return () => {};
      },
    },
  });
  const withObserverManager = withObserver.context.FlowStockScanner.createScannerManager({
    mode: "intent",
    canScan: () => true,
  });
  const events = [];
  withObserverManager.setDiagnosticObserver((event) => events.push(event));
  withObserverManager.setHandler(() => {});
  withObserverManager.start();
  callbackWithObserver({
    value: "WITH-OBSERVER-PAYLOAD",
    toJSON() {
      cloneCounter += 1;
      return { value: "WITH-OBSERVER-PAYLOAD" };
    },
  });
  assert(cloneCounter > 0, "observer path should clone raw payload for telemetry");
  assert(events.some((event) => event.type === "raw-input" && event.detail.rawPayload.value === "WITH-OBSERVER-PAYLOAD"));
}

async function testStorageAndExport() {
  assert(storageJs.includes("var DB_NAME = \"tsd_app\""));
  assert(storageJs.includes("var DB_VERSION = 8"));
  assert(!/scannerDiagnostic/i.test(storageJs));
  assert(!/saveScannerDiagnostic|loadScannerDiagnostic|clearScannerDiagnostic/i.test(storageJs));

  const context = {
    console,
    indexedDB: createFakeIndexedDb(),
    navigator: { onLine: true },
    location: { origin: "https://flowstock.test" },
    alert() {},
    setTimeout,
    clearTimeout,
  };
  context.window = context;
  context.self = context;
  vm.createContext(context);
  vm.runInContext(diagnosticsStoreJs, context, { filename: "scanner-diagnostics-store.js" });
  assert.strictEqual(context.FlowStockScannerDiagnosticsStore._test.DB_NAME, "FlowStockScannerDiagnostics");
  assert.strictEqual(context.FlowStockScannerDiagnosticsStore._test.DB_VERSION, 1);

  for (let i = 0; i < 22; i += 1) {
    await context.FlowStockScannerDiagnosticsStore.saveReport({
      schema: "flowstock-scanner-diagnostic-report/v1",
      setId: "FS-SCANDIAG-V1",
      sessionId: "session-" + i,
      startedAt: new Date(2026, 5, 19, 19, i, 0).toISOString(),
      finishedAt: new Date(2026, 5, 19, 19, i, 1).toISOString(),
      scanner: { selectedProvider: "keyboard" },
      summary: { overallStatus: i % 2 ? "WARNING" : "PASS" },
      steps: [],
    });
  }
  const reports = await context.FlowStockScannerDiagnosticsStore.listReports();
  assert.strictEqual(reports.length, 20, "diagnostic history should be limited to last 20 reports");
  assert.strictEqual(reports[0].sessionId, "session-21");
  assert.strictEqual(reports[19].sessionId, "session-2");
  const loaded = await context.FlowStockScannerDiagnosticsStore.getReport("session-21");
  assert.strictEqual(loaded.summary.overallStatus, "WARNING");
  await context.FlowStockScannerDiagnosticsStore.deleteReport("session-21");
  assert.strictEqual(await context.FlowStockScannerDiagnosticsStore.getReport("session-21"), null);

  await context.FlowStockScannerDiagnosticsStore.saveActiveSession({
    schema: "flowstock-scanner-diagnostic-report/v1",
    sessionId: "draft",
  });
  assert.strictEqual((await context.FlowStockScannerDiagnosticsStore.loadActiveSession()).sessionId, "draft");
  await context.FlowStockScannerDiagnosticsStore.clearActiveSession();
  assert.strictEqual(await context.FlowStockScannerDiagnosticsStore.loadActiveSession(), null);

  const unavailableContext = loadDiagnosticsContext({ indexedDB: null });
  const unavailableFixture = createControllerFixture();
  unavailableFixture.deps.diagnosticStore = unavailableContext.FlowStockScannerDiagnosticsStore;
  await unavailableFixture.controller.startSession();
  const unavailableManifest = unavailableFixture.context.FlowStockScannerDiagnosticManifest;
  await unavailableFixture.getScanHandler()({ value: unavailableManifest.steps[0].expectedValue, source: "keyboard" });
  await unavailableFixture.getScanHandler()({ value: unavailableManifest.steps[1].expectedValue, source: "keyboard" });
  const unavailableReport = await unavailableFixture.getScanHandler()({
    value: unavailableManifest.steps[2].expectedValue,
    source: "keyboard",
  });
  assert.strictEqual(unavailableReport.summary.overallStatus, "PASS");
  assert.strictEqual(unavailableFixture.storage.reports.length, 0);
  assert(unavailableFixture.app.innerHTML.includes("Скачать JSON"));

  const diagnosticsContext = loadDiagnosticsContext();
  const report = {
    schema: "flowstock-scanner-diagnostic-report/v1",
    setId: "FS-SCANDIAG-V1",
    sessionId: "export",
    startedAt: "2026-06-19T19:15:00.000Z",
    finishedAt: "2026-06-19T19:15:00.000Z",
  };
  const fileName = diagnosticsContext.FlowStockScannerDiagnostics._test.getReportFileName(report);
  assert(fileName.startsWith("flowstock-scanner-diag_FS-SCANDIAG-V1_"));
  assert(fileName.endsWith(".json"));
  await diagnosticsContext.FlowStockScannerDiagnostics._test.copyReport(report);
  assert(diagnosticsContext.navigator.clipboard.writes[0].includes('"schema"'));
}

function testShellIntegration() {
  assert(indexHtml.includes("scanner-diagnostics-manifest.js"));
  assert(indexHtml.indexOf("scanner-diagnostics-manifest.js") < indexHtml.indexOf("scanner-diagnostics-store.js"));
  assert(indexHtml.indexOf("scanner-diagnostics-store.js") < indexHtml.indexOf("scanner-diagnostics.js"));
  assert(indexHtml.indexOf("scanner-lifecycle-diagnostics.js") < indexHtml.indexOf("scanner.js"));
  assert(indexHtml.includes("scanner-diagnostics.js"));
  assert(serviceWorkerJs.includes('"./scanner-lifecycle-diagnostics.js"'));
  assert(serviceWorkerJs.includes('"./scanner-diagnostics-manifest.js"'));
  assert(serviceWorkerJs.includes('"./scanner-diagnostics-store.js"'));
  assert(serviceWorkerJs.includes('"./scanner-diagnostics.js"'));
  assert(appVersionJs.includes('var version = "61"'));
  assert(appJs.includes('id="scannerDiagnosticsBtn"'));
  assert(appJs.includes('navigate("/scanner-diagnostics")'));
  assert(appJs.includes('route.name === "scannerDiagnostics"'));
}

(async function run() {
  await testManifestAndPrn();
  await testWizardFlow();
  await testDiagnosticsStepPreferredScanTarget();
  await testDiagnosticsRouteCleanupClearsPreferredTargetOnHistoryReportAndStart();
  await testWizardRejectRetryAbortAndRestore();
  await testKeyboardTailEventsAreSavedBeforeDraftAndReport();
  await testIntentTailEventsAreProviderSpecificAndDeferred();
  await testAttemptCorrelationAndDuplicateDispatch();
  await testDuplicateDispatchAttemptDoesNotSkipStep();
  await testLateDuplicateDispatchAttemptPersistsFailWithoutStepChange();
  await testOrderedDraftPersistenceKeepsLatestDuplicateFail();
  await testTabTerminatorReportMeasurement();
  await testScannerObserverOverheadGuard();
  await testAsyncRouteTokenAndInvalidDraft();
  await testDelayedSaveDoesNotRenderAfterRouteCleanup();
  await testBusinessIsolation();
  await testScannerObserverTransports();
  await testAndroidImeEnterCancelsStaleTargetRead();
  await testAndroidImeTimeoutWithoutEnterStillScans();
  await testKeyboardWedgeCharsEnterStillScansOnce();
  await testAndroidImeEnterPhysicalDuplicateKeepsDedupeSemantics();
  await testKeyboardProviderStopCancelsPendingTargetRead();
  await testIntentPayloadCloneIsObserverGated();
  await testStorageAndExport();
  testShellIntegration();
  console.log("scanner-diagnostics.test.js passed");
})().catch((error) => {
  console.error(error);
  process.exit(1);
});
