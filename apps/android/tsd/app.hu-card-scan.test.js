// Regression test for the hardware-wedge repeat scan on an open HU card.
//
// This drives the REAL scanner transport: it loads scanner.js (the keyboard wedge)
// and app.js, renders the HU card the way the router does, then dispatches a real
// document "keydown" Enter event the way the wedge sees a hardware scan. It never
// calls the route scan handler directly through FlowStockTsdTestHooks, because that
// bypasses focus / readOnly / the wedge's document-keydown path where this bug lived.
//
// Root cause it locks down: the card used to fall back to the global readOnly
// scanSink for focus. The wedge skips scanSink in handleDocKeydown before unlocking
// it, so every hardware scan on the card was dropped. The card now renders its own
// data-scan-allow input (like /hu, filling and outbound) and focuses that instead.

const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const appJs = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");
const scannerJs = fs.readFileSync(path.join(__dirname, "scanner.js"), "utf8");

const hooks = {};
const documentListeners = [];
const overlays = [];
let activeElementRef = null;
let appHtml = "";
let resolveCalls = 0;
let huCardCalls = 0;
let lastHuCardCode = "";
let unknownNext = false;

function addDocumentListener(type, handler, capture) {
  documentListeners.push({ type, handler, capture: !!capture });
}
function removeDocumentListener(type, handler, capture) {
  for (let i = documentListeners.length - 1; i >= 0; i -= 1) {
    const e = documentListeners[i];
    if (e.type === type && e.handler === handler && e.capture === !!capture) {
      documentListeners.splice(i, 1);
    }
  }
}
function dispatchDocKeydown(key, target) {
  const ev = {
    type: "keydown",
    key,
    target,
    altKey: false,
    ctrlKey: false,
    metaKey: false,
    shiftKey: false,
    defaultPrevented: false,
    preventDefault() {
      this.defaultPrevented = true;
    },
  };
  documentListeners
    .filter((l) => l.type === "keydown" && l.capture)
    .forEach((l) => l.handler(ev));
  documentListeners
    .filter((l) => l.type === "keydown" && !l.capture)
    .forEach((l) => l.handler(ev));
  return ev;
}

function makeScanInput(id) {
  const listeners = {};
  const attrs = { "data-scan-allow": "1" };
  return {
    id,
    tagName: "INPUT",
    type: "text",
    value: "",
    readOnly: false,
    disabled: false,
    isConnected: true,
    isContentEditable: false,
    className: "",
    setAttribute(name, value) {
      attrs[name] = String(value);
    },
    getAttribute(name) {
      return Object.prototype.hasOwnProperty.call(attrs, name) ? attrs[name] : null;
    },
    removeAttribute(name) {
      delete attrs[name];
    },
    hasAttribute(name) {
      return Object.prototype.hasOwnProperty.call(attrs, name);
    },
    addEventListener(type, handler) {
      listeners[type] = listeners[type] || [];
      listeners[type].push(handler);
    },
    removeEventListener(type, handler) {
      listeners[type] = (listeners[type] || []).filter((h) => h !== handler);
    },
    focus() {
      activeElementRef = this;
    },
    blur() {
      if (activeElementRef === this) {
        activeElementRef = null;
      }
    },
    getClientRects() {
      return [1];
    },
    _listeners: listeners,
  };
}

const scanSinkEl = makeScanInput("scanSink");
const huCardScanInputEl = makeScanInput("huCardScanInput");
const huCardScanMessageEl = { textContent: "" };
const appEl = {
  id: "app",
  set innerHTML(v) {
    appHtml = v;
  },
  get innerHTML() {
    return appHtml;
  },
  querySelectorAll() {
    return [];
  },
};

function createGenericEl() {
  const attrs = {};
  return {
    className: "",
    innerHTML: "",
    parentNode: null,
    style: {},
    classList: { add() {}, remove() {}, contains() { return false; } },
    setAttribute(n, v) {
      attrs[n] = String(v);
    },
    getAttribute(n) {
      return Object.prototype.hasOwnProperty.call(attrs, n) ? attrs[n] : null;
    },
    addEventListener() {},
    removeEventListener() {},
    appendChild() {},
    removeChild() {},
    querySelector() {
      return null;
    },
    querySelectorAll() {
      return [];
    },
    focus() {},
  };
}

const context = {
  console,
  setImmediate,
  window: {
    FlowStockTsdTestHooks: hooks,
    FLOWSTOCK_SCANNER_MODE: "keyboard",
    location: {
      hash: "#/home",
      replace(url) {
        this.hash = String(url).replace(/^#/, "");
      },
    },
    sessionStorage: (function () {
      const s = {};
      return {
        setItem(k, v) {
          s[k] = String(v);
        },
        getItem(k) {
          return Object.prototype.hasOwnProperty.call(s, k) ? s[k] : null;
        },
        removeItem(k) {
          delete s[k];
        },
      };
    })(),
    localStorage: {
      setItem() {},
      getItem() {
        return null;
      },
    },
    alert() {},
    setTimeout(cb) {
      if (cb) cb();
      return 0;
    },
    clearTimeout() {},
    requestAnimationFrame(cb) {
      if (cb) cb();
      return 0;
    },
    setInterval() {
      return 0;
    },
    clearInterval() {},
    addEventListener() {},
    removeEventListener() {},
    KeyboardEvent: function () {},
  },
  document: {
    documentElement: null,
    get activeElement() {
      return activeElementRef;
    },
    set activeElement(v) {
      activeElementRef = v;
    },
    body: {
      classList: { add() {}, remove() {} },
      appendChild(el) {
        el.parentNode = this;
        overlays.push(el);
      },
      removeChild(el) {
        const i = overlays.indexOf(el);
        if (i >= 0) overlays.splice(i, 1);
        el.parentNode = null;
      },
    },
    getElementById(id) {
      if (id === "app") return appEl;
      if (id === "scanSink") return scanSinkEl;
      if (id === "huCardScanInput") return huCardScanInputEl;
      if (id === "huCardScanMessage") return huCardScanMessageEl;
      return null;
    },
    createElement() {
      return createGenericEl();
    },
    querySelector() {
      return null;
    },
    querySelectorAll(sel) {
      if (sel === ".overlay") return overlays;
      return [];
    },
    addEventListener: addDocumentListener,
    removeEventListener: removeDocumentListener,
    hasFocus() {
      return true;
    },
    visibilityState: "visible",
    hidden: false,
  },
  navigator: {},
  TsdStorage: {
    getSetting() {
      return Promise.resolve("keyboard");
    },
    setSetting() {
      return Promise.resolve();
    },
    apiResolveHu(code) {
      resolveCalls += 1;
      if (unknownNext) {
        unknownNext = false;
        return Promise.resolve({ known: false, huCode: code });
      }
      return Promise.resolve({
        known: true,
        huCode: code,
        state: "WAREHOUSE_FREE",
        title: "HU на складе",
        description: "Свободная HU.",
        cardAction: { type: "OPEN_HU_CARD", huCode: code },
        documentActions: [],
      });
    },
    apiGetHuCard(code) {
      huCardCalls += 1;
      lastHuCardCode = code;
      return Promise.resolve({
        known: true,
        huCode: code,
        state: "WAREHOUSE_FREE",
        title: "HU на складе",
        description: "Свободная HU.",
        stock: [],
        productionPallets: [],
        reservations: [],
        documents: [],
        documentActions: [],
      });
    },
  },
};
context.window.document = context.document;
context.window.navigator = context.navigator;
context.window.TsdStorage = context.TsdStorage;
context.localStorage = context.window.localStorage;
context.sessionStorage = context.window.sessionStorage;
context.requestAnimationFrame = context.window.requestAnimationFrame;
context.setTimeout = context.window.setTimeout;
context.clearTimeout = context.window.clearTimeout;
context.KeyboardEvent = context.window.KeyboardEvent;

vm.createContext(context);
vm.runInContext("var window = this.window; var document = this.document;", context);
vm.runInContext(scannerJs, context, { filename: "scanner.js" });
vm.runInContext(appJs, context, { filename: "app.js" });

async function flush() {
  await Promise.resolve();
  await Promise.resolve();
  await new Promise((r) => setImmediate(r));
}

async function renderCard(code) {
  context.window.location.hash = "#/hu/" + code;
  hooks.renderRouteInternal({ name: "huCard", id: code });
  await flush();
}

// A hardware wedge delivers the scanned value into the focused scan-allowed input
// and terminates with Enter. We mirror that: set the focused input's value, then
// dispatch the real document keydown the wedge listens for. No handler is called.
async function wedgeScan(value) {
  const target = activeElementRef;
  assert(target, "a scan target must be focused before a wedge scan");
  target.value = value;
  dispatchDocKeydown("Enter", target);
  await flush();
}

async function main() {
  await hooks.initScannerManager();

  // Render the HU card the way the router does.
  const huCardBefore = huCardCalls;
  await renderCard("HU-000801");
  assert.strictEqual(huCardCalls, huCardBefore + 1, "card loads via apiGetHuCard");
  assert.strictEqual(lastHuCardCode, "HU-000801");

  // The card exposes its own dedicated scan-allowed input...
  assert.match(appHtml, /id="huCardScanInput"/, "card renders a dedicated scan input");
  assert.match(appHtml, /data-scan-allow="1"/, "card scan input is scan-allowed");
  // ...and a route scan handler is installed.
  assert.strictEqual(
    typeof hooks.getActiveScanHandler(),
    "function",
    "open card keeps a global scan handler"
  );
  // Focus is on the dedicated card input, NOT the global readOnly scanSink.
  assert.strictEqual(
    activeElementRef,
    huCardScanInputEl,
    "card focuses its dedicated scan input, not scanSink"
  );

  // A hardware-wedge scan on the open card (through the real keyboard transport)
  // loads the next HU in place by replacing the route.
  const resolveBefore = resolveCalls;
  const huCardBeforeSecond = huCardCalls;
  await wedgeScan("HU-000802");
  assert.strictEqual(resolveCalls, resolveBefore + 1, "wedge scan resolves exactly once");
  assert.strictEqual(
    context.window.location.hash,
    "/hu/HU-000802",
    "wedge scan on the card navigates to the new HU"
  );

  // The replaced route renders the new HU and re-wires a fresh dedicated input.
  await renderCard("HU-000802");
  assert.strictEqual(huCardCalls, huCardBeforeSecond + 1, "new HU card loads after the scan");
  assert.strictEqual(lastHuCardCode, "HU-000802");
  assert.strictEqual(activeElementRef, huCardScanInputEl, "new card refocuses its scan input");

  // Mechanism guard: the global readOnly scanSink is a dead zone for the wedge —
  // handleDocKeydown returns before unlocking it, so a card that relied on it (the
  // old behavior) would drop the scan. This documents why the dedicated input is
  // required.
  scanSinkEl.readOnly = true;
  scanSinkEl.value = "";
  activeElementRef = scanSinkEl;
  const resolveBeforeSink = resolveCalls;
  dispatchDocKeydown("Enter", scanSinkEl);
  await flush();
  assert.strictEqual(
    resolveCalls,
    resolveBeforeSink,
    "a scan while only the readOnly scanSink is focused is dropped (dead zone the fix avoids)"
  );

  // Unknown HU on the open card keeps the card and shows the error, then refocuses
  // the dedicated input so the next wedge scan is received.
  await renderCard("HU-000802");
  huCardScanMessageEl.textContent = "";
  unknownNext = true;
  const resolveBeforeUnknown = resolveCalls;
  await wedgeScan("HU-999999");
  assert.strictEqual(resolveCalls, resolveBeforeUnknown + 1, "unknown HU resolves once");
  assert.strictEqual(
    context.window.location.hash,
    "#/hu/HU-000802",
    "unknown HU keeps the current card"
  );
  assert.match(
    huCardScanMessageEl.textContent,
    /HU неизвестен: HU-999999/,
    "unknown HU shows the error on the card"
  );
  assert.strictEqual(
    activeElementRef,
    huCardScanInputEl,
    "card refocuses its scan input after an unknown HU"
  );

  console.log("TSD HU card hardware-wedge scan tests passed.");
}

main().catch(function (error) {
  console.error(error);
  process.exit(1);
});
