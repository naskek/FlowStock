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
      orderedQty: 100,
      shippedQty: 100,
      productionHuCodes: ["HU-P1"],
      palletPlannedQty: 100,
      palletFilledQty: 60,
      plannedPalletCount: 2,
      filledPalletCount: 1,
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
  [{ itemId: 5, hu: "HU-W1" }]
);

assert.match(html, /Готово к отгрузке/);
assert.doesNotMatch(html, /Отгружено/);
assert.match(html, /data-order-line-toggle="0"/);
assert.match(html, /order-line-hu-panel/);
assert.match(html, /HU-P1/);
assert.match(html, /HU-W1/);
assert.match(html, /Складские HU по товару/);
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

console.log("TSD order details presentation tests passed.");
