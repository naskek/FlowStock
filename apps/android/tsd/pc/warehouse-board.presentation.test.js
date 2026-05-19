const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const boardPath = path.join(__dirname, "warehouse-board.js");
const appPath = path.join(__dirname, "app.js");
const boardSource = fs.readFileSync(boardPath, "utf8");
const appSource = fs.readFileSync(appPath, "utf8");

const context = { window: {}, console, localStorage: { getItem: () => null } };
vm.createContext(context);
vm.runInContext(boardSource, context, { filename: boardPath });

const board = context.window.FlowStockWarehouseBoard;
assert.ok(board, "FlowStockWarehouseBoard must be exported");

assert.strictEqual(board.statusLabel("SUBMITTED"), "На подтверждении");
assert.strictEqual(board.UI_LABELS.WHAT_TO_DO, "Что нужно сделать?");
assert.strictEqual(board.UI_LABELS.CARD_MOVE_TITLE, "Переместить паллету / HU");
assert.strictEqual(board.UI_LABELS.CARD_ADOPT_TITLE, "Перенести план паллет");
assert.strictEqual(board.UI_LABELS.EMPTY_PACKAGE, "Пакет пока пустой. Выберите действие сверху.");
assert.ok(boardSource.includes("Шаг 1"), "guided steps must be present");
assert.ok(boardSource.includes(board.UI_LABELS.SAFETY_HINT), "safety hint must be present");

assert.ok(!boardSource.includes("Создать MOVE_HU"), "no raw MOVE_HU button");
assert.ok(!boardSource.includes("Создать ADOPT"), "no raw ADOPT button");
assert.ok(!boardSource.includes("item_id="), "no raw item_id in UI strings");

assert.ok(
  appSource.includes("flowstock_experimental_warehouse_tasks"),
  "PC app must gate warehouse board behind experimental flag"
);
assert.ok(
  appSource.includes('views.push("warehouse-board")'),
  "warehouse-board registration must remain behind flag check"
);

board.init({
  escapeHtml: function (value) {
    return String(value || "");
  },
});

const html = board.render();
assert.ok(html.includes("wpScenarioPicker"), "render must include scenario picker shell");
assert.ok(html.includes(board.UI_LABELS.SAFETY_HINT), "render must include safety hint");

console.log("warehouse-board.presentation.test.js: ok");
