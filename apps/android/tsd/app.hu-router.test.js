const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const appJs = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");
const storageJs = fs.readFileSync(path.join(__dirname, "storage.js"), "utf8");
const appVersionJs = fs.readFileSync(path.join(__dirname, "app-version.js"), "utf8");
const serviceWorkerJs = fs.readFileSync(path.join(__dirname, "service-worker.js"), "utf8");

const hooks = {};
const alerts = [];
const overlays = [];
let scannerHandler = null;
let scannerFocusCount = 0;
let scannerSetHandlerCount = 0;
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
      setItem: function () {},
      getItem: function () {
        return null;
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
    setInterval: function () {
      return 0;
    },
    addEventListener: function () {},
    FlowStockScanner: {
      createScannerManager: function () {
        return {
          setHandler: function (handler) {
            scannerHandler = handler;
            scannerSetHandlerCount += 1;
          },
          setErrorHandler: function () {},
          setMode: function () {},
          start: function () {},
          focus: function () {
            scannerFocusCount += 1;
          },
        };
      },
    },
  },
  document: {
    documentElement: null,
    activeElement: null,
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
      return id === "app" ? appEl : null;
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
    addEventListener: function () {},
    removeEventListener: function () {},
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

vm.createContext(context);
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
  assert(appVersionJs.includes('var version = "39"'));
  assert(serviceWorkerJs.includes('importScripts("./app-version.js")'));
  assert(serviceWorkerJs.includes('"./app.js"'));

  await hooks.initScannerManager();
  const handlerCallsBeforeHome = scannerSetHandlerCount;
  hooks.renderRouteInternal({ name: "home" });
  assert(scannerSetHandlerCount > handlerCallsBeforeHome, "home render should install a scanner-manager handler");
  assert.strictEqual(typeof scannerHandler, "function");
  assert(scannerFocusCount > 0, "home render should focus scanner through the post-render helper");

  scannerHandler({ value: "NOT-A-HU", source: "test" });
  await flushPromises();
  assert.strictEqual(resolveCalls, 0, "non-HU scan from home should not call resolver");

  const homeHash = context.window.location.hash;
  scannerHandler({ value: "HU-000123", source: "test" });
  await flushPromises();
  assert.strictEqual(resolveCalls, 1, "home scan should call apiResolveHu through scanner manager");
  assert.strictEqual(overlays.length, 0, "known HU should not show the choice overlay");
  assert.strictEqual(context.window.location.hash, "/hu/HU-000123", "known HU should navigate to HU card");
  assert.notStrictEqual(context.window.location.hash, homeHash, "known HU should leave the neutral route");
  assert.strictEqual(fillingScanCalls, 0);
  assert.strictEqual(fillingFillCalls, 0);
  assert.strictEqual(fillingMixedFillCalls, 0);
  assert.strictEqual(outboundScanCalls, 0);
  assert.strictEqual(outboundCompleteCalls, 0);

  context.window.location.hash = "#/home";
  alerts.length = 0;
  nextResolveResult = { known: false };
  scannerHandler({ value: "HU-999999", source: "test" });
  await flushPromises();
  assert.strictEqual(resolveCalls, 2, "unknown home scan should call apiResolveHu through scanner manager");
  assert.deepStrictEqual(alerts, ["HU неизвестен: HU-999999"]);
  assert.strictEqual(context.window.location.hash, "#/home", "unknown HU must not navigate to a HU card");

  overlays.length = 0;
  const resolveCallsBeforeOutbound = resolveCalls;
  hooks.renderOutboundPickingOrder(outboundOrder, {});
  assert.strictEqual(typeof scannerHandler, "function");
  scannerHandler({ value: "HU-000123", source: "test" });
  await flushPromises();
  assert.strictEqual(resolveCalls, resolveCallsBeforeOutbound, "outbound active handler must bypass global resolver");
  assert.strictEqual(outboundScanCalls, 1, "outbound active handler should receive physical scan");

  overlays.length = 0;
  const resolveCallsBeforeFilling = resolveCalls;
  hooks.renderFillingScanScreen(fillingContext, {});
  assert.strictEqual(typeof scannerHandler, "function");
  scannerHandler({ value: "HU-000123", source: "test" });
  await flushPromises();
  assert.strictEqual(resolveCalls, resolveCallsBeforeFilling, "filling active handler must bypass global resolver");
  assert.strictEqual(fillingScanCalls, 1, "filling active handler should receive physical scan");

  hooks.setScanHandler(null);
  context.window.location.hash = "#/hu/HU-000321";
  const huCardCallsBefore = huCardCalls;
  hooks.renderRouteInternal({ name: "huCard", id: "HU-000321" });
  await flushPromises();
  assert.strictEqual(huCardCalls, huCardCallsBefore + 1, "HU card route should load apiGetHuCard");
  assert.strictEqual(lastHuCardCode, "HU-000321");
  assert.match(appEl.innerHTML, /HU-000321/);
  assert.match(appEl.innerHTML, /Открыть заказ 005/);
  assert.strictEqual(huCardActionButtons.length, 1, "HU card should wire action buttons");
  huCardActionButtons[0].click();
  assert.strictEqual(context.window.location.hash, "/order/619", "HU card action should keep existing navigation behavior");

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
  assert.match(freeCardHtml, /Свободная HU/);
  assert.match(freeCardHtml, /MAIN/);
  assert.doesNotMatch(freeCardHtml, /data-hu-card-action/);

  console.log("TSD global HU-router scan pipeline tests passed.");
}

main().catch(function (error) {
  console.error(error);
  process.exit(1);
});
