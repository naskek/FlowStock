"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const pickerSource = fs.readFileSync(path.join(__dirname, "pc-order-item-picker.js"), "utf8");
const appSource = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");
const indexSource = fs.readFileSync(path.join(__dirname, "index.html"), "utf8");
const cssSource = fs.readFileSync(path.join(__dirname, "styles.css"), "utf8");
const context = { window: {} };
vm.createContext(context);
vm.runInContext(pickerSource, context, { filename: "pc-order-item-picker.js" });
const createModel = context.window.FlowStockPcOrderItemPicker.testHooks.createModel;
const createPicker = context.window.FlowStockPcOrderItemPicker.create;

const products = [
  { id: 1, name: "Горчица русская", brand: "Вкус & Цвет", volume: "200 г",
    base_uom: "шт", barcode: "SKU-001", gtin: "04600000000001", is_active: true },
  { id: 2, name: "Горчица дижонская", brand: "Вкус & Цвет", volume: "1 кг",
    base_uom_code: "кг", barcode: "SKU-002", gtin: "04600000000002" },
  { id: 3, name: "Хрен столовый", brand: "Традиция", volume: "200 г",
    base_uom: "шт", barcode: "SKU-003", gtin: "04600000000003" },
  { id: 4, name: "Скрытый товар", brand: "Традиция", volume: "1 кг",
    base_uom: "кг", barcode: "SKU-004", gtin: "04600000000004", is_active: false },
];

function ids(rows) {
  return Array.from(rows, function (row) { return Number(row.id); });
}

function testModel() {
  const model = createModel();
  model.setItems(products);
  assert.deepEqual(ids(model.filtered()), [2, 1, 3], "sorted selectable items, inactive excluded");
  assert.deepEqual(Array.from(model.options("brand")), ["Вкус & Цвет", "Традиция"]);
  assert.deepEqual(Array.from(model.options("volume")), ["1 кг", "200 г"]);
  assert.deepEqual(Array.from(model.options("baseUom")), ["кг", "шт"]);

  model.setQuery("sku-003");
  assert.deepEqual(ids(model.filtered()), [3], "SKU search");
  model.setQuery("04600000000002");
  assert.deepEqual(ids(model.filtered()), [2], "GTIN search");
  model.setQuery("дижонская");
  assert.deepEqual(ids(model.filtered()), [2], "name search");
  model.setQuery("традиция");
  assert.deepEqual(ids(model.filtered()), [3], "brand search");
  model.setQuery("200 г");
  assert.deepEqual(ids(model.filtered()), [1, 3], "volume search");
  model.setQuery("");

  model.setNone("brand");
  assert.deepEqual(ids(model.filtered()), [], "Нет means no selected options");
  model.setSelected("brand", "Вкус & Цвет", true);
  assert.deepEqual(ids(model.filtered()), [2, 1]);
  model.setSelected("brand", "Традиция", true);
  model.setNone("volume");
  model.setSelected("volume", "200 г", true);
  model.setNone("baseUom");
  model.setSelected("baseUom", "шт", true);
  assert.deepEqual(ids(model.filtered()), [1, 3], "three independent multi-select filters combine");
  model.setSelected("brand", "Традиция", false);
  assert.deepEqual(ids(model.filtered()), [1], "deselect one brand leaves other selected");
  model.setAll("brand");
  assert.deepEqual(ids(model.filtered()), [1, 3], "Все restores an entire group");
  model.setQuery("SKU-001");
  assert.deepEqual(ids(model.filtered()), [1], "query composes with filters");
  model.reset();
  assert.equal(model.getQuery(), "");
  assert.deepEqual(ids(model.filtered()), [2, 1, 3], "reset restores all three groups and clears search");
  assert.equal(model.itemById(4), null, "non-selectable item cannot be selected");
}

function fakeElement(name) {
  const attributes = new Map();
  return {
    name: name,
    isConnected: true,
    attributes,
    focusCount: 0,
    value: "",
    innerHTML: "",
    textContent: "",
    setAttribute: function (key, value) { attributes.set(key, String(value)); },
    getAttribute: function (key) { return attributes.has(key) ? attributes.get(key) : null; },
    removeAttribute: function (key) { attributes.delete(key); },
    focus: function () { this.focusCount += 1; },
    matches: function (selector) {
      return selector === "[data-picker-search]" && name === "search";
    }
  };
}

function fakeDocument() {
  let overlay = null;
  const nodes = {
    ".pc-order-picker-card": fakeElement("card"),
    "[data-picker-filters]": fakeElement("filters"),
    "[data-picker-count]": fakeElement("count"),
    "[data-picker-results]": fakeElement("results"),
    "[data-picker-search]": fakeElement("search"),
  };
  nodes[".pc-order-picker-card"].querySelectorAll = function () { return []; };
  const body = {
    appendChild: function (element) {
      overlay = element;
      element.parentNode = body;
      element.isConnected = true;
    },
    removeChild: function (element) {
      assert.equal(element, overlay);
      element.isConnected = false;
      element.parentNode = null;
      overlay = null;
    }
  };
  const document = {
    body,
    createElement: function () {
      const element = fakeElement("overlay");
      element.events = {};
      element.addEventListener = function (type, callback) { this.events[type] = callback; };
      element.querySelector = function (selector) { return nodes[selector] || null; };
      element.contains = function () { return true; };
      return element;
    }
  };
  return { document, current: function () { return overlay; }, nodes };
}

function action(kind, key, itemId) {
  return {
    closest: function () { return this; },
    getAttribute: function (attr) {
      return { "data-picker-action": kind, "data-picker-group": key,
        "data-picker-item": itemId }[attr] || null;
    }
  };
}

function renderEscape(value) {
  return String(value).replace(/&/g, "&amp;").replace(/</g, "&lt;")
    .replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#39;");
}

// Evaluate the actual production duplicate/selection function in isolation,
// so the new picker calls the SAME path as the existing autocomplete.
function createActualSelector(linesState) {
  const from = appSource.indexOf("    function applySuggestedItem(index, selectedId) {");
  const to = appSource.indexOf("    function snapshotDraft()", from);
  assert.ok(from > 0 && to > from, "production applySuggestedItem anchors exist");
  const controls = {};
  const sandbox = {
    linesState,
    activeLineIndex: 0,
    refs: { linesWrap: { querySelector: function (selector) { return controls[selector] || null; } } },
    getItemById: function (id) { return products.find(function (p) { return p.id === id; }); },
    buildItemLabel: function (item) { return item.name + " · GTIN: " + item.gtin; },
    renderLines: function () {},
    hideSuggestionOverlay: function () {},
    setWarningStatus: function (message) { sandbox.warning = message; },
    scheduleDuplicateWarningClear: function () { sandbox.warningScheduled = true; },
  };
  vm.createContext(sandbox);
  vm.runInContext(appSource.slice(from, to) + "\nthis.productionSelect = applySuggestedItem;", sandbox);
  return { select: sandbox.productionSelect, sandbox, controls };
}

function testModalWorkflow() {
  const fake = fakeDocument();
  const parent = fakeElement("new-order");
  const prior = parent.getAttribute("aria-hidden");
  const linesState = [
    { item_id: 1, query: "Горчица русская", qty_ordered: "25", locked: true },
    { item_id: 0, query: "", qty_ordered: "12", locked: false }
  ];
  const actual = createActualSelector(linesState);
  const qtySecond = fakeElement("qty2");
  const qtyFirst = fakeElement("qty1");
  qtyFirst.select = function () {};
  qtySecond.select = function () {};
  actual.controls['.line-qty[data-index="0"]'] = qtyFirst;
  actual.controls['.line-qty[data-index="1"]'] = qtySecond;

  let submitCount = 0;
  const picker = createPicker({
    items: products,
    parentModal: parent,
    escapeHtml: renderEscape,
    bindModalDismiss: function (_element, onClose) {
      fake.dismiss = onClose;
      return function () { fake.dismiss = null; };
    },
    onSelect: function (index, itemId) {
      actual.select(index, itemId);
    }
  });

  const openButton = fakeElement("open");
  picker.open(1, openButton);
  assert.ok(picker.isOpen());
  assert.equal(parent.getAttribute("aria-hidden"), "true");
  assert.match(fake.nodes["[data-picker-filters]"].innerHTML, /Вкус &amp; Цвет/,
    "filter values are HTML escaped");
  assert.match(fake.nodes["[data-picker-results]"].innerHTML, /Горчица русская/);
  assert.doesNotMatch(fake.nodes["[data-picker-results]"].innerHTML, /Скрытый товар/);
  assert.match(fake.current().innerHTML, /Строка заказа № 2/);
  fake.current().events.input({ target: Object.assign(fake.nodes["[data-picker-search]"], { value: "sku-002" }) });
  assert.equal(picker.model.getQuery(), "sku-002");
  assert.equal(fake.nodes["[data-picker-count]"].textContent, "Найдено товаров: 1");
  fake.current().events.change({
    target: { matches: function (s) { return s === "[data-picker-filter]"; },
      getAttribute: function (s) {
        return s === "data-picker-filter" ? "brand" : "Вкус & Цвет";
      }, checked: false }
  });
  assert.equal(picker.model.filtered().length, 0);
  // All resets only the brand group; search remains persisted.
  fake.current().events.click({ target: action("all", "brand") });
  assert.deepEqual(ids(picker.model.filtered()), [2]);
  fake.dismiss(); // Escape / backdrop invokes canonical modal dismiss
  assert.equal(picker.isOpen(), false);
  assert.equal(parent.getAttribute("aria-hidden"), prior);
  assert.equal(openButton.focusCount, 1);
  assert.equal(linesState[1].item_id, 0);
  assert.equal(linesState[1].qty_ordered, "12", "back does not modify order draft");
  assert.equal(submitCount, 0);

  const anotherTrigger = fakeElement("another");
  picker.open(1, anotherTrigger);
  assert.equal(fake.nodes["[data-picker-search]"].value, "", "fake DOM stores HTML as text; retained state is authoritative");
  assert.match(fake.current().innerHTML, /value="sku-002"/, "search persists across opens");
  assert.deepEqual(ids(picker.model.filtered()), [2], "filters and query persist across lines");
  fake.current().events.click({ target: action("select", null, "2") });
  assert.equal(picker.isOpen(), false);
  assert.equal(linesState[1].item_id, 2);
  assert.equal(linesState[1].locked, true);
  assert.equal(linesState[1].qty_ordered, "12");
  assert.equal(qtySecond.focusCount, 1, "quantity receives focus via actual autocomplete path");
  assert.equal(submitCount, 0, "picker never submits a request");

  picker.open(1, anotherTrigger);
  fake.current().events.click({ target: action("reset") });
  assert.equal(picker.model.getQuery(), "");
  assert.deepEqual(ids(picker.model.filtered()), [2, 1, 3]);
  fake.current().events.click({ target: action("select", null, "1") });
  assert.equal(linesState[1].item_id, 2, "duplicate never overwrites current line");
  assert.match(actual.sandbox.warning, /Товар уже добавлен/);
  assert.ok(linesState[0].isDuplicateTarget, "duplicate highlights existing line");
  assert.equal(qtyFirst.focusCount, 1, "duplicate redirects to existing quantity");
  assert.equal(actual.sandbox.warningScheduled, true);

  picker.open(1, anotherTrigger);
  fake.current().events.click({ target: action("select", null, "4") });
  assert.ok(picker.isOpen(), "inactive item not selectable via crafted click");
  picker.dispose();
  assert.equal(parent.getAttribute("aria-hidden"), null);
  assert.equal(submitCount, 0);
}

testModel();
const dom = fakeDocument();
context.document = dom.document;
testModalWorkflow();

assert.match(indexSource, /pc-order-item-picker\.js[\s\S]*app\.js/,
  "selector is loaded before app.js");
assert.match(appSource, /var catalogPicker = window\.FlowStockPcOrderItemPicker\.create\(/);
assert.match(appSource, /onSelect: function \(index, selectedId\) \{\s*applySuggestedItem\(index, selectedId\)/,
  "new picker must use existing duplicate protection and focus");
assert.match(appSource, /line-catalog-picker-btn/);
assert.match(appSource, /catalogPicker\.setItems\(items\)/,
  "selector must use filtered server-owned order reference items");
assert.match(appSource, /fetchJson\("\/api\/orders\/requests\/create"/,
  "existing submit request path remains unchanged");
assert.match(cssSource, /\.pc-order-picker-modal/);

console.log("PC new-order catalog picker: PASS");
