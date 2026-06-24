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

let scanBehavior = null;
let resolveBehavior = null;
let getBehavior = null;
const scanCalls = [];
const resolveCalls = [];
let getCalls = 0;

const context = {
  console: { log: console.log, info: function () {}, warn: function () {}, error: function () {} },
  window: {
    FlowStockTsdTestHooks: hooks,
    location: { hash: "#/order-control/123" },
    setTimeout: function (cb) { if (typeof cb === "function") cb(); return 0; },
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
  localStorage: { getItem: function () { return null; }, setItem: function () {} },
  sessionStorage: { getItem: function () { return null; }, setItem: function () {}, removeItem: function () {} },
  navigator: {},
  TsdStorage: {
    init: function () { return Promise.resolve(); },
    ensureDefaults: function () { return Promise.resolve(); },
    getSetting: function () { return Promise.resolve("TSD-1"); },
    normalizeOrderControlDetails: function (value) { return value; },
    apiScanOrderControlHu: function (taskId, huCode) {
      scanCalls.push({ taskId: taskId, huCode: huCode });
      return scanBehavior ? scanBehavior(taskId, huCode) : Promise.resolve({ message: "ok" });
    },
    apiResolveHu: function (raw) {
      resolveCalls.push(raw);
      return resolveBehavior ? resolveBehavior(raw) : Promise.resolve({ known: false });
    },
    apiGetOrderControlTask: function (taskId) {
      getCalls += 1;
      return getBehavior ? getBehavior(taskId) : Promise.resolve(baseDetail());
    },
    apiCompleteOrderControlTask: function () { return Promise.resolve({}); },
  },
};
context.window.document = context.document;
context.window.localStorage = context.localStorage;
context.window.sessionStorage = context.sessionStorage;
context.window.navigator = context.navigator;
context.window.TsdStorage = context.TsdStorage;

vm.createContext(context);
vm.runInContext(appJs, context, { filename: "app.js" });

function baseDetail() {
  return {
    task: { id: 123, taskRef: "CTRL-2026-000123", status: "IN_EXECUTION", orders: [{ orderRef: "080" }] },
    progress: { expectedHuCount: 2, checkedHuCount: 0, discrepancyHuCount: 0, pendingHuCount: 2, canComplete: false },
    hus: [
      { huCode: "HU-0000001", status: "PENDING", itemSummary: "Горчица", lines: [{ itemName: "Горчица", qty: 600, locationCode: "FG-01" }] },
      { huCode: "HU-0000002", status: "PENDING", itemSummary: "Соус", lines: [{ itemName: "Соус", qty: 12, locationCode: "FG-02" }] },
    ],
  };
}

function checkedOneDetail() {
  const d = baseDetail();
  d.progress.checkedHuCount = 1;
  d.progress.pendingHuCount = 1;
  d.hus[0].status = "CHECKED";
  return d;
}

function flush() {
  return new Promise(function (resolve) { setImmediate(resolve); });
}
async function settle() {
  for (let i = 0; i < 6; i += 1) {
    await flush();
  }
}

function scan(value) {
  scanInput.value = value;
  assert.ok(typeof scanHandler === "function", "scan handler should be wired before scanning");
  scanHandler({ key: "Enter", preventDefault: function () {} });
}

function resetRecorders() {
  scanCalls.length = 0;
  resolveCalls.length = 0;
  getCalls = 0;
  scanBehavior = null;
  resolveBehavior = null;
  getBehavior = null;
}

function startTask() {
  hooks.setCurrentRoute({ name: "orderControlTask", id: 123 });
  hooks.renderOrderControlTask(baseDetail(), { message: "", messageType: "" });
}

(async function run() {
  assert.ok(hooks.resolveOrderControlScannedHu, "resolveOrderControlScannedHu hook should exist");
  assert.ok(hooks.isValidOrderControlDetail, "isValidOrderControlDetail hook should exist");

  // Unit: resolver matches task HU / rejects foreign.
  const d = baseDetail();
  assert.strictEqual(hooks.resolveOrderControlScannedHu("HU-0000001", d), "HU-0000001");
  assert.strictEqual(hooks.resolveOrderControlScannedHu("  hu0000001 ", d), "HU-0000001");
  assert.strictEqual(hooks.resolveOrderControlScannedHu("PALLET HU-0000002 END", d), "HU-0000002");
  assert.strictEqual(hooks.resolveOrderControlScannedHu("HU-0009999", d), "", "foreign HU should not resolve to a task HU");
  assert.strictEqual(hooks.isValidOrderControlDetail(baseDetail(), 123), true);
  assert.strictEqual(hooks.isValidOrderControlDetail({ task: { id: 999, taskRef: "X" }, hus: [] }, 123), false);
  assert.strictEqual(hooks.isValidOrderControlDetail({ task: { id: 123, taskRef: "" }, hus: [] }, 123), false);
  assert.strictEqual(hooks.isValidOrderControlDetail({ task: { id: 123, taskRef: "X" } }, 123), false);

  // 1) exact HU passes and is sent verbatim.
  resetRecorders();
  startTask();
  scanBehavior = function () { return Promise.resolve({ message: "HU проверена." }); };
  getBehavior = function () { return Promise.resolve(checkedOneDetail()); };
  scan("HU-0000001");
  await settle();
  assert.strictEqual(scanCalls.length, 1, "exact HU should POST once");
  assert.strictEqual(scanCalls[0].huCode, "HU-0000001", "canonical task HU should be sent");
  assert.strictEqual(resolveCalls.length, 0, "exact task HU should not need apiResolveHu");
  assert.ok(appEl.innerHTML.includes("Проверено <strong>1</strong>"), "screen should reflect reloaded detail");

  // 2) raw barcode resolves via apiResolveHu to the expected HU.
  resetRecorders();
  startTask();
  resolveBehavior = function () { return Promise.resolve({ known: true, huCode: "HU-0000002" }); };
  scanBehavior = function () { return Promise.resolve({ message: "ok" }); };
  getBehavior = function () { return Promise.resolve(baseDetail()); };
  scan("46071860001234");
  await settle();
  assert.strictEqual(resolveCalls.length, 1, "barcode should be resolved through apiResolveHu");
  assert.strictEqual(resolveCalls[0], "46071860001234", "raw value should be passed to resolver");
  assert.strictEqual(scanCalls.length, 1);
  assert.strictEqual(scanCalls[0].huCode, "HU-0000002", "resolved canonical HU should be sent");

  // 3) extracted HU passes without server resolve.
  resetRecorders();
  startTask();
  scanBehavior = function () { return Promise.resolve({ message: "ok" }); };
  getBehavior = function () { return Promise.resolve(baseDetail()); };
  scan("  hu-0000002  ");
  await settle();
  assert.strictEqual(resolveCalls.length, 0, "extracted task HU should not call resolver");
  assert.strictEqual(scanCalls[0].huCode, "HU-0000002");

  // 4) foreign canonical HU is sent and yields HU_NOT_IN_TASK; detail preserved.
  resetRecorders();
  startTask();
  resolveBehavior = function () { return Promise.resolve({ known: true, huCode: "HU-0009999" }); };
  scanBehavior = function () {
    return Promise.reject({ status: 400, code: "HU_NOT_IN_TASK", message: "HU не входит в задание контроля." });
  };
  getBehavior = function () { return Promise.resolve(baseDetail()); };
  scan("HU-0009999");
  await settle();
  assert.strictEqual(scanCalls[0].huCode, "HU-0009999", "foreign HU should be sent verbatim, not swapped");
  // 5 + 6) after 400: GET detail is called and header/progress/HU list preserved.
  assert.ok(getCalls >= 1, "GET detail should run after rejected scan");
  assert.ok(appEl.innerHTML.includes("Контроль · Заказ 080 · 0 / 2 паллет"), "task header should remain");
  assert.ok(appEl.innerHTML.includes("Ожидается <strong>2</strong>"), "progress should not collapse");
  assert.ok(appEl.innerHTML.includes("HU-0000001") && appEl.innerHTML.includes("HU-0000002"), "HU list should stay visible");
  assert.ok(appEl.innerHTML.includes("HU не входит в задание контроля."), "error message should be shown");
  // 8) error must not collapse the screen to 0/0 or a dashed header.
  assert.ok(!appEl.innerHTML.includes("0 / 0"), "screen must not become 0 / 0");
  assert.ok(!appEl.innerHTML.includes("Контроль · - ·"), "header must not collapse to a dash");
  assert.ok(!appEl.innerHTML.includes("Нет HU в задании"), "HU list must not become empty");

  // 7) if GET detail fails, previous detail is kept (not empty object).
  resetRecorders();
  startTask();
  resolveBehavior = function () { return Promise.resolve({ known: true, huCode: "HU-0009999" }); };
  scanBehavior = function () { return Promise.reject({ status: 400, code: "HU_NOT_IN_TASK" }); };
  getBehavior = function () { return Promise.reject(new Error("NETWORK")); };
  scan("HU-0009999");
  await settle();
  assert.ok(appEl.innerHTML.includes("Контроль · Заказ 080 · 0 / 2 паллет"), "failed GET should fall back to previous detail");
  assert.ok(appEl.innerHTML.includes("HU-0000001"), "HU list should remain after failed GET");
  assert.ok(!appEl.innerHTML.includes("Нет HU в задании"), "screen should not show empty state");

  // 9) next valid scan after an error still works.
  resetRecorders();
  scanBehavior = function () { return Promise.resolve({ message: "HU проверена." }); };
  getBehavior = function () { return Promise.resolve(checkedOneDetail()); };
  scan("HU-0000001");
  await settle();
  assert.strictEqual(scanCalls.length, 1, "next scan after error should POST");
  assert.strictEqual(scanCalls[0].huCode, "HU-0000001");

  // 10) two fast scanner events create a single POST.
  resetRecorders();
  startTask();
  let resolveScan;
  scanBehavior = function () { return new Promise(function (res) { resolveScan = res; }); };
  getBehavior = function () { return Promise.resolve(checkedOneDetail()); };
  scan("HU-0000001");
  scan("HU-0000001");
  await settle();
  assert.strictEqual(scanCalls.length, 1, "second concurrent scan must be ignored while the first POST is in flight");
  resolveScan({ message: "ok" });
  await settle();
  assert.strictEqual(scanCalls.length, 1, "still a single POST after the in-flight scan settles");

  // Navigation away during a scan must not restore the old task screen.
  resetRecorders();
  startTask();
  let resolveScan2;
  scanBehavior = function () { return new Promise(function (res) { resolveScan2 = res; }); };
  getBehavior = function () { return Promise.resolve(baseDetail()); };
  scan("HU-0000001");
  await settle();
  appEl.innerHTML = "OTHER-SCREEN";
  hooks.setCurrentRoute({ name: "home" });
  resolveScan2({ message: "ok" });
  await settle();
  assert.strictEqual(appEl.innerHTML, "OTHER-SCREEN", "stale scan result must not redraw the previous task");

  console.log("app.order-control-scan.test.js passed");
})().catch(function (error) {
  console.error(error && error.stack ? error.stack : error);
  process.exit(1);
});
