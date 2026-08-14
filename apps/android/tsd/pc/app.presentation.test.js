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
const indexPath = path.join(__dirname, "index.html");
const styles = fs.readFileSync(path.join(__dirname, "styles.css"), "utf8");
const hooks = {};
const versionBanner = { hidden: true };
const versionReloadHandlers = {};
let reloadCount = 0;
let nextTimerId = 0;
const activeIntervals = new Map();
let versionFetchRejects = false;
let versionFetchCount = 0;
let versionResponse = { pc_web_version: "loaded-version", version: "server-version" };
let deferredVersionResponse = null;

function createDeferred() {
  let resolve;
  const promise = new Promise(function (resolvePromise) {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}

function createVersionFetchResponse(payload) {
  return {
    ok: true,
    json: function () { return Promise.resolve(payload); },
  };
}

const context = {
  console,
  clearInterval: function (timerId) {
    activeIntervals.delete(timerId);
  },
  fetch: function () {
    versionFetchCount += 1;
    if (versionFetchRejects) {
      return Promise.reject(new Error("offline"));
    }
    if (deferredVersionResponse) {
      return deferredVersionResponse.promise.then(createVersionFetchResponse);
    }
    return Promise.resolve(createVersionFetchResponse(versionResponse));
  },
  window: {
    FlowStockPcTestHooks: hooks,
    location: {
      reload: function () { reloadCount += 1; },
    },
    setInterval: function (handler, intervalMs) {
      nextTimerId += 1;
      activeIntervals.set(nextTimerId, { handler, intervalMs });
      return nextTimerId;
    },
  },
  document: {
    getElementById: function (id) {
      if (id === "pcVersionBanner") {
        return versionBanner;
      }
      if (id === "pcVersionReloadBtn") {
        return {
          addEventListener: function (type, handler) {
            versionReloadHandlers[type] = handler;
          },
        };
      }
      return null;
    },
    querySelector: function (selector) {
      if (selector === 'meta[name="flowstock-pc-web-version"]') {
        return {
          getAttribute: function (name) {
            return name === "content" ? "loaded-version" : null;
          },
        };
      }
      return null;
    },
    querySelectorAll: function () {
      return [];
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

const pc = context.window.FlowStockPcTestHooks;

const attentionModalHtml = pc.renderAttentionModalContent(
  "Не удалось подтвердить заказ",
  "Ошибка <НДС>"
);
assert.match(attentionModalHtml, /role="alertdialog"/);
assert.match(attentionModalHtml, /pc-attention-modal-icon[^>]*>!</);
assert.match(attentionModalHtml, />Не удалось подтвердить заказ</);
assert.match(attentionModalHtml, />Ошибка &lt;НДС&gt;</);
assert.match(attentionModalHtml, /id="attentionModalOkBtn"[^>]*>OK<\/button>/);

assert.strictEqual(
  pc.getOrderStatusPresentation({ status: "Отменён" }).label,
  "Неизвестно",
  "legacy display-only order must not become the canonical status"
);
assert.strictEqual(
  pc.getOrderStatusPresentation({
    order_status: "CANCELLED",
    status: "legacy-cancelled",
    order_status_presentation: { code: "CANCELLED", label: "Отменён" },
  }).tone,
  "cancelled",
  "canonical cancelled status should use cancelled badge tone"
);

const shippedOrderHtml = pc.renderOrdersTable([
  {
    id: 9,
    order_ref: "009",
    order_status: "SHIPPED",
    status: "legacy-shipped",
    order_status_presentation: { code: "SHIPPED", label: "Выполнен" },
  },
]);
assert.match(shippedOrderHtml, /pc-order-status-icon/);

const pendingRequestRow = {
  id: "request:41",
  request_id: 41,
  request_type: "CREATE_ORDER",
  management_supported: true,
  order_ref: "041",
  order_type: "CUSTOMER",
  partner_name: "Очень длинное наименование контрагента",
  is_pending_confirmation: true,
};
const adminPendingOrderHtml = pc.renderOrdersTable([pendingRequestRow], {
  canManagePendingRequests: true,
});
assert.match(adminPendingOrderHtml, />Действия</);
assert.match(adminPendingOrderHtml, /data-pending-request-action="confirm"/);
assert.match(adminPendingOrderHtml, />Подтвердить</);
assert.match(adminPendingOrderHtml, /data-pending-request-action="reject"/);
assert.match(adminPendingOrderHtml, />Отклонить</);
assert.match(
  adminPendingOrderHtml,
  /class="btn primary-btn pc-order-inline-action pc-order-inline-action--confirm"[^>]*>Подтвердить<\/button>/
);
assert.match(
  adminPendingOrderHtml,
  /class="btn pc-order-inline-action pc-order-inline-action--reject"[^>]*>Отклонить<\/button>/
);
assert.match(adminPendingOrderHtml, /class="pc-table pc-table-zebra pc-orders-table pc-orders-table--with-actions"/);
assert.match(adminPendingOrderHtml, /class="pc-order-ref-cell" title="041"/);
assert.match(
  adminPendingOrderHtml,
  /class="pc-order-partner-cell" title="Очень длинное наименование контрагента"/
);
assert.match(adminPendingOrderHtml, /class="pc-order-actions-header"/);

const operatorPendingOrderHtml = pc.renderOrdersTable([pendingRequestRow], {
  canManagePendingRequests: false,
});
assert.doesNotMatch(operatorPendingOrderHtml, />Действия</);
assert.doesNotMatch(operatorPendingOrderHtml, /data-pending-request-action=/);
assert.match(operatorPendingOrderHtml, /class="pc-table pc-table-zebra pc-orders-table"/);
assert.doesNotMatch(operatorPendingOrderHtml, /pc-orders-table--with-actions/);

const canonicalAdminOrderHtml = pc.renderOrdersTable([
  {
    id: 41,
    order_ref: "041",
    order_type: "CUSTOMER",
    management_supported: true,
    order_status: "IN_PROGRESS",
    order_status_presentation: { code: "IN_PROGRESS", label: "В работе" },
  },
], { canManagePendingRequests: true });
assert.match(canonicalAdminOrderHtml, />Действия</);
assert.doesNotMatch(canonicalAdminOrderHtml, /data-pending-request-action=/);

const contentAwareOrdersHtml = pc.renderOrdersTable([
  {
    id: 42,
    order_ref: "PRICE-SNAPSHOT-VERY-LONG-ORDER-REF",
    order_type: "CUSTOMER",
    partner_name: "Очень длинное наименование контрагента для проверки сжатия",
    order_status: "IN_PROGRESS",
    order_status_presentation: { code: "PARTIALLY_SHIPPED", label: "Частично отгружен" },
  },
  {
    id: 43,
    order_ref: "043",
    order_type: "INTERNAL",
    order_status: "IN_PROGRESS",
    order_status_presentation: { code: "IN_PROGRESS", label: "В работе" },
    needs_production_pallet_plan: true,
  },
  {
    id: 44,
    order_ref: "044",
    order_type: "INTERNAL",
    order_status: "IN_PROGRESS",
    order_status_presentation: { code: "IN_PROGRESS", label: "В работе" },
    planned_pallet_count: 14,
    filled_pallet_count: 4,
    has_production_pallet_plan: true,
  },
], { canManagePendingRequests: true });
assert.match(contentAwareOrdersHtml, />Частично отгружен</);
assert.match(contentAwareOrdersHtml, />План не сформирован</);
assert.match(contentAwareOrdersHtml, />4 \/ 14</);
assert.match(contentAwareOrdersHtml, /title="PRICE-SNAPSHOT-VERY-LONG-ORDER-REF"/);
assert.match(
  contentAwareOrdersHtml,
  /title="Очень длинное наименование контрагента для проверки сжатия"/
);
assert.match(shippedOrderHtml, /title="Выполнен"/);
assert.doesNotMatch(shippedOrderHtml, />Выполнен</);

const readyOrderHtml = pc.renderOrdersTable([
  {
    id: 10,
    order_ref: "010",
    order_status: "ACCEPTED",
    status: "legacy-ready",
    order_status_presentation: { code: "ACCEPTED", label: "Готов к отгрузке" },
  },
]);
assert.match(readyOrderHtml, />Готов к отгрузке</);
assert.doesNotMatch(readyOrderHtml, /legacy-ready/);

assert.strictEqual(
  pc.getOrderStatusPresentation({
    order_status: "ACCEPTED",
    status: "legacy-ready",
    order_status_presentation: { code: "PARTIALLY_SHIPPED", label: "Частично отгружен" },
  }).label,
  "Частично отгружен",
  "PC must accept server-owned status precedence and label"
);

assert.strictEqual(
  pc.getOrderMarkingPresentation({
    marking_label: "Маркировка проведена",
  }).label,
  "Маркировка проведена"
);
assert.strictEqual(
  pc.getOrderMarkingPresentation({
    marking_label: "Маркировка не проведена",
  }).label,
  "Маркировка не проведена"
);
assert.strictEqual(
  pc.getOrderMarkingPresentation({
    marking_order_id: "11111111-1111-1111-1111-111111111111",
    marking_completed: false,
    marking_effective_status: "REQUIRED",
    marking_label: "Маркировка не проведена",
  }).label,
  "Маркировка не проведена"
);
assert.strictEqual(
  pc.getOrderMarkingPresentation({
    marking_completed: true,
  }).label,
  "Маркировка проведена"
);
assert.strictEqual(
  pc.getOrderMarkingPresentation({
    marking_effective_status: "PRINTED",
    marking_status_display: "Маркировка проведена",
  }).label,
  "Маркировка проведена"
);
assert.strictEqual(
  pc.getOrderMarkingPresentation({
    marking_effective_status: "REQUIRED",
    marking_status_display: "Маркировка не проведена",
  }).label,
  "Маркировка не проведена"
);
assert.strictEqual(
  pc.getOrderMarkingPresentation({
    marking_effective_status: "NOT_REQUIRED",
  }).label,
  ""
);
assert.strictEqual(
  pc.getOrderMarkingPresentation({
    marking_status: "REQUIRED",
  }).label,
  "Маркировка не проведена"
);
assert.strictEqual(
  pc.getOrderMarkingPresentation({
    marking_required: true,
  }).label,
  "Маркировка не проведена"
);

const printedHtml = pc.renderOrderMarkingIndicator({
  marking_effective_status: "PRINTED",
  marking_status_display: "Маркировка проведена",
});
assert.match(printedHtml, /pc-icon-status/);
assert.match(printedHtml, /pc-marking-badge/);
assert.match(printedHtml, /title="Маркировка проведена"/);
assert.doesNotMatch(printedHtml, />Маркировка проведена</);

assert.strictEqual(
  pc.getOrderPalletFillingPresentation({
    needs_production_pallet_plan: true,
    has_production_pallet_plan: false,
  }).label,
  "План не сформирован"
);
assert.strictEqual(
  pc.getOrderPalletFillingPresentation({
    has_production_pallet_plan: true,
    planned_pallet_count: 3,
    filled_pallet_count: 1,
    pallet_plan_status: "Наполнение идёт: 1 / 3",
  }).label,
  "Наполнено 1 / 3"
);
assert.strictEqual(
  pc.getOrderPalletFillingPresentation({
    has_production_pallet_plan: true,
    planned_pallet_count: 2,
    filled_pallet_count: 2,
  }).tone,
  "completed"
);

const completedPalletHtml = pc.renderOrderPalletFillingIndicator({
  has_production_pallet_plan: true,
  planned_pallet_count: 2,
  filled_pallet_count: 2,
});
assert.match(completedPalletHtml, /pc-icon-status/);
assert.match(completedPalletHtml, /title="Паллеты: 2 \/ 2"/);
assert.doesNotMatch(completedPalletHtml, /Количество:/);
assert.doesNotMatch(completedPalletHtml, />Наполнено 2 \/ 2</);

const ordersWithPalletHtml = pc.renderOrdersTable([
  {
    id: 77,
    order_ref: "077",
    order_type: "INTERNAL",
    order_status: "IN_PROGRESS",
    has_production_pallet_plan: true,
    planned_pallet_count: 2,
    filled_pallet_count: 1,
  },
]);
assert.match(ordersWithPalletHtml, /Наполнение паллет/);
assert.match(ordersWithPalletHtml, /pc-pallet-progress-inprogress/);
assert.match(ordersWithPalletHtml, /pc-pallet-progress-fill" style="width:50%"/);
assert.match(ordersWithPalletHtml, /Наполнено 1 \/ 2/);
assert.match(ordersWithPalletHtml, />1 \/ 2</);

const orderLinesWithPalletHtml = pc.renderOrderLinesTable(
  [
    {
      item_name: "Горчица",
      barcode: "SKU-001",
      gtin: "04607186951520",
      production_purpose: "INTERNAL_STOCK",
      qty_ordered: 20,
      qty_produced: 10,
      planned_pallet_count: 2,
      filled_pallet_count: 1,
      pallet_planned_qty: 20,
      pallet_filled_qty: 10,
    },
  ],
  { order_type: "INTERNAL", order_status: "IN_PROGRESS" }
);
assert.doesNotMatch(orderLinesWithPalletHtml, /pc-order-line-coverage-covered/);
assert.match(orderLinesWithPalletHtml, /Наполнение/);
assert.match(orderLinesWithPalletHtml, /pc-pallet-progress-inprogress/);
assert.match(orderLinesWithPalletHtml, /Наполнено 1 \/ 2/);
assert.match(orderLinesWithPalletHtml, />1 \/ 2</);
assert.match(
  orderLinesWithPalletHtml,
  /<th>Товар<\/th><th>SKU \/ ШК<\/th><th>GTIN<\/th><th>Заказано<\/th><th>Наполнение<\/th>/
);
assert.match(orderLinesWithPalletHtml, /pc-order-lines-table-wrap/);
assert.match(orderLinesWithPalletHtml, /pc-order-lines-col-item/);
assert.match(orderLinesWithPalletHtml, /pc-order-lines-col-filling/);
assert.match(styles, /\.pc-order-lines-table\s*\{[^}]*width:\s*100%;[^}]*table-layout:\s*fixed;/s);
assert.match(styles, /\.pc-order-lines-table-wrap\s*\{[^}]*width:\s*100%;[^}]*overflow-x:\s*auto;/s);
assert.doesNotMatch(orderLinesWithPalletHtml, /<th>В наличии<\/th>/);
assert.doesNotMatch(orderLinesWithPalletHtml, /<th>Назначение<\/th>/);
assert.doesNotMatch(orderLinesWithPalletHtml, /<th>Отгружено<\/th>/);
assert.doesNotMatch(orderLinesWithPalletHtml, /<th>Выпущено<\/th>/);

const orderLinesCompletePalletHtml = pc.renderOrderLinesTable(
  [
    {
      item_name: "Горчица",
      barcode: "SKU-001",
      gtin: "04607186951520",
      production_purpose: "INTERNAL_STOCK",
      qty_ordered: 20,
      qty_produced: 20,
      planned_pallet_count: 2,
      filled_pallet_count: 2,
      pallet_planned_qty: 20,
      pallet_filled_qty: 20,
    },
  ],
  { order_type: "INTERNAL", order_status: "IN_PROGRESS" }
);
assert.match(orderLinesCompletePalletHtml, /pc-order-line-coverage-covered/);
assert.match(orderLinesCompletePalletHtml, /pc-pallet-progress-completed/);
assert.match(orderLinesCompletePalletHtml, />2 \/ 2</);
assert.match(orderLinesCompletePalletHtml, /aria-label="Наполнено 2 \/ 2"/);

const mixedComponentFilledLineHtml = pc.renderOrderLinesTable(
  [
    {
      item_name: "Горчица",
      barcode: "SKU-001",
      gtin: "04607186951520",
      production_purpose: "CUSTOMER_ORDER",
      qty_ordered: 1,
      qty_shipped: 0,
      qty_available: 0,
      can_ship_now: 0,
      coverage: { ordered_qty: 1, covered_qty: 0, missing_qty: 1, shipped_qty: 0 },
      planned_pallet_count: 1,
      filled_pallet_count: 1,
      pallet_planned_qty: 1,
      pallet_filled_qty: 1,
    },
  ],
  { order_type: "CUSTOMER", order_status: "IN_PROGRESS" }
);
assert.match(mixedComponentFilledLineHtml, /pc-order-line-coverage-missing/);
assert.doesNotMatch(mixedComponentFilledLineHtml, /pc-order-line-coverage-covered/);
assert.match(mixedComponentFilledLineHtml, /pc-pallet-progress-completed/);
assert.match(mixedComponentFilledLineHtml, />1 \/ 1</);
assert.match(mixedComponentFilledLineHtml, /aria-label="Наполнено 1 \/ 1"/);

const mixedComponentPartialLineHtml = pc.renderOrderLinesTable(
  [
    {
      item_name: "Горчица",
      barcode: "SKU-001",
      gtin: "04607186951520",
      production_purpose: "CUSTOMER_ORDER",
      qty_ordered: 1,
      qty_shipped: 0,
      qty_available: 0,
      can_ship_now: 0,
      coverage: { ordered_qty: 1, covered_qty: 0, missing_qty: 1, shipped_qty: 0 },
      planned_pallet_count: 1,
      filled_pallet_count: 0,
      pallet_planned_qty: 1,
      pallet_filled_qty: 0.5,
    },
  ],
  { order_type: "CUSTOMER", order_status: "IN_PROGRESS" }
);
assert.match(mixedComponentPartialLineHtml, /pc-order-line-coverage-missing/);
assert.doesNotMatch(mixedComponentPartialLineHtml, /pc-order-line-coverage-covered/);
assert.match(mixedComponentPartialLineHtml, /pc-pallet-progress-inprogress/);
assert.match(mixedComponentPartialLineHtml, />0\.5 \/ 1</);
assert.match(mixedComponentPartialLineHtml, /aria-label="Наполнено 0\.5 \/ 1"/);
assert.match(styles, /\.pc-order-line-coverage-partial td\s*\{[^}]*background:\s*#fef3c7;/s);

const mixedComponentOrder = {
  id: 4,
  order_ref: "004",
  order_type: "CUSTOMER",
  order_status: "IN_PROGRESS",
  due_date: "2026-06-01",
  shipped_at: null,
  planned_pallet_count: 1,
  filled_pallet_count: 1,
};
const mixedComponentLines = [
  {
    item_name: "Горчица",
    barcode: "SKU-001",
    gtin: "04607186951520",
    production_purpose: "CUSTOMER_ORDER",
    qty_ordered: 1,
    qty_shipped: 0,
    qty_available: 0,
    can_ship_now: 0,
    coverage: { ordered_qty: 1, covered_qty: 0, missing_qty: 1, shipped_qty: 0 },
    planned_pallet_count: 1,
    filled_pallet_count: 1,
    pallet_planned_qty: 1,
    pallet_filled_qty: 1,
  },
];
const modalUpdatesAfterMixedFill = pc.getOrderModalContentUpdates(mixedComponentOrder, mixedComponentLines);
assert.match(modalUpdatesAfterMixedFill.linesHtml, /pc-pallet-progress-completed/);
assert.match(modalUpdatesAfterMixedFill.linesHtml, />1 \/ 1</);
assert.match(modalUpdatesAfterMixedFill.linesHtml, /aria-label="Наполнено 1 \/ 1"/);
assert.match(modalUpdatesAfterMixedFill.linesHtml, /pc-order-line-coverage-missing/);
assert.doesNotMatch(modalUpdatesAfterMixedFill.linesHtml, /pc-order-line-coverage-covered/);
assert.strictEqual(Object.prototype.hasOwnProperty.call(modalUpdatesAfterMixedFill, "summaryHtml"), false);

const modalDom = {
  datesEl: { textContent: "" },
  readinessEl: { outerHTML: '<span id="orderReadinessBadge"></span>' },
  linesEl: { innerHTML: "" },
  querySelector: function (selector) {
    if (selector === "#orderDatesStatus") {
      return this.datesEl;
    }
    if (selector === "#orderReadinessBadge") {
      return this.readinessEl;
    }
    if (selector === "#orderLinesWrap") {
      return this.linesEl;
    }
    return null;
  },
};
pc.applyOrderModalContentUpdates(modalDom, modalUpdatesAfterMixedFill);
assert.match(modalDom.linesEl.innerHTML, /pc-pallet-progress-completed/);
assert.match(modalDom.linesEl.innerHTML, />1 \/ 1</);
assert.match(modalDom.linesEl.innerHTML, /aria-label="Наполнено 1 \/ 1"/);

const completedOrderModalUpdates = pc.getOrderModalContentUpdates(
  {
    order_ref: "004",
    order_type: "CUSTOMER",
    order_status: "SHIPPED",
    marking_effective_status: "PRINTED",
    has_production_pallet_plan: true,
    planned_pallet_count: 1,
    filled_pallet_count: 1,
  },
  [
    {
      item_name: "Горчица",
      qty_ordered: 1824,
      qty_shipped: 1824,
      planned_pallet_count: 1,
      filled_pallet_count: 1,
      show_pallet_completed_icon: true,
      pallet_fill_title: "Паллеты: 1 / 1",
    },
  ]
);
assert.strictEqual(Object.prototype.hasOwnProperty.call(completedOrderModalUpdates, "summaryHtml"), false);
assert.doesNotMatch(completedOrderModalUpdates.linesHtml, /pc-order-modal-summary/);
assert.match(completedOrderModalUpdates.linesHtml, /pc-icon-status/);

let modalRefreshCalls = 0;
pc.__setOpenOrderModalControllerForTest({
  modal: { isConnected: true },
  refresh: function () {
    modalRefreshCalls += 1;
  },
});
pc.refreshOpenOrderModalIfNeeded();
assert.strictEqual(modalRefreshCalls, 1, "live refresh hook should call open order modal refresh");
pc.clearOpenOrderModalController();
assert.strictEqual(pc.getOpenOrderModalController(), null);

pc.__setOpenOrderModalControllerForTest({
  modal: { isConnected: false },
  refresh: function () {
    modalRefreshCalls += 1;
  },
});
pc.refreshOpenOrderModalIfNeeded();
assert.strictEqual(pc.getOpenOrderModalController(), null, "stale disconnected modal controller should be cleared");

const internalPlannedOnlyHtml = pc.renderOrderLinesTable(
  [
    {
      item_name: "Горчица",
      barcode: "SKU-001",
      gtin: "04607186951520",
      production_purpose: "INTERNAL_STOCK",
      qty_ordered: 3648,
      qty_produced: 0,
      planned_pallet_count: 6,
      filled_pallet_count: 0,
      pallet_planned_qty: 3648,
      pallet_filled_qty: 0,
    },
  ],
  { order_type: "INTERNAL", order_status: "IN_PROGRESS" }
);
assert.doesNotMatch(internalPlannedOnlyHtml, /pc-order-line-coverage-covered/);

const customerFullHuCoverageHtml = pc.renderOrderLinesTable(
  [
    {
      item_name: "Горчица",
      barcode: "SKU-001",
      gtin: "04607186951520",
      production_purpose: "CUSTOMER_ORDER",
      qty_ordered: 1800,
      qty_shipped: 0,
      qty_produced: 1800,
      qty_left: 1800,
      qty_available: 0,
      coverage: { ordered_qty: 1800, covered_qty: 1800, missing_qty: 0, shipped_qty: 0 },
    },
  ],
  { order_type: "CUSTOMER", order_status: "IN_PROGRESS" }
);
assert.match(customerFullHuCoverageHtml, /pc-order-line-coverage-covered/);
assert.match(customerFullHuCoverageHtml, /Горчица: покрыто 1800 из 1800/);

const customerPartialHuCoverageHtml = pc.renderOrderLinesTable(
  [
    {
      item_name: "Горчица",
      barcode: "SKU-001",
      gtin: "04607186951520",
      production_purpose: "CUSTOMER_ORDER",
      qty_ordered: 1800,
      qty_shipped: 0,
      qty_produced: 1200,
      qty_left: 1800,
      qty_available: 9999,
      coverage: { ordered_qty: 1800, covered_qty: 1200, missing_qty: 600, shipped_qty: 0 },
    },
  ],
  { order_type: "CUSTOMER", order_status: "IN_PROGRESS" }
);
assert.match(customerPartialHuCoverageHtml, /pc-order-line-coverage-partial/);
assert.doesNotMatch(customerPartialHuCoverageHtml, /pc-order-line-coverage-covered/);
assert.match(customerPartialHuCoverageHtml, /не хватает 600/);

const shippedCustomerStalePalletHtml = pc.renderOrderLinesTable(
  [
    {
      item_name: "Хрен столовый",
      barcode: "SKU-066",
      gtin: "04607186951520",
      production_purpose: "CUSTOMER_ORDER",
      qty_ordered: 1890,
      qty_shipped: 1890,
      planned_pallet_count: 5,
      filled_pallet_count: 2,
      hide_pallet_fill_indicator: true,
      line_fully_shipped: true,
    },
  ],
  { order_type: "CUSTOMER", order_status: "SHIPPED" }
);
assert.doesNotMatch(shippedCustomerStalePalletHtml, /Наполнено 2 \/ 5/);

const shippedCustomerStalePalletFallbackHtml = pc.renderOrderLinesTable(
  [
    {
      item_name: "Хрен столовый",
      barcode: "SKU-066",
      gtin: "04607186951520",
      production_purpose: "CUSTOMER_ORDER",
      qty_ordered: 1890,
      qty_shipped: 1890,
      planned_pallet_count: 5,
      filled_pallet_count: 2,
    },
  ],
  { order_type: "CUSTOMER", order_status: "SHIPPED" }
);
assert.doesNotMatch(shippedCustomerStalePalletFallbackHtml, /Наполнено 2 \/ 5/);
assert.match(shippedCustomerStalePalletFallbackHtml, /pc-icon-status/);

const expandedCustomerOrderLineHtml = pc.renderOrderLinesTable(
  [
    {
      id: 501,
      item_name: "Горчица",
      barcode: "SKU-001",
      gtin: "04607186951520",
      qty_ordered: 100,
      hu_presentation: {
        operational_hus: [
          {
            hu_code: "HU-LINE",
            qty: 40,
            state: { code: "RESERVED", label: "Зарезервирован" },
            location: { code: "FG-01", name: "Основной склад" },
          },
          {
            hu_code: "HU-SHIPPED",
            qty: 20,
            state: { code: "SHIPPED", label: "Отгружен" },
            location: null,
          },
        ],
        production_tasks: [
          {
            hu_code: "HU-PRODUCTION",
            qty: 60,
            state: { code: "AWAITING_FILL", label: "Ожидает наполнения" },
            pallet_status: "PLANNED",
          },
        ],
      },
      coverage: {
        ordered_qty: 100,
        warehouse_bound_qty: 40,
        production_filled_qty: 60,
        shipped_qty: 20,
        covered_qty: 80,
        missing_qty: 20,
      },
    },
  ],
  { order_ref: "003", order_type: "CUSTOMER", order_status: "IN_PROGRESS" },
  { 501: true }
);
assert.match(expandedCustomerOrderLineHtml, /data-order-line-toggle="501"/);
assert.match(expandedCustomerOrderLineHtml, /aria-expanded="true"/);
assert.strictEqual((expandedCustomerOrderLineHtml.match(/>Паллеты по товару</g) || []).length, 1);
assert.strictEqual((expandedCustomerOrderLineHtml.match(/pc-order-line-detail-table"/g) || []).length, 1);
assert.match(
  expandedCustomerOrderLineHtml,
  /<th>HU<\/th><th>Кол-во<\/th><th>Состояние<\/th><th>Локация<\/th>/
);
assert.strictEqual((expandedCustomerOrderLineHtml.match(/HU-LINE/g) || []).length, 1);
assert.strictEqual((expandedCustomerOrderLineHtml.match(/HU-PRODUCTION/g) || []).length, 1);
assert.ok(
  expandedCustomerOrderLineHtml.indexOf("HU-LINE") <
    expandedCustomerOrderLineHtml.indexOf("HU-PRODUCTION"),
  "operational HU must remain before production tasks"
);
assert.match(expandedCustomerOrderLineHtml, /Зарезервирован/);
assert.match(expandedCustomerOrderLineHtml, /Отгружен/);
assert.match(expandedCustomerOrderLineHtml, /HU-PRODUCTION/);
assert.match(expandedCustomerOrderLineHtml, /Ожидает наполнения/);
assert.match(
  expandedCustomerOrderLineHtml,
  /HU-PRODUCTION<\/td><td>60<\/td><td>Ожидает наполнения<\/td><td>—<\/td>/
);
assert.doesNotMatch(expandedCustomerOrderLineHtml, />Производство</);
assert.doesNotMatch(expandedCustomerOrderLineHtml, /PLANNED|Движение HU|Привязка|<th>PRD<\/th>|<th>План<\/th>|<th>Наполнено<\/th>/);
assert.doesNotMatch(expandedCustomerOrderLineHtml, />Отгрузка этой строки заказа</);
assert.match(expandedCustomerOrderLineHtml, />Итог</);
assert.match(expandedCustomerOrderLineHtml, />Заказано</);
assert.match(expandedCustomerOrderLineHtml, />Выпущено</);
assert.match(expandedCustomerOrderLineHtml, /Не хватает/);
assert.match(expandedCustomerOrderLineHtml, /is-missing/);

const expandedLineWithoutExactCoverageHtml = pc.renderOrderLinesTable(
  [
    {
      id: 502,
      item_name: "Горчица",
      qty_ordered: 100,
      shortage: 25,
      hu_presentation: { operational_hus: [], production_tasks: [] },
    },
  ],
  { order_type: "CUSTOMER", order_status: "IN_PROGRESS" },
  { 502: true }
);
assert.match(expandedLineWithoutExactCoverageHtml, /Паллеты отсутствуют/);
assert.doesNotMatch(expandedLineWithoutExactCoverageHtml, /Операционные HU отсутствуют/);
assert.doesNotMatch(expandedLineWithoutExactCoverageHtml, />Производство</);
assert.doesNotMatch(expandedLineWithoutExactCoverageHtml, />Отгрузка этой строки заказа</);
assert.match(expandedLineWithoutExactCoverageHtml, /Точный итог покрытия недоступен/);
assert.match(expandedLineWithoutExactCoverageHtml, /Существующий серверный дефицит: 25/);
assert.doesNotMatch(expandedLineWithoutExactCoverageHtml, /pc-order-line-coverage-grid/);

const expandedAwaitingShipmentOrderLineHtml = pc.renderOrderLinesTable(
  [
    {
      id: 505,
      item_name: "Соус",
      qty_ordered: 60,
      hu_presentation: {
        operational_hus: [{
          hu_code: "HU-AWAITING",
          qty: 60,
          state: { code: "AWAITING_SHIPMENT", label: "Ожидает отгрузки" },
          location: { code: "FG-01", name: "" },
        }],
        production_tasks: [],
      },
      coverage: { ordered_qty: 60, production_filled_qty: 60, covered_qty: 60, missing_qty: 0 },
    },
  ],
  { order_ref: "005", order_type: "CUSTOMER", order_status: "IN_PROGRESS" },
  { 505: true }
);
assert.match(expandedAwaitingShipmentOrderLineHtml, /HU-AWAITING/);
assert.match(expandedAwaitingShipmentOrderLineHtml, /Ожидает отгрузки/);
assert.match(expandedAwaitingShipmentOrderLineHtml, /<td>FG-01<\/td>/);
assert.strictEqual((expandedAwaitingShipmentOrderLineHtml.match(/pc-order-line-detail-table"/g) || []).length, 1);
assert.doesNotMatch(expandedAwaitingShipmentOrderLineHtml, />Наполнена?</);
assert.strictEqual(
  (expandedAwaitingShipmentOrderLineHtml.match(/Ожидает отгрузки/g) || []).length,
  1,
  "server-derived fate label must not be duplicated in movement"
);

const expandedCustomerLineWithOnlyShippedHuHtml = pc.renderOrderLinesTable(
  [
    {
      id: 504,
      item_name: "Горчица",
      qty_ordered: 1824,
      hu_presentation: {
        operational_hus: [{
          hu_code: "HU-0002083",
          qty: 1824,
          state: { code: "SHIPPED", label: "Отгружен" },
          location: null,
        }],
        production_tasks: [],
      },
      coverage: { ordered_qty: 1824, covered_qty: 1824, missing_qty: 0, shipped_qty: 1824 },
    },
  ],
  { order_ref: "004", order_type: "CUSTOMER", order_status: "SHIPPED" },
  { 504: true }
);
assert.match(expandedCustomerLineWithOnlyShippedHuHtml, />Паллеты по товару</);
assert.match(expandedCustomerLineWithOnlyShippedHuHtml, /HU-0002083/);
assert.match(expandedCustomerLineWithOnlyShippedHuHtml, /Отгружен/);
assert.match(
  expandedCustomerLineWithOnlyShippedHuHtml,
  /HU-0002083<\/td><td>1824<\/td><td>Отгружен<\/td><td>—/
);
assert.doesNotMatch(expandedCustomerLineWithOnlyShippedHuHtml, /Операционные HU отсутствуют/);
assert.doesNotMatch(expandedCustomerLineWithOnlyShippedHuHtml, />Отгрузка этой строки заказа</);

const expandedInternalOrderLineHtml = pc.renderOrderLinesTable(
  [
    {
      id: 503,
      item_name: "Горчица",
      qty_ordered: 100,
      hu_presentation: {
        operational_hus: [],
        production_tasks: [{
          hu_code: "HU-INTERNAL",
          qty: 1824,
          state: { code: "AWAITING_FILL", label: "Ожидает наполнения" },
        }, {
          hu_code: "HU-INTERNAL-2",
          qty: 176,
          state: { code: "AWAITING_FILL", label: "Ожидает наполнения" },
        }],
      },
      coverage: { ordered_qty: 2000, covered_qty: 1824, missing_qty: 176 },
    },
  ],
  { order_ref: "003", order_type: "INTERNAL", order_status: "IN_PROGRESS" },
  { 503: true }
);
assert.match(expandedInternalOrderLineHtml, /HU-INTERNAL/);
assert.match(expandedInternalOrderLineHtml, /HU-INTERNAL-2/);
assert.match(
  expandedInternalOrderLineHtml,
  /HU-INTERNAL<\/td><td>1824<\/td><td>Ожидает наполнения<\/td><td>—<\/td>/
);
assert.strictEqual((expandedInternalOrderLineHtml.match(/pc-order-line-detail-table"/g) || []).length, 1);
assert.doesNotMatch(expandedInternalOrderLineHtml, /Операционные HU отсутствуют|>Производство</);
assert.doesNotMatch(expandedInternalOrderLineHtml, /Движение HU|Привязка|PRD/);
assert.match(expandedInternalOrderLineHtml, />Итог</);
assert.match(expandedInternalOrderLineHtml, />Заказано</);
assert.match(expandedInternalOrderLineHtml, />Выпущено</);
assert.match(expandedInternalOrderLineHtml, />Не хватает</);
assert.doesNotMatch(expandedInternalOrderLineHtml, /Резерв этого заказа/);
assert.match(expandedInternalOrderLineHtml, />Паллеты по товару</);
assert.doesNotMatch(expandedInternalOrderLineHtml, />Отгрузка этой строки заказа</);
assert.doesNotMatch(expandedInternalOrderLineHtml, /Отгружено по строке/);
assert.doesNotMatch(expandedInternalOrderLineHtml, /По этой строке заказа отгрузки нет/);

const orderModalSource = fs.readFileSync(orderModalPath, "utf8");
assert.doesNotMatch(orderModalSource, /Операционные HU отсутствуют/);
assert.doesNotMatch(orderModalSource, /pc-order-line-detail-title">Производство/);

const shippedCustomerStalePalletListHtml = pc.renderOrdersTable([
  {
    id: 66,
    order_ref: "066",
    order_type: "CUSTOMER",
    order_status: "SHIPPED",
    status: "Выполнен",
    order_status_presentation: { code: "SHIPPED", label: "Выполнен" },
    has_production_pallet_plan: true,
    planned_pallet_count: 5,
    filled_pallet_count: 2,
    pallet_fill_show_completed_icon: true,
    pallet_fill_tone: "completed",
    pallet_fill_title: "Заказ полностью отгружен",
  },
]);
assert.match(shippedCustomerStalePalletListHtml, /pc-order-pallet-cell/);
assert.match(shippedCustomerStalePalletListHtml, /pc-icon-status/);
assert.match(shippedCustomerStalePalletListHtml, /pc-order-icon-cell-inner/);
assert.doesNotMatch(shippedCustomerStalePalletListHtml, /Наполнено 2 \/ 5/);

const customerInProgressPalletHtml = pc.renderOrdersTable([
  {
    id: 70,
    order_ref: "070",
    order_type: "CUSTOMER",
    order_status: "IN_PROGRESS",
    status: "В работе",
    order_status_presentation: { code: "IN_PROGRESS", label: "В работе" },
    has_production_pallet_plan: true,
    planned_pallet_count: 5,
    filled_pallet_count: 2,
    pallet_plan_status: "Наполнение идёт: 2 / 5",
  },
]);
assert.match(customerInProgressPalletHtml, /Наполнено 2 \/ 5/);
assert.match(customerInProgressPalletHtml, /pc-pallet-progress-inprogress/);

assert.strictEqual(
  pc.getOrderPalletFillingPresentation({
    has_production_pallet_plan: true,
    planned_pallet_count: 5,
    filled_pallet_count: 2,
    pallet_plan_status: "План сформирован",
  }).label,
  "Наполнено 2 / 5"
);
assert.strictEqual(
  pc.getOrderPalletFillingPresentation({
    has_production_pallet_plan: true,
    planned_pallet_count: 5,
    filled_pallet_count: 2,
    pallet_plan_status: "План сформирован",
  }).tone,
  "inprogress"
);

assert.strictEqual(
  pc.getOrderPalletFillingPresentation({
    has_production_pallet_plan: true,
    planned_pallet_count: 5,
    filled_pallet_count: 0,
  }).label,
  "Наполнено 0 / 5"
);
assert.strictEqual(
  pc.getOrderPalletFillingPresentation({
    has_production_pallet_plan: true,
    planned_pallet_count: 5,
    filled_pallet_count: 0,
  }).tone,
  "inprogress"
);

assert.strictEqual(
  pc.getOrderPalletFillingPresentation({
    has_production_pallet_plan: true,
    planned_pallet_count: 5,
    filled_pallet_count: 5,
  }).tone,
  "completed"
);

const planFormedOnlyHtml = pc.renderOrdersTable([
  {
    id: 71,
    order_ref: "071",
    order_type: "INTERNAL",
    order_status: "IN_PROGRESS",
    has_production_pallet_plan: true,
    planned_pallet_count: 4,
    filled_pallet_count: 0,
    pallet_plan_status: "План сформирован",
  },
]);
assert.doesNotMatch(planFormedOnlyHtml, />План сформирован</);
assert.match(planFormedOnlyHtml, /Наполнено 0 \/ 4/);

assert.strictEqual(
  pc.getOrderPalletFillingPresentation({
    order_type: "CUSTOMER",
    order_status: "SHIPPED",
    has_production_pallet_plan: true,
    planned_pallet_count: 5,
    filled_pallet_count: 2,
    pallet_plan_status: "Наполнение идёт: 2 / 5",
  }).iconOnly,
  true
);
assert.strictEqual(
  pc.getOrderPalletFillingPresentation({
    order_type: "CUSTOMER",
    order_status: "SHIPPED",
    has_production_pallet_plan: true,
    planned_pallet_count: 5,
    filled_pallet_count: 2,
    pallet_plan_status: "Наполнение идёт: 2 / 5",
  }).tone,
  "completed"
);

const fallbackOrder = { id: 88, order_ref: "088" };
pc.applyOrderLinePalletFillingFallback(fallbackOrder, [
  {
    planned_pallet_count: 3,
    filled_pallet_count: 3,
    pallet_planned_qty: 3600,
    pallet_filled_qty: 3600,
  },
]);
assert.strictEqual(pc.getOrderPalletFillingPresentation(fallbackOrder).label, "Наполнено 3 / 3");
assert.strictEqual(pc.getOrderPalletFillingPresentation(fallbackOrder).tone, "completed");

const reservedShipmentOrder = { id: 89, order_ref: "089", order_type: "CUSTOMER" };
pc.applyOrderLineShipmentPalletReadiness(reservedShipmentOrder, [
  {
    qty_left: 1200,
    can_ship_now: 1200,
    shortage: 0,
    production_hu_codes: ["HU-001", "HU-002"],
  },
  {
    qty_left: 600,
    can_ship_now: 600,
    shortage: 0,
    production_hu_codes: ["HU-003"],
  },
]);
assert.strictEqual(
  pc.getOrderPalletFillingPresentation(reservedShipmentOrder).title,
  "К отгрузке готово 3 из 3 паллет по заказу"
);
const reservedShipmentHtml = pc.renderOrderPalletFillingIndicator(reservedShipmentOrder);
assert.match(reservedShipmentHtml, /pc-icon-status/);
assert.match(reservedShipmentHtml, /title="К отгрузке готово 3 из 3 паллет по заказу"/);
assert.doesNotMatch(reservedShipmentHtml, />К отгрузке 3 \/ 3 паллет</);

const partialReservedShipmentOrder = { id: 90, order_ref: "090", order_type: "CUSTOMER" };
pc.applyOrderLineShipmentPalletReadiness(partialReservedShipmentOrder, [
  {
    qty_left: 1200,
    can_ship_now: 1200,
    shortage: 0,
    production_hu_codes: ["HU-004"],
  },
  {
    qty_left: 600,
    can_ship_now: 0,
    shortage: 600,
    production_hu_codes: ["HU-005"],
  },
]);
assert.strictEqual(
  pc.getOrderPalletFillingPresentation(partialReservedShipmentOrder).label,
  "К отгрузке 1 / 2 паллет"
);

const markingLabels = [
  pc.getOrderMarkingPresentation({ marking_effective_status: "PRINTED" }).label,
  pc.getOrderMarkingPresentation({ marking_effective_status: "REQUIRED" }).label,
].filter(Boolean);
assert.deepStrictEqual(markingLabels.sort(), ["Маркировка не проведена", "Маркировка проведена"].sort());

const beforeExcelPresentation = pc.getOrderMarkingPresentation({
  marking_order_id: "22222222-2222-2222-2222-222222222222",
  codes_total: 0,
  requested_quantity: 12,
  marking_completed: false,
  marking_effective_status: "REQUIRED",
  marking_label: "Маркировка не проведена",
});
assert.strictEqual(beforeExcelPresentation.label, "Маркировка не проведена");

const afterExcelPresentation = pc.getOrderMarkingPresentation({
  marking_order_id: "33333333-3333-3333-3333-333333333333",
  codes_total: 12,
  requested_quantity: 12,
  marking_completed: true,
  marking_effective_status: "PRINTED",
  marking_label: "Маркировка проведена",
});
assert.strictEqual(afterExcelPresentation.label, "Маркировка проведена");

const forbiddenMarkingLabels = ["требуется", "в работе", "частично", "не требуется"];
assert.ok(
  !forbiddenMarkingLabels.includes(beforeExcelPresentation.label.toLowerCase()),
  "до Excel список заказов должен показывать только серверный бинарный статус ЧЗ"
);
assert.ok(
  !forbiddenMarkingLabels.includes(afterExcelPresentation.label.toLowerCase()),
  "после Excel список заказов должен показывать только серверный бинарный статус ЧЗ"
);

const markingRows = pc.normalizeMarkingTaskRows([
  {
    marking_order_id: "44444444-4444-4444-4444-444444444444",
    order_id: null,
    source_type: "PRODUCTION_NEED",
    display_source: "Потребность производства",
  },
]);
assert.strictEqual(markingRows.length, 1);
assert.strictEqual(markingRows[0].source_type, "PRODUCTION_NEED");

const page = pc.trimOrdersPage(Array.from({ length: 21 }, function (_, index) {
  return { id: index + 1 };
}));
assert.strictEqual(page.rows.length, 20);
assert.strictEqual(page.hasMore, true);

const lastPage = pc.trimOrdersPage(Array.from({ length: 20 }, function (_, index) {
  return { id: index + 1 };
}));
assert.strictEqual(lastPage.rows.length, 20);
assert.strictEqual(lastPage.hasMore, false);

const selectableOrderItems = pc.filterOrderSelectableItems([
  { id: 1, name: "Активный", is_active: true },
  { id: 2, name: "Legacy без флага" },
  { id: 3, name: "Неактивный", is_active: false },
  null,
]);
assert.deepStrictEqual(
  Array.from(selectableOrderItems, function (item) { return item.id; }),
  [1, 2],
  "order autocomplete must exclude inactive items without changing the shared items API"
);

const stockPageHtml = pc.renderStock();
assert.match(stockPageHtml, /<section class="pc-card pc-stock-card">/);
assert.match(stockPageHtml, /stockCreateProductionOrderBtn/);
assert.match(stockPageHtml, /Пополнение склада/);
assert.match(stockPageHtml, /Подготовить заказ/);
assert.doesNotMatch(stockPageHtml, /Сформировать заказ/);
assert.match(stockPageHtml, /data-stock-section="replenishment"/);
assert.match(stockPageHtml, /data-stock-section="list"/);
assert.ok(
  stockPageHtml.indexOf('data-stock-section="replenishment"') < stockPageHtml.indexOf('id="stockSearchInput"'),
  "replenishment preview must be structurally separated from warehouse search"
);
assert.match(stockPageHtml, /Товары на складе/);
assert.match(stockPageHtml, /data-stock-section="list"[\s\S]*id="stockSearchInput"[\s\S]*id="stockStatus"/);
assert.match(
  fs.readFileSync(stockPath, "utf8"),
  /setStatus\("Позиций: " \+ rows\.length\)/,
  "filtered warehouse position count must stay with the warehouse list"
);
assert.doesNotMatch(stockPageHtml, /Показать производственный план/);

const replenishmentReadyHtml = pc.renderStockReplenishmentPreview({ status: "ready", count: 2 });
assert.match(replenishmentReadyHtml, /pc-stock-replenishment-icon/);
assert.match(replenishmentReadyHtml, /<svg/);
assert.match(replenishmentReadyHtml, /pc-stock-replenishment-card is-ready/);
assert.match(replenishmentReadyHtml, /Требуется пополнение · 2 позиции/);
assert.doesNotMatch(replenishmentReadyHtml, /Рассчитать, какие товары нужно добавить/);
assert.doesNotMatch(replenishmentReadyHtml, /disabled aria-disabled="true"/);
[
  [1, "1 позиция"],
  [2, "2 позиции"],
  [5, "5 позиций"],
  [11, "11 позиций"],
  [21, "21 позиция"]
].forEach(function (sample) {
  assert.match(pc.renderStockReplenishmentPreview({ status: "ready", count: sample[0] }), new RegExp(sample[1]));
});

const replenishmentEmptyHtml = pc.renderStockReplenishmentPreview({ status: "empty", count: 0 });
assert.match(replenishmentEmptyHtml, /pc-stock-replenishment-card is-empty/);
assert.match(replenishmentEmptyHtml, /Дополнительное пополнение не требуется/);
assert.match(replenishmentEmptyHtml, /disabled aria-disabled="true"/);
assert.doesNotMatch(replenishmentEmptyHtml, /Требуется пополнение/);

const replenishmentLoadingHtml = pc.renderStockReplenishmentPreview({ status: "loading" });
assert.match(replenishmentLoadingHtml, /pc-stock-replenishment-card is-loading/);
assert.match(replenishmentLoadingHtml, />Загрузка…</);
assert.match(replenishmentLoadingHtml, /disabled aria-disabled="true"/);

const replenishmentErrorHtml = pc.renderStockReplenishmentPreview({ status: "error" });
assert.match(replenishmentErrorHtml, /pc-stock-replenishment-card is-error/);
assert.match(replenishmentErrorHtml, /Не удалось загрузить предпросмотр/);
assert.match(replenishmentErrorHtml, /disabled aria-disabled="true"/);
assert.doesNotMatch(replenishmentErrorHtml, /Требуется пополнение/);
assert.doesNotMatch(replenishmentErrorHtml, /пополнение не требуется/);
assert.match(
  fs.readFileSync(stockPath, "utf8"),
  /loadProductionNeedCreateOrdersPreview\(\)[\s\S]*status:\s*rows\.length \? "ready" : "empty"/,
  "replenishment preview state must remain server-preview-driven"
);
assert.ok(
  !pc.getEnabledViews().includes("production-need"),
  "separate production need tab must be hidden from normal PC navigation"
);
assert.match(
  fs.readFileSync(stockPath, "utf8"),
  /\/api\/reports\/warehouse-production-state/,
  "stock page must load warehouse-production-state"
);
assert.match(
  fs.readFileSync(stockPath, "utf8"),
  /Не удалось загрузить объединённое состояние склада/,
  "warehouse-production-state endpoint error must show readable message"
);

function operatorPresentation(code, label, productionTask) {
  const presentation = {};
  presentation[productionTask ? "production_task" : "operational_hu"] = { state: { code, label } };
  return presentation;
}

const warehouseStockRow = pc.mapWarehouseProductionStateRow({
  item_id: 10,
  item_name: "Товар 1",
  barcode: "SKU-001",
  gtin: "04607186951520",
  base_uom: "шт",
  item_type: "Готовая продукция",
  stock_qty: 17,
  min_stock_qty: 20,
  below_min_qty: 3,
  customer_remaining_to_ship_qty: 5,
  hu_rows: [
    { location: "FG-01", hu_code: " hu-000001 ", qty: 7, reserved_customer_order_ref: "C-100", operator_presentation: operatorPresentation("ON_STOCK", "На складе", false) },
    { location: "FG-01", hu_code: "HU-000001", qty: 5, reserved_customer_order_ref: "C-100", operator_presentation: operatorPresentation("ON_STOCK", "На складе", false) },
    { location: "FG-02", hu_code: "HU-000004", qty: 2, reserved_customer_order_ref: "C-200", origin_internal_order_ref: "I-LOWER", operator_presentation: operatorPresentation("RESERVED", "Зарезервирован", false) },
    { location: "FG-03", hu_code: "hu-000004", qty: 3, reserved_customer_order_ref: "C-201", origin_internal_order_ref: "I-LOWER", operator_presentation: operatorPresentation("RESERVED", "Зарезервирован", false) }
  ],
  production_receipts: [
    { pallet_id: 1, hu_code: "HU-000001", pallet_status: "FILLED", qty: 99, source_order_ref: "P-LOWER", operator_presentation: operatorPresentation("ON_STOCK", "На складе", false) },
    { pallet_id: 2, hu_code: "HU-000002", pallet_status: "PLANNED", qty: 3, source_order_ref: "P-2", operator_presentation: operatorPresentation("AWAITING_FILL", "Ожидает наполнения", true) },
    { pallet_id: 2, hu_code: "hu-000002", pallet_status: "PRINTED", qty: 4, source_order_ref: "P-2", operator_presentation: operatorPresentation("AWAITING_FILL", "Ожидает наполнения", true) },
    { pallet_id: 3, hu_code: "HU-000003", pallet_status: "PLANNED", qty: 5, operator_presentation: operatorPresentation("INCONSISTENT", "Несогласованное состояние", false) },
    { pallet_id: 4, hu_code: "hu-000003", pallet_status: "FILLED", qty: 6, operator_presentation: operatorPresentation("INCONSISTENT", "Несогласованное состояние", false) }
  ]
});
assert.strictEqual(warehouseStockRow.customerRemainingToShipQty, 5);
assert.strictEqual(warehouseStockRow.palletCount, 4, "pallet count must use normalized HU identities");
assert.strictEqual(warehouseStockRow.palletSummary, "4 паллеты", "unknown pallet quantity must suppress a partial total");
const mergedLedgerPallet = warehouseStockRow.palletRows.find(function (row) { return row.huCode === "HU-000001"; });
assert.strictEqual(mergedLedgerPallet.qty, 12, "ledger item quantities must be summed and win over production quantity");
assert.strictEqual(mergedLedgerPallet.location, "FG-01");
assert.strictEqual(mergedLedgerPallet.orderRef, "C-100", "reserved order ref must win over lower-priority sources");
const multiLocationPallet = warehouseStockRow.palletRows.find(function (row) { return row.huCode === "HU-000004"; });
assert.strictEqual(multiLocationPallet.qty, 5);
assert.strictEqual(multiLocationPallet.location, "Несколько локаций");
assert.strictEqual(multiLocationPallet.orderRef, "—", "ambiguous refs must not fall back to a lower-priority source");
const samePalletProduction = warehouseStockRow.palletRows.find(function (row) { return row.huCode === "HU-000002"; });
assert.strictEqual(samePalletProduction.qty, 7, "same pallet item rows may be aggregated");
const ambiguousProduction = warehouseStockRow.palletRows.find(function (row) { return row.huCode === "HU-000003"; });
assert.strictEqual(ambiguousProduction.qtyKnown, false, "different pallet ids must not be summed as one physical identity");
assert.strictEqual(ambiguousProduction.qtyDisplay, "—");

const expandedStockHtml = pc.renderStockTable([warehouseStockRow], { 10: true }, { status: "loading", rows: [] });
assert.match(expandedStockHtml, /data-sort-key="itemName"[^>]*>Товар/);
assert.match(expandedStockHtml, /data-sort-key="stockQty"[^>]*>На складе/);
assert.match(expandedStockHtml, /data-sort-key="customerRemainingToShipQty"[^>]*>Осталось отгрузить/);
assert.doesNotMatch(expandedStockHtml, /Заказы клиентов/);
assert.match(expandedStockHtml, /data-sort-key="palletCount"[^>]*>Паллеты/);
assert.doesNotMatch(expandedStockHtml, /<button[^>]*>Минимум|<th>Потребность<\/th>|<th>План<\/th>/);
assert.match(expandedStockHtml, /colspan="4" class="pc-stock-detail-cell"/);
assert.match(expandedStockHtml, /pc-stock-detail-layout/);
assert.match(expandedStockHtml, /Минимальный запас: 20 шт/);
assert.match(expandedStockHtml, /Ниже минимального запаса на 3 шт/);
assert.match(expandedStockHtml, /pc-stock-below-dot[^>]*aria-label="Ниже минимального запаса"[^>]*title="Ниже минимального запаса"/);
assert.strictEqual((expandedStockHtml.match(/class="pc-stock-detail-warning is-unknown"/g) || []).length, 1);
assert.match(expandedStockHtml, /Проверяем необходимость дополнительного пополнения…/);
assert.ok(warehouseStockRow.belowMinQty > 0, "semantic independence case must retain the current item deficit");
assert.match(replenishmentEmptyHtml, /Дополнительное пополнение не требуется/);
assert.match(expandedStockHtml, /<th>Паллета<\/th><th>Статус<\/th><th class="pc-num">Кол-во<\/th><th>Заказ<\/th><th>Локация<\/th>/);
assert.match(expandedStockHtml, /Несколько локаций/);
assert.match(expandedStockHtml, /Ожидает наполнения/);
assert.match(expandedStockHtml, /Несогласованное состояние/);
assert.doesNotMatch(expandedStockHtml, /Складские HU|План \/ производство|Расчёт потребности|<th>PRD<\/th>|CUSTOMER|INTERNAL/);

const canonicalStates = [
  ["AWAITING_FILL", "Ожидает наполнения", true],
  ["ON_STOCK", "На складе", false],
  ["RESERVED", "Зарезервирован", false],
  ["AWAITING_SHIPMENT", "Ожидает отгрузки", false],
  ["SHIPPED", "Отгружен", false],
  ["INCONSISTENT", "Несогласованное состояние", false]
];
const canonicalStateRow = pc.mapWarehouseProductionStateRow({
  item_id: 20,
  item_name: "Статусы",
  base_uom: "шт",
  production_receipts: canonicalStates.map(function (state, index) {
    return {
      pallet_id: index + 1,
      hu_code: "HU-STATE-" + index,
      qty: 1,
      pallet_status: ["PLANNED", "PRINTED", "FILLED"][index % 3],
      operator_presentation: operatorPresentation(state[0], state[1], state[2])
    };
  })
});
const canonicalStateHtml = pc.renderStockTable([canonicalStateRow], { 20: true });
canonicalStates.forEach(function (state) { assert.match(canonicalStateHtml, new RegExp(state[1])); });
assert.strictEqual(
  canonicalStateRow.palletRows.map(function (row) { return row.stateCode; }).join(","),
  "INCONSISTENT,ON_STOCK,RESERVED,AWAITING_SHIPMENT,AWAITING_FILL,SHIPPED",
  "canonical state codes must pass through unchanged in presentation display-order"
);
assert.doesNotMatch(canonicalStateHtml, />PLANNED<|>PRINTED<|>FILLED</);
assert.match(canonicalStateHtml, /pc-stock-pallet-state--on-stock[^>]*>На складе/);
assert.match(canonicalStateHtml, /pc-stock-pallet-state--awaiting-fill[^>]*>Ожидает наполнения/);
assert.match(canonicalStateHtml, /pc-stock-pallet-state--awaiting-shipment[^>]*>Ожидает отгрузки/);
assert.match(canonicalStateHtml, /pc-stock-pallet-state--inconsistent[^>]*>Несогласованное состояние/);
assert.ok(canonicalStateRow.palletStateSummary.indexOf("На складе: 1") < canonicalStateRow.palletStateSummary.indexOf("Ожидает отгрузки: 1"));
assert.ok(canonicalStateRow.palletStateSummary.indexOf("Ожидает отгрузки: 1") < canonicalStateRow.palletStateSummary.indexOf("Ожидает наполнения: 1"));

const palletDisplayOrderRow = pc.mapWarehouseProductionStateRow({
  item_id: 28,
  item_name: "Порядок паллет",
  base_uom: "шт",
  production_receipts: [
    { pallet_id: 1, hu_code: "HU-FILL", qty: 1, operator_presentation: operatorPresentation("AWAITING_FILL", "Ожидает наполнения", true) },
    { pallet_id: 2, hu_code: "HU-ON-Z", qty: 1, operator_presentation: operatorPresentation("ON_STOCK", "На складе", false) },
    { pallet_id: 3, hu_code: "HU-SHIP", qty: 1, operator_presentation: operatorPresentation("AWAITING_SHIPMENT", "Ожидает отгрузки", false) },
    { pallet_id: 4, hu_code: "HU-INCONSISTENT", qty: 1, operator_presentation: operatorPresentation("INCONSISTENT", "Несогласованное состояние", false) },
    { pallet_id: 5, hu_code: "HU-ON-A", qty: 1, operator_presentation: operatorPresentation("ON_STOCK", "На складе", false) }
  ]
});
assert.strictEqual(
  palletDisplayOrderRow.palletRows.map(function (row) { return row.huCode; }).join(","),
  "HU-INCONSISTENT,HU-ON-A,HU-ON-Z,HU-SHIP,HU-FILL",
  "pallet rows must sort by canonical display-order and then HU"
);

const conflictingStateRow = pc.mapWarehouseProductionStateRow({
  item_id: 21,
  item_name: "Конфликт статуса",
  base_uom: "шт",
  hu_rows: [{ hu_code: "HU-CONFLICT", qty: 2, operator_presentation: operatorPresentation("ON_STOCK", "На складе", false) }],
  production_receipts: [{ pallet_id: 21, hu_code: "hu-conflict", qty: 2, operator_presentation: operatorPresentation("RESERVED", "Зарезервирован", false) }]
});
assert.strictEqual(conflictingStateRow.palletRows[0].stateCode, "");
assert.strictEqual(conflictingStateRow.palletRows[0].stateLabel, "—", "client must not arbitrate conflicting canonical states");
assert.strictEqual(conflictingStateRow.palletCount, 1, "unknown state pallet must remain in the deduplicated count");
assert.strictEqual(conflictingStateRow.palletStateSummary, "Состояние не определено: 1");
const conflictingStateHtml = pc.renderStockTable([conflictingStateRow], { 21: true });
assert.match(conflictingStateHtml, /pc-stock-pallet-state--unknown[^>]*>—/);

const knownAggregateRow = pc.mapWarehouseProductionStateRow({
  item_id: 22,
  item_name: "Известный агрегат",
  base_uom: "кг",
  production_receipts: [
    { pallet_id: 1, hu_code: "HU-KG-1", qty: 3, operator_presentation: operatorPresentation("AWAITING_FILL", "Ожидает наполнения", true) },
    { pallet_id: 2, hu_code: "HU-KG-2", qty: 4, operator_presentation: operatorPresentation("AWAITING_FILL", "Ожидает наполнения", true) }
  ]
});
assert.strictEqual(knownAggregateRow.palletSummary, "2 паллеты");
assert.strictEqual(knownAggregateRow.palletStateSummary, "Ожидает наполнения: 2");
assert.strictEqual(pc.mapWarehouseProductionStateRow({ item_id: 23, base_uom: "шт", production_receipts: [
  { pallet_id: 1, hu_code: "HU-ONE", qty: 1, operator_presentation: operatorPresentation("AWAITING_FILL", "Ожидает наполнения", true) }
] }).palletSummary, "1 паллета");
assert.strictEqual(pc.mapWarehouseProductionStateRow({ item_id: 24, base_uom: "шт", production_receipts: [1, 2, 3, 4, 5].map(function (id) {
  return { pallet_id: id, hu_code: "HU-FIVE-" + id, qty: 1, operator_presentation: operatorPresentation("AWAITING_FILL", "Ожидает наполнения", true) };
}) }).palletSummary, "5 паллет");

function mappedPalletCount(count) {
  return pc.mapWarehouseProductionStateRow({
    item_id: 100 + count,
    base_uom: "шт",
    production_receipts: Array.from({ length: count }, function (_, index) {
      return {
        pallet_id: index + 1,
        hu_code: "HU-COUNT-" + count + "-" + index,
        qty: 1,
        operator_presentation: operatorPresentation("AWAITING_FILL", "Ожидает наполнения", true)
      };
    })
  });
}

assert.strictEqual(mappedPalletCount(1).palletSummary, "1 паллета");
assert.strictEqual(mappedPalletCount(2).palletSummary, "2 паллеты");
assert.strictEqual(mappedPalletCount(5).palletSummary, "5 паллет");
assert.strictEqual(mappedPalletCount(11).palletSummary, "11 паллет");
assert.strictEqual(mappedPalletCount(21).palletSummary, "21 паллета");

const palletBreakdownRow = pc.mapWarehouseProductionStateRow({
  item_id: 27,
  item_name: "Сводка паллет",
  base_uom: "шт",
  production_receipts: Array.from({ length: 8 }, function (_, index) {
    const onStock = index === 7;
    return {
      pallet_id: index + 1,
      hu_code: "HU-SUMMARY-" + (index + 1),
      qty: 1824,
      operator_presentation: operatorPresentation(
        onStock ? "ON_STOCK" : "AWAITING_FILL",
        onStock ? "На складе" : "Ожидает наполнения",
        !onStock
      )
    };
  })
});
assert.strictEqual(palletBreakdownRow.palletSummary, "8 паллет");
assert.strictEqual(palletBreakdownRow.palletStateSummary, "На складе: 1 · Ожидает наполнения: 7");
const palletBreakdownHtml = pc.renderStockTable([palletBreakdownRow], {});
assert.match(palletBreakdownHtml, /<div class="pc-stock-pallet-count">8 паллет<\/div>/);
assert.match(palletBreakdownHtml, /На складе: 1 · Ожидает наполнения: 7/);
assert.doesNotMatch(palletBreakdownHtml, /14 592 шт/);

const emptyStockRow = pc.mapWarehouseProductionStateRow({
  item_id: 25,
  item_name: "Пустой склад",
  base_uom: "шт",
  stock_qty: 0,
  min_stock_qty: 0,
  below_min_qty: 0.000001,
  customer_remaining_to_ship_qty: 0,
  hu_rows: [],
  production_receipts: []
});
const emptyStockHtml = pc.renderStockTable([emptyStockRow], { 25: true });
assert.match(emptyStockHtml, /Нет на складе/);
assert.match(emptyStockHtml, /Паллет нет/);
assert.doesNotMatch(emptyStockHtml, /Ниже минимального запаса/);
assert.doesNotMatch(emptyStockHtml, /pc-stock-below-dot/);
assert.doesNotMatch(emptyStockHtml, /Свободный остаток/);

const negativeStockRow = pc.mapWarehouseProductionStateRow({
  item_id: 26,
  item_name: "Отрицательный остаток",
  base_uom: "кг",
  stock_qty: -600,
  hu_rows: [],
  production_receipts: []
});
assert.strictEqual(negativeStockRow.stockQtyDisplay, "-600 кг");
const negativeStockHtml = pc.renderStockTable([negativeStockRow], { 26: true });
assert.match(negativeStockHtml, /-600 кг/);
assert.doesNotMatch(negativeStockHtml, /Нет на складе/);

const freeStockPresentationRow = pc.mapWarehouseProductionStateRow({
  item_id: 29,
  item_name: "Свободный остаток",
  base_uom: "шт.",
  stock_qty: 2400,
  free_qty: 1200,
  min_stock_qty: 3600,
  below_min_qty: 2400,
  hu_rows: [{
    hu_code: "HU-LONG-LOCATION",
    qty: 2400,
    location: "СКЛАД-ГОТОВОЙ-ПРОДУКЦИИ-ОЧЕНЬ-ДЛИННАЯ-ЛОКАЦИЯ",
    operator_presentation: operatorPresentation("ON_STOCK", "На складе", false)
  }],
  production_receipts: []
});
assert.strictEqual(freeStockPresentationRow.stockQtyDisplay, "2\u00a0400 шт.");
assert.strictEqual(freeStockPresentationRow.freeQtyDisplay, "1\u00a0200 шт.");
assert.strictEqual(freeStockPresentationRow.minStockQtyDisplay, "3\u00a0600 шт.");
assert.strictEqual(freeStockPresentationRow.belowMinQtyDisplay, "2\u00a0400 шт.");
const coveredStockHtml = pc.renderStockTable([freeStockPresentationRow], { 29: true }, { status: "success", rows: [] });
assert.match(coveredStockHtml, /pc-stock-below-dot is-covered/);
assert.match(coveredStockHtml, /pc-stock-detail-warning is-covered/);
assert.match(coveredStockHtml, /Свободный остаток:[\s\S]*1\s200 шт\./);
assert.match(coveredStockHtml, /Ниже минимального запаса на 2\s400 шт\./);
assert.match(coveredStockHtml, /Пополнение уже запланировано/);
assert.match(coveredStockHtml, /Дополнительный заказ не требуется/);
assert.doesNotMatch(coveredStockHtml, /Требуется дополнительное пополнение/);
assert.match(coveredStockHtml, /pc-stock-pallet-location">СКЛАД-ГОТОВОЙ-ПРОДУКЦИИ-ОЧЕНЬ-ДЛИННАЯ-ЛОКАЦИЯ/);

const actionRequiredStockHtml = pc.renderStockTable([freeStockPresentationRow], { 29: true }, {
  status: "success",
  rows: [{ itemId: "29", qtyToCreate: 1800 }]
});
assert.match(actionRequiredStockHtml, /pc-stock-below-dot is-action-required/);
assert.match(actionRequiredStockHtml, /pc-stock-detail-warning is-action-required/);
assert.match(actionRequiredStockHtml, /Ниже минимального запаса на 2\s400 шт\./);
assert.match(actionRequiredStockHtml, /Требуется дополнительное пополнение: 1\s800 шт\./);
assert.doesNotMatch(actionRequiredStockHtml, /Требуется дополнительное пополнение: 2\s400 шт\./);
assert.doesNotMatch(actionRequiredStockHtml, /Дополнительный заказ не требуется/);

const otherItemPreviewHtml = pc.renderStockTable([freeStockPresentationRow], { 29: true }, {
  status: "success",
  rows: [{ itemId: 30, qtyToCreate: 1800 }]
});
assert.match(otherItemPreviewHtml, /pc-stock-below-dot is-covered/);
assert.doesNotMatch(otherItemPreviewHtml, /is-action-required/);

const loadingStockHtml = pc.renderStockTable([freeStockPresentationRow], { 29: true }, { status: "loading", rows: [] });
assert.match(loadingStockHtml, /pc-stock-below-dot is-unknown/);
assert.match(loadingStockHtml, /pc-stock-detail-warning is-unknown/);
assert.match(loadingStockHtml, /Проверяем необходимость дополнительного пополнения…/);
assert.doesNotMatch(loadingStockHtml, /Пополнение уже запланировано|Требуется дополнительное пополнение:/);

const errorStockHtml = pc.renderStockTable([freeStockPresentationRow], { 29: true }, { status: "error", rows: [] });
assert.match(errorStockHtml, /pc-stock-below-dot is-unknown/);
assert.match(errorStockHtml, /Не удалось проверить необходимость дополнительного пополнения/);
assert.doesNotMatch(errorStockHtml, /Пополнение уже запланировано|Требуется дополнительное пополнение:/);

const normalStockRow = pc.mapWarehouseProductionStateRow({
  item_id: 31,
  base_uom: "шт.",
  stock_qty: 4200,
  free_qty: 4200,
  min_stock_qty: 3600,
  below_min_qty: 0,
  hu_rows: [],
  production_receipts: []
});
const normalStockHtml = pc.renderStockTable([normalStockRow], { 31: true }, {
  status: "success",
  rows: [{ itemId: 31, qtyToCreate: 1800 }]
});
assert.doesNotMatch(normalStockHtml, /pc-stock-below-dot|pc-stock-detail-warning/);

const globalReadyForOtherItemHtml = pc.renderStockReplenishmentPreview({ status: "ready", count: 1 });
assert.match(globalReadyForOtherItemHtml, /Требуется пополнение · 1 позиция/);
assert.match(otherItemPreviewHtml, /pc-stock-below-dot is-covered/);

const negativeFreeStockRow = pc.mapWarehouseProductionStateRow({
  item_id: 30,
  base_uom: "кг",
  stock_qty: 1,
  free_qty: -600,
  min_stock_qty: 600,
  below_min_qty: 1200,
  hu_rows: [],
  production_receipts: []
});
assert.strictEqual(negativeFreeStockRow.freeQtyDisplay, "-600 кг");
assert.doesNotMatch(pc.renderStockTable([negativeFreeStockRow], { 30: true }), /Свободный остаток:[\s\S]*Нет на складе/);

const stockSource = fs.readFileSync(stockPath, "utf8");
assert.match(stockSource, /renderSortableHeader\("stock", "palletCount", "Паллеты"\)/);
assert.match(stockSource, /palletCount:\s*\{ type: "number", getValue: function \(row\) \{ return row\.palletCount; \} \}/);
assert.strictEqual(
  (stockSource.match(/deps\.loadProductionNeedCreateOrdersPreview\(\)/g) || []).length,
  1,
  "item-level attention must reuse the one existing replenishment preview request"
);
assert.match(stockSource, /cachedItemReplenishmentContext = createItemReplenishmentContext\(\{ status: "success", rows: rows \}\);[\s\S]*renderRows\(\);/);
const baseCardCss = styles.match(/\.pc-card\s*\{([^}]*)\}/);
assert.ok(baseCardCss, "global card CSS must exist");
assert.match(baseCardCss[1], /width:\s*fit-content/);
const stockCardCss = styles.match(/\.pc-stock-card\s*\{([^}]*)\}/);
assert.ok(stockCardCss, "stock-specific card width CSS must exist");
assert.match(stockCardCss[1], /width:\s*min\(var\(--pc-content-max\),\s*calc\(100vw - \(var\(--pc-page-gutter\) \* 2\)\)\)/);
assert.match(stockCardCss[1], /max-width:\s*min\(var\(--pc-content-max\),\s*calc\(100vw - \(var\(--pc-page-gutter\) \* 2\)\)\)/);
assert.doesNotMatch(stockCardCss[1], /fit-content/);
assert.match(styles, /\.pc-stock-detail-layout\s*\{[^}]*grid-template-columns:\s*minmax\(180px, 240px\) minmax\(0, 1fr\)/s);
assert.match(styles, /\.pc-stock-detail-layout\s*\{[^}]*width:\s*100%[^}]*min-width:\s*0/s);
assert.match(styles, /\.pc-stock-pallet-section\s*\{[^}]*min-width:\s*0/s);
assert.match(styles, /\.pc-stock-pallet-section \.pc-stock-detail-table\s*\{[^}]*width:\s*100%/s);
assert.match(styles, /\.pc-stock-pallet-section \.pc-stock-detail-table\s*\{[^}]*min-width:\s*0/s);
assert.match(styles, /\.pc-stock-pallet-location\s*\{[^}]*overflow-wrap:\s*anywhere/s);
const detailBlockCss = styles.match(/\.pc-stock-detail-block\s*\{([^}]*)\}/);
assert.ok(detailBlockCss, "expanded detail wrapper CSS must exist");
assert.match(detailBlockCss[1], /width:\s*100%/);
assert.match(detailBlockCss[1], /border-left:/);
assert.match(detailBlockCss[1], /border-right:/);
assert.match(detailBlockCss[1], /border-bottom:/);
assert.match(detailBlockCss[1], /border-radius:\s*0 0 [^;]+;/);
assert.match(styles, /\.pc-stock-detail-layout\s*\{[^}]*grid-template-columns:\s*1fr/s);
const minStockWarningCss = styles.match(/\.pc-stock-detail-warning\s*\{([^}]*)\}/);
assert.ok(minStockWarningCss, "min-stock warning CSS must exist");
assert.match(minStockWarningCss[1], /border:/);
assert.match(minStockWarningCss[1], /color:/);
assert.match(minStockWarningCss[1], /background:/);
["covered", "action-required", "unknown"].forEach(function (variant) {
  assert.match(styles, new RegExp("\\.pc-stock-below-dot\\.is-" + variant + "\\s*\\{"));
  assert.match(styles, new RegExp("\\.pc-stock-detail-warning\\.is-" + variant + "\\s*\\{"));
});
["ready", "empty", "error"].forEach(function (variant) {
  assert.match(styles, new RegExp("\\.pc-stock-replenishment-card\\.is-" + variant + "\\s*\\{"));
  assert.match(styles, new RegExp("\\.pc-stock-replenishment-card\\.is-" + variant + " \\.pc-stock-replenishment-status\\s*\\{"));
});
["on-stock", "reserved", "awaiting-shipment", "awaiting-fill", "shipped", "inconsistent", "unknown"].forEach(function (variant) {
  assert.match(styles, new RegExp("\\.pc-stock-pallet-state--" + variant + "\\s*\\{"));
});
assert.strictEqual(pc.translatePalletStatus("Cancelled"), "Отменена");
assert.strictEqual(pc.translatePalletStatus("CUSTOM"), "CUSTOM");

const canonicalSortedOrders = pc.sortOrdersNewestFirst([
  {
    id: 58,
    order_ref: "058",
    order_type: "CUSTOMER",
    order_status: "IN_PROGRESS",
    created_at: "2026-05-12T10:00:00Z"
  },
  {
    id: 56,
    order_ref: "056",
    order_type: "INTERNAL",
    order_status: "IN_PROGRESS",
    created_at: "2026-05-11T10:00:00Z"
  },
  {
    id: 57,
    order_ref: "057",
    order_type: "CUSTOMER",
    order_status: "ACCEPTED",
    due_date: "2026-05-15",
    created_at: "2026-05-10T10:00:00Z"
  },
  {
    id: 53,
    order_ref: "053",
    order_type: "CUSTOMER",
    order_status: "ACCEPTED",
    due_date: "2026-05-14",
    created_at: "2026-05-09T10:00:00Z"
  },
  {
    id: 55,
    order_ref: "055",
    order_type: "CUSTOMER",
    order_status: "SHIPPED",
    created_at: "2026-05-08T10:00:00Z"
  }
]);
assert.deepStrictEqual(
  canonicalSortedOrders.map(function (row) { return row.order_ref; }),
  ["058", "056", "057", "053", "055"],
  "fallback client-side sort должен совпадать с серверным правилом: status, затем order_ref DESC"
);

const pendingAndRealOrders = pc.sortOrdersNewestFirst([
  {
    id: 112,
    order_ref: "112",
    order_type: "INTERNAL",
    order_status: "IN_PROGRESS",
    created_at: "2026-05-11T10:00:00Z"
  },
  {
    id: 117,
    order_ref: "117",
    order_type: "CUSTOMER",
    order_status: "IN_PROGRESS",
    created_at: "2026-05-12T10:00:00Z"
  },
  {
    id: "request:118",
    order_ref: "118",
    order_type: "CUSTOMER",
    status_code: "PENDING_CONFIRMATION",
    is_pending_confirmation: true,
    created_at: "2026-05-10T10:00:00Z"
  }
]);
assert.deepStrictEqual(
  pendingAndRealOrders.map(function (row) { return row.order_ref; }),
  ["118", "117", "112"],
  "pending promotion applies only to synthetic confirmation rows; real INTERNAL orders stay in canonical order"
);

assert.strictEqual(
  pc.buildOrdersUrl("abc 001", 21, 20),
  "/api/orders?include_internal=1&include_pending_requests=1&limit=21&offset=20&q=abc%20001"
);

assert.strictEqual(
  pc.getProductionNeedCreateOrdersRefreshUrl(),
  "/api/orders?include_internal=1&include_pending_requests=1&limit=21&offset=0"
);
assert.ok(
  !pc.getProductionNeedCreateOrdersRefreshUrl().includes("/api/marking/orders"),
  "create-orders must refresh orders, not marking queue"
);

const productionNeedRow = pc.mapProductionNeedRow({
  item_id: 1001,
  item_name: "Горчица",
  gtin: "04607186951520",
  free_stock_qty: 100,
  min_stock_qty: 300,
  to_close_orders_qty: 200,
  to_min_stock_qty: 150,
  qty_to_create: 125,
  can_create_order: true,
  reason: "Требуется пополнение склада до минимального остатка.",
  open_internal_order_qty: 75,
  planned_pallet_qty: 100,
  filled_pallet_qty: 25,
  planned_pallet_count: 2,
  filled_pallet_count: 1,
  remaining_pallet_qty: 75,
  total_to_make_qty: 350,
});
assert.strictEqual(productionNeedRow.openInternalOrderQty, 75);
assert.strictEqual(productionNeedRow.filledPalletQty, 25);
assert.strictEqual(productionNeedRow.qtyToCreate, 125);
assert.strictEqual(productionNeedRow.canCreateOrder, true);

const productionNeedHtml = pc.renderProductionNeedTable([productionNeedRow]);
assert.match(productionNeedHtml, /Во внутренних заказах/);
assert.match(productionNeedHtml, /Наполнено паллетами/);
assert.match(productionNeedHtml, /1 \/ 2 паллет, 25 шт/);

const productionNeedPreviewHtml = pc.renderProductionNeedPreviewModalContent([
  {
    itemId: 1001,
    itemName: "Горчица",
    gtin: "04607186951520",
    qtyToCreate: 125,
    reason: "Этот текст причины не должен отображаться",
  },
]);
assert.match(productionNeedPreviewHtml, /Предпросмотр внутреннего заказа/);
assert.match(productionNeedPreviewHtml, /Количество можно изменить\. Строки с 0 не будут созданы\./);
assert.strictEqual(
  (productionNeedPreviewHtml.match(/<th(?:\s|>)/g) || []).length,
  3,
  "production need preview must contain exactly three columns"
);
assert.match(productionNeedPreviewHtml, /<th>Номенклатура<\/th>/);
assert.match(productionNeedPreviewHtml, /<th>GTIN<\/th>/);
assert.match(productionNeedPreviewHtml, /<th class="pc-num">Количество<\/th>/);
assert.doesNotMatch(productionNeedPreviewHtml, /Причина/);
assert.doesNotMatch(productionNeedPreviewHtml, /Этот текст причины не должен отображаться/);
assert.match(productionNeedPreviewHtml, />Создать заказ<\/button>/);
assert.doesNotMatch(productionNeedPreviewHtml, />Подтвердить<\/button>/);
assert.match(productionNeedPreviewHtml, />×<\/button>/);
assert.match(productionNeedPreviewHtml, /id="productionNeedPreviewCancelBtn"/);
assert.match(productionNeedPreviewHtml, /id="productionNeedPreviewConfirmBtn"/);
assert.match(styles, /\.pc-production-need-preview-table\s*\{[^}]*width:\s*100%;[^}]*table-layout:\s*fixed;/s);
assert.match(styles, /\.pc-modal-footer\.pc-production-need-preview-footer\s*\{[^}]*display:\s*flex;[^}]*justify-content:\s*flex-end;[^}]*border-top:\s*1px solid/s);
assert.match(styles, /\.pc-modal-footer \.btn\.pc-production-need-preview-cancel\s*\{[^}]*border-color:\s*#dc2626;[^}]*background:\s*transparent;[^}]*color:\s*#dc2626;/s);
assert.match(styles, /\.pc-modal-footer \.btn\.pc-production-need-preview-confirm\s*\{[^}]*background:\s*var\(--pc-accent\);[^}]*color:\s*#ffffff;/s);
assert.match(
  fs.readFileSync(appPath, "utf8"),
  /\/api\/reports\/production-need\/create-orders\/preview/,
  "PC production need must use server-side preview endpoint before create"
);

const catalogHtml = pc.renderCatalogTable([
  {
    itemId: 100,
    itemName: "Горчица Русская 1 кг",
    brand: "Печагин",
    volume: "1 кг",
    barcode: "1234567890123",
    gtin: "04607186951520",
    baseUom: "шт",
  },
  {
    itemId: 101,
    itemName: "Товар без кодов",
    brand: "Бренд",
    volume: "-",
    barcode: "",
    gtin: "",
    baseUom: "шт",
  },
  {
    itemId: 102,
    itemName: "Только GTIN",
    brand: "-",
    volume: "-",
    barcode: "",
    gtin: "04609999999999",
    baseUom: "шт",
  },
]);
assert.match(catalogHtml, /pc-catalog-item-name/);
assert.match(catalogHtml, /Горчица Русская 1 кг/);
assert.match(catalogHtml, /GTIN: 04607186951520/);
assert.match(catalogHtml, /ШК: 1234567890123/);
assert.match(catalogHtml, /Только GTIN/);
assert.match(catalogHtml, /GTIN: 04609999999999/);
assert.doesNotMatch(catalogHtml, /GTIN:\s*<\/div>/);
assert.doesNotMatch(catalogHtml, /ШК:\s*<\/div>/);
assert.doesNotMatch(catalogHtml, /<th[^>]*>\s*ID\s*<\/th>/);
assert.doesNotMatch(catalogHtml, /<th[^>]*>\s*GTIN\s*<\/th>/);
assert.doesNotMatch(catalogHtml, /<th[^>]*>[^<]*ШК[^<]*<\/th>/);
assert.match(catalogHtml, /data-catalog-item-id="100"/);
assert.match(catalogHtml, /tabindex="0" role="button"/);

const catalogSource = fs.readFileSync(catalogPath, "utf8");
assert.match(catalogSource, /event\.key !== "Enter" && event\.key !== " "/);
assert.match(catalogSource, /deps\.fetchJson\("\/api\/item-types\?include_inactive=1"\)/);

const catalogHooks = context.window.FlowStockPcCatalog.testHooks;
assert.strictEqual(
  catalogHooks.formatVatRate({ default_sale_vat_rate_name: "Основная", default_sale_vat_rate: 20 }),
  "Основная — 20%"
);
assert.strictEqual(
  catalogHooks.formatVatRate({ default_sale_vat_rate_name: "Базовая 22%", default_sale_vat_rate: 22 }),
  "Базовая 22%",
  "VAT percentage already present in the rate name must not be duplicated"
);
assert.strictEqual(
  catalogHooks.formatVatRate({
    default_sale_vat_rate_name: "Базовая 22%",
    default_sale_vat_rate: 22,
    default_sale_vat_rate_is_active: false,
  }),
  "Базовая 22% (неактивна)"
);
assert.strictEqual(
  catalogHooks.formatVatRate({ default_sale_vat_rate_name: "Только название" }),
  "Только название"
);
assert.strictEqual(
  catalogHooks.formatVatRate({ default_sale_vat_rate: 10 }),
  "10%"
);
assert.deepStrictEqual(
  Array.from(catalogHooks.buildExactFilterOptions([
    { brand: "Brand" },
    { brand: "brand" },
    { brand: "Brand" },
    { brand: "A  B" },
    { brand: "A B" },
    { brand: "" },
    { brand: "   " },
  ], "brand")),
  ["A  B", "A B", "brand", "Brand"].sort(function (left, right) {
    return left.localeCompare(right, "ru", { sensitivity: "variant", numeric: true });
  }),
  "brand options must preserve exact case and internal spaces while excluding empty values"
);

const exactFilterRows = catalogHooks.buildCatalogRows([
  { id: 1, item_type_id: 10, name: "Горчица", brand: "Brand", volume: "1 л" },
  { id: 2, item_type_id: 10, name: "Горчица", brand: "brand", volume: "1 л" },
  { id: 3, item_type_id: 11, name: "Соус", brand: "Brand", volume: "2 л" },
]);
assert.deepStrictEqual(
  Array.from(catalogHooks.filterCatalogRows(exactFilterRows, {
    query: "горчица",
    typeId: 10,
    brand: "Brand",
    volume: "1 л",
  })).map(function (row) { return row.itemId; }),
  [1],
  "catalog search, type, brand and volume filters must be combined with AND"
);
assert.strictEqual(catalogHooks.retainAvailableValue("Brand", ["Brand"]), "Brand");
assert.strictEqual(catalogHooks.retainAvailableValue("Missing", ["Brand"]), "");
assert.strictEqual(
  catalogHooks.selectVisibleCatalogItems([
    { id: 1, is_active: true, item_type_is_visible_in_product_catalog: false },
    { id: 2, is_active: false, item_type_is_visible_in_product_catalog: true },
  ]).length,
  0,
  "catalog must not fall back to all items when visible item types contain no active products"
);

const productCardHtml = catalogHooks.renderProductCardContent({
  id: 100,
  name: "Соус <острый>",
  barcode: "0",
  gtin: "",
  brand: "Печагин & Ко",
  volume: "1 кг",
  shelf_life_months: 0,
  storage_conditions: "Хранить сухо\nНе замораживать <0>",
  tara_name: "Банка",
  item_type_name: "Соусы",
  base_uom_code: "шт",
  default_sale_price_gross: 12.34567,
  default_sale_vat_rate_name: "Основная",
  default_sale_vat_rate: 20,
  default_sale_vat_rate_is_active: false,
  item_type_enable_min_stock_control: true,
  min_stock_qty: 0,
  item_type_enable_marking: true,
  max_qty_per_hu: 48,
}, {
  enable_hu_distribution: true,
  enable_marking: true,
});
assert.match(productCardHtml, /Соус &lt;острый&gt;/);
assert.match(productCardHtml, /Печагин &amp; Ко/);
assert.match(productCardHtml, /Хранить сухо\nНе замораживать &lt;0&gt;/);
assert.match(productCardHtml, /Цена продажи с НДС[\s\S]*12,3457/);
assert.match(productCardHtml, /Ставка НДС[\s\S]*Основная — 20% \(неактивна\)/);
assert.match(productCardHtml, /Минимальный остаток[\s\S]*>0</);
assert.match(productCardHtml, /Макс\. в 1 HU[\s\S]*48/);
assert.match(productCardHtml, /Маркировка ЧЗ[\s\S]*Нет, GTIN не заполнен/);
assert.doesNotMatch(productCardHtml, /<input|<select|data-product-price-(?:save|edit)/);

const plainProductCardHtml = catalogHooks.renderProductCardContent({ name: "Без настроек" }, {});
assert.match(plainProductCardHtml, /SKU \/ штрихкод[\s\S]*—/);
assert.doesNotMatch(plainProductCardHtml, /Минимальный остаток|Макс\. в 1 HU|Маркировка ЧЗ/);

const customerPricesHtml = catalogHooks.renderCustomerPrices({
  loaded: true,
  loading: false,
  error: false,
  totalCount: 2,
  items: [
    { partner_name: "Клиент <1>", partner_code: "K&1", unit_price_gross: 10.5, is_active: true },
    { partner_name: "Клиент 2", partner_code: "", unit_price_gross: 0, is_active: false },
  ],
});
assert.match(customerPricesHtml, /Клиент &lt;1&gt;/);
assert.match(customerPricesHtml, /K&amp;1/);
assert.match(customerPricesHtml, /Цена с НДС/);
assert.match(customerPricesHtml, /Активна/);
assert.match(customerPricesHtml, /Неактивна/);
assert.match(customerPricesHtml, />0</);
assert.match(
  catalogHooks.renderCustomerPrices({ loaded: true, loading: false, error: false, totalCount: 0, items: [] }),
  /Индивидуальные цены клиентов не заданы\./
);
assert.match(
  catalogHooks.renderCustomerPrices({ loaded: true, loading: false, error: true, totalCount: 0, items: [] }),
  /data-product-prices-retry/
);
assert.strictEqual(
  catalogHooks.buildCustomerPricesUrl(123, 200),
  "/api/partner-item-sale-prices?item_id=123&limit=100&offset=200"
);

function runSharedModalDismissRegression() {
  const core = context.window.FlowStockPcCore;
  const connectedModals = [];
  const documentListeners = {};

  function addListener(store, type, handler) {
    if (!store[type]) store[type] = [];
    store[type].push(handler);
  }
  function removeListener(store, type, handler) {
    store[type] = (store[type] || []).filter(function (registered) { return registered !== handler; });
  }
  function createModal() {
    const listeners = {};
    return {
      isConnected: true,
      addEventListener: function (type, handler) { addListener(listeners, type, handler); },
      removeEventListener: function (type, handler) { removeListener(listeners, type, handler); },
      dispatchClick: function (target) {
        (listeners.click || []).slice().forEach(function (handler) { handler({ target: target }); });
      },
      listenerCount: function (type) { return (listeners[type] || []).length; },
    };
  }
  function dispatchEscape() {
    const event = {
      key: "Escape",
      defaultPrevented: false,
      preventDefault: function () { this.defaultPrevented = true; },
    };
    (documentListeners.keydown || []).slice().forEach(function (handler) { handler(event); });
  }

  const originalQuerySelectorAll = context.document.querySelectorAll;
  context.document.querySelectorAll = function (selector) {
    return selector === ".pc-modal" ? connectedModals.slice() : [];
  };
  context.document.addEventListener = function (type, handler) { addListener(documentListeners, type, handler); };
  context.document.removeEventListener = function (type, handler) { removeListener(documentListeners, type, handler); };

  const first = createModal();
  const second = createModal();
  connectedModals.push(first, second);
  let firstDismissals = 0;
  let secondDismissals = 0;
  let disposeFirst = function () {};
  let disposeSecond = function () {};
  disposeFirst = core.bindModalDismiss(first, function () {
    firstDismissals += 1;
    first.isConnected = false;
    connectedModals.splice(connectedModals.indexOf(first), 1);
    disposeFirst();
  });
  disposeSecond = core.bindModalDismiss(second, function () {
    secondDismissals += 1;
    second.isConnected = false;
    connectedModals.splice(connectedModals.indexOf(second), 1);
    disposeSecond();
  });

  first.dispatchClick({});
  assert.strictEqual(firstDismissals, 0, "click inside modal card must not dismiss its overlay");
  dispatchEscape();
  assert.strictEqual(firstDismissals, 0, "one Escape must leave the lower modal open");
  assert.strictEqual(secondDismissals, 1, "one Escape must dismiss only the last connected modal");
  assert.strictEqual(first.listenerCount("click"), 1, "lower modal listeners must remain active");
  dispatchEscape();
  assert.strictEqual(firstDismissals, 1, "the next Escape must dismiss the remaining modal");
  assert.strictEqual(secondDismissals, 1, "one Escape event must never dismiss two modals");
  assert.strictEqual((documentListeners.keydown || []).length, 0, "disposers must remove keydown listeners");

  const overlay = createModal();
  connectedModals.push(overlay);
  let overlayDismissals = 0;
  let disposeOverlay = function () {};
  disposeOverlay = core.bindModalDismiss(overlay, function () {
    overlayDismissals += 1;
    disposeOverlay();
  });
  overlay.dispatchClick({});
  assert.strictEqual(overlayDismissals, 0);
  overlay.dispatchClick(overlay);
  overlay.dispatchClick(overlay);
  assert.strictEqual(overlayDismissals, 1, "overlay click and disposer must be idempotent");
  assert.strictEqual(overlay.listenerCount("click"), 0);

  context.document.querySelectorAll = originalQuerySelectorAll;
}

runSharedModalDismissRegression();


const pcAppSourceForOrderRefSort =
  fs.readFileSync(appPath, "utf8") + fs.readFileSync(orderModalPath, "utf8");
assert(
  pcAppSourceForOrderRefSort.includes('orderRef: { type: "number", getValue: function (row) { return Number(String(row.order_ref || "").trim()) || 0; } }'),
  "orderRef sort column should be numeric so PC orders sort 117, 116, 115, 112 instead of string/DOM order"
);
assert(
  !pcAppSourceForOrderRefSort.includes('orderRef: { type: "string", getValue: function (row) { return row.order_ref; } }'),
  "orderRef sort column must not use string sorting"
);
assert(
  !pcAppSourceForOrderRefSort.includes("return sortPendingOrdersFirst(sortedRows);"),
  "explicit column sorting must not be overridden by pending-first promotion"
);
assert.match(
  pcAppSourceForOrderRefSort,
  /refreshOpenOrderModalIfNeeded\(\)/,
  "live refresh should also refresh an open order modal"
);
assert.match(
  pcAppSourceForOrderRefSort,
  /openOrderModalController = \{/,
  "open order modal should register a live refresh controller"
);
assert.match(
  pcAppSourceForOrderRefSort,
  /expandedOrderLineIds:\s*\{\}/,
  "open order modal should keep expanded order line state across live refresh"
);
assert.match(
  fs.readFileSync(orderModalPath, "utf8"),
  /disposeDismiss = bindModalDismiss\(modal, close\);[\s\S]*openOrderModalController = \{/,
  "order modal dismissal must wrap the existing controller lifecycle"
);
assert.match(
  fs.readFileSync(orderModalPath, "utf8"),
  /if \(!modal\.isConnected\) \{[\s\S]*clearOpenOrderModalController\(\);[\s\S]*return;/,
  "late order modal refresh must not update detached DOM"
);

const pcIndexSource = fs.readFileSync(indexPath, "utf8");
const pcAppSource = fs.readFileSync(appPath, "utf8");
assert.strictEqual(
  (pcAppSource.match(/disposeDismiss = bindModalDismiss\(modal, close\);/g) || []).length,
  3,
  "attention, production preview and new-order modals must use the shared dismissal helper"
);
assert.match(
  pcAppSource,
  /function openNewOrderModal[\s\S]*if \(duplicateWarningTimer\) \{[\s\S]*clearTimeout\(duplicateWarningTimer\)[\s\S]*removeEventListener\("resize", syncSuggestionOverlay\)[\s\S]*suggestionOverlay\.parentNode\.removeChild/,
  "new-order close path must retain timer, floating overlay and resize listener cleanup"
);
assert.match(
  pcAppSource,
  /if \(closed \|\| modal\.isConnected === false\) \{\s*return;\s*\}[\s\S]*var refsData = payload\[0\]/,
  "late new-order reference data must not update detached DOM"
);
assert.match(pcIndexSource, /id="pcVersionBanner"/);
assert.match(pcIndexSource, /Доступна новая версия FlowStock/);
assert.match(pcIndexSource, /id="pcVersionReloadBtn"[^>]*>Обновить</);
assert.match(styles, /\.pc-version-banner\[hidden\]\s*\{[^}]*display:\s*none/s);
assert(
  pcIndexSource.indexOf('id="pcVersionBanner"') > pcIndexSource.indexOf('id="app"'),
  "version banner must live outside the route-rendered #app container"
);
assert.strictEqual(
  (pcAppSource.match(/window\.setInterval\(/g) || []).length,
  1,
  "existing PC version watcher should remain the single interval lifecycle"
);
assert.strictEqual(
  (pcAppSource.match(/window\.location\.reload\(\)/g) || []).length,
  1,
  "only the explicit version update action may reload the page"
);
assert.doesNotMatch(
  pcAppSource,
  /payload\s*&&\s*payload\.version\s*\?/,
  "PC update compatibility must use pc_web_version instead of server assembly version"
);
assert.match(
  pcAppSource,
  /onLoginSuccess:\s*function\s*\(\)\s*\{\s*startVersionWatcher\(\)/,
  "successful PC authentication should start the existing version watcher"
);
assert.match(
  pcAppSource,
  /clearAccount\(\);\s*stopVersionWatcher\(\)/,
  "PC logout should stop the existing version watcher"
);

async function runPcVersionWatcherTests() {
  assert.strictEqual(pc.getVersionWatcherState().loadedPcWebVersion, "loaded-version");

  assert.strictEqual(pc.applyServerPcWebVersion("loaded-version"), false);
  assert.strictEqual(versionBanner.hidden, true);
  assert.strictEqual(reloadCount, 0);

  assert.strictEqual(pc.applyServerPcWebVersion("new-version"), true);
  assert.strictEqual(versionBanner.hidden, false);
  assert.strictEqual(reloadCount, 0, "version mismatch must not reload automatically");

  versionResponse = { pc_web_version: "new-version", version: "server-version" };
  const fetchCountBeforeDeduplication = versionFetchCount;
  const firstCheck = pc.checkServerVersionAndShowUpdateBanner();
  const concurrentCheck = pc.checkServerVersionAndShowUpdateBanner();
  assert.strictEqual(firstCheck, concurrentCheck, "concurrent version checks should share one request");
  await firstCheck;
  assert.strictEqual(versionFetchCount, fetchCountBeforeDeduplication + 1);

  versionResponse = { version: "changed-server-version" };
  await pc.checkServerVersionAndShowUpdateBanner();
  assert.strictEqual(versionBanner.hidden, false, "missing pc_web_version must keep the banner state");

  versionFetchRejects = true;
  await pc.checkServerVersionAndShowUpdateBanner();
  assert.strictEqual(versionBanner.hidden, false, "network failure must keep an existing update banner");
  assert.strictEqual(reloadCount, 0);
  versionFetchRejects = false;
  versionResponse = { pc_web_version: "loaded-version", version: "server-version" };

  pc.applyServerPcWebVersion("new-version");
  assert.strictEqual(versionBanner.hidden, false);
  pc.stopVersionWatcher();
  assert.strictEqual(versionBanner.hidden, true, "logout must hide an existing version banner");

  deferredVersionResponse = createDeferred();
  pc.startVersionWatcher();
  const lateVersionCheck = pc.checkServerVersionAndShowUpdateBanner();
  pc.stopVersionWatcher();
  deferredVersionResponse.resolve({ pc_web_version: "late-mismatch" });
  deferredVersionResponse = null;
  await lateVersionCheck;
  assert.strictEqual(
    versionBanner.hidden,
    true,
    "a version response completed after logout must not show the banner"
  );

  const fetchCountBeforeNextLogin = versionFetchCount;
  pc.startVersionWatcher();
  await pc.checkServerVersionAndShowUpdateBanner();
  assert.strictEqual(
    versionFetchCount,
    fetchCountBeforeNextLogin + 1,
    "the next login must start a fresh version check"
  );
  pc.stopVersionWatcher();

  pc.startVersionWatcher();
  assert.strictEqual(activeIntervals.size, 1);
  const firstTimerId = pc.getVersionWatcherState().timerId;
  pc.startVersionWatcher();
  assert.strictEqual(activeIntervals.size, 1, "restarting watcher must replace its existing interval");
  assert.notStrictEqual(pc.getVersionWatcherState().timerId, firstTimerId);
  pc.stopVersionWatcher();
  assert.strictEqual(activeIntervals.size, 0);

  assert.strictEqual(typeof versionReloadHandlers.click, "function");
  versionReloadHandlers.click();
  assert.strictEqual(reloadCount, 1, "explicit Обновить action should reload exactly once");

  versionResponse = { pc_web_version: "loaded-version", version: "changed-server-version" };
  pc.applyServerPcWebVersion(versionResponse.pc_web_version);
  assert.strictEqual(versionBanner.hidden, true, "banner should hide when frontend versions match again");
}

async function runCatalogModalTests() {
  const catalog = context.window.FlowStockPcCatalog;
  const core = context.window.FlowStockPcCore;
  const connectedModals = [];
  const documentListeners = {};
  const fetchCalls = [];
  const firstPage = createDeferred();
  const latePage = createDeferred();
  let fetchMode = "first";

  function addListener(store, type, handler) {
    if (!store[type]) store[type] = [];
    store[type].push(handler);
  }
  function removeListener(store, type, handler) {
    store[type] = (store[type] || []).filter(function (registered) { return registered !== handler; });
  }
  function createButton() {
    const listeners = {};
    return {
      textContent: "",
      addEventListener: function (type, handler) { addListener(listeners, type, handler); },
      setAttribute: function () {},
      click: function () {
        (listeners.click || []).slice().forEach(function (handler) { handler({ target: this }); }, this);
      },
    };
  }
  function createCatalogModal() {
    const listeners = {};
    const closeButton = createButton();
    const toggleButton = createButton();
    const pricesSection = {
      hidden: true,
      _innerHTML: "Индивидуальные цены будут загружены по запросу.",
      moreButton: null,
      retryButton: null,
      set innerHTML(value) {
        this._innerHTML = String(value || "");
        this.moreButton = this._innerHTML.indexOf("data-product-prices-more") >= 0 ? createButton() : null;
        this.retryButton = this._innerHTML.indexOf("data-product-prices-retry") >= 0 ? createButton() : null;
      },
      get innerHTML() {
        return this._innerHTML;
      },
      querySelector: function (selector) {
        if (selector === "[data-product-prices-more]") return this.moreButton;
        if (selector === "[data-product-prices-retry]") return this.retryButton;
        return null;
      },
    };
    return {
      className: "",
      innerHTML: "",
      parentNode: null,
      isConnected: false,
      closeButton,
      toggleButton,
      pricesSection,
      addEventListener: function (type, handler) { addListener(listeners, type, handler); },
      removeEventListener: function (type, handler) { removeListener(listeners, type, handler); },
      dispatchClick: function (target) {
        (listeners.click || []).slice().forEach(function (handler) { handler({ target: target }); });
      },
      querySelector: function (selector) {
        if (selector === "[data-product-card-close]") return closeButton;
        if (selector === "[data-product-prices-toggle]") return toggleButton;
        if (selector === "[data-product-prices]") return pricesSection;
        return null;
      },
    };
  }

  context.document.querySelectorAll = function (selector) {
    return selector === ".pc-modal" ? connectedModals.slice() : [];
  };
  context.document.addEventListener = function (type, handler) { addListener(documentListeners, type, handler); };
  context.document.removeEventListener = function (type, handler) { removeListener(documentListeners, type, handler); };
  context.document.createElement = function () { return createCatalogModal(); };
  context.document.body = {
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
  };

  catalog.init({
    escapeHtml: core.escapeHtml,
    bindModalDismiss: core.bindModalDismiss,
    fetchJson: function (url) {
      fetchCalls.push(url);
      if (fetchMode === "first") return firstPage.promise;
      if (fetchMode === "late") return latePage.promise;
      return Promise.resolve({ items: [], total_count: 0 });
    },
  });

  const originalItem = { id: 777, name: "Snapshot товар", default_sale_vat_rate_name: "Основная", default_sale_vat_rate: 20 };
  const firstController = catalog.testHooks.openProductCard(originalItem);
  const firstModal = firstController.modal;
  const originalHtml = firstModal.innerHTML;
  originalItem.name = "Изменён после открытия";
  assert.strictEqual(firstModal.innerHTML, originalHtml, "open product card must remain a read-only snapshot");
  assert.strictEqual(fetchCalls.length, 0, "customer prices must stay lazy until first expansion");
  firstModal.toggleButton.click();
  assert.strictEqual(fetchCalls.length, 1);
  assert.strictEqual(fetchCalls[0], "/api/partner-item-sale-prices?item_id=777&limit=100&offset=0");
  firstPage.resolve({
    items: [{ partner_name: "Клиент", partner_code: "K1", unit_price_gross: 9.99, is_active: true }],
    total_count: 2,
  });
  await firstPage.promise;
  await Promise.resolve();
  await Promise.resolve();
  assert.match(firstModal.pricesSection.innerHTML, /Клиент/);
  firstModal.toggleButton.click();
  firstModal.toggleButton.click();
  assert.strictEqual(fetchCalls.length, 1, "repeated expansion must use the product card cache");
  assert.ok(firstModal.pricesSection.moreButton, "partial page must offer explicit pagination");
  fetchMode = "resolved";
  firstModal.pricesSection.moreButton.click();
  assert.strictEqual(fetchCalls[1], "/api/partner-item-sale-prices?item_id=777&limit=100&offset=1");
  await Promise.resolve();
  await Promise.resolve();
  firstModal.closeButton.click();
  firstModal.closeButton.click();
  assert.strictEqual(catalog.testHooks.getOpenProductCardController(), null);

  fetchMode = "late";
  const lateController = catalog.testHooks.openProductCard({ id: 778, name: "Поздний ответ" });
  const lateModal = lateController.modal;
  lateModal.toggleButton.click();
  const htmlBeforeClose = lateModal.pricesSection.innerHTML;
  lateModal.dispatchClick({});
  assert.ok(lateModal.isConnected, "click inside product modal card must not close it");
  lateModal.dispatchClick(lateModal);
  assert.strictEqual(lateModal.isConnected, false, "product card overlay click must use its close path");
  latePage.resolve({ items: [{ partner_name: "Не отображать", unit_price_gross: 1, is_active: true }], total_count: 1 });
  await latePage.promise;
  await Promise.resolve();
  await Promise.resolve();
  assert.strictEqual(
    lateModal.pricesSection.innerHTML,
    htmlBeforeClose,
    "late customer price response must not mutate detached product card DOM"
  );
}

async function runOrderModalDismissTests() {
  const orderModal = context.window.FlowStockPcOrderModal;
  const core = context.window.FlowStockPcCore;
  const connectedModals = [];
  const documentListeners = {};
  let pendingRefresh = null;

  function addListener(store, type, handler) {
    if (!store[type]) store[type] = [];
    store[type].push(handler);
  }
  function removeListener(store, type, handler) {
    store[type] = (store[type] || []).filter(function (registered) { return registered !== handler; });
  }
  function createOrderModalElement() {
    const listeners = {};
    const closeButton = {
      addEventListener: function (type, handler) { if (type === "click") this.click = handler; },
      click: function () {},
    };
    const linesWrap = { innerHTML: "", textContent: "Загрузка строк..." };
    return {
      className: "",
      innerHTML: "",
      parentNode: null,
      isConnected: false,
      closeButton,
      linesWrap,
      addEventListener: function (type, handler) { addListener(listeners, type, handler); },
      removeEventListener: function (type, handler) { removeListener(listeners, type, handler); },
      dispatchClick: function (target) {
        (listeners.click || []).slice().forEach(function (handler) { handler({ target: target }); });
      },
      querySelector: function (selector) {
        if (selector === "#modalCloseBtn") return closeButton;
        if (selector === "#orderLinesWrap") return linesWrap;
        return null;
      },
      querySelectorAll: function () { return []; },
    };
  }

  context.document.querySelectorAll = function (selector) {
    return selector === ".pc-modal" ? connectedModals.slice() : [];
  };
  context.document.addEventListener = function (type, handler) { addListener(documentListeners, type, handler); };
  context.document.removeEventListener = function (type, handler) { removeListener(documentListeners, type, handler); };
  context.document.createElement = function () { return createOrderModalElement(); };
  context.document.body = {
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
  };

  orderModal.init({
    fetchJson: function () { return pendingRefresh ? pendingRefresh.promise : Promise.resolve([]); },
    escapeHtml: core.escapeHtml,
    formatDate: function () { return "—"; },
    formatQuantity: function (value) { return String(value == null ? "—" : value); },
    isInternalOrder: function () { return false; },
    isShippedOrder: function () { return false; },
    getShipmentReadiness: function () { return {}; },
    renderReadinessBadge: function () { return ""; },
    applyOrderReadinessFromLines: function () {},
    translatePalletStatus: function (value) { return String(value || ""); },
    getOrderLineHighlightState: function () { return {}; },
    renderLinePalletFillingBadge: function () { return ""; },
    getOrderTypeLabel: function () { return "Клиентский"; },
    bindModalDismiss: core.bindModalDismiss,
    hasCapability: function (name) { return name === "ManagePendingRequests"; },
  });

  assert.strictEqual(
    orderModal.canManagePendingOrder({ is_pending_confirmation: true, management_supported: true }),
    true,
    "ADMIN capability should expose actions for every dispatcher-supported pending row"
  );
  assert.strictEqual(
    orderModal.canManagePendingOrder({ is_pending_confirmation: true, management_supported: false }),
    false,
    "unsupported request types must stay read-only"
  );

  orderModal.openOrderModal({ id: 1, order_ref: "001", order_type: "CUSTOMER" });
  let controller = orderModal.getOpenOrderModalController();
  const explicitModal = controller.modal;
  explicitModal.closeButton.click();
  explicitModal.closeButton.click();
  assert.strictEqual(orderModal.getOpenOrderModalController(), null, "order close must clear controller once");

  orderModal.openOrderModal({ id: 2, order_ref: "002", order_type: "CUSTOMER" });
  controller = orderModal.getOpenOrderModalController();
  const overlayModal = controller.modal;
  overlayModal.dispatchClick({});
  assert.ok(overlayModal.isConnected, "click inside order modal card must not close it");
  overlayModal.dispatchClick(overlayModal);
  assert.strictEqual(overlayModal.isConnected, false, "order overlay click must use the close path");

  orderModal.openOrderModal({ id: 3, order_ref: "003", order_type: "CUSTOMER" });
  controller = orderModal.getOpenOrderModalController();
  const escapeModal = controller.modal;
  const escapeEvent = {
    key: "Escape",
    defaultPrevented: false,
    preventDefault: function () { this.defaultPrevented = true; },
  };
  (documentListeners.keydown || []).slice().forEach(function (handler) { handler(escapeEvent); });
  assert.strictEqual(escapeModal.isConnected, false, "Escape must close order modal");

  pendingRefresh = createDeferred();
  orderModal.openOrderModal({ id: 4, order_ref: "004", order_type: "CUSTOMER" });
  controller = orderModal.getOpenOrderModalController();
  const lateModal = controller.modal;
  const linesBeforeClose = lateModal.linesWrap.textContent;
  controller.close();
  pendingRefresh.resolve([]);
  await pendingRefresh.promise;
  await Promise.resolve();
  await Promise.resolve();
  assert.strictEqual(
    lateModal.linesWrap.textContent,
    linesBeforeClose,
    "late order refresh must not update detached modal DOM"
  );
}

async function runInlinePendingOrderActionTests() {
  const orderModal = context.window.FlowStockPcOrderModal;
  const pendingOrder = {
    id: "request:81",
    request_id: 81,
    request_type: "CREATE_ORDER",
    management_supported: true,
    order_ref: "081",
    order_type: "CUSTOMER",
    is_pending_confirmation: true,
  };
  const submitted = [];
  const busyStates = [];
  const successAttentionCalls = [];
  let statusMessage = "";
  let refreshedRows = [pendingOrder];
  orderModal.init({
    fetchJson: function (url, options) {
      submitted.push({ url: url, method: options && options.method });
      return Promise.resolve({ status: "APPROVED", applied_order_id: 81 });
    },
  });

  const outcome = await pc.executePendingOrderAction(pendingOrder, "confirm", {
    setBusy: function (busy) { busyStates.push(busy); },
    refresh: function () {
      refreshedRows = [{
        id: 81,
        order_ref: "081",
        order_type: "CUSTOMER",
        order_status: "IN_PROGRESS",
        order_status_presentation: { code: "IN_PROGRESS", label: "В работе" },
      }];
      return Promise.resolve();
    },
    setStatus: function (message) { statusMessage = message; },
    showAttention: function (title, message) {
      successAttentionCalls.push({ title: title, message: message });
    },
  });

  assert.deepStrictEqual(submitted, [{
    url: "/api/orders/requests/81/confirm",
    method: "POST",
  }]);
  assert.strictEqual(outcome, "success");
  assert.deepStrictEqual(busyStates, [true, false]);
  assert.deepStrictEqual(successAttentionCalls, []);
  assert.match(statusMessage, /подтверждена/i);
  const refreshedHtml = pc.renderOrdersTable(refreshedRows, { canManagePendingRequests: true });
  assert.match(refreshedHtml, />В работе</);
  assert.doesNotMatch(refreshedHtml, /Ожидает подтверждения|Черновик/);
  assert.doesNotMatch(refreshedHtml, /data-pending-request-action=/);

  const rejectBusyStates = [];
  statusMessage = "";
  refreshedRows = [pendingOrder];
  orderModal.init({
    fetchJson: function () {
      return Promise.resolve({ status: "REJECTED" });
    },
  });

  const rejectOutcome = await pc.executePendingOrderAction(pendingOrder, "reject", {
    setBusy: function (busy) { rejectBusyStates.push(busy); },
    refresh: function () {
      refreshedRows = [];
      return Promise.resolve();
    },
    setStatus: function (message) { statusMessage = message; },
  });

  assert.strictEqual(rejectOutcome, "success");
  assert.deepStrictEqual(rejectBusyStates, [true, false]);
  assert.match(statusMessage, /отклонена/i);
  const rejectedHtml = pc.renderOrdersTable(refreshedRows, { canManagePendingRequests: true });
  assert.match(rejectedHtml, /Заказов нет/);
  assert.doesNotMatch(rejectedHtml, /Ожидает подтверждения|Черновик|data-pending-request-action=/);

  let conflictRefreshCount = 0;
  const conflictAttentionCalls = [];
  const conflictBusyStates = [];
  statusMessage = "";
  refreshedRows = [pendingOrder];
  orderModal.init({
    fetchJson: function () {
      return Promise.reject(new Error("ORDER_REQUEST_ALREADY_RESOLVED"));
    },
  });

  const conflictOutcome = await pc.executePendingOrderAction(pendingOrder, "confirm", {
    setBusy: function (busy) { conflictBusyStates.push(busy); },
    refresh: function () {
      conflictRefreshCount += 1;
      refreshedRows = [{
        id: 81,
        order_ref: "081",
        order_type: "CUSTOMER",
        order_status: "IN_PROGRESS",
        order_status_presentation: { code: "IN_PROGRESS", label: "В работе" },
      }];
      return Promise.resolve();
    },
    setStatus: function (message) { statusMessage = message; },
    showAttention: function (title, message) {
      conflictAttentionCalls.push({ title: title, message: message });
    },
  });

  assert.strictEqual(conflictOutcome, "conflict");
  assert.strictEqual(conflictRefreshCount, 1);
  assert.deepStrictEqual(conflictBusyStates, [true, false]);
  assert.deepStrictEqual(conflictAttentionCalls, []);
  assert.match(statusMessage, /с сервера/i);
  const conflictHtml = pc.renderOrdersTable(refreshedRows, { canManagePendingRequests: true });
  assert.match(conflictHtml, />В работе</);
  assert.doesNotMatch(conflictHtml, /Ожидает подтверждения|Черновик|data-pending-request-action=/);

  const businessMessage = "Для товара не выбрана ставка НДС продажи. Укажите ставку в карточке товара.";
  const businessBusyStates = [];
  const attentionCalls = [];
  let businessRefreshCount = 0;
  statusMessage = "Подтверждение заявки...";
  orderModal.init({
    fetchJson: function () {
      return Promise.reject(new Error(businessMessage));
    },
  });
  const businessOutcome = await pc.executePendingOrderAction(pendingOrder, "confirm", {
    setBusy: function (busy) { businessBusyStates.push(busy); },
    refresh: function () {
      businessRefreshCount += 1;
      return Promise.resolve();
    },
    showAttention: function (title, message) {
      attentionCalls.push({ title: title, message: message });
    },
    setStatus: function (message) { statusMessage = message; },
  });
  assert.strictEqual(businessOutcome, "error");
  assert.deepStrictEqual(businessBusyStates, [true, false]);
  assert.strictEqual(businessRefreshCount, 0);
  assert.deepStrictEqual(attentionCalls, [{
    title: "Не удалось подтвердить заказ",
    message: businessMessage,
  }]);
  assert.strictEqual(statusMessage, "Заявка не изменена.");
  assert.doesNotMatch(statusMessage, /ставка НДС/);
  assert.match(
    pc.renderOrdersTable([pendingOrder], { canManagePendingRequests: true }),
    /data-pending-request-action="confirm"/
  );

  const rejectAttentionCalls = [];
  orderModal.init({
    fetchJson: function () {
      return Promise.reject(new Error("Отклонение запрещено бизнес-правилом."));
    },
  });
  const rejectBusinessOutcome = await pc.executePendingOrderAction(pendingOrder, "reject", {
    showAttention: function (title, message) {
      rejectAttentionCalls.push({ title: title, message: message });
    },
  });
  assert.strictEqual(rejectBusinessOutcome, "error");
  assert.deepStrictEqual(rejectAttentionCalls, [{
    title: "Не удалось отклонить заявку",
    message: "Отклонение запрещено бизнес-правилом.",
  }]);

  let sessionInvalidCount = 0;
  const unauthorizedBusyStates = [];
  const authAttentionCalls = [];
  orderModal.init({
    fetchJson: function () {
      return Promise.reject(new Error("UNAUTHORIZED"));
    },
  });
  const unauthorizedOutcome = await pc.executePendingOrderAction(pendingOrder, "confirm", {
    setBusy: function (busy) { unauthorizedBusyStates.push(busy); },
    onSessionInvalid: function () { sessionInvalidCount += 1; },
    showAttention: function (title, message) {
      authAttentionCalls.push({ title: title, message: message });
    },
  });
  assert.strictEqual(unauthorizedOutcome, "unauthorized");
  assert.strictEqual(sessionInvalidCount, 1);
  assert.deepStrictEqual(unauthorizedBusyStates, [true, false]);
  assert.deepStrictEqual(authAttentionCalls, []);

  let sessionRefreshCount = 0;
  let forbiddenListRefreshCount = 0;
  orderModal.init({
    fetchJson: function () {
      return Promise.reject(new Error("MANAGE_PENDING_REQUESTS_REQUIRED"));
    },
  });
  const forbiddenOutcome = await pc.executePendingOrderAction(pendingOrder, "reject", {
    refreshSession: function () {
      sessionRefreshCount += 1;
      return Promise.resolve();
    },
    refresh: function () {
      forbiddenListRefreshCount += 1;
      return Promise.resolve();
    },
    showAttention: function (title, message) {
      authAttentionCalls.push({ title: title, message: message });
    },
  });
  assert.strictEqual(forbiddenOutcome, "forbidden");
  assert.strictEqual(sessionRefreshCount, 1);
  assert.strictEqual(forbiddenListRefreshCount, 1);
  assert.deepStrictEqual(authAttentionCalls, []);

  const refreshFailureBusyStates = [];
  statusMessage = "";
  orderModal.init({
    fetchJson: function () {
      return Promise.resolve({ status: "APPROVED" });
    },
  });
  const refreshFailureOutcome = await pc.executePendingOrderAction(pendingOrder, "confirm", {
    setBusy: function (busy) { refreshFailureBusyStates.push(busy); },
    refresh: function () { return Promise.resolve({ refreshFailed: true }); },
    setStatus: function (message) { statusMessage = message; },
  });
  assert.strictEqual(refreshFailureOutcome, "error");
  assert.deepStrictEqual(refreshFailureBusyStates, [true, false]);
  assert.match(statusMessage, /Не удалось обновить список заказов/);
}

function runAttentionModalTests() {
  const originalCreateElement = context.document.createElement;
  const originalBody = context.document.body;
  const originalQuerySelectorAll = context.document.querySelectorAll;
  const originalAddEventListener = context.document.addEventListener;
  const originalRemoveEventListener = context.document.removeEventListener;
  const okButton = {
    addEventListener: function (type, handler) {
      if (type === "click") this.click = handler;
    },
    click: function () {},
  };
  const modal = {
    className: "",
    innerHTML: "",
    parentNode: null,
    isConnected: false,
    addEventListener: function () {},
    removeEventListener: function () {},
    querySelector: function (selector) {
      return selector === "#attentionModalOkBtn" ? okButton : null;
    },
  };
  context.document.createElement = function () { return modal; };
  context.document.querySelectorAll = function (selector) {
    return selector === ".pc-modal" && modal.isConnected ? [modal] : [];
  };
  context.document.addEventListener = function () {};
  context.document.removeEventListener = function () {};
  context.document.body = {
    appendChild: function (element) {
      element.parentNode = this;
      element.isConnected = true;
    },
    removeChild: function (element) {
      element.parentNode = null;
      element.isConnected = false;
    },
  };

  const controller = pc.openAttentionModal(
    "Не удалось подтвердить заказ",
    "Для товара не выбрана ставка НДС продажи."
  );
  assert.strictEqual(modal.isConnected, true);
  assert.match(modal.innerHTML, />Не удалось подтвердить заказ</);
  assert.match(modal.innerHTML, />Для товара не выбрана ставка НДС продажи\.</);
  okButton.click();
  assert.strictEqual(modal.isConnected, false);
  controller.close();

  context.document.createElement = originalCreateElement;
  context.document.body = originalBody;
  context.document.querySelectorAll = originalQuerySelectorAll;
  context.document.addEventListener = originalAddEventListener;
  context.document.removeEventListener = originalRemoveEventListener;
}

function runLatestOrdersLoadGateTests() {
  const gate = pc.createLatestOnlyGate();
  const staleLoad = gate.begin();
  const currentLoad = gate.begin();

  assert.strictEqual(gate.isCurrent(staleLoad), false);
  assert.strictEqual(gate.isCurrent(currentLoad), true);
}

async function runAsyncRegressions() {
  runLatestOrdersLoadGateTests();
  runAttentionModalTests();
  await runPcVersionWatcherTests();
  await runCatalogModalTests();
  await runInlinePendingOrderActionTests();
  await runOrderModalDismissTests();
}

runAsyncRegressions().catch(function (error) {
  console.error(error);
  process.exitCode = 1;
});
