const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const appPath = path.join(__dirname, "app.js");
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
assert.match(orderLinesWithPalletHtml, /pc-order-line-pallet-filled/);
assert.match(orderLinesWithPalletHtml, /Наполнение/);
assert.match(orderLinesWithPalletHtml, /Наполнено 1 \/ 2/);

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
assert.match(orderLinesCompletePalletHtml, /pc-icon-status/);
assert.doesNotMatch(orderLinesCompletePalletHtml, />Наполнено 2 \/ 2</);

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

const expandedStockHtml = pc.renderStockTable(
  [
    {
      itemId: 10,
      itemName: "Товар 1",
      barcode: "SKU-001",
      brand: "Бренд",
      volume: "1 л",
      qtyDisplay: "12",
      details: [
        {
          locationCode: "01",
          hu: "HU-000001",
          reservationPartnerName: "Очень длинное имя клиента для проверки стабильной сетки",
          reservationOrderRef: "ORD-2026-000001/Сверхдлинный суффикс",
          qtyDisplay: "7"
        }
      ]
    }
  ],
  { 10: true }
);
assert.match(expandedStockHtml, /pc-stock-shared-grid/);
assert.match(expandedStockHtml, /pc-stock-detail-block/);
assert.match(expandedStockHtml, /pc-stock-detail-entry/);
assert.match(expandedStockHtml, /pc-stock-detail-col-client/);
assert.match(expandedStockHtml, /pc-stock-detail-col-order/);
assert.match(expandedStockHtml, /pc-stock-detail-col-qty/);
assert.match(expandedStockHtml, /pc-stock-detail-text/);
assert.match(expandedStockHtml, /pc-stock-item-meta/);
assert.match(expandedStockHtml, /Номенклатура/);
assert.match(expandedStockHtml, /colspan="4" class="pc-stock-detail-cell"/);
assert.match(
  expandedStockHtml,
  /pc-stock-detail-head[^>]*>.*Место.*HU.*Заказ.*Клиент.*Кол-во/s
);
assert.match(
  expandedStockHtml,
  /pc-stock-detail-col-order[\s\S]*pc-stock-detail-col-client[\s\S]*pc-stock-detail-col-qty/s,
  "в detail row порядок должен быть Заказ -> Клиент -> Кол-во"
);
assert.doesNotMatch(
  expandedStockHtml,
  /pc-stock-detail-col-client[\s\S]*pc-stock-detail-col-qty[\s\S]*pc-stock-detail-col-order/s,
  "qty cell не должна попадать внутрь блока клиент/заказ"
);

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
