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
  addEventListener: function () {},
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
    expectedHuCount: 2,
    checkedHuCount: 1,
    discrepancyHuCount: 0,
    orders: [{ orderRef: "080" }],
  },
  progress: {
    expectedHuCount: 2,
    checkedHuCount: 1,
    discrepancyHuCount: 0,
    canComplete: false,
  },
  hus: [
    { huCode: "HU-0000507", status: "PENDING", itemSummary: "Горчица", qty: 10, lines: [] },
    {
      huCode: "HU-0000506",
      status: "CHECKED",
      itemSummary: "Микс-паллета",
      qty: 15,
      lines: [
        { itemName: "Товар A", qty: 10 },
        { itemName: "Товар B", qty: 5 },
      ],
    },
  ],
};

assert.ok(hooks.renderOrderControlList, "renderOrderControlList hook should exist");
assert.ok(hooks.renderOrderControlTask, "renderOrderControlTask hook should exist");

const listHtml = hooks.renderOrderControlList([sampleDetail.task]);
assert.ok(listHtml.includes("CTRL-2026-000123"));
assert.ok(listHtml.includes("1/2"));

hooks.renderOrderControlTask(sampleDetail, {
  message: "HU не входит в задание контроля.",
  messageType: "error",
});
assert.ok(appEl.innerHTML.includes("Контроль CTRL-2026-000123"));
assert.ok(appEl.innerHTML.includes("HU не входит в задание контроля."));
assert.ok(appEl.innerHTML.includes("HU-0000506"));
assert.ok(appEl.innerHTML.includes("Товар A"));
assert.ok(appEl.innerHTML.includes("disabled"));
assert.ok(scanHandler, "scan input should be wired");

console.log("app.order-control.test.js passed");
