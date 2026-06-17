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
  "filling screen should show grouped compact color-coded pallet rows with page breathing room"
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
const fillingHandleScannedValue = extractFunctionBody(appJs, "wireFillingScan");
assert(
  appJs.includes("function submitFillingScan(rawValue, context, options)") &&
    appJs.includes("function isProbablyCompleteHuScan(value, context)") &&
    fillingHandleScannedValue.includes("submitFillingScan(value, context") &&
    !fillingHandleScannedValue.includes('String(value || "").trim()'),
  "filling scan should submit through centralized submitFillingScan guard"
);
assert(
  appJs.includes("function isMixedFillingPallet(pallet)") &&
    appJs.includes("function renderFillingMixedPalletLines(pallet)") &&
    appJs.includes("function getFillingPalletGroupDisplayTitle(group)") &&
    fillingHandleScannedValue.includes("submitFillingScan(value, context"),
  "mixed pallet presentation should not change filling scan submit guard"
);
const handleDocKeydownBody = extractFunctionBody(scannerJs, "handleDocKeydown");
const handleDocInputBody = extractFunctionBody(scannerJs, "handleDocInput");
assert(
  handleDocKeydownBody.includes('emit(targetValue, { reason: "enter" })') &&
    handleDocKeydownBody.includes("if (isScanTarget)") &&
    handleDocKeydownBody.indexOf("if (isScanTarget)") <
      handleDocKeydownBody.indexOf("buffer += event.key"),
  "scan target Enter should emit full input value without keydown partial flush"
);
assert(
  handleDocInputBody.includes("if (isScanAllowedTarget(target))") &&
    handleDocInputBody.includes("return;") &&
    !handleDocInputBody.includes("scheduleFlush"),
  "scan-allowed input events should not schedule partial flush"
);
assert(
  scannerJs.includes("function unlockScanTarget(target)") &&
    scannerJs.includes('target.getAttribute("data-scan-readonly") !== "1"') &&
    scannerJs.includes("target.readOnly = false"),
  "keyboard scanner should temporarily unlock scan input when soft keyboard suppression made it readonly"
);
assert(
  scannerJs.includes("buffer += event.key") &&
    scannerJs.includes('flushBuffer("enter")'),
  "keyboard scanner should still buffer global key events for non-scan targets"
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
  storageJs.includes("normalizeProductionFillResult") &&
    storageJs.includes(".then(normalizeProductionFillResult)"),
  "fill APIs should normalize single and mixed fill responses through shared mapper"
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
  fillOverlay.includes("handleProductionFillSuccess(context, preview, result)") &&
    !fillOverlay.includes("loadFillingContext(fillOrderId)"),
  "production fill overlay should delegate post-fill flow to handleProductionFillSuccess"
);
assert(
  appJs.includes("function handleProductionFillSuccess(") &&
    extractFunctionBody(appJs, "handleProductionFillSuccess").includes("loadFillingContext(fillOrderId)") &&
    extractFunctionBody(appJs, "handleProductionFillSuccess").includes("shouldPromptOperationClose"),
  "post-fill handler should reload server progress before offering explicit finalize"
);
assert(appJs.includes("buildClosePromptStateKey") && appJs.includes("operationFingerprint"),
  "declined close prompt should be scoped to server fingerprint and progress state");
assert(
  !extractFunctionBody(appJs, "isProductionFillFinal").includes("isProductionFillPrdClosed"),
  "fill final detection must not treat PRD auto-close as order completion"
);
assert(
  storageJs.includes("orderCompleted") && storageJs.includes("remainingFillablePalletCount"),
  "fill result normalization should expose order-level completion fields when backend provides them"
);
const submitFillingScanBody = extractFunctionBody(appJs, "submitFillingScan");
assert(
  !submitFillingScanBody.includes("handleProductionFillSuccess") &&
    !submitFillingScanBody.includes("isProductionFillFinal"),
  "filling scan guard should stay independent from fill completion reload logic"
);
const fetchJsonWithTimeoutBody = extractFunctionBody(storageJs, "fetchJsonWithTimeout");
assert(
  fetchJsonWithTimeoutBody.includes("requestError.status = response.status") &&
    fetchJsonWithTimeoutBody.includes("requestError.code = code") &&
    fetchJsonWithTimeoutBody.includes("requestError.payload = payload") &&
    fetchJsonWithTimeoutBody.includes('new Error(message || code || "SERVER_ERROR")'),
  "TSD HTTP errors should preserve status, code, message and payload"
);
assert(
  fetchJsonWithTimeoutBody.includes('if (code === "BLOCK_DISABLED")'),
  "BLOCK_DISABLED notification should use the structured server error code"
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

const visibleFillingContext = {
  document: {
    pallets: [{ huCode: "HU-0001164" }, { huCode: "HU-0001165" }],
  },
};
assert.strictEqual(
  hooks.resolveFillingScannedHu("HU0001164", visibleFillingContext),
  "HU-0001164",
  "filling scan should map compact scanner text to visible pallet HU"
);
assert.strictEqual(
  hooks.resolveFillingScannedHu("0001164", visibleFillingContext),
  "HU-0001164",
  "filling scan should map digits-only scanner text to visible pallet HU"
);
assert.strictEqual(
  hooks.resolveFillingScannedHu("\u041DU-0001164", visibleFillingContext),
  "HU-0001164",
  "filling scan should normalize Cyrillic H lookalike before pallet lookup"
);
[
  "Паллета не найдена в плане выпуска",
  "Эта паллета относится к другому заказу",
  "Паллета отменена",
  "Паллета отменена и не может быть наполнена.",
  "Выпуск превышает остаток по строке заказа",
].forEach(function (message) {
  assert.strictEqual(
    hooks.mapFillingError({
      code: "FILL_REJECTED",
      message: "FILL_REJECTED",
      payload: { error: "FILL_REJECTED", message: message },
    }),
    message,
    "filling should keep the server rejection visible: " + message
  );
});

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
  hooks.isProductionFillFinal({
    ok: true,
    prdAutoClosed: true,
    closedPrdDocRef: "PRD-2026-000001",
  }),
  false,
  "PRD auto-close alone must not mark order filling as final"
);
assert.strictEqual(
  hooks.isProductionFillFinal({
    ok: true,
    closedPrdDocRef: "PRD-2026-000001",
  }),
  false,
  "closed PRD reference alone must not mark order filling as final"
);
assert.strictEqual(
  hooks.isProductionFillFinal({
    ok: true,
    prdAutoClosed: true,
    document: { summary: { remainingPalletCount: 0 } },
  }),
  false,
  "PRD-scoped zero remaining in fill document must not skip order context reload"
);
assert.strictEqual(
  hooks.isProductionFillFinal({
    ok: true,
    order_completed: true,
  }),
  true,
  "explicit order_completed from server should mark fill as final"
);
assert.strictEqual(
  hooks.isProductionFillFinal({
    ok: true,
    remaining_fillable_pallet_count: 0,
  }),
  true,
  "order-level remaining_fillable_pallet_count zero should mark fill as final"
);
assert.strictEqual(
  hooks.isProductionFillFinal({
    ok: true,
    effectiveStatus: "PARTIALLY_FILLED",
    document: { summary: { remainingPalletCount: 1 } },
  }),
  false,
  "partial mixed fill should not be treated as final while pallets remain"
);
assert.strictEqual(
  hooks.getRemainingPalletCountFromFillResult({
    document: { summary: { remainingPalletCount: 2 } },
  }),
  2,
  "fill result should expose remaining pallet count from normalized document summary"
);
assert.strictEqual(
  hooks.getRemainingFillablePalletCountFromFillResult({
    remainingFillablePalletCount: 1,
  }),
  1,
  "fill result should expose order-level remaining fillable pallet count when provided"
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
const fillingScanErrorHtml = hooks.renderFillingScan(
  {
    workItem: { orderRef: "104", prdDocRef: "PRD-2026-000001" },
    document: {
      summary: { filledPalletCount: 0, plannedPalletCount: 1 },
      pallets: [{ huCode: "HU-000001", itemName: "Товар", status: "PENDING" }],
    },
  },
  { message: "Эта паллета относится к другому заказу", messageType: "error" }
);
assert.match(
  fillingScanErrorHtml,
  /filling-message filling-message-error[\s\S]*Эта паллета относится к другому заказу/
);
assert(
  fillingHandleScannedValue.includes("return refreshContext({") &&
    fillingHandleScannedValue.includes("message: mapFillingError(error)") &&
    fillingHandleScannedValue.includes("focusScan()"),
  "rejected filling scan should keep the error through refresh and restore scanner focus"
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
  "filling scan screen should keep the pallet list as normal page content in the list card"
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
  "filling pallet list should include page-flow bottom breathing room class"
);
assert.doesNotMatch(
  fillingPalletListHtml,
  /filling-pallet-list--status/,
  "filling pallet status list should not use the failed pointer pass-through hotfix class"
);
assert.match(
  fillingPalletListHtml,
  /<li class="filling-pallet-item filling-pallet-item--compact (?:is-filled|is-pending)">/,
  "filling pallet rows should remain compact display-only list items"
);
assert.doesNotMatch(
  fillingPalletListHtml,
  /filling-mixed-component-line|filling-mixed-component-indicator/,
  "single pallet list should not render mixed component indicators"
);
const singlePalletRowHtml = hooks.renderFillingPalletHuRow({
  huCode: "HU-0001199",
  itemName: "Горчица, Печагин, 1 кг",
  status: "PENDING",
});
assert.match(singlePalletRowHtml, /HU-0001199/);
assert.doesNotMatch(
  singlePalletRowHtml,
  /filling-mixed-pallet-item|outbound-picking-hu-lines|filling-mixed-component-line/,
  "single pallet row HTML should stay compact without component list"
);

const mixedFillingPalletFixture = {
  huCode: "HU-0001201",
  isMixedPallet: true,
  status: "PLANNED",
  effectiveStatus: "PARTIALLY_FILLED",
  filledComponentCount: 1,
  totalComponentCount: 3,
  lines: [
    {
      itemName: "Горчица Печагин, 200 гр",
      plannedQty: 200,
      filledQty: 0,
      isCompleted: false,
      uom: "шт",
    },
    {
      itemName: "Хрен столовый, Печагин, 200 гр",
      plannedQty: 200,
      filledQty: 200,
      isCompleted: true,
      uom: "шт",
    },
    {
      itemName: "Аджика, Печагин, 200 гр",
      plannedQty: 200,
      filledQty: 0,
      isCompleted: false,
      uom: "шт",
    },
  ],
};
const partialMixedPalletHtml = hooks.renderFillingPalletStatusList([mixedFillingPalletFixture]);
assert.match(partialMixedPalletHtml, /Микс-паллета · 1 \/ 3/);
assert.match(partialMixedPalletHtml, /HU-0001201/);
assert.match(partialMixedPalletHtml, /Горчица Печагин, 200 гр/);
assert.match(partialMixedPalletHtml, /Хрен столовый, Печагин, 200 гр/);
assert.match(partialMixedPalletHtml, /Аджика, Печагин, 200 гр/);
assert.match(partialMixedPalletHtml, /0 шт \/ 200 шт/);
assert.match(partialMixedPalletHtml, /200 шт \/ 200 шт/);
assert.match(
  partialMixedPalletHtml,
  /filling-mixed-component-line is-completed[\s\S]*Хрен столовый, Печагин, 200 гр/,
  "completed mixed component should render checked indicator"
);
assert.match(
  partialMixedPalletHtml,
  /filling-mixed-component-line is-pending[\s\S]*Горчица Печагин, 200 гр/,
  "incomplete mixed component should render waiting indicator"
);

const fullMixedFillingPalletFixture = {
  huCode: "HU-0001201",
  isMixedPallet: true,
  status: "FILLED",
  effectiveStatus: "FILLED",
  filledComponentCount: 3,
  totalComponentCount: 3,
  lines: mixedFillingPalletFixture.lines.map(function (line) {
    return {
      itemName: line.itemName,
      plannedQty: line.plannedQty,
      filledQty: line.plannedQty,
      isCompleted: true,
      uom: line.uom,
    };
  }),
};
const fullMixedPalletHtml = hooks.renderFillingPalletStatusList([fullMixedFillingPalletFixture]);
assert.match(fullMixedPalletHtml, /Микс-паллета · 3 \/ 3/);
assert.match(fullMixedPalletHtml, /filling-mixed-component-line is-completed/);
assert.doesNotMatch(
  fullMixedPalletHtml,
  /filling-mixed-component-line is-pending/,
  "fully filled mixed pallet should not render waiting component lines"
);

const partiallyFilledPalletHtml = hooks.renderFillingPalletStatusList([
  {
    id: 6,
    huCode: "HU-0000870",
    itemName: "Микс-паллета",
    status: "PLANNED",
    effectiveStatus: "PARTIALLY_FILLED",
    filledComponentCount: 1,
    totalComponentCount: 3,
    canFill: true,
  },
]);
assert.strictEqual(
  hooks.isFillingPalletCompleted({
    status: "PLANNED",
    effectiveStatus: "PARTIALLY_FILLED",
    canFill: true,
  }),
  false,
  "partially filled mixed HU should remain available instead of being treated as filled"
);
assert.match(
  partiallyFilledPalletHtml,
  /is-pending[\s\S]*HU-0000870|HU-0000870[\s\S]*is-pending/,
  "partially filled mixed HU should render as pending and available"
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
  appJs.includes("TsdStorage.apiCompleteOutboundPicking(orderId, allowPartial)") &&
    storageJs.includes("allow_partial") &&
    appJs.includes("Закрыть частичную отгрузку?"),
  "outbound picking complete should support explicit partial close confirmation"
);
assert(
  appJs.includes("Микс-паллета") &&
    appJs.includes("preview.lines") &&
    appJs.includes("filling-preview-composition") &&
    appJs.includes("filling-component-checkbox") &&
    appJs.includes("TsdStorage.apiFillMixedProductionPalletComponents") &&
    storageJs.includes("/api/tsd/production/fill-mixed-pallet-components"),
  "mixed pallet preview should select and submit component lines"
);
assert(
  appJs.includes("updateMixedConfirmState") && appJs.includes("result.message"),
  "mixed component fill should require a selection and show partial-save message"
);
const partialMixedPreviewHtml = hooks.buildFillingPreviewHtml(
  {
    isMixedPallet: true,
    orderRef: "103",
    huCode: "HU-0000870",
    palletIndex: 1,
    palletCount: 1,
    effectiveStatus: "PARTIALLY_FILLED",
    canFill: true,
    filledComponentCount: 1,
    totalComponentCount: 3,
    lines: [
      { componentLineId: 1, itemName: "Хрен 200 гр", plannedQty: 200, uom: "шт", isCompleted: true },
      { componentLineId: 2, itemName: "Аджика 200 гр", plannedQty: 200, uom: "шт", isCompleted: false },
      { componentLineId: 3, itemName: "Горчица 200 гр", plannedQty: 200, uom: "шт", isCompleted: false },
    ],
  },
  { orderRef: "103" }
);
assert.match(
  partialMixedPreviewHtml,
  /value="1" checked disabled/,
  "completed mixed component should be checked and disabled"
);
assert.match(
  partialMixedPreviewHtml,
  /value="2" \/>/,
  "remaining mixed component should stay unchecked and enabled"
);
assert.match(partialMixedPreviewHtml, /Хрен 200 гр — 200 шт · наполнено/);
assert.strictEqual(
  hooks.shouldRenderProductionFillCompletion(
    { ok: true, effective_status: "PARTIALLY_FILLED" },
    { document: { summary: { remainingPalletCount: 1 } } },
    null
  ),
  false,
  "order should not be marked done while mixed HU has incomplete components"
);
assert.strictEqual(
  hooks.shouldRenderProductionFillCompletion(
    { ok: true, effective_status: "FILLED" },
    { document: { summary: { remainingPalletCount: 0 } } },
    null
  ),
  true,
  "order may be marked done after final component when no pallets remain"
);
assert(appVersionJs.includes('var version = "47"'), "TSD shell version should be bumped for HU scan wedge fix");
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
      serviceWorkerJs.includes('"./img/home/hu-search.png"'),
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

function createFillSuccessHarness(contextHandler) {
  const localHooks = {};
  const localAppEl = {
    innerHTML: "",
    querySelectorAll: function () {
      return [];
    },
  };
  let fillingContextCalls = 0;
  const localVmContext = {
    console,
    TsdStorage: {
      apiGetProductionFillingContext: function (orderId) {
        fillingContextCalls += 1;
        return contextHandler(orderId, fillingContextCalls);
      },
    },
    window: {
      FlowStockTsdTestHooks: localHooks,
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
        if (id === "app") {
          return localAppEl;
        }
        if (id === "fillingCompletionListBtn") {
          return { addEventListener: function () {} };
        }
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
  };
  localVmContext.window.document = localVmContext.document;
  localVmContext.window.localStorage = localVmContext.localStorage;
  localVmContext.window.navigator = localVmContext.navigator;
  vm.createContext(localVmContext);
  vm.runInContext(appJs, localVmContext, { filename: "app.js" });
  return {
    hooks: localHooks,
    appEl: localAppEl,
    get fillingContextCalls() {
      return fillingContextCalls;
    },
    resetCalls: function () {
      fillingContextCalls = 0;
    },
  };
}

async function runFillSuccessRuntimeTests() {
  const fillContext = {
    workItem: { orderId: 120, orderRef: "120" },
    document: {
      summary: { remainingPalletCount: 1, filledPalletCount: 1, plannedPalletCount: 2 },
      pallets: [{ huCode: "HU-0001204", status: "PENDING" }],
    },
  };
  const preview = { orderId: 120, prdDocId: 55, huCode: "HU-0001203" };

  const prdClosedHarness = createFillSuccessHarness(function () {
    return Promise.resolve({
      orderId: 120,
      orderRef: "120",
      prdDocId: 56,
      document: {
        summary: { remainingPalletCount: 2, filledPalletCount: 1, plannedPalletCount: 3 },
        pallets: [
          { huCode: "HU-0001204", status: "PENDING" },
          { huCode: "HU-0001205", status: "PENDING" },
        ],
      },
    });
  });
  prdClosedHarness.resetCalls();
  await prdClosedHarness.hooks.handleProductionFillSuccess(fillContext, preview, {
    ok: true,
    prdAutoClosed: true,
    closedPrdDocRef: "PRD-2026-000001",
    document: { summary: { remainingPalletCount: 0 } },
  });
  assert.strictEqual(
    prdClosedHarness.fillingContextCalls,
    1,
    "PRD auto-close with remaining order pallets should reload filling context"
  );
  assert.match(prdClosedHarness.appEl.innerHTML, /id="fillingScanInput"/);
  assert.match(prdClosedHarness.appEl.innerHTML, /PRD PRD-2026-000001 закрыт\./);
  assert.doesNotMatch(prdClosedHarness.appEl.innerHTML, /filling-completion-card/);

  const orderCompletedHarness = createFillSuccessHarness(function () {
    return Promise.reject(new Error("Заказ недоступен для наполнения."));
  });
  orderCompletedHarness.resetCalls();
  await orderCompletedHarness.hooks.handleProductionFillSuccess(fillContext, preview, {
    ok: true,
    orderCompleted: true,
    prdAutoClosed: true,
    closedPrdDocRef: "PRD-2026-000001",
  });
  assert.strictEqual(
    orderCompletedHarness.fillingContextCalls,
    1,
    "explicit order_completed should still reload server progress before finalize UX"
  );
  assert.match(orderCompletedHarness.appEl.innerHTML, /Паллета наполнена\. Заказ выполнен\./);

  const partialHarness = createFillSuccessHarness(function () {
    return Promise.resolve({
      orderId: 120,
      orderRef: "120",
      prdDocId: 55,
      document: {
        summary: { remainingPalletCount: 1, filledPalletCount: 1, plannedPalletCount: 2 },
        pallets: [{ huCode: "HU-0001204", status: "PENDING" }],
      },
    });
  });
  partialHarness.resetCalls();
  await partialHarness.hooks.handleProductionFillSuccess(fillContext, preview, {
    ok: true,
    effectiveStatus: "PARTIALLY_FILLED",
    document: { summary: { remainingPalletCount: 1 } },
  });
  assert.strictEqual(
    partialHarness.fillingContextCalls,
    1,
    "partial mixed fill should reload filling context once"
  );
  assert.match(partialHarness.appEl.innerHTML, /id="fillingScanInput"/);

  const reloadFallbackHarness = createFillSuccessHarness(function () {
    return Promise.reject(new Error("Выпуск по заказу уже завершён. Нет паллет к наполнению."));
  });
  reloadFallbackHarness.resetCalls();
  await reloadFallbackHarness.hooks.handleProductionFillSuccess(fillContext, preview, {
    ok: true,
    effectiveStatus: "PARTIALLY_FILLED",
  });
  assert.strictEqual(
    reloadFallbackHarness.fillingContextCalls,
    1,
    "non-final fill without summary should still attempt one context reload"
  );
  assert.match(
    reloadFallbackHarness.appEl.innerHTML,
    /Паллета наполнена\. Заказ выполнен\./,
    "context reload no-pallets after fill should render completion instead of throwing"
  );
}

async function runFillingScanGuardTests() {
  const fillingContext1203 = {
    workItem: { orderId: 120, prdDocId: 55 },
    document: {
      pallets: [{ huCode: "HU-0001203" }],
    },
  };

  function createScanRecorder() {
    const calls = [];
    return {
      calls: calls,
      executeScan: function (huCode) {
        calls.push(huCode);
        return Promise.resolve({ ok: true, huCode: huCode });
      },
    };
  }

  hooks.resetFillingScanGuards();
  const partialRecorder = createScanRecorder();
  const partialFragments = ["H", "HU", "HU-", "001203"];
  for (const fragment of partialFragments) {
    assert.strictEqual(
      hooks.isProbablyCompleteHuScan(fragment, fillingContext1203),
      false,
      "partial fragment should be rejected: " + fragment
    );
    const outcome = await hooks.submitFillingScan(fragment, fillingContext1203, {
      executeScan: partialRecorder.executeScan,
    });
    assert.strictEqual(outcome.accepted, false, "partial fragment submit should be ignored: " + fragment);
  }
  assert.strictEqual(partialRecorder.calls.length, 0, "partial fragments must not call scan API");

  hooks.resetFillingScanGuards();
  const fullRecorder = createScanRecorder();
  const fullOutcome = await hooks.submitFillingScan("HU-0001203", fillingContext1203, {
    executeScan: fullRecorder.executeScan,
  });
  assert.strictEqual(fullOutcome.accepted, true);
  assert.strictEqual(fullRecorder.calls.length, 1);
  assert.strictEqual(fullRecorder.calls[0], "HU-0001203");

  hooks.resetFillingScanGuards();
  const compactRecorder = createScanRecorder();
  await hooks.submitFillingScan("HU0001203", fillingContext1203, {
    executeScan: compactRecorder.executeScan,
  });
  assert.strictEqual(compactRecorder.calls.length, 1);
  assert.strictEqual(compactRecorder.calls[0], "HU-0001203");

  hooks.resetFillingScanGuards();
  const digitsRecorder = createScanRecorder();
  await hooks.submitFillingScan("0001203", fillingContext1203, {
    executeScan: digitsRecorder.executeScan,
  });
  assert.strictEqual(digitsRecorder.calls.length, 1);
  assert.strictEqual(digitsRecorder.calls[0], "HU-0001203");

  hooks.resetFillingScanGuards();
  const duplicateRecorder = createScanRecorder();
  await hooks.submitFillingScan("HU-0001203", fillingContext1203, {
    executeScan: duplicateRecorder.executeScan,
  });
  await hooks.submitFillingScan("HU-0001203", fillingContext1203, {
    executeScan: duplicateRecorder.executeScan,
  });
  assert.strictEqual(duplicateRecorder.calls.length, 1, "duplicate HU within dedup window should scan once");

  hooks.resetFillingScanGuards();
  const rerenderRecorder = createScanRecorder();
  let releaseInFlightScan;
  const inFlightScanPromise = hooks.submitFillingScan("HU-0001203", fillingContext1203, {
    executeScan: function (huCode) {
      rerenderRecorder.calls.push(huCode);
      return new Promise(function (resolve) {
        releaseInFlightScan = resolve;
      });
    },
  });
  await Promise.resolve();
  const rerenderDuplicate = await hooks.submitFillingScan("HU-0001203", fillingContext1203, {
    executeScan: rerenderRecorder.executeScan,
  });
  assert.strictEqual(rerenderDuplicate.accepted, false);
  assert.strictEqual(rerenderRecorder.calls.length, 1, "rerender duplicate should stay blocked while scan is in flight");
  releaseInFlightScan({ ok: true });
  await inFlightScanPromise;

  hooks.resetFillingScanGuards();
  const dualSourceRecorder = createScanRecorder();
  const dualExecute = dualSourceRecorder.executeScan;
  await Promise.all([
    hooks.submitFillingScan("HU-0001203", fillingContext1203, { executeScan: dualExecute }),
    hooks.submitFillingScan("HU0001203", fillingContext1203, { executeScan: dualExecute }),
  ]);
  assert.strictEqual(dualSourceRecorder.calls.length, 1, "Enter and scanner sources should dedupe to one API call");
}

runFillSuccessRuntimeTests()
  .then(runFillingScanGuardTests)
  .then(function () {
    console.log("TSD filling presentation tests passed.");
  })
  .catch(function (error) {
    console.error(error);
    process.exit(1);
  });
