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
assert.match(printedHtml, /pc-status-badge/);
assert.match(printedHtml, /pc-marking-badge/);
assert.match(printedHtml, /Маркировка проведена/);

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
    id: 3,
    order_ref: "CUST-003",
    order_type: "CUSTOMER",
    order_status: "SHIPPED",
    due_date: "2026-05-14",
    created_at: "2026-05-10T10:00:00Z"
  },
  {
    id: 2,
    order_ref: "INT-002",
    order_type: "INTERNAL",
    order_status: "IN_PROGRESS",
    due_date: "2026-05-15",
    created_at: "2026-05-09T10:00:00Z"
  },
  {
    id: 1,
    order_ref: "CUST-001",
    order_type: "CUSTOMER",
    order_status: "IN_PROGRESS",
    due_date: "2026-05-12",
    created_at: "2026-05-08T10:00:00Z"
  }
]);
assert.deepStrictEqual(
  canonicalSortedOrders.map(function (row) { return row.order_ref; }),
  ["CUST-001", "INT-002", "CUST-003"],
  "INTERNAL IN_PROGRESS должен оставаться выше SHIPPED и сортироваться внутри статуса по плановой дате"
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
