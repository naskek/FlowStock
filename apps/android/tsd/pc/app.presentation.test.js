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
    marking_effective_status: "PRINTED",
    marking_status_display: "ЧЗ готов к нанесению",
  }).label,
  "Маркировка проведена"
);
assert.strictEqual(
  pc.getOrderMarkingPresentation({
    marking_effective_status: "REQUIRED",
    marking_status_display: "Требуется файл ЧЗ",
  }).label,
  "Маркировка не проведена"
);
assert.strictEqual(
  pc.getOrderMarkingPresentation({
    marking_effective_status: "NOT_REQUIRED",
    marking_status_display: "Маркировка не требуется",
  }).label,
  ""
);
assert.strictEqual(
  pc.getOrderMarkingPresentation({
    marking_effective_status: "EXCEL_GENERATED",
    marking_status_display: "Файл ЧЗ сформирован",
  }).label,
  "Маркировка проведена"
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
  marking_status_display: "ЧЗ готов к нанесению",
});
assert.match(printedHtml, /pc-status-badge/);
assert.match(printedHtml, /pc-marking-badge/);
assert.match(printedHtml, /Маркировка проведена/);

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
