const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const appPath = path.join(__dirname, "app.js");
const styles = fs.readFileSync(path.join(__dirname, "styles.css"), "utf8");
const hooks = {};
const context = {
  console,
  window: {
    FlowStockPcTestHooks: hooks,
  },
  document: {
    getElementById: function () {
      return null;
    },
    querySelectorAll: function () {
      return [];
    },
  },
};

context.window.document = context.document;
vm.createContext(context);
vm.runInContext(fs.readFileSync(appPath, "utf8"), context, { filename: appPath });

const pc = context.window.FlowStockPcTestHooks;

assert.strictEqual(
  pc.getOrderStatusPresentation({ status: "Отменён" }).label,
  "Отменён",
  "display-only cancelled order must not fall back to in-progress"
);
assert.strictEqual(
  pc.getOrderStatusPresentation({ order_status: "CANCELLED", status: "Отменён" }).tone,
  "cancelled",
  "canonical cancelled status should use cancelled badge tone"
);

const shippedOrderHtml = pc.renderOrdersTable([
  { id: 9, order_ref: "009", order_status: "SHIPPED", status: "Выполнен" },
]);
assert.match(shippedOrderHtml, /pc-order-status-icon/);
assert.match(shippedOrderHtml, /title="Выполнен"/);
assert.doesNotMatch(shippedOrderHtml, />Выполнен</);

const readyOrderHtml = pc.renderOrdersTable([
  { id: 10, order_ref: "010", order_status: "ACCEPTED", status: "Готов" },
]);
assert.match(readyOrderHtml, />Готов</);

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
assert.match(ordersWithPalletHtml, /pc-pallet-filling-badge/);
assert.match(ordersWithPalletHtml, /Наполнено 1 \/ 2/);

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
assert.match(orderLinesWithPalletHtml, /Наполнено 1 \/ 2/);
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
assert.match(orderLinesCompletePalletHtml, />Наполнено 2 \/ 2</);

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
      planned_pallet_count: 1,
      filled_pallet_count: 1,
      pallet_planned_qty: 1,
      pallet_filled_qty: 1,
    },
  ],
  { order_type: "CUSTOMER", order_status: "IN_PROGRESS" }
);
assert.match(mixedComponentFilledLineHtml, /pc-order-line-coverage-covered/);
assert.doesNotMatch(mixedComponentFilledLineHtml, /pc-order-line-coverage-missing/);
assert.match(mixedComponentFilledLineHtml, />Наполнено 1 \/ 1</);

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
      planned_pallet_count: 1,
      filled_pallet_count: 0,
      pallet_planned_qty: 1,
      pallet_filled_qty: 0.5,
    },
  ],
  { order_type: "CUSTOMER", order_status: "IN_PROGRESS" }
);
assert.match(mixedComponentPartialLineHtml, /pc-order-line-coverage-partial/);
assert.doesNotMatch(mixedComponentPartialLineHtml, /pc-order-line-coverage-missing/);
assert.match(mixedComponentPartialLineHtml, />Наполнено 0\.5 \/ 1</);
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
    planned_pallet_count: 1,
    filled_pallet_count: 1,
    pallet_planned_qty: 1,
    pallet_filled_qty: 1,
  },
];
const modalUpdatesAfterMixedFill = pc.getOrderModalContentUpdates(mixedComponentOrder, mixedComponentLines);
assert.match(modalUpdatesAfterMixedFill.linesHtml, />Наполнено 1 \/ 1</);
assert.match(modalUpdatesAfterMixedFill.linesHtml, /pc-order-line-coverage-covered/);
assert.match(modalUpdatesAfterMixedFill.summaryHtml, /pc-order-modal-summary/);

const modalDom = {
  datesEl: { textContent: "" },
  summaryEl: { innerHTML: "" },
  readinessEl: { outerHTML: '<span id="orderReadinessBadge"></span>' },
  linesEl: { innerHTML: "" },
  querySelector: function (selector) {
    if (selector === "#orderDatesStatus") {
      return this.datesEl;
    }
    if (selector === "#orderSummaryIndicators") {
      return this.summaryEl;
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
assert.match(modalDom.linesEl.innerHTML, />Наполнено 1 \/ 1</);
assert.match(modalDom.summaryEl.innerHTML, /pc-order-modal-summary/);

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
    },
  ],
  { order_type: "CUSTOMER", order_status: "IN_PROGRESS" }
);
assert.match(customerFullHuCoverageHtml, /pc-order-line-coverage-covered/);
assert.match(customerFullHuCoverageHtml, /Горчица: привязано 1800 из 1800/);

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
    },
  ],
  { order_type: "CUSTOMER", order_status: "IN_PROGRESS" }
);
assert.match(customerPartialHuCoverageHtml, /pc-order-line-coverage-missing/);
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

const shippedCustomerStalePalletListHtml = pc.renderOrdersTable([
  {
    id: 66,
    order_ref: "066",
    order_type: "CUSTOMER",
    order_status: "SHIPPED",
    status: "Выполнен",
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
    has_production_pallet_plan: true,
    planned_pallet_count: 5,
    filled_pallet_count: 2,
    pallet_plan_status: "Наполнение идёт: 2 / 5",
  },
]);
assert.match(customerInProgressPalletHtml, /Наполнено 2 \/ 5/);
assert.match(customerInProgressPalletHtml, /pc-status-badge-inprogress/);

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
    production_hu_codes: ["HU-001", "HU-002"],
  },
  {
    qty_left: 600,
    can_ship_now: 600,
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
    production_hu_codes: ["HU-004"],
  },
  {
    qty_left: 600,
    can_ship_now: 0,
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

const stockPageHtml = pc.renderStock();
assert.match(stockPageHtml, /stockCreateProductionOrderBtn/);
assert.match(stockPageHtml, /Сформировать заказ/);
assert.doesNotMatch(stockPageHtml, /Показать производственный план/);
assert.ok(
  !pc.getEnabledViews().includes("production-need"),
  "separate production need tab must be hidden from normal PC navigation"
);
assert.match(
  fs.readFileSync(appPath, "utf8"),
  /\/api\/reports\/warehouse-production-state/,
  "stock page must load warehouse-production-state"
);
assert.match(
  fs.readFileSync(appPath, "utf8"),
  /Не удалось загрузить объединённое состояние склада/,
  "warehouse-production-state endpoint error must show readable message"
);

const warehouseStockRow = pc.mapWarehouseProductionStateRow({
  item_id: 10,
  item_name: "Товар 1",
  barcode: "SKU-001",
  gtin: "04607186951520",
  base_uom: "шт",
  item_type: "Готовая продукция",
  stock_qty: 12,
  min_stock_qty: 20,
  below_min_qty: 8,
  customer_open_demand_qty: 5,
  internal_remaining_qty: 6,
  prd_planned_qty: 4,
  prd_filled_qty: 2,
  remaining_need_qty: 3,
  hu_rows: [{ location: "FG-01", hu_code: "HU-000001", qty: 12 }],
  production_receipts: [
    {
      hu_code: "HU-000001",
      pallet_status: "PLANNED",
      planned_qty: 4,
      filled_qty: 0,
      composition: "Товар 1"
    },
    {
      hu_code: "HU-000002",
      pallet_status: "PRINTED",
      planned_qty: 4,
      filled_qty: 1,
      composition: "Товар 1"
    },
    {
      hu_code: "HU-000003",
      pallet_status: "FILLED",
      planned_qty: 4,
      filled_qty: 1,
      composition: "Товар 1"
    }
  ],
  need_breakdown: {
    demand_to_close_customer_orders: 5,
    demand_to_min_stock: 8,
    already_planned_internal: 6,
    already_planned_prd: 4,
    remaining_to_create: 3
  }
});
assert.strictEqual(warehouseStockRow.stockQty, 12);
assert.strictEqual(warehouseStockRow.internalRemainingQty, 6);
assert.strictEqual(warehouseStockRow.prdPlannedQty, 4);
assert.strictEqual(warehouseStockRow.remainingNeedSummary, "Произвести: 3 шт");

const expandedStockHtml = pc.renderStockTable([warehouseStockRow], { 10: true });
assert.match(expandedStockHtml, /pc-stock-detail-block/);
assert.match(expandedStockHtml, /pc-stock-item-meta/);
assert.match(expandedStockHtml, /Товар/);
assert.match(expandedStockHtml, /На складе/);
assert.match(expandedStockHtml, /Минимум/);
assert.match(expandedStockHtml, /Потребность/);
assert.match(expandedStockHtml, /План/);
assert.doesNotMatch(expandedStockHtml, /Выпущено \/ наполнено/);
assert.doesNotMatch(expandedStockHtml, /Осталось выпустить/);
assert.match(expandedStockHtml, /Клиенты: 5 шт/);
assert.match(expandedStockHtml, /До мин.: 8 шт/);
assert.match(expandedStockHtml, /Внутр.: 6 шт/);
assert.match(expandedStockHtml, /PRD: 4 шт/);
assert.match(expandedStockHtml, /2 шт/);
assert.doesNotMatch(expandedStockHtml, /Произвести: 3 шт/);
assert.match(expandedStockHtml, /colspan="5" class="pc-stock-detail-cell"/);
assert.match(expandedStockHtml, />Складские HU</);
assert.match(expandedStockHtml, />План \/ производство</);
assert.match(expandedStockHtml, />Расчёт потребности</);
assert.doesNotMatch(expandedStockHtml, /Реальный склад \/ HU/);
assert.doesNotMatch(expandedStockHtml, /Клиентские заказы/);
assert.doesNotMatch(expandedStockHtml, /Внутренние заказы/);
assert.match(expandedStockHtml, /<th>HU<\/th><th class="pc-num">Кол-во<\/th><th>Статус<\/th><th>Локация<\/th>/);
assert.match(expandedStockHtml, /<th>HU<\/th><th>Статус<\/th><th class="pc-num">Кол-во<\/th><th>Заказ<\/th><th>PRD<\/th><th>Примечание<\/th>/);
assert.match(expandedStockHtml, /Ожидает/);
assert.match(expandedStockHtml, /Этикетка напечатана/);
assert.match(expandedStockHtml, /Наполнена/);
assert.match(expandedStockHtml, /Всего в заказах для клиентов/);
assert.match(expandedStockHtml, /До минимума/);
assert.match(expandedStockHtml, /Во внутренних заказах/);
assert.doesNotMatch(expandedStockHtml, /<th class="pc-num">Выпущено<\/th>/);
assert.doesNotMatch(expandedStockHtml, /<th class="pc-num">Осталось выпустить<\/th>/);
assert.match(
  expandedStockHtml,
  /pc-stock-plan-cell"><div class="pc-stock-summary-line">Внутр\.: 6 шт<\/div><div class="pc-stock-summary-line">PRD: 4 шт<\/div><\/td>/
);

const coveredStockRow = pc.mapWarehouseProductionStateRow({
  item_id: 11,
  item_name: "Покрытый товар",
  base_uom: "шт",
  stock_qty: 10,
  internal_remaining_qty: 5,
  remaining_need_qty: 0,
  need_breakdown: { already_planned_internal: 5, remaining_to_create: 0 },
  production_receipts: []
});
assert.strictEqual(coveredStockRow.remainingNeedSummary, "Покрыто");
const coveredStockHtml = pc.renderStockTable([coveredStockRow], { 11: true });
assert.doesNotMatch(coveredStockHtml, /Покрыто/);
assert.match(coveredStockHtml, /План \/ производство не сформирован/);

const noNeedStockHtml = pc.renderStockTable([
  pc.mapWarehouseProductionStateRow({
    item_id: 12,
    item_name: "Без потребности",
    base_uom: "шт",
    stock_qty: 10,
    remaining_need_qty: 0,
    need_breakdown: { remaining_to_create: 0 },
    production_receipts: []
  })
], { 12: true });
assert.match(noNeedStockHtml, /Потребности нет/);

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


const pcAppSourceForOrderRefSort = fs.readFileSync(appPath, "utf8");
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
