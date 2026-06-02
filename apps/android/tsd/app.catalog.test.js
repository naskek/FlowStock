const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const appJs = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");

const hooks = {};
const context = {
  console,
  window: {
    FlowStockTsdTestHooks: hooks,
    location: { hash: "" },
    setTimeout: function () {
      return 0;
    },
    clearTimeout: function () {},
    setInterval: function () {
      return 0;
    },
    addEventListener: function () {},
  },
  document: {
    documentElement: {
      classList: {
        toggle: function () {},
      },
    },
    getElementById: function () {
      return null;
    },
    querySelector: function () {
      return null;
    },
    querySelectorAll: function () {
      return [];
    },
    addEventListener: function () {},
  },
  localStorage: {
    setItem: function () {},
    getItem: function () {
      return null;
    },
  },
  navigator: {},
  TsdStorage: {},
};
context.window.document = context.document;
context.window.localStorage = context.localStorage;
context.window.navigator = context.navigator;
context.window.TsdStorage = context.TsdStorage;

vm.createContext(context);
vm.runInContext(appJs, context, { filename: "app.js" });

const itemsHtml = hooks.renderItems();
assert.match(itemsHtml, /id="itemsBrandFilter"/);
assert.match(itemsHtml, /Бренд/);
assert.match(itemsHtml, /Название, бренд, объем, SKU, GTIN или штрихкод/);

const items = [
  {
    itemId: 1,
    name: "Аджика Печагин, 1 кг",
    brand: "Печагин",
    volume: "1 кг",
    sku: "A-1",
    gtin: "0461",
  },
  {
    itemId: 2,
    name: "Аджика Печагин, 200 гр",
    brand: "Печагин",
    volume: "200 гр",
    sku: "A-2",
  },
  {
    itemId: 3,
    name: "Соус Мирный, 500 гр",
    brand: "Мирный",
    volume: "500 гр",
    barcode: "4600",
  },
  {
    itemId: 4,
    name: "Товар без объема",
    brand: "",
    volume: "",
  },
];

assert.deepStrictEqual(Array.from(hooks.buildCatalogBrandOptions(items)), ["Мирный", "Печагин"]);

const filteredByBrand = hooks.filterCatalogItems(items, "", "Печагин");
assert.deepStrictEqual(
  Array.from(filteredByBrand).map(function (item) {
    return item.itemId;
  }),
  [1, 2],
  "brand filter should keep only selected brand"
);

const filteredBySearch = hooks.filterCatalogItems(items, "4600", "");
assert.deepStrictEqual(
  Array.from(filteredBySearch).map(function (item) {
    return item.itemId;
  }),
  [3],
  "catalog search should match barcode and existing fields"
);

const groups = hooks.groupCatalogItemsByVolume(items);
assert.deepStrictEqual(
  Array.from(groups).map(function (group) {
    return group.label;
  }),
  ["200 гр", "500 гр", "1 кг", "Без объема"],
  "volume groups should sort by normalized amount and keep missing volume last"
);

console.log("TSD catalog presentation tests passed.");
