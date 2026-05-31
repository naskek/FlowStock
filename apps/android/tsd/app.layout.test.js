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
    buildOperationsMenuBody.includes('data-op="'),
  "operations tiles should keep existing routes and operation handlers"
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
    buildHomeMenuBody.includes('"items"') &&
    buildHomeMenuBody.includes('"orders"') &&
    buildHomeMenuBody.includes('"settings"') &&
    buildHomeMenuTileBody.includes('data-route="') &&
    buildMenuTileBody.includes("home-menu-tile"),
  "home tiles should use existing routes for operations, catalog, orders, and settings"
);
assert(
  buildHomeMenuBody.includes("Операции") &&
    buildHomeMenuBody.includes("Каталог") &&
    buildHomeMenuBody.includes("Заказы") &&
    buildHomeMenuBody.includes("Информация") &&
    buildHomeMenuBody.includes("Синхронизация, статус и полезные сведения"),
  "home should render four tile labels with information subtitle"
);
assert(
  buildHomeMenuTileBody.includes('class="home-menu-icon"') &&
    buildHomeMenuTileBody.includes("home-menu-icon-bubble") &&
    buildHomeMenuBody.includes("img/home/operations.png") &&
    buildHomeMenuBody.includes("img/home/catalogue.png") &&
    buildHomeMenuBody.includes("img/home/orders.png") &&
    buildHomeMenuBody.includes("img/home/info.png") &&
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
    serviceWorkerJs.includes('"./img/home/info.png"'),
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
