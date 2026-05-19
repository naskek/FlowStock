const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const boardPath = path.join(__dirname, "warehouse-board.js");
const context = { window: {}, console };
vm.createContext(context);
vm.runInContext(fs.readFileSync(boardPath, "utf8"), context, { filename: boardPath });

const board = context.window.FlowStockWarehouseBoard;
assert.ok(board, "FlowStockWarehouseBoard must be exported");
assert.strictEqual(board.statusLabel("SUBMITTED"), "На подтверждении");
assert.strictEqual(board.statusLabel("EXECUTED"), "Исполнено ТСД");
assert.strictEqual(board.STATUS_LABELS.DRAFT, "Черновик");
