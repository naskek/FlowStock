const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const appJs = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");
const storageJs = fs.readFileSync(path.join(__dirname, "storage.js"), "utf8");

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

const hooks = {};
const rootClasses = new Set();
const context = {
  console,
  window: {
    FlowStockTsdTestHooks: hooks,
    location: { hash: "" },
    setTimeout: function () {
      return 0;
    },
    clearTimeout: function () {},
    setInterval: function () {
      return 0;
    },
    addEventListener: function () {},
  },
  document: {
    documentElement: {
      classList: {
        toggle: function (className, force) {
          if (force) {
            rootClasses.add(className);
          } else {
            rootClasses.delete(className);
          }
        },
      },
    },
    getElementById: function () {
      return null;
    },
    querySelector: function () {
      return null;
    },
    querySelectorAll: function () {
      return [];
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
  TsdStorage: {},
};
context.window.document = context.document;
context.window.localStorage = context.localStorage;
context.window.navigator = context.navigator;
context.window.TsdStorage = context.TsdStorage;

vm.createContext(context);
vm.runInContext(appJs, context, { filename: "app.js" });

const readyBody = extractFunctionBody(appJs, "getOrderLineReadyToShipQty");
assert(
  !readyBody.includes("qtyShipped") && !readyBody.includes("qty_shipped"),
  "ready-to-ship helper must not use shipped quantity"
);
assert.strictEqual(
  hooks.getOrderLineReadyToShipQty(
    { orderType: "CUSTOMER" },
    { orderedQty: 100, shippedQty: 100 }
  ),
  null,
  "CUSTOMER without explicit ready read-model fields should not derive readiness from shipped qty"
);
assert.strictEqual(
  hooks.getOrderLineReadyToShipQty(
    { orderType: "CUSTOMER" },
    { orderedQty: 100, shippedQty: 100, canShipNow: 35 }
  ),
  35,
  "CUSTOMER should use explicit canShipNow when present"
);
assert.strictEqual(
  hooks.getOrderLineReadyToShipQty(
    { orderType: "INTERNAL" },
    { orderedQty: 100, shippedQty: 0, qtyProduced: 120 }
  ),
  100,
  "INTERNAL ready qty should use produced qty and cap it by ordered qty"
);

const html = hooks.renderOrderDetails(
  {
    number: "001",
    orderType: "CUSTOMER",
    partnerName: "Тестовый клиент",
    status: "IN_PROGRESS",
  },
  [
    {
      orderLineId: 11,
      itemId: 5,
      itemName: "Соус Печагин, 200 гр",
      barcode: "04607186950000",
      orderedQty: 1134,
      shippedQty: 100,
      palletPlannedQty: 1134,
      palletFilledQty: 567,
      plannedPalletCount: 4,
      filledPalletCount: 1,
      coverage: { orderedQty: 1134, productionFilledQty: 378, missingQty: 756 },
      productionHuRows: [
        {
          huCode: "HU-0002323",
          palletStatus: "FILLED",
          plannedQty: 378,
          filledQty: 378,
          prdRef: "PRD-2026-000028",
        },
        {
          huCode: "HU-0002324",
          palletStatus: "PLANNED",
          plannedQty: 378,
          filledQty: 0,
          prdRef: "PRD-2026-000027",
        },
        {
          huCode: "HU-PART",
          palletStatus: "PLANNED",
          plannedQty: 378,
          filledQty: 189,
          prdRef: "PRD-2026-000027",
        },
        {
          huCode: "HU-BAD",
          palletStatus: "CANCELLED",
          plannedQty: 378,
          filledQty: 0,
          prdRef: "PRD-2026-000026",
        },
      ],
      warehouseHuRows: [
        {
          huCode: "HU-0002323",
          qty: 378,
          locationCode: "001",
          locationName: "Склад ГП",
          isBoundToOrder: true,
        },
        {
          huCode: "HU-W1",
          qty: 35,
          isBoundToOrder: true,
        },
        {
          huCode: "HU-FREE",
          qty: 15,
          locationCode: "MAIN",
          isBoundToOrder: false,
        },
      ],
      shippedHuRows: [{ huCode: "HU-S1", qty: 20 }],
    },
    {
      orderLineId: 12,
      itemId: 6,
      itemName: "Горчица Печагин, 1 кг",
      orderedQty: 100,
      shippedQty: 100,
      canShipNow: 35,
    },
  ],
  []
);

function cardFor(huCode) {
  return (
    html
      .split('<div class="order-line-hu-card order-line-hu-card--')
      .filter(function (part) {
        return part.includes(huCode);
      })[0] || ""
  );
}

assert.match(html, /Готово к отгрузке/);
assert.doesNotMatch(html, /Отгружено/);
assert.match(html, /data-order-line-toggle="0"/);
assert.match(html, /order-line-hu-panel/);
assert.match(html, /Производство \/ план паллет/);
assert.doesNotMatch(html, /Производственные HU/);
assert.doesNotMatch(html, /Складские HU по товару/);
assert.strictEqual((html.match(/HU-0002323/g) || []).length, 1, "production+warehouse HU should render once");
assert.match(cardFor("HU-0002323"), /Наполнена/);
assert.match(cardFor("HU-0002323"), /План: 378 · Наполнено: 378/);
assert.match(cardFor("HU-0002323"), /PRD: PRD-2026-000028/);
assert.match(cardFor("HU-0002323"), /Движение: 001 — Склад ГП · 378 шт\./);
assert.match(cardFor("HU-0002324"), /Ожидает/);
assert.match(cardFor("HU-0002324"), /План: 378 · Наполнено: 0/);
assert.match(cardFor("HU-0002324"), /Движение: —/);
assert.match(cardFor("HU-PART"), /Частично/);
assert.match(cardFor("HU-BAD"), /Проблема/);
assert.match(html, /HU-W1/);
assert.match(cardFor("HU-W1"), /Зарезервирована/);
assert.match(cardFor("HU-W1"), /План: —/);
assert.match(cardFor("HU-W1"), /Привязано к заказу: 35 шт\./);
assert.doesNotMatch(cardFor("HU-W1"), /Наполнено:/);
assert.match(cardFor("HU-FREE"), /На складе/);
assert.match(cardFor("HU-FREE"), /На складе: 15 шт\./);
assert.doesNotMatch(cardFor("HU-FREE"), /Наполнено:/);
assert.match(cardFor("HU-S1"), /Отгружена/);
assert.match(cardFor("HU-S1"), /Движение: отгружена · 20 шт\./);
assert.match(html, /Итог выпуска/);
assert.match(html, /Заказано[\s\S]*1134/);
assert.match(html, /Выпущено[\s\S]*378/);
assert.match(html, /Осталось выпустить[\s\S]*756/);
assert.match(html, />—<\/div>/, "missing CUSTOMER ready field should render dash fallback");
assert.match(html, />35<\/div>[\s\S]*>65<\/div>/, "remaining should be ordered minus explicit ready qty");
assert.doesNotMatch(html, /Применить|Отмена|Сохранить|Удалить|apply-final/i);

assert(
  appJs.includes("TsdStorage.apiGetOrderBoundHu(route.id).catch(function ()") &&
    appJs.includes("return [];"),
  "order route should fail-soft when bound HU endpoint fails"
);
assert(
  storageJs.includes("function apiGetOrderBoundHu") &&
    storageJs.includes('"/bound-hu"') &&
    storageJs.includes(".catch(function ()") &&
    storageJs.includes("return [];"),
  "storage bound HU helper should be read-only and fail-soft"
);
assert(
  storageJs.includes("productionHuRows") &&
    storageJs.includes("warehouseHuRows") &&
    storageJs.includes("shippedHuRows") &&
    storageJs.includes("coverage: normalizeCoverage"),
  "TSD order line normalizer should preserve detailed HU rows from the single order-lines endpoint"
);

console.log("TSD order details presentation tests passed.");
