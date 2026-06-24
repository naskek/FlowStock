const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const appJs = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");
const hooks = {};
const appEl = { innerHTML: "", querySelectorAll: function () { return []; } };
let scanHandler = null;

const scanInput = {
  value: "",
  isConnected: true,
  focus: function () {},
  addEventListener: function (type, handler) {
    if (type === "keydown") {
      scanHandler = handler;
    }
  },
};

const completeButton = {
  disabled: false,
  listeners: {},
  addEventListener: function (type, handler) {
    this.listeners[type] = handler;
  },
};

const context = {
  console,
  window: {
    FlowStockTsdTestHooks: hooks,
    location: { hash: "" },
    setTimeout: function (callback) {
      if (typeof callback === "function") callback();
      return 0;
    },
    clearTimeout: function () {},
    setInterval: function () { return 0; },
    addEventListener: function () {},
  },
  document: {
    documentElement: { classList: { toggle: function () {} } },
    activeElement: null,
    body: { classList: { add: function () {}, remove: function () {} } },
    getElementById: function (id) {
      if (id === "app") return appEl;
      if (id === "orderControlScanInput") return scanInput;
      if (id === "orderControlCompleteBtn") return completeButton;
      return null;
    },
    querySelector: function () { return null; },
    querySelectorAll: function () { return []; },
    addEventListener: function () {},
    createElement: function () {
      return { classList: { add: function () {} }, addEventListener: function () {}, querySelector: function () { return null; } };
    },
  },
  localStorage: {
    getItem: function () { return null; },
    setItem: function () {},
  },
  sessionStorage: {
    getItem: function () { return null; },
    setItem: function () {},
    removeItem: function () {},
  },
  navigator: {},
  TsdStorage: {
    init: function () { return Promise.resolve(); },
    ensureDefaults: function () { return Promise.resolve(); },
    getSetting: function () { return Promise.resolve("TSD-1"); },
    normalizeOrderControlDetails: function (value) { return value; },
    apiGetOrderControlTask: function () { return Promise.resolve(sampleDetail); },
  },
};
context.window.document = context.document;
context.window.localStorage = context.localStorage;
context.window.sessionStorage = context.sessionStorage;
context.window.navigator = context.navigator;
context.window.TsdStorage = context.TsdStorage;

vm.createContext(context);
vm.runInContext(appJs, context, { filename: "app.js" });

const sampleDetail = {
  task: {
    id: 123,
    taskRef: "CTRL-2026-000123",
    status: "IN_EXECUTION",
    expectedHuCount: 3,
    checkedHuCount: 1,
    discrepancyHuCount: 1,
    orders: [{ orderRef: "080" }],
  },
  progress: {
    expectedHuCount: 3,
    checkedHuCount: 1,
    discrepancyHuCount: 1,
    canComplete: false,
  },
  hus: [
    {
      huCode: "HU-PENDING",
      status: "PENDING",
      itemSummary: "Горчица",
      lines: [{ itemName: "Горчица", qty: 600, locationCode: "FG-01" }],
    },
    {
      huCode: "HU-CHECKED",
      status: "CHECKED",
      itemSummary: "Микс-паллета",
      isMixedPallet: true,
      lines: [
        { itemName: "Товар A", qty: 10 },
        { itemName: "Товар B", qty: 5 },
      ],
    },
    {
      huCode: "HU-DISC",
      status: "DISCREPANCY",
      itemSummary: "Соус",
      message: "Нет ledger-остатка",
      lines: [{ itemName: "Соус", qty: 12, locationCode: "FG-02" }],
    },
  ],
};

assert.ok(hooks.renderOrderControlList, "renderOrderControlList hook should exist");
assert.ok(hooks.renderOrderControlTask, "renderOrderControlTask hook should exist");
assert.ok(hooks.renderOperationsMenu, "renderOperationsMenu hook should exist");
assert.ok(hooks.applyClientBlocks, "applyClientBlocks hook should exist");
assert.ok(hooks.getOrderControlStatusInfo, "getOrderControlStatusInfo hook should exist");
assert.ok(hooks.sortOrderControlHus, "sortOrderControlHus hook should exist");

function countOccurrences(value, needle) {
  return (String(value).match(new RegExp(needle.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "g")) || []).length;
}

const homeHtml = hooks.renderHome();
assert.ok(!homeHtml.includes("Контроль заказов"), "home should not render order control tile");
assert.ok(!homeHtml.includes('data-route="order-control"'), "home should not link directly to order control");

let operationsHtml = hooks.renderOperationsMenu();
assert.ok(operationsHtml.includes("Контроль заказов"), "operations should render order control tile");
assert.ok(operationsHtml.includes('data-route="order-control"'), "operations should link to order control");

hooks.applyClientBlocks({ tsd_order_control: false });
operationsHtml = hooks.renderOperationsMenu();
assert.ok(!operationsHtml.includes("Контроль заказов"), "order control tile should honor tsd_order_control block");
assert.ok(!operationsHtml.includes('data-route="order-control"'), "disabled order control block should remove route");
hooks.applyClientBlocks({});

assert.strictEqual(hooks.getBackRouteForRoute({ name: "orderControl" }, ""), "/operations");
assert.strictEqual(hooks.getBackRouteForRoute({ name: "orderControlTask", id: 123 }, ""), "/order-control");
assert.deepStrictEqual(
  [
    hooks.getOrderControlStatusInfo("PENDING").label,
    hooks.getOrderControlStatusInfo("CHECKED").label,
    hooks.getOrderControlStatusInfo("DISCREPANCY").label,
  ],
  ["Ожидает", "Проверена", "Расхождение"]
);
assert.deepStrictEqual(
  hooks.sortOrderControlHus(sampleDetail.hus).map(function (hu) { return hu.huCode; }),
  ["HU-DISC", "HU-PENDING", "HU-CHECKED"],
  "HUs should sort discrepancy, pending, checked"
);

const listHtml = hooks.renderOrderControlList([sampleDetail.task]);
assert.ok(listHtml.includes("CTRL-2026-000123"));
assert.ok(listHtml.includes("1/3"));
assert.ok(listHtml.includes("В работе"));
assert.ok(listHtml.includes("Расхождения"));
assert.ok(!listHtml.includes("IN_EXECUTION"));

hooks.renderOrderControlTask(sampleDetail, {
  message: "HU не входит в задание контроля.",
  messageType: "error",
});
assert.ok(appEl.innerHTML.includes("Контроль · Заказ 080 · 1 / 3 паллет"));
assert.ok(appEl.innerHTML.includes("outbound-picking-progress"), "task screen should render the canonical 4-counter block");
assert.ok(appEl.innerHTML.includes("Ожидается <strong>3</strong>"));
assert.ok(appEl.innerHTML.includes("Проверено <strong>1</strong>"));
assert.ok(appEl.innerHTML.includes("Осталось <strong>2</strong>"));
assert.ok(appEl.innerHTML.includes("Расхождения <strong>1</strong>"));
assert.ok(appEl.innerHTML.includes("filling-scan-slot"), "task screen should use the canonical scan slot");
assert.ok(appEl.innerHTML.includes('id="orderControlScanInput"'));
assert.ok(!appEl.innerHTML.includes("Сканируйте HU"), "task screen should drop the custom scan block title");
assert.ok(appEl.innerHTML.includes("HU не входит в задание контроля."));
assert.ok(appEl.innerHTML.includes("HU-CHECKED"));
assert.ok(appEl.innerHTML.includes("filling-pallet-item--compact"), "HU rows should be compact");
assert.ok(appEl.innerHTML.includes("filling-pallet-dot"), "HU rows should use the canonical status dot");
assert.ok(appEl.innerHTML.includes("is-discrepancy"), "discrepancy HU should use error tone");
assert.ok(appEl.innerHTML.includes("is-pending"), "pending HU should use pending tone");
assert.ok(appEl.innerHTML.includes("is-filled"), "checked HU should use filled tone");
assert.ok(appEl.innerHTML.includes("Товар A"));
assert.ok(appEl.innerHTML.includes("Смешанная HU · 2 позиций"));
assert.strictEqual(countOccurrences(appEl.innerHTML, "Горчица"), 1, "normal HU should not duplicate item summary and line");
assert.strictEqual(countOccurrences(appEl.innerHTML, "Микс-паллета"), 0, "mixed HU summary should not duplicate component list");
assert.ok(!appEl.innerHTML.includes("10 шт"));
assert.ok(!appEl.innerHTML.includes("5 шт"));
assert.ok(!appEl.innerHTML.includes(">PENDING<"));
assert.ok(!appEl.innerHTML.includes(">CHECKED<"));
assert.ok(!appEl.innerHTML.includes(">DISCREPANCY<"));
assert.ok(
  appEl.innerHTML.indexOf("HU-DISC") < appEl.innerHTML.indexOf("HU-PENDING") &&
    appEl.innerHTML.indexOf("HU-PENDING") < appEl.innerHTML.indexOf("HU-CHECKED"),
  "task screen should render discrepancy, pending, checked order"
);
assert.ok(appEl.innerHTML.includes("actions-bar order-control-action-bar"));
assert.ok(appEl.innerHTML.includes("disabled"));
assert.ok(scanHandler, "scan input should be wired");

console.log("app.order-control.test.js passed");
