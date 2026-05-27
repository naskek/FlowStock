const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const hooks = {};
const appEl = {
  innerHTML: "",
  querySelectorAll: function () {
    return [];
  },
};
let outboundClickHandler = null;
let outboundScanHandler = null;
let lastScanRequest = null;
let currentOutboundOrder = null;
const scanInputEl = {
  value: "",
  isConnected: true,
  focus: function () {},
  addEventListener: function (type, handler) {
    if (type === "keydown") {
      outboundScanHandler = handler;
    }
  },
};
const completeBtnEl = {
  disabled: false,
  addEventListener: function () {},
};

const context = {
  console,
  window: {
    FlowStockTsdTestHooks: hooks,
    location: { hash: "" },
    setTimeout: function (callback) {
      if (typeof callback === "function") {
        callback();
      }
      return 0;
    },
    clearTimeout: function () {},
    setInterval: function () {
      return 0;
    },
    addEventListener: function () {},
  },
  document: {
    getElementById: function (id) {
      if (id === "app") {
        return appEl;
      }
      if (id === "outboundPickingScanInput") {
        return scanInputEl;
      }
      if (id === "outboundPickingCompleteBtn") {
        return completeBtnEl;
      }
      return null;
    },
    querySelector: function () {
      return null;
    },
    querySelectorAll: function (selector) {
      if (selector !== "[data-outbound-order]") {
        return [];
      }
      return [
        {
          getAttribute: function (name) {
            return name === "data-outbound-order" ? "93" : "";
          },
          addEventListener: function (type, handler) {
            if (type === "click") {
              outboundClickHandler = handler;
            }
          },
        },
      ];
    },
    addEventListener: function () {},
  },
  localStorage: {
    setItem: function () {},
    getItem: function () {
      return null;
    },
  },
  navigator: {},
  TsdStorage: {
    normalizeOutboundPickingOrder: function (order) {
      return order;
    },
    apiScanOutboundPickingHu: function (orderId, huCode) {
      lastScanRequest = {
        orderId,
        huCode,
        url: "/api/tsd/outbound/orders/" + encodeURIComponent(orderId) + "/scan",
      };
      return Promise.resolve({
        message: "HU подобрана.",
        order: {
          ...currentOutboundOrder,
          picked_hu_count: 1,
        },
      });
    },
    apiGetOutboundPickingOrder: function () {
      return Promise.resolve(currentOutboundOrder);
    },
    apiCompleteOutboundPicking: function () {
      return Promise.resolve({ order: currentOutboundOrder });
    },
  },
};

context.window.document = context.document;
context.window.localStorage = context.localStorage;
context.window.navigator = context.navigator;
context.window.TsdStorage = context.TsdStorage;

vm.createContext(context);
vm.runInContext(fs.readFileSync(path.join(__dirname, "app.js"), "utf8"), context, {
  filename: "app.js",
});

const snakeCaseOrder = {
  order_id: 93,
  order_ref: "TSD-UI-20260527172818",
  partner_name: "Тестовый клиент",
  status: "Готов",
  expected_hu_count: 1,
  picked_hu_count: 0,
  is_complete: false,
  draft_outbound_doc_id: 123,
  draft_outbound_doc_ref: "OUT-2026-000123",
};

const listHtml = hooks.renderOutboundPickingList([snakeCaseOrder]);
assert.match(listHtml, /data-outbound-order="93"/);
assert.match(listHtml, /Заказ № TSD-UI-20260527172818/);
assert.match(listHtml, /Клиент: Тестовый клиент/);
assert.match(listHtml, /Подобрано <strong>0\/1<\/strong>/);

hooks.wireOutboundPickingList();
assert.strictEqual(typeof outboundClickHandler, "function");
outboundClickHandler();
assert.strictEqual(context.window.location.hash, "/outbound/93");

hooks.renderOutboundPickingOrder(
  {
    ...snakeCaseOrder,
    hus: [
      {
        hu_code: "HU-000001",
        item_summary: "Горчица, 1 шт",
        location_code: "FG-01",
        order_line_id: 252,
        lines: [
          {
            item_name: "Горчица",
            location_code: "FG-01",
            order_line_id: 252,
            qty: 1,
          },
        ],
      },
    ],
  },
  {}
);

assert.match(appEl.innerHTML, /Заказ: <strong>TSD-UI-20260527172818<\/strong>/);
assert.match(appEl.innerHTML, /Черновик OUTBOUND: <strong>OUT-2026-000123<\/strong>/);
assert.match(appEl.innerHTML, /HU-000001/);
assert.match(appEl.innerHTML, /Горчица, 1 шт/);
assert.match(appEl.innerHTML, /Горчица/);

const outboundScanOrder = {
  order_id: 95,
  order_ref: "003",
  partner_name: "Тестовый клиент",
  status: "Готов",
  expected_hu_count: 2,
  picked_hu_count: 0,
  is_complete: false,
  hus: [
    {
      hu_code: "HU-0000725",
      item_summary: "Товар, 600 шт",
      location_code: "MAIN",
      order_line_id: 501,
      qty: 600,
      lines: [
        {
          item_name: "Товар",
          location_code: "MAIN",
          order_line_id: 501,
          qty: 600,
        },
      ],
    },
    {
      hu_code: "HU-0000726",
      item_summary: "Товар, 600 шт",
      location_code: "MAIN",
      order_line_id: 501,
      qty: 600,
      lines: [
        {
          item_name: "Товар",
          location_code: "MAIN",
          order_line_id: 501,
          qty: 600,
        },
      ],
    },
  ],
};

assert.strictEqual(
  hooks.resolveOutboundPickingScannedHu("0000726", outboundScanOrder),
  "HU-0000726"
);
assert.strictEqual(
  hooks.resolveOutboundPickingScannedHu("prefix:HU-0000726:suffix", outboundScanOrder),
  "HU-0000726"
);

async function scanOutboundHu(rawValue) {
  currentOutboundOrder = outboundScanOrder;
  lastScanRequest = null;
  outboundScanHandler = null;
  hooks.renderOutboundPickingOrder(outboundScanOrder, {});
  assert.strictEqual(typeof outboundScanHandler, "function");
  scanInputEl.value = rawValue;
  outboundScanHandler({
    key: "Enter",
    preventDefault: function () {},
  });
  await Promise.resolve();
  await Promise.resolve();
  return lastScanRequest;
}

(async function () {
  let request = await scanOutboundHu("HU-0000726");
  assert.strictEqual(request.huCode, "HU-0000726");
  assert.strictEqual(request.orderId, 95);
  assert.strictEqual(request.url, "/api/tsd/outbound/orders/95/scan");

  request = await scanOutboundHu("0000726");
  assert.strictEqual(request.huCode, "HU-0000726");
  assert.strictEqual(request.url, "/api/tsd/outbound/orders/95/scan");

  request = await scanOutboundHu("prefix:HU-0000726:suffix");
  assert.strictEqual(request.huCode, "HU-0000726");
  assert.strictEqual(request.url, "/api/tsd/outbound/orders/95/scan");

  console.log("TSD outbound presentation tests passed.");
})().catch(function (error) {
  console.error(error);
  process.exitCode = 1;
});
