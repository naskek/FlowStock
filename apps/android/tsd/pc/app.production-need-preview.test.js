const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const corePath = path.join(__dirname, "pc-core.js");
const authPath = path.join(__dirname, "pc-auth.js");
const orderModalPath = path.join(__dirname, "pc-order-modal.js");
const catalogPath = path.join(__dirname, "pc-catalog.js");
const stockPath = path.join(__dirname, "pc-stock.js");
const appPath = path.join(__dirname, "app.js");
const hooks = {};
let lastModal = null;
let lastAlert = "";
const connectedModals = [];
const documentListeners = {};

function addListener(store, type, handler) {
  if (!store[type]) store[type] = [];
  store[type].push(handler);
}

function removeListener(store, type, handler) {
  if (!store[type]) return;
  store[type] = store[type].filter(function (registered) { return registered !== handler; });
}

function dispatchDocument(type, event) {
  (documentListeners[type] || []).slice().forEach(function (handler) { handler(event); });
}

function createEscapeEvent() {
  return {
    key: "Escape",
    defaultPrevented: false,
    preventDefault: function () { this.defaultPrevented = true; },
  };
}

function createModalElement() {
  const buttons = {};
  const inputs = {};
  const listeners = {};
  const modal = {
    className: "",
    parentNode: null,
    isConnected: false,
    _innerHTML: "",
    buttons,
    inputs,
    set innerHTML(value) {
      this._innerHTML = String(value || "");
      const inputRegex = /data-preview-index="([^"]*)" value="([^"]*)"/g;
      let match;
      while ((match = inputRegex.exec(this._innerHTML)) !== null) {
        inputs[match[1]] = { value: match[2] };
      }
    },
    get innerHTML() {
      return this._innerHTML;
    },
    addEventListener: function (type, handler) {
      addListener(listeners, type, handler);
    },
    removeEventListener: function (type, handler) {
      removeListener(listeners, type, handler);
    },
    dispatch: function (type, event) {
      (listeners[type] || []).slice().forEach(function (handler) { handler(event); });
    },
    listenerCount: function (type) {
      return (listeners[type] || []).length;
    },
    querySelector: function (selector) {
      const previewMatch = String(selector || "").match(/^\[data-preview-index="([^"]+)"\]$/);
      if (previewMatch) {
        return inputs[previewMatch[1]] || null;
      }
      if (String(selector || "").indexOf("#") === 0) {
        const id = selector.slice(1);
        if (!buttons[id]) {
          buttons[id] = {
            addEventListener: function (type, handler) {
              if (type === "click") {
                this.click = handler;
              }
            },
            click: function () {},
          };
        }
        return buttons[id];
      }
      return null;
    },
  };
  return modal;
}

const context = {
  console,
  window: {
    FlowStockPcTestHooks: hooks,
    alert: function (message) {
      lastAlert = String(message || "");
    },
  },
  document: {
    getElementById: function () {
      return null;
    },
    querySelectorAll: function (selector) {
      return selector === ".pc-modal" ? connectedModals.slice() : [];
    },
    querySelector: function () {
      return null;
    },
    createElement: function () {
      lastModal = createModalElement();
      return lastModal;
    },
    addEventListener: function (type, handler) {
      addListener(documentListeners, type, handler);
    },
    removeEventListener: function (type, handler) {
      removeListener(documentListeners, type, handler);
    },
    body: {
      appendChild: function (element) {
        element.parentNode = this;
        element.isConnected = true;
        connectedModals.push(element);
      },
      removeChild: function (element) {
        element.parentNode = null;
        element.isConnected = false;
        const index = connectedModals.indexOf(element);
        if (index >= 0) connectedModals.splice(index, 1);
      },
    },
  },
};

context.window.document = context.document;
vm.createContext(context);
vm.runInContext(fs.readFileSync(corePath, "utf8"), context, { filename: corePath });
vm.runInContext(fs.readFileSync(authPath, "utf8"), context, { filename: authPath });
vm.runInContext(fs.readFileSync(orderModalPath, "utf8"), context, { filename: orderModalPath });
vm.runInContext(fs.readFileSync(catalogPath, "utf8"), context, { filename: catalogPath });
vm.runInContext(fs.readFileSync(stockPath, "utf8"), context, { filename: stockPath });
vm.runInContext(fs.readFileSync(appPath, "utf8"), context, { filename: appPath });

const source = fs.readFileSync(appPath, "utf8");
assert.strictEqual(
  (source.match(/data-preview-index="' \+ previewIndex \+ '"/g) || []).length,
  1,
  "shared production need preview renderer must use a safe numeric previewIndex"
);
assert.doesNotMatch(source, /data-preview-index="' \+ escapeHtml\(index\) \+ '"/);

let confirmedRows = null;
hooks.openProductionNeedPreviewModal(
  [
    {
      itemId: 34,
      itemName: "Горчица 200 гр",
      gtin: "04607186951520",
      reason: "Пополнение склада до минимального остатка.",
      qtyToCreate: 5472,
    },
  ],
  function (requestRows) {
    confirmedRows = requestRows;
  }
);

assert.ok(lastModal);
assert.match(lastModal.innerHTML, /Предпросмотр внутреннего заказа/);
assert.doesNotMatch(lastModal.innerHTML, /Причина/);
assert.match(lastModal.innerHTML, />Создать заказ<\/button>/);
assert.doesNotMatch(lastModal.innerHTML, />Подтвердить<\/button>/);
assert.match(lastModal.innerHTML, />×<\/button>/);
assert.match(lastModal.innerHTML, /data-preview-index="0"/);
assert.strictEqual(lastModal.inputs["0"].value, "5472");
lastModal.buttons.productionNeedPreviewConfirmBtn.click();
assert.ok(Array.isArray(confirmedRows));
assert.strictEqual(confirmedRows.length, 1);
assert.strictEqual(confirmedRows[0].item_id, 34);
assert.strictEqual(confirmedRows[0].qty_ordered, 5472);
assert.strictEqual(lastAlert, "");

let cancelCount = 0;
hooks.openProductionNeedPreviewModal(
  [{ itemId: 34, itemName: "Горчица 200 гр", gtin: "04607186951520", qtyToCreate: 1 }],
  function () {},
  function () { cancelCount += 1; }
);
const cancelModal = lastModal;
cancelModal.buttons.productionNeedPreviewCancelBtn.click();
assert.strictEqual(cancelModal.parentNode, null);
assert.strictEqual(cancelCount, 1, "cancel button must use the existing cancel flow");

hooks.openProductionNeedPreviewModal(
  [{ itemId: 34, itemName: "Горчица 200 гр", gtin: "04607186951520", qtyToCreate: 1 }],
  function () {},
  function () { cancelCount += 1; }
);
const closeModal = lastModal;
closeModal.buttons.productionNeedPreviewCloseBtn.click();
assert.strictEqual(closeModal.parentNode, null);
assert.strictEqual(cancelCount, 2, "close icon must use the existing cancel flow");

hooks.openProductionNeedPreviewModal(
  [{ itemId: 34, itemName: "Горчица 200 гр", gtin: "04607186951520", qtyToCreate: 1 }],
  function () {},
  function () { cancelCount += 1; }
);
const escapeModal = lastModal;
const keydownListenersBeforeEscape = (documentListeners.keydown || []).length;
dispatchDocument("keydown", createEscapeEvent());
assert.strictEqual(escapeModal.parentNode, null);
assert.strictEqual(cancelCount, 3, "Escape must use the existing cancel flow exactly once");
assert.strictEqual(
  (documentListeners.keydown || []).length,
  keydownListenersBeforeEscape - 1,
  "Escape close must remove its keydown listener"
);
escapeModal.buttons.productionNeedPreviewCloseBtn.click();
assert.strictEqual(cancelCount, 3, "repeated close must not call onCancel twice");

hooks.openProductionNeedPreviewModal(
  [{ itemId: 34, itemName: "Горчица 200 гр", gtin: "04607186951520", qtyToCreate: 1 }],
  function () {},
  function () { cancelCount += 1; }
);
const overlayModal = lastModal;
overlayModal.dispatch("click", { target: {} });
assert.notStrictEqual(overlayModal.parentNode, null, "click inside modal card must not close the overlay");
overlayModal.dispatch("click", { target: overlayModal });
assert.strictEqual(overlayModal.parentNode, null, "click directly on overlay must close the modal");
assert.strictEqual(cancelCount, 4);
assert.strictEqual(overlayModal.listenerCount("click"), 0, "overlay listener must be removed on close");

let confirmCount = 0;
let confirmCancelCount = 0;
hooks.openProductionNeedPreviewModal(
  [{ itemId: 34, itemName: "Горчица 200 гр", gtin: "04607186951520", qtyToCreate: 1 }],
  function () { confirmCount += 1; },
  function () { confirmCancelCount += 1; }
);
const confirmedModal = lastModal;
confirmedModal.buttons.productionNeedPreviewConfirmBtn.click();
confirmedModal.buttons.productionNeedPreviewCloseBtn.click();
assert.strictEqual(confirmCount, 1, "confirm callback must retain its existing single-call semantics");
assert.strictEqual(confirmCancelCount, 0, "confirmed preview must never call onCancel");

console.log("app.production-need-preview.test.js: ok");
