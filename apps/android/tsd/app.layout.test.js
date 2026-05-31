const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const appJs = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");
const stylesCss = fs.readFileSync(path.join(__dirname, "styles.css"), "utf8");
const indexHtml = fs.readFileSync(path.join(__dirname, "index.html"), "utf8");

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

const renderHomeBody = extractFunctionBody(appJs, "renderHome");
assert(
  renderHomeBody.includes("home-screen--centered") &&
    renderHomeBody.includes("home-menu-wrap") &&
    renderHomeBody.includes("menu-grid"),
  "home screen should render centered menu layout"
);
assert(
  !renderHomeBody.includes("Позиции ниже минимума"),
  "home screen should not render below-minimum card"
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
    stylesCss.includes(".home-menu-wrap"),
  "styles should define centered home menu layout"
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
