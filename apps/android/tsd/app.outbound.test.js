const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const appJs = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");

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
  disabled: false,
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
vm.runInContext(appJs, context, {
  filename: "app.js",
});

function extractFunctionBody(source, name) {
  const marker = `function ${name}(`;
  const start = source.indexOf(marker);
  assert.notStrictEqual(start, -1, `${name} should exist`);
  const braceStart = source.indexOf("{", start);
  let depth = 0;
  for (let i = braceStart; i < source.length; i += 1) {
    if (source[i] === "{") {
      depth += 1;
    } else if (source[i] === "}") {
      depth -= 1;
      if (depth === 0) {
        return source.slice(braceStart + 1, i);
      }
    }
  }
  throw new Error(`${name} body was not closed`);
}

const renderOutboundPickingOrderBody = extractFunctionBody(appJs, "renderOutboundPickingOrder");

assert(
  appJs.includes("buildOutboundPickingHuGroups") &&
    appJs.includes("tsd-scan-input-hidden") &&
    appJs.includes("filling-pallet-list--scroll-breathing") &&
    appJs.includes("buildOutboundPickingHeaderLine(order)"),
  "outbound screen should reuse grouped HU list and compact scan header pattern from filling"
);
assert(
  !renderOutboundPickingOrderBody.includes("filling-scan-hint"),
  "outbound scan screen should not render visible scanner hint text"
);
assert(
  !renderOutboundPickingOrderBody.includes("Завершить подбор"),
  "outbound scan screen should not render manual complete button"
);
assert(
  appJs.includes("var showTsdBelowMinimumEntry = false"),
  "home below-minimum entry should be gated by a frontend-only flag"
);
const homeHtml = hooks.renderHome();
assert.doesNotMatch(homeHtml, /Позиции ниже минимума|homeLowStockWrap/);
assert.match(homeHtml, /menu-grid/, "home screen should still render main menu");
assert(
  !appJs.includes("escapeHtml(getOutboundPickingStatusLabel(status))"),
  "outbound HU list should not render textual status labels"
);

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

const outboundHuFixtures = [
  {
    hu_code: "HU-0000753",
    item_summary: "Горчица Печагин, 200 гр, 1 шт",
    status: "PENDING",
    lines: [{ item_name: "Горчица Печагин, 200 гр", qty: 1 }],
  },
  {
    hu_code: "HU-0000745",
    item_summary: "Аджика Печагин, 200 гр, 1 шт",
    status: "PENDING",
    lines: [{ item_name: "Аджика Печагин, 200 гр", qty: 1 }],
  },
  {
    hu_code: "HU-0000742",
    item_summary: "Аджика Печагин, 200 гр, 1 шт",
    status: "PICKED",
    lines: [{ item_name: "Аджика Печагин, 200 гр", qty: 1 }],
  },
  {
    hu_code: "HU-0000752",
    item_summary: "Горчица Печагин, 200 гр, 1 шт",
    status: "PENDING",
    lines: [{ item_name: "Горчица Печагин, 200 гр", qty: 1 }],
  },
];

const outboundHuGroups = hooks.buildOutboundPickingHuGroups(outboundHuFixtures);
assert.strictEqual(outboundHuGroups.length, 2);
assert.ok(
  outboundHuGroups[0].label.indexOf("Аджика") === 0 &&
    outboundHuGroups[1].label.indexOf("Горчица") === 0,
  "outbound HU groups should sort product titles ascending"
);
assert.strictEqual(outboundHuGroups[0].hus[0].huCode, "HU-0000742");
assert.strictEqual(outboundHuGroups[0].hus[1].huCode, "HU-0000745");
assert.strictEqual(outboundHuGroups[1].hus[0].huCode, "HU-0000752");
assert.strictEqual(outboundHuGroups[1].hus[1].huCode, "HU-0000753");

const outboundHuListHtml = hooks.renderOutboundPickingHuList(outboundHuFixtures);
assert.match(outboundHuListHtml, /filling-pallet-group-title/);
assert.match(outboundHuListHtml, /Аджика Печагин, 200 гр/);
assert.match(outboundHuListHtml, /HU-0000742/);
assert.doesNotMatch(outboundHuListHtml, /filling-pallet-item-name/);
assert.doesNotMatch(outboundHuListHtml, /filling-pallet-status-text/);
assert.doesNotMatch(outboundHuListHtml, />Подобрано</);
assert.doesNotMatch(outboundHuListHtml, />Ожидает</);
assert.doesNotMatch(outboundHuListHtml, /Не отгружена|Не отобрана|Отгружена|Отобрана/);
assert.match(outboundHuListHtml, /is-filled/);
assert.match(outboundHuListHtml, /is-pending/);
assert.match(outboundHuListHtml, /filling-pallet-list--scroll-breathing/);

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

assert.match(
  appEl.innerHTML,
  /Отгрузка · Заказ TSD-UI-20260527172818 · 0 \/ 1 паллет/
);
assert.doesNotMatch(
  appEl.innerHTML,
  /Отобрано паллет:|Отгружено паллет:|Сканируйте HU \/ паллетный штрихкод|Завершить подбор/
);
assert.doesNotMatch(appEl.innerHTML, /Черновик OUTBOUND|OUT-2026-000123/);
assert.match(appEl.innerHTML, /id="outboundPickingScanInput"/);
assert.match(appEl.innerHTML, /tsd-scan-input-hidden/);
assert.doesNotMatch(appEl.innerHTML, /id="outboundPickingScanInput"[^>]*disabled/i);
assert.match(appEl.innerHTML, /filling-scan-slot/);
assert.match(appEl.innerHTML, /HU-000001/);
assert.match(appEl.innerHTML, /filling-pallet-group-title/);
assert.match(appEl.innerHTML, /Горчица/);
assert.doesNotMatch(appEl.innerHTML, /Горчица, 1 шт/);
assert.strictEqual(
  hooks.buildOutboundPickingSummaryLine({
    order_ref: "003",
    picked_hu_count: 1,
    expected_hu_count: 2,
  }),
  "Заказ 003 · 1 / 2 паллет"
);
assert.strictEqual(
  hooks.buildOutboundPickingHeaderLine({
    order_ref: "007",
    picked_hu_count: 0,
    expected_hu_count: 12,
  }),
  "Отгрузка · Заказ 007 · 0 / 12 паллет"
);
assert.strictEqual(
  hooks.getOutboundPickingHuItemLabel({
    lines: [{ item_name: "Горчица" }],
    item_summary: "Горчица, 1 шт",
  }),
  "Горчица"
);

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
