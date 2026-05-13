const fs = require("fs");
const path = require("path");
const assert = require("assert");

const appJs = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");
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

const operationsMenu = extractFunctionBody(appJs, "wireOperationsMenu");
assert(
  operationsMenu.includes('document.querySelectorAll("[data-route]")'),
  "operations menu should wire route buttons"
);
assert(
  operationsMenu.includes('navigate("/" + route)'),
  "filling route click should navigate instead of becoming a no-op"
);

assert(
  appJs.includes('<button class="btn menu-btn" data-route="filling">Наполнение</button>'),
  "filling button should use the common menu button design"
);
assert(
  appJs.includes("renderFillingLoading()"),
  "filling route should show the filling loading screen immediately"
);
assert(
  appJs.includes("TsdStorage.apiGetProductionFillingOrders()"),
  "filling route should load /api/tsd/production/filling-orders"
);
assert(
  appJs.includes("Выберите заказ с подготовленными паллетами.") &&
    appJs.includes("Нет заказов с подготовленными паллетами для наполнения"),
  "empty filling list message should be visible"
);
assert(
  appJs.includes("TsdStorage.apiGetProductionFillingContext(orderId)") &&
    appJs.includes('data-filling-order="'),
  "selecting an order should load existing filling context and open scan screen"
);
assert(
  appJs.includes("Не удалось загрузить заказы для наполнения") && appJs.includes("console.error(error)"),
  "filling API failures should be visible and logged"
);
assert(
  !appJs.includes("apiStartProductionFilling(orderId)"),
  "TSD filling should not call the old start-filling flow"
);
assert(
  /CACHE_NAME\s*=\s*"tsd-shell-v\d+"/.test(serviceWorkerJs) && serviceWorkerJs.includes('"./app.js"'),
  "service worker should version and cache app.js"
);

console.log("TSD filling presentation tests passed.");
