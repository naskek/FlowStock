"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");
const settle = () => new Promise(resolve => setImmediate(resolve));
function element() { return { handlers: {}, innerHTML: "", addEventListener(event, fn) { this.handlers[event] = fn; } }; }
(async function () {
  const button = element(), confirm = element(), cancel = element(), status = element(), area = element();
  const boxes = [ { checked: true, getAttribute: () => "10" }, { checked: false, getAttribute: () => "11" } ];
  area.querySelector = selector => selector === "[data-excel-confirm]" ? confirm : cancel;
  area.querySelectorAll = () => boxes;
  const modal = { isConnected: true, querySelector: selector => ({ "#orderExcelBtn": button, "#orderExcelPreview": area, "#orderExcelStatus": status })[selector] };
  let posts = [], gets = [], downloads = 0, fail = false;
  const context = {
    window: {}, URL: { createObjectURL: () => "blob:test", revokeObjectURL() {} }, setTimeout: fn => fn(),
    document: { body: { appendChild() {}, removeChild() {} }, createElement: () => ({ click() { downloads++; } }) },
    fetch: async (url, options) => { posts.push({ url, options }); return fail ? { ok: false, json: async () => ({ error: "stale preview" }) } : { ok: true, blob: async () => ({}) }; }
  };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(path.join(__dirname, "pc-order-modal.js"), "utf8"), context);
  const api = context.window.FlowStockPcOrderModal;
  api.init({ fetchJson: async url => { gets.push(url); return { snapshot: "snapshot-1", lines: [ { order_line_id: 10, item_name: "<x>", gtin: "0012", qty: 3.125678 }, { order_line_id: 11, item_name: "B", gtin: "0023", qty: 7 } ] }; }, escapeHtml: value => String(value).replace(/</g, "&lt;").replace(/>/g, "&gt;"), formatQuantity: String });
  api.bindExcelExport(modal, { id: 1 });
  button.handlers.click(); await settle();
  assert.equal(gets[0], "/api/orders/1/marking/preview");
  assert.match(area.innerHTML, /&lt;x&gt;/); assert.match(area.innerHTML, /3.125678/);
  confirm.handlers.click(); await settle(); await settle();
  assert.deepEqual(JSON.parse(posts[0].options.body), { selected_line_ids: [10], snapshot: "snapshot-1" });
  assert.equal(posts[0].url, "/api/orders/1/marking/export"); assert.equal(downloads, 1);
  fail = true; confirm.handlers.click(); await settle(); await settle();
  assert.match(status.textContent, /stale preview/); assert.equal(confirm.disabled, false);
  fail = false; confirm.handlers.click(); await settle(); await settle(); assert.equal(downloads, 2);
  boxes[0].checked = false; confirm.handlers.click(); await settle(); assert.equal(posts.length, 3);
  cancel.handlers.click(); assert.equal(area.innerHTML, "");
  assert.ok(posts.every(post => post.url.endsWith("/marking/export")));
})().catch(error => { console.error(error); process.exitCode = 1; });
