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
