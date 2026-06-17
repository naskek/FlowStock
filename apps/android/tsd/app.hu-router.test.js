const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const appJs = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");
const storageJs = fs.readFileSync(path.join(__dirname, "storage.js"), "utf8");
const appVersionJs = fs.readFileSync(path.join(__dirname, "app-version.js"), "utf8");
const serviceWorkerJs = fs.readFileSync(path.join(__dirname, "service-worker.js"), "utf8");
const scannerJs = fs.readFileSync(path.join(__dirname, "scanner.js"), "utf8");

const hooks = {};
const alerts = [];
const overlays = [];
const sessionStore = {};
const documentListeners = [];

function addDocumentListener(type, handler, capture) {
  documentListeners.push({ type: type, handler: handler, capture: !!capture });
}

function removeDocumentListener(type, handler, capture) {
  for (let i = documentListeners.length - 1; i >= 0; i -= 1) {
    const entry = documentListeners[i];
    if (entry.type === type && entry.handler === handler && entry.capture === !!capture) {
      documentListeners.splice(i, 1);
    }
  }
}

function dispatchDocumentEvent(event, target) {
  event.target = target;
  documentListeners
    .filter(function (entry) {
      return entry.type === event.type && entry.capture;
    })
    .forEach(function (entry) {
      entry.handler(event);
    });
  documentListeners
    .filter(function (entry) {
      return entry.type === event.type && !entry.capture;
    })
    .forEach(function (entry) {
      entry.handler(event);
    });
}

function getRouteScanHandler() {
  return hooks.getActiveScanHandler ? hooks.getActiveScanHandler() : null;
}

class KeyboardEventPolyfill {
  constructor(type, init) {
    init = init || {};
    this.type = type;
    this.key = init.key || "";
    this.bubbles = !!init.bubbles;
    this.target = null;
    this.defaultPrevented = false;
  }

  preventDefault() {
    this.defaultPrevented = true;
  }
}
let resolveCalls = 0;
let fillingScanCalls = 0;
let fillingFillCalls = 0;
let fillingMixedFillCalls = 0;
let outboundScanCalls = 0;
let outboundCompleteCalls = 0;
let huCardCalls = 0;
let lastHuCardCode = "";
let huCardActionButtons = [];
let nextResolveResult = null;

const appEl = {
  innerHTML: "",
  querySelectorAll: function () {
    return [];
  },
};

function createButton() {
  return {
    disabled: false,
    isConnected: true,
    value: "",
    addEventListener: function () {},
    getAttribute: function () {
      return null;
    },
    focus: function () {},
  };
}

function createHuCardActionButtons() {
  huCardActionButtons = [];
  const matches = appEl.innerHTML.matchAll(/data-hu-card-action="(\d+)"/g);
  for (const match of matches) {
    const actionIndex = match[1];
    const button = createButton();
    let clickHandler = null;
    button.addEventListener = function (event, handler) {
      if (event === "click") {
        clickHandler = handler;
      }
    };
    button.getAttribute = function (name) {
      return name === "data-hu-card-action" ? actionIndex : null;
    };
    button.click = function () {
      if (clickHandler) {
        clickHandler({ preventDefault: function () {} });
      }
    };
    huCardActionButtons.push(button);
  }
  return huCardActionButtons;
}

let huLookupFocusCount = 0;
let activeElementRef = null;

function createHuLookupInput() {
  const attrs = {
    "data-scan-allow": "1",
    "data-hu-lookup": "1",
    inputmode: "none",
  };
  const listeners = {};
  const input = {
    tagName: "INPUT",
    value: "",
    readOnly: false,
    attrs: attrs,
    setAttribute: function (name, value) {
      this.attrs[name] = value;
    },
    getAttribute: function (name) {
      return Object.prototype.hasOwnProperty.call(this.attrs, name) ? this.attrs[name] : null;
    },
    removeAttribute: function (name) {
      delete this.attrs[name];
    },
    addEventListener: function (event, handler, capture) {
      const key = event + (capture ? ":capture" : "");
      listeners[key] = listeners[key] || [];
      listeners[key].push(handler);
    },
    simulateWedgeInput: function (text) {
      if (this.readOnly) {
        throw new Error("wedge blocked by readOnly");
      }
      this.value = String(text || "");
    },
    dispatchEvent: function (event) {
      if (!event.preventDefault) {
        event.preventDefault = function () {
          event.defaultPrevented = true;
        };
      }
      event.target = this;
      documentListeners
        .filter(function (entry) {
          return entry.type === event.type && entry.capture;
        })
        .forEach(function (entry) {
          entry.handler(event);
        });
      (listeners[event.type] || []).forEach(function (handler) {
        handler(event);
      });
      if (event.bubbles) {
        documentListeners
          .filter(function (entry) {
            return entry.type === event.type && !entry.capture;
          })
          .forEach(function (entry) {
            entry.handler(event);
          });
      }
      return !event.defaultPrevented;
    },
    dispatchPointerDown: function () {
      const event = {
        preventDefault: function () {
          event.defaultPrevented = true;
        },
        defaultPrevented: false,
      };
      (listeners["pointerdown:capture"] || []).forEach(function (handler) {
        handler(event);
      });
    },
    dispatchBlur: function () {
      activeElementRef = null;
      (listeners.blur || []).forEach(function (handler) {
        handler({});
      });
    },
    blur: function () {
      activeElementRef = null;
    },
    focus: function () {
      huLookupFocusCount += 1;
      activeElementRef = input;
    },
  };
  return input;
}

let huLookupInput = null;
let huLookupFindBtn = null;
let huLookupMessageEl = null;

function resetHuLookupDom() {
  huLookupInput = createHuLookupInput();
  huLookupFindBtn = createButton();
  huLookupMessageEl = { textContent: "" };
  huLookupFocusCount = 0;
  activeElementRef = null;
}

resetHuLookupDom();

function createOverlay() {
  const closeBtn = createButton();
  const cancelBtn = createButton();
  const cardBtn = createButton();
  const relatedBtn = createButton();
  const relatedList = {
    innerHTML: "",
    querySelectorAll: function () {
      return [];
    },
  };
  return {
    className: "",
    innerHTML: "",
    parentNode: null,
    setAttribute: function () {},
    getAttribute: function () {
      return null;
    },
    addEventListener: function () {},
    focus: function () {},
    querySelector: function (selector) {
      if (selector === ".overlay-close") {
        return closeBtn;
      }
      if (selector === ".overlay-cancel") {
        return cancelBtn;
      }
      if (selector === "#globalHuCardBtn") {
        return cardBtn;
      }
      if (selector === "#globalHuRelatedBtn") {
        return relatedBtn;
      }
      if (selector === "#globalHuRelatedList") {
        return relatedList;
      }
      return null;
    },
  };
}

const outboundOrder = {
  order_id: 620,
  order_ref: "006",
  expected_hu_count: 2,
  picked_hu_count: 0,
  remaining_qty: 1200,
  scanned_qty: 0,
  is_complete: false,
  can_close: false,
  hus: [
    {
      hu_code: "HU-000123",
      status: "PENDING",
      qty: 600,
      lines: [{ item_name: "Товар", qty: 600 }],
    },
  ],
};
const fillingContext = {
  workItem: { orderId: 619, orderRef: "005", prdDocId: 1490, prdDocRef: "PRD-1490" },
  document: {
    summary: { plannedPalletCount: 1, filledPalletCount: 0, remainingPalletCount: 1 },
    pallets: [{ huCode: "HU-000123", status: "PLANNED", plannedQty: 600 }],
  },
};

const context = {
  console,
  window: {
    FlowStockTsdTestHooks: hooks,
    location: { hash: "#/home" },
    sessionStorage: {
      setItem: function (key, value) {
        sessionStore[key] = String(value);
      },
      getItem: function (key) {
        return Object.prototype.hasOwnProperty.call(sessionStore, key) ? sessionStore[key] : null;
      },
      removeItem: function (key) {
        delete sessionStore[key];
      },
    },
    localStorage: {
      setItem: function () {},
      getItem: function () {
        return null;
      },
    },
    alert: function (message) {
      alerts.push(message);
    },
    setTimeout: function (callback) {
      if (callback) {
        callback();
      }
      return 0;
    },
    clearTimeout: function () {},
    requestAnimationFrame: function (callback) {
      if (callback) {
        callback();
      }
    },
    setInterval: function () {
      return 0;
    },
    addEventListener: function () {},
    KeyboardEvent: KeyboardEventPolyfill,
  },
  document: {
    documentElement: null,
    get activeElement() {
      return activeElementRef;
    },
    set activeElement(value) {
      activeElementRef = value;
    },
    body: {
      classList: { add: function () {}, remove: function () {} },
      appendChild: function (element) {
        element.parentNode = this;
        overlays.push(element);
      },
      removeChild: function (element) {
        const index = overlays.indexOf(element);
        if (index >= 0) {
          overlays.splice(index, 1);
        }
        element.parentNode = null;
      },
    },
    getElementById: function (id) {
      if (id === "app") {
        return appEl;
      }
      if (id === "huLookupInput") {
        return huLookupInput;
      }
      if (id === "huLookupFindBtn") {
        return huLookupFindBtn;
      }
      if (id === "huLookupMessage") {
        return huLookupMessageEl;
      }
      return null;
    },
    createElement: function () {
      return createOverlay();
    },
    querySelector: function () {
      return null;
    },
    querySelectorAll: function (selector) {
      if (selector === ".overlay") {
        return overlays;
      }
      if (selector === "[data-hu-card-action]") {
        return createHuCardActionButtons();
      }
      return [];
    },
    addEventListener: addDocumentListener,
    removeEventListener: removeDocumentListener,
  },
  navigator: {},
  localStorage: null,
  sessionStorage: null,
  TsdStorage: {
    getSetting: function () {
      return Promise.resolve("auto");
    },
    setSetting: function () {
      return Promise.resolve();
    },
    normalizeOutboundPickingOrder: function (order) {
      return order;
    },
    apiResolveHu: function (code) {
      resolveCalls += 1;
      if (nextResolveResult) {
        const result = nextResolveResult;
        nextResolveResult = null;
        return Promise.resolve(typeof result === "function" ? result(code) : result);
      }
      return Promise.resolve({
        known: true,
        huCode: code,
        state: "PLANNED_PRODUCTION",
        title: "HU запланирована к наполнению",
        description: "Заказ 005",
        cardAction: { type: "OPEN_HU_CARD", huCode: code, label: "Карточка паллеты" },
        documentActions: [{ type: "OPEN_FILLING", orderId: 619, label: "Открыть наполнение заказа 005" }],
      });
    },
    apiGetHuCard: function (code) {
      huCardCalls += 1;
      lastHuCardCode = code;
      return Promise.resolve({
        known: true,
        huCode: code,
        state: "PLANNED_PRODUCTION",
        title: "HU запланирована к наполнению",
        description: "Заказ 005",
        stock: [],
        productionPallets: [],
        reservations: [],
        documents: [],
        documentActions: [{ type: "OPEN_ORDER", orderId: 619, label: "Открыть заказ 005" }],
      });
    },
    apiScanProductionPallet: function () {
      fillingScanCalls += 1;
      return Promise.resolve({ alreadyFilled: true });
    },
    apiFillProductionPallet: function () {
      fillingFillCalls += 1;
      return Promise.resolve({});
    },
    apiFillMixedProductionPalletComponents: function () {
      fillingMixedFillCalls += 1;
      return Promise.resolve({});
    },
    apiGetProductionFillingContext: function () {
      return Promise.resolve(fillingContext);
    },
    apiScanOutboundPickingHu: function () {
      outboundScanCalls += 1;
      return Promise.resolve({ message: "HU подобрана.", order: outboundOrder });
    },
    apiGetOutboundPickingOrder: function () {
      return Promise.resolve(outboundOrder);
    },
    apiCompleteOutboundPicking: function () {
      outboundCompleteCalls += 1;
      return Promise.resolve({});
    },
  },
};
context.localStorage = context.window.localStorage;
context.sessionStorage = context.window.sessionStorage;
context.window.document = context.document;
context.window.navigator = context.navigator;
context.window.TsdStorage = context.TsdStorage;
context.requestAnimationFrame = context.window.requestAnimationFrame;
context.KeyboardEvent = KeyboardEventPolyfill;

vm.createContext(context);
vm.runInContext("var window = this.window; var document = this.document;", context);
vm.runInContext(scannerJs, context, { filename: "scanner.js" });
vm.runInContext(appJs, context, { filename: "app.js" });

async function flushPromises() {
  await Promise.resolve();
  await Promise.resolve();
  await new Promise(function (resolve) {
    setImmediate(resolve);
  });
}

async function main() {
  assert.strictEqual(hooks.extractHuCode(" HU0000123\n"), "HU-0000123");
  assert.strictEqual(hooks.extractHuCode("prefix HU-000123 suffix"), "HU-000123");
  assert.strictEqual(hooks.extractHuCode("123456"), "");
  assert.deepStrictEqual(
    JSON.parse(JSON.stringify(hooks.getRouteFromPath("/hu/HU-000123"))),
    { name: "huCard", id: "HU-000123" }
  );

  assert(storageJs.includes("/api/tsd/hu/resolve?code="));
  assert(storageJs.includes("/api/tsd/hu/card?code="));
  assert(appVersionJs.includes('var version = "47"'));
  assert(serviceWorkerJs.includes('importScripts("./app-version.js")'));
  assert(serviceWorkerJs.includes('"./app.js"'));
  assert(serviceWorkerJs.includes('"./img/home/hu-search.png"'));

  const homeHtml = hooks.renderHome();
  assert.match(homeHtml, /Поиск HU/);
  assert.match(homeHtml, /Сканирование и поиск паллеты/);
  assert.match(homeHtml, /data-route="hu"/);
  assert.match(homeHtml, /img\/home\/hu-search\.png/);
  assert.doesNotMatch(homeHtml, /Информация/);
  assert.doesNotMatch(homeHtml, /data-route="settings"/);

  await hooks.initScannerManager();
  hooks.renderRouteInternal({ name: "home" });
  assert.strictEqual(
    getRouteScanHandler(),
    null,
    "home render should not install a global HU scan handler"
  );

  const homeHandler = getRouteScanHandler();
  if (homeHandler) {
    homeHandler({ value: "NOT-A-HU", source: "test" });
  }
  await flushPromises();
  assert.strictEqual(resolveCalls, 0, "non-HU scan from home should not call resolver");

  const homeHash = context.window.location.hash;
  if (homeHandler) {
    homeHandler({ value: "HU-000123", source: "test" });
  }
  await flushPromises();
  assert.strictEqual(resolveCalls, 0, "home HU scan should not call resolver without route handler");
  assert.strictEqual(context.window.location.hash, homeHash, "home HU scan must stay on home");

  context.window.location.hash = "#/orders";
  const resolveCallsBeforeOrders = resolveCalls;
  assert.strictEqual(getRouteScanHandler(), null, "orders route should not install HU lookup handler");
  await flushPromises();
  assert.strictEqual(resolveCalls, resolveCallsBeforeOrders, "orders without scan handler must not resolve HU");

  context.window.location.hash = "#/hu";
  resetHuLookupDom();
  const resolveCallsBeforeHuRoute = resolveCalls;
  hooks.renderRouteInternal({ name: "hu" });
  await flushPromises();
  assert.strictEqual(typeof getRouteScanHandler(), "function");
  assert.strictEqual(huLookupInput.readOnly, false, "/hu should open in scan-mode");
  assert.strictEqual(huLookupInput.getAttribute("inputmode"), "none", "/hu should suppress inputmode keyboard");
  assert.strictEqual(huLookupInput.getAttribute("data-hu-manual"), null, "/hu should not start in manual mode");
  assert.strictEqual(activeElementRef, huLookupInput, "/hu render should focus lookup input");
  assert(huLookupFocusCount >= 1, "/hu render should call focus via requestAnimationFrame");

  const focusCountAfterRender = huLookupFocusCount;
  huLookupInput.dispatchBlur();
  assert.strictEqual(huLookupFocusCount, focusCountAfterRender, "blur must not restore focus");
  assert.strictEqual(huLookupInput.readOnly, false, "blur should keep scan-mode");

  const wedgeCode = "HU-000124";
  huLookupInput.simulateWedgeInput(wedgeCode);
  assert.strictEqual(huLookupInput.value, wedgeCode, "wedge should write into lookup field");
  huLookupInput.dispatchEvent(
    new KeyboardEventPolyfill("keydown", {
      key: "Enter",
      bubbles: true,
    })
  );
  await flushPromises();
  assert.strictEqual(resolveCalls, resolveCallsBeforeHuRoute + 1, "/hu wedge Enter should resolve once");
  assert.strictEqual(context.window.location.hash, "/hu/HU-000124", "/hu wedge should open HU card");

  context.window.location.hash = "#/hu";
  resetHuLookupDom();
  resolveCalls = 0;
  hooks.renderRouteInternal({ name: "hu" });
  await flushPromises();
  const focusCountBeforeProgrammatic = huLookupFocusCount;
  huLookupInput.focus();
  assert.strictEqual(huLookupInput.getAttribute("data-hu-manual"), null, "programmatic focus must not enable manual-mode");
  const resolveCallsBeforeManual = resolveCalls;
  huLookupInput.dispatchPointerDown();
  assert.strictEqual(huLookupInput.readOnly, false, "tap should enable manual HU input");
  assert.strictEqual(huLookupInput.getAttribute("inputmode"), "text");
  assert.strictEqual(huLookupInput.getAttribute("data-hu-manual"), "1");
  huLookupInput.value = "HU-000125";
  await hooks.submitHuLookup();
  await flushPromises();
  assert.strictEqual(resolveCalls, resolveCallsBeforeManual + 1, "manual Find path should resolve once");
  assert.strictEqual(context.window.location.hash, "/hu/HU-000125");

  context.window.location.hash = "#/hu";
  resetHuLookupDom();
  nextResolveResult = { known: false };
  hooks.renderRouteInternal({ name: "hu" });
  await flushPromises();
  const resolveCallsBeforeUnknown = resolveCalls;
  const focusBeforeUnknown = huLookupFocusCount;
  await hooks.submitHuLookup("HU-999999");
  await flushPromises();
  assert.strictEqual(resolveCalls, resolveCallsBeforeUnknown + 1);
  assert.match(huLookupMessageEl.textContent, /HU неизвестен: HU-999999/);
  assert.strictEqual(context.window.location.hash, "#/hu", "unknown HU must stay on lookup screen");
  assert.strictEqual(huLookupInput.readOnly, false, "unknown HU should restore scan-mode");
  assert(huLookupFocusCount > focusBeforeUnknown, "unknown HU should restore scanner focus");

  context.window.location.hash = "#/hu";
  resetHuLookupDom();
  nextResolveResult = function () {
    return Promise.reject(new Error("Failed to fetch"));
  };
  hooks.renderRouteInternal({ name: "hu" });
  await flushPromises();
  const focusBeforeNetworkError = huLookupFocusCount;
  await hooks.submitHuLookup("HU-000127");
  await flushPromises();
  assert.strictEqual(huLookupInput.readOnly, false, "network error should restore scan-mode");
  assert(huLookupFocusCount > focusBeforeNetworkError, "network error should restore scanner focus");
  nextResolveResult = null;

  context.window.location.hash = "#/hu";
  resetHuLookupDom();
  hooks.renderRouteInternal({ name: "hu" });
  await flushPromises();
  const resolveCallsBeforeInvalid = resolveCalls;
  await hooks.submitHuLookup("not-a-hu");
  await flushPromises();
  assert.strictEqual(resolveCalls, resolveCallsBeforeInvalid, "invalid format must not call resolve");
  assert.strictEqual(huLookupInput.readOnly, false, "invalid format should restore scan-mode");
  assert.strictEqual(activeElementRef, huLookupInput, "invalid format should restore focus");

  context.window.location.hash = "#/hu";
  resetHuLookupDom();
  hooks.renderRouteInternal({ name: "hu" });
  await flushPromises();
  const resolveCallsBeforeFindBtn = resolveCalls;
  huLookupInput.value = "HU-000128";
  await hooks.submitHuLookup();
  await flushPromises();
  assert.strictEqual(resolveCalls, resolveCallsBeforeFindBtn + 1, "Find button path should resolve once");

  context.window.location.hash = "#/hu";
  resetHuLookupDom();
  hooks.renderRouteInternal({ name: "hu" });
  await flushPromises();
  const resolveCallsBeforeEnter = resolveCalls;
  huLookupInput.simulateWedgeInput("HU-000126");
  huLookupInput.dispatchEvent(
    new KeyboardEventPolyfill("keydown", {
      key: "Enter",
      bubbles: true,
    })
  );
  await flushPromises();
  assert.strictEqual(resolveCalls, resolveCallsBeforeEnter + 1, "Enter wedge path should resolve exactly once");
  assert.strictEqual(context.window.location.hash, "/hu/HU-000126");

  hooks.setNavOrigin("/hu");
  assert.strictEqual(hooks.navigateBack({ name: "huCard", id: "HU-000126" }), "/hu");
  context.window.location.hash = "#/hu";
  resetHuLookupDom();
  hooks.renderRouteInternal({ name: "hu" });
  await flushPromises();
  assert.strictEqual(activeElementRef, huLookupInput, "back to /hu should restore scanner focus");
  assert.strictEqual(huLookupInput.readOnly, false, "back to /hu should restore scan-mode");

  resetHuLookupDom();
  hooks.renderRouteInternal({ name: "hu" });
  await flushPromises();
  const resolveCallsBeforeBackWedge = resolveCalls;
  huLookupInput.simulateWedgeInput("HU-000130");
  huLookupInput.dispatchEvent(
    new KeyboardEventPolyfill("keydown", {
      key: "Enter",
      bubbles: true,
    })
  );
  await flushPromises();
  assert.strictEqual(resolveCalls, resolveCallsBeforeBackWedge + 1, "wedge after back should resolve without tap");
  assert.strictEqual(context.window.location.hash, "/hu/HU-000130");

  overlays.length = 0;
  const resolveCallsBeforeOutbound = resolveCalls;
  hooks.renderOutboundPickingOrder(outboundOrder, {});
  assert.strictEqual(typeof getRouteScanHandler(), "function");
  const outboundHandler = getRouteScanHandler();
  outboundHandler({ value: "HU-000123", source: "test" });
  await flushPromises();
  assert.strictEqual(resolveCalls, resolveCallsBeforeOutbound, "outbound active handler must bypass global resolver");
  assert.strictEqual(outboundScanCalls, 1, "outbound active handler should receive physical scan");

  overlays.length = 0;
  const resolveCallsBeforeFilling = resolveCalls;
  hooks.renderFillingScanScreen(fillingContext, {});
  assert.strictEqual(typeof getRouteScanHandler(), "function");
  const fillingHandler = getRouteScanHandler();
  fillingHandler({ value: "HU-000123", source: "test" });
  await flushPromises();
  assert.strictEqual(resolveCalls, resolveCallsBeforeFilling, "filling active handler must bypass global resolver");
  assert.strictEqual(fillingScanCalls, 1, "filling active handler should receive physical scan");

  const wireDocStart = appJs.indexOf("function wireDoc(doc)");
  const wireDocEnd = appJs.indexOf("function wire", wireDocStart + 1);
  const wireDocBody = appJs.slice(wireDocStart, wireDocEnd);
  assert(wireDocBody.includes("setScanHandler(handleScanEvent)"), "draft document screen should keep route-specific scan handler");
  assert(!wireDocBody.includes("resolveHuAndNavigate"), "draft document scan handler must not use HU lookup resolver");

  hooks.setScanHandler(null);
  context.window.location.hash = "#/hu/HU-000321";
  const huCardCallsBefore = huCardCalls;
  hooks.renderRouteInternal({ name: "huCard", id: "HU-000321" });
  await flushPromises();
  assert.strictEqual(huCardCalls, huCardCallsBefore + 1, "HU card route should load apiGetHuCard");
  assert.strictEqual(lastHuCardCode, "HU-000321");
  assert.match(appEl.innerHTML, /HU-000321/);
  assert.match(appEl.innerHTML, /hu-card-screen/);
  assert.match(appEl.innerHTML, /hu-detail-card/);
  assert.match(appEl.innerHTML, /Открыть заказ 005/);
  assert.strictEqual(huCardActionButtons.length, 1, "HU card should wire action buttons");
  huCardActionButtons[0].click();
  assert.strictEqual(context.window.location.hash, "/order/619", "HU card action should keep existing navigation behavior");
  assert.strictEqual(hooks.getNavOrigin(), "/hu/HU-000321", "HU card action should save HU card origin");
  assert.strictEqual(hooks.navigateBack({ name: "order", id: "619" }), "/hu/HU-000321");
  assert.strictEqual(context.window.location.hash, "/hu/HU-000321", "order back should return to HU card origin");
  assert.strictEqual(hooks.getNavOrigin(), null, "back navigation should clear saved origin");

  hooks.clearNavOrigin();
  context.window.location.hash = "#/order/777";
  assert.strictEqual(hooks.navigateBack({ name: "order", id: "777" }), "/orders");
  assert.strictEqual(context.window.location.hash, "/orders", "direct order back should use standard fallback");
  assert.strictEqual(hooks.getNavOrigin(), null);

  context.window.location.hash = "#/hu/HU-0001334";
  assert.strictEqual(hooks.executeTsdHuAction({ type: "OPEN_DOCUMENT", docId: 1490 }), true);
  assert.strictEqual(context.window.location.hash, "/doc/1490");
  assert.strictEqual(hooks.getNavOrigin(), "/hu/HU-0001334");
  assert.strictEqual(hooks.navigateBack({ name: "doc", id: "1490" }), "/hu/HU-0001334");
  assert.strictEqual(context.window.location.hash, "/hu/HU-0001334", "document back should return to HU card origin");
  assert.strictEqual(hooks.getNavOrigin(), null);

  context.window.location.hash = "#/hu/HU-0001334";
  assert.strictEqual(hooks.executeTsdHuAction({ type: "OPEN_FILLING", orderId: 619 }), true);
  assert.strictEqual(context.window.location.hash, "/filling/619");
  assert.strictEqual(hooks.getNavOrigin(), "/hu/HU-0001334");
  assert.strictEqual(hooks.navigateBack({ name: "fillingDoc", id: "619" }), "/hu/HU-0001334");
  assert.strictEqual(context.window.location.hash, "/hu/HU-0001334", "filling back should return to HU card origin");
  assert.strictEqual(hooks.getNavOrigin(), null);

  context.window.location.hash = "#/hu/HU-0001334";
  assert.strictEqual(hooks.executeTsdHuAction({ type: "OPEN_OUTBOUND", orderId: 620 }), true);
  assert.strictEqual(context.window.location.hash, "/outbound/620");
  assert.strictEqual(hooks.getNavOrigin(), "/hu/HU-0001334");
  assert.strictEqual(hooks.navigateBack({ name: "outboundOrder", id: "620" }), "/hu/HU-0001334");
  assert.strictEqual(context.window.location.hash, "/hu/HU-0001334", "outbound back should return to HU card origin");
  assert.strictEqual(hooks.getNavOrigin(), null);

  context.window.location.hash = "#/orders";
  hooks.setNavOrigin("/orders");
  context.window.location.hash = "/order/620";
  assert.strictEqual(hooks.navigateBack({ name: "order", id: "620" }), "/orders");
  assert.strictEqual(context.window.location.hash, "/orders", "orders-origin back must not leak an old HU origin");
  assert.strictEqual(hooks.getNavOrigin(), null);

  hooks.clearNavOrigin();
  context.window.location.hash = "#/hu/HU-000654";
  hooks.renderRouteInternal({ name: "huCard", id: "HU-000654" });
  await flushPromises();
  assert.strictEqual(hooks.getNavOrigin(), null, "detail route render must not create nav origin");

  const freeCardHtml = hooks.renderTsdHuCard({
    known: true,
    huCode: "HU-000321",
    state: "WAREHOUSE_FREE",
    title: "HU на складе",
    description: "Свободная HU. Не привязана к активному заказу.",
    stock: [{ item_name: "Товар", location_code: "MAIN", qty: 600, uom: "шт" }],
    productionPallets: [],
    reservations: [],
    documents: [],
    documentActions: [],
  });
  assert.match(freeCardHtml, /HU-000321/);
  assert.match(freeCardHtml, /hu-card-heading/);
  assert.match(freeCardHtml, /hu-status-panel hu-status-panel--stock/);
  assert.match(freeCardHtml, /На складе/);
  assert.doesNotMatch(freeCardHtml, /Статус:/);
  assert.match(freeCardHtml, /MAIN/);
  assert.strictEqual((freeCardHtml.match(/600 шт/g) || []).length, 1, "free HU card should show qty once");
  assert.doesNotMatch(freeCardHtml, /data-hu-card-action/);

  const filledCardHtml = hooks.renderTsdHuCard({
    known: true,
    huCode: "HU-000555",
    state: "FILLED_PRODUCTION_PALLET",
    stock: [],
    productionPallets: [
      {
        pallet_no: 1,
        pallet_count: 1,
        prd_doc_ref: "PRD-1490",
        prd_doc_status: "CLOSED",
        order_ref: "005",
        order_type: "INTERNAL",
        components: [{ item_name: "Аджика 500 мл", filled_qty: 378, planned_qty: 378, uom: "шт" }],
      },
    ],
    reservations: [],
    documents: [
      {
        doc_id: 1490,
        doc_ref: "PRD-1490",
        doc_type: "PRODUCTION_RECEIPT",
        doc_status: "CLOSED",
        item_name: "Аджика 500 мл",
        qty: 378,
        uom: "шт",
      },
    ],
    latestMovement: { doc_ref: "PRD-1490", timestamp: "2026-01-02T03:04:05Z" },
    documentActions: [{ type: "OPEN_DOCUMENT", docId: 1490, label: "PRD-1490" }],
  });
  assert.match(filledCardHtml, /hu-status-panel--filled/);
  assert.match(filledCardHtml, /Наполнена/);
  assert.match(filledCardHtml, /hu-content-row/);
  assert.strictEqual((filledCardHtml.match(/378 шт/g) || []).length, 1, "filled HU main content should show qty once");
  assert.doesNotMatch(filledCardHtml, /read-only/);
  assert.doesNotMatch(filledCardHtml, /PRODUCTION_RECEIPT/);
  assert.doesNotMatch(filledCardHtml, /CLOSED/);
  assert.doesNotMatch(filledCardHtml, /FILLED/);
  assert.match(filledCardHtml, /Открыть документ выпуска/);
  assert(
    filledCardHtml.indexOf("PRD-1490") > filledCardHtml.indexOf("Техническая информация"),
    "production document ref should be in technical block"
  );

  const plannedCardHtml = hooks.renderTsdHuCard({
    known: true,
    huCode: "HU-000556",
    state: "PLANNED_PRODUCTION",
    stock: [],
    productionPallets: [
      {
        order_ref: "006",
        order_type: "INTERNAL",
        components: [{ item_name: "Соус 250 мл", planned_qty: 378, uom: "шт" }],
      },
    ],
    reservations: [],
    documents: [],
    documentActions: [
      { type: "OPEN_FILLING", orderId: 619, label: "Открыть наполнение заказа 006" },
      { type: "OPEN_ORDER", orderId: 619, label: "Открыть заказ 006" },
    ],
  });
  assert.match(plannedCardHtml, /hu-status-panel--waiting/);
  assert.match(plannedCardHtml, /Запланирована к наполнению/);
  assert.doesNotMatch(plannedCardHtml, /PLANNED/);
  assert.match(plannedCardHtml, /Еще не на складе/);
  assert.match(plannedCardHtml, /Открыть наполнение заказа 006/);
  assert.match(plannedCardHtml, /hu-action-btn--primary/);
  assert.match(plannedCardHtml, /hu-action-btn--secondary/);
  assert.match(plannedCardHtml, /Открыть заказ 006/);

  const mixedCardHtml = hooks.renderTsdHuCard({
    known: true,
    huCode: "HU-000557",
    state: "FILLED_PRODUCTION_PALLET",
    stock: [],
    productionPallets: [
      {
        order_ref: "007",
        components: [
          { item_name: "Соус острый", filled_qty: 189, uom: "шт" },
          { item_name: "Соус мягкий", filled_qty: 189, uom: "шт" },
        ],
      },
    ],
    reservations: [],
    documents: [],
    documentActions: [],
  });
  assert.match(mixedCardHtml, /Соус острый/);
  assert.match(mixedCardHtml, /Соус мягкий/);
  assert.match(mixedCardHtml, /Итого/);
  assert.match(mixedCardHtml, /378 шт/);

  const toneCases = [
    ["WAREHOUSE_FREE", "stock"],
    ["WAREHOUSE_RESERVED", "reserved"],
    ["FILLED_PRODUCTION_PALLET", "filled"],
    ["PLANNED_PRODUCTION", "waiting"],
    ["OUTBOUND_PICKED", "partial"],
    ["SHIPPED", "shipped"],
    ["AMBIGUOUS", "problem"],
    ["UNKNOWN", "problem"],
    ["HISTORY_ONLY", "stock"],
    ["", "problem"],
    ["CLOSED", "problem"],
  ];
  toneCases.forEach(function (entry) {
    const state = entry[0];
    const tone = entry[1];
    assert.strictEqual(hooks.getTsdHuStatusTone(state), tone, "tone for " + (state || "(empty)"));
    const html = hooks.renderTsdHuCard({
      known: true,
      huCode: "HU-TONE",
      state: state,
      stock: [],
      productionPallets: [],
      reservations: [],
      documents: [],
      documentActions: [],
    });
    assert.match(html, new RegExp("hu-status-panel--" + tone), "panel class for " + (state || "(empty)"));
    assert.doesNotMatch(html, /class="hu-status-panel">/, "status panel must include tone modifier");
  });

  assert.strictEqual(context.window.FlowStockTsdIsBusy(), false, "default busy must be false");
  assert.strictEqual(typeof context.window.FlowStockTsdIsBusy(), "boolean");

  hooks.setCurrentRoute({ name: "home" });
  assert.strictEqual(context.window.FlowStockTsdIsBusy(), false, "/home must not be busy");
  hooks.setCurrentRoute({ name: "settings" });
  assert.strictEqual(context.window.FlowStockTsdIsBusy(), false, "/settings must not be busy");
  hooks.setCurrentRoute({ name: "hu" });
  assert.strictEqual(context.window.FlowStockTsdIsBusy(), false, "/hu must not be busy");
  hooks.setCurrentRoute({ name: "orders" });
  assert.strictEqual(context.window.FlowStockTsdIsBusy(), false, "/orders must not be busy");
  hooks.setCurrentRoute({ name: "fillingDoc", id: "619" });
  assert.strictEqual(context.window.FlowStockTsdIsBusy(), false, "open filling without request must not be busy");
  hooks.setCurrentRoute({ name: "outboundOrder", id: "620" });
  assert.strictEqual(context.window.FlowStockTsdIsBusy(), false, "open outbound without request must not be busy");

  let syncThrowBusy = false;
  try {
    await hooks.runTsdCriticalOperation("test.sync-throw", function () {
      throw new Error("sync fail");
    });
  } catch (error) {
    syncThrowBusy = error.message === "sync fail";
  }
  assert(syncThrowBusy, "sync throw should propagate");
  assert.strictEqual(hooks.isTsdCriticalOperationActive(), false, "sync throw must reset busy");

  await hooks
    .runTsdCriticalOperation("test.reject", function () {
      return Promise.reject(new Error("reject fail"));
    })
    .catch(function () {});
  assert.strictEqual(hooks.isTsdCriticalOperationActive(), false, "rejected promise must reset busy");

  const parallelA = hooks.runTsdCriticalOperation("test.parallel", function () {
    return new Promise(function (resolve) {
      setImmediate(resolve);
    });
  });
  const parallelB = hooks.runTsdCriticalOperation("test.parallel", function () {
    return new Promise(function (resolve) {
      setImmediate(resolve);
    });
  });
  assert.strictEqual(context.window.FlowStockTsdIsBusy(), true, "parallel ops must keep busy");
  assert.strictEqual(hooks.getTsdCriticalOperationDiagnostics().count, 2);
  assert.strictEqual(hooks.getTsdCriticalOperationDiagnostics().reasons["test.parallel"], 2);
  await parallelA;
  await parallelB;
  assert.strictEqual(context.window.FlowStockTsdIsBusy(), false, "busy must clear after parallel ops complete");

  console.log("TSD HU lookup scan pipeline tests passed.");
}

main().catch(function (error) {
  console.error(error);
  process.exit(1);
});
