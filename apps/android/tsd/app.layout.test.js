const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const appJs = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");
const stylesCss = fs.readFileSync(path.join(__dirname, "styles.css"), "utf8");
const indexHtml = fs.readFileSync(path.join(__dirname, "index.html"), "utf8");
const serviceWorkerJs = fs.readFileSync(path.join(__dirname, "service-worker.js"), "utf8");

function extractFunctionBody(source, name) {
  const marker = `function ${name}(`;
  const start = source.indexOf(marker);
  assert.notStrictEqual(start, -1, `${name} should exist`);
  const braceStart = source.indexOf("{", start);
  let depth = 0;
  for (let i = braceStart; i < source.length; i += 1) {
    if (source[i] === "{") {
      depth += 1;
    } else if (source[i] === "}") {
      depth -= 1;
      if (depth === 0) {
        return source.slice(braceStart + 1, i);
      }
    }
  }
  throw new Error(`${name} body was not closed`);
}

function extractCssRuleBody(source, selector) {
  const escapedSelector = selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const match = new RegExp(`(^|\\n)\\s*${escapedSelector}\\s*\\{`, "m").exec(source);
  assert(match, `${selector} rule should exist`);
  const braceStart = source.indexOf("{", match.index);
  let depth = 0;
  for (let i = braceStart; i < source.length; i += 1) {
    if (source[i] === "{") {
      depth += 1;
    } else if (source[i] === "}") {
      depth -= 1;
      if (depth === 0) {
        return source.slice(braceStart + 1, i);
      }
    }
  }
  throw new Error(`${selector} rule was not closed`);
}

function assertCssContains(selector, snippets, message) {
  const body = extractCssRuleBody(stylesCss, selector);
  snippets.forEach(function (snippet) {
    assert(body.includes(snippet), `${message}: missing ${snippet} in ${selector}`);
  });
  return body;
}

function assertCssDoesNotMatch(selector, pattern, message) {
  const body = extractCssRuleBody(stylesCss, selector);
  assert.doesNotMatch(body, pattern, message);
  return body;
}

function assertNoFillingHuTouchBlockers(selector) {
  assertCssDoesNotMatch(
    selector,
    /touch-action\s*:\s*none|pointer-events\s*:\s*none|overflow\s*:\s*hidden/,
    `${selector} should not block touch scrolling`
  );
}

const renderSettingsBody = extractFunctionBody(appJs, "renderSettings");
assert(
  !renderSettingsBody.includes("Разрешить экранную клавиатуру") &&
    !renderSettingsBody.includes("softKeyboardToggle"),
  "settings should not render soft keyboard toggle"
);
assert(
  renderSettingsBody.includes('id="tsdThemeToggle"') &&
    renderSettingsBody.includes("Темная тема"),
  "settings should render dark theme toggle"
);
assert(
  renderSettingsBody.includes('id="pwaCheckUpdateBtn"') &&
    renderSettingsBody.includes("Проверить обновления") &&
    renderSettingsBody.indexOf("pwaCheckUpdateBtn") <
      renderSettingsBody.indexOf('id="pwaAppVersion"'),
  "settings should render app version after update check button"
);
assert(
  renderSettingsBody.includes("settings-version--centered"),
  "settings version should use centered muted class"
);

const renderOperationsMenuBody = extractFunctionBody(appJs, "renderOperationsMenu");
const buildOperationsMenuBody = extractFunctionBody(appJs, "buildOperationsMenuButtonsHtml");
assert(
  renderOperationsMenuBody.includes("operations-menu-grid--2x6") &&
    renderOperationsMenuBody.includes("operations-screen--centered"),
  "operations screen should render centered 2x6 tile grid"
);
const buildMenuTileBody = extractFunctionBody(appJs, "buildMenuTile");
assert(
  buildOperationsMenuBody.includes("buildMenuTile(") &&
    buildMenuTileBody.includes("home-menu-tile") &&
    !buildOperationsMenuBody.includes("menu-btn") &&
    !buildMenuTileBody.includes("<svg"),
  "operations menu should use centered text tiles without inline svg"
);
assert(
  buildOperationsMenuBody.includes('data-route="filling"') &&
    buildOperationsMenuBody.includes('data-route="outbound"') &&
    buildOperationsMenuBody.includes('data-route="order-control"') &&
    buildOperationsMenuBody.includes("Контроль заказов") &&
    buildOperationsMenuBody.includes('data-op="'),
  "operations tiles should keep existing routes, order control, and operation handlers"
);

const renderHomeBody = extractFunctionBody(appJs, "renderHome");
const buildHomeMenuBody = extractFunctionBody(appJs, "buildHomeMenuButtonsHtml");
assert(
  renderHomeBody.includes("home-screen--centered") &&
    renderHomeBody.includes("home-menu-wrap") &&
    renderHomeBody.includes("home-menu-grid"),
  "home screen should render centered tile menu layout"
);
assert(
  !renderHomeBody.includes("Позиции ниже минимума"),
  "home screen should not render below-minimum card"
);
const buildHomeMenuTileBody = extractFunctionBody(appJs, "buildHomeMenuTile");
assert(
  buildHomeMenuBody.includes('"operations"') &&
    buildHomeMenuBody.includes('"stock"') &&
    buildHomeMenuBody.includes('"orders"') &&
    buildHomeMenuBody.includes('"hu"') &&
    !buildHomeMenuBody.includes('"order-control"') &&
    !buildHomeMenuBody.includes("Контроль заказов") &&
    !buildHomeMenuBody.includes('"items"') &&
    buildHomeMenuTileBody.includes('data-route="') &&
    buildMenuTileBody.includes("home-menu-tile"),
  "home tiles should use existing routes for operations, stock, orders, and HU lookup without direct order control"
);
assert(
  buildHomeMenuBody.includes("Операции") &&
    buildHomeMenuBody.includes("Склад") &&
    buildHomeMenuBody.includes("Остатки, потребность и производство") &&
    buildHomeMenuBody.includes("Заказы") &&
    buildHomeMenuBody.includes("Поиск HU") &&
    buildHomeMenuBody.includes("Сканирование и поиск паллеты") &&
    !buildHomeMenuBody.includes("Каталог"),
  "home should render the stock tile instead of catalog with its subtitle"
);
assert(
  buildHomeMenuTileBody.includes('class="home-menu-icon"') &&
    buildHomeMenuTileBody.includes("home-menu-icon-bubble") &&
    buildHomeMenuBody.includes("img/home/operations.png") &&
    buildHomeMenuBody.includes("img/home/catalogue.png") &&
    buildHomeMenuBody.includes("img/home/orders.png") &&
    buildHomeMenuBody.includes("img/home/hu-search.png") &&
    !buildHomeMenuTileBody.includes("<svg") &&
    !buildHomeMenuBody.includes("<svg") &&
    !buildHomeMenuBody.includes("menu-btn"),
  "home should render png icons in colored bubbles without inline svg"
);
assert(
  buildMenuTileBody.includes("home-menu-tile__content") &&
    !buildMenuTileBody.includes("<svg"),
  "shared menu tile helper should stay text-only for operations screen"
);
assert(
  stylesCss.includes(".home-menu-icon-bubble") &&
    stylesCss.includes(".home-menu-icon") &&
    stylesCss.includes("--home-bubble-operations-bg") &&
    stylesCss.includes(".home-menu-tile__content") &&
    stylesCss.includes("align-items: center") &&
    stylesCss.includes("text-align: center"),
  "styles should center tile content and style icon bubbles"
);
assert(
  serviceWorkerJs.includes('"./img/home/operations.png"') &&
    serviceWorkerJs.includes('"./img/home/catalogue.png"') &&
    serviceWorkerJs.includes('"./img/home/orders.png"') &&
    serviceWorkerJs.includes('"./img/home/hu-search.png"'),
  "service worker should cache home menu png icons for offline use"
);

const fillOverlayBody = extractFunctionBody(appJs, "openFillingPreviewOverlay");
const buildPreviewBody = extractFunctionBody(appJs, "buildFillingPreviewHtml");
assert(
  fillOverlayBody.includes("filling-preview-overlay-shell--compact") &&
    buildPreviewBody.includes("filling-preview-overlay-card--compact"),
  "filling confirmation modal should keep compact modifiers"
);
assert(
  !fillOverlayBody.includes("overlay-close") &&
    !fillOverlayBody.includes(">Закрыть</button>") &&
    !fillOverlayBody.includes("Подтверждение наполнения"),
  "filling confirmation modal should not render close button or legacy title"
);

assert(
  stylesCss.includes(".overlay--centered") &&
    stylesCss.includes("align-items: center") &&
    stylesCss.includes("justify-content: center") &&
    stylesCss.includes("--tsd-overlay-max") &&
    stylesCss.includes("100dvh"),
  "styles should define centered responsive overlay layout"
);
assert(
  stylesCss.includes(".app-content.route-transition-active > .screen") &&
    stylesCss.includes("@keyframes tsd-route-enter") &&
    stylesCss.includes("animation: tsd-route-enter 190ms ease-out both") &&
    stylesCss.includes(".app-content.route-transition-exit > .screen"),
  "styles should define opacity-only route-level enter/exit screen transitions"
);
const routeTransitionCss = [
  extractCssRuleBody(stylesCss, ".app-content.route-transition-active > .screen"),
  extractCssRuleBody(stylesCss, ".app-content.route-transition-exit > .screen"),
  stylesCss.slice(
    stylesCss.indexOf("@keyframes tsd-route-enter"),
    stylesCss.indexOf("@media (prefers-reduced-motion: reduce)")
  ),
].join("\n");
assert.doesNotMatch(
  routeTransitionCss,
  /\btransform\b/,
  "route transitions should not transform route wrappers or screens"
);
assert(
  stylesCss.includes("@media (prefers-reduced-motion: reduce)") &&
    stylesCss.includes(".app-content.route-transition-active > .screen") &&
    stylesCss.includes("animation: none") &&
    stylesCss.includes("transition: none"),
  "route transitions should respect reduced motion"
);
assert(
  stylesCss.includes(".filling-screen--scan .filling-card--scan") &&
    stylesCss.includes(".filling-screen--scan .filling-pallet-list") &&
    stylesCss.includes("height: auto") &&
    stylesCss.includes("max-height: none") &&
    stylesCss.includes(".filling-screen--scan .filling-pallet-list--scroll-breathing") &&
    stylesCss.includes("padding-bottom: clamp(8px, 2dvh, 16px)"),
  "filling scan screen should use page-level scroll with the pallet list as normal content"
);
assertCssContains(
  ".app-content",
  ["flex: 1", "min-height: 0", "overflow-x: hidden", "overflow-y: auto", "-webkit-overflow-scrolling: touch"],
  "app content should be the route-level mobile scroll container"
);
assertCssContains(
  ".screen",
  ["display: flex", "flex-direction: column", "min-height: 0"],
  "screen should be a flex parent for scrollable route content"
);
assertCssContains(
  ".screen-card",
  ["display: flex", "flex-direction: column", "min-height: 0"],
  "screen cards should be flex parents for inner scroll containers"
);
assertCssContains(
  ".doc-screen-card",
  ["flex: 1 1 auto", "min-height: 0"],
  "document cards should allow child scroll areas to shrink"
);
assertCssContains(
  ".filling-card--scan .filling-pallet-list-card",
  ["flex: 0 0 auto", "min-height: 0", "display: flex", "flex-direction: column"],
  "filling scan list card should stay in normal page flow"
);
assertCssContains(
  ".filling-pallet-list-card",
  ["display: flex", "flex-direction: column", "min-height: 0"],
  "pallet list card should keep compact list layout without owning scroll"
);
assertCssContains(
  ".filling-pallet-list",
  [
    "display: flex",
    "flex-direction: column",
    "height: auto",
    "max-height: none",
    "min-height: 0",
    "flex: 0 0 auto",
    "overflow: visible",
  ],
  "pallet list should be normal document content, not an inner scroll container"
);
assertCssDoesNotMatch(
  ".filling-pallet-list",
  /(?:^|[;\n])\s*(?:overflow-y\s*:\s*auto|max-height\s*:\s*(?!\s*none\b)|height\s*:\s*100%|flex\s*:\s*1\b|scroll-padding)/i,
  "filling pallet list should not trap scrolling"
);
assertCssContains(
  ".filling-screen--scan .filling-pallet-list",
  ["flex: 0 0 auto", "min-height: 0", "height: auto", "max-height: none", "overflow: visible"],
  "scan screen pallet list should remain page-flow content"
);
assertCssDoesNotMatch(
  ".filling-screen--scan .filling-card--scan",
  /(?:^|[;\n])\s*(?:flex\s*:\s*1\b|height\s*:\s*100%|overflow(?:-y)?\s*:\s*(?:auto|hidden))/i,
  "filling scan card should not create a nested scroll trap"
);
assertCssDoesNotMatch(
  ".filling-card--scan .filling-pallet-list-card",
  /(?:^|[;\n])\s*(?:flex\s*:\s*1\b|height\s*:\s*100%|max-height\s*:\s*(?!\s*none\b)|overflow(?:-y)?\s*:\s*(?:auto|hidden))/i,
  "filling list card should not create a nested scroll trap"
);
assertCssDoesNotMatch(
  ".filling-pallet-list--scroll-breathing",
  /(?:^|[;\n])\s*(?:scroll-padding|overflow(?:-y)?\s*:\s*auto|max-height\s*:\s*(?!\s*none\b)|height\s*:\s*100%|flex\s*:\s*1\b)/i,
  "filling breathing class should not add inner-scroll behavior"
);
assertCssContains(
  ".filling-pallet-group",
  ["display: flex", "flex: 0 0 auto", "flex-direction: column"],
  "pallet groups should stay content-sized"
);
assertCssContains(
  ".filling-pallet-group-items",
  ["display: flex", "flex-direction: column", "min-height: 0"],
  "pallet group item lists should not stretch rows"
);
assertCssContains(
  ".filling-pallet-item",
  ["flex: 0 0 auto", "align-self: stretch"],
  "HU rows should be compact content-sized rows"
);
assertCssDoesNotMatch(
  ".filling-pallet-item",
  /pointer-events\s*:\s*none/,
  "global HU row selector should not disable pointer events"
);
assertCssDoesNotMatch(
  ".filling-pallet-item",
  /(?:^|;)\s*(?:flex\s*:\s*1\b|height\s*:\s*100%|align-self\s*:\s*(?:normal|auto|center|flex-start|flex-end)\b)/,
  "HU rows should not grow vertically or use fixed full height"
);
assertCssDoesNotMatch(
  ".filling-pallet-item--compact",
  /(?:^|;)\s*(?:flex\s*:\s*1\b|height\s*:\s*100%)/,
  "compact HU rows should not grow vertically or use fixed full height"
);
[
  ".filling-pallet-list",
  ".filling-pallet-group",
  ".filling-pallet-group-title",
  ".filling-pallet-group-items",
  ".filling-pallet-item",
  ".filling-pallet-item--compact",
  ".filling-pallet-dot",
  ".filling-pallet-code",
].forEach(assertNoFillingHuTouchBlockers);
assertCssDoesNotMatch(
  ".app-content.route-transition-active > .screen",
  /overflow(?:-y)?\s*:\s*hidden|pointer-events\s*:\s*none|touch-action\s*:\s*none/,
  "active route transition screen should not block scrolling"
);
assertCssDoesNotMatch(
  ".app-content.route-transition-exit > .screen",
  /overflow(?:-y)?\s*:\s*hidden|pointer-events\s*:\s*none|touch-action\s*:\s*none/,
  "exit route transition screen should not block scrolling"
);
assert(
  stylesCss.includes(".order-details-screen") &&
    stylesCss.includes(".order-details-card") &&
    stylesCss.includes(".order-details-card .order-lines") &&
    stylesCss.includes("overflow-y: auto") &&
    stylesCss.includes(".order-line-hu-panel"),
  "order details should define adaptive height and read-only HU expansion styles"
);
assert(
  appJs.includes('<section class="screen order-details-screen">') &&
    appJs.includes('class="screen-card doc-screen-card order-details-card"') &&
    appJs.includes('id="itemsList"') &&
    appJs.includes('class="items-toolbar"') &&
    appJs.includes("outbound-picking-hu-list") &&
    appJs.includes('data-outbound-order="'),
  "orders, order details, items, and outbound routes should keep their route layout hooks"
);
assertCssContains(
  ".order-details-card .order-lines",
  ["overflow-y: auto", "overscroll-behavior: contain"],
  "order detail line list should keep its intended local scroll behavior"
);
assertCssContains(
  ".catalog-volume-items",
  ["display: grid", "gap: 6px"],
  "items catalogue list should keep normal page-flow grouped layout"
);
assert(
  stylesCss.includes(".home-screen--centered") &&
    stylesCss.includes(".home-menu-wrap") &&
    stylesCss.includes(".home-menu-grid") &&
    stylesCss.includes(".home-menu-tile__content") &&
    stylesCss.includes(".operations-menu-grid--2x6") &&
    stylesCss.includes(".operations-screen--centered"),
  "styles should define centered home and operations tile menu layout"
);
assert(
  stylesCss.includes("html.tsd-theme-dark") && stylesCss.includes("--tsd-bg-start"),
  "styles should define dark theme variables"
);
assert(
  indexHtml.includes("viewport-fit=cover"),
  "index.html should enable safe-area viewport fit"
);

assert(
  appJs.includes('TSD_THEME_KEY = "flowstock.tsd.theme"') &&
    appJs.includes("function applyTsdTheme(") &&
    appJs.includes("function initTsdTheme(") &&
    appJs.includes('TSD_THEME_LIGHT = "light"') &&
    appJs.includes('TSD_THEME_DARK = "dark"'),
  "app.js should define theme storage helpers with light default"
);
assert(
  appJs.includes("function getRouteTransitionKey(") &&
    appJs.includes("function prepareRouteTransition(") &&
    appJs.includes("prepareRouteTransition(route)") &&
    appJs.includes("markRouteTransitionExit()"),
  "app.js should manage route transitions through route-level helpers"
);

const storage = {};
const rootClasses = new Set(["tsd-theme-light"]);
const hooks = {};
const context = {
  console,
  window: {
    FlowStockTsdTestHooks: hooks,
    localStorage: {
      setItem: function (key, value) {
        storage[key] = String(value);
      },
      getItem: function (key) {
        return Object.prototype.hasOwnProperty.call(storage, key) ? storage[key] : null;
      },
    },
    setTimeout: function () {
      return 0;
    },
    clearTimeout: function () {},
    setInterval: function () {
      return 0;
    },
    addEventListener: function () {},
    location: { hash: "" },
  },
  document: {
    documentElement: {
      classList: {
        toggle: function (className, force) {
          if (force) {
            rootClasses.add(className);
          } else {
            rootClasses.delete(className);
          }
        },
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
  localStorage: null,
  navigator: {},
  TsdStorage: {
    init: function () {
      return Promise.resolve();
    },
    ensureDefaults: function () {
      return Promise.resolve();
    },
    getSetting: function () {
      return Promise.resolve(null);
    },
  },
};

context.window.document = context.document;
context.window.localStorage = context.window.localStorage;
context.localStorage = context.window.localStorage;

vm.createContext(context);
vm.runInContext(appJs, context, { filename: "app.js" });

assert.strictEqual(hooks.getRouteTransitionKey({ name: "outboundOrder", id: 93 }), "outboundOrder:93");
assert.strictEqual(hooks.getRouteTransitionKey({ name: "items" }), "items");
assert.match(hooks.renderHome(), /home-screen/, "home route renderer should still render target screen markup");

const stockScreenHtml = hooks.renderStock();
assert.match(stockScreenHtml, /Состояние склада/, "stock route should render warehouse state title");
assert.match(stockScreenHtml, /id="stockSearchInput"/, "stock screen should render search field");
assert.match(stockScreenHtml, /id="stockList"/, "stock screen should render card list container");
assert.match(stockScreenHtml, /id="stockMessage"/, "stock screen should render state message container");
assert.doesNotMatch(stockScreenHtml, /Сформировать заказ/, "TSD stock screen should not render create-order button");
assert.doesNotMatch(stockScreenHtml, /stockScanInput|stockLocationFilter/, "stock screen should drop legacy HU scan controls");

const stockStateRow = hooks.mapWarehouseProductionStateRow(
  {
    item_id: 1,
    item_name: "Горчица, Печагин, 1 кг",
    base_uom: "Шт",
    sku: "SKU-1",
    barcode: "04607186951544",
    stock_qty: 600,
    min_stock_qty: 3600,
    below_min_qty: 3000,
    need_breakdown: {
      demand_to_close_customer_orders: 2400,
      demand_to_min_stock: 3000,
      already_planned_internal: 3600,
      remaining_to_create: 1200,
    },
    hu_rows: [{ hu_code: "HU-1", location: "A-01", qty: 600, stock_status: "На складе" }],
    production_receipts: [
      { hu_code: "HU-9", prd_ref: "PRD-1", pallet_status_display: "Печать", qty: 0, source_order_ref: "149" },
    ],
  },
  {}
);
assert.strictEqual(stockStateRow.status, "below", "below-minimum stock should map to below status");
assert.strictEqual(stockStateRow.huRows.length, 1, "warehouse HU rows should be mapped");
assert.strictEqual(stockStateRow.productionReceipts.length, 1, "production receipts should be mapped");

const stockCardHtml = hooks.renderStockStateCard(stockStateRow);
assert.match(stockCardHtml, /stock-state-item--below/, "below card should carry below status class");
assert.match(stockCardHtml, /Ниже минимума/, "below card should show below-minimum chip");
assert.match(stockCardHtml, /Складские HU/, "expanded card should show warehouse HU section");
assert.match(stockCardHtml, /План \/ производство/, "expanded card should show production section");
assert.match(stockCardHtml, /Расчёт потребности/, "expanded card should show need breakdown section");
assert.match(stockCardHtml, /data-stock-toggle="1"/, "card should expose toggle hook");

assert.strictEqual(
  hooks.filterWarehouseStateRows([stockStateRow], "04607186951544").length,
  1,
  "search should match by barcode"
);
assert.strictEqual(
  hooks.filterWarehouseStateRows([stockStateRow], "нет-такого").length,
  0,
  "search should return empty for unknown query"
);

assert.strictEqual(hooks.normalizeTsdTheme("dark"), "dark");
assert.strictEqual(hooks.normalizeTsdTheme("light"), "light");
assert.strictEqual(hooks.normalizeTsdTheme(""), "light");

context.window.localStorage.setItem("flowstock.tsd.theme", "dark");
assert.strictEqual(hooks.getStoredTsdTheme(), "dark");

hooks.applyTsdTheme("light");
assert.strictEqual(storage["flowstock.tsd.theme"], "light");
assert.ok(rootClasses.has("tsd-theme-light"));
assert.ok(!rootClasses.has("tsd-theme-dark"));

hooks.applyTsdTheme("dark");
assert.strictEqual(storage["flowstock.tsd.theme"], "dark");
assert.ok(rootClasses.has("tsd-theme-dark"));
assert.ok(!rootClasses.has("tsd-theme-light"));

console.log("TSD layout and theme tests passed.");
