const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const corePath = path.join(__dirname, "pc-core.js");
const authPath = path.join(__dirname, "pc-auth.js");
const orderModalPath = path.join(__dirname, "pc-order-modal.js");
const appPath = path.join(__dirname, "app.js");
const hooks = {};
let lastModal = null;
let lastAlert = "";

function createModalElement() {
  const buttons = {};
  const inputs = {};
  const modal = {
    className: "",
    parentNode: null,
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
    querySelectorAll: function () {
      return [];
    },
    querySelector: function () {
      return null;
    },
    createElement: function () {
      lastModal = createModalElement();
      return lastModal;
    },
    body: {
      appendChild: function (element) {
        element.parentNode = this;
      },
      removeChild: function (element) {
        element.parentNode = null;
      },
    },
  },
};

context.window.document = context.document;
vm.createContext(context);
vm.runInContext(fs.readFileSync(corePath, "utf8"), context, { filename: corePath });
vm.runInContext(fs.readFileSync(authPath, "utf8"), context, { filename: authPath });
vm.runInContext(fs.readFileSync(orderModalPath, "utf8"), context, { filename: orderModalPath });
vm.runInContext(fs.readFileSync(appPath, "utf8"), context, { filename: appPath });

const source = fs.readFileSync(appPath, "utf8");
assert.strictEqual(
  (source.match(/data-preview-index="' \+ previewIndex \+ '"/g) || []).length,
  2,
  "both production need preview modal blocks must use a safe numeric previewIndex"
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
assert.match(lastModal.innerHTML, /data-preview-index="0"/);
assert.strictEqual(lastModal.inputs["0"].value, "5472");
lastModal.buttons.productionNeedPreviewConfirmBtn.click();
assert.ok(Array.isArray(confirmedRows));
assert.strictEqual(confirmedRows.length, 1);
assert.strictEqual(confirmedRows[0].item_id, 34);
assert.strictEqual(confirmedRows[0].qty_ordered, 5472);
assert.strictEqual(lastAlert, "");

console.log("app.production-need-preview.test.js: ok");
