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
  "ЧЗ готов к нанесению"
);
assert.strictEqual(
  pc.getOrderMarkingPresentation({
    marking_effective_status: "REQUIRED",
    marking_status_display: "Требуется файл ЧЗ",
  }).label,
  "Требуется ЧЗ"
);
assert.strictEqual(
  pc.getOrderMarkingPresentation({
    marking_effective_status: "NOT_REQUIRED",
    marking_status_display: "Маркировка не требуется",
  }).label,
  "Маркировка не требуется"
);
assert.strictEqual(
  pc.getOrderMarkingPresentation({
    marking_effective_status: "EXCEL_GENERATED",
    marking_status_display: "Файл ЧЗ сформирован",
  }).label,
  "ЧЗ готов к нанесению"
);

const printedHtml = pc.renderOrderMarkingIndicator({
  marking_effective_status: "PRINTED",
  marking_status_display: "ЧЗ готов к нанесению",
});
assert.match(printedHtml, /pc-status-badge/);
assert.match(printedHtml, /pc-marking-badge/);
assert.match(printedHtml, /ЧЗ готов к нанесению/);
