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

const buildOrderLineHuRowsBody = extractFunctionBody(appJs, "buildOrderLineHuRows");
assert(
  buildOrderLineHuRowsBody.includes("huPresentation") &&
    buildOrderLineHuRowsBody.includes("productionTasks") &&
    buildOrderLineHuRowsBody.includes("operationalHus"),
  "order-line HU rows should use canonical hu_presentation branches"
);
assert.doesNotMatch(
  buildOrderLineHuRowsBody,
  /warehouseHuRows|warehouse_hu_rows|productionHuRows|production_hu_rows|shippedHuRows|shipped_hu_rows|fate/i,
  "order-line HU rows should not restore the legacy production/warehouse/shipped merge"
);
[
  "ensureOrderLineHuRow",
  "addProductionHuRow",
  "addWarehouseHuRow",
  "addShippedHuRow",
  "getOrderLineHuMovementText",
].forEach(function (legacyHelper) {
  assert(
    !appJs.includes("function " + legacyHelper + "("),
    legacyHelper + " should stay removed after canonical hu_presentation migration"
  );
});

const html = hooks.renderOrderDetails(
  {
    number: "001",
    orderType: "CUSTOMER",
    partnerName: "Тестовый клиент",
    status: "IN_PROGRESS",
    orderStatusPresentation: { code: "IN_PROGRESS", label: "В работе" },
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
      huPresentation: {
        operationalHus: [
          {
            huCode: "HU-0002323",
            qty: 378,
            uom: "шт",
            state: { code: "RESERVED", label: "Зарезервирован" },
            location: { code: "001", name: "Склад ГП" },
          },
          {
          huCode: "HU-AWAITING",
            qty: 300,
            uom: "шт",
            state: { code: "AWAITING_SHIPMENT", label: "Ожидает отгрузки" },
            location: { code: "001", name: "Склад ГП" },
          },
          {
            huCode: "HU-W1",
            qty: 35,
            uom: "шт",
            state: { code: "RESERVED", label: "Зарезервирован" },
            location: null,
          },
          {
            huCode: "HU-FREE",
            qty: 15,
            uom: "шт",
            state: { code: "ON_STOCK", label: "На складе" },
            location: { code: "MAIN", name: "" },
          },
          {
            huCode: "HU-S1",
            qty: 20,
            uom: "шт",
            state: { code: "SHIPPED", label: "Отгружен" },
            location: null,
          },
        ],
        productionTasks: [
          {
          huCode: "HU-0002324",
            qty: 378,
            uom: "шт",
            state: { code: "AWAITING_FILL", label: "Ожидает наполнения" },
          },
          {
          huCode: "HU-PART",
            qty: 378,
            uom: "шт",
            state: { code: "INCONSISTENT", label: "Несогласованное состояние" },
          },
          {
          huCode: "HU-BAD",
            qty: 378,
            uom: "шт",
            state: { code: "INCONSISTENT", label: "Несогласованное состояние" },
          },
        ],
      },
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
assert.match(html, /Операционные HU/);
assert.match(html, />Производство</);
assert.strictEqual((html.match(/HU-0002323/g) || []).length, 1, "canonical HU should render once");
assert.match(cardFor("HU-0002323"), /Зарезервирован/);
assert.match(cardFor("HU-0002323"), /Кол-во: 378 шт/);
assert.match(cardFor("HU-0002323"), /Локация: Склад ГП/);
assert.match(cardFor("HU-AWAITING"), /Ожидает отгрузки/);
assert.strictEqual(
  (cardFor("HU-AWAITING").match(/Ожидает отгрузки/g) || []).length,
  1,
  "server-derived fate label must be the single primary status"
);
assert.match(cardFor("HU-0002324"), /Ожидает/);
assert.match(cardFor("HU-0002324"), /Ожидает наполнения/);
assert.match(cardFor("HU-PART"), /Несогласованное состояние/);
assert.match(cardFor("HU-BAD"), /Несогласованное состояние/);
assert.match(html, /HU-W1/);
assert.match(cardFor("HU-W1"), /Зарезервирован/);
assert.match(cardFor("HU-W1"), /Кол-во: 35 шт/);
assert.match(cardFor("HU-FREE"), /На складе/);
assert.match(cardFor("HU-FREE"), /Кол-во: 15 шт/);
assert.match(cardFor("HU-S1"), /Отгружен/);
assert.match(cardFor("HU-S1"), /Кол-во: 20 шт/);
assert.doesNotMatch(html, /PRD:|Движение:|Привязано к заказу:|Частично/);
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
  storageJs.includes("huPresentation") &&
    storageJs.includes("normalizeHuPresentation") &&
    storageJs.includes("coverage: normalizeCoverage"),
  "TSD order line normalizer should preserve canonical HU presentation from the order-lines endpoint"
);

// --- Unified orders list presentation tests (no show-ready/show-done buttons) ---

const ordersScreen = hooks.renderOrders();
assert(
  ordersScreen.includes('id="ordersSearchInput"') &&
    ordersScreen.includes('id="ordersStatus"') &&
    ordersScreen.includes('id="ordersList"'),
  "orders screen keeps search, status and list containers"
);
assert(
  !ordersScreen.includes("ordersFilterActions") &&
    !ordersScreen.includes("ordersToggleReadyBtn") &&
    !ordersScreen.includes("ordersToggleDoneBtn") &&
    !ordersScreen.includes("Показать готовые") &&
    !ordersScreen.includes("Показать выполненные"),
  "orders screen must not render the removed show-ready/show-done filter buttons"
);
assert(
  !/<table|<thead|<th\b|<td\b/.test(ordersScreen),
  "orders screen must not use a desktop table structure"
);

const cardInWork = hooks.buildOrderListItemHtml({
  orderId: 1048,
  number: "1048",
  orderType: "CUSTOMER",
  partnerId: 1,
  partnerName: "ООО «Ромашка»",
  status: "IN_PROGRESS",
  statusDisplay: "legacy-in-progress",
  orderStatusPresentation: { code: "IN_PROGRESS", label: "В работе" },
  plannedDate: "2026-07-02",
});
assert(
  cardInWork.includes("order-type-customer") && cardInWork.includes("Клиентский"),
  "customer order shows the 'Клиентский' type badge"
);
assert(cardInWork.includes("Ромашка"), "customer order shows partner name");
assert(cardInWork.includes("План: "), "list card shows the planned date row");
assert(
  cardInWork.includes("order-status-progress") && cardInWork.includes("В работе"),
  "IN_PROGRESS renders progress-tone pill with 'В работе'"
);
assert(cardInWork.includes('data-order="1048"'), "list card carries data-order for navigation");
assert(cardInWork.trim().indexOf("<button") === 0, "list card is a button (touch target / clickable)");
assert(
  !cardInWork.includes("Факт") &&
    !cardInWork.includes("Создан") &&
    !cardInWork.includes("order-plan-") &&
    !cardInWork.includes("order-item-needs-plan") &&
    !cardInWork.includes("Наполнен") &&
    !cardInWork.includes("ЧЗ") &&
    !cardInWork.includes("pallet_fill") &&
    !/<table|<th\b|<td\b/.test(cardInWork),
  "list card must not contain Факт/Создан/plan-info/fill/ЧЗ/table elements"
);

const cardPartial = hooks.buildOrderListItemHtml({
  orderId: 1042,
  number: "1042",
  orderType: "CUSTOMER",
  partnerId: 2,
  partnerName: "АО «Северный Альянс»",
  status: "IN_PROGRESS",
  statusDisplay: "legacy-partial",
  orderStatusPresentation: { code: "PARTIALLY_SHIPPED", label: "Частично отгружен" },
  plannedDate: "2026-06-30",
});
assert(cardPartial.includes("Частично отгружен"), "partial-shipped card shows canonical server label");
assert(cardPartial.includes("order-status-progress"), "partial-shipped tone follows canonical code");
assert(!cardPartial.includes("В работе"), "partial-shipped must not be flattened to 'В работе'");

const cardReady = hooks.buildOrderListItemHtml({
  orderId: 1039,
  number: "1039",
  orderType: "CUSTOMER",
  partnerId: 3,
  partnerName: "ИП Кузнецов",
  status: "ACCEPTED",
  statusDisplay: "legacy-ready",
  orderStatusPresentation: { code: "ACCEPTED", label: "Готов к отгрузке" },
  plannedDate: "2026-06-29",
});
assert(cardReady.includes("order-status-accepted") && cardReady.includes("Готов к отгрузке"), "ACCEPTED renders canonical accepted presentation");

const cardDone = hooks.buildOrderListItemHtml({
  orderId: 1019,
  number: "1019",
  orderType: "CUSTOMER",
  partnerId: 4,
  partnerName: "ООО «Балтийская ТГ»",
  status: "SHIPPED",
  statusDisplay: "legacy-shipped",
  orderStatusPresentation: { code: "SHIPPED", label: "Выполнен" },
  plannedDate: "2026-06-24",
});
assert(cardDone.includes("order-status-shipped") && cardDone.includes("Выполнен"), "SHIPPED renders shipped-tone pill");

const cardInternal = hooks.buildOrderListItemHtml({
  orderId: 1031,
  number: "1031",
  orderType: "INTERNAL",
  status: "IN_PROGRESS",
  statusDisplay: "legacy-internal",
  orderStatusPresentation: { code: "IN_PROGRESS", label: "В работе" },
  plannedDate: "2026-06-27",
});
assert(
  cardInternal.includes("order-type-internal") && cardInternal.includes("Внутренний заказ"),
  "internal order shows the 'Внутренний' badge and 'Внутренний заказ' label"
);

const longName =
  "Общество с ограниченной ответственностью «Производственно-торговая компания Дальневосточные Региональные Поставки и Логистика»";
const cardLong = hooks.buildOrderListItemHtml({
  orderId: 1035,
  number: "1035",
  orderType: "CUSTOMER",
  partnerId: 5,
  partnerName: longName,
  status: "IN_PROGRESS",
  orderStatusPresentation: { code: "IN_PROGRESS", label: "В работе" },
  plannedDate: "2026-06-28",
});
assert(cardLong.includes(longName), "long partner name is rendered in full (wrapping handled by CSS)");

// Customer order WITHOUT any partner (no partnerId, no partnerName): still a customer.
const cardCustomerNoPartner = hooks.buildOrderListItemHtml({
  orderId: 1027,
  number: "1027",
  orderType: "CUSTOMER",
  partnerName: "",
  status: "ACCEPTED",
  orderStatusPresentation: { code: "ACCEPTED", label: "Готов к отгрузке" },
  plannedDate: null,
});
assert(cardCustomerNoPartner.includes("order-type-customer") && cardCustomerNoPartner.includes("Клиентский"),
  "explicit CUSTOMER without partner still shows the 'Клиентский' badge");
assert(cardCustomerNoPartner.includes("—"), "customer without partner shows '—'");
assert(!cardCustomerNoPartner.includes("Внутренний заказ"),
  "customer without partner must not be labelled 'Внутренний заказ'");
assert(cardCustomerNoPartner.includes("План: —"), "missing planned date degrades to a dash");

// Internal order with empty partner: internal badge and 'Внутренний заказ' label.
const cardInternalNoPartner = hooks.buildOrderListItemHtml({
  orderId: 1011,
  number: "1011",
  orderType: "INTERNAL",
  partnerName: "",
  status: "IN_PROGRESS",
  orderStatusPresentation: { code: "IN_PROGRESS", label: "В работе" },
  plannedDate: "2026-06-20",
});
assert(cardInternalNoPartner.includes("order-type-internal") && cardInternalNoPartner.includes("Внутренний заказ"),
  "INTERNAL with empty partner shows internal badge and 'Внутренний заказ'");

// isInternalOrder semantics (regression for the shared helper used across consumers).
assert.strictEqual(hooks.isInternalOrder({ orderType: "INTERNAL" }), true, "explicit INTERNAL is internal");
assert.strictEqual(hooks.isInternalOrder({ order_type: "INTERNAL" }), true, "snake_case INTERNAL is internal");
assert.strictEqual(
  hooks.isInternalOrder({ orderType: "CUSTOMER", partnerName: "", partnerId: null }),
  false,
  "explicit CUSTOMER without partner is NOT internal"
);
assert.strictEqual(
  hooks.isInternalOrder({ order_type: "CUSTOMER" }),
  false,
  "snake_case CUSTOMER without partner is NOT internal"
);
// No explicit type + no partner: legacy fallback classifies it as internal.
assert.strictEqual(
  hooks.isInternalOrder({ partnerName: "", partnerId: null }),
  true,
  "typeless order without a partner falls back to internal"
);
assert.strictEqual(
  hooks.isInternalOrder({ partnerName: "ООО Тест", partnerId: 9 }),
  false,
  "typeless order with a partner falls back to customer"
);

// Shared status helper — all main consumers use only server-owned presentation.
const sInProgress = hooks.getOrderStatusInfoForOrder({
  status: "SHIPPED",
  statusDisplay: "legacy-wrong",
  orderStatusPresentation: { code: "IN_PROGRESS", label: "В работе" },
});
assert.strictEqual(sInProgress.label, "В работе", "canonical label wins over raw/legacy fields");
assert(sInProgress.className.includes("order-status-progress"));

const sPartial = hooks.getOrderStatusInfoForOrder({
  orderStatusPresentation: { code: "PARTIALLY_SHIPPED", label: "Частично отгружен" },
});
assert.strictEqual(sPartial.label, "Частично отгружен");
assert(sPartial.className.includes("order-status-progress"));

const sAccepted = hooks.getOrderStatusInfoForOrder({
  orderStatusPresentation: { code: "ACCEPTED", label: "Готов к отгрузке" },
});
assert.strictEqual(sAccepted.label, "Готов к отгрузке");
assert(sAccepted.className.includes("order-status-accepted"));

const sShipped = hooks.getOrderStatusInfoForOrder({
  orderStatusPresentation: { code: "SHIPPED", label: "Выполнен" },
});
assert.strictEqual(sShipped.label, "Выполнен");
assert(sShipped.className.includes("order-status-shipped"));

const sMissingCanonical = hooks.getOrderStatusInfoForOrder({ status: "ACCEPTED", statusDisplay: "Готов" });
assert.strictEqual(sMissingCanonical.label, "Неизвестно", "legacy fields are not a main-status fallback");
assert(sMissingCanonical.className.includes("order-status-neutral"));

// Consumer: read-only order details renders the same canonical server presentation.
const detailsPartial = hooks.renderOrderDetails(
  {
    number: "777",
    orderType: "CUSTOMER",
    partnerName: "Клиент",
    status: "IN_PROGRESS",
    statusDisplay: "legacy-partial",
    orderStatusPresentation: { code: "PARTIALLY_SHIPPED", label: "Частично отгружен" },
  },
  [],
  []
);
assert(detailsPartial.includes("Частично отгружен"), "order details consumer shows canonical server label");
// Consumer: filling list delegates to the same helper (covered by app.filling.test.js status mapping assertion).

console.log("TSD order details presentation tests passed.");
console.log("TSD unified orders list presentation tests passed.");
