const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const corePath = path.join(__dirname, "pc-core.js");
const boardPath = path.join(__dirname, "warehouse-board.js");
const appPath = path.join(__dirname, "app.js");
const boardSource = fs.readFileSync(boardPath, "utf8");
const appSource = fs.readFileSync(appPath, "utf8");

const connectedModals = [];
const documentListeners = {};
let lastModal = null;

function addListener(store, type, handler) {
  if (!store[type]) store[type] = [];
  store[type].push(handler);
}

function removeListener(store, type, handler) {
  store[type] = (store[type] || []).filter(function (registered) { return registered !== handler; });
}

function createModal() {
  const listeners = {};
  const closeButton = {
    addEventListener: function (type, handler) {
      if (type === "click") this.click = handler;
    },
    click: function () {},
  };
  return {
    className: "",
    innerHTML: "",
    parentNode: null,
    isConnected: false,
    addEventListener: function (type, handler) { addListener(listeners, type, handler); },
    removeEventListener: function (type, handler) { removeListener(listeners, type, handler); },
    dispatchClick: function (target) {
      (listeners.click || []).slice().forEach(function (handler) { handler({ target: target }); });
    },
    querySelector: function (selector) {
      return selector === "[data-close-modal]" ? closeButton : null;
    },
    remove: function () {
      this.parentNode = null;
      this.isConnected = false;
      const index = connectedModals.indexOf(this);
      if (index >= 0) connectedModals.splice(index, 1);
    },
    closeButton,
    listenerCount: function (type) { return (listeners[type] || []).length; },
  };
}

const context = {
  window: {},
  console,
  Headers,
  localStorage: { getItem: () => null },
  fetch: function () {
    return Promise.resolve({
      ok: true,
      json: function () { return Promise.resolve({ bundles: [] }); },
    });
  },
  document: {
    createElement: function () {
      lastModal = createModal();
      return lastModal;
    },
    querySelectorAll: function (selector) {
      return selector === ".pc-modal" ? connectedModals.slice() : [];
    },
    addEventListener: function (type, handler) { addListener(documentListeners, type, handler); },
    removeEventListener: function (type, handler) { removeListener(documentListeners, type, handler); },
    body: {
      appendChild: function (element) {
        element.parentNode = this;
        element.isConnected = true;
        connectedModals.push(element);
      },
    },
  },
};
context.window.document = context.document;
vm.createContext(context);
vm.runInContext(fs.readFileSync(corePath, "utf8"), context, { filename: corePath });
vm.runInContext(boardSource, context, { filename: boardPath });

const board = context.window.FlowStockWarehouseBoard;
assert.ok(board, "FlowStockWarehouseBoard must be exported");

assert.strictEqual(board.statusLabel("SUBMITTED"), "На подтверждении");
assert.strictEqual(board.UI_LABELS.WHAT_TO_DO, "Что нужно сделать?");
assert.strictEqual(board.UI_LABELS.CARD_MOVE_TITLE, "Переместить паллету / HU");
assert.strictEqual(board.UI_LABELS.CARD_ADOPT_TITLE, "Перенести план паллет");
assert.strictEqual(board.UI_LABELS.EMPTY_PACKAGE, "Пакет пока пустой. Выберите действие сверху.");
assert.ok(boardSource.includes("Шаг 1"), "guided steps must be present");
assert.ok(boardSource.includes(board.UI_LABELS.SAFETY_HINT), "safety hint must be present");

assert.ok(!boardSource.includes("Создать MOVE_HU"), "no raw MOVE_HU button");
assert.ok(!boardSource.includes("Создать ADOPT"), "no raw ADOPT button");
assert.ok(!boardSource.includes("item_id="), "no raw item_id in UI strings");

assert.ok(
  appSource.includes("flowstock_experimental_warehouse_tasks"),
  "PC app must gate warehouse board behind experimental flag"
);
assert.ok(
  appSource.includes('views.push("warehouse-board")'),
  "warehouse-board registration must remain behind flag check"
);

board.init({
  escapeHtml: function (value) {
    return String(value || "");
  },
  bindModalDismiss: context.window.FlowStockPcCore.bindModalDismiss,
});

const html = board.render();
assert.ok(html.includes("wpScenarioPicker"), "render must include scenario picker shell");
assert.ok(html.includes(board.UI_LABELS.SAFETY_HINT), "render must include safety hint");

async function runBundlesModalRegression() {
  await board.testHooks.openBundlesModal();
  const explicitModal = lastModal;
  assert.ok(explicitModal.isConnected);
  explicitModal.closeButton.click();
  explicitModal.closeButton.click();
  assert.strictEqual(explicitModal.isConnected, false, "explicit close must use idempotent removal path");
  assert.strictEqual(explicitModal.listenerCount("click"), 0, "close must dispose overlay listener");

  await board.testHooks.openBundlesModal();
  const overlayModal = lastModal;
  overlayModal.dispatchClick({});
  assert.ok(overlayModal.isConnected, "click inside modal card must not close bundles modal");
  overlayModal.dispatchClick(overlayModal);
  assert.strictEqual(overlayModal.isConnected, false, "overlay click must close bundles modal");

  await board.testHooks.openBundlesModal();
  const escapeModal = lastModal;
  const escapeEvent = {
    key: "Escape",
    defaultPrevented: false,
    preventDefault: function () { this.defaultPrevented = true; },
  };
  (documentListeners.keydown || []).slice().forEach(function (handler) { handler(escapeEvent); });
  assert.strictEqual(escapeModal.isConnected, false, "Escape must close bundles modal");
  assert.strictEqual((documentListeners.keydown || []).length, 0, "Escape close must remove keydown listener");
}

runBundlesModalRegression().then(function () {
  console.log("warehouse-board.presentation.test.js: ok");
}).catch(function (error) {
  console.error(error);
  process.exitCode = 1;
});
