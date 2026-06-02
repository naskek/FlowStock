const fs = require("fs");
const path = require("path");
const assert = require("assert");
const vm = require("vm");

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
  appJs.includes('data-route="filling"') &&
    appJs.includes("Наполнение") &&
    appJs.includes("home-menu-tile") &&
    !appJs.includes('<button class="btn menu-btn" data-route="filling">Наполнение</button>'),
  "filling entry should use centered operations tile instead of legacy menu button"
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
  appJs.includes("sortFillingListItems(items)") &&
    appJs.includes("getFillingWorkOrderSortValue"),
  "filling order list should sort orders by numeric order number in the frontend renderer"
);
assert(
  appJs.includes("formatPalletCountValue(summary.filledPalletCount)") &&
    appJs.includes("renderFillingPalletStatusList(document.pallets)") &&
    appJs.includes('"is-filled"') &&
    appJs.includes('"is-pending"') &&
    appJs.includes("filling-pallet-item--compact") &&
    appJs.includes("getFillingPalletItemLabel") &&
    appJs.includes("buildFillingPalletGroups") &&
    appJs.includes("filling-pallet-list--scroll-breathing"),
  "filling screen should show grouped compact color-coded pallet rows with scroll breathing room"
);
assert(
  !appJs.includes('isFilled ? "Наполнена" : "Не наполнена"'),
  "filling pallet list should not render textual fill status labels"
);
assert(
  appJs.includes('id="fillingScanInput" type="text"') &&
    appJs.includes('data-scan-allow="1"') &&
    appJs.includes('placeholder="HU-000001"') &&
    appJs.includes("tsd-scan-input-hidden") &&
    appJs.includes("filling-scan-input-hidden"),
  "filling scan input should stay in DOM for keyboard-wedge scanner text but be visually hidden"
);
const renderFillingScanBody = extractFunctionBody(appJs, "renderFillingScan");
assert(
  appJs.includes("buildFillingScanHeaderLine(work, summary)") &&
    appJs.includes('class="filling-scan-header"') &&
    appJs.includes("filling-card--scan"),
  "filling scan screen should use a compact single-line header"
);
assert(
  !renderFillingScanBody.includes("filling-scan-hint"),
  "filling scan screen should not render visible scanner hint text"
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
    appJs.includes('className = "overlay overlay--centered filling-preview-overlay"') &&
    !appJs.includes("Подтверждение наполнения"),
  "fill confirmation should open in a separate modal overlay without legacy title"
);
assert(
  appJs.includes('id="fillingOverlayConfirmBtn"') &&
    appJs.includes("Подтвердить"),
  "fill confirmation overlay should keep confirm button"
);
assert(
  appJs.includes('id="fillingOverlayCancelBtn"') &&
    appJs.includes(">Отмена</button>"),
  "fill confirmation overlay should keep cancel button"
);
assert(
  appJs.includes("filling-preview-overlay-shell--compact") &&
    appJs.includes("filling-preview-overlay-card--compact"),
  "fill confirmation overlay should use compact modal modifiers"
);

const fillPreviewOverlay = extractFunctionBody(appJs, "openFillingPreviewOverlay");
assert(
  !fillPreviewOverlay.includes("Подтверждение наполнения"),
  "fill confirmation overlay should not render legacy title"
);
assert(
  !fillPreviewOverlay.includes("overlay-close") &&
    !fillPreviewOverlay.includes(">Закрыть</button>"),
  "fill confirmation overlay should not render close button"
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

const wireFillingScan = extractFunctionBody(appJs, "wireFillingScan");
assert(
  !wireFillingScan.includes("preview.document"),
  "production scan should not promote preview.document into the main filling context"
);
assert(
  wireFillingScan.includes("loadFillingContext(scanOrderId)") &&
    wireFillingScan.includes("preview: preview"),
  "production scan should reload filling context by order and keep preview only for overlay"
);

const fillOverlay = extractFunctionBody(appJs, "openFillingPreviewOverlay");
assert(
  fillOverlay.includes("loadFillingContext(fillOrderId)") &&
    fillOverlay.includes("shouldRenderProductionFillCompletion(result, nextContext, null)") &&
    fillOverlay.includes("shouldRenderProductionFillCompletion(result, null, reloadError)") &&
  !fillOverlay.includes("document: nextDocument") &&
  !fillOverlay.includes("result.document"),
  "production fill should reload context for normal scans and render final success when reload reports completion"
);

const hooks = {};
const appEl = {
  innerHTML: "",
  querySelectorAll: function () {
    return [];
  },
};
const vmContext = {
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
    getElementById: function (id) {
      return id === "app" ? appEl : null;
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
};
vmContext.window.document = vmContext.document;
vmContext.window.localStorage = vmContext.localStorage;
vmContext.window.navigator = vmContext.navigator;
vm.createContext(vmContext);
vm.runInContext(appJs, vmContext, { filename: "app.js" });

const unavailableAfterFill = new Error("Заказ недоступен для наполнения.");
assert.strictEqual(
  hooks.isFillingContextUnavailableAfterSuccessfulFill(unavailableAfterFill),
  true,
  "completed order reload error should be recognized after successful fill"
);
assert.strictEqual(
  hooks.shouldRenderProductionFillCompletion(
    { ok: true, prd_auto_closed: true, closed_prd_doc_ref: "PRD-2026-000001" },
    null,
    unavailableAfterFill
  ),
  true,
  "final pallet success should render completion when context reload says order is unavailable"
);
const finalHtml = hooks.renderFillingCompletion(
  { workItem: { orderRef: "TSD-FILL-001", prdDocRef: "PRD-2026-000001" } },
  { ok: true, prd_auto_closed: true, closed_prd_doc_ref: "PRD-2026-000001" },
  unavailableAfterFill
);
assert.match(finalHtml, /Паллета наполнена\. Заказ выполнен\./);
assert.match(finalHtml, /К списку наполнения/);
assert.doesNotMatch(finalHtml, /недоступен для наполнения/i);

assert.strictEqual(
  hooks.shouldRenderProductionFillCompletion(
    { ok: true, prd_auto_closed: true },
    { document: { summary: { remainingPalletCount: 1 } } },
    null
  ),
  false,
  "non-final pallet fill should keep the filling screen active after context reload"
);
assert.strictEqual(
  hooks.buildProductionFillCompletionMessage({ ok: true, prdAutoClosed: true }, null),
  "Паллета наполнена. Выпуск закрыт.",
  "single PRD close without unavailable order should show PRD closed success"
);

const fillingScanHtml = hooks.renderFillingScan(
  {
    workItem: { orderRef: "104", prdDocRef: "PRD-2026-000001" },
    document: {
      summary: { filledPalletCount: 0, plannedPalletCount: 10 },
      pallets: [{ huCode: "HU-000001", itemName: "Товар", status: "PENDING" }],
    },
  },
  {}
);
assert.match(
  fillingScanHtml,
  /Наполнение · Заказ 104 · 0 \/ 10 паллет/,
  "filling scan screen should show compact header with order and pallet progress"
);
assert.doesNotMatch(
  fillingScanHtml,
  /Наполнено паллет:|Сканируйте HU \/ паллетный штрихкод/,
  "filling scan screen should not show legacy progress label or visible scanner hint"
);
assert.doesNotMatch(fillingScanHtml, /PRD:/i, "filling scan screen should not display PRD number");
assert.match(fillingScanHtml, /id="fillingScanInput"/, "filling scan input should remain in DOM");
assert.doesNotMatch(
  fillingScanHtml,
  /id="fillingScanInput"[^>]*disabled/i,
  "filling scan input should stay enabled for scanner focus"
);
assert.match(
  fillingScanHtml,
  /filling-scan-input-hidden/,
  "filling scan input should use visual-hide CSS class"
);
assert.match(
  fillingScanHtml,
  /filling-screen--scan/,
  "filling scan screen should use adaptive scan screen class"
);
assert.match(
  fillingScanHtml,
  /filling-pallet-list-card[\s\S]*filling-pallet-list--scroll-breathing/,
  "filling scan screen should keep the pallet list in a scrollable list card"
);
assert.doesNotMatch(
  fillingScanHtml,
  /route-transition-active|route-transition-exit/,
  "filling scan render should not require transition state before scanner input/list markup is available"
);
assert.strictEqual(
  hooks.buildFillingScanSummaryLine(
    { orderRef: "104" },
    { filledPalletCount: 0, plannedPalletCount: 10 }
  ),
  "Заказ 104 · 0 / 10 паллет"
);
assert.strictEqual(
  hooks.buildFillingScanHeaderLine(
    { orderRef: "007" },
    { filledPalletCount: 0, plannedPalletCount: 12 }
  ),
  "Наполнение · Заказ 007 · 0 / 12 паллет"
);

const fillingListItems = [
  {
    orderRef: "104",
    orderId: 104,
    prdDocRef: "PRD-2026-000290",
    orderTypeDisplay: "Производственный",
    summary: { plannedPalletCount: 10, filledPalletCount: 0, remainingQty: 0 },
  },
  {
    orderRef: "100",
    orderId: 100,
    prdDocRef: "PRD-2026-000100",
    orderTypeDisplay: "Производственный",
    summary: { plannedPalletCount: 8, filledPalletCount: 0, remainingQty: 0 },
  },
];
const fillingListHtml = hooks.renderFillingList(fillingListItems);
const fillingListOrder100Index = fillingListHtml.indexOf("Заказ № 100");
const fillingListOrder104Index = fillingListHtml.indexOf("Заказ № 104");
assert.notStrictEqual(fillingListOrder100Index, -1, "filling list should render order 100");
assert.notStrictEqual(fillingListOrder104Index, -1, "filling list should render order 104");
assert(
  fillingListOrder100Index < fillingListOrder104Index,
  "filling list should render lower order numbers before higher ones"
);
assert.doesNotMatch(fillingListHtml, /PRD:/i, "filling list cards should not display PRD line");
assert.doesNotMatch(
  fillingListHtml,
  /PRD-2026-000290|PRD-2026-000100/,
  "filling list should hide PRD refs while keeping them in source items"
);
assert.deepStrictEqual(
  hooks.sortFillingListItems(fillingListItems).map(function (item) {
    return item.orderRef;
  }),
  ["100", "104"],
  "filling list sort helper should order numeric refs ascending"
);

const fillingPalletFixtures = [
  {
    id: 1,
    huCode: "HU-0000753",
    itemName: "Горчица Печагин, 200 гр",
    status: "PENDING",
  },
  {
    id: 2,
    huCode: "HU-0000745",
    itemName: "Аджика Печагин, 200 гр",
    status: "PENDING",
  },
  {
    id: 3,
    huCode: "HU-0000742",
    itemName: "Аджика Печагин, 200 гр",
    status: "FILLED",
  },
  {
    id: 4,
    huCode: "HU-0000752",
    itemName: "Горчица Печагин, 200 гр",
    status: "PENDING",
  },
  {
    id: 5,
    huCode: "HU-0000999",
    status: "PENDING",
  },
];
const fillingPalletGroups = hooks.buildFillingPalletGroups(fillingPalletFixtures);
assert.strictEqual(fillingPalletGroups.length, 3, "filling pallet list should render three product groups");
assert.ok(
  fillingPalletGroups[0].label.indexOf("Аджика") === 0 &&
    fillingPalletGroups[1].label.indexOf("Горчица") === 0,
  "filling pallet groups should sort product titles ascending"
);
assert.strictEqual(
  fillingPalletGroups[2].label,
  hooks.getFillingPalletGroupLabel({ huCode: "HU-0000999" }),
  "filling pallet groups should keep unlabeled pallets in the last group"
);
assert.strictEqual(fillingPalletGroups[0].pallets.length, 2);
assert.strictEqual(fillingPalletGroups[0].pallets[0].huCode, "HU-0000742");
assert.strictEqual(fillingPalletGroups[0].pallets[1].huCode, "HU-0000745");
assert.strictEqual(fillingPalletGroups[1].pallets.length, 2);
assert.strictEqual(fillingPalletGroups[1].pallets[0].huCode, "HU-0000752");
assert.strictEqual(fillingPalletGroups[1].pallets[1].huCode, "HU-0000753");

const fillingPalletListHtml = hooks.renderFillingPalletStatusList(fillingPalletFixtures);
assert.match(fillingPalletListHtml, /filling-pallet-group-title/, "filling pallet list should render product group headers");
assert.match(fillingPalletListHtml, /Аджика Печагин, 200 гр/, "filling pallet list should render product title in group header");
assert.match(fillingPalletListHtml, /HU-0000742/, "filling pallet list should render HU code");
assert.doesNotMatch(
  fillingPalletListHtml,
  /filling-pallet-item-name/,
  "filling pallet list should not duplicate product title inside HU rows"
);
assert.doesNotMatch(
  fillingPalletListHtml,
  /Не наполнена|Наполнена/,
  "filling pallet list should rely on color classes instead of status text"
);
assert.match(
  fillingPalletListHtml,
  /is-filled[\s\S]*HU-0000742|HU-0000742[\s\S]*is-filled/,
  "filled pallet row should keep is-filled status class"
);
assert.match(
  fillingPalletListHtml,
  /is-pending[\s\S]*HU-0000745|HU-0000745[\s\S]*is-pending/,
  "pending pallet row should keep is-pending status class"
);
assert.match(
  fillingPalletListHtml,
  /filling-pallet-list--scroll-breathing/,
  "filling pallet list scroll container should include bottom breathing room class"
);
const adjikaTitleIndex = fillingPalletListHtml.indexOf("Аджика Печагин, 200 гр");
const mustardTitleIndex = fillingPalletListHtml.indexOf("Горчица Печагин, 200 гр");
const hu742Index = fillingPalletListHtml.indexOf("HU-0000742");
const hu745Index = fillingPalletListHtml.indexOf("HU-0000745");
const hu752Index = fillingPalletListHtml.indexOf("HU-0000752");
const hu753Index = fillingPalletListHtml.indexOf("HU-0000753");
assert(
  adjikaTitleIndex !== -1 &&
    mustardTitleIndex !== -1 &&
    adjikaTitleIndex < mustardTitleIndex &&
    hu742Index > adjikaTitleIndex &&
    hu745Index > hu742Index &&
    hu752Index > mustardTitleIndex &&
    hu753Index > hu752Index,
  "filling pallet list should render grouped and sorted product sections with ascending HU rows"
);
assert.strictEqual(
  hooks.getFillingPalletItemLabel({ itemName: "Товар" }),
  "Товар",
  "filling pallet label helper should read itemName"
);
assert.strictEqual(
  hooks.getFillingPalletGroupLabel({ huCode: "HU-1", status: "PENDING" }),
  "Без товара",
  "filling pallet group label helper should use fallback title when item name is missing"
);

const wireOutboundPickingOrder = extractFunctionBody(appJs, "wireOutboundPickingOrder");
assert(
  wireOutboundPickingOrder.includes("normalizeOutboundPickingOrderView(order)") &&
    storageJs.includes('pickOutboundField(row, "orderId", "order_id")'),
  "outbound scan wire should normalize order id from order_id when orderId is absent"
);
assert(
  appJs.includes('data-route="outbound"') &&
    appJs.includes("Отгрузка") &&
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
    serviceWorkerJs.includes('"./img/home/operations.png"') &&
      serviceWorkerJs.includes('"./img/home/info.png"'),
    "service worker should cache home menu png icons"
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
