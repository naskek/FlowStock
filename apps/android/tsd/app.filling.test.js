const fs = require("fs");
const path = require("path");
const assert = require("assert");

const appJs = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");
const storageJs = fs.readFileSync(path.join(__dirname, "storage.js"), "utf8");
const serviceWorkerJs = fs.readFileSync(path.join(__dirname, "service-worker.js"), "utf8");
const swUpdateJs = fs.readFileSync(path.join(__dirname, "sw-update.js"), "utf8");
const appVersionJs = fs.readFileSync(path.join(__dirname, "app-version.js"), "utf8");
const indexHtml = fs.readFileSync(path.join(__dirname, "index.html"), "utf8");
const scannerJs = fs.readFileSync(path.join(__dirname, "scanner.js"), "utf8");

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
  appJs.includes('if (op === "PRODUCTION_RECEIPT")') &&
    appJs.includes('onlyShipmentAvailable: doc.op === "OUTBOUND"'),
  "operations menu should hide the legacy production receipt button and keep outbound order filtering"
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
  appJs.includes("formatPalletCountValue(summary.filledPalletCount)") &&
    appJs.includes("renderFillingPalletStatusList(document.pallets)") &&
    appJs.includes('"is-filled"') &&
    appJs.includes('"is-pending"'),
  "filling screen should show explicit 0/N counters and a status list of pallet HU codes"
);
assert(
  appJs.includes('id="fillingScanInput" type="text"') &&
    appJs.includes('data-scan-allow="1"') &&
    appJs.includes('placeholder="HU-000001"'),
  "filling scan input should accept keyboard-wedge scanner text"
);
assert(
  appJs.includes("getOrderStatusInfoForOrder(order)") &&
    appJs.includes("statusDisplay || order.orderStatusDisplay || order.order_status_display"),
  "TSD order and filling lists should render status labels from the same status/display mapping"
);
assert(
  appJs.includes("getProductionPalletPlanInfo(order)") &&
    appJs.includes("order-item-needs-plan") &&
    storageJs.includes("hasProductionPalletPlan") &&
    storageJs.includes("needsProductionPalletPlan"),
  "TSD order list should surface pallet plan state and highlight orders without a pallet plan"
);
assert(
  appJs.includes('scanInput.addEventListener("keydown"') &&
    appJs.includes('event.key === "Enter"') &&
    appJs.includes("handleScannedValue(scanInput.value)"),
  "filling scan input should submit typed scanner text on Enter"
);
assert(
  scannerJs.includes("function unlockScanTarget(target)") &&
    scannerJs.includes('target.getAttribute("data-scan-readonly") !== "1"') &&
    scannerJs.includes("target.readOnly = false"),
  "keyboard scanner should temporarily unlock scan input when soft keyboard suppression made it readonly"
);
assert(
  scannerJs.includes("buffer += event.key") &&
    scannerJs.includes('flushBuffer("enter")') &&
    scannerJs.includes("isScanAllowedTarget(event.target)"),
  "keyboard scanner should collect scanner key events and flush on Enter"
);
assert(
  appJs.includes("TsdStorage.apiScanProductionPallet({") &&
    appJs.includes("orderId: context.workItem && context.workItem.orderId") &&
    appJs.includes("prdDocId: context.workItem && context.workItem.prdDocId"),
  "HU scan should include selected order and PRD context"
);
assert(
  appJs.includes("TsdStorage.apiFillProductionPallet({") &&
    appJs.includes("orderId: preview.orderId") &&
    appJs.includes("prdDocId: preview.prdDocId"),
  "fill confirmation should include scanned order and PRD context"
);
assert(
  appJs.includes("openFillingPreviewOverlay(context, activePreview)") &&
    appJs.includes('className = "overlay filling-preview-overlay"') &&
    appJs.includes("Подтверждение наполнения"),
  "fill confirmation should open in a separate modal overlay"
);
assert(
  appJs.includes("function buildProductionFillSuccessMessage(") &&
    appJs.includes("Паллета проведена.") &&
    appJs.includes('PRD " + prdRef + " закрыт.') &&
    appJs.includes("Заказ выполнен: все паллеты наполнены."),
  "final pallet fill should show pallet posted, PRD closed, and order completed status"
);
assert(
  storageJs.includes("prd_auto_closed") && storageJs.includes("closed_prd_doc_ref"),
  "fill API mapping should expose auto-closed PRD fields from the server"
);
assert(
  appJs.includes('<button class="btn menu-btn" data-route="outbound">Отгрузка</button>') &&
    appJs.includes("TsdStorage.apiGetOutboundPickingOrders()") &&
    appJs.includes("renderOutboundPickingList(orders || [])") &&
    appJs.includes('navigate("/outbound")'),
  "TSD outbound should open the new order pallet picking list"
);
assert(
  storageJs.includes('"/api/tsd/outbound/orders"') &&
    storageJs.includes("apiGetOutboundPickingOrders") &&
    storageJs.includes("apiScanOutboundPickingHu") &&
    storageJs.includes("apiCompleteOutboundPicking"),
  "TSD storage should call the outbound picking endpoints"
);
assert(
  appJs.includes('data-outbound-order="') &&
    appJs.includes("Подобрано") &&
    appJs.includes("Нет готовых клиентских заказов с ожидаемыми HU к отгрузке."),
  "outbound picking list should show accepted customer orders with picked progress"
);
assert(
  appJs.includes('id="outboundPickingScanInput"') &&
    appJs.includes("TsdStorage.apiScanOutboundPickingHu(orderId, huCode)") &&
    appJs.includes("HU не ожидается для выбранного заказа."),
  "outbound order screen should scan expected HU through the server"
);
assert(
  appJs.includes("TsdStorage.apiCompleteOutboundPicking(orderId)") &&
    appJs.includes("Все паллеты подобраны. Отгрузка проведена."),
  "outbound picking complete should reflect server auto-close message"
);
assert(
  appJs.includes("Микс-паллета") &&
    appJs.includes("preview.lines") &&
    appJs.includes("filling-preview-composition"),
  "mixed pallet preview should render composition lines"
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
  appVersionJs.includes('flowstock-tsd-v') && appVersionJs.includes("TSD_CACHE_NAME"),
  "app-version.js should define shared cache version"
);
assert(
  serviceWorkerJs.includes('importScripts("./app-version.js")') &&
    serviceWorkerJs.includes("SKIP_WAITING") &&
    serviceWorkerJs.includes("self.skipWaiting()") &&
    !serviceWorkerJs.includes(".then(() => self.skipWaiting())"),
  "service worker should use versioned cache and activate only after SKIP_WAITING"
);
assert(
  serviceWorkerJs.includes('"./app.js"') && serviceWorkerJs.includes('"./sw-update.js"'),
  "service worker should cache shell assets including sw-update.js"
);
assert(
  swUpdateJs.includes("Доступна новая версия приложения") &&
    swUpdateJs.includes("SKIP_WAITING") &&
    swUpdateJs.includes("controllerchange") &&
    swUpdateJs.includes("checkNow") &&
    swUpdateJs.includes("applyUpdate") &&
    swUpdateJs.includes("Ручная проверка обновления"),
  "sw-update.js should offer manual and automatic refresh lifecycle"
);
assert(
  appJs.includes('id="pwaCheckUpdateBtn"') && appJs.includes("Проверить обновления"),
  "settings should include manual PWA update check button"
);
assert(
  indexHtml.includes("sw-update.js") && indexHtml.includes("TsdSwUpdate.init"),
  "index.html should bootstrap PWA update UI"
);
assert(
  appJs.includes("FlowStockTsdIsBusy") && !appJs.includes("window.location.reload();\n        return true;"),
  "app.js should expose busy guard and avoid forced API-version reload"
);

console.log("TSD filling presentation tests passed.");
