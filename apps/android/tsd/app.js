(function () {
  "use strict";

  var app = document.getElementById("app");
  var appHeader = document.querySelector(".app-header");
  var appTitle = document.querySelector(".app-title");
  var appTitleWrap = document.querySelector(".app-title-wrap");
  var backBtn = document.getElementById("backBtn");
  var settingsBtn = document.getElementById("settingsBtn");
  var networkStatus = document.getElementById("networkStatus");
  var scanSink = document.getElementById("scanSink");
  var currentRoute = null;
  var scanKeydownHandler = null;
  var scanInputHandler = null;
  var scanInputKeydownHandler = null;
  var scannerManager = null;
  var pendingScanHandler = null;
  var activeScanHandler = null;
  var scanHandlerActive = false;
  var scanPreferredTarget = null;
  var scannerMode = "auto";
  var SCANNER_MODE_KEY = "scannerMode";
  var SCAN_DEBUG_OPEN_KEY = "scanDebugOpen";
  var softKeyboardEnabled = false;
  var SOFT_KEYBOARD_KEY = "softKeyboardEnabled";
  var SERVER_PING_INTERVAL = 15000;
  var serverStatus = { ok: null, checkedAt: 0 };
  var scanDebug = {
    enabled: false,
    log: [],
    maxEntries: 200,
    logEl: null,
    stateEl: null,
    handlersAttached: false,
    keydownHandler: null,
    inputHandler: null,
    focusHandler: null,
    blurHandler: null,
  };

  var STATUS_ORDER = {
    DRAFT: 0,
    RECOUNT: 1,
    READY: 2,
    CLOSED: 3,
    EXPORTED: 4,
  };

  var NAV_ORIGIN_KEY = "tsdNavOrigin";
  var CLIENT_BLOCK_CONTEXT_KEY = "flowstock_block_context";
  var CLIENT_BLOCK_HEADER = "X-FlowStock-Block-Key";
  var clientBlocks = getDefaultClientBlocks();
  var clientBlocksLoadPromise = null;
  var currentClientBlockContext = "";

  function getDefaultClientBlocks() {
    return {
      tsd_operations: true,
      tsd_stock: true,
      tsd_catalog: true,
      tsd_orders: true,
      tsd_inbound: true,
      tsd_production_receipt: true,
      tsd_outbound: true,
      tsd_move: true,
      tsd_write_off: true,
      tsd_inventory: true,
    };
  }

  function applyClientBlocks(raw) {
    var next = getDefaultClientBlocks();
    if (raw && typeof raw === "object") {
      Object.keys(next).forEach(function (key) {
        if (raw[key] === false) {
          next[key] = false;
        }
      });
    }
    clientBlocks = next;
    return clientBlocks;
  }

  function fetchClientBlocks() {
    return fetch("/api/client-blocks", { method: "GET", cache: "no-store" })
      .then(function (response) {
        return response
          .json()
          .catch(function () {
            return null;
          })
          .then(function (payload) {
            if (!response.ok) {
              throw new Error("CLIENT_BLOCKS_ERROR");
            }
            return applyClientBlocks(payload && payload.blocks);
          });
      })
      .catch(function () {
        return applyClientBlocks(null);
      });
  }

  function ensureClientBlocksLoaded(forceRefresh) {
    if (forceRefresh || !clientBlocksLoadPromise) {
      clientBlocksLoadPromise = fetchClientBlocks();
    }
    return clientBlocksLoadPromise;
  }

  function getOperationBlockKey(op) {
    var normalized = String(op || "").trim().toUpperCase();
    return OP_BLOCK_KEYS[normalized] || "";
  }

  function resolveRouteBlockContext(route) {
    if (!route) {
      return "";
    }

    if (route.name === "operations") {
      return "tsd_operations";
    }
    if (route.name === "docs" || route.name === "new") {
      return getOperationBlockKey(route.op) || "tsd_operations";
    }
    if (route.name === "stock") {
      return "tsd_stock";
    }
    if (route.name === "items") {
      return "tsd_catalog";
    }
    if (route.name === "orders" || route.name === "order") {
      return "tsd_orders";
    }
    if (route.name === "doc") {
      return currentClientBlockContext || "";
    }
    return "";
  }

  function setCurrentClientBlockContext(value) {
    currentClientBlockContext = String(value || "").trim();
    try {
      if (currentClientBlockContext) {
        sessionStorage.setItem(CLIENT_BLOCK_CONTEXT_KEY, currentClientBlockContext);
      } else {
        sessionStorage.removeItem(CLIENT_BLOCK_CONTEXT_KEY);
      }
    } catch (error) {
      // ignore storage failures
    }
  }

  function createBlockHeaders(source, blockKey) {
    var headers = new Headers(source || {});
    var normalized = String(blockKey || "").trim();
    if (normalized && !headers.has(CLIENT_BLOCK_HEADER)) {
      headers.set(CLIENT_BLOCK_HEADER, normalized);
    }
    return headers;
  }

  function normalizeScannerMode(value) {
    var mode = String(value || "").toLowerCase();
    if (mode === "keyboard" || mode === "intent" || mode === "auto") {
      return mode;
    }
    return "auto";
  }

  function normalizeSoftKeyboardSetting(value) {
    if (typeof value === "boolean") {
      return value;
    }
    if (value === "false" || value === "0") {
      return false;
    }
    return true;
  }

  function isDebugMode() {
    try {
      if (window.location && /[?&]debug=1/.test(window.location.search || "")) {
        return true;
      }
      if (window.localStorage && localStorage.getItem("flowstock_debug") === "1") {
        return true;
      }
    } catch (error) {
      return false;
    }
    return false;
  }

  function formatDebugTarget(target) {
    if (!target) {
      return "-";
    }
    var tag = target.tagName ? target.tagName.toLowerCase() : "node";
    var id = target.id ? "#" + target.id : "";
    var cls = "";
    if (target.classList && target.classList.length) {
      cls = "." + Array.prototype.join.call(target.classList, ".");
    }
    var scanAllowed =
      typeof target.getAttribute === "function" && target.getAttribute("data-scan-allow") === "1"
        ? " scan"
        : "";
    return tag + id + cls + scanAllowed;
  }

  function getScanBlockReason() {
    if (!scanHandlerActive) {
      return "handler-off";
    }
    if (isManualOverlayOpen()) {
      return "modal-open";
    }
    if (currentRoute && currentRoute.name === "login") {
      return "login";
    }
    var active = document.activeElement;
    if (!active) {
      return "";
    }
    if (scanSink && active === scanSink) {
      return "";
    }
    if (isScanAllowedElement(active)) {
      return "";
    }
    if (active.isContentEditable) {
      return "content-editable";
    }
    var tag = active.tagName;
    if (!tag) {
      return "";
    }
    tag = tag.toUpperCase();
    if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") {
      return "focus-input";
    }
    return "";
  }

  function getScanStateSnapshot() {
    var active = document.activeElement;
    var preferred = getPreferredScanTarget();
    var provider =
      scannerManager && scannerManager.getProviderType
        ? scannerManager.getProviderType()
        : "keyboard";
    return {
      route: currentRoute ? currentRoute.name : "-",
      handlerActive: scanHandlerActive,
      canScan: canScanNow(),
      blockReason: getScanBlockReason(),
      activeElement: formatDebugTarget(active),
      preferredTarget: formatDebugTarget(preferred),
      scanSinkFocused: !!(scanSink && active === scanSink),
      scannerMode: scannerMode,
      softKeyboardEnabled: softKeyboardEnabled,
      provider: provider,
      overlayOpen: isManualOverlayOpen(),
    };
  }

  function formatScanState(state) {
    if (!state) {
      return "";
    }
    var lines = [
      "route: " + state.route,
      "handler: " + (state.handlerActive ? "on" : "off"),
      "canScan: " + (state.canScan ? "yes" : "no") + (state.blockReason ? " (" + state.blockReason + ")" : ""),
      "active: " + state.activeElement,
      "preferred: " + state.preferredTarget,
      "scanSink: " + (state.scanSinkFocused ? "focused" : "no"),
      "provider: " + state.provider + " (" + state.scannerMode + ")",
      "soft keyboard: " + (state.softKeyboardEnabled ? "on" : "off"),
      "overlay: " + (state.overlayOpen ? "open" : "none"),
    ];
    return lines.join("\n");
  }

  function appendScanDebug(label, message) {
    if (!scanDebug.enabled) {
      return;
    }
    var stamp = new Date().toISOString();
    var text = stamp + " " + label + (message ? " " + message : "");
    scanDebug.log.unshift(text);
    if (scanDebug.log.length > scanDebug.maxEntries) {
      scanDebug.log.length = scanDebug.maxEntries;
    }
    if (scanDebug.logEl) {
      scanDebug.logEl.textContent = scanDebug.log.join("\n");
    }
    if (scanDebug.stateEl) {
      scanDebug.stateEl.textContent = formatScanState(getScanStateSnapshot());
    }
  }

  function clearScanDebugLog() {
    scanDebug.log = [];
    if (scanDebug.logEl) {
      scanDebug.logEl.textContent = "";
    }
  }

  function attachScanDebugHandlers() {
    if (scanDebug.handlersAttached) {
      return;
    }
    scanDebug.keydownHandler = function (event) {
      if (!scanDebug.enabled) {
        return;
      }
      var details =
        'key="' +
        String(event.key) +
        '" code=' +
        String(event.code || "") +
        " which=" +
        String(event.which || 0) +
        " target=" +
        formatDebugTarget(event.target) +
        " alt=" +
        (event.altKey ? "1" : "0") +
        " ctrl=" +
        (event.ctrlKey ? "1" : "0") +
        " shift=" +
        (event.shiftKey ? "1" : "0");
      appendScanDebug("keydown", details);
    };
    scanDebug.inputHandler = function (event) {
      if (!scanDebug.enabled) {
        return;
      }
      var target = event.target;
      var value = "";
      if (target && typeof target.value === "string") {
        value = target.value;
      }
      if (value && value.length > 120) {
        value = value.slice(0, 120) + "...";
      }
      appendScanDebug("input", 'target=' + formatDebugTarget(target) + ' value="' + value + '"');
    };
    scanDebug.focusHandler = function (event) {
      if (!scanDebug.enabled) {
        return;
      }
      appendScanDebug("focusin", formatDebugTarget(event.target));
    };
    scanDebug.blurHandler = function (event) {
      if (!scanDebug.enabled) {
        return;
      }
      appendScanDebug("focusout", formatDebugTarget(event.target));
    };

    document.addEventListener("keydown", scanDebug.keydownHandler, true);
    document.addEventListener("input", scanDebug.inputHandler, true);
    document.addEventListener("focusin", scanDebug.focusHandler, true);
    document.addEventListener("focusout", scanDebug.blurHandler, true);
    scanDebug.handlersAttached = true;
  }

  function detachScanDebugHandlers() {
    if (!scanDebug.handlersAttached) {
      return;
    }
    document.removeEventListener("keydown", scanDebug.keydownHandler, true);
    document.removeEventListener("input", scanDebug.inputHandler, true);
    document.removeEventListener("focusin", scanDebug.focusHandler, true);
    document.removeEventListener("focusout", scanDebug.blurHandler, true);
    scanDebug.keydownHandler = null;
    scanDebug.inputHandler = null;
    scanDebug.focusHandler = null;
    scanDebug.blurHandler = null;
    scanDebug.handlersAttached = false;
  }

  function setScanDebugEnabled(enabled) {
    scanDebug.enabled = !!enabled;
    if (scanDebug.enabled) {
      attachScanDebugHandlers();
      appendScanDebug("debug", "enabled");
    } else {
      detachScanDebugHandlers();
    }
  }

  function mountScanDebugUI(logEl, stateEl) {
    scanDebug.logEl = logEl || null;
    scanDebug.stateEl = stateEl || null;
    if (scanDebug.logEl) {
      scanDebug.logEl.textContent = scanDebug.log.join("\n");
    }
    if (scanDebug.stateEl) {
      scanDebug.stateEl.textContent = formatScanState(getScanStateSnapshot());
    }
  }

  function updateNetworkStatus() {
    if (!networkStatus) {
      return;
    }

    var online = serverStatus.ok;
    networkStatus.textContent = online ? "Server: OK" : "Server: OFF";
    networkStatus.classList.toggle("is-offline", !online);
  }

  function isSoftKeyboardSuppressed() {
    if (softKeyboardEnabled) {
      return false;
    }
    if (currentRoute && currentRoute.name === "login") {
      return false;
    }
    return true;
  }

  function hideVirtualKeyboard() {
    if (!navigator || !navigator.virtualKeyboard || !navigator.virtualKeyboard.hide) {
      return;
    }
    try {
      navigator.virtualKeyboard.hide();
    } catch (error) {
      // ignore
    }
  }

  function rememberInputMode(target) {
    if (!target || typeof target.getAttribute !== "function") {
      return;
    }
    if (target.getAttribute("data-orig-inputmode") != null) {
      return;
    }
    var mode = target.getAttribute("inputmode");
    target.setAttribute("data-orig-inputmode", mode != null ? mode : "");
  }

  function applyScanInputMode(target) {
    if (!target || typeof target.setAttribute !== "function") {
      return;
    }
    rememberInputMode(target);
    target.setAttribute("inputmode", "text");
  }

  function applyInputMode(target, suppress) {
    if (!target || typeof target.setAttribute !== "function") {
      return;
    }
    rememberInputMode(target);
    if (suppress) {
      target.setAttribute("inputmode", "none");
      return;
    }
    var original = target.getAttribute("data-orig-inputmode");
    if (original) {
      target.setAttribute("inputmode", original);
    } else {
      target.removeAttribute("inputmode");
    }
  }

  function shouldLockKeyboardForInput(input) {
    if (!input || !input.tagName) {
      return false;
    }
    var tag = input.tagName.toUpperCase();
    if (tag === "TEXTAREA") {
      return true;
    }
    if (tag !== "INPUT") {
      return false;
    }
    var type = (input.type || "").toLowerCase();
    if (
      type === "checkbox" ||
      type === "radio" ||
      type === "button" ||
      type === "submit" ||
      type === "range" ||
      type === "color"
    ) {
      return false;
    }
    return true;
  }

  function applySoftKeyboardSetting(root) {
    var suppress = isSoftKeyboardSuppressed();
    var scope = root || document;
    var inputs = scope.querySelectorAll("input, textarea, select");
    inputs.forEach(function (input) {
      var scanAllowed = isScanAllowedElement(input);
      if (suppress) {
        if (scanAllowed) {
          applyScanInputMode(input);
          input.setAttribute("data-scan-readonly", "1");
        } else if (shouldLockKeyboardForInput(input)) {
          input.setAttribute("data-kbd-readonly", "1");
        }
        applyInputMode(input, !scanAllowed);
        if (shouldLockKeyboardForInput(input)) {
          input.readOnly = true;
        }
        return;
      }
      if (input.hasAttribute("data-scan-readonly")) {
        input.removeAttribute("data-scan-readonly");
      }
      if (input.hasAttribute("data-kbd-readonly")) {
        input.removeAttribute("data-kbd-readonly");
      }
      if (shouldLockKeyboardForInput(input)) {
        input.readOnly = false;
      }
      applyInputMode(input, false);
    });
    if (suppress) {
      hideVirtualKeyboard();
    }
  }

  function setSoftKeyboardEnabled(enabled) {
    softKeyboardEnabled = !!enabled;
    applySoftKeyboardSetting(document);
  }

  function normalizeBaseUrl(value) {
    var url = String(value || "").trim();
    if (!url) {
      return "";
    }
    return url.replace(/\/+$/, "");
  }

  function getServerBaseUrl() {
    return TsdStorage.getBaseUrl()
      .then(function (value) {
        var normalized = normalizeBaseUrl(value);
        if (normalized) {
          return normalized;
        }
        if (window.location && String(window.location.origin || "").indexOf("http") === 0) {
          return normalizeBaseUrl(window.location.origin);
        }
        return "https://localhost:7154";
      })
      .catch(function () {
        if (window.location && String(window.location.origin || "").indexOf("http") === 0) {
          return normalizeBaseUrl(window.location.origin);
        }
        return "https://localhost:7154";
      });
  }

  function pingServer(force) {
    var now = Date.now();
    if (!force && serverStatus.checkedAt && now - serverStatus.checkedAt < SERVER_PING_INTERVAL) {
      updateNetworkStatus();
      return Promise.resolve(!!serverStatus.ok);
    }

    return getServerBaseUrl()
      .then(function (baseUrl) {
        return fetch(baseUrl + "/api/ping", { method: "GET", cache: "no-store" })
          .then(function (response) {
            serverStatus.ok = response.ok;
            serverStatus.checkedAt = Date.now();
            updateNetworkStatus();
            return response.ok;
          })
          .catch(function () {
            serverStatus.ok = false;
            serverStatus.checkedAt = Date.now();
            updateNetworkStatus();
            return false;
          });
      })
      .catch(function () {
        serverStatus.ok = false;
        serverStatus.checkedAt = Date.now();
        updateNetworkStatus();
        return false;
      });
  }

  function ensureServerAvailable() {
    return pingServer(true).then(function (ok) {
      if (!ok) {
        alert("Нет связи с сервером FlowStock. Проверьте Wi-Fi.");
        throw new Error("server_unavailable");
      }
      return true;
    });
  }

  function setNavOrigin(value) {
    if (!window.sessionStorage) {
      return;
    }
    try {
      sessionStorage.setItem(NAV_ORIGIN_KEY, value);
    } catch (error) {
      // ignore
    }
  }

  function getNavOrigin() {
    if (!window.sessionStorage) {
      return null;
    }
    try {
      return sessionStorage.getItem(NAV_ORIGIN_KEY);
    } catch (error) {
      return null;
    }
  }

  function setScanHighlight(active) {
    var scanCard = document.querySelector(".scan-card");
    if (scanCard) {
      scanCard.classList.toggle("scan-active", !!active);
    }
  }

  function isManualOverlayOpen() {
    var overlays = document.querySelectorAll(".overlay");
    if (!overlays.length) {
      return false;
    }
    for (var i = 0; i < overlays.length; i += 1) {
      if (overlays[i].getAttribute("data-scan-allow") === "1") {
        continue;
      }
      return true;
    }
    return false;
  }

  function isScanAllowedElement(el) {
    if (!el || typeof el.getAttribute !== "function") {
      return false;
    }
    if (el.getAttribute("data-scan-allow") === "1") {
      return true;
    }
    return false;
  }

  function canScanNow() {
    if (!scanHandlerActive) {
      return false;
    }
    if (isManualOverlayOpen()) {
      return false;
    }
    if (currentRoute && currentRoute.name === "login") {
      return false;
    }
    var active = document.activeElement;
    if (!active) {
      return true;
    }
    if (scanSink && active === scanSink) {
      return true;
    }
    if (isScanAllowedElement(active)) {
      return true;
    }
    if (active.isContentEditable) {
      return false;
    }
    var tag = active.tagName;
    if (!tag) {
      return true;
    }
    tag = tag.toUpperCase();
    if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") {
      return false;
    }
    return true;
  }

  function setPreferredScanTarget(el) {
    scanPreferredTarget = el || null;
  }

  function getPreferredScanTarget() {
    if (!scanPreferredTarget) {
      return null;
    }
    if (scanPreferredTarget.disabled) {
      return null;
    }
    if (!scanPreferredTarget.isConnected) {
      return null;
    }
    if (typeof scanPreferredTarget.getClientRects === "function") {
      if (!scanPreferredTarget.getClientRects().length) {
        return null;
      }
    }
    return scanPreferredTarget;
  }

  function focusPreferredScanTarget() {
    var target = getPreferredScanTarget();
    if (target && typeof target.focus === "function") {
      target.focus();
      return true;
    }
    return false;
  }

  function shouldFocusForScan(target) {
    if (!target) {
      return true;
    }
    if (isScanAllowedElement(target)) {
      return false;
    }
    if (target.isContentEditable) {
      return false;
    }
    var tag = target.tagName ? target.tagName.toUpperCase() : "";
    if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") {
      return false;
    }
    return true;
  }

  function ensureScanFocus() {
    if (!scanHandlerActive) {
      return;
    }
    if (isManualOverlayOpen()) {
      return;
    }
    if (currentRoute && currentRoute.name === "login") {
      return;
    }
    enterScanMode();
  }

  function enterScanMode() {
    var active = document.activeElement;
    if (isScanAllowedElement(active)) {
      setScanHighlight(true);
      if (isSoftKeyboardSuppressed()) {
        hideVirtualKeyboard();
      }
      return;
    }
    if (active && active.blur) {
      active.blur();
    }
    if (focusPreferredScanTarget()) {
      setScanHighlight(true);
      if (isSoftKeyboardSuppressed()) {
        hideVirtualKeyboard();
      }
      return;
    }
    if (scannerManager && scannerManager.focus) {
      scannerManager.focus();
      setScanHighlight(true);
      if (isSoftKeyboardSuppressed()) {
        hideVirtualKeyboard();
      }
      return;
    }
    if (scanSink) {
      scanSink.value = "";
      scanSink.focus();
    }
    setScanHighlight(true);
    if (isSoftKeyboardSuppressed()) {
      hideVirtualKeyboard();
    }
  }

  function setScanHandler(handler) {
    scanHandlerActive = !!handler;
    activeScanHandler = handler || null;
    var wrappedHandler = handler
      ? function (scan) {
          if (scanDebug.enabled) {
            var detail =
              (scan && scan.source ? scan.source : "scan") +
              " " +
              (scan && scan.value ? scan.value : "");
            appendScanDebug("scan", detail);
          }
          if (activeScanHandler) {
            activeScanHandler(scan);
          }
        }
      : null;
    if (!scannerManager) {
      pendingScanHandler = wrappedHandler;
      return;
    }
    scannerManager.setHandler(wrappedHandler);
  }

  function setScanInputHandlers(inputHandler, keydownHandler) {
    // Scan input handlers are managed by the scanner module.
    scanInputHandler = null;
    scanInputKeydownHandler = null;
  }

  function initScannerManager() {
    if (!window.FlowStockScanner || !window.FlowStockScanner.createScannerManager) {
      return Promise.resolve(false);
    }
    scannerManager = window.FlowStockScanner.createScannerManager({
      scanSink: scanSink,
      canScan: canScanNow,
    });
    scannerManager.setErrorHandler(function (error) {
      if (window.console && console.warn) {
        console.warn("Scanner error", error);
      }
      if (scanDebug.enabled) {
        var message = error && error.message ? error.message : String(error || "error");
        appendScanDebug("scan-error", message);
      }
    });
    var envMode = window.FLOWSTOCK_SCANNER_MODE;
    if (envMode) {
      scannerMode = normalizeScannerMode(envMode);
      scannerManager.setMode(scannerMode);
      scannerManager.start();
      if (pendingScanHandler) {
        scannerManager.setHandler(pendingScanHandler);
        pendingScanHandler = null;
      }
      return TsdStorage.setSetting(SCANNER_MODE_KEY, scannerMode).catch(function () {
        return false;
      });
    }
    return TsdStorage.getSetting(SCANNER_MODE_KEY)
      .then(function (mode) {
        scannerMode = normalizeScannerMode(mode);
        scannerManager.setMode(scannerMode);
        scannerManager.start();
        if (pendingScanHandler) {
          scannerManager.setHandler(pendingScanHandler);
          pendingScanHandler = null;
        }
        return true;
      })
      .catch(function () {
        scannerMode = "auto";
        scannerManager.setMode(scannerMode);
        scannerManager.start();
        if (pendingScanHandler) {
          scannerManager.setHandler(pendingScanHandler);
          pendingScanHandler = null;
        }
        return false;
      });
  }

  function updateScannerMode(nextMode) {
    scannerMode = normalizeScannerMode(nextMode);
    if (scannerManager) {
      scannerManager.setMode(scannerMode);
    }
    return TsdStorage.setSetting(SCANNER_MODE_KEY, scannerMode).catch(function () {
      return false;
    });
  }

  var STATUS_LABELS = {
    DRAFT: "Черновик",
    RECOUNT: "На пересчет",
    READY: "Наполнен",
    CLOSED: "Закрыт",
    EXPORTED: "Передан",
  };
  var preserveScanFocus = false;

  var OPS = {
    INBOUND: { label: "Приемка", prefix: "IN" },
    PRODUCTION_RECEIPT: { label: "Выпуск продукции", prefix: "PRD" },
    OUTBOUND: { label: "Отгрузка", prefix: "OUT" },
    MOVE: { label: "Перемещение", prefix: "MOVE" },
    WRITE_OFF: { label: "Списание", prefix: "WO" },
    INVENTORY: { label: "Инвентаризация", prefix: "INV" },
  };

  var OP_BLOCK_KEYS = {
    INBOUND: "tsd_inbound",
    PRODUCTION_RECEIPT: "tsd_production_receipt",
    OUTBOUND: "tsd_outbound",
    MOVE: "tsd_move",
    WRITE_OFF: "tsd_write_off",
    INVENTORY: "tsd_inventory",
  };

  var WRITE_OFF_REASONS = [
    { code: "DAMAGED", label: "Повреждено" },
    { code: "EXPIRED", label: "Просрочено" },
    { code: "DEFECT", label: "Брак" },
    { code: "SAMPLE", label: "Проба" },
    { code: "PRODUCTION", label: "Производство" },
    { code: "OTHER", label: "Прочее" },
  ];

  var REQUIRED_FIELDS = {
    INBOUND: ["to"],
    PRODUCTION_RECEIPT: ["to"],
    OUTBOUND: ["from"],
    MOVE: ["from", "to"],
    WRITE_OFF: ["from", "reason_code"],
    INVENTORY: ["location"],
  };

  function isClientBlockEnabled(key) {
    return clientBlocks[key] !== false;
  }

  function isOperationEnabled(op) {
    var normalizedOp = String(op || "").toUpperCase();
    var blockKey = OP_BLOCK_KEYS[normalizedOp];
    if (!blockKey) {
      return false;
    }
    return isClientBlockEnabled("tsd_operations") && isClientBlockEnabled(blockKey);
  }

  function getEnabledOperations() {
    return Object.keys(OPS).filter(function (op) {
      return isOperationEnabled(op);
    });
  }

  function hasOperationsMenu() {
    return getEnabledOperations().length > 0;
  }

  function isBlockContextEnabled(blockKey) {
    var normalized = String(blockKey || "").trim().toLowerCase();
    if (!normalized) {
      return true;
    }
    if (normalized === "tsd_operations") {
      return hasOperationsMenu();
    }
    if (normalized === "tsd_stock" || normalized === "tsd_catalog" || normalized === "tsd_orders") {
      return isClientBlockEnabled(normalized);
    }

    var op = Object.keys(OP_BLOCK_KEYS).find(function (entry) {
      return OP_BLOCK_KEYS[entry] === normalized;
    });
    if (op) {
      return isOperationEnabled(op);
    }

    return true;
  }

  function isRouteAllowed(route) {
    if (!route) {
      return true;
    }

    if (route.name === "home" || route.name === "login" || route.name === "settings" || route.name === "hu") {
      return true;
    }
    if (route.name === "operations" || route.name === "new") {
      return hasOperationsMenu();
    }
    if (route.name === "docs") {
      return !!route.op && isOperationEnabled(route.op);
    }
    if (route.name === "doc") {
      return isBlockContextEnabled(currentClientBlockContext);
    }
    if (route.name === "stock") {
      return isClientBlockEnabled("tsd_stock");
    }
    if (route.name === "items") {
      return isClientBlockEnabled("tsd_catalog");
    }
    if (route.name === "orders" || route.name === "order") {
      return isClientBlockEnabled("tsd_orders");
    }

    return true;
  }

  function escapeHtml(value) {
    return String(value || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function normalizePlatform(value) {
    var normalized = String(value || "").trim().toUpperCase();
    if (normalized === "PC") {
      return "PC";
    }
    if (normalized === "BOTH" || normalized === "PC+TSD" || normalized === "PC_TSD") {
      return "BOTH";
    }
    return "TSD";
  }

  function storeAccount(deviceId, platform, login) {
    var payload = {
      device_id: deviceId || "",
      platform: platform || "TSD",
      login: login || "",
    };
    try {
      localStorage.setItem("flowstock_account", JSON.stringify(payload));
    } catch (error) {
      // ignore storage failures
    }
  }

  function getStoredAccount() {
    try {
      var raw = localStorage.getItem("flowstock_account");
      if (!raw) {
        return null;
      }
      return JSON.parse(raw);
    } catch (error) {
      return null;
    }
  }

  function getStoredLogin() {
    var account = getStoredAccount();
    var login = account && account.login ? String(account.login).trim() : "";
    return login;
  }

  function getReasonLabel(code) {
    if (!code) {
      return "";
    }
    var match = WRITE_OFF_REASONS.find(function (item) {
      return item.code === code;
    });
    return match ? match.label : code;
  }

  function padNumber(value, size) {
    var str = String(value);
    while (str.length < size) {
      str = "0" + str;
    }
    return str;
  }

  function formatDocCreatedAt(doc) {
    if (!doc) {
      return "—";
    }
    var raw = doc.createdAt || doc.created_at;
    if (!raw) {
      return "—";
    }
    var date = new Date(raw);
    if (isNaN(date.getTime())) {
      return "—";
    }
    var day = padNumber(date.getDate(), 2);
    var month = padNumber(date.getMonth() + 1, 2);
    var year = date.getFullYear();
    var hours = padNumber(date.getHours(), 2);
    var minutes = padNumber(date.getMinutes(), 2);
    return day + "." + month + "." + year + " " + hours + ":" + minutes;
  }

  function formatDate(value) {
    if (!value) {
      return "—";
    }
    var date = new Date(value);
    if (isNaN(date.getTime())) {
      return "—";
    }
    var day = padNumber(date.getDate(), 2);
    var month = padNumber(date.getMonth() + 1, 2);
    var year = date.getFullYear();
    return day + "." + month + "." + year;
  }

  function formatDateTime(value) {
    if (!value) {
      return "-";
    }
    var date = new Date(value);
    if (isNaN(date.getTime())) {
      return "—";
    }
    var day = padNumber(date.getDate(), 2);
    var month = padNumber(date.getMonth() + 1, 2);
    var year = date.getFullYear();
    var hours = padNumber(date.getHours(), 2);
    var minutes = padNumber(date.getMinutes(), 2);
    return day + "." + month + "." + year + " " + hours + ":" + minutes;
  }

  function isServerDocId(value) {
    return /^[0-9]+$/.test(String(value || ""));
  }

  function getDocSortTime(doc) {
    if (!doc) {
      return 0;
    }
    var raw =
      doc.updatedAt ||
      doc.updated_at ||
      doc.createdAt ||
      doc.created_at ||
      doc.closed_at ||
      doc.closedAt ||
      null;
    if (!raw) {
      return 0;
    }
    var date = new Date(raw);
    return isNaN(date.getTime()) ? 0 : date.getTime();
  }

  function getOrderStatusInfo(status) {
    var raw = String(status || "").trim();
    if (!raw) {
      return { label: "-", className: "order-status-pill order-status-neutral" };
    }
    var normalized = raw.toLowerCase();
    if (
      normalized.indexOf("принят") !== -1 ||
      normalized.indexOf("accepted") !== -1 ||
      normalized.indexOf("new") !== -1
    ) {
      return { label: raw, className: "order-status-pill order-status-accepted" };
    }
    if (
      normalized.indexOf("черновик") !== -1 ||
      normalized.indexOf("draft") !== -1 ||
      normalized.indexOf("процесс") !== -1 ||
      normalized.indexOf("в работе") !== -1 ||
      normalized.indexOf("processing") !== -1 ||
      normalized.indexOf("picking") !== -1
    ) {
      return { label: "В работе", className: "order-status-pill order-status-progress" };
    }
    if (
      normalized.indexOf("отгруж") !== -1 ||
      normalized.indexOf("shipped") !== -1 ||
      normalized.indexOf("done") !== -1 ||
      normalized.indexOf("closed") !== -1
    ) {
      return { label: raw, className: "order-status-pill order-status-shipped" };
    }
    return { label: raw, className: "order-status-pill order-status-neutral" };
  }

  function getDateKey(date) {
    var year = date.getFullYear();
    var month = padNumber(date.getMonth() + 1, 2);
    var day = padNumber(date.getDate(), 2);
    return "" + year + month + day;
  }

  function getTimeKey(date) {
    var hours = padNumber(date.getHours(), 2);
    var minutes = padNumber(date.getMinutes(), 2);
    return "" + hours + minutes;
  }

  function createUuid() {
    if (window.crypto && window.crypto.randomUUID) {
      return window.crypto.randomUUID();
    }
    return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, function (c) {
      var r = (Math.random() * 16) | 0;
      var v = c === "x" ? r : (r & 0x3) | 0x8;
      return v.toString(16);
    });
  }

  function formatPartnerLabel(partner) {
    if (!partner) {
      return "Не выбран";
    }
    var name = partner.name || "";
    var code = partner.code ? " (" + partner.code + ")" : "";
    return name + code;
  }

  function getPartnerRoleForOp(op) {
    if (op === "INBOUND") {
      return "supplier";
    }
    if (op === "OUTBOUND") {
      return "customer";
    }
    return "both";
  }

  function formatLocationLabel(code, name) {
    var safeCode = String(code || "").trim();
    var safeName = String(name || "").trim();
    if (!safeCode && !safeName) {
      return "Не выбрана";
    }
    if (safeCode && safeName) {
      return safeCode + " — " + safeName;
    }
    return safeCode || safeName;
  }

  function formatOrderLabel(orderNumber) {
    var safeNumber = String(orderNumber || "").trim();
    return safeNumber ? safeNumber : "Не выбран";
  }

  function updateRecentSetting(key, value) {
    return TsdStorage.getSetting(key)
      .then(function (list) {
        var current = Array.isArray(list) ? list.slice() : [];
        var cleanValue = value;
        current = current.filter(function (item) {
          return item !== cleanValue;
        });
        current.unshift(cleanValue);
        return TsdStorage.setSetting(key, current.slice(0, 5));
      })
      .catch(function () {
        return TsdStorage.setSetting(key, [value]);
      });
  }

  function getRoute() {
    var hash = window.location.hash || "";
    var path = hash.replace("#", "");
    if (path.indexOf("/") === 0) {
      path = path.slice(1);
    }
    if (!path) {
      return { name: "home" };
    }

    var parts = path.split("/");
    if (parts[0] === "operations") {
      return { name: "operations" };
    }
    if (parts[0] === "docs" && parts[1]) {
      return { name: "docs", op: decodeURIComponent(parts[1]) };
    }
    if (parts[0] === "docs") {
      return { name: "docs" };
    }
    if (parts[0] === "doc" && parts[1]) {
      return { name: "doc", id: decodeURIComponent(parts[1]) };
    }
    if (parts[0] === "order" && parts[1]) {
      return { name: "order", id: decodeURIComponent(parts[1]) };
    }
    return { name: parts[0] };
  }

  function navigate(route) {
    if (route) {
      window.location.hash = route;
    }
  }

  function updateHeader(route) {
    if (!appHeader || !backBtn) {
      return;
    }

    if (route.name === "home" || route.name === "login") {
      if (backBtn.parentNode) {
        backBtn.parentNode.removeChild(backBtn);
      }
    } else if (!backBtn.parentNode) {
      var anchor = appTitleWrap || appTitle;
      if (anchor && anchor.parentNode === appHeader) {
        appHeader.insertBefore(backBtn, anchor);
      } else {
        appHeader.insertBefore(backBtn, appHeader.firstChild);
      }
    }

    if (settingsBtn) {
      settingsBtn.classList.toggle("is-active", route.name === "settings");
      settingsBtn.classList.toggle("is-hidden", route.name === "login");
    }
  }

  function renderRoute() {
    setScanHandler(null);
    setScanInputHandlers(null, null);
    setPreferredScanTarget(null);
    if (!window.location.hash || window.location.hash === "#") {
      navigate("/home");
      return;
    }
    var route = getRoute();
    currentRoute = route;
    setCurrentClientBlockContext(resolveRouteBlockContext(route));

    if (!app) {
      return;
    }

    TsdStorage.getSetting("device_id")
      .then(function (deviceId) {
        var hasDevice = deviceId && String(deviceId).trim();
        if (!hasDevice && route.name !== "login") {
          navigate("/login");
          return;
        }
        if (hasDevice && route.name === "login") {
          navigate("/home");
          return;
        }
        ensureClientBlocksLoaded(true)
          .then(function () {
            if (!isRouteAllowed(route)) {
              navigate("/home");
              return;
            }
            updateHeader(route);
            renderRouteInternal(route);
          })
          .catch(function () {
            updateHeader(route);
            renderRouteInternal(route);
          });
      })
      .catch(function () {
        if (route.name !== "login") {
          navigate("/login");
          return;
        }
        ensureClientBlocksLoaded(true)
          .then(function () {
            updateHeader(route);
            renderRouteInternal(route);
          })
          .catch(function () {
            updateHeader(route);
            renderRouteInternal(route);
          });
      });
  }

  function renderRouteInternal(route) {
    if (route.name === "login") {
      app.innerHTML = renderLogin();
      wireLogin();
      applySoftKeyboardSetting(app);
      return;
    }

    if (route.name === "docs") {
      if (!route.op || !isOperationEnabled(route.op)) {
        navigate("/home");
        return;
      }
      setCurrentClientBlockContext(getOperationBlockKey(route.op) || "tsd_operations");
      app.innerHTML = renderLoading();
      Promise.all([
        TsdStorage.apiGetDocs(route.op).catch(function () {
          return null;
        }),
        TsdStorage.listDocs().catch(function () {
          return [];
        }),
      ])
        .then(function (results) {
          var serverDocs = results[0];
          var localDocs = results[1] || [];
          var notice = null;
          var list = [];
          if (Array.isArray(serverDocs)) {
            list = serverDocs;
          } else {
            notice = "Документы с сервера недоступны.";
          }
          if (localDocs.length) {
            var localIds = {};
            localDocs.forEach(function (doc) {
              localIds[String(doc.id)] = true;
            });
            list = list.filter(function (doc) {
              var docUid = doc && (doc.doc_uid || doc.docUid) ? String(doc.doc_uid || doc.docUid) : "";
              if (!docUid) {
                return true;
              }
              return !localIds[docUid];
            });
            list = localDocs.concat(list);
          }
          app.innerHTML = renderDocsList(list, route.op, notice);
          wireDocsList();
          applySoftKeyboardSetting(app);
        })
        .catch(function () {
          app.innerHTML = renderError("Ошибка загрузки документов");
          applySoftKeyboardSetting(app);
        });
      return;
    }

    if (route.name === "operations" || route.name === "new") {
      if (!hasOperationsMenu()) {
        navigate("/home");
        return;
      }
      app.innerHTML = renderOperationsMenu();
      wireOperationsMenu();
      applySoftKeyboardSetting(app);
      return;
    }

    if (route.name === "doc") {
      app.innerHTML = renderLoading();
      if (isServerDocId(route.id)) {
        TsdStorage.apiGetDocById(route.id)
          .then(function (doc) {
            if (!doc) {
              app.innerHTML = renderError("Документ не найден");
              applySoftKeyboardSetting(app);
              return;
            }
            if (!isOperationEnabled(doc.op)) {
              navigate("/home");
              return;
            }
            setCurrentClientBlockContext(getOperationBlockKey(doc.op) || currentClientBlockContext);
            return TsdStorage.apiGetDocLines(route.id)
              .then(function (lines) {
                if (doc.status === "DRAFT" && doc.doc_uid) {
                  startDraftDoc(doc, lines || []);
                  return;
                }
                app.innerHTML = renderServerDoc(doc, lines || []);
                wireServerDoc(doc, lines || []);
                applySoftKeyboardSetting(app);
              })
              .catch(function () {
                app.innerHTML = renderError("Ошибка загрузки строк документа");
                applySoftKeyboardSetting(app);
              });
          })
          .catch(function () {
            app.innerHTML = renderError("Ошибка загрузки документа");
            applySoftKeyboardSetting(app);
          });
      } else {
        TsdStorage.getDoc(route.id)
          .then(function (doc) {
            if (!doc) {
              app.innerHTML = renderError("Документ не найден");
              applySoftKeyboardSetting(app);
              return;
            }
            if (!isOperationEnabled(doc.op)) {
              navigate("/home");
              return;
            }
            setCurrentClientBlockContext(getOperationBlockKey(doc.op) || currentClientBlockContext);
            app.innerHTML = renderDoc(doc);
            wireDoc(doc);
            applySoftKeyboardSetting(app);
          })
          .catch(function () {
            app.innerHTML = renderError("Ошибка загрузки документа");
            applySoftKeyboardSetting(app);
          });
      }
      return;
    }

    if (route.name === "settings") {
      app.innerHTML = renderSettings();
      wireSettings();
      applySoftKeyboardSetting(app);
      return;
    }

    if (route.name === "orders") {
      app.innerHTML = renderOrders();
      wireOrders();
      applySoftKeyboardSetting(app);
      return;
    }

    if (route.name === "order") {
      app.innerHTML = renderLoading();
      TsdStorage.getOrderById(route.id)
        .then(function (order) {
          if (!order) {
            app.innerHTML = renderError("Заказ не найден");
            applySoftKeyboardSetting(app);
            return;
          }
          return TsdStorage.listOrderLines(route.id)
            .then(function (lines) {
              app.innerHTML = renderOrderDetails(order, lines || []);
              wireOrderDetails();
              applySoftKeyboardSetting(app);
            })
            .catch(function () {
              app.innerHTML = renderError("Ошибка загрузки строк заказа");
              applySoftKeyboardSetting(app);
            });
        })
        .catch(function () {
          app.innerHTML = renderError("Ошибка загрузки заказа");
          applySoftKeyboardSetting(app);
        });
      return;
    }

    if (route.name === "items") {
      app.innerHTML = renderItems();
      wireItems();
      applySoftKeyboardSetting(app);
      return;
    }

    if (route.name === "stock") {
      app.innerHTML = renderStock();
      wireStock();
      applySoftKeyboardSetting(app);
      return;
    }

    if (route.name === "hu") {
      app.innerHTML = renderHuLookup();
      wireHuLookup();
      applySoftKeyboardSetting(app);
      return;
    }

    app.innerHTML = renderHome();
    wireHome();
    applySoftKeyboardSetting(app);
  }

  function canRefreshClientBlocksForCurrentRoute() {
    if (!currentRoute) {
      return false;
    }

    return (
      currentRoute.name === "home" ||
      currentRoute.name === "operations" ||
      currentRoute.name === "docs" ||
      currentRoute.name === "stock" ||
      currentRoute.name === "items" ||
      currentRoute.name === "orders"
    );
  }

  function renderLoading() {
    return '<section class="screen"><div class="screen-card">Загрузка...</div></section>';
  }

  function renderError(message) {
    return (
      '<section class="screen"><div class="screen-card">' +
      escapeHtml(message) +
      "</div></section>"
    );
  }

  function buildHomeMenuButtonsHtml() {
    var buttons = [];
    if (hasOperationsMenu()) {
      buttons.push('<button class="btn menu-btn" data-route="operations">Операции</button>');
    }
    if (isClientBlockEnabled("tsd_stock")) {
      buttons.push('<button class="btn menu-btn" data-route="stock">Состояние склада</button>');
    }
    if (isClientBlockEnabled("tsd_catalog")) {
      buttons.push('<button class="btn menu-btn" data-route="items">Каталог</button>');
    }
    if (isClientBlockEnabled("tsd_orders")) {
      buttons.push('<button class="btn menu-btn" data-route="orders">Заказы</button>');
    }
    if (!buttons.length) {
      return '<div class="empty-state">Все блоки ТСД сейчас временно отключены.</div>';
    }
    return buttons.join("");
  }

  var homeLowStockRequestSeq = 0;

  function formatQtyWithUnit(qty, unit) {
    var num = Number(qty);
    if (!isFinite(num)) {
      num = 0;
    }
    var normalized =
      Math.abs(num - Math.round(num)) < 0.000001
        ? String(Math.round(num))
        : num.toFixed(3).replace(/0+$/, "").replace(/\.$/, "");
    return normalized + (unit ? " " + unit : "");
  }

  function renderHomeLowStockRows(rows) {
    if (!rows || !rows.length) {
      return '<div class="empty-state">Позиции ниже минимума отсутствуют.</div>';
    }

    var body = rows
      .map(function (row) {
        return (
          "<tr>" +
          "<td>" +
          escapeHtml(row.itemName || "-") +
          "</td>" +
          "<td>" +
          escapeHtml(row.itemTypeName || "-") +
          "</td>" +
          '<td><span class="pc-qty">' +
          escapeHtml(row.qtyDisplay || "0") +
          "</span></td>" +
          "<td>" +
          escapeHtml(row.minStockDisplay || "-") +
          "</td>" +
          "<td>" +
          escapeHtml(row.shortageDisplay || "-") +
          "</td>" +
          "</tr>"
        );
      })
      .join("");

    return (
      '<div class="pc-low-stock-card home-low-stock-card">' +
      '  <div class="pc-low-stock-title">Позиции ниже минимума: ' +
      rows.length +
      "</div>" +
      '  <div class="pc-low-stock-table-wrap">' +
      '    <table class="pc-table">' +
      "      <thead>" +
      "        <tr>" +
      "          <th>Товар</th>" +
      "          <th>Тип</th>" +
      "          <th>В наличии</th>" +
      "          <th>Минимум</th>" +
      "          <th>Нехватка</th>" +
      "        </tr>" +
      "      </thead>" +
      "      <tbody>" +
      body +
      "      </tbody>" +
      "    </table>" +
      "  </div>" +
      "</div>"
    );
  }

  function loadHomeLowStockRows() {
    return Promise.all([TsdStorage.apiSearchItems(""), TsdStorage.apiGetStockRows()]).then(function (
      payloads
    ) {
      var items = Array.isArray(payloads[0]) ? payloads[0] : [];
      var stockRows = Array.isArray(payloads[1]) ? payloads[1] : [];
      var itemMap = {};
      var totalsByItem = {};

      items.forEach(function (item) {
        var id = Number(item && (item.itemId != null ? item.itemId : item.id)) || 0;
        if (!id) {
          return;
        }
        itemMap[id] = {
          itemId: id,
          name: String(item.name || "").trim(),
          base_uom: String(item.base_uom_code || item.base_uom || "").trim(),
          itemTypeId: Number(item.item_type_id) || 0,
          itemTypeName: String(item.item_type_name || "").trim(),
          itemTypeEnableMinStockControl: item.item_type_enable_min_stock_control === true,
          minStockQty: item.min_stock_qty != null ? Number(item.min_stock_qty) : null,
        };
      });

      stockRows.forEach(function (row) {
        var itemId = Number(row && row.item_id) || 0;
        if (!itemId) {
          return;
        }
        totalsByItem[itemId] = (totalsByItem[itemId] || 0) + Number(row.qty || 0);
      });

      return Object.keys(itemMap)
        .map(function (key) {
          return itemMap[Number(key)];
        })
        .filter(function (item) {
          if (!item) {
            return false;
          }
          if (!(item.itemTypeEnableMinStockControl === true) || item.minStockQty == null) {
            return false;
          }
          var qty = Number(totalsByItem[item.itemId] || 0);
          return qty < Number(item.minStockQty || 0);
        })
        .map(function (item) {
          var qty = Number(totalsByItem[item.itemId] || 0);
          var minStockQty = Number(item.minStockQty || 0);
          var shortage = Math.max(0, minStockQty - qty);
          var unit = item.base_uom || "шт";
          return {
            itemName: item.name || "-",
            itemTypeName: item.itemTypeName || "-",
            qtyDisplay: formatQtyWithUnit(qty, unit),
            minStockDisplay: formatQtyWithUnit(minStockQty, unit),
            shortageDisplay: formatQtyWithUnit(shortage, unit),
            shortage: shortage,
          };
        })
        .sort(function (a, b) {
          if (b.shortage !== a.shortage) {
            return b.shortage - a.shortage;
          }
          return String(a.itemName || "").localeCompare(String(b.itemName || ""), "ru");
        });
    });
  }

  function buildOperationsMenuButtonsHtml() {
    var buttons = [];
    Object.keys(OPS).forEach(function (op) {
      if (!isOperationEnabled(op)) {
        return;
      }
      buttons.push(
        '<button class="btn menu-btn" data-op="' +
          escapeHtml(op) +
          '">' +
          escapeHtml(OPS[op].label) +
          "</button>"
      );
    });
    if (!buttons.length) {
      return '<div class="empty-state">Все операции сейчас временно отключены.</div>';
    }
    return buttons.join("");
  }

  function renderHome() {
    return (
      '<section class="screen home-screen">' +
      '  <div class="menu-grid">' +
      buildHomeMenuButtonsHtml() +
      "  </div>" +
      '  <div id="homeLowStockWrap" class="home-low-stock-wrap"></div>' +
      "</section>"
    );
  }
  function renderDocsList(docs, opFilter, notice) {
    var list = docs || [];
    var listOp = opFilter && OPS[opFilter] ? opFilter : null;
    if (listOp) {
      list = list.filter(function (doc) {
        return doc.op === listOp;
      });
    }
    list.sort(function (a, b) {
      var statusDiff = (STATUS_ORDER[a.status] || 0) - (STATUS_ORDER[b.status] || 0);
      if (statusDiff !== 0) {
        return statusDiff;
      }
      var dateA = getDocSortTime(a);
      var dateB = getDocSortTime(b);
      return dateB - dateA;
    });

    var rows = list
      .map(function (doc) {
        var opLabel = OPS[doc.op] ? OPS[doc.op].label : doc.op;
        var statusLabel = STATUS_LABELS[doc.status] || doc.status;
        var createdLabel = formatDocCreatedAt(doc);
        return (
          '<button class="doc-item" data-doc="' +
          escapeHtml(doc.id) +
          '">' +
          '  <div class="doc-main">' +
          '    <div class="doc-title">' +
          escapeHtml(opLabel) +
          "</div>" +
          '    <div class="doc-ref">' +
          escapeHtml(doc.doc_ref || "") +
          "</div>" +
          '    <div class="doc-created">Создан: ' +
          escapeHtml(createdLabel) +
          "</div>" +
          "  </div>" +
          '  <div class="doc-status">' +
          escapeHtml(statusLabel) +
          "</div>" +
          "</button>"
        );
      })
      .join("");

    if (!rows) {
      rows = '<div class="empty-state">Операций пока нет.</div>';
    }

    var title = listOp && OPS[listOp] ? OPS[listOp].label : "Операции";
    var actionsHtml = "";
    if (listOp) {
      actionsHtml =
        '<div class="actions-row doc-actions">' +
        '  <button class="btn primary-btn" id="newDocBtn" data-op="' +
        escapeHtml(listOp) +
        '">+ Новый</button>' +
        "</div>";
    }

    var noticeHtml = notice
      ? '<div class="status">' + escapeHtml(notice) + "</div>"
      : "";

    return (
      '<section class="screen">' +
      '  <div class="screen-card doc-screen-card">' +
      '    <div class="section-title">' +
      escapeHtml(title) +
      "</div>" +
      actionsHtml +
      noticeHtml +
      '    <div class="doc-list">' +
      rows +
      "    </div>" +
      "  </div>" +
      "</section>"
    );
  }

  function renderOrders() {
    return (
      '<section class="screen">' +
      '  <div class="screen-card doc-screen-card">' +
      '    <div class="section-title">Заказы</div>' +
      '    <input class="form-input" id="ordersSearchInput" type="text" autocomplete="off" placeholder="Поиск по номеру или контрагенту" />' +
      '    <div id="ordersStatus" class="status"></div>' +
      '    <div id="ordersList" class="doc-list order-list"></div>' +
      "  </div>" +
      "</section>"
    );
  }

  function renderOrderDetails(order, lines) {
    var statusInfo = getOrderStatusInfo(order.status);
    var orderNumber =
      order.number || order.orderNumber || order.order_ref || order.orderRef || "—";
    var partnerLabel = order.partnerName || "—";
    if (order.partnerInn) {
      partnerLabel += " · ИНН: " + order.partnerInn;
    }
    var plannedDate = formatDate(order.plannedDate || order.planned_date);
    var shippedDate = formatDate(order.shippedAt || order.shipped_at);
    var createdAt = formatDateTime(order.createdAt || order.created_at);

    var rows = (lines || [])
      .map(function (line) {
        var ordered = Number(line.orderedQty) || 0;
        var shipped = Number(line.shippedQty) || 0;
        var left = Number(line.leftQty);
        if (isNaN(left)) {
          left = Math.max(ordered - shipped, 0);
        }
        return (
          '<div class="order-line-row">' +
          '  <div class="order-line-item">' +
          '    <div class="order-line-name">' +
          escapeHtml(line.itemName || "-") +
          "</div>" +
          (line.barcode
            ? '    <div class="order-line-barcode">' + escapeHtml(line.barcode) + "</div>"
            : "") +
          "  </div>" +
          '  <div class="order-line-qty">' +
          escapeHtml(String(ordered)) +
          "</div>" +
          '  <div class="order-line-qty">' +
          escapeHtml(String(shipped)) +
          "</div>" +
          '  <div class="order-line-qty">' +
          escapeHtml(String(left)) +
          "</div>" +
          "</div>"
        );
      })
      .join("");

    if (!rows) {
      rows = '<div class="empty-state">Строк заказа нет.</div>';
    } else {
      rows =
        '<div class="order-line-header">' +
        '  <div class="order-line-head">Товар</div>' +
        '  <div class="order-line-head">Заказано</div>' +
        '  <div class="order-line-head">Отгружено</div>' +
        '  <div class="order-line-head">Осталось</div>' +
        "</div>" +
        rows;
    }

    return (
      '<section class="screen">' +
      '  <div class="screen-card doc-screen-card">' +
      '    <div class="order-head">' +
      '      <div class="order-title">Заказ № ' +
      escapeHtml(String(orderNumber || "—")) +
      "</div>" +
      '      <div class="' +
      escapeHtml(statusInfo.className) +
      '">' +
      escapeHtml(statusInfo.label) +
      "</div>" +
      "    </div>" +
      '    <div class="order-fields">' +
      '      <div class="order-field-row">' +
      '        <div class="order-field-label">Контрагент</div>' +
      '        <div class="order-field-value">' +
      escapeHtml(partnerLabel) +
      "</div>" +
      "      </div>" +
      '      <div class="order-field-row">' +
      '        <div class="order-field-label">Плановая отгрузка</div>' +
      '        <div class="order-field-value">' +
      escapeHtml(plannedDate) +
      "</div>" +
      "      </div>" +
      '      <div class="order-field-row">' +
      '        <div class="order-field-label">Факт отгрузки</div>' +
      '        <div class="order-field-value">' +
      escapeHtml(shippedDate) +
      "</div>" +
      "      </div>" +
      '      <div class="order-field-row">' +
      '        <div class="order-field-label">Создан</div>' +
      '        <div class="order-field-value">' +
      escapeHtml(createdAt) +
      "</div>" +
      "      </div>" +
      "    </div>" +
      '    <div class="section-subtitle">Строки заказа</div>' +
      '    <div class="order-lines">' +
      rows +
      "    </div>" +
      "  </div>" +
      "</section>"
    );
  }

  function renderOperationsMenu() {
    return (
      '<section class="screen">' +
      '  <div class="screen-card">' +
      '    <div class="section-title">Операции</div>' +
      '    <div class="menu-grid">' +
      buildOperationsMenuButtonsHtml() +
      "    </div>" +
      "  </div>" +
      "</section>"
    );
  }

  function renderStock() {
    return (
      '<section class="screen">' +
      '  <div class="screen-card">' +
      '    <div class="section-title">Состояние склада</div>' +
      '    <div id="stockStatus" class="stock-status">Дата актуальности: -</div>' +
      '    <div class="section-subtitle">Сканирование</div>' +
      '    <div class="scan-input-row">' +
      '      <input class="form-input" id="stockScanInput" type="text" autocomplete="off" autocapitalize="off" autocorrect="off" spellcheck="false" data-scan-allow="1" placeholder="Сканируйте HU или товар" />' +
      '      <button class="btn btn-outline" id="stockScanBtn" type="button">Сканировать</button>' +
      "    </div>" +
      '    <div class="stock-toolbar">' +
      '      <div class="stock-filter-field">' +
      '        <label class="form-label" for="stockSearchInput">Поиск</label>' +
      '        <input class="form-input" id="stockSearchInput" type="text" autocomplete="off" placeholder="Название, бренд, объем, SKU, GTIN, штрихкод" />' +
      "      </div>" +
      '      <div class="stock-filter-field">' +
      '        <label class="form-label" for="stockLocationFilter">Место хранения</label>' +
      '        <select class="form-input" id="stockLocationFilter"></select>' +
      "      </div>" +
      '      <div class="stock-filter-field">' +
      '        <label class="form-label" for="stockTypeFilter">Тип</label>' +
      '        <select class="form-input" id="stockTypeFilter"></select>' +
      "      </div>" +
      '      <div class="stock-filter-field">' +
      '        <label class="form-label" for="stockHuInput">HU (только цифры)</label>' +
      '        <div class="stock-hu-inline">' +
      '          <span class="stock-hu-prefix">HU-</span>' +
      '          <input class="form-input" id="stockHuInput" type="text" autocomplete="off" inputmode="numeric" placeholder="например, 00010" />' +
      "        </div>" +
      '        <div id="stockHuHint" class="stock-input-hint" hidden>Можно вводить только цифры.</div>' +
      "      </div>" +
      '      <div id="stockStatusText" class="stock-toolbar-status"></div>' +
      "    </div>" +
      '    <div id="stockLowWrap"></div>' +
      '    <div id="stockTableWrap"></div>' +
      '    <div id="stockMessage" class="stock-message"></div>' +
      '    <div id="stockDetails" class="stock-details"></div>' +
      '    <div class="actions-bar">' +
      '      <button class="btn btn-outline" id="stockClearBtn" type="button">Очистить</button>' +
      "    </div>" +
      "  </div>" +
      "</section>"
    );
  }

  function renderItems() {
    return (
      '<section class="screen">' +
      '  <div class="screen-card doc-screen-card">' +
      '    <div class="section-title">Каталог</div>' +
      '    <input class="form-input" id="itemsSearchInput" type="text" autocomplete="off" autocapitalize="off" autocorrect="off" spellcheck="false" placeholder="Поиск по названию, SKU, GTIN или штрихкоду" />' +
      '    <div id="itemsStatus" class="status"></div>' +
      '    <div id="itemsList" class="doc-list"></div>' +
      "  </div>" +
      "</section>"
    );
  }

  function renderHuLookup() {
    return (
      '<section class="screen">' +
      '  <div class="screen-card">' +
      '    <div class="section-title">HU / Паллеты</div>' +
      '    <div class="hu-actions">' +
      '      <button class="btn btn-outline" id="huLookupScanBtn" type="button">Сканировать HU</button>' +
      "    </div>" +
      '    <div id="huLookupStatus" class="stock-status">Данные по ПК: отсутствуют</div>' +
      '    <div id="huLookupMessage" class="stock-message"></div>' +
      '    <div id="huLookupDetails" class="hu-details"></div>' +
      "  </div>" +
      "</section>"
    );
  }

  function wireStock() {
    var statusLabel = document.getElementById("stockStatus");
    var scanInput = document.getElementById("stockScanInput");
    var scanBtn = document.getElementById("stockScanBtn");
    var messageEl = document.getElementById("stockMessage");
    var detailsEl = document.getElementById("stockDetails");
    var clearBtn = document.getElementById("stockClearBtn");
    var dataReady = false;
    var lastStockValue = "";
    var locationMap = {};
    var itemsById = {};
    var itemsLoading = null;
    var huStockCache = null;
    var huStockCachedAt = 0;
    var HU_STOCK_TTL_MS = 15000;
    var rowsStatusEl = document.getElementById("stockStatusText");
    var stockSearchInput = document.getElementById("stockSearchInput");
    var locationSelect = document.getElementById("stockLocationFilter");
    var typeFilter = document.getElementById("stockTypeFilter");
    var huInput = document.getElementById("stockHuInput");
    var huHint = document.getElementById("stockHuHint");
    var lowWrap = document.getElementById("stockLowWrap");
    var tableWrap = document.getElementById("stockTableWrap");
    var stockDataLoaded = false;
    var stockRenderTimer = null;
    var cachedItems = [];
    var cachedItemsById = {};
    var cachedItemTypes = [];
    var cachedStockRows = [];
    var cachedHuRows = [];
    var cachedCombinedRows = [];

    function setStatusText(text) {
      if (statusLabel) {
        statusLabel.textContent = text;
      }
    }

    function setStockMessage(text) {
      if (messageEl) {
        messageEl.textContent = text || "";
      }
    }

    function clearDetails() {
      if (detailsEl) {
        detailsEl.innerHTML = "";
      }
    }

    function setRowsStatus(text) {
      if (rowsStatusEl) {
        rowsStatusEl.textContent = text || "";
      }
    }

    function normalizeQty(value) {
      var num = Number(value);
      if (!isFinite(num)) {
        return 0;
      }
      return num;
    }

    function formatQtyDisplay(qty, itemId) {
      var num = normalizeQty(qty);
      var item = cachedItemsById[Number(itemId)] || {};
      var unit = item.base_uom || item.base_uom_code || "шт";
      var normalized =
        Math.abs(num - Math.round(num)) < 0.000001
          ? String(Math.round(num))
          : num.toFixed(3).replace(/0+$/, "").replace(/\.$/, "");
      return normalized + (unit ? " " + unit : "");
    }

    function setCachedItems(items) {
      cachedItems = Array.isArray(items) ? items : [];
      cachedItemsById = {};
      cachedItems.forEach(function (item) {
        var id = Number(item && item.itemId != null ? item.itemId : item.id);
        if (!id) {
          return;
        }
        cachedItemsById[id] = {
          itemId: id,
          name: String(item.name || "").trim(),
          barcode: String(item.barcode || "").trim(),
          gtin: String(item.gtin || "").trim(),
          sku: String(item.sku || "").trim(),
          brand: String(item.brand || "").trim(),
          volume: String(item.volume || "").trim(),
          base_uom: String(item.base_uom_code || item.base_uom || "").trim(),
          base_uom_code: String(item.base_uom_code || item.base_uom || "").trim(),
          itemTypeId: Number(item.item_type_id) || 0,
          itemTypeName: String(item.item_type_name || "").trim(),
          itemTypeEnableMinStockControl: item.item_type_enable_min_stock_control === true,
          minStockQty: item.min_stock_qty != null ? Number(item.min_stock_qty) : null,
        };
      });
      itemsById = Object.assign({}, cachedItemsById);
    }

    function setCachedLocations(locations) {
      locationMap = {};
      (Array.isArray(locations) ? locations : []).forEach(function (location) {
        var id = Number(location && location.locationId != null ? location.locationId : location.id);
        if (!id) {
          return;
        }
        locationMap[id] = {
          locationId: id,
          code: String(location.code || "").trim(),
          name: String(location.name || "").trim(),
        };
      });
    }

    function setCachedItemTypes(itemTypes) {
      cachedItemTypes = Array.isArray(itemTypes) ? itemTypes.slice() : [];
    }

    function buildCombinedRows() {
      var totalsByKey = {};
      cachedHuRows.forEach(function (row) {
        var key = Number(row.itemId) + "|" + Number(row.locationId);
        totalsByKey[key] = (totalsByKey[key] || 0) + normalizeQty(row.qty);
      });

      var combined = cachedHuRows.slice();
      cachedStockRows.forEach(function (row) {
        var key = Number(row.itemId) + "|" + Number(row.locationId);
        var huQty = totalsByKey[key] || 0;
        var diff = normalizeQty(row.qty) - huQty;
        if (Math.abs(diff) < 0.000001) {
          return;
        }

        combined.push({
          itemId: Number(row.itemId) || 0,
          locationId: Number(row.locationId) || 0,
          qty: diff,
          qtyDisplay: formatQtyDisplay(diff, row.itemId),
          itemName: row.itemName || "-",
          barcode: row.barcode || "",
          gtin: row.gtin || "",
          brand: row.brand || "",
          volume: row.volume || "",
          itemTypeId: Number(row.itemTypeId) || 0,
          itemTypeName: row.itemTypeName || "",
          locationCode: row.locationCode || "",
          hu: "",
        });
      });

      cachedCombinedRows = combined;
    }

    function renderLowStockTable(rows) {
      if (!rows || !rows.length) {
        return "";
      }

      var body = rows
        .map(function (row) {
          return (
            "<tr>" +
            "<td>" +
            escapeHtml(row.itemName || "-") +
            "</td>" +
            "<td>" +
            escapeHtml(row.itemTypeName || "-") +
            "</td>" +
            '<td><span class="pc-qty">' +
            escapeHtml(row.qtyDisplay || "0") +
            "</span></td>" +
            "<td>" +
            escapeHtml(row.minStockDisplay || "-") +
            "</td>" +
            "<td>" +
            escapeHtml(row.shortageDisplay || "-") +
            "</td>" +
            "</tr>"
          );
        })
        .join("");

      return (
        '<section class="pc-low-stock-card">' +
        '  <div class="pc-low-stock-title">Позиции ниже минимума: ' +
        rows.length +
        "</div>" +
        '  <div class="pc-low-stock-table-wrap">' +
        '    <table class="pc-table">' +
        "      <thead><tr>" +
        "        <th>Товар</th>" +
        "        <th>Тип</th>" +
        "        <th>В наличии</th>" +
        "        <th>Минимум</th>" +
        "        <th>Нехватка</th>" +
        "      </tr></thead>" +
        "      <tbody>" +
        body +
        "      </tbody>" +
        "    </table>" +
        "  </div>" +
        "</section>"
      );
    }

    function renderStockTable(rows) {
      if (!rows || !rows.length) {
        return '<div class="empty-state">Нет данных по остаткам.</div>';
      }

      var body = rows
        .map(function (row) {
          var qtyLabel = row.qtyDisplay || formatQtyDisplay(row.qty, row.itemId);
          var huLabel = row.hu ? row.hu : "-";
          return (
            "<tr>" +
            "<td>" +
            escapeHtml(row.itemName || "-") +
            "</td>" +
            "<td>" +
            escapeHtml(row.brand || "-") +
            "</td>" +
            "<td>" +
            escapeHtml(row.volume || "-") +
            "</td>" +
            "<td>" +
            escapeHtml(row.barcode || "-") +
            "</td>" +
            "<td>" +
            escapeHtml(row.locationCode || "-") +
            "</td>" +
            "<td>" +
            escapeHtml(huLabel) +
            "</td>" +
            '<td><span class="pc-qty">' +
            escapeHtml(String(qtyLabel)) +
            "</span></td>" +
            "</tr>"
          );
        })
        .join("");

      return (
        '<table class="pc-table">' +
        "<thead><tr>" +
        "<th>Товар</th>" +
        "<th>Бренд</th>" +
        "<th>Объем</th>" +
        "<th>SKU / ШК</th>" +
        "<th>Место</th>" +
        "<th>HU</th>" +
        "<th>Кол-во</th>" +
        "</tr></thead>" +
        "<tbody>" +
        body +
        "</tbody>" +
        "</table>"
      );
    }

    function renderLocationRows(rows) {
      return rows
        .map(function (row) {
          var qtyText = String(row.qtyBase != null ? row.qtyBase : 0);
          var locationLabel = formatLocationLabel(row.code, row.name);
          if (row.hu) {
            locationLabel += " - " + row.hu;
          }
          return (
            '<div class="stock-location-row">' +
            '  <div class="stock-location-label">' +
            escapeHtml(locationLabel) +
            "</div>" +
            '  <div class="stock-location-qty">' +
            escapeHtml(qtyText) +
            " шт</div>" +
            "</div>"
          );
        })
        .join("");
    }

    function renderHuRows(rows, uom) {
      if (!rows.length) {
        return '<div class="stock-no-rows">Нет HU-разреза.</div>';
      }
      return rows
        .map(function (row) {
          var hu = row.hu || "-";
          var locationCode = row.locationCode || "";
          var qtyText = String(row.qty != null ? row.qty : 0);
          var line =
            hu +
            " - " +
            qtyText +
            " " +
            (uom || "шт") +
            " (локация: " +
            locationCode +
            ")";
          return '<div class="stock-hu-row">' + escapeHtml(line) + "</div>";
        })
        .join("");
    }

    function getTypeFilterId() {
      if (!typeFilter) {
        return 0;
      }
      return Number(typeFilter.value || 0) || 0;
    }

    function getHuDigitsFilter() {
      if (!huInput) {
        return "";
      }
      var raw = String(huInput.value || "").trim();
      var isValid = /^\d*$/.test(raw);
      if (huInput) {
        huInput.classList.toggle("form-input-error", !isValid);
      }
      if (huHint) {
        huHint.hidden = isValid;
      }
      return isValid ? raw : "";
    }

    function matchesStockSearch(row, normalizedQuery) {
      if (!normalizedQuery) {
        return true;
      }
      var fields = [
        row.itemName,
        row.brand,
        row.volume,
        row.barcode,
        row.gtin,
        row.locationCode,
        row.hu,
        row.itemTypeName,
      ];
      for (var i = 0; i < fields.length; i += 1) {
        var value = String(fields[i] || "").toLowerCase();
        if (value.indexOf(normalizedQuery) !== -1) {
          return true;
        }
      }
      return false;
    }

    function buildLowStockRows(typeId) {
      var totalsByItem = {};
      cachedStockRows.forEach(function (row) {
        var itemId = Number(row.itemId) || 0;
        if (!itemId) {
          return;
        }
        totalsByItem[itemId] = (totalsByItem[itemId] || 0) + normalizeQty(row.qty);
      });

      return Object.keys(cachedItemsById)
        .map(function (key) {
          return cachedItemsById[Number(key)];
        })
        .filter(function (item) {
          if (!item) {
            return false;
          }
          if (typeId && Number(item.itemTypeId) !== typeId) {
            return false;
          }
          if (!(item.itemTypeEnableMinStockControl === true) || item.minStockQty == null) {
            return false;
          }

          var itemId = Number(item.itemId) || 0;
          var qty = normalizeQty(totalsByItem[itemId] || 0);
          return qty < normalizeQty(item.minStockQty);
        })
        .map(function (item) {
          var itemId = Number(item.itemId) || 0;
          var qty = normalizeQty(totalsByItem[itemId] || 0);
          var minStockQty = normalizeQty(item.minStockQty);
          var shortage = Math.max(0, minStockQty - qty);
          return {
            itemName: item.name || "-",
            itemTypeName: item.itemTypeName || "-",
            qtyDisplay: formatQtyDisplay(qty, itemId),
            minStockDisplay: formatQtyDisplay(minStockQty, itemId),
            shortageDisplay: formatQtyDisplay(shortage, itemId),
            shortage: shortage,
          };
        })
        .sort(function (a, b) {
          if (b.shortage !== a.shortage) {
            return b.shortage - a.shortage;
          }
          return String(a.itemName || "").localeCompare(String(b.itemName || ""), "ru");
        });
    }

    function fillTypeFilter() {
      if (!typeFilter) {
        return;
      }

      var previous = String(typeFilter.value || "");
      var options =
        '<option value="">Все типы</option>' +
        cachedItemTypes
          .slice()
          .sort(function (left, right) {
            var leftOrder = Number(left && left.sortOrder) || Number(left && left.sort_order) || 0;
            var rightOrder = Number(right && right.sortOrder) || Number(right && right.sort_order) || 0;
            if (leftOrder !== rightOrder) {
              return leftOrder - rightOrder;
            }
            var leftName = String((left && left.name) || "").toLowerCase();
            var rightName = String((right && right.name) || "").toLowerCase();
            return leftName < rightName ? -1 : leftName > rightName ? 1 : 0;
          })
          .map(function (type) {
            var id = Number(type && (type.itemTypeId != null ? type.itemTypeId : type.id)) || 0;
            var name = String((type && type.name) || "").trim() || "Без названия";
            return '<option value="' + escapeHtml(String(id)) + '">' + escapeHtml(name) + "</option>";
          })
          .join("");

      typeFilter.innerHTML = options;
      if (previous) {
        var hasPrevious = Array.prototype.some.call(typeFilter.options || [], function (option) {
          return String(option.value || "") === previous;
        });
        if (hasPrevious) {
          typeFilter.value = previous;
        }
      }
    }

    function fillFilters() {
      if (locationSelect) {
        var options =
          '<option value="">Все места</option>' +
          Object.keys(locationMap)
            .map(function (key) {
              return locationMap[Number(key)];
            })
            .filter(function (loc) {
              return !!loc;
            })
            .map(function (loc) {
              var label = loc.code ? loc.code + " — " + (loc.name || "") : loc.name || "";
              return (
                '<option value="' +
                escapeHtml(String(loc.locationId)) +
                '">' +
                escapeHtml(label) +
                "</option>"
              );
            })
            .join("");
        locationSelect.innerHTML = options;
      }

      fillTypeFilter();
    }

    function renderLowStock() {
      if (!lowWrap) {
        return;
      }
      var lowRows = buildLowStockRows(getTypeFilterId());
      lowWrap.innerHTML = renderLowStockTable(lowRows);
    }

    function renderRows() {
      if (!tableWrap) {
        return;
      }

      var query = String(stockSearchInput && stockSearchInput.value ? stockSearchInput.value : "")
        .trim()
        .toLowerCase();
      var locationId = locationSelect ? Number(locationSelect.value || 0) : 0;
      var typeId = getTypeFilterId();
      var huDigits = getHuDigitsFilter();
      var source = cachedCombinedRows.length ? cachedCombinedRows : cachedStockRows;

      var rows = source.filter(function (row) {
        if (locationId && Number(row.locationId) !== locationId) {
          return false;
        }
        if (typeId && Number(row.itemTypeId) !== typeId) {
          return false;
        }
        var huRaw = String(row.hu || "");
        if (huDigits && huRaw.indexOf(huDigits) === -1) {
          return false;
        }
        return matchesStockSearch(row, query);
      });

      setRowsStatus("Строк: " + rows.length);
      renderLowStock();
      tableWrap.innerHTML = renderStockTable(rows);
    }

    function scheduleRender() {
      if (stockRenderTimer) {
        window.clearTimeout(stockRenderTimer);
      }
      stockRenderTimer = window.setTimeout(renderRows, 120);
    }

    function loadStockData() {
      return Promise.all([
        TsdStorage.apiSearchItems(""),
        TsdStorage.apiGetLocations(),
        TsdStorage.apiGetStockRows(),
        TsdStorage.apiGetHuStockRows(),
        TsdStorage.apiGetItemTypes(false),
      ]).then(function (payloads) {
        var items = Array.isArray(payloads[0]) ? payloads[0] : [];
        var locations = Array.isArray(payloads[1]) ? payloads[1] : [];
        var stockRows = Array.isArray(payloads[2]) ? payloads[2] : [];
        var huRows = Array.isArray(payloads[3]) ? payloads[3] : [];
        var itemTypes = Array.isArray(payloads[4]) ? payloads[4] : [];

        setCachedItems(items);
        setCachedLocations(locations);
        setCachedItemTypes(itemTypes);

        cachedStockRows = stockRows.map(function (row) {
          var item = cachedItemsById[Number(row.item_id)] || {};
          var location = locationMap[Number(row.location_id)] || {};
          var qty = normalizeQty(row.qty);
          return {
            itemId: Number(row.item_id) || 0,
            locationId: Number(row.location_id) || 0,
            qty: qty,
            qtyDisplay: formatQtyDisplay(qty, row.item_id),
            itemName: item.name || row.item_name || "-",
            barcode: item.barcode || row.barcode || "",
            gtin: item.gtin || row.gtin || "",
            brand: item.brand || row.brand || "",
            volume: item.volume || row.volume || "",
            itemTypeId: Number(item.itemTypeId || row.item_type_id) || 0,
            itemTypeName: item.itemTypeName || row.item_type_name || "",
            locationCode: location.code || row.location_code || "",
            hu: row.hu || "",
          };
        });

        cachedHuRows = huRows.map(function (row) {
          var item = cachedItemsById[Number(row.itemId)] || {};
          var location = locationMap[Number(row.locationId)] || {};
          var qty = normalizeQty(row.qty);
          return {
            itemId: Number(row.itemId) || 0,
            locationId: Number(row.locationId) || 0,
            qty: qty,
            qtyDisplay: formatQtyDisplay(qty, row.itemId),
            itemName: item.name || "-",
            barcode: item.barcode || "",
            gtin: item.gtin || "",
            brand: item.brand || "",
            volume: item.volume || "",
            itemTypeId: Number(item.itemTypeId) || 0,
            itemTypeName: item.itemTypeName || "",
            locationCode: location.code || "",
            hu: row.hu || "",
          };
        });

        buildCombinedRows();
        fillFilters();
        renderRows();
        stockDataLoaded = true;
      });
    }

    function renderDetails(item, rows, huRows) {
      if (!detailsEl) {
        return;
      }
      var total = rows.reduce(function (sum, row) {
        return sum + (row.qtyBase || 0);
      }, 0);
      var skuParts = [];
      if (item.sku) {
        skuParts.push("SKU: " + item.sku);
      }
      if (item.gtin) {
        skuParts.push("GTIN: " + item.gtin);
      }
      var skuLine = skuParts.join(" · ");
      var metaParts = [];
      if (item.brand) {
        metaParts.push("Бренд: " + item.brand);
      }
      if (item.volume) {
        metaParts.push("Объем: " + item.volume);
      }
      var metaLine = metaParts.join(" · ");
      var uom = item.base_uom || item.base_uom_code || "шт";
      var locationsHtml = rows.length
        ? renderLocationRows(rows)
        : '<div class="stock-no-rows">Нет остатков по данным выгрузки.</div>';
      var huHtml = "";
      if (huRows) {
        huHtml =
          '<div class="stock-hu-block">' +
          '<div class="stock-hu-title">По HU</div>' +
          renderHuRows(huRows, uom) +
          "</div>";
      }
      detailsEl.innerHTML =
        '<div class="stock-card">' +
        '<div class="stock-title">' +
        escapeHtml(item.name || "-") +
        "</div>" +
        (skuLine
          ? '<div class="stock-subtitle">' + escapeHtml(skuLine) + "</div>"
          : "") +
        (metaLine
          ? '<div class="stock-subtitle">' + escapeHtml(metaLine) + "</div>"
          : "") +
        '<div class="stock-total">Итого: ' +
        escapeHtml(total) +
        " шт</div>" +
        '<div class="stock-locations">' +
        locationsHtml +
        "</div>" +
        huHtml +
        "</div>";
    }

    function loadLocationNameMap() {
      if (Object.keys(locationMap).length) {
        return Promise.resolve(locationMap);
      }
      return TsdStorage.apiGetLocations()
        .then(function (locations) {
          var map = {};
          locations.forEach(function (location) {
            map[location.locationId] = location;
          });
          locationMap = map;
          return map;
        })
        .catch(function () {
          locationMap = {};
          return locationMap;
        });
    }

    function ensureItemsMap() {
      if (Object.keys(itemsById).length) {
        return Promise.resolve(itemsById);
      }
      if (itemsLoading) {
        return itemsLoading;
      }
      itemsLoading = TsdStorage.apiSearchItems("")
        .then(function (items) {
          var map = {};
          (items || []).forEach(function (item) {
            map[item.itemId] = item;
          });
          itemsById = map;
          return itemsById;
        })
        .catch(function () {
          itemsById = {};
          return itemsById;
        })
        .finally(function () {
          itemsLoading = null;
        });
      return itemsLoading;
    }

    function showStockItem(item) {
      if (!item) {
        setStockMessage("Товар не найден в данных");
        return;
      }
      var barcode = item.barcode || item.gtin;
      if (!barcode) {
        setStockMessage("У товара нет штрихкода");
        return;
      }
      setStockMessage("");
      Promise.all([TsdStorage.apiGetStockByBarcode(barcode), loadLocationNameMap()])
        .then(function (result) {
          var stock = result[0] || {};
          var locationMap = result[1] || {};
          var rows = (stock.totalsByLocation || []).map(function (row) {
            var location = locationMap[row.locationId] || {};
            return {
              qtyBase: row.qty,
              code: row.locationCode || location.code || "",
              name: location.name || "",
              hu: null,
            };
          });
          var huRows = (stock.byHu || []).map(function (row) {
            var location = locationMap[row.locationId] || {};
            return {
              hu: row.hu,
              qty: row.qty,
              locationCode: row.locationCode || location.code || "",
            };
          });
          renderDetails(item, rows, huRows);
        })
        .catch(function () {
          setStockMessage("Ошибка получения остатков");
        });
    }

    function formatQty(value) {
      var num = Number(value);
      if (isNaN(num)) {
        return "0";
      }
      return String(num);
    }

    function getHuStockRows() {
      var now = Date.now();
      if (huStockCache && now - huStockCachedAt < HU_STOCK_TTL_MS) {
        return Promise.resolve(huStockCache);
      }
      return TsdStorage.apiGetHuStockRows()
        .then(function (rows) {
          huStockCache = rows || [];
          huStockCachedAt = Date.now();
          return huStockCache;
        })
        .catch(function () {
          return [];
        });
    }

    function renderHuDetails(huCode, rows) {
      if (!detailsEl) {
        return;
      }
      if (!rows.length) {
        detailsEl.innerHTML = "";
        return;
      }
        var linesHtml = rows
          .map(function (row) {
            var item = itemsById[row.itemId] || {};
            var location = locationMap[row.locationId] || {};
            var itemName = item.name || ("ID " + row.itemId);
            var skuValue = item.sku || item.barcode || (Array.isArray(item.barcodes) ? item.barcodes[0] : "") || item.gtin || "";
            var skuLabel = skuValue ? "SKU: " + skuValue : "";
            var qtyLabel = formatQty(row.qty);
            var uomLabel = item.base_uom || item.base_uom_code || "шт";
            var locationLabel = location.code ? " (" + location.code + ")" : "";
            return (
              '<div class="hu-line">' +
              "<div>" +
              escapeHtml(itemName + locationLabel) +
              (skuLabel
                ? '<div class="line-barcode">' + escapeHtml(skuLabel) + "</div>"
                : "") +
              "</div>" +
              "<div>" +
              escapeHtml(qtyLabel + " " + uomLabel) +
              "</div>" +
            "</div>"
          );
        })
        .join("");

      detailsEl.innerHTML =
        '<div class="hu-card">' +
        '<div class="hu-card-title">HU: ' +
        escapeHtml(huCode) +
        "</div>" +
        '<div class="hu-lines">' +
        linesHtml +
        "</div>" +
        "</div>";
    }

    function showHuStock(rawHu) {
      if (!dataReady) {
        setStockMessage("Нет связи с сервером");
        return;
      }
      var normalized = normalizeHuCode(rawHu);
      if (!normalized) {
        setStockMessage("Неверный код HU");
        return;
      }
      setStockMessage("");
      clearDetails();
      Promise.all([getHuStockRows(), loadLocationNameMap(), ensureItemsMap()])
        .then(function (result) {
          var rows = result[0] || [];
          var filtered = rows.filter(function (row) {
            return String(row.hu || "").toUpperCase() === normalized;
          });
          if (!filtered.length) {
            setStockMessage("HU не найден");
            return;
          }
          renderHuDetails(normalized, filtered);
        })
        .catch(function () {
          setStockMessage("Ошибка получения HU");
        });
    }

    function handleStockSearch(value) {
      var trimmed = String(value || "").trim();
      if (!trimmed || !dataReady) {
        return;
      }
      setStockMessage("Ищем...");
      TsdStorage.apiFindItemByCode(trimmed)
        .then(function (item) {
          if (item) {
            itemsById[item.itemId] = item;
            showStockItem(item);
          } else {
            setStockMessage("Товар не найден в данных");
          }
        })
        .catch(function () {
          setStockMessage("Ошибка поиска");
        });
    }

    function updateDataStatus() {
      pingServer(true)
        .then(function (ok) {
          dataReady = !!ok;
          setStatusText(ok ? "Онлайн" : "Нет связи с сервером");
          if (!ok) {
            setStockMessage("Нет связи с сервером");
            setRowsStatus("");
            if (lowWrap) {
              lowWrap.innerHTML = "";
            }
            if (tableWrap) {
              tableWrap.innerHTML = "";
            }
            clearDetails();
            return;
          }
          setStockMessage("");
          return loadStockData()
            .then(function () {
              renderRows();
            })
            .catch(function () {
              setRowsStatus("Ошибка загрузки списка");
              if (lowWrap) {
                lowWrap.innerHTML = "";
              }
              if (tableWrap) {
                tableWrap.innerHTML = '<div class="empty-state">Данные недоступны.</div>';
              }
            });
        })
        .catch(function () {
          setStatusText("Нет связи с сервером");
          setStockMessage("Нет связи с сервером");
          setRowsStatus("");
          if (lowWrap) {
            lowWrap.innerHTML = "";
          }
          if (tableWrap) {
            tableWrap.innerHTML = "";
          }
          dataReady = false;
          clearDetails();
        });
    }

    function clearScanInput() {
      if (scanInput) {
        scanInput.value = "";
      }
      lastStockValue = "";
    }

    function handleScannedValue(value) {
      var trimmed = normalizeValue(value);
      if (!trimmed) {
        return;
      }
      if (scanInput) {
        lastStockValue = trimmed;
        scanInput.value = trimmed;
      }
      if (normalizeHuCode(trimmed)) {
        showHuStock(trimmed);
        clearScanInput();
        return;
      }
      handleStockSearch(trimmed);
      clearScanInput();
    }

    function handleScanEvent(scan) {
      var value = scan && scan.value ? scan.value : scan;
      handleScannedValue(value);
    }

    setScanHandler(handleScanEvent);

    if (scanBtn) {
      scanBtn.addEventListener("click", function () {
        clearScanInput();
        enterScanMode();
      });
    }

    if (scanInput) {
      setPreferredScanTarget(scanInput);
      scanInput.addEventListener("input", function () {
        if (scanInput.value !== lastStockValue) {
          scanInput.value = lastStockValue;
        }
      });
      scanInput.addEventListener("keydown", function (event) {
        if (event.key === "Enter") {
          event.preventDefault();
          handleScannedValue(scanInput.value);
          return;
        }
      });
    }

    if (stockSearchInput) {
      stockSearchInput.addEventListener("input", scheduleRender);
    }

    if (locationSelect) {
      locationSelect.addEventListener("change", renderRows);
    }

    if (typeFilter) {
      typeFilter.addEventListener("change", renderRows);
    }

    if (huInput) {
      huInput.addEventListener("input", scheduleRender);
    }

    if (clearBtn) {
      clearBtn.addEventListener("click", function () {
        if (scanInput) {
          scanInput.value = "";
        }
        if (stockSearchInput) {
          stockSearchInput.value = "";
        }
        if (locationSelect) {
          locationSelect.value = "";
        }
        if (typeFilter) {
          typeFilter.value = "";
        }
        if (huInput) {
          huInput.value = "";
          huInput.classList.remove("form-input-error");
        }
        if (huHint) {
          huHint.hidden = true;
        }
        setRowsStatus("");
        clearDetails();
        setStockMessage("");
        renderRows();
      });
    }

    updateDataStatus();
    ensureScanFocus();
  }

  function getDocTotals(lines) {
    var totals = { count: 0, qty: 0 };
    if (!Array.isArray(lines)) {
      return totals;
    }
    totals.count = lines.length;
    for (var i = 0; i < lines.length; i += 1) {
      var qty = Number(lines[i].qty) || 0;
      totals.qty += qty;
    }
    return totals;
  }

  function renderDoc(doc) {
    var headerFields = renderHeaderFields(doc);
    var linesHtml = renderLines(doc);
    var pickListHtml = renderOutboundPickList(doc);
    var totals = getDocTotals(doc.lines);
    var totalsHtml =
      '    <div class="doc-totals">' +
      '      <div class="doc-totals-item">Позиций: <span id="docTotalCount">' +
      totals.count +
      "</span></div>" +
      '      <div class="doc-totals-item">Всего: <span id="docTotalQty">' +
      totals.qty +
      "</span> шт</div>" +
      "    </div>";
    var statusLabel = STATUS_LABELS[doc.status] || doc.status;
    var statusClass = "status-" + String(doc.status || "").toLowerCase();
    var isDraft = doc.status === "DRAFT";
    var isReady = doc.status === "READY";
    var isExported = doc.status === "EXPORTED";
    var opLabel = OPS[doc.op] ? OPS[doc.op].label : doc.op;
    var docRefValue = doc.doc_ref || "";
    var docRefDisplay = docRefValue ? escapeHtml(docRefValue) : "—";
    var docRefInputHtml = '<span class="doc-ref-text">' + docRefDisplay + "</span>";

    return (
      '<section class="screen">' +
      '  <div class="screen-card">' +
      '    <div class="doc-header">' +
      '      <div class="doc-head-top">' +
      '        <div class="doc-titleblock">' +
      '          <div class="doc-header-title">' +
      escapeHtml(opLabel) +
      "</div>" +
      '          <div class="doc-ref-line">№ ' +
      docRefInputHtml +
      "</div>" +
      "        </div>" +
      '        <div class="status-pill ' +
      escapeHtml(statusClass) +
      '">' +
      escapeHtml(statusLabel) +
      "</div>" +
      "      </div>" +
      "    </div>" +
      '    <div class="form-grid doc-form-grid">' +
      headerFields +
      "    </div>" +
      (isExported
        ? '<div class="notice">Документ экспортирован, редактирование недоступно.</div>'
        : "") +
      '    <div class="section-subtitle">Сканирование</div>' +
      renderScanBlock(doc, isDraft) +
      '    <div class="section-subtitle">Строки</div>' +
      '    <div class="lines-section">' +
      linesHtml +
      totalsHtml +
      "    </div>" +
      (pickListHtml
        ? '    <div class="section-subtitle">Где лежит</div>' + pickListHtml
        : "") +
      '    <div id="docActionStatus" class="status"></div>' +
      '    <div class="actions-bar">' +
      '      <button class="btn btn-outline" id="undoBtn" ' +
      (doc.undoStack && doc.undoStack.length && isDraft ? "" : "disabled") +
      ">Undo</button>" +
      (isDraft
        ? '      <button class="btn primary-btn" id="finishBtn">Завершить</button>'
        : "") +
      (isReady
        ? '      <button class="btn btn-outline" id="revertBtn">Вернуть в черновик</button>'
        : "") +
      "    </div>" +
      "  </div>" +
      "</section>"
    );
  }

  function wireHuLookup() {
    var scanBtn = document.getElementById("huLookupScanBtn");
    var statusEl = document.getElementById("huLookupStatus");
    var messageEl = document.getElementById("huLookupMessage");
    var detailsEl = document.getElementById("huLookupDetails");
    var lookupOverlay = null;
    var lookupInput = null;
    var lookupError = null;
    var lookupConfirm = null;
    var lookupValue = null;
    var lastHuRawInput = "";
    var lastLookupValue = "";
    var overlayKeyListener = null;

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text || "";
      }
    }

    function setMessage(text) {
      if (!messageEl) {
        return;
      }
      messageEl.textContent = text || "";
    }

    function formatQty(value) {
      var num = Number(value);
      if (isNaN(num)) {
        return "0";
      }
      return String(num);
    }

    function renderDetails(huCode, entry, itemsMap) {
      if (!detailsEl) {
        return;
      }
      if (!entry || !Array.isArray(entry.rows) || !entry.rows.length) {
        detailsEl.innerHTML = "";
        return;
      }
        var linesHtml = entry.rows.map(function (row) {
          var item = itemsMap[row.itemId] || {};
          var itemName = item.name || ("ID " + row.itemId);
          var skuValue = item.sku || item.barcode || "";
          var skuLabel = skuValue ? "SKU: " + skuValue : "";
          var baseUom = item.base_uom || "";
          var qtyLabel = formatQty(row.qtyBase);
          var uomLabel = baseUom ? " " + baseUom : "";
          return (
            '<div class="hu-line">' +
            "<div>" +
            escapeHtml(itemName) +
            (skuLabel
              ? '<div class="line-barcode">' + escapeHtml(skuLabel) + "</div>"
              : "") +
            "</div>" +
            "<div>" +
            escapeHtml(qtyLabel + uomLabel) +
            "</div>" +
          "</div>"
        );
      }).join("");

      detailsEl.innerHTML =
        '<div class="hu-card">' +
        '  <div class="hu-card-title">' +
        escapeHtml(huCode) +
        "</div>" +
        '  <div class="hu-card-meta">Данные по ПК на момент экспорта: ' +
        escapeHtml(entry.exportedAt || "-") +
        "</div>" +
        '  <div class="hu-lines">' +
        linesHtml +
        "</div>" +
        "</div>";
    }

    function loadHuDetails(huCode) {
      setMessage("");
      var normalized = extractHuCode(huCode);
      if (!normalized) {
        var rawValue = normalizeValue(huCode).toUpperCase();
        if (!rawValue) {
          rawValue = lastHuRawInput;
        }
        setMessage(
          rawValue
            ? "Неверный формат HU: " + rawValue
            : "Неверный формат HU."
        );
        if (window.console) {
          console.log("[hu] invalid code", { raw: rawValue, input: huCode });
        }
        return;
      }

      TsdStorage.getHuStockByCode(normalized)
        .then(function (entry) {
          if (!entry) {
            setStatus("Данные по ПК: отсутствуют");
            detailsEl.innerHTML = "";
            setMessage("HU не найден в снимке.");
            return;
          }
          setStatus(
            "Данные по ПК на момент экспорта: " + (entry.exportedAt || "-")
          );
          var itemIds = [];
          entry.rows.forEach(function (row) {
            if (row.itemId != null && itemIds.indexOf(row.itemId) === -1) {
              itemIds.push(row.itemId);
            }
          });
          TsdStorage.getItemsByIds(itemIds)
            .then(function (itemsMap) {
              renderDetails(normalized, entry, itemsMap || {});
            })
            .catch(function () {
              renderDetails(normalized, entry, {});
            });
        })
        .catch(function () {
          setMessage("Ошибка чтения данных HU.");
        });
    }

    function extractHuCode(value) {
      var normalized = normalizeValue(value).toUpperCase();
      if (!normalized) {
        return "";
      }
      normalized = normalized
        .replace(/\u041D/g, "H") // Cyrillic Н -> Latin H
        .replace(/\u0423/g, "U"); // Cyrillic У -> Latin U
      var cleaned = normalized.replace(/[^A-Z0-9-]/g, "");
      if (!cleaned) {
        return "";
      }
      var match = cleaned.match(/HU-?\d{6}/);
      if (!match) {
        if (window.console) {
          console.log("[hu] no match", { normalized: normalized, cleaned: cleaned });
        }
        return "";
      }
      var digits = match[0].replace(/[^0-9]/g, "");
      if (digits.length !== 6) {
        return "";
      }
      return "HU-" + digits;
    }

    function isValidHuFormat(value) {
      return !!extractHuCode(value);
    }

    function setLookupValue(rawValue, showError) {
      if (!lookupInput) {
        return;
      }
      var rawNormalized = normalizeValue(rawValue).toUpperCase();
      lastHuRawInput = rawNormalized;
      var trimmed = extractHuCode(rawValue);
      if (!trimmed) {
        lookupValue = null;
        lookupInput.value = rawNormalized;
        lastLookupValue = lookupInput.value;
        if (lookupError) {
          lookupError.textContent =
            showError && rawNormalized
              ? "Неверный формат HU: " + rawNormalized
              : "";
        }
        if (lookupConfirm) {
          lookupConfirm.disabled = true;
        }
        if (showError && window.console) {
          console.log("[hu] scan raw", rawNormalized);
        }
        return;
      }

      lookupValue = trimmed;
      lookupInput.value = trimmed;
      lastLookupValue = lookupInput.value;
      if (lookupError) {
        lookupError.textContent = "Найден HU: " + trimmed;
      }
      if (lookupConfirm) {
        lookupConfirm.disabled = false;
      }
    }

    function handleScanEvent(scan) {
      if (!lookupOverlay) {
        return;
      }
      var value = scan && scan.value ? scan.value : scan;
      setLookupValue(value, true);
    }

    function openLookupOverlay() {
      if (lookupOverlay) {
        return;
      }
      lookupOverlay = document.createElement("div");
      lookupOverlay.className = "overlay hu-lookup-overlay";
      lookupOverlay.setAttribute("tabindex", "-1");
      lookupOverlay.setAttribute("data-scan-allow", "1");
      lookupOverlay.innerHTML =
        '<div class="overlay-card">' +
        '  <div class="overlay-header">' +
        '    <div class="overlay-title">Сканировать HU</div>' +
        '    <button class="btn btn-ghost overlay-close" type="button">Закрыть</button>' +
        "  </div>" +
        '  <div class="overlay-body">' +
        '    <div class="form-label">Отсканируйте HU-код</div>' +
        '    <input class="form-input" id="huLookupInput" type="text" placeholder="HU-000001" autocomplete="off" autocapitalize="off" autocorrect="off" spellcheck="false" data-scan-allow="1" />' +
        '    <div class="field-error" id="huLookupError"></div>' +
        '    <div class="overlay-actions">' +
        '      <button class="btn btn-outline" type="button" id="huLookupCancel">Отмена</button>' +
        '      <button class="btn primary-btn" type="button" id="huLookupConfirm" disabled>Показать</button>' +
        "    </div>" +
        "  </div>" +
        "</div>";

      document.body.appendChild(lookupOverlay);
      lockOverlayScroll();
      focusOverlay(lookupOverlay);

      lookupInput = lookupOverlay.querySelector("#huLookupInput");
      lookupError = lookupOverlay.querySelector("#huLookupError");
      lookupConfirm = lookupOverlay.querySelector("#huLookupConfirm");
      var cancelBtn = lookupOverlay.querySelector("#huLookupCancel");
      var closeBtn = lookupOverlay.querySelector(".overlay-close");

      function closeOverlay() {
        unlockOverlayScroll();
        document.body.removeChild(lookupOverlay);
        lookupOverlay = null;
        lookupInput = null;
        lookupError = null;
        lookupConfirm = null;
        lookupValue = null;
        setPreferredScanTarget(null);
        if (overlayKeyListener) {
          document.removeEventListener("keydown", overlayKeyListener);
          overlayKeyListener = null;
        }
      }

      function confirmLookup() {
        if (!lookupValue) {
          return;
        }
        var confirmedValue = lookupValue;
        closeOverlay();
        loadHuDetails(confirmedValue);
      }

      if (lookupInput) {
        setPreferredScanTarget(lookupInput);
        lookupInput.addEventListener("input", function () {
          if (lookupInput.value !== lastLookupValue) {
            lookupInput.value = lastLookupValue;
          }
          setLookupValue(lookupInput.value, true);
        });
        lookupInput.addEventListener("keydown", function (event) {
          if (event.key === "Enter" && lookupConfirm && !lookupConfirm.disabled) {
            event.preventDefault();
            confirmLookup();
            return;
          }
        });
      }
      if (cancelBtn) {
        cancelBtn.addEventListener("click", closeOverlay);
      }
      if (closeBtn) {
        closeBtn.addEventListener("click", closeOverlay);
      }
      if (lookupConfirm) {
        lookupConfirm.addEventListener("click", confirmLookup);
      }

      overlayKeyListener = function (event) {
        if (event.key === "Escape") {
          closeOverlay();
        }
        if (event.key === "Enter" && lookupConfirm && !lookupConfirm.disabled) {
          event.preventDefault();
          confirmLookup();
        }
      };
      document.addEventListener("keydown", overlayKeyListener);

      setLookupValue("", false);
      if (lookupInput) {
        lookupInput.focus();
      }
    }

    setScanHandler(handleScanEvent);

    if (scanBtn) {
      scanBtn.addEventListener("click", function () {
        openLookupOverlay();
      });
    }
  }

  function renderHeaderFields(doc) {
    var header = doc.header || {};
    var isDraft = doc.status === "DRAFT";
    if (doc.op === "INBOUND") {
      var inboundPartnerValue = header.partner || "Не выбран";
      var inboundToValue = formatLocationLabel(header.to, header.to_name);
      return (
        '<div class="header-fields">' +
        renderPickerRow({
          label: "Поставщик",
          value: inboundPartnerValue,
          valueId: "partnerValue",
          pickId: "partnerPickBtn",
          disabled: !isDraft,
        }) +
        '  <div class="field-error" id="partnerError"></div>' +
        renderPickerRow({
          label: "Куда",
          value: inboundToValue,
          valueId: "toValue",
          pickId: "toPickBtn",
          disabled: !isDraft,
        }) +
        '  <div class="field-error" id="toError"></div>' +
        renderHuField(header, isDraft) +
        "</div>"
      );
    }

    if (doc.op === "PRODUCTION_RECEIPT") {
      var receiptToValue = formatLocationLabel(header.to, header.to_name);
      return (
        '<div class="header-fields">' +
        renderPickerRow({
          label: "Куда",
          value: receiptToValue,
          valueId: "toValue",
          pickId: "toPickBtn",
          disabled: !isDraft,
        }) +
        '  <div class="field-error" id="toError"></div>' +
        renderHuField(header, isDraft) +
        "</div>"
      );
    }

    if (doc.op === "OUTBOUND") {
      var outboundPartnerValue = header.partner || "Не выбран";
      var outboundFromValue = formatLocationLabel(header.from, header.from_name);
      var outboundOrderValue = formatOrderLabel(header.order_ref);
      var outboundOrderId = header.order_id || null;
      return (
        '<div class="header-fields">' +
        renderPickerRow({
          label: "Покупатель",
          value: outboundPartnerValue,
          valueId: "partnerValue",
          pickId: "partnerPickBtn",
          disabled: !isDraft,
        }) +
        '  <div class="field-error" id="partnerError"></div>' +
        renderPickerRow({
          label: "Откуда",
          value: outboundFromValue,
          valueId: "fromValue",
          pickId: "fromPickBtn",
          disabled: !isDraft,
        }) +
        '  <div class="field-error" id="fromError"></div>' +
        '  <div class="field-row field-row-4">' +
        '    <div class="field-label">Заказ</div>' +
        '    <div class="field-value" id="orderValue">' +
        escapeHtml(outboundOrderValue) +
        "</div>" +
        '    <button class="btn btn-outline field-info-btn" id="orderInfoBtn" type="button" ' +
        (outboundOrderId ? "" : "disabled") +
        '>i</button>' +
        '    <button class="btn btn-outline field-pick" id="orderPickBtn" type="button" ' +
        (isDraft ? "" : "disabled") +
        ">+</button>" +
        "  </div>" +
        renderHuField(header, isDraft) +
        "</div>"
      );
    }

    if (doc.op === "MOVE") {
      var moveFromValue = formatLocationLabel(header.from, header.from_name);
      var moveToValue = formatLocationLabel(header.to, header.to_name);
      return (
        '<div class="header-fields">' +
        renderPickerRow({
          label: "Откуда",
          value: moveFromValue,
          valueId: "fromValue",
          pickId: "fromPickBtn",
          disabled: !isDraft,
        }) +
        '  <div class="field-error" id="fromError"></div>' +
        renderPickerRow({
          label: "Куда",
          value: moveToValue,
          valueId: "toValue",
          pickId: "toPickBtn",
          disabled: !isDraft,
        }) +
        '  <div class="field-error" id="toError"></div>' +
        renderMoveInternalRow(header, isDraft) +
        renderMoveHuField("С HU", "huFromValue", "huFromScanBtn", "huFromError", header.from_hu, isDraft) +
        renderMoveHuField("На HU", "huToValue", "huToScanBtn", "huToError", header.to_hu, isDraft) +
        "</div>"
      );
    }
    if (doc.op === "WRITE_OFF") {
      var writeoffFromValue = formatLocationLabel(header.from, header.from_name);
      var currentReasonLabel = getReasonLabel(header.reason_code) || "Не выбрана";
      return (
        '<div class="header-fields">' +
        renderPickerRow({
          label: "Откуда",
          value: writeoffFromValue,
          valueId: "fromValue",
          pickId: "fromPickBtn",
          disabled: !isDraft,
        }) +
        '  <div class="field-error" id="fromError"></div>' +
        renderPickerRow({
          label: "Причина",
          value: currentReasonLabel,
          valueId: "reasonValue",
          pickId: "reasonPickBtn",
          disabled: !isDraft,
        }) +
        '  <div class="field-error" id="reasonError"></div>' +
        renderHuField(header, isDraft) +
        "</div>"
      );
    }

    if (doc.op === "INVENTORY") {
      var inventoryValue = formatLocationLabel(header.location, header.location_name);
      return (
        '<div class="header-fields">' +
        renderPickerRow({
          label: "Локация",
          value: inventoryValue,
          valueId: "locationValue",
          pickId: "locationPickBtn",
          disabled: !isDraft,
        }) +
        '  <div class="field-error" id="locationError"></div>' +
        renderHuField(header, isDraft) +
        "</div>"
      );
    }

    return "";
  }

  function renderScanBlock(doc, isDraft) {
    var qtyMode = doc.header && doc.header.qtyMode === "INC1" ? "INC1" : "ASK";
    return (
      '<div class="scan-card">' +
      '  <label class="form-label" for="barcodeInput">Штрихкод</label>' +
      '  <div class="qty-mode-toggle">' +
      '    <button class="btn qty-mode-btn' +
      (qtyMode === "INC1" ? " is-active" : "") +
      '" data-mode="INC1" type="button">+1</button>' +
      '    <button class="btn qty-mode-btn' +
      (qtyMode === "ASK" ? " is-active" : "") +
      '" data-mode="ASK" type="button">Кол-во</button>' +
      "  </div>" +
      '  <div class="scan-input-row">' +
      '    <input class="form-input scan-input" id="barcodeInput" type="text" autocomplete="off" autocapitalize="off" autocorrect="off" spellcheck="false" data-scan-allow="1" ' +
      (isDraft ? "" : "disabled") +
      " />" +
      '    <button class="btn btn-outline kbd-btn" data-manual="barcode" type="button" aria-label="Ручной ввод" ' +
      (isDraft ? "" : "disabled") +
      ">⌨</button>" +
      "  </div>" +
      '  <div id="scanItemInfo" class="scan-info"></div>' +
      '  <button class="btn primary-btn" id="addLineBtn" type="button" ' +
      (isDraft ? "" : "disabled") +
      ">Добавить</button>" +
      "</div>"
    );
  }

  function renderLines(doc) {
    var lines = doc.lines || [];
    if (!lines.length) {
      return '<div class="empty-state">Добавьте товары сканированием.</div>';
    }
    var showInventoryHu = doc.op === "INVENTORY";
    var selectedIndex =
      typeof doc.selectedLineIndex === "number" ? doc.selectedLineIndex : -1;

    var rows = lines
      .map(function (line, index) {
        var nameText = line.itemName ? line.itemName : "Неизвестный код";
        var qtyValue = Number(line.qty) || 0;
        var minusDisabledAttr = qtyValue <= 1 ? ' disabled' : "";
        var minusClassDisabled = qtyValue <= 1 ? " is-disabled" : "";
        var lineHu = showInventoryHu ? normalizeHuCode(line.to_hu) : null;
        var rowClass = "lines-row" + (index === selectedIndex ? " is-selected" : "");
        return (
          '<div class="' +
          rowClass +
          '" data-line-index="' +
          index +
          '">' +
          '  <div class="lines-cell">' +
          '    <div class="line-name">' +
          escapeHtml(nameText) +
          "</div>" +
          '    <div class="line-barcode">' +
          escapeHtml(line.barcode) +
          "</div>" +
          (lineHu ? '    <div class="line-barcode">HU: ' + escapeHtml(lineHu) + "</div>" : "") +
          "</div>" +
          '  <div class="lines-cell line-actions">' +
          '    <div class="line-qty">' +
          escapeHtml(line.qty) +
          " шт</div>" +
          '    <div class="line-control-buttons">' +
          '      <button class="btn btn-ghost line-control-btn' +
          minusClassDisabled +
          '" data-action="minus" data-index="' +
          index +
          '"' +
          minusDisabledAttr +
          '>−</button>' +
          '      <button class="btn btn-ghost line-control-btn" data-action="plus" data-index="' +
          index +
          '">+</button>' +
          '      <button class="btn btn-icon line-delete" data-index="' +
          index +
          '" aria-label="Удалить строку">' +
          '        <svg viewBox="0 0 24 24" aria-hidden="true">' +
          "          <path d=\"M3 6h18l-1.5 14h-15z\"></path>" +
          "          <path d=\"M9 4V2h6v2h5v2H4V4z\"></path>" +
          "        </svg>" +
          "      </button>" +
          "    </div>" +
          "  </div>" +
          "</div>"
        );
      })
      .join("");

      return (
        '<div class="lines-table">' +
        '  <div class="lines-header">' +
        '    <div class="lines-cell">Товар</div>' +
        '    <div class="lines-cell qty-column">Кол-во</div>' +
        "  </div>" +
        rows +
        "</div>"
      );
    }

  function renderOutboundPickList(doc) {
    if (doc.op !== "OUTBOUND") {
      return "";
    }
    var lines = doc.lines || [];
    if (!lines.length) {
      return '<div class="empty-state">Нет строк для подбора.</div>';
    }
    var index = typeof doc.selectedLineIndex === "number" ? doc.selectedLineIndex : 0;
    if (index < 0 || index >= lines.length) {
      index = 0;
    }
    var line = lines[index] || {};
    var title = line.itemName || line.barcode || "Позиция";
    var huMap = {};
    lines.forEach(function (entry) {
      var hu = normalizeHuCode(entry.from_hu);
      if (hu) {
        huMap[hu] = true;
      }
    });
    var huCount = Object.keys(huMap).length;
    return (
      '<div class="picklist-card">' +
      '  <div class="picklist-title">Где лежит: ' +
      escapeHtml(title) +
      "</div>" +
      '  <div class="picklist-hint">Выбрано HU: ' +
      escapeHtml(String(huCount)) +
      ". Можно использовать несколько HU.</div>" +
      '  <div id="outboundPickList" class="picklist-body">Загрузка...</div>' +
      "</div>"
    );
  }

  function renderSettings() {
    var debugScanner = isDebugMode();
    var scannerHtml = "";
    var debugPanel = "";
    if (debugScanner) {
      scannerHtml =
        '<label class="form-label">Scanner mode (debug)</label>' +
        '    <select class="form-input" id="scannerModeSelect">' +
        '      <option value="auto">auto</option>' +
        '      <option value="keyboard">keyboard</option>' +
        '      <option value="intent">intent</option>' +
        "    </select>" +
        '    <div class="field-hint" id="scannerModeHint"></div>';
      debugPanel =
        '<label class="toggle-row">' +
        '  <span class="toggle-label">Отладка сканера</span>' +
        '  <span class="toggle-switch">' +
        '    <input type="checkbox" id="scanDebugPanelToggle" />' +
        '    <span class="toggle-slider"></span>' +
        "  </span>" +
        "</label>" +
        '<div class="debug-panel is-collapsed" id="scanDebugPanel">' +
        '  <div class="debug-title">Scanner debug</div>' +
        (scannerHtml ? '  <div class="debug-block">' + scannerHtml + "</div>" : "") +
        '  <div class="debug-actions">' +
        '    <button class="btn btn-outline" type="button" id="scanDebugFocusBtn">Фокус сканера</button>' +
        '    <button class="btn btn-outline" type="button" id="scanDebugStateBtn">Снимок</button>' +
        '    <button class="btn btn-outline" type="button" id="scanDebugClearBtn">Очистить</button>' +
        '    <button class="btn btn-outline" type="button" id="scanDebugCopyBtn">Копировать</button>' +
        "  </div>" +
        '  <pre class="debug-state" id="scanDebugState"></pre>' +
        '  <pre class="debug-log" id="scanDebugLog"></pre>' +
        "</div>";
    }
    return (
      '<section class="screen">' +
      '  <div class="screen-card">' +
      '    <h1 class="screen-title">Настройки</h1>' +
      '    <label class="form-label">ID устройства</label>' +
      '    <div class="field-value" id="deviceIdValue"></div>' +
      '    <label class="toggle-row">' +
      '      <span class="toggle-label">Разрешить экранную клавиатуру</span>' +
      '      <span class="toggle-switch">' +
      '        <input type="checkbox" id="softKeyboardToggle" />' +
      '        <span class="toggle-slider"></span>' +
      "      </span>" +
      "    </label>" +
      (debugPanel ? debugPanel : "") +
      '    <div class="version">Версия приложения: 0.1</div>' +
      "  </div>" +
      "</section>"
    );
  }

  function renderServerDocLines(lines) {
    var rows = (lines || [])
      .map(function (line) {
        var nameText = line.itemName || "-";
        var barcodeText = line.barcode || "";
        var lineHu = normalizeHuCode(line.toHu || line.fromHu);
        var qtyValue = Number(line.qty) || 0;
        var uom = line.uom ? " " + line.uom : "";
        return (
          '<div class="lines-row">' +
          '  <div class="lines-cell">' +
          '    <div class="line-name">' +
          escapeHtml(nameText) +
          "</div>" +
          (barcodeText
            ? '    <div class="line-barcode">' + escapeHtml(barcodeText) + "</div>"
            : "") +
          (lineHu ? '    <div class="line-barcode">HU: ' + escapeHtml(lineHu) + "</div>" : "") +
          "  </div>" +
          '  <div class="lines-cell" style="text-align:right;">' +
          escapeHtml(String(qtyValue) + uom) +
          "</div>" +
          "</div>"
        );
      })
      .join("");

    if (!rows) {
      return '<div class="empty-state">Строк нет.</div>';
    }

    return (
      '<div class="lines-table">' +
      '  <div class="lines-header">' +
      '    <div class="lines-cell">Товар</div>' +
      '    <div class="lines-cell qty-column">Кол-во</div>' +
      "  </div>" +
      rows +
      "</div>"
    );
  }

  function resolveDocHuFromLines(op, lines) {
    var field = "";
    var opValue = String(op || "").toUpperCase();
    if (opValue === "INBOUND" || opValue === "PRODUCTION_RECEIPT" || opValue === "INVENTORY") {
      field = "toHu";
    } else if (opValue === "OUTBOUND" || opValue === "WRITE_OFF") {
      field = "fromHu";
    } else if (opValue === "MOVE") {
      field = "toHu";
    } else {
      return "";
    }

    var unique = [];
    (lines || []).forEach(function (line) {
      if (!line) {
        return;
      }
      var raw = field === "fromHu" ? line.fromHu : line.toHu;
      var normalized = normalizeHuCode(raw);
      if (!normalized) {
        return;
      }
      if (unique.indexOf(normalized) === -1) {
        unique.push(normalized);
      }
    });

    return unique.length === 1 ? unique[0] : "";
  }

  function getUniqueLineValue(lines, field, normalizeFn) {
    var unique = "";
    var hasValue = false;
    var list = Array.isArray(lines) ? lines : [];
    for (var i = 0; i < list.length; i += 1) {
      var line = list[i];
      if (!line) {
        continue;
      }
      var raw = line[field];
      var normalized = normalizeFn ? normalizeFn(raw) : normalizeValue(raw);
      if (!normalized) {
        continue;
      }
      if (!hasValue) {
        unique = normalized;
        hasValue = true;
        continue;
      }
      if (unique !== normalized) {
        return "";
      }
    }
    return hasValue ? unique : "";
  }

  function renderServerDoc(doc, lines) {
    var opLabel = OPS[doc.op] ? OPS[doc.op].label : doc.op;
    var statusLabel = STATUS_LABELS[doc.status] || doc.status;
    var statusClass = "status-" + String(doc.status || "").toLowerCase();
    var partnerLabel = doc.partnerCode && doc.partnerName
      ? doc.partnerCode + " - " + doc.partnerName
      : doc.partnerCode || doc.partnerName || "-";
    var orderRef = doc.order_ref || "-";
    var shippingRef = doc.shipping_ref || "";
    var createdAt = formatDateTime(doc.created_at || doc.createdAt);
    var closedAt = formatDateTime(doc.closed_at || doc.closedAt);
    var linesHtml = renderServerDocLines(lines);
    var opValue = String(doc.op || "").toUpperCase();
    var showOrder = opValue === "OUTBOUND" || opValue === "PRODUCTION_RECEIPT";
    var orderRowHtml = showOrder
      ? '      <div class="order-field-row">' +
        '        <div class="order-field-label">Заказ</div>' +
        '        <div class="order-field-value">' +
        escapeHtml(orderRef) +
        "</div>" +
        "      </div>"
      : "";
    var resolvedHuFromLines = resolveDocHuFromLines(doc.op, lines || []);
    var shippingLabel = shippingRef || resolvedHuFromLines || "-";
    var isInventory = String(doc.op || "").toUpperCase() === "INVENTORY";
    var canResumeRecount = doc.status === "RECOUNT" && isInventory && doc.doc_uid;
    var canResumeDraft = doc.status === "DRAFT" && doc.doc_uid;
    var resumeAction = canResumeRecount || canResumeDraft
      ? '<div class="actions-row doc-actions">' +
        '  <button class="btn primary-btn" id="docResumeBtn">В работу</button>' +
        "</div>"
      : "";

    return (
      '<section class="screen">' +
      '  <div class="screen-card doc-screen-card">' +
      '    <div class="doc-header">' +
      '      <div class="doc-head-top">' +
      '        <div class="doc-titleblock">' +
      '          <div class="doc-header-title">' +
      escapeHtml(opLabel) +
      "</div>" +
      '          <div class="doc-ref-line">№ ' +
      escapeHtml(doc.doc_ref || "-") +
      "</div>" +
      "        </div>" +
      '        <div class="status-pill ' +
      escapeHtml(statusClass) +
      '">' +
      escapeHtml(statusLabel) +
      "</div>" +
      "      </div>" +
      "    </div>" +
      resumeAction +
      '    <div class="order-fields">' +
      '      <div class="order-field-row">' +
      '        <div class="order-field-label">Контрагент</div>' +
      '        <div class="order-field-value">' +
      escapeHtml(partnerLabel) +
      "</div>" +
      "      </div>" +
      orderRowHtml +
      '      <div class="order-field-row">' +
      '        <div class="order-field-label">HU</div>' +
      '        <div class="order-field-value">' +
      escapeHtml(shippingLabel) +
      "</div>" +
      "      </div>" +
      '      <div class="order-field-row">' +
      '        <div class="order-field-label">Создан</div>' +
      '        <div class="order-field-value">' +
      escapeHtml(createdAt) +
      "</div>" +
      "      </div>" +
      '      <div class="order-field-row">' +
      '        <div class="order-field-label">Закрыт</div>' +
      '        <div class="order-field-value">' +
      escapeHtml(closedAt) +
      "</div>" +
      "      </div>" +
      "    </div>" +
      '    <div class="section-subtitle">Строки</div>' +
      '    <div class="lines-section">' +
      linesHtml +
      "    </div>" +
      "  </div>" +
      "</section>"
    );
  }

  function wireServerDoc(doc, lines) {
    var resumeBtn = document.getElementById("docResumeBtn");
    if (!resumeBtn) {
      return;
    }

    resumeBtn.addEventListener("click", function () {
      if (doc.status === "RECOUNT") {
        startRecountDoc(doc, lines || []);
        return;
      }
      if (doc.status === "DRAFT") {
        startDraftDoc(doc, lines || []);
      }
    });
  }

  function startDraftDoc(doc, lines) {
    if (!doc || !doc.doc_uid) {
      alert("Нельзя продолжить документ без идентификатора ТСД.");
      return;
    }

    var docUid = String(doc.doc_uid);
    var nowIso = new Date().toISOString();
    var op = String(doc.op || "").toUpperCase();
    var header = getDefaultHeader(doc.op);

    var partnerId = doc.partner_id != null ? Number(doc.partner_id) : null;
    if (partnerId != null && isNaN(partnerId)) {
      partnerId = null;
    }
    var orderId = doc.order_id != null ? Number(doc.order_id) : null;
    if (orderId != null && isNaN(orderId)) {
      orderId = null;
    }

    var fromLocation = getUniqueLineValue(lines, "fromLocation", normalizeValue);
    var toLocation = getUniqueLineValue(lines, "toLocation", normalizeValue);
    var fromHu = getUniqueLineValue(lines, "fromHu", normalizeHuCode);
    var toHu = getUniqueLineValue(lines, "toHu", normalizeHuCode);

    if (op === "INBOUND") {
      header.to = toLocation;
      header.hu = toHu || "";
      header.partner_id = partnerId;
      if (doc.partnerName || doc.partnerCode) {
        header.partner = formatPartnerLabel({
          name: doc.partnerName || "",
          code: doc.partnerCode || "",
        });
      }
    } else if (op === "PRODUCTION_RECEIPT") {
      header.to = toLocation;
      header.hu = toHu || "";
      header.order_id = orderId;
      header.order_ref = doc.order_ref || "";
    } else if (op === "OUTBOUND") {
      header.from = fromLocation;
      header.hu = fromHu || "";
      header.partner_id = partnerId;
      header.order_id = orderId;
      header.order_ref = doc.order_ref || "";
      if (doc.partnerName || doc.partnerCode) {
        header.partner = formatPartnerLabel({
          name: doc.partnerName || "",
          code: doc.partnerCode || "",
        });
      }
    } else if (op === "MOVE") {
      header.from = fromLocation;
      header.to = toLocation;
      header.from_hu = fromHu || "";
      header.to_hu = toHu || "";
      if (header.from && header.to && header.from === header.to) {
        header.move_internal = true;
      }
    } else if (op === "WRITE_OFF") {
      header.from = fromLocation;
      header.hu = fromHu || "";
      header.reason_code = doc.reason_code || null;
      header.reason_label = getReasonLabel(header.reason_code) || null;
    } else if (op === "INVENTORY") {
      header.location = toLocation || fromLocation;
      header.hu = toHu || "";
    }

    var localLines = (lines || [])
      .map(function (line) {
        return {
          barcode: String(line.barcode || ""),
          qty: Number(line.qty) || 0,
          from: line.fromLocation || null,
          to: line.toLocation || null,
          from_id: null,
          to_id: null,
          from_hu: line.fromHu || null,
          to_hu: line.toHu || null,
          reason_code: null,
          itemId: line.itemId || null,
          itemName: line.itemName || null,
          orderLineId: line.orderLineId || null,
        };
      })
      .filter(function (line) {
        return line.barcode;
      });

    if ((op === "INBOUND" || op === "PRODUCTION_RECEIPT") && header.to) {
      localLines.forEach(function (line) {
        if (!line.to) {
          line.to = header.to;
        }
      });
    }
    if ((op === "OUTBOUND" || op === "WRITE_OFF") && header.from) {
      localLines.forEach(function (line) {
        if (!line.from) {
          line.from = header.from;
        }
      });
    }
    if (op === "MOVE") {
      if (header.from) {
        localLines.forEach(function (line) {
          if (!line.from) {
            line.from = header.from;
          }
        });
      }
      if (header.to) {
        localLines.forEach(function (line) {
          if (!line.to) {
            line.to = header.to;
          }
        });
      }
    }
    if (op === "INVENTORY" && header.location) {
      localLines.forEach(function (line) {
        if (!line.to) {
          line.to = header.location;
        }
      });
    }
    if (op === "WRITE_OFF" && header.reason_code) {
      localLines.forEach(function (line) {
        line.reason_code = header.reason_code;
      });
    }

    function saveDoc(locationMap) {
      if (header.from) {
        var fromLocationEntry = locationMap && locationMap[header.from];
        if (fromLocationEntry) {
          header.from_id = fromLocationEntry.locationId || null;
          header.from_name = fromLocationEntry.name || null;
        }
      }
      if (header.to) {
        var toLocationEntry = locationMap && locationMap[header.to];
        if (toLocationEntry) {
          header.to_id = toLocationEntry.locationId || null;
          header.to_name = toLocationEntry.name || null;
        }
      }
      if (header.location) {
        var locationEntry = locationMap && locationMap[header.location];
        if (locationEntry) {
          header.location_id = locationEntry.locationId || null;
          header.location_name = locationEntry.name || null;
        }
      }

      var localDoc = {
        id: docUid,
        op: doc.op,
        doc_ref: doc.doc_ref || "",
        status: "DRAFT",
        header: header,
        lines: localLines,
        undoStack: [],
        createdAt: doc.created_at || nowIso,
        updatedAt: nowIso,
        exportedAt: null,
      };

      return TsdStorage.saveDoc(localDoc).then(function () {
        setNavOrigin("docs");
        navigate("/doc/" + encodeURIComponent(docUid));
      });
    }

    TsdStorage.apiGetLocations()
      .then(function (locations) {
        var map = {};
        (locations || []).forEach(function (location) {
          if (location && location.code) {
            map[String(location.code)] = location;
          }
        });
        return saveDoc(map);
      })
      .catch(function () {
        return saveDoc(null);
      });
  }

  function startRecountDoc(doc, lines) {
    if (!doc || !doc.doc_uid) {
      alert("Нельзя продолжить инвентаризацию без идентификатора ТСД.");
      return;
    }

    var docUid = String(doc.doc_uid);
    var nowIso = new Date().toISOString();
    var header = getDefaultHeader(doc.op);
    var isInventory = String(doc.op || "").toUpperCase() === "INVENTORY";
    if (isInventory) {
      var locationCode = "";
      (lines || []).some(function (line) {
        if (line && line.toLocation) {
          locationCode = String(line.toLocation);
          return true;
        }
        return false;
      });
      header.location = locationCode;
      header.location_name = null;
      header.location_id = null;
      var huFromLines = resolveDocHuFromLines(doc.op, lines || []);
      header.hu = huFromLines || "";
    }

    var localLines = (lines || [])
      .map(function (line) {
        return {
          barcode: String(line.barcode || ""),
          qty: Number(line.qty) || 0,
          from: line.fromLocation || null,
          to: line.toLocation || null,
          from_id: null,
          to_id: null,
          from_hu: line.fromHu || null,
          to_hu: line.toHu || null,
          reason_code: null,
          itemId: line.itemId || null,
          itemName: line.itemName || null,
          orderLineId: line.orderLineId || null,
        };
      })
      .filter(function (line) {
        return line.barcode;
      });

    if (isInventory && header.location) {
      localLines.forEach(function (line) {
        if (!line.to) {
          line.to = header.location;
        }
      });
    }

    function saveDoc(locationMap) {
      if (isInventory && header.location) {
        var location = locationMap && locationMap[header.location];
        if (location) {
          header.location_id = location.locationId || null;
          header.location_name = location.name || null;
        }
      }

      var localDoc = {
        id: docUid,
        op: doc.op,
        doc_ref: doc.doc_ref || "",
        status: "DRAFT",
        header: header,
        lines: localLines,
        undoStack: [],
        createdAt: doc.created_at || nowIso,
        updatedAt: nowIso,
        exportedAt: null,
      };

      return TsdStorage.saveDoc(localDoc).then(function () {
        setNavOrigin("docs");
        navigate("/doc/" + encodeURIComponent(docUid));
      });
    }

    TsdStorage.apiGetLocations()
      .then(function (locations) {
        var map = {};
        (locations || []).forEach(function (location) {
          if (location && location.code) {
            map[String(location.code)] = location;
          }
        });
        return saveDoc(map);
      })
      .catch(function () {
        return saveDoc(null);
      });
  }

  function renderLogin() {
    return (
      '<section class="screen">' +
      '  <div class="screen-card">' +
      '    <h1 class="screen-title">Вход</h1>' +
      '    <label class="form-label" for="loginInput">Логин</label>' +
      '    <input class="form-input" id="loginInput" type="text" autocomplete="username" />' +
      '    <label class="form-label" for="passwordInput">Пароль</label>' +
      '    <input class="form-input" id="passwordInput" type="password" autocomplete="current-password" />' +
      '    <button id="loginBtn" class="btn primary-btn" type="button">Войти</button>' +
      '    <div id="loginStatus" class="status"></div>' +
      "  </div>" +
      "</section>"
    );
  }
  function wireHome() {
    var homeLowStockWrap = document.getElementById("homeLowStockWrap");
    var homeLowStockRequestId = ++homeLowStockRequestSeq;

    var buttons = document.querySelectorAll("[data-op]");
    buttons.forEach(function (btn) {
      btn.addEventListener("click", function () {
        var op = btn.getAttribute("data-op");
        if (op && isOperationEnabled(op)) {
          navigate("/docs/" + encodeURIComponent(op));
        }
      });
    });
    var routes = document.querySelectorAll("[data-route]");
    routes.forEach(function (btn) {
      btn.addEventListener("click", function () {
        var route = btn.getAttribute("data-route");
        navigate("/" + (route || ""));
      });
    });

    if (homeLowStockWrap && isClientBlockEnabled("tsd_stock")) {
      homeLowStockWrap.innerHTML = '<div class="status">Загрузка позиций ниже минимума...</div>';
      loadHomeLowStockRows()
        .then(function (rows) {
          if (!homeLowStockWrap || !currentRoute || currentRoute.name !== "home") {
            return;
          }
          if (homeLowStockRequestId !== homeLowStockRequestSeq) {
            return;
          }
          homeLowStockWrap.innerHTML = renderHomeLowStockRows(rows || []);
        })
        .catch(function () {
          if (homeLowStockWrap && currentRoute && currentRoute.name === "home") {
            homeLowStockWrap.innerHTML = '<div class="status">Не удалось загрузить позиции ниже минимума.</div>';
          }
        });
    } else if (homeLowStockWrap) {
      homeLowStockWrap.innerHTML = "";
    }
  }

  function wireDocsList() {
    var newBtn = document.getElementById("newDocBtn");
    var docs = document.querySelectorAll("[data-doc]");
    var listOp = currentRoute && currentRoute.op ? currentRoute.op : null;
    var listOrigin = listOp ? "operations" : "home";

    if (newBtn) {
      newBtn.addEventListener("click", function () {
        var op = newBtn.getAttribute("data-op") || listOp;
        if (!op) {
          return;
        }
        createDocAndOpen(op, listOrigin);
      });
    }

    docs.forEach(function (item) {
      item.addEventListener("click", function () {
        var docId = item.getAttribute("data-doc");
        setNavOrigin(listOrigin);
        navigate("/doc/" + encodeURIComponent(docId));
      });
    });
  }

  function wireOrders() {
    var searchInput = document.getElementById("ordersSearchInput");
    var listEl = document.getElementById("ordersList");
    var statusEl = document.getElementById("ordersStatus");

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text || "";
      }
    }

    function renderList(orders) {
      if (!listEl) {
        return;
      }
      var rows = (orders || [])
        .map(function (order) {
          var statusInfo = getOrderStatusInfo(order.status);
          var orderNumber =
            order.number || order.orderNumber || order.order_ref || order.orderRef || "—";
          var partnerLabel = order.partnerName || "—";
          if (order.partnerInn) {
            partnerLabel += " · ИНН: " + order.partnerInn;
          }
          var plannedDate = formatDate(order.plannedDate || order.planned_date);
          var shippedDate = formatDate(order.shippedAt || order.shipped_at);
          var createdAt = formatDateTime(order.createdAt || order.created_at);
          return (
            '<button class="doc-item order-item" data-order="' +
            escapeHtml(order.orderId) +
            '">' +
            '  <div class="doc-main">' +
            '    <div class="doc-title">' +
            escapeHtml(String(orderNumber)) +
            "</div>" +
            '    <div class="order-meta">' +
            '      <div class="order-meta-row">' +
            escapeHtml(partnerLabel) +
            "</div>" +
            '      <div class="order-meta-row">План: ' +
            escapeHtml(plannedDate) +
            "</div>" +
            '      <div class="order-meta-row">Факт: ' +
            escapeHtml(shippedDate) +
            "</div>" +
            '      <div class="order-meta-row">Создан: ' +
            escapeHtml(createdAt) +
            "</div>" +
            "    </div>" +
            "  </div>" +
            '  <div class="' +
            escapeHtml(statusInfo.className) +
            '">' +
            escapeHtml(statusInfo.label) +
            "</div>" +
            "</button>"
          );
        })
        .join("");

      if (!rows) {
        rows = '<div class="empty-state">Заказов пока нет.</div>';
      }
      listEl.innerHTML = rows;
      var items = listEl.querySelectorAll("[data-order]");
      items.forEach(function (item) {
        item.addEventListener("click", function () {
          var orderId = item.getAttribute("data-order");
          navigate("/order/" + encodeURIComponent(orderId));
        });
      });
    }

    function loadOrders(query) {
      setStatus("Загрузка...");
      TsdStorage.listOrders({ q: query })
        .then(function (orders) {
          renderList(orders);
          if (!orders || !orders.length) {
            setStatus("Заказов нет");
          } else {
            setStatus("Данные с сервера");
          }
        })
        .catch(function () {
          setStatus("Ошибка загрузки заказов");
          renderList([]);
        });
    }

    if (searchInput) {
      searchInput.addEventListener("input", function () {
        loadOrders(searchInput.value);
      });
    }

    loadOrders("");
  }

  function wireItems() {
    var searchInput = document.getElementById("itemsSearchInput");
    var listEl = document.getElementById("itemsList");
    var statusEl = document.getElementById("itemsStatus");
    var searchToken = 0;

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text || "";
      }
    }

    function buildMeta(item) {
      var parts = [];
      if (item.sku) {
        parts.push("SKU: " + item.sku);
      }
      if (item.gtin) {
        parts.push("GTIN: " + item.gtin);
      }
      if (item.barcode) {
        parts.push("ШК: " + item.barcode);
      }
      return parts.join(" · ");
    }

    function renderList(items) {
      if (!listEl) {
        return;
      }
      var rows = (items || [])
        .map(function (item) {
          var meta = buildMeta(item);
          var uom = item.base_uom || item.base_uom_code;
          var metaHtml = "";
          if (meta) {
            metaHtml += '<div class="doc-ref">' + escapeHtml(meta) + "</div>";
          }
          if (uom) {
            metaHtml +=
              '<div class="doc-created">Базовая ед.: ' + escapeHtml(uom) + "</div>";
          }
          if (!metaHtml) {
            metaHtml = '<div class="doc-ref">—</div>';
          }
          return (
            '<div class="doc-item item-item">' +
            '  <div class="doc-main">' +
            '    <div class="doc-title">' +
            escapeHtml(item.name || "-") +
            "</div>" +
            metaHtml +
            "  </div>" +
            "</div>"
          );
        })
        .join("");

      if (!rows) {
        rows = '<div class="empty-state">Товаров нет.</div>';
      }
      listEl.innerHTML = rows;
    }

    function loadItems(query) {
      var q = String(query || "").trim();
      var token = (searchToken += 1);
      setStatus(q ? "Поиск..." : "Загрузка...");
      TsdStorage.apiSearchItems(q)
        .then(function (items) {
          if (token !== searchToken) {
            return;
          }
          renderList(items);
          if (!items || !items.length) {
            setStatus(q ? "Ничего не найдено" : "Товаров нет");
          } else {
            setStatus("Данные с сервера");
          }
        })
        .catch(function () {
          if (token !== searchToken) {
            return;
          }
          setStatus("Ошибка загрузки товаров");
          renderList([]);
        });
    }

    if (searchInput) {
      searchInput.addEventListener("input", function () {
        loadItems(searchInput.value);
      });
    }

    loadItems("");
  }

  function wireOrderDetails() {
    // Read-only screen; no actions to wire.
  }

  function wireOperationsMenu() {
    var buttons = document.querySelectorAll("[data-op]");
    buttons.forEach(function (btn) {
      btn.addEventListener("click", function () {
        var op = btn.getAttribute("data-op");
        if (!op || !isOperationEnabled(op)) {
          return;
        }
        createDocAndOpen(op, "operations");
      });
    });
  }

  function buildOverlay(title) {
    var overlay = document.createElement("div");
    overlay.className = "overlay";
    overlay.setAttribute("tabindex", "-1");
    overlay.innerHTML =
      '<div class="overlay-card">' +
      '  <div class="overlay-header">' +
      '    <div class="overlay-title"></div>' +
      '    <button class="btn btn-ghost overlay-close" type="button">Закрыть</button>' +
      "  </div>" +
      '  <input class="form-input overlay-search" type="text" placeholder="Поиск..." />' +
      '  <div class="overlay-section">' +
      '    <div class="overlay-section-title">Последние</div>' +
      '    <div class="overlay-list overlay-recents"></div>' +
      "  </div>" +
      '  <div class="overlay-section">' +
      '    <div class="overlay-section-title">Результаты</div>' +
      '    <div class="overlay-list overlay-results"></div>' +
      "  </div>" +
      "</div>";
    overlay.querySelector(".overlay-title").textContent = title;
    return overlay;
  }

  var overlayOpenCount = 0;

  function overlayTouchBlocker(event) {
    if (!event.target.closest(".overlay-card")) {
      event.preventDefault();
    }
  }

  function lockOverlayScroll() {
    overlayOpenCount += 1;
    if (overlayOpenCount !== 1) {
      return;
    }

    document.body.classList.add("modal-open");
    document.addEventListener("touchmove", overlayTouchBlocker, { passive: false });
  }

  function unlockOverlayScroll() {
    if (overlayOpenCount > 0) {
      overlayOpenCount -= 1;
    }

    if (overlayOpenCount !== 0) {
      return;
    }

    document.body.classList.remove("modal-open");
    document.removeEventListener("touchmove", overlayTouchBlocker);
  }

  function focusOverlay(overlay) {
    var closeBtn = overlay.querySelector(".overlay-close");
    if (closeBtn) {
      closeBtn.focus();
    } else {
      overlay.focus();
    }
  }

  function renderOverlayList(listEl, items, renderLabel, onSelect) {
    listEl.innerHTML = "";
    if (!items.length) {
      var empty = document.createElement("div");
      empty.className = "overlay-empty";
      empty.textContent = "Нет данных";
      listEl.appendChild(empty);
      return;
    }
    items.forEach(function (item) {
      var button = document.createElement("button");
      button.type = "button";
      button.className = "overlay-item";
      var label = document.createElement("div");
      label.className = "overlay-item-title";
      label.textContent = renderLabel(item);
      button.appendChild(label);
      if (item.subLabel) {
        var sub = document.createElement("div");
        sub.className = "overlay-item-sub";
        sub.textContent = item.subLabel;
        button.appendChild(sub);
      }
      button.addEventListener("click", function () {
        onSelect(item);
      });
      listEl.appendChild(button);
    });
  }

  function openPartnerPicker(role, onSelect) {
    setScanHighlight(false);
    var overlay = buildOverlay("Контрагенты");
    var input = overlay.querySelector(".overlay-search");
    var recentsEl = overlay.querySelector(".overlay-recents");
    var resultsEl = overlay.querySelector(".overlay-results");
    var closeBtn = overlay.querySelector(".overlay-close");
    var partnerList = null;
    var partnerLoading = null;

    function close() {
      unlockOverlayScroll();
      document.body.removeChild(overlay);
      document.removeEventListener("keydown", onKeyDown);
      enterScanMode();
    }

    function onKeyDown(event) {
      if (event.key === "Escape") {
        close();
      }
    }

    function ensurePartnerList() {
      if (partnerList) {
        return Promise.resolve(partnerList);
      }
      if (partnerLoading) {
        return partnerLoading;
      }
      partnerLoading = TsdStorage.apiGetPartners(role)
        .then(function (partners) {
          partnerList = (partners || []).map(function (partner) {
            partner.nameLower = String(partner.name || "").toLowerCase();
            partner.codeLower = String(partner.code || "").toLowerCase();
            partner.innLower = String(partner.inn || "").toLowerCase();
            return partner;
          });
          return partnerList;
        })
        .catch(function () {
          partnerList = [];
          return partnerList;
        })
        .finally(function () {
          partnerLoading = null;
        });
      return partnerLoading;
    }

    function loadRecents() {
      Promise.all([ensurePartnerList(), TsdStorage.getSetting("recentPartnerIds")])
        .then(function (result) {
          var partners = result[0] || [];
          var ids = result[1];
          var list = Array.isArray(ids) ? ids.slice(0, 5) : [];
          var filtered = list
            .map(function (id) {
              return partners.find(function (partner) {
                return partner.partnerId === id;
              });
            })
            .filter(function (partner) {
              return !!partner;
            });
          renderOverlayList(
            recentsEl,
            filtered,
            function (partner) {
              return formatPartnerLabel(partner);
            },
            function (partner) {
              onSelect(partner);
              updateRecentSetting("recentPartnerIds", partner.partnerId);
              close();
            }
          );
        })
        .catch(function () {
          renderOverlayList(recentsEl, [], function () {
            return "";
          });
        });
    }

    function runSearch(query) {
      var q = String(query || "").trim().toLowerCase();
      ensurePartnerList()
        .then(function (partners) {
          var list = partners.filter(function (partner) {
            if (!q) {
              return true;
            }
            return (
              (partner.nameLower && partner.nameLower.indexOf(q) !== -1) ||
              (partner.codeLower && partner.codeLower.indexOf(q) !== -1) ||
              (partner.innLower && partner.innLower.indexOf(q) !== -1)
            );
          });

          list.forEach(function (partner) {
            var sub = [];
            if (partner.code) {
              sub.push("Код: " + partner.code);
            }
            if (partner.inn) {
              sub.push("ИНН: " + partner.inn);
            }
            partner.subLabel = sub.join(" · ");
          });

          renderOverlayList(
            resultsEl,
            list,
            function (partner) {
              return formatPartnerLabel(partner);
            },
            function (partner) {
              onSelect(partner);
              updateRecentSetting("recentPartnerIds", partner.partnerId);
              close();
            }
          );
        })
        .catch(function () {
          renderOverlayList(resultsEl, [], function () {
            return "";
          });
        });
    }

    document.body.appendChild(overlay);
    lockOverlayScroll();
    document.addEventListener("keydown", onKeyDown);

    overlay.addEventListener("click", function (event) {
      if (event.target === overlay) {
        close();
      }
    });

    closeBtn.addEventListener("click", close);
    input.addEventListener("input", function () {
      runSearch(input.value);
    });

    loadRecents();
    runSearch("");
    focusOverlay(overlay);
  }

  function openLocationPicker(onSelect) {
    setScanHighlight(false);
    var overlay = buildOverlay("Локации");
    var input = overlay.querySelector(".overlay-search");
    var recentsEl = overlay.querySelector(".overlay-recents");
    var resultsEl = overlay.querySelector(".overlay-results");
    var closeBtn = overlay.querySelector(".overlay-close");

    function close() {
      unlockOverlayScroll();
      document.body.removeChild(overlay);
      document.removeEventListener("keydown", onKeyDown);
      enterScanMode();
    }

    function onKeyDown(event) {
      if (event.key === "Escape") {
        close();
      }
    }

    function loadRecents() {
      TsdStorage.getSetting("recentLocationIds")
        .then(function (ids) {
          var list = Array.isArray(ids) ? ids.slice(0, 5) : [];
          return Promise.all(
            list.map(function (id) {
              return TsdStorage.apiGetLocationById(id).catch(function () {
                return null;
              });
            })
          );
        })
        .then(function (locations) {
          var filtered = locations.filter(function (location) {
            return !!location;
          });
          renderOverlayList(
            recentsEl,
            filtered,
            function (location) {
              return formatLocationLabel(location.code, location.name);
            },
            function (location) {
              onSelect(location);
              updateRecentSetting("recentLocationIds", location.locationId);
              close();
            }
          );
        })
        .catch(function () {
          renderOverlayList(recentsEl, [], function () {
            return "";
          });
        });
    }

    function runSearch(query) {
      TsdStorage.apiSearchLocations(query)
        .then(function (locations) {
          renderOverlayList(
            resultsEl,
            locations,
            function (location) {
              return formatLocationLabel(location.code, location.name);
            },
            function (location) {
              onSelect(location);
              updateRecentSetting("recentLocationIds", location.locationId);
              close();
            }
          );
        })
        .catch(function () {
          renderOverlayList(resultsEl, [], function () {
            return "";
          });
        });
    }

    document.body.appendChild(overlay);
    document.addEventListener("keydown", onKeyDown);

    overlay.addEventListener("click", function (event) {
      if (event.target === overlay) {
        close();
      }
    });

    closeBtn.addEventListener("click", close);
    input.addEventListener("input", function () {
      runSearch(input.value);
    });

    loadRecents();
    runSearch("");
    focusOverlay(overlay);
  }

  function openOrderPicker(onSelect) {
    setScanHighlight(false);
    var overlay = buildOverlay("Заказы");
    var input = overlay.querySelector(".overlay-search");
    var recentsEl = overlay.querySelector(".overlay-recents");
    var resultsEl = overlay.querySelector(".overlay-results");
    var closeBtn = overlay.querySelector(".overlay-close");

    var recentsSection = recentsEl ? recentsEl.parentElement : null;
    if (recentsSection) {
      recentsSection.parentElement.removeChild(recentsSection);
      recentsEl = null;
    }

    function close() {
      unlockOverlayScroll();
      document.body.removeChild(overlay);
      document.removeEventListener("keydown", onKeyDown);
      enterScanMode();
    }

    function onKeyDown(event) {
      if (event.key === "Escape") {
        close();
      }
    }

    function runSearch(query) {
      TsdStorage.searchOrders(query)
        .then(function (orders) {
          var list = orders.map(function (order) {
            var meta = [];
            if (order.plannedDate) {
              meta.push("Дата: " + order.plannedDate);
            }
            if (order.status) {
              meta.push("Статус: " + order.status);
            }
            order.subLabel = meta.join(" · ");
            return order;
          });
          renderOverlayList(
            resultsEl,
            list,
            function (order) {
              return formatOrderLabel(
                order.number || order.orderNumber || order.order_id || order.orderId || order.id
              );
            },
            function (order) {
              onSelect(order);
              close();
            }
          );
        })
        .catch(function () {
          renderOverlayList(resultsEl, [], function () {
            return "";
          });
        });
    }

    document.body.appendChild(overlay);
    lockOverlayScroll();
    document.addEventListener("keydown", onKeyDown);

    overlay.addEventListener("click", function (event) {
      if (event.target === overlay) {
        close();
      }
    });

    closeBtn.addEventListener("click", close);
    input.addEventListener("input", function () {
      runSearch(input.value);
    });

    runSearch("");
    focusOverlay(overlay);
  }

  function openReasonPicker(onSelect) {
    setScanHighlight(false);
    var overlay = buildOverlay("Причина списания");
    var searchInput = overlay.querySelector(".overlay-search");
    var recentsEl = overlay.querySelector(".overlay-recents");
    var resultsEl = overlay.querySelector(".overlay-results");
    var closeBtn = overlay.querySelector(".overlay-close");

    var recentsSection = recentsEl ? recentsEl.parentElement : null;
    if (recentsSection) {
      recentsSection.parentElement.removeChild(recentsSection);
      recentsEl = null;
    }

    function close() {
      unlockOverlayScroll();
      document.body.removeChild(overlay);
      document.removeEventListener("keydown", onKeyDown);
      enterScanMode();
    }

    function onKeyDown(event) {
      if (event.key === "Escape") {
        close();
      }
    }

    function runSearch(query) {
      var normalized = String(query || "").trim().toLowerCase();
      var filtered = WRITE_OFF_REASONS.filter(function (reason) {
        if (!normalized) {
          return true;
        }
        var label = reason.label.toLowerCase();
        var code = reason.code.toLowerCase();
        return label.indexOf(normalized) !== -1 || code.indexOf(normalized) !== -1;
      });
      resultEntries(resultsEl, filtered);
    }

    function resultEntries(target, list) {
      renderOverlayList(
        target,
        list,
        function (item) {
          return item.label;
        },
        function (item) {
          if (!item) {
            return;
          }
          onSelect(item);
          close();
        }
      );
    }

    document.body.appendChild(overlay);
    document.addEventListener("keydown", onKeyDown);

    overlay.addEventListener("click", function (event) {
      if (event.target === overlay) {
        close();
      }
    });

    closeBtn.addEventListener("click", close);
    if (searchInput) {
      searchInput.addEventListener("input", function () {
        runSearch(searchInput.value);
      });
    }

    runSearch("");
    focusOverlay(overlay);
  }

  function openConfirmOverlay(title, message, confirmLabel, onConfirm) {
    setScanHighlight(false);
    var overlay = document.createElement("div");
    overlay.className = "overlay";
    overlay.innerHTML =
      '<div class="overlay-card confirm-card">' +
      '  <div class="overlay-header">' +
      '    <div class="overlay-title"></div>' +
      '  </div>' +
      '  <div class="confirm-message"></div>' +
      '  <div class="confirm-actions">' +
      '    <button class="btn btn-outline overlay-cancel" type="button">Отмена</button>' +
      '    <button class="btn btn-danger overlay-confirm" type="button"></button>' +
      "  </div>" +
      "</div>";
    overlay.querySelector(".overlay-title").textContent = title;
    overlay.querySelector(".confirm-message").textContent = message;
    var confirmBtn = overlay.querySelector(".overlay-confirm");
    confirmBtn.textContent = confirmLabel;

    function close() {
      document.body.removeChild(overlay);
      document.removeEventListener("keydown", onKeyDown);
      enterScanMode();
    }

    function onKeyDown(event) {
      if (event.key === "Escape") {
        close();
      }
    }

    document.body.appendChild(overlay);
    document.addEventListener("keydown", onKeyDown);

    overlay.addEventListener("click", function (event) {
      if (event.target === overlay) {
        close();
      }
    });

    overlay.querySelector(".overlay-cancel").addEventListener("click", close);
    confirmBtn.addEventListener("click", function () {
      close();
      if (typeof onConfirm === "function") {
        onConfirm();
      }
    });
  }

  function openManualInputOverlay(options) {
    var config = options || {};
    var title = config.title || "Ручной ввод";
    var label = config.label || "";
    var placeholder = config.placeholder || "";
    var inputMode = config.inputMode || "text";
    setScanHighlight(false);
    var overlay = document.createElement("div");
    overlay.className = "overlay manual-input-overlay";
    overlay.innerHTML =
      '<div class="overlay-card manual-input-card">' +
      '  <div class="overlay-header">' +
      '    <div class="overlay-title"></div>' +
      '    <button class="btn btn-ghost overlay-close" type="button">Закрыть</button>' +
      "  </div>" +
      '  <div class="manual-input-hint">Ручной ввод. Подтвердите Enter или OK.</div>' +
      (label ? '<label class="form-label"></label>' : "") +
      '  <input class="form-input manual-input-field" type="text" inputmode="' +
      escapeHtml(inputMode) +
      '" autocomplete="off" autocapitalize="off" autocorrect="off" spellcheck="false" />' +
      '  <div class="manual-input-actions">' +
      '    <button class="btn btn-outline overlay-cancel" type="button">Отмена</button>' +
      '    <button class="btn primary-btn overlay-confirm" type="button">OK</button>' +
      "  </div>" +
      "</div>";
    overlay.querySelector(".overlay-title").textContent = title;
    if (label) {
      overlay.querySelector(".form-label").textContent = label;
    }
    var input = overlay.querySelector(".manual-input-field");
    var closeBtn = overlay.querySelector(".overlay-close");
    var cancelBtn = overlay.querySelector(".overlay-cancel");
    var confirmBtn = overlay.querySelector(".overlay-confirm");

    if (input) {
      input.placeholder = placeholder;
    }

    function close() {
      unlockOverlayScroll();
      document.body.removeChild(overlay);
      document.removeEventListener("keydown", onKeyDown);
      if (typeof config.onClose === "function") {
        config.onClose();
      }
      enterScanMode();
    }

    function submit() {
      var value = input ? input.value.trim() : "";
      if (value && typeof config.onSubmit === "function") {
        config.onSubmit(value);
      }
      close();
    }

    function onKeyDown(event) {
      if (event.key === "Escape") {
        close();
      }
      if (event.key === "Enter") {
        event.preventDefault();
        submit();
      }
    }

    document.body.appendChild(overlay);
    lockOverlayScroll();
    document.addEventListener("keydown", onKeyDown);

    overlay.addEventListener("click", function (event) {
      if (event.target === overlay) {
        close();
      }
    });

    closeBtn.addEventListener("click", close);
    cancelBtn.addEventListener("click", close);
    confirmBtn.addEventListener("click", submit);

    if (input) {
      input.focus();
      input.select();
    }
  }

  function openItemRequestOverlay(scannedBarcode, onSent) {
    setScanHighlight(false);
    var overlay = document.createElement("div");
    overlay.className = "overlay";
    overlay.innerHTML =
      '<div class="overlay-card">' +
      '  <div class="overlay-header">' +
      '    <div class="overlay-title">Запрос товара</div>' +
      '    <button class="btn btn-ghost overlay-close" type="button">Закрыть</button>' +
      "  </div>" +
      '  <label class="form-label" for="itemRequestBarcodeInput">Штрихкод*</label>' +
      '  <input class="form-input" id="itemRequestBarcodeInput" type="text" autocomplete="off" />' +
      '  <div class="field-error" id="itemRequestBarcodeError"></div>' +
      '  <label class="form-label" for="itemRequestCommentInput">Комментарий*</label>' +
      '  <textarea class="form-input" id="itemRequestCommentInput" rows="3" autocomplete="off"></textarea>' +
      '  <div class="field-error" id="itemRequestCommentError"></div>' +
      '  <div class="overlay-actions">' +
      '    <button class="btn btn-outline overlay-cancel" type="button">Отмена</button>' +
      '    <button class="btn primary-btn overlay-confirm" type="button">Отправить</button>' +
      "  </div>" +
      "</div>";

    var barcodeInput = overlay.querySelector("#itemRequestBarcodeInput");
    var commentInput = overlay.querySelector("#itemRequestCommentInput");
    var barcodeError = overlay.querySelector("#itemRequestBarcodeError");
    var commentError = overlay.querySelector("#itemRequestCommentError");
    var closeBtn = overlay.querySelector(".overlay-close");
    var cancelBtn = overlay.querySelector(".overlay-cancel");
    var confirmBtn = overlay.querySelector(".overlay-confirm");

    function setError(el, message) {
      if (el) {
        el.textContent = message || "";
      }
    }

    function close() {
      unlockOverlayScroll();
      document.body.removeChild(overlay);
      document.removeEventListener("keydown", onKeyDown);
      enterScanMode();
    }

    function validate() {
      setError(barcodeError, "");
      setError(commentError, "");
      var barcodeValue = barcodeInput ? barcodeInput.value.trim() : "";
      var commentValue = commentInput ? commentInput.value.trim() : "";
      var valid = true;
      if (!barcodeValue) {
        setError(barcodeError, "Введите штрихкод");
        valid = false;
      }
      if (!commentValue) {
        setError(commentError, "Введите комментарий");
        valid = false;
      }
      return valid;
    }

    function formatError(error) {
      var code = error && error.message ? error.message : "";
      if (code === "MISSING_BARCODE") {
        return "Введите штрихкод.";
      }
      if (code === "MISSING_COMMENT") {
        return "Введите комментарий.";
      }
      if (code === "INVALID_JSON" || code === "EMPTY_BODY" || code === "INVALID_RESPONSE") {
        return "Некорректные данные.";
      }
      if (code === "SERVER_ERROR") {
        return "Ошибка сервера.";
      }
      return code || "Ошибка отправки.";
    }

    function submit() {
      if (!validate()) {
        return;
      }
      confirmBtn.disabled = true;
      var barcodeValue = barcodeInput ? barcodeInput.value.trim() : "";
      var commentValue = commentInput ? commentInput.value.trim() : "";
      var login = getStoredLogin();
      TsdStorage.getSetting("device_id")
        .then(function (deviceId) {
          return TsdStorage.apiCreateItemRequest({
            barcode: barcodeValue,
            comment: commentValue,
            device_id: deviceId || null,
            login: login || null,
          });
        })
        .then(function () {
          close();
          if (typeof onSent === "function") {
            onSent();
          }
        })
        .catch(function (error) {
          confirmBtn.disabled = false;
          setError(commentError, formatError(error));
        });
    }

    function onKeyDown(event) {
      if (event.key === "Escape") {
        close();
      }
      if (event.key === "Enter") {
        event.preventDefault();
        submit();
      }
    }

    document.body.appendChild(overlay);
    lockOverlayScroll();
    document.addEventListener("keydown", onKeyDown);

    overlay.addEventListener("click", function (event) {
      if (event.target === overlay) {
        close();
      }
    });

    closeBtn.addEventListener("click", close);
    cancelBtn.addEventListener("click", close);
    confirmBtn.addEventListener("click", submit);

    if (barcodeInput) {
      barcodeInput.value = scannedBarcode || "";
    }

    if (commentInput) {
      commentInput.focus();
    }
  }

  function wireDoc(doc) {
    doc.lines = doc.lines || [];
    doc.undoStack = doc.undoStack || [];
    doc.header = doc.header || getDefaultHeader(doc.op);
    var headerDefaults = getDefaultHeader(doc.op);
    Object.keys(headerDefaults).forEach(function (key) {
      if (doc.header[key] === undefined) {
        doc.header[key] = headerDefaults[key];
      }
    });

    var qtyStep = 1;
    var barcodeInput = document.getElementById("barcodeInput");
    var addLineBtn = document.getElementById("addLineBtn");
    var undoBtn = document.getElementById("undoBtn");
    var finishBtn = document.getElementById("finishBtn");
    var revertBtn = document.getElementById("revertBtn");
    var headerInputs = document.querySelectorAll("[data-header]");
    var deleteButtons = document.querySelectorAll(".line-delete");
    var manualInputButtons = document.querySelectorAll(".kbd-btn");
    var partnerPickBtn = document.getElementById("partnerPickBtn");
    var toPickBtn = document.getElementById("toPickBtn");
    var fromPickBtn = document.getElementById("fromPickBtn");
    var orderPickBtn = document.getElementById("orderPickBtn");
    var orderInfoBtn = document.getElementById("orderInfoBtn");
    var locationPickBtn = document.getElementById("locationPickBtn");
    var partnerPickerRow = document.getElementById("partnerPickerRow");
    var toPickerRow = document.getElementById("toPickerRow");
    var fromPickerRow = document.getElementById("fromPickerRow");
    var locationPickerRow = document.getElementById("locationPickerRow");
    var toInput = document.getElementById("toInput");
    var fromInput = document.getElementById("fromInput");
    var locationInput = document.getElementById("locationInput");
    var scanItemInfo = document.getElementById("scanItemInfo");
    var reasonPickBtn = document.getElementById("reasonPickBtn");
    var reasonErrorEl = document.getElementById("reasonError");
    var partnerErrorEl = document.getElementById("partnerError");
    var moveInternalToggle = document.getElementById("moveInternalToggle");
    var huValueEl = document.getElementById("huValue");
    var huScanBtn = document.getElementById("huScanBtn");
    var huClearBtn = document.getElementById("huClearBtn");
    var huErrorEl = document.getElementById("huError");
    var huFromValueEl = document.getElementById("huFromValue");
    var huFromScanBtn = document.getElementById("huFromScanBtn");
    var huFromErrorEl = document.getElementById("huFromError");
    var huToValueEl = document.getElementById("huToValue");
    var huToScanBtn = document.getElementById("huToScanBtn");
    var huToErrorEl = document.getElementById("huToError");
    var lookupToken = 0;
    var lastBarcodeValue = "";
    var qtyModeButtons = document.querySelectorAll(".qty-mode-btn");
    var qtyOverlay = null;
    var qtyOverlayKeyListener = null;
    var isDraftDoc = doc.status === "DRAFT";
    var docActionStatus = document.getElementById("docActionStatus");
    var incBtn = null;
    var incHoldTimer = null;
    var incHoldTriggered = false;
    var huOverlay = null;
    var huModalInput = null;
    var huModalError = null;
    var huModalConfirm = null;
    var huModalValue = null;
    var huModalTarget = "hu";
    var huModalKeyListener = null;
    var huLocationCache = null;
    var huLocationCachedAt = 0;
    var HU_LOCATION_TTL_MS = 10000;
    var apiItemsCache = null;
    var apiItemsCacheAt = 0;
    var API_ITEMS_CACHE_MS = 60000;
    var lastScanHandledAt = 0;
    var lastScanHandledValue = "";
    var outboundPickToken = 0;
    var outboundPickCandidates = [];

    function clearBarcodeInput(keepScanInfo) {
      if (barcodeInput) {
        barcodeInput.value = "";
      }
      lastBarcodeValue = "";
      if (!keepScanInfo) {
        updateScanInfoByBarcode("");
      }
    }

    function setDocStatus(text) {
      if (!docActionStatus) {
        return;
      }
      docActionStatus.textContent = text || "";
    }

    function setScanInfo(text, isUnknown) {
      if (!scanItemInfo) {
        return;
      }
      scanItemInfo.textContent = text || "";
      scanItemInfo.classList.toggle("scan-info-unknown", !!isUnknown);
    }

    function updateScanInfoByBarcode(value) {
      var trimmed = String(value || "").trim();
      if (!trimmed) {
        setScanInfo("", false);
        return;
      }
      var token = (lookupToken += 1);
      TsdStorage.apiFindItemByCode(trimmed)
        .then(function (item) {
          if (token !== lookupToken) {
            return;
          }
          if (item && item.name) {
            setScanInfo(item.name, false);
          } else {
            setScanInfo("Неизвестный код", true);
          }
        })
        .catch(function (error) {
          if (token === lookupToken) {
            var code = error && error.message ? error.message : "";
            if (code === "ITEM_INACTIVE") {
              setScanInfo("Карточка товара заблокирована", true);
              return;
            }
            setScanInfo("", false);
          }
        });
    }

    function focusBarcode() {
      enterScanMode();
    }

    function applyCatalogState() {
      var online = serverStatus.ok !== false;
      var hasPartners = online;
      var hasLocations = online;
      var hasOrders = online;

      if (partnerPickerRow) {
        partnerPickerRow.classList.remove("is-hidden");
      }
      if (partnerPickBtn) {
        partnerPickBtn.disabled = !hasPartners || doc.status !== "DRAFT";
      }
      var isDraft = doc.status === "DRAFT";
      if (toPickBtn) {
        var toDisabled = !hasLocations || !isDraft;
        if (doc.op === "MOVE" && doc.header.move_internal) {
          toDisabled = true;
        }
        toPickBtn.disabled = toDisabled;
      }
      if (fromPickBtn) {
        fromPickBtn.disabled = !hasLocations || !isDraft;
      }
      if (orderPickBtn) {
        orderPickBtn.disabled = !hasOrders || !isDraft;
      }
      if (locationPickBtn) {
        locationPickBtn.disabled = !hasLocations || !isDraft;
      }

    }

    function closeQuantityOverlay() {
      if (!qtyOverlay) {
        return;
      }
      document.body.removeChild(qtyOverlay);
      qtyOverlay = null;
      if (qtyOverlayKeyListener) {
        document.removeEventListener("keydown", qtyOverlayKeyListener);
        qtyOverlayKeyListener = null;
      }
    }

    function openQuantityOverlay(barcode, item, onSelect) {
      if (!doc.status || doc.status !== "DRAFT") {
        return;
      }
      setScanHighlight(false);
      closeQuantityOverlay();
      qtyOverlay = document.createElement("div");
      qtyOverlay.className = "overlay qty-overlay";
      var quickQty = [1, 2, 3, 5, 10];
      var quickButtons = quickQty
        .map(function (value) {
          return '<button type="button" class="btn qty-overlay-quick-btn" data-qty="' + value + '">' + value + " шт</button>";
        })
        .join("");
      var itemLabel = item && item.name ? String(item.name) : "";
      qtyOverlay.innerHTML =
        '<div class="overlay-card">' +
        '  <div class="overlay-header">' +
        '    <div class="overlay-title">Количество</div>' +
        '    <button class="btn btn-ghost overlay-close" type="button">✕</button>' +
        "  </div>" +
        '  <div class="qty-overlay-body">' +
        (itemLabel
          ? '    <div class="qty-overlay-item">' + escapeHtml(itemLabel) + "</div>"
          : "") +
        '    <div class="qty-overlay-barcode">' +
        escapeHtml(barcode) +
        "</div>" +
        '    <div class="qty-overlay-stock">' +
        '      <div class="qty-overlay-stock-title">Остаток сейчас</div>' +
        '      <div class="qty-overlay-stock-body" id="qtyStockBody">Загрузка...</div>' +
        "    </div>" +
        '    <div class="qty-overlay-quick">' +
        quickButtons +
        "    </div>" +
        '    <input class="form-input qty-overlay-input" type="number" min="1" value="1" />' +
        '    <div class="field-error qty-overlay-error"></div>' +
        '    <div class="qty-overlay-actions">' +
        '      <button class="btn btn-outline" type="button" id="qtyCancel">Отмена</button>' +
        '      <button class="btn primary-btn" type="button" id="qtyOk">OK</button>' +
        "    </div>" +
        "  </div>" +
        "</div>";
      document.body.appendChild(qtyOverlay);
      var quantityInput = qtyOverlay.querySelector(".qty-overlay-input");
      var okBtn = qtyOverlay.querySelector("#qtyOk");
      var cancelBtn = qtyOverlay.querySelector("#qtyCancel");
      var closeBtn = qtyOverlay.querySelector(".overlay-close");
      var quickButtonsEls = qtyOverlay.querySelectorAll(".qty-overlay-quick-btn");
      var stockBody = qtyOverlay.querySelector("#qtyStockBody");

      if (stockBody) {
        Promise.all([TsdStorage.apiGetStockByBarcode(barcode), TsdStorage.apiGetLocations()])
          .then(function (result) {
            var stock = result[0] || {};
            var locations = result[1] || [];
            var locationMap = {};
            locations.forEach(function (location) {
              locationMap[location.locationId] = location;
            });
            var totals = Array.isArray(stock.totalsByLocation) ? stock.totalsByLocation : [];
            var byHu = Array.isArray(stock.byHu) ? stock.byHu : [];
            var uom = (item && (item.base_uom || item.base_uom_code)) || "шт";

            if (!totals.length && !byHu.length) {
              stockBody.textContent = "Остатков нет";
              return;
            }

            function renderTotalsRows(rows) {
              if (!rows.length) {
                return '<div class="qty-stock-empty">Нет данных</div>';
              }
              return rows
                .map(function (row) {
                  var location = locationMap[row.locationId] || {};
                  var code = row.locationCode || location.code || "";
                  var name = location.name || "";
                  var label = formatLocationLabel(code, name);
                  return (
                    '<div class="qty-stock-row">' +
                    '  <div class="qty-stock-label">' +
                    escapeHtml(label) +
                    "</div>" +
                    '  <div class="qty-stock-qty">' +
                    escapeHtml(String(row.qty)) +
                    " шт</div>" +
                    "</div>"
                  );
                })
                .join("");
            }

            function renderHuRows(rows) {
              if (!rows.length) {
                return '<div class="qty-stock-empty">Нет HU-разреза</div>';
              }
              return rows
                .map(function (row) {
                  var code = row.locationCode || "";
                  var line =
                    row.hu +
                    " — " +
                    row.qty +
                    " " +
                    uom +
                    " (локация: " +
                    code +
                    ")";
                  return '<div class="qty-stock-row">' + escapeHtml(line) + "</div>";
                })
                .join("");
            }

            var totalsHtml = renderTotalsRows(totals);
            var byHuHtml =
              '<div class="qty-stock-section">' +
              '  <div class="qty-stock-section-title">По HU</div>' +
              renderHuRows(byHu) +
              "</div>";

            stockBody.innerHTML =
              '<div class="qty-stock-section">' +
              '  <div class="qty-stock-section-title">По локациям</div>' +
              totalsHtml +
              "</div>" +
              byHuHtml;
          })
          .catch(function () {
            stockBody.textContent = "Остаток недоступен";
          });
      }

      function setQtyError(message) {
        var errorEl = qtyOverlay.querySelector(".qty-overlay-error");
        if (errorEl) {
          errorEl.textContent = message || "";
        }
        if (quantityInput) {
          quantityInput.classList.toggle("input-error", !!message);
        }
      }

      function tryApplyQuantity() {
        if (!quantityInput) {
          return;
        }
        var value = parseFloat(quantityInput.value);
        if (!value || value <= 0) {
          setQtyError("Введите количество больше 0");
          return;
        }
        setQtyError("");
        closeQuantityOverlay();
        if (typeof onSelect === "function") {
          onSelect(value);
        }
        focusBarcode();
      }

      quickButtonsEls.forEach(function (btn) {
        btn.addEventListener("click", function () {
          var value = parseInt(btn.getAttribute("data-qty"), 10);
          if (!value || value <= 0) {
            return;
          }
          if (quantityInput) {
            quantityInput.value = value;
          }
          tryApplyQuantity();
        });
      });

      okBtn &&
        okBtn.addEventListener("click", function () {
          tryApplyQuantity();
        });
      cancelBtn &&
        cancelBtn.addEventListener("click", function () {
          closeQuantityOverlay();
          focusBarcode();
        });
      closeBtn &&
        closeBtn.addEventListener("click", function () {
          closeQuantityOverlay();
          focusBarcode();
        });

      qtyOverlay.addEventListener("click", function (event) {
        if (event.target === qtyOverlay) {
          closeQuantityOverlay();
          focusBarcode();
        }
      });

      qtyOverlayKeyListener = function (event) {
        if (event.key === "Escape") {
          closeQuantityOverlay();
          focusBarcode();
        }
        if (event.key === "Enter") {
          event.preventDefault();
          tryApplyQuantity();
        }
      };
      document.addEventListener("keydown", qtyOverlayKeyListener);

      if (quantityInput) {
        quantityInput.addEventListener("input", function () {
          setQtyError("");
        });
      }
    }

    function addLineWithQuantity(barcode, quantity, itemOverride) {
      var qtyValue = Number(quantity) || 0;
      if (!qtyValue || qtyValue <= 0) {
        focusBarcode();
        return;
      }
      var lineData = buildLineData(doc.op, doc.header);
      if (itemOverride) {
        finalizeLine(itemOverride);
        return;
      }
      TsdStorage.apiFindItemByCode(barcode)
        .then(function (item) {
          if (item) {
            finalizeLine(item);
            return;
          }
          setScanInfo("Товар не найден", true);
          openItemRequestOverlay(barcode, function () {
            if (barcodeInput) {
              barcodeInput.value = "";
            }
            setScanInfo("Запрос отправлен оператору", false);
            focusBarcode();
          });
        })
        .catch(function (error) {
          var code = error && error.message ? error.message : "";
          if (code === "ITEM_INACTIVE") {
            setScanInfo("Карточка товара заблокирована", true);
            return;
          }
          setScanInfo("Товар не найден", true);
        });

      function maybeValidateMoveQty(itemId, nextQty) {
        if (doc.op !== "MOVE" || !doc.header.move_internal) {
          return Promise.resolve(true);
        }
        if (!doc.header.from_id) {
          return Promise.resolve(true);
        }
        var fromHu = normalizeHuCode(lineData.from_hu);
        if (!fromHu) {
          return Promise.resolve(true);
        }
        return TsdStorage.apiGetStockByBarcode(barcode)
          .then(function (stock) {
            var rows = stock && Array.isArray(stock.byHu) ? stock.byHu : [];
            var available = 0;
            rows.forEach(function (row) {
              if (row.locationId === doc.header.from_id && normalizeHuCode(row.hu) === fromHu) {
                available += Number(row.qty) || 0;
              }
            });

            if (nextQty > available) {
              alert("Недостаточно остатка на выбранном HU.");
              return false;
            }

            return true;
          })
          .catch(function () {
            return true;
          });
      }

      function finalizeLine(item) {
        var itemId = item ? item.itemId : null;
        var itemName = item ? item.name : null;
        if (!item) {
          return;
        }
        var lineIndex = findLineIndex(doc.op, doc.lines, barcode, lineData);
        var nextQty = qtyValue;
        if (lineIndex >= 0) {
          nextQty = (Number(doc.lines[lineIndex].qty) || 0) + qtyValue;
        }

        maybeValidateMoveQty(itemId, nextQty).then(function (ok) {
          if (!ok) {
            focusBarcode();
            return;
          }

          if (lineIndex >= 0) {
            doc.lines[lineIndex].qty = nextQty;
            if (itemName && !doc.lines[lineIndex].itemName) {
              doc.lines[lineIndex].itemName = itemName;
              doc.lines[lineIndex].itemId = itemId;
            }
            if (lineData.from_id && !doc.lines[lineIndex].from_id) {
              doc.lines[lineIndex].from_id = lineData.from_id;
            }
            if (lineData.to_id && !doc.lines[lineIndex].to_id) {
              doc.lines[lineIndex].to_id = lineData.to_id;
            }
          } else {
            doc.lines.push({
              barcode: barcode,
              qty: qtyValue,
              from: lineData.from,
              to: lineData.to,
              from_id: lineData.from_id || null,
              to_id: lineData.to_id || null,
              from_hu: lineData.from_hu,
              to_hu: lineData.to_hu,
              reason_code: lineData.reason_code,
              itemId: itemId,
              itemName: itemName,
            });
          }

          doc.undoStack.push({
            barcode: barcode,
            qtyDelta: qtyValue,
            from: lineData.from,
            to: lineData.to,
            from_hu: lineData.from_hu,
            to_hu: lineData.to_hu,
            reason_code: lineData.reason_code,
          });

          if (barcodeInput) {
            barcodeInput.value = "";
          }
          setScanInfo("", false);
          saveDocState().then(refreshDocView);
        });
      }
    }

    var locationErrorTimers = {};
    var reasonErrorTimer = null;
    var locationFieldInputs = document.querySelectorAll("[data-location-field]");

    function setLocationError(field, message) {
      var errorEl = document.getElementById(field + "Error");
      var inputEl = document.getElementById(field + "Input");
      if (errorEl) {
        errorEl.textContent = message || "";
      }
      if (inputEl) {
        inputEl.classList.toggle("input-error", !!message);
      }
      if (locationErrorTimers[field]) {
        clearTimeout(locationErrorTimers[field]);
        locationErrorTimers[field] = null;
      }
      if (message) {
        locationErrorTimers[field] = window.setTimeout(function () {
          setLocationError(field, "");
        }, 2000);
      }
    }

    function setReasonError(message) {
      if (reasonErrorEl) {
        reasonErrorEl.textContent = message || "";
      }
      if (reasonErrorTimer) {
        clearTimeout(reasonErrorTimer);
        reasonErrorTimer = null;
      }
      if (message) {
        reasonErrorTimer = window.setTimeout(function () {
          if (reasonErrorEl) {
            reasonErrorEl.textContent = "";
          }
          reasonErrorTimer = null;
        }, 2500);
      }
    }

    function adjustLineAt(index, delta) {
      if (!isDraftDoc) {
        return;
      }
      if (typeof index !== "number") {
        return;
      }
      if (index < 0 || index >= doc.lines.length) {
        return;
      }
      var line = doc.lines[index];
      if (!line) {
        return;
      }
      var nextQty = (Number(line.qty) || 0) + delta;
      if (delta < 0 && nextQty < 1) {
        nextQty = 1;
      }
      line.qty = nextQty;
      doc.updatedAt = new Date().toISOString();
      preserveScanFocus = true;
      saveDocState().then(function () {
        refreshDocView();
      });
    }

    function deleteLineAt(index) {
      if (!isDraftDoc) {
        return;
      }
      if (isNaN(index) || index < 0 || index >= doc.lines.length) {
        return;
      }
      doc.lines.splice(index, 1);
      doc.updatedAt = new Date().toISOString();
      preserveScanFocus = true;
      saveDocState().then(function () {
        refreshDocView();
      });
    }

    function focusFirstLocationOrBarcode() {
      if (preserveScanFocus) {
        preserveScanFocus = false;
      }
      focusBarcode();
    }

    function handleLocationEntry(field, valueOverride) {
      if (doc.status !== "DRAFT") {
        return;
      }
      var inputEl = document.getElementById(field + "Input");
      var value = normalizeValue(
        valueOverride != null ? valueOverride : inputEl ? inputEl.value : ""
      );
      if (inputEl && valueOverride != null) {
        inputEl.value = value;
      }
      if (!value) {
        setLocationError(field, "Введите код локации");
        return;
      }
      TsdStorage.findLocationByCode(value)
        .then(function (location) {
          if (!location) {
            setLocationError(field, "Локация не найдена: " + value);
            return;
          }
          applyLocationSelection(field, location);
        })
        .catch(function () {
          setLocationError(field, "Ошибка поиска локации");
        });
    }

    function hydrateHeaderFromCatalog() {
      var updates = [];
      var changed = false;

      if (doc.header.partner_id && !doc.header.partner) {
        updates.push(
          TsdStorage.getPartnerById(doc.header.partner_id).then(function (partner) {
            if (partner) {
              doc.header.partner = partner.name || "";
              changed = true;
            }
          })
        );
      }

      if (doc.header.order_id && !doc.header.order_ref) {
        updates.push(
          TsdStorage.getOrderById(doc.header.order_id).then(function (order) {
            if (order) {
              doc.header.order_ref = order.number || order.orderNumber || "";
              changed = true;
            }
          })
        );
      }

      if (doc.header.from_id && !doc.header.from_name) {
        updates.push(
          TsdStorage.apiGetLocationById(doc.header.from_id).then(function (location) {
            if (location) {
              doc.header.from = location.code || doc.header.from;
              doc.header.from_name = location.name || null;
              changed = true;
            }
          })
        );
      }

      if (doc.header.to_id && !doc.header.to_name) {
        updates.push(
          TsdStorage.apiGetLocationById(doc.header.to_id).then(function (location) {
            if (location) {
              doc.header.to = location.code || doc.header.to;
              doc.header.to_name = location.name || null;
              changed = true;
            }
          })
        );
      }

      if (doc.header.location_id && !doc.header.location_name) {
        updates.push(
          TsdStorage.apiGetLocationById(doc.header.location_id).then(function (location) {
            if (location) {
              doc.header.location = location.code || doc.header.location;
              doc.header.location_name = location.name || null;
              changed = true;
            }
          })
        );
      }

      if (!updates.length) {
        return;
      }

      Promise.all(updates)
        .then(function () {
          if (changed) {
            saveDocState().then(refreshDocView);
          }
        })
        .catch(function () {
          return false;
        });
    }

    function saveDocState() {
      doc.updatedAt = new Date().toISOString();
      return TsdStorage.saveDoc(doc).catch(function () {
        return false;
      });
    }

    function refreshDocView() {
      app.innerHTML = renderDoc(doc);
      wireDoc(doc);
    }

    function handleAddLine(barcodeOverride) {
      if (doc.status !== "DRAFT") {
        return;
      }
      var barcode = normalizeValue(
        barcodeOverride != null ? barcodeOverride : barcodeInput ? barcodeInput.value : ""
      );
      if (!barcode) {
        focusBarcode();
        return;
      }
      var huCode = extractHuCode(barcode);
      if (huCode) {
        handleHuScan(huCode);
        return;
      }
      if (!ensureLocationSelectedForOperation(true)) {
        return;
      }
      var qtyMode = doc.header.qtyMode === "INC1" ? "INC1" : "ASK";
      if (qtyMode === "ASK") {
        TsdStorage.apiFindItemByCode(barcode)
          .then(function (item) {
            if (item) {
              openQuantityOverlay(barcode, item, function (quantity) {
                addLineWithQuantity(barcode, quantity, item);
              });
              return;
            }
            addLineWithQuantity(barcode, qtyStep);
          })
          .catch(function (error) {
            var code = error && error.message ? error.message : "";
            if (code === "ITEM_INACTIVE") {
              setScanInfo("Карточка товара заблокирована", true);
              return;
            }
            addLineWithQuantity(barcode, qtyStep);
          });
        return;
      }
      addLineWithQuantity(barcode, qtyStep);
    }

    function validateBeforeFinish() {
      var valid = true;
      setPartnerError("");
      setHuError("", "hu");
      setHuError("", "from_hu");
      setHuError("", "to_hu");
      setLocationError("from", "");
      setLocationError("to", "");
      setLocationError("location", "");
      if (doc.op !== "MOVE") {
        if (!validateHuHeader(true)) {
          valid = false;
        }
      } else {
        if (!validateHuField("from_hu", true)) {
          valid = false;
        }
        if (!validateHuField("to_hu", true)) {
          valid = false;
        }
      }
      if (doc.op === "INBOUND") {
        if (!doc.header.partner_id) {
          setPartnerError("Выберите поставщика");
          valid = false;
        }
        if (!normalizeValue(doc.header.to) && !doc.header.to_id) {
          setLocationError("to", "Для приемки выберите место хранения (Куда).");
          valid = false;
        }
      } else if (doc.op === "PRODUCTION_RECEIPT") {
        if (!normalizeValue(doc.header.to) && !doc.header.to_id) {
          setLocationError("to", "Для выпуска продукции выберите место хранения (Куда).");
          valid = false;
        }
      } else if (doc.op === "OUTBOUND") {
        if (!doc.header.partner_id) {
          setPartnerError("Выберите покупателя");
          valid = false;
        }
        if (!normalizeValue(doc.header.from) && !doc.header.from_id) {
          setLocationError("from", "Укажите место хранения");
          valid = false;
        }
      } else if (doc.op === "MOVE") {
        if (!normalizeValue(doc.header.from) && !doc.header.from_id) {
          setLocationError("from", "Укажите место хранения");
          valid = false;
        }
        if (!normalizeValue(doc.header.to) && !doc.header.to_id) {
          setLocationError("to", "Укажите место хранения");
          valid = false;
        }
        if (doc.header.move_internal) {
          if (doc.header.from_id && doc.header.to_id && doc.header.from_id !== doc.header.to_id) {
            setLocationError("to", "Для внутреннего перемещения выберите то же место хранения.");
            valid = false;
          }

          if (!normalizeValue(doc.header.from_hu)) {
            setHuError("Выберите HU-источник.", "from_hu");
            valid = false;
          }
          if (!normalizeValue(doc.header.to_hu)) {
            setHuError("Выберите HU-назначение.", "to_hu");
            valid = false;
          }

          var fromHu = normalizeHuCode(doc.header.from_hu);
          var toHu = normalizeHuCode(doc.header.to_hu);
          if (fromHu && toHu && fromHu === toHu) {
            setHuError("HU-источник и HU-назначение должны быть разными.", "to_hu");
            valid = false;
          }
        } else if (
          doc.header.from_id &&
          doc.header.to_id &&
          doc.header.from_id === doc.header.to_id
        ) {
          setLocationError("to", "Для перемещения внутри склада включите режим и задайте HU.");
          valid = false;
        }
      } else if (doc.op === "WRITE_OFF") {
        if (!normalizeValue(doc.header.from) && !doc.header.from_id) {
          setLocationError("from", "Укажите место хранения");
          valid = false;
        }
        if (!normalizeValue(doc.header.reason_code)) {
          setReasonError("Выберите причину списания");
          valid = false;
        }
      } else if (doc.op === "INVENTORY") {
        if (!normalizeValue(doc.header.location) && !doc.header.location_id) {
          setLocationError("location", "Укажите место хранения");
          valid = false;
        }
      }
      return valid;
    }

    function normalizeQtyStep(value) {
      var parsed = parseInt(value, 10);
      if (!parsed || parsed < 1) {
        return 1;
      }
      return parsed;
    }

    function updateQtyModeLabel() {
      if (incBtn) {
        incBtn.textContent = "+" + qtyStep;
      }
    }

    function saveQtyStep(value) {
      qtyStep = normalizeQtyStep(value);
      updateQtyModeLabel();
      return TsdStorage.setSetting("qtyStep", qtyStep).catch(function () {
        return false;
      });
    }

    function openStepOverlay() {
      if (!incBtn) {
        return;
      }
      setScanHighlight(false);
      var overlay = document.createElement("div");
      overlay.className = "overlay qty-overlay";
      var quickValues = [1, 5, 10, 12, 15];
      var quickButtons = quickValues
        .map(function (value) {
          return (
            '<button type="button" class="btn qty-overlay-quick-btn" data-step="' +
            value +
            '">' +
            value +
            " шт</button>"
          );
        })
        .join("");
      overlay.innerHTML =
        '<div class="overlay-card">' +
        '  <div class="overlay-header">' +
        '    <div class="overlay-title">Шаг инкремента</div>' +
        '    <button class="btn btn-ghost overlay-close" type="button">Закрыть</button>' +
        "  </div>" +
        '  <div class="qty-overlay-body">' +
        '    <div class="qty-overlay-quick">' +
        quickButtons +
        "    </div>" +
        '    <input class="form-input qty-overlay-input" type="number" min="1" value="' +
        qtyStep +
        '" />' +
        '    <div class="qty-overlay-actions">' +
        '      <button class="btn btn-outline" type="button" id="stepCancel">Отмена</button>' +
        '      <button class="btn primary-btn" type="button" id="stepOk">OK</button>' +
        "    </div>" +
        "  </div>" +
        "</div>";
      document.body.appendChild(overlay);

      var input = overlay.querySelector(".qty-overlay-input");
      var closeBtn = overlay.querySelector(".overlay-close");
      var cancelBtn = overlay.querySelector("#stepCancel");
      var okBtn = overlay.querySelector("#stepOk");
      var quickBtns = overlay.querySelectorAll(".qty-overlay-quick-btn");

      function close() {
        document.body.removeChild(overlay);
        document.removeEventListener("keydown", onKeyDown);
        enterScanMode();
      }

      function submit(value) {
        var next = normalizeQtyStep(value != null ? value : input ? input.value : 1);
        saveQtyStep(next).then(function () {
          close();
        });
      }

      function onKeyDown(event) {
        if (event.key === "Escape") {
          close();
        }
        if (event.key === "Enter") {
          event.preventDefault();
          submit();
        }
      }

      function setQuickActive(value) {
        quickBtns.forEach(function (btn) {
          var step = parseInt(btn.getAttribute("data-step"), 10) || 1;
          btn.classList.toggle("is-active", step === value);
        });
      }

      quickBtns.forEach(function (btn) {
        btn.addEventListener("click", function () {
          var value = parseInt(btn.getAttribute("data-step"), 10) || 1;
          if (input) {
            input.value = value;
          }
          setQuickActive(value);
        });
      });

      if (input) {
        input.addEventListener("input", function () {
          setQuickActive(normalizeQtyStep(input.value));
        });
      }

      overlay.addEventListener("click", function (event) {
        if (event.target === overlay) {
          close();
        }
      });

      closeBtn.addEventListener("click", close);
      cancelBtn.addEventListener("click", close);
      okBtn.addEventListener("click", function () {
        submit();
      });

      document.addEventListener("keydown", onKeyDown);
      setQuickActive(normalizeQtyStep(qtyStep));
    }

    function setPartnerError(message) {
      if (partnerErrorEl) {
        partnerErrorEl.textContent = message || "";
      }
    }

    function getHuFieldElements(field) {
      if (field === "from_hu") {
        return { valueEl: huFromValueEl, errorEl: huFromErrorEl };
      }
      if (field === "to_hu") {
        return { valueEl: huToValueEl, errorEl: huToErrorEl };
      }
      return { valueEl: huValueEl, errorEl: huErrorEl };
    }

    function setHuError(message, field) {
      var target = field || "hu";
      var nodes = getHuFieldElements(target);
      if (nodes.errorEl) {
        nodes.errorEl.textContent = message || "";
      }
    }

    function setHuDisplay(value, field) {
      var target = field || "hu";
      var nodes = getHuFieldElements(target);
      if (!nodes.valueEl) {
        return;
      }
      var current = normalizeValue(value != null ? value : doc.header[target]);
      var normalized = normalizeHuCode(current);
      var labelValue = normalized || current;
      var label = labelValue ? "HU: " + labelValue : "HU: не задан";
      nodes.valueEl.textContent = label;
    }

    function applyHuContentsFromStock(huCode) {
      if (!isDraftDoc) {
        return Promise.resolve(false);
      }
      if (doc.op !== "OUTBOUND" && doc.op !== "WRITE_OFF") {
        return Promise.resolve(false);
      }
      var normalized = normalizeHuCode(huCode);
      if (!normalized) {
        return Promise.resolve(false);
      }
      var fromLocationId = doc.header.from_id || null;
      setDocStatus("Загрузка содержимого HU...");
      return TsdStorage.apiGetHuStockRows()
        .then(function (rows) {
          var filtered = (rows || []).filter(function (row) {
            if (normalizeHuCode(row.hu) !== normalized) {
              return false;
            }
            if (fromLocationId && row.locationId !== fromLocationId) {
              return false;
            }
            var qty = Number(row.qty) || 0;
            return qty > 0;
          });
          if (!filtered.length) {
            return null;
          }
          var grouped = {};
          filtered.forEach(function (row) {
            var itemId = Number(row.itemId);
            var qty = Number(row.qty) || 0;
            if (!itemId || qty <= 0) {
              return;
            }
            grouped[itemId] = (grouped[itemId] || 0) + qty;
          });
          var itemIds = Object.keys(grouped)
            .map(function (id) {
              return Number(id);
            })
            .filter(function (id) {
              return !!id;
            });
          if (!itemIds.length) {
            return null;
          }
          return TsdStorage.getItemsByIds(itemIds).then(function (itemsMap) {
            var map = itemsMap || {};
            var missing = [];
            itemIds.forEach(function (id) {
              if (!map[id]) {
                missing.push(id);
              }
            });
            if (!missing.length) {
              return { grouped: grouped, itemsMap: map };
            }
            var now = Date.now();
            var useCache = apiItemsCache && now - apiItemsCacheAt < API_ITEMS_CACHE_MS;
            var loader = useCache ? Promise.resolve(apiItemsCache) : TsdStorage.apiSearchItems("");
            return loader
              .then(function (items) {
                var list = items || [];
                if (!useCache) {
                  apiItemsCache = list;
                  apiItemsCacheAt = Date.now();
                }
                var missingMap = {};
                missing.forEach(function (id) {
                  missingMap[id] = true;
                });
                list.forEach(function (item) {
                  if (item && missingMap[item.itemId]) {
                    map[item.itemId] = item;
                  }
                });
                return { grouped: grouped, itemsMap: map };
              })
              .catch(function () {
                return { grouped: grouped, itemsMap: map };
              });
          });
        })
        .then(function (result) {
          if (!result) {
            setDocStatus("HU пустой.");
            return false;
          }
          var grouped = result.grouped;
          var itemsMap = result.itemsMap || {};
          var lineData = buildLineData(doc.op, doc.header);
          var nextLines = [];
          Object.keys(grouped).forEach(function (itemIdStr) {
            var itemId = Number(itemIdStr);
            var qty = grouped[itemIdStr];
            if (!itemId || !qty) {
              return;
            }
            var item = itemsMap[itemId] || null;
            var barcode = "";
            var name = "";
            if (item) {
              barcode = item.barcode || item.sku || item.gtin || "";
              name = item.name || "";
            }
            nextLines.push({
              barcode: barcode,
              qty: qty,
              from: lineData.from,
              to: lineData.to,
              from_hu: lineData.from_hu,
              to_hu: lineData.to_hu,
              reason_code: lineData.reason_code,
              itemId: itemId,
              itemName: name,
            });
          });
          if (!nextLines.length) {
            setDocStatus("HU пустой.");
            return false;
          }
          doc.lines = nextLines;
          doc.undoStack = [];
          doc.updatedAt = new Date().toISOString();
          return saveDocState().then(function () {
            refreshDocView();
            setDocStatus("HU загружен.");
            return true;
          });
        })
        .catch(function () {
          setDocStatus("Ошибка загрузки HU.");
          return false;
        });
    }

    function formatQtyValue(value) {
      var num = Number(value);
      if (isNaN(num)) {
        return "0";
      }
      return String(num);
    }

    function updateSelectedLineHighlight(index) {
      var rows = document.querySelectorAll(".lines-row[data-line-index]");
      rows.forEach(function (row) {
        var rowIndex = parseInt(row.getAttribute("data-line-index"), 10);
        row.classList.toggle("is-selected", rowIndex === index);
      });
    }

    function getSelectedOutboundLine() {
      var lines = doc.lines || [];
      if (!lines.length) {
        return { line: null, index: -1 };
      }
      var index =
        typeof doc.selectedLineIndex === "number" ? doc.selectedLineIndex : 0;
      if (index < 0 || index >= lines.length) {
        index = 0;
      }
      return { line: lines[index], index: index };
    }

    function setSelectedLineIndex(index, persist) {
      if (!Array.isArray(doc.lines) || !doc.lines.length) {
        return;
      }
      if (index < 0 || index >= doc.lines.length) {
        return;
      }
      if (doc.selectedLineIndex === index) {
        return;
      }
      doc.selectedLineIndex = index;
      updateSelectedLineHighlight(index);
      loadOutboundPickList();
      if (persist) {
        saveDocState();
      }
    }

    function getLocationFieldForHu(target) {
      if (doc.op === "MOVE") {
        if (target === "from_hu") {
          return "from";
        }
        if (target === "to_hu") {
          return "to";
        }
        return null;
      }
      if (doc.op === "INBOUND" || doc.op === "PRODUCTION_RECEIPT") {
        return "to";
      }
      if (doc.op === "OUTBOUND" || doc.op === "WRITE_OFF") {
        return "from";
      }
      if (doc.op === "INVENTORY") {
        return "location";
      }
      return null;
    }

    function hasSelectedLocation(field) {
      if (field === "from") {
        return !!normalizeValue(doc.header.from) || !!doc.header.from_id;
      }
      if (field === "to") {
        return !!normalizeValue(doc.header.to) || !!doc.header.to_id;
      }
      if (field === "location") {
        return !!normalizeValue(doc.header.location) || !!doc.header.location_id;
      }
      return false;
    }

    function getLocationErrorMessage(field) {
      if ((doc.op === "INBOUND" || doc.op === "PRODUCTION_RECEIPT") && field === "to") {
        return doc.op === "PRODUCTION_RECEIPT"
          ? "Для выпуска продукции выберите место хранения (Куда)."
          : "Для приемки выберите место хранения (Куда).";
      }
      return "Укажите место хранения";
    }

    function ensureLocationSelectedForHu(target, showAlert) {
      var field = getLocationFieldForHu(target);
      if (!field) {
        return true;
      }
      if (hasSelectedLocation(field)) {
        return true;
      }
      var message = getLocationErrorMessage(field);
      setLocationError(field, message);
      if (showAlert) {
        alert(message);
      }
      return false;
    }

    function ensureLocationSelectedForOperation(showAlert) {
      var valid = true;
      var alertMessage = "";
      setLocationError("from", "");
      setLocationError("to", "");
      setLocationError("location", "");

      if (doc.op === "INBOUND" || doc.op === "PRODUCTION_RECEIPT") {
        if (!hasSelectedLocation("to")) {
          var inboundMessage = getLocationErrorMessage("to");
          setLocationError("to", inboundMessage);
          valid = false;
          alertMessage = alertMessage || inboundMessage;
        }
      } else if (doc.op === "OUTBOUND" || doc.op === "WRITE_OFF") {
        if (!hasSelectedLocation("from")) {
          var fromMessage = getLocationErrorMessage("from");
          setLocationError("from", fromMessage);
          valid = false;
          alertMessage = alertMessage || fromMessage;
        }
      } else if (doc.op === "MOVE") {
        if (!hasSelectedLocation("from")) {
          var moveFromMessage = getLocationErrorMessage("from");
          setLocationError("from", moveFromMessage);
          valid = false;
          alertMessage = alertMessage || moveFromMessage;
        }
        if (!hasSelectedLocation("to")) {
          var moveToMessage = getLocationErrorMessage("to");
          setLocationError("to", moveToMessage);
          valid = false;
          alertMessage = alertMessage || moveToMessage;
        }
      } else if (doc.op === "INVENTORY") {
        if (!hasSelectedLocation("location")) {
          var inventoryMessage = getLocationErrorMessage("location");
          setLocationError("location", inventoryMessage);
          valid = false;
          alertMessage = alertMessage || inventoryMessage;
        }
      }

      if (showAlert && !valid && alertMessage) {
        alert(alertMessage);
      }

      return valid;
    }

    function getHuLocationRows() {
      var now = Date.now();
      if (huLocationCache && now - huLocationCachedAt < HU_LOCATION_TTL_MS) {
        return Promise.resolve(huLocationCache);
      }
      return TsdStorage.apiGetHuStockRows()
        .then(function (rows) {
          huLocationCache = rows || [];
          huLocationCachedAt = Date.now();
          return huLocationCache;
        })
        .catch(function (error) {
          if (huLocationCache) {
            return huLocationCache;
          }
          throw error;
        });
    }

    function getOutboundHuLocationRows(line) {
      var orderId = doc.header.order_id || null;
      if (orderId && line && line.itemId) {
        return TsdStorage.apiGetHuStockRows({ orderId: orderId, itemId: line.itemId });
      }
      return getHuLocationRows();
    }

    function loadOutboundPickList() {
      var container = document.getElementById("outboundPickList");
      if (!container) {
        return;
      }
      if (doc.op !== "OUTBOUND") {
        container.innerHTML = "";
        return;
      }
      var selection = getSelectedOutboundLine();
      if (!selection.line) {
        container.innerHTML = '<div class="empty-state">Нет строк для подбора.</div>';
        return;
      }
      updateSelectedLineHighlight(selection.index);

      var line = selection.line;
      if (!line.itemId) {
        container.innerHTML = '<div class="empty-state">Выберите строку.</div>';
        return;
      }

      container.innerHTML = '<div class="status">Загрузка...</div>';
      var token = (outboundPickToken += 1);
      Promise.all([getOutboundHuLocationRows(line), TsdStorage.apiGetLocations().catch(function () { return []; })])
        .then(function (result) {
          if (token !== outboundPickToken) {
            return;
          }
          var rows = result[0] || [];
          var locations = result[1] || [];
          var locationsById = {};
          locations.forEach(function (location) {
            if (!location || !location.locationId) {
              return;
            }
            locationsById[location.locationId] = location;
          });

          var reservedByHu = {};
          (doc.lines || []).forEach(function (lineEntry) {
            var hu = normalizeHuCode(lineEntry.from_hu);
            var locationId = lineEntry.from_id;
            if (!hu || !locationId) {
              return;
            }
            var key = hu + "|" + locationId;
            var qty = Number(lineEntry.qty) || 0;
            reservedByHu[key] = (reservedByHu[key] || 0) + qty;
          });

          var candidates = rows
            .filter(function (row) {
              return row.itemId === line.itemId && row.qty > 0 && row.hu;
            })
            .map(function (row) {
              var hu = normalizeHuCode(row.hu);
              if (!hu) {
                return null;
              }
              var location = locationsById[row.locationId] || null;
              var locationCode = location ? location.code : String(row.locationId);
              var locationLabel = formatLocationLabel(locationCode, location ? location.name : "");
              var key = hu + "|" + row.locationId;
              var reserved = reservedByHu[key] || 0;
              var available = Number(row.qty) - reserved;
              if (available <= 0) {
                return null;
              }
              return {
                hu: hu,
                locationId: row.locationId,
                locationCode: locationCode,
                locationLabel: locationLabel,
                available: available,
              };
            })
            .filter(function (entry) {
              return !!entry;
            });

          candidates.sort(function (a, b) {
            var locCompare = String(a.locationLabel).localeCompare(String(b.locationLabel));
            if (locCompare !== 0) {
              return locCompare;
            }
            return String(a.hu).localeCompare(String(b.hu));
          });

          outboundPickCandidates = candidates;
          if (!candidates.length) {
            container.innerHTML = '<div class="empty-state">Нет остатков по HU.</div>';
            return;
          }

          var activeHu = normalizeHuCode(line.from_hu);
          var activeLocationId = line.from_id || null;
          var rowsHtml = candidates
            .map(function (candidate, index) {
              var isActive =
                activeHu &&
                candidate.hu === activeHu &&
                activeLocationId === candidate.locationId;
              var rowClass = "picklist-row" + (isActive ? " is-selected" : "");
              return (
                '<div class="' +
                rowClass +
                '">' +
                '  <div class="picklist-info">' +
                '    <div class="picklist-hu">' +
                escapeHtml(candidate.hu) +
                "</div>" +
                '    <div class="picklist-meta">' +
                escapeHtml(candidate.locationLabel) +
                "</div>" +
                "  </div>" +
                '  <div class="picklist-qty">' +
                escapeHtml(formatQtyValue(candidate.available)) +
                " шт</div>" +
                '  <button class="btn btn-outline picklist-apply" data-pick-index="' +
                index +
                '" type="button">Отгрузить</button>' +
                "</div>"
              );
            })
            .join("");

          container.innerHTML = rowsHtml;
          var buttons = container.querySelectorAll(".picklist-apply");
          buttons.forEach(function (button) {
            button.addEventListener("click", function () {
              var index = parseInt(button.getAttribute("data-pick-index"), 10);
              if (isNaN(index)) {
                return;
              }
              applyOutboundHuCandidate(index);
            });
          });
        })
        .catch(function () {
          if (token !== outboundPickToken) {
            return;
          }
          container.innerHTML = '<div class="empty-state">Не удалось загрузить HU.</div>';
        });
    }

    function applyOutboundHuCandidate(index) {
      var selection = getSelectedOutboundLine();
      var line = selection.line;
      if (!line) {
        return;
      }
      var candidate = outboundPickCandidates[index];
      if (!candidate) {
        return;
      }
      var candidateHu = normalizeHuCode(candidate.hu);
      if (!candidateHu) {
        return;
      }
      var existingHu = normalizeHuCode(line.from_hu);
      if (existingHu && existingHu !== candidateHu) {
        alert("Строка уже привязана к другому HU.");
        return;
      }

      var available = Number(candidate.available) || 0;
      if (available <= 0) {
        alert("Нет доступного остатка на выбранном HU.");
        return;
      }

      var lineQty = Number(line.qty) || 0;
      var allocQty = Math.min(lineQty, available);
      if (allocQty <= 0) {
        return;
      }

      function applyAllocation() {
        var remainingQty = lineQty - allocQty;
        if (remainingQty <= 0.000001) {
          line.qty = allocQty;
          line.from = candidate.locationCode;
          line.from_id = candidate.locationId;
          line.from_hu = candidateHu;
        } else {
          line.qty = remainingQty;
          var newLine = {
            barcode: line.barcode,
            qty: allocQty,
            from: candidate.locationCode,
            to: line.to,
            from_id: candidate.locationId,
            to_id: line.to_id || null,
            from_hu: candidateHu,
            to_hu: line.to_hu || null,
            reason_code: line.reason_code,
            itemId: line.itemId,
            itemName: line.itemName,
            orderLineId: line.orderLineId || null,
          };
          doc.lines.splice(selection.index + 1, 0, newLine);
        }
        saveDocState().then(refreshDocView);
      }

      if (allocQty < lineQty) {
        openConfirmOverlay(
          "Частичная отгрузка",
          "На HU доступно " + formatQtyValue(available) + " шт. Отгрузить?",
          "Отгрузить",
          applyAllocation
        );
        return;
      }

      applyAllocation();
    }

    function checkHuLocationAvailability(huCode, target) {
      var field = getLocationFieldForHu(target);
      if (!field) {
        return Promise.resolve({ ok: true });
      }
      var locationId =
        field === "from"
          ? doc.header.from_id
          : field === "to"
            ? doc.header.to_id
            : doc.header.location_id;
      if (!locationId) {
        return Promise.resolve({ ok: true });
      }
      return getHuLocationRows().then(function (rows) {
        if (isHuInOtherLocation(rows, huCode, locationId)) {
          return { ok: false, message: "HU уже находится в другой локации." };
        }
        return { ok: true };
      });
    }

    function resolveMoveHuTarget(huCode) {
      var fromHu = normalizeValue(doc.header.from_hu);
      var toHu = normalizeValue(doc.header.to_hu);
      if (!fromHu) {
        return Promise.resolve("from_hu");
      }
      if (!toHu) {
        return Promise.resolve("to_hu");
      }
      var fromLocationId = doc.header.from_id || null;
      var toLocationId = doc.header.to_id || null;
      if (!fromLocationId && !toLocationId) {
        return Promise.resolve("to_hu");
      }
      var normalized = normalizeHuCode(huCode);
      if (!normalized) {
        return Promise.resolve("to_hu");
      }
      return getHuLocationRows()
        .then(function (rows) {
          var locationId = null;
          for (var i = 0; i < rows.length; i += 1) {
            var rowHu = normalizeHuCode(rows[i].hu);
            if (rowHu && rowHu === normalized) {
              locationId = rows[i].locationId;
              break;
            }
          }
          if (locationId && fromLocationId && locationId === fromLocationId) {
            return "from_hu";
          }
          if (locationId && toLocationId && locationId === toLocationId) {
            return "to_hu";
          }
          return "to_hu";
        })
        .catch(function () {
          return "to_hu";
        });
    }

    function resolveHuTarget(huCode) {
      if (doc.op !== "MOVE") {
        return Promise.resolve("hu");
      }
      return resolveMoveHuTarget(huCode);
    }

    function handleHuScan(huCode) {
      if (!isDraftDoc) {
        return;
      }
      var normalized = normalizeHuCode(huCode) || extractHuCode(huCode);
      if (!normalized) {
        return;
      }
      var targetField = "hu";
      resolveHuTarget(normalized)
        .then(function (target) {
          targetField = target || "hu";
          if (!ensureLocationSelectedForHu(targetField, true)) {
            return null;
          }
          return checkHuLocationAvailability(normalized, targetField).then(function (result) {
            if (!result || result.ok === false) {
              var message =
                result && result.message ? result.message : "HU недоступен для выбранной локации.";
              setHuError(message, targetField);
              return null;
            }
            doc.header[targetField] = normalized;
            setHuError("", targetField);
            setHuDisplay(normalized, targetField);
            var shouldPopulate =
              targetField === "hu" && (doc.op === "OUTBOUND" || doc.op === "WRITE_OFF");
            if (shouldPopulate) {
              return applyHuContentsFromStock(normalized).then(function (applied) {
                if (!applied) {
                  setHuError("Не удалось загрузить содержимое HU.", targetField);
                  return saveDocState();
                }
                return true;
              });
            }
            return saveDocState();
          });
        })
        .catch(function () {
          setHuError("Ошибка проверки HU.", targetField);
        })
        .finally(function () {
          focusBarcode();
        });
    }

    function validateHuField(field, showAlert) {
      var target = field || "hu";
      var current = normalizeValue(doc.header[target]);
      if (!current) {
        setHuError("", target);
        setHuDisplay("", target);
        return true;
      }

      var normalized = normalizeHuCode(current);
      if (normalized === null) {
        var message = "Это не HU-код. HU должен начинаться с HU-.";
        setHuError(message, target);
        if (showAlert) {
          alert(message);
        }
        return false;
      }

      setHuError("", target);
      if (normalized !== doc.header[target]) {
        doc.header[target] = normalized;
        saveDocState();
      }
      setHuDisplay(normalized, target);
      return true;
    }

    function validateHuHeader(showAlert) {
      return validateHuField("hu", showAlert);
    }

    function refreshHuHeaderDisplay() {
      if (doc.op === "MOVE") {
        setHuDisplay(doc.header.from_hu, "from_hu");
        setHuDisplay(doc.header.to_hu, "to_hu");
        return;
      }
      setHuDisplay(doc.header.hu, "hu");
    }

    function setHuModalValue(rawValue, showError) {
      if (!huModalInput) {
        return;
      }
      var trimmed = normalizeValue(rawValue);
      var normalized = extractHuCode(trimmed);
      if (!normalized) {
        huModalValue = null;
        if (huModalError) {
          huModalError.textContent =
            showError && trimmed ? "Это не HU-код. HU должен начинаться с HU-." : "";
        }
        if (huModalConfirm) {
          huModalConfirm.disabled = true;
        }
        return;
      }

      huModalValue = normalized;
      huModalInput.value = normalized;
      if (huModalError) {
        huModalError.textContent = "Найден HU: " + normalized;
      }
      if (huModalConfirm) {
        huModalConfirm.disabled = false;
      }
    }

    function openHuOverlay(targetField) {
      if (!isDraftDoc || huOverlay) {
        return;
      }

      var target = targetField || "hu";
      if (!ensureLocationSelectedForHu(target, true)) {
        return;
      }
      huModalTarget = target;
      setScanHighlight(false);
      var huTitle = "Сканировать HU";
      if (huModalTarget === "from_hu") {
        huTitle = "Сканировать HU-источник";
      } else if (huModalTarget === "to_hu") {
        huTitle = "Сканировать HU-назначение";
      }
      huOverlay = document.createElement("div");
      huOverlay.className = "overlay hu-overlay";
      huOverlay.setAttribute("tabindex", "-1");
      huOverlay.innerHTML =
        '<div class="overlay-card">' +
        '  <div class="overlay-header">' +
        '    <div class="overlay-title">' +
        escapeHtml(huTitle) +
        "</div>" +
        '    <button class="btn btn-ghost overlay-close" type="button">Закрыть</button>' +
        "  </div>" +
        '  <div class="overlay-body">' +
        '    <div class="form-label">Отсканируйте HU-код</div>' +
        '    <input class="form-input" id="huModalInput" type="text" placeholder="HU-..." inputmode="none" autocomplete="off" autocapitalize="off" autocorrect="off" spellcheck="false" readonly />' +
        '    <div class="field-error" id="huModalError"></div>' +
        '    <div class="overlay-actions">' +
        '      <button class="btn btn-outline" type="button" id="huModalCancel">Отмена</button>' +
        '      <button class="btn primary-btn" type="button" id="huModalConfirm" disabled>Подтвердить</button>' +
        "    </div>" +
        "  </div>" +
        "</div>";

      document.body.appendChild(huOverlay);
      lockOverlayScroll();
      focusOverlay(huOverlay);

      huModalInput = huOverlay.querySelector("#huModalInput");
      huModalError = huOverlay.querySelector("#huModalError");
      huModalConfirm = huOverlay.querySelector("#huModalConfirm");
      var cancelBtn = huOverlay.querySelector("#huModalCancel");
      var closeBtn = huOverlay.querySelector(".overlay-close");

      function closeOverlay() {
        unlockOverlayScroll();
        document.body.removeChild(huOverlay);
        huOverlay = null;
        huModalInput = null;
        huModalError = null;
        huModalConfirm = null;
        huModalValue = null;
        if (huModalKeyListener) {
          document.removeEventListener("keydown", huModalKeyListener);
          huModalKeyListener = null;
        }
        setDocStatus("");
        huModalTarget = "hu";
        focusBarcode();
      }

      function confirmHu() {
        if (!huModalValue) {
          return;
        }
        if (!ensureLocationSelectedForHu(huModalTarget, true)) {
          return;
        }
        var pendingValue = huModalValue;
        var shouldPopulate = huModalTarget === "hu" && (doc.op === "OUTBOUND" || doc.op === "WRITE_OFF");
        if (huModalConfirm) {
          huModalConfirm.disabled = true;
        }
        checkHuLocationAvailability(pendingValue, huModalTarget)
          .then(function (result) {
            if (!result || result.ok === false) {
              var message =
                result && result.message ? result.message : "HU недоступен для выбранной локации.";
              if (huModalError) {
                huModalError.textContent = message;
              }
              setHuError(message, huModalTarget);
              return;
            }
            doc.header[huModalTarget] = pendingValue;
            setHuError("", huModalTarget);
            setHuDisplay(pendingValue, huModalTarget);
            if (shouldPopulate) {
              return applyHuContentsFromStock(pendingValue).then(function (applied) {
                if (!applied) {
                  if (huModalError) {
                    huModalError.textContent = "Не удалось загрузить содержимое HU.";
                  }
                  return;
                }
                closeOverlay();
              });
            }
            return saveDocState().then(function () {
              refreshDocView();
              closeOverlay();
            });
          })
          .catch(function () {
            if (huModalError) {
              huModalError.textContent = "Ошибка проверки HU.";
            }
          })
          .finally(function () {
            if (huModalConfirm) {
              huModalConfirm.disabled = false;
            }
          });
      }

      huOverlay.addEventListener("click", function (event) {
        if (event.target === huOverlay) {
          closeOverlay();
        }
      });

      if (cancelBtn) {
        cancelBtn.addEventListener("click", closeOverlay);
      }
      if (closeBtn) {
        closeBtn.addEventListener("click", closeOverlay);
      }
      if (huModalConfirm) {
        huModalConfirm.addEventListener("click", confirmHu);
      }

      huModalKeyListener = function (event) {
        if (event.key === "Escape") {
          closeOverlay();
        }
        if (event.key === "Enter" && huModalConfirm && !huModalConfirm.disabled) {
          event.preventDefault();
          confirmHu();
        }
      };
      document.addEventListener("keydown", huModalKeyListener);

      var statusLabel = "Сканируйте HU";
      if (huModalTarget === "from_hu") {
        statusLabel = "Сканируйте HU-источник";
      } else if (huModalTarget === "to_hu") {
        statusLabel = "Сканируйте HU-назначение";
      }
      setHuModalValue(doc.header[huModalTarget], false);
      setDocStatus(statusLabel);
      enterScanMode();
    }

    function handleScannedValue(value) {
      var trimmed = normalizeValue(value);
      if (!trimmed) {
        return;
      }
      var huCode = extractHuCode(trimmed);
      if (barcodeInput) {
        lastBarcodeValue = trimmed;
        barcodeInput.value = trimmed;
        if (!huCode) {
          updateScanInfoByBarcode(trimmed);
        }
      }
      var now = Date.now();
      if (lastScanHandledValue === trimmed && now - lastScanHandledAt < 120) {
        return;
      }
      lastScanHandledValue = trimmed;
      lastScanHandledAt = now;
      if (huCode) {
        setScanInfo("HU добавлен", false);
        handleHuScan(huCode);
        clearBarcodeInput(true);
        return;
      }
      handleAddLine(trimmed);
      clearBarcodeInput();
    }

    function handleScanEvent(scan) {
      if (!isDraftDoc) {
        return;
      }
      var value = scan && scan.value ? scan.value : scan;
      handleScannedValue(value);
    }

    setScanHandler(handleScanEvent);

    if (barcodeInput) {
      setPreferredScanTarget(barcodeInput);
      barcodeInput.addEventListener("input", function () {
        if (barcodeInput.value !== lastBarcodeValue) {
          barcodeInput.value = lastBarcodeValue;
        }
        if (!extractHuCode(barcodeInput.value)) {
          updateScanInfoByBarcode(barcodeInput.value);
        }
      });
      barcodeInput.addEventListener("keydown", function (event) {
        if (event.key === "Enter") {
          event.preventDefault();
          handleAddLine();
          return;
        }
      });
    }

    if (addLineBtn) {
      addLineBtn.addEventListener("click", function () {
        handleAddLine();
      });
    }

    qtyModeButtons.forEach(function (btn) {
      btn.addEventListener("click", function () {
        if (doc.status !== "DRAFT") {
          return;
        }
        var mode = btn.getAttribute("data-mode");
        if (incHoldTriggered && btn === incBtn) {
          incHoldTriggered = false;
          return;
        }
        if (!mode || doc.header.qtyMode === mode) {
          return;
        }
        doc.header.qtyMode = mode;
        saveDocState().then(refreshDocView);
      });
    });

    incBtn = document.querySelector('.qty-mode-btn[data-mode="INC1"]');
    if (incBtn) {
      incBtn.addEventListener("pointerdown", function () {
        if (doc.status !== "DRAFT") {
          return;
        }
        incHoldTriggered = false;
        if (incHoldTimer) {
          clearTimeout(incHoldTimer);
        }
        incHoldTimer = window.setTimeout(function () {
          incHoldTriggered = true;
          openStepOverlay();
        }, 600);
      });
      incBtn.addEventListener("pointerup", function () {
        if (incHoldTimer) {
          clearTimeout(incHoldTimer);
          incHoldTimer = null;
        }
      });
      incBtn.addEventListener("pointerleave", function () {
        if (incHoldTimer) {
          clearTimeout(incHoldTimer);
          incHoldTimer = null;
        }
      });
      incBtn.addEventListener("pointercancel", function () {
        if (incHoldTimer) {
          clearTimeout(incHoldTimer);
          incHoldTimer = null;
        }
      });
    }

    TsdStorage.getSetting("qtyStep")
      .then(function (value) {
        qtyStep = normalizeQtyStep(value);
        updateQtyModeLabel();
      })
      .catch(function () {
        qtyStep = 1;
        updateQtyModeLabel();
      });

    if (undoBtn) {
      undoBtn.addEventListener("click", function () {
        if (!doc.undoStack.length || doc.status !== "DRAFT") {
          return;
        }
        var last = doc.undoStack.pop();
        var lineIndex = findLineIndex(doc.op, doc.lines, last.barcode, last);
        if (lineIndex >= 0) {
          doc.lines[lineIndex].qty -= last.qtyDelta;
          if (doc.lines[lineIndex].qty <= 0) {
            doc.lines.splice(lineIndex, 1);
          }
          saveDocState().then(refreshDocView);
        }
      });
    }

    deleteButtons.forEach(function (btn) {
      btn.addEventListener("click", function (event) {
        if (event) {
          event.preventDefault();
        }
        if (doc.status !== "DRAFT") {
          return;
        }
        var index = parseInt(btn.getAttribute("data-index"), 10);
        if (isNaN(index)) {
          return;
        }
        openConfirmOverlay("Удалить строку?", "Удалить строку?", "Удалить", function () {
          deleteLineAt(index);
        });
      });
    });

    var lineRows = document.querySelectorAll(".lines-row[data-line-index]");
    lineRows.forEach(function (row) {
      row.addEventListener("click", function (event) {
        if (event && event.target && event.target.closest("button")) {
          return;
        }
        var index = parseInt(row.getAttribute("data-line-index"), 10);
        if (isNaN(index)) {
          return;
        }
        setSelectedLineIndex(index, true);
      });
    });

    loadOutboundPickList();

    function applyPartnerSelection(partner) {
      setPartnerError("");
      doc.header.partner_id = partner.partnerId;
      doc.header.partner = partner.name || "";
      saveDocState().then(refreshDocView);
    }

    function applyOrderSelection(order) {
      if (!order) {
        return;
      }
      doc.header.order_id = order.orderId || order.order_id || order.id || null;
      doc.header.order_ref =
        order.number || order.orderNumber || order.order_id || order.orderId || "";
      if (doc.op === "OUTBOUND") {
        applyOutboundOrderLines(order);
        return;
      }
      saveDocState().then(refreshDocView);
    }

    function applyOutboundOrderLines(order) {
      var orderId = order.orderId || order.order_id || order.id || null;
      if (!orderId) {
        saveDocState().then(refreshDocView);
        return;
      }

      function applyLines() {
        setDocStatus("Загрузка строк заказа...");
        var lineData = buildLineData(doc.op, doc.header);
        TsdStorage.apiGetOrderLines(orderId)
          .then(function (lines) {
            var nextLines = (lines || [])
              .filter(function (line) {
                var left = Number(line.leftQty);
                if (isNaN(left)) {
                  var ordered = Number(line.orderedQty) || 0;
                  var shipped = Number(line.shippedQty) || 0;
                  left = Math.max(ordered - shipped, 0);
                }
                return left > 0;
              })
              .map(function (line) {
                var remaining = Number(line.leftQty);
                if (isNaN(remaining)) {
                  var ordered = Number(line.orderedQty) || 0;
                  var shipped = Number(line.shippedQty) || 0;
                  remaining = Math.max(ordered - shipped, 0);
                }
                return {
                  barcode: line.barcode || "",
                  qty: remaining,
                  from: lineData.from,
                  to: lineData.to,
                  from_id: lineData.from_id || null,
                  to_id: lineData.to_id || null,
                  from_hu: lineData.from_hu || null,
                  to_hu: lineData.to_hu || null,
                  reason_code: lineData.reason_code,
                  itemId: line.itemId,
                  itemName: line.itemName || "",
                  orderLineId: line.orderLineId || null,
                };
              });
            doc.lines = nextLines;
            doc.selectedLineIndex = nextLines.length ? 0 : null;
            doc.updatedAt = new Date().toISOString();
            saveDocState().then(refreshDocView);
            if (!nextLines.length) {
              alert("По заказу нет остатка к отгрузке.");
            }
          })
          .catch(function () {
            alert("Не удалось загрузить строки заказа.");
            refreshDocView();
          });
      }

      if (doc.lines && doc.lines.length) {
        openConfirmOverlay(
          "Заменить строки?",
          "Заменить текущие строки данными из заказа?",
          "Заменить",
          applyLines
        );
        return;
      }

      applyLines();
    }

    function applyLocationSelection(field, location) {
      setLocationError(field, "");
      doc.header[field] = location.code || "";
      doc.header[field + "_name"] = location.name || null;
      doc.header[field + "_id"] = location.locationId;
      if (doc.op === "MOVE" && doc.header.move_internal && field === "from") {
        doc.header.to = location.code || "";
        doc.header.to_name = location.name || null;
        doc.header.to_id = location.locationId;
      }
      updateRecentSetting("recentLocationIds", location.locationId);
      saveDocState().then(refreshDocView);
    }

    function applyReasonSelection(reason) {
      if (!reason || !reason.code) {
        return;
      }
      setReasonError("");
      doc.header.reason_code = reason.code;
      doc.header.reason_label = reason.label || reason.code;
      saveDocState().then(refreshDocView);
    }

    if (partnerPickBtn) {
      partnerPickBtn.addEventListener("click", function () {
        if (doc.status !== "DRAFT") {
          return;
        }
        openPartnerPicker(getPartnerRoleForOp(doc.op), applyPartnerSelection);
      });
    }

    if (toPickBtn) {
      toPickBtn.addEventListener("click", function () {
        if (doc.status !== "DRAFT") {
          return;
        }
        openLocationPicker(function (location) {
          applyLocationSelection("to", location);
        });
      });
    }

    if (fromPickBtn) {
      fromPickBtn.addEventListener("click", function () {
        if (doc.status !== "DRAFT") {
          return;
        }
        openLocationPicker(function (location) {
          applyLocationSelection("from", location);
        });
      });
    }

    if (orderPickBtn) {
      orderPickBtn.addEventListener("click", function () {
        if (doc.status !== "DRAFT") {
          return;
        }
        openOrderPicker(applyOrderSelection);
      });
    }

    if (orderInfoBtn) {
      orderInfoBtn.addEventListener("click", function () {
        if (!doc.header.order_id) {
          return;
        }
        navigate("/order/" + encodeURIComponent(doc.header.order_id));
      });
    }

    if (locationPickBtn) {
      locationPickBtn.addEventListener("click", function () {
        if (doc.status !== "DRAFT") {
          return;
        }
        openLocationPicker(function (location) {
          applyLocationSelection("location", location);
        });
      });
    }
    if (reasonPickBtn) {
      reasonPickBtn.addEventListener("click", function () {
        if (doc.status !== "DRAFT") {
          return;
        }
        openReasonPicker(applyReasonSelection);
      });
    }
    if (finishBtn) {
      finishBtn.addEventListener("click", function () {
        if (doc.status !== "DRAFT") {
          return;
        }
        if (!doc.lines.length) {
          alert("Добавьте хотя бы одну строку перед завершением.");
          return;
        }
        if (!validateBeforeFinish()) {
          var missingFields = getMissingLocationFields(doc);
          if ((doc.op === "INBOUND" || doc.op === "PRODUCTION_RECEIPT") && missingFields.indexOf("to") >= 0) {
            alert(
              doc.op === "PRODUCTION_RECEIPT"
                ? "Для выпуска продукции выберите место хранения (Куда)."
                : "Для приемки выберите место хранения (Куда)."
            );
          }
          return;
        }

        finishBtn.disabled = true;
        setDocStatus("Отправка на сервер...");
        submitDocToServer(doc)
          .then(function () {
            setDocStatus("Отправлено на сервер");
            return TsdStorage.deleteDoc(doc.id).catch(function () {
              return true;
            });
          })
          .then(function () {
            var targetRoute = "/docs";
            if (doc && doc.op && OPS[doc.op]) {
              targetRoute = "/docs/" + encodeURIComponent(doc.op);
            }
            navigate(targetRoute);
          })
          .catch(function (error) {
            var message = error && error.message ? error.message : "Ошибка отправки на сервер";
            setDocStatus(message);
            finishBtn.disabled = false;
          });
      });
    }

    if (revertBtn) {
      revertBtn.addEventListener("click", function () {
        if (doc.status !== "READY") {
          return;
        }
        doc.status = "DRAFT";
        saveDocState().then(refreshDocView);
      });
    }


    headerInputs.forEach(function (input) {
      var handler = function () {
        var field = input.getAttribute("data-header");
        if (!field) {
          return;
        }
        if (field === "hu") {
          return;
        }
        doc.header[field] = input.value;
        if (field === "partner") {
          doc.header.partner_id = null;
        }
        if (field === "from") {
          doc.header.from_name = null;
          doc.header.from_id = null;
        }
        if (field === "to") {
          doc.header.to_name = null;
          doc.header.to_id = null;
        }
        if (field === "location") {
          doc.header.location_name = null;
          doc.header.location_id = null;
        }
        saveDocState();
      };

      if (input.tagName === "SELECT") {
        input.addEventListener("change", handler);
      } else {
        input.addEventListener("input", handler);
      }
    });

    if (moveInternalToggle) {
      moveInternalToggle.addEventListener("change", function () {
        if (!isDraftDoc) {
          return;
        }
        doc.header.move_internal = !!moveInternalToggle.checked;
        if (doc.header.move_internal) {
          if (doc.header.from_id) {
            doc.header.to = doc.header.from || "";
            doc.header.to_name = doc.header.from_name || null;
            doc.header.to_id = doc.header.from_id || null;
          } else {
            doc.header.to = "";
            doc.header.to_name = null;
            doc.header.to_id = null;
          }
        }
        saveDocState().then(refreshDocView);
      });
    }

    if (huClearBtn) {
      huClearBtn.addEventListener("click", function () {
        if (!isDraftDoc) {
          return;
        }
        doc.header.hu = "";
        setHuError("", "hu");
        setHuDisplay("", "hu");
        saveDocState();
      });
    }

    if (huFromScanBtn) {
      huFromScanBtn.addEventListener("click", function () {
        if (!isDraftDoc) {
          return;
        }
        doc.header.from_hu = "";
        setHuError("", "from_hu");
        setHuDisplay("", "from_hu");
        saveDocState();
      });
    }

    if (huToScanBtn) {
      huToScanBtn.addEventListener("click", function () {
        if (!isDraftDoc) {
          return;
        }
        doc.header.to_hu = "";
        setHuError("", "to_hu");
        setHuDisplay("", "to_hu");
        saveDocState();
      });
    }

    locationFieldInputs.forEach(function (input) {
      var field = input.getAttribute("data-location-field");
      if (!field) {
        return;
      }
      input.addEventListener("input", function () {
        setLocationError(field, "");
      });
      input.addEventListener("keydown", function (event) {
        if (event.key === "Enter") {
          event.preventDefault();
          handleLocationEntry(field);
        }
      });
    });

    manualInputButtons.forEach(function (btn) {
      btn.addEventListener("click", function (event) {
        if (event) {
          event.preventDefault();
        }
        if (!isDraftDoc) {
          return;
        }
        var target = btn.getAttribute("data-manual");
        if (!target) {
          return;
        }
        if (target === "barcode") {
          openManualInputOverlay({
            title: "Ручной ввод",
            label: "Штрихкод",
            placeholder: "Введите штрихкод",
            inputMode: "text",
            onClose: function () {
              focusBarcode();
            },
            onSubmit: function (value) {
              focusBarcode();
              handleAddLine(value);
            },
          });
          return;
        }
        openManualInputOverlay({
          title: "Ручной ввод",
          label: "Код локации",
          placeholder: "Введите код локации",
          inputMode: "text",
          onClose: function () {
            focusBarcode();
          },
          onSubmit: function (value) {
            handleLocationEntry(target, value);
          },
        });
      });
    });

    var lineControlButtons = document.querySelectorAll(".line-control-btn");
    Array.prototype.forEach.call(lineControlButtons, function (btn) {
      btn.addEventListener("click", function (event) {
        if (event) {
          event.preventDefault();
        }
        if (!isDraftDoc) {
          return;
        }
        var action = btn.getAttribute("data-action");
        var index = parseInt(btn.getAttribute("data-index"), 10);
        if (isNaN(index)) {
          return;
        }
        if (action === "minus") {
          adjustLineAt(index, -1);
        }
        if (action === "plus") {
          adjustLineAt(index, normalizeQtyStep(qtyStep));
        }
      });
    });

    applyCatalogState();
    hydrateHeaderFromCatalog();

    refreshHuHeaderDisplay();
    focusFirstLocationOrBarcode();
  }

  function wireSettings() {
    var deviceIdValue = document.getElementById("deviceIdValue");
    var softKeyboardToggle = document.getElementById("softKeyboardToggle");
    var scannerSelect = document.getElementById("scannerModeSelect");
    var scannerHint = document.getElementById("scannerModeHint");
    var scanDebugPanelToggle = document.getElementById("scanDebugPanelToggle");
    var scanDebugPanel = document.getElementById("scanDebugPanel");
    var scanDebugLog = document.getElementById("scanDebugLog");
    var scanDebugState = document.getElementById("scanDebugState");
    var scanDebugFocusBtn = document.getElementById("scanDebugFocusBtn");
    var scanDebugStateBtn = document.getElementById("scanDebugStateBtn");
    var scanDebugClearBtn = document.getElementById("scanDebugClearBtn");
    var scanDebugCopyBtn = document.getElementById("scanDebugCopyBtn");

    function renderDeviceId(value) {
      if (!deviceIdValue) {
        return;
      }
      var clean = value ? String(value).trim() : "";
      deviceIdValue.textContent = clean || "Не задан";
    }

    function renderScannerHint() {
      if (!scannerHint) {
        return;
      }
      var provider =
        scannerManager && scannerManager.getProviderType
          ? scannerManager.getProviderType()
          : "keyboard";
      scannerHint.textContent = "Текущий провайдер: " + provider;
    }

    TsdStorage.getSetting("device_id")
      .then(function (value) {
        renderDeviceId(value);
      })
      .catch(function () {
        renderDeviceId("");
      });

    if (scannerSelect) {
      TsdStorage.getSetting(SCANNER_MODE_KEY)
        .then(function (value) {
          scannerSelect.value = normalizeScannerMode(value);
          renderScannerHint();
        })
        .catch(function () {
          scannerSelect.value = normalizeScannerMode(scannerMode);
          renderScannerHint();
        });

      scannerSelect.addEventListener("change", function () {
        var value = normalizeScannerMode(scannerSelect.value);
        updateScannerMode(value).then(function () {
          renderScannerHint();
        });
      });
    }

    if (softKeyboardToggle) {
      softKeyboardToggle.checked = softKeyboardEnabled;
      softKeyboardToggle.addEventListener("change", function () {
        var enabled = !!softKeyboardToggle.checked;
        setSoftKeyboardEnabled(enabled);
        TsdStorage.setSetting(SOFT_KEYBOARD_KEY, enabled).catch(function () {
          return false;
        });
      });
    }

    function setDebugPanelOpen(open) {
      var isOpen = !!open;
      if (scanDebugPanel) {
        scanDebugPanel.classList.toggle("is-collapsed", !isOpen);
      }
      if (scanDebugPanelToggle) {
        scanDebugPanelToggle.checked = isOpen;
      }
      setScanDebugEnabled(isOpen);
      if (isOpen && scanDebug.stateEl) {
        scanDebug.stateEl.textContent = formatScanState(getScanStateSnapshot());
      }
    }

    if (scanDebugLog || scanDebugState) {
      mountScanDebugUI(scanDebugLog, scanDebugState);
      if (scanDebugPanelToggle || scanDebugPanel) {
        TsdStorage.getSetting(SCAN_DEBUG_OPEN_KEY)
          .then(function (value) {
            setDebugPanelOpen(!!value);
          })
          .catch(function () {
            setDebugPanelOpen(false);
          });
      }
    }

    if (scanDebugPanelToggle) {
      scanDebugPanelToggle.addEventListener("change", function () {
        var open = !!scanDebugPanelToggle.checked;
        setDebugPanelOpen(open);
        TsdStorage.setSetting(SCAN_DEBUG_OPEN_KEY, open).catch(function () {
          return false;
        });
      });
    }

    if (scanDebugFocusBtn) {
      scanDebugFocusBtn.addEventListener("click", function () {
        enterScanMode();
        appendScanDebug("focus", formatDebugTarget(document.activeElement));
      });
    }

    if (scanDebugStateBtn) {
      scanDebugStateBtn.addEventListener("click", function () {
        if (scanDebug.stateEl) {
          scanDebug.stateEl.textContent = formatScanState(getScanStateSnapshot());
        }
        appendScanDebug("snapshot", "");
      });
    }

    if (scanDebugClearBtn) {
      scanDebugClearBtn.addEventListener("click", function () {
        clearScanDebugLog();
      });
    }

    if (scanDebugCopyBtn) {
      scanDebugCopyBtn.addEventListener("click", function () {
        var text = scanDebug.log.join("\n");
        if (!text) {
          return;
        }
        if (navigator.clipboard && navigator.clipboard.writeText) {
          navigator.clipboard.writeText(text).then(function () {
            appendScanDebug("copy", "ok");
          });
        } else {
          window.prompt("Скопируйте лог:", text);
        }
      });
    }
  }

  function wireLogin() {
    var loginInput = document.getElementById("loginInput");
    var passwordInput = document.getElementById("passwordInput");
    var loginBtn = document.getElementById("loginBtn");
    var statusEl = document.getElementById("loginStatus");
    var redirecting = false;

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text || "";
      }
    }

    function submit() {
      if (!loginInput || !passwordInput || !loginBtn) {
        return;
      }
      var login = loginInput.value ? loginInput.value.trim() : "";
      var password = passwordInput.value || "";
      if (!login || !password) {
        setStatus("Введите логин и пароль.");
        return;
      }
      loginBtn.disabled = true;
      setStatus("Подключение...");
      TsdStorage.apiLogin(login, password)
        .then(function (result) {
          var deviceId = result && result.device_id ? String(result.device_id).trim() : "";
          var platform = normalizePlatform(result && result.platform);
          var pcPort = result && result.pc_port ? String(result.pc_port).trim() : "";
          if (!deviceId) {
            throw new Error("NO_DEVICE_ID");
          }
          applyClientBlocks(result && result.blocks);
          clientBlocksLoadPromise = Promise.resolve(clientBlocks);
          storeAccount(deviceId, platform, login);
          if (platform === "PC") {
            redirecting = true;
            if (!pcPort) {
              pcPort = "7154";
            }
            var targetUrl = new URL(window.location.href);
            targetUrl.protocol = "https:";
            targetUrl.port = pcPort;
            targetUrl.pathname = "/";
            targetUrl.search = "";
            targetUrl.hash = "";
            window.location.href = targetUrl.toString();
            return null;
          }
          return TsdStorage.setSetting("device_id", deviceId);
        })
        .then(function () {
          if (redirecting) {
            return;
          }
          setStatus("");
          navigate("/home");
        })
        .catch(function (error) {
          loginBtn.disabled = false;
          var message = "Ошибка входа.";
          var code = error && error.message ? error.message : "";
          if (code === "INVALID_CREDENTIALS") {
            message = "Пользователь не найден. Обратитесь к оператору.";
          } else if (code === "DEVICE_BLOCKED") {
            message = "Устройство заблокировано. Обратитесь к оператору.";
          } else if (code === "MISSING_CREDENTIALS") {
            message = "Введите логин и пароль.";
          }
          setStatus(message);
        });
    }

    if (loginBtn) {
      loginBtn.addEventListener("click", submit);
    }

    if (passwordInput) {
      passwordInput.addEventListener("keydown", function (event) {
        if (event.key === "Enter") {
          event.preventDefault();
          submit();
        }
      });
    }

    if (loginInput) {
      loginInput.focus();
    }
  }

  function createDocAndOpen(op, navOrigin) {
    if (!OPS[op] || !isOperationEnabled(op)) {
      return;
    }

    var docId = createUuid();
    var eventId = createUuid();

    ensureServerAvailable()
      .then(function () {
        return TsdStorage.getSetting("device_id");
      })
      .then(function (deviceId) {
        return TsdStorage.apiCreateDocDraft(op, docId, eventId, deviceId || null);
      })
      .then(function (payload) {
        var docInfo = payload && payload.doc ? payload.doc : payload;
        var docRef = docInfo && docInfo.doc_ref ? String(docInfo.doc_ref) : "";
        if (!docRef) {
          throw new Error("INVALID_DOC_REF");
        }
        var nowIso = new Date().toISOString();

        var doc = {
          id: docId,
          op: op,
          doc_ref: docRef,
          status: "DRAFT",
          header: getDefaultHeader(op),
          lines: [],
          undoStack: [],
          createdAt: nowIso,
          updatedAt: nowIso,
          exportedAt: null,
        };

        return TsdStorage.saveDoc(doc).then(function () {
          setNavOrigin(navOrigin || "home");
          navigate("/doc/" + encodeURIComponent(docId));
        });
      })
      .catch(function () {
        alert("Не удалось создать документ. Проверьте связь с сервером.");
        return false;
      });
  }

  function getDefaultHeader(op) {
    if (op === "INBOUND") {
      return {
        partner: "",
        partner_id: null,
        hu: "",
        to: "",
        to_name: null,
        to_id: null,
        qtyMode: "ASK",
      };
    }
    if (op === "PRODUCTION_RECEIPT") {
      return {
        hu: "",
        to: "",
        to_name: null,
        to_id: null,
        qtyMode: "ASK",
      };
    }
    if (op === "OUTBOUND") {
      return {
        partner: "",
        partner_id: null,
        hu: "",
        order_id: null,
        order_ref: "",
        from: "",
        from_name: null,
        from_id: null,
        qtyMode: "ASK",
      };
    }
    if (op === "MOVE") {
      return {
        hu: "",
        from: "",
        from_name: null,
        from_id: null,
        to: "",
        to_name: null,
        to_id: null,
        move_internal: false,
        from_hu: "",
        to_hu: "",
        qtyMode: "ASK",
      };
    }
    if (op === "WRITE_OFF") {
      return {
        hu: "",
        from: "",
        from_name: null,
        from_id: null,
        reason_code: null,
        reason_label: null,
        qtyMode: "ASK",
      };
    }
    if (op === "INVENTORY") {
      return {
        hu: "",
        location: "",
        location_name: null,
        location_id: null,
        qtyMode: "ASK",
      };
    }
    return {};
  }

  function normalizeValue(value) {
    var trimmed = String(value || "").trim();
    return trimmed ? trimmed : "";
  }

  function extractHuCode(value) {
    var normalized = normalizeValue(value).toUpperCase();
    if (!normalized) {
      return "";
    }
    normalized = normalized
      .replace(/\u041D/g, "H") // Cyrillic Н -> Latin H
      .replace(/\u0423/g, "U"); // Cyrillic У -> Latin U
    var cleaned = normalized.replace(/[^A-Z0-9-]/g, "");
    if (!cleaned) {
      return "";
    }
    var match = cleaned.match(/HU-?\d{6}/);
    if (!match) {
      return "";
    }
    var digits = match[0].replace(/[^0-9]/g, "");
    if (digits.length !== 6) {
      return "";
    }
    return "HU-" + digits;
  }

  function normalizeHuCode(value) {
    var trimmed = normalizeValue(value);
    if (!trimmed) {
      return "";
    }
    if (trimmed.toUpperCase().indexOf("HU-") !== 0) {
      return null;
    }
    return trimmed.toUpperCase();
  }

  function isHuInOtherLocation(rows, huCode, locationId) {
    if (!locationId) {
      return false;
    }
    var normalized = normalizeHuCode(huCode);
    if (!normalized) {
      return false;
    }
    var hasOther = false;
    rows.forEach(function (row) {
      if (hasOther) {
        return;
      }
      var rowHu = normalizeHuCode(row.hu);
      if (!rowHu || rowHu !== normalized) {
        return;
      }
      if (row.locationId !== locationId) {
        hasOther = true;
      }
    });
    return hasOther;
  }

  function getMissingLocationFields(doc) {
    var header = (doc && doc.header) || {};
    var missing = [];

    if (doc.op === "INBOUND" || doc.op === "PRODUCTION_RECEIPT") {
      if (!normalizeValue(header.to) && !header.to_id) {
        missing.push("to");
      }
      return missing;
    }

    if (doc.op === "OUTBOUND" || doc.op === "WRITE_OFF") {
      if (!normalizeValue(header.from) && !header.from_id) {
        missing.push("from");
      }
      return missing;
    }

    if (doc.op === "MOVE") {
      if (!normalizeValue(header.from) && !header.from_id) {
        missing.push("from");
      }
      if (!normalizeValue(header.to) && !header.to_id) {
        missing.push("to");
      }
      if (header.move_internal) {
        if (!normalizeValue(header.from_hu)) {
          missing.push("from_hu");
        }
        if (!normalizeValue(header.to_hu)) {
          missing.push("to_hu");
        }
        var missingFromHu = normalizeHuCode(header.from_hu);
        var missingToHu = normalizeHuCode(header.to_hu);
        if (missingFromHu && missingToHu && missingFromHu === missingToHu) {
          missing.push("to_hu");
        }
      }
      return missing;
    }

    if (doc.op === "INVENTORY") {
      if (!normalizeValue(header.location) && !header.location_id) {
        missing.push("location");
      }
    }

    return missing;
  }

  function logExportWarning(doc, missingFields) {
    if (typeof console !== "undefined" && console.warn) {
      console.warn("TSD export skipped: missing fields", {
        doc_ref: doc && doc.doc_ref,
        op: doc && doc.op,
        missing: missingFields,
      });
    }
  }

  function submitDocToServer(doc) {
    if (!doc || !doc.lines || !doc.lines.length) {
      return Promise.reject(new Error("Нет строк для отправки"));
    }

    if (
      doc.op !== "INBOUND" &&
      doc.op !== "PRODUCTION_RECEIPT" &&
      doc.op !== "OUTBOUND" &&
      doc.op !== "MOVE" &&
      doc.op !== "WRITE_OFF" &&
      doc.op !== "INVENTORY"
    ) {
      return Promise.reject(new Error("Операция пока не поддерживается на сервере."));
    }

    function mapApiError(code) {
      if (!code) {
        return "Ошибка сервера";
      }
      if (code === "BLOCK_DISABLED") {
        return "Блок временно отключен оператором.";
      }
      if (code === "MISSING_PARTNER") {
        return "Укажите контрагента.";
      }
      if (code === "UNKNOWN_PARTNER") {
        return "Контрагент не найден.";
      }
      if (code === "MISSING_LOCATION") {
        return "Укажите локацию.";
      }
      if (code === "UNKNOWN_LOCATION") {
        return "Локация не найдена.";
      }
      if (code === "UNKNOWN_ITEM") {
        return "Товар не найден.";
      }
      if (code === "ITEM_INACTIVE") {
        return "Карточка товара заблокирована.";
      }
      if (code === "DOC_REF_EXISTS") {
        return "Документ с таким номером уже существует.";
      }
      if (code === "DOC_NOT_DRAFT") {
        return "Документ уже закрыт.";
      }
      if (code === "DOC_NOT_FOUND") {
        return "Документ не найден.";
      }
      if (code === "INVALID_TYPE") {
        return "Тип документа не поддерживается.";
      }
      if (code === "INVALID_QTY") {
        return "Некорректное количество.";
      }
      return code;
    }

    function postJson(url, payload) {
      var blockKey = getOperationBlockKey(doc.op) || currentClientBlockContext;
      return fetch(url, {
        method: "POST",
        headers: createBlockHeaders({ "Content-Type": "application/json" }, blockKey),
        body: JSON.stringify(payload),
      })
        .then(function (response) {
          return response
            .json()
            .catch(function () {
              return null;
            })
            .then(function (payload) {
              if (!response.ok) {
                var errorText = (payload && payload.error) || "SERVER_ERROR";
                throw new Error(mapApiError(errorText));
              }
              if (payload && payload.ok === false) {
                var apiError = payload.error || "SERVER_ERROR";
                throw new Error(mapApiError(apiError));
              }
              return payload;
            });
        });
    }

    function validateHuLocationsForSubmit(currentDoc) {
      var header = (currentDoc && currentDoc.header) || {};
      var fromLocationId = header.from_id || null;
      var toLocationId = header.to_id || null;
      var mainHu = normalizeHuCode(header.hu);
      var fromHu = normalizeHuCode(header.from_hu) || null;
      var toHu = normalizeHuCode(header.to_hu) || null;

      if (currentDoc.op === "INVENTORY") {
        toLocationId = header.location_id || toLocationId;
        fromLocationId = null;
        fromHu = null;
        toHu = null;
      }

      if (currentDoc.op === "INBOUND" || currentDoc.op === "PRODUCTION_RECEIPT") {
        toHu = mainHu || toHu;
      } else if (currentDoc.op === "OUTBOUND" || currentDoc.op === "WRITE_OFF") {
        fromHu = mainHu || fromHu;
      }

      var checks = [];
      if (fromHu && fromLocationId) {
        checks.push({ hu: fromHu, locationId: fromLocationId });
      }
      if (toHu && toLocationId) {
        checks.push({ hu: toHu, locationId: toLocationId });
      }
      if (currentDoc.op === "INVENTORY" && toLocationId) {
        (currentDoc.lines || []).forEach(function (line) {
          var lineHu = normalizeHuCode(line.to_hu);
          if (lineHu) {
            checks.push({ hu: lineHu, locationId: toLocationId });
          }
        });
      }
      if (currentDoc.op === "WRITE_OFF" && fromLocationId) {
        (currentDoc.lines || []).forEach(function (line) {
          var lineHu = normalizeHuCode(line.from_hu);
          if (lineHu) {
            checks.push({ hu: lineHu, locationId: fromLocationId });
          }
        });
      }
      if (!checks.length) {
        return Promise.resolve(true);
      }

      return TsdStorage.apiGetHuStockRows()
        .then(function (rows) {
          for (var i = 0; i < checks.length; i += 1) {
            if (isHuInOtherLocation(rows, checks[i].hu, checks[i].locationId)) {
              throw new Error("HU уже находится в другой локации.");
            }
          }
          return true;
        })
        .catch(function () {
          throw new Error("Ошибка проверки HU.");
        });
    }

    return ensureServerAvailable()
      .then(function () {
        return validateHuLocationsForSubmit(doc);
      })
      .then(function () {
        return Promise.all([getServerBaseUrl(), TsdStorage.getSetting("device_id")]);
      })
      .then(function (result) {
        var baseUrl = result[0];
        var deviceId = result[1] || null;
        var header = doc.header || {};
        var docUid = doc.id;
        var fromLocationId = header.from_id || null;
        var toLocationId = header.to_id || null;
        var mainHu = normalizeHuCode(header.hu);
        var fromHu = normalizeHuCode(header.from_hu) || null;
        var toHu = normalizeHuCode(header.to_hu) || null;

        if (doc.op === "INVENTORY") {
          toLocationId = header.location_id || toLocationId;
          fromHu = null;
          toHu = null;
        }

        if (doc.op === "INBOUND" || doc.op === "PRODUCTION_RECEIPT") {
          toHu = mainHu || toHu;
        } else if (doc.op === "OUTBOUND" || doc.op === "WRITE_OFF") {
          fromHu = mainHu || fromHu;
        }

        var docPayload = {
          doc_uid: docUid,
          event_id: createUuid(),
          device_id: deviceId,
          type: doc.op,
          doc_ref: doc.doc_ref || null,
          comment: "TSD",
          reason_code: doc.op === "WRITE_OFF" ? normalizeValue(header.reason_code) || null : null,
          partner_id: header.partner_id || null,
          order_id: header.order_id || null,
          order_ref: header.order_ref ? header.order_ref : null,
          from_location_id: fromLocationId,
          to_location_id: toLocationId,
          from_hu: fromHu || null,
          to_hu: toHu || null,
        };

        return postJson(baseUrl + "/api/docs", docPayload).then(function (createResult) {
          var docInfo = createResult && createResult.doc ? createResult.doc : null;
          if (docInfo && docInfo.doc_ref) {
            var resolvedRef = String(docInfo.doc_ref);
            if (resolvedRef) {
              doc.doc_ref = resolvedRef;
            }
            if (docInfo.doc_ref_changed) {
              alert("Номер документа занят. Присвоен новый: " + resolvedRef);
            }
          }
          return doc.lines.reduce(function (chain, line) {
            return chain.then(function () {
              var qty = Number(line.qty) || 0;
              var linePayload = {
                event_id: createUuid(),
                device_id: deviceId,
                qty: qty,
              };
              var lineFromHu = normalizeHuCode(line.from_hu);
              var lineToHu = normalizeHuCode(line.to_hu);
              if (line.itemId) {
                linePayload.item_id = line.itemId;
              } else {
                linePayload.barcode = line.barcode || "";
              }
              var lineOrderId = line.orderLineId != null ? Number(line.orderLineId) : null;
              if (lineOrderId && !isNaN(lineOrderId)) {
                linePayload.order_line_id = lineOrderId;
              }
              var lineFromId = line.from_id != null ? Number(line.from_id) : null;
              if (lineFromId && !isNaN(lineFromId)) {
                linePayload.from_location_id = lineFromId;
              }
              var lineToId = line.to_id != null ? Number(line.to_id) : null;
              if (lineToId && !isNaN(lineToId)) {
                linePayload.to_location_id = lineToId;
              }
              if (lineFromHu) {
                linePayload.from_hu = lineFromHu;
              }
              if (lineToHu) {
                linePayload.to_hu = lineToHu;
              }
              return postJson(baseUrl + "/api/docs/" + encodeURIComponent(docUid) + "/lines", linePayload);
            });
          }, Promise.resolve(true)).then(function () {
            return postJson(baseUrl + "/api/docs/" + encodeURIComponent(docUid) + "/close", {
              event_id: createUuid(),
              device_id: deviceId,
            });
          });
        });
      });
  }

  function buildLineData(op, header) {
    var from = null;
    var to = null;
    var reason = null;
    var fromHu = null;
    var toHu = null;
    var fromId = null;
    var toId = null;

    if (op === "INBOUND" || op === "PRODUCTION_RECEIPT") {
      to = normalizeValue(header.to) || null;
      toId = header.to_id || null;
    } else if (op === "OUTBOUND") {
      from = normalizeValue(header.from) || null;
      fromId = header.from_id || null;
    } else if (op === "MOVE") {
      from = normalizeValue(header.from) || null;
      to = normalizeValue(header.to) || null;
      fromId = header.from_id || null;
      toId = header.to_id || null;
      fromHu = normalizeHuCode(header.from_hu) || null;
      toHu = normalizeHuCode(header.to_hu) || null;
    } else if (op === "WRITE_OFF") {
      from = normalizeValue(header.from) || null;
      fromId = header.from_id || null;
      reason = normalizeValue(header.reason_code) || null;
    } else if (op === "INVENTORY") {
      to = normalizeValue(header.location) || null;
      toId = header.location_id || null;
      toHu = normalizeHuCode(header.hu) || null;
    }

    return {
      from: from,
      to: to,
      from_id: fromId,
      to_id: toId,
      from_hu: fromHu,
      to_hu: toHu,
      reason_code: reason,
    };
  }

  function buildLineKey(op, barcode, lineData) {
    var safeBarcode = normalizeValue(barcode);
    if (op === "INBOUND" || op === "PRODUCTION_RECEIPT") {
      return safeBarcode + "|" + (lineData.to || "");
    }
    if (op === "OUTBOUND") {
      return safeBarcode + "|" + (lineData.from || "");
    }
    if (op === "MOVE") {
      return (
        safeBarcode +
        "|" +
        (lineData.from || "") +
        "|" +
        (lineData.to || "") +
        "|" +
        (lineData.from_hu || "") +
        "|" +
        (lineData.to_hu || "")
      );
    }
    if (op === "WRITE_OFF") {
      return safeBarcode + "|" + (lineData.from || "") + "|" + (lineData.reason_code || "");
    }
    if (op === "INVENTORY") {
      return safeBarcode + "|" + (lineData.to || "") + "|" + (lineData.to_hu || "");
    }
    return safeBarcode;
  }

  function findLineIndex(op, lines, barcode, lineData) {
    var targetKey = buildLineKey(op, barcode, lineData);
    for (var i = 0; i < lines.length; i += 1) {
      var lineKey = buildLineKey(op, lines[i].barcode, lines[i]);
      if (lineKey === targetKey) {
        return i;
      }
    }
    return -1;
  }

  function isHeaderComplete(op, header) {
    var fields = REQUIRED_FIELDS[op] || [];
    for (var i = 0; i < fields.length; i += 1) {
      var value = normalizeValue(header[fields[i]]);
      if (!value) {
        return false;
      }
    }
    return true;
  }
  function exportAllJsonl(statusElementId) {
    var statusEl = statusElementId ? document.getElementById(statusElementId) : null;
    void statusEl;
    return;

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text;
      }
    }

    function downloadJsonl(filename, lines, errorText) {
      if (!lines.length) {
        return false;
      }

      try {
        var blob = new Blob([lines.join("\n")], { type: "application/jsonl" });
        var url = URL.createObjectURL(blob);
        var link = document.createElement("a");
        link.href = url;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
        URL.revokeObjectURL(url);
      } catch (error) {
        setStatus(errorText);
        console.error("TSD shift export failed", error);
        return false;
      }

      return true;
    }

    Promise.all([
      TsdStorage.listDocs(),
      TsdStorage.listLocalItemsForExport(),
      TsdStorage.getSetting("device_id"),
    ])
      .then(function (results) {
        var docs = results[0] || [];
        var items = results[1] || [];
        var deviceId = results[2] || "CT48-01";
        var readyDocs = docs.filter(function (doc) {
          return doc.status === "READY";
        });
        var exportableDocs = [];
        var skippedDocs = [];
        readyDocs.forEach(function (doc) {
          var missingFields = getMissingLocationFields(doc);
          if (missingFields.length) {
            logExportWarning(doc, missingFields);
            skippedDocs.push({ doc: doc, missing: missingFields });
            return;
          }
          exportableDocs.push(doc);
        });

        if (!exportableDocs.length && !items.length) {
          if (skippedDocs.length) {
            setStatus("Есть документы без обязательных полей. Проверьте Куда/Откуда.");
            return;
          }
          setStatus("Нет данных для экспорта");
          return;
        }

        var now = new Date();
        var nowIso = now.toISOString();
        var dateKey = getDateKey(now);
        var timeKey = getTimeKey(now);

        var itemLines = items.map(function (item) {
          var barcode =
            item.barcode ||
            (Array.isArray(item.barcodes) && item.barcodes[0]) ||
            "";
          var record = {
            schema_version: 1,
            event_id: createUuid(),
            ts: nowIso,
            device_id: deviceId,
            event: "ITEM_UPSERT",
            item: {
              name: item.name || "",
              barcode: barcode || "",
              gtin: item.gtin || "",
              base_uom: item.base_uom || "",
            },
          };
          return JSON.stringify(record);
        });

        var opLines = [];
        exportableDocs.forEach(function (doc) {
          var header = doc.header || {};
          var fallbackFrom = normalizeValue(header.from) || null;
          var fallbackTo = normalizeValue(header.to) || null;
          var fallbackReason = normalizeValue(header.reason_code) || null;
          var huCode = normalizeHuCode(header.hu);
          if (doc.op === "INVENTORY") {
            fallbackTo = normalizeValue(header.location) || null;
          }

          (doc.lines || []).forEach(function (line) {
            var record = {
              schema_version: 1,
              event_id: createUuid(),
              ts: nowIso,
              device_id: deviceId,
              event: "OP",
              op: doc.op,
              doc_ref: doc.doc_ref,
              barcode: line.barcode,
              qty: line.qty,
              from: line.from || fallbackFrom,
              to: line.to || fallbackTo,
              partner_id: header.partner_id ? header.partner_id : null,
              order_ref: header.order_ref ? header.order_ref : null,
              reason_code: line.reason_code || fallbackReason,
              hu_code: doc.op === "MOVE" ? null : huCode || null,
            };
            if (doc.op === "MOVE") {
              record.from_loc = line.from || fallbackFrom;
              record.to_loc = line.to || fallbackTo;
              record.from_hu = normalizeHuCode(line.from_hu || header.from_hu) || null;
              record.to_hu = normalizeHuCode(line.to_hu || header.to_hu) || null;
            }
            opLines.push(JSON.stringify(record));
          });
        });

        var opsWarning = "";
        if (!opLines.length) {
          if (exportableDocs.length) {
            opsWarning = "Нет строк для экспорта операций";
          } else if (skippedDocs.length) {
            opsWarning = "Пропущено операций: " + skippedDocs.length + " (нет Куда/Откуда)";
          }
        }

        if (!opLines.length && !itemLines.length) {
          if (opsWarning) {
            setStatus(opsWarning);
            return;
          }
          setStatus("Нет данных для экспорта");
          return;
        }

        var lines = itemLines.concat(opLines);
        var filename = "SHIFT_" + dateKey + "_" + deviceId + "_" + timeKey + ".jsonl";
        if (!downloadJsonl(filename, lines, "Ошибка экспорта смены")) {
          setStatus("Ошибка экспорта смены");
          return;
        }

        var exportedAt = new Date().toISOString();
        var saveTasks = [];

        if (opLines.length) {
          var saveDocs = exportableDocs.map(function (doc) {
            doc.status = "EXPORTED";
            doc.exportedAt = exportedAt;
            doc.updatedAt = exportedAt;
            return TsdStorage.saveDoc(doc);
          });
          saveTasks.push(Promise.all(saveDocs));
        }

        if (itemLines.length) {
          var ids = items.map(function (item) {
            return item.itemId;
          });
          saveTasks.push(TsdStorage.markLocalItemsExported(ids, exportedAt));
        }

        Promise.all(saveTasks)
          .then(function () {
            var parts = [];
            if (itemLines.length) {
              parts.push("Товары: " + items.length);
            }
            if (opLines.length) {
              parts.push(
                "Операции: " + exportableDocs.length + " (" + opLines.length + " строк)"
              );
            } else if (opsWarning) {
              parts.push(opsWarning);
            }
            setStatus("Экспортировано: " + parts.join(" · "));
            console.info("TSD shift export complete", {
              items: itemLines.length,
              ops: opLines.length,
              device_id: deviceId,
              file: filename,
            });
            if (opLines.length && currentRoute && currentRoute.name === "docs") {
              renderRoute();
            }
          })
          .catch(function (error) {
            console.error("TSD shift export status save failed", error);
            setStatus("Ошибка сохранения статусов");
          });
      })
      .catch(function (error) {
        console.error("TSD shift export failed", error);
        setStatus("Ошибка экспорта");
      });
  }
  function exportReadyDocs(statusElementId) {
    var statusEl = statusElementId ? document.getElementById(statusElementId) : null;
    void statusEl;
    return;

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text;
      }
    }

    Promise.all([TsdStorage.listDocs(), TsdStorage.getSetting("device_id")])
      .then(function (results) {
        var docs = results[0] || [];
        var deviceId = results[1] || "CT48-01";
        var readyDocs = docs.filter(function (doc) {
          return doc.status === "READY";
        });
        var exportableDocs = [];
        var skippedDocs = [];
        readyDocs.forEach(function (doc) {
          var missingFields = getMissingLocationFields(doc);
          if (missingFields.length) {
            logExportWarning(doc, missingFields);
            skippedDocs.push({ doc: doc, missing: missingFields });
            return;
          }
          exportableDocs.push(doc);
        });

        if (!exportableDocs.length) {
          if (skippedDocs.length) {
            setStatus("Есть операции без обязательных полей. Проверьте Куда/Откуда.");
            return;
          }
          setStatus("Нет готовых операций для экспорта");
          return;
        }

        var now = new Date();
        var nowIso = now.toISOString();
        var dateKey = getDateKey(now);
        var timeKey = getTimeKey(now);
        var filename = "SHIFT_" + dateKey + "_" + deviceId + "_" + timeKey + ".jsonl";
        var lines = [];

        exportableDocs.forEach(function (doc) {
          var header = doc.header || {};
          var fallbackFrom = normalizeValue(header.from) || null;
          var fallbackTo = normalizeValue(header.to) || null;
          var fallbackReason = normalizeValue(header.reason_code) || null;
          var huCode = normalizeHuCode(header.hu);
          if (doc.op === "INVENTORY") {
            fallbackTo = normalizeValue(header.location) || null;
          }

          (doc.lines || []).forEach(function (line) {
            var record = {
              event_id: createUuid(),
              ts: nowIso,
              device_id: deviceId,
              op: doc.op,
              doc_ref: doc.doc_ref,
              barcode: line.barcode,
              qty: line.qty,
              from: line.from || fallbackFrom,
              to: line.to || fallbackTo,
              partner_id: header.partner_id ? header.partner_id : null,
              order_id: header.order_id ? header.order_id : null,
              order_ref: header.order_ref ? header.order_ref : null,
              reason_code: line.reason_code || fallbackReason,
            };
            if (doc.op === "MOVE") {
              record.from_loc = line.from || fallbackFrom;
              record.to_loc = line.to || fallbackTo;
              record.from_hu = normalizeHuCode(line.from_hu || header.from_hu) || null;
              record.to_hu = normalizeHuCode(line.to_hu || header.to_hu) || null;
              record.hu_code = null;
            } else {
              record.hu_code = huCode || null;
            }
            lines.push(JSON.stringify(record));
          });
        });

        if (!lines.length) {
          setStatus("Нет строк для экспорта");
          return;
        }

        try {
          var blob = new Blob([lines.join("\n")], { type: "application/jsonl" });
          var url = URL.createObjectURL(blob);
          var link = document.createElement("a");
          link.href = url;
          link.download = filename;
          document.body.appendChild(link);
          link.click();
          document.body.removeChild(link);
          URL.revokeObjectURL(url);
        } catch (error) {
          setStatus("Ошибка экспорта");
          return;
        }

        var exportedAt = new Date().toISOString();
        var saveTasks = exportableDocs.map(function (doc) {
          doc.status = "EXPORTED";
          doc.exportedAt = exportedAt;
          doc.updatedAt = exportedAt;
          return TsdStorage.saveDoc(doc);
        });

        Promise.all(saveTasks)
          .then(function () {
            var message = "Экспортировано: " + exportableDocs.length + " операций";
            if (skippedDocs.length) {
              message += " (пропущено: " + skippedDocs.length + ")";
            }
            setStatus(message);
            if (currentRoute && currentRoute.name === "docs") {
              renderRoute();
            }
          })
          .catch(function () {
            setStatus("Ошибка сохранения статусов");
          });
      })
      .catch(function () {
        if (statusEl) {
          statusEl.textContent = "Ошибка экспорта";
        }
      });
  }

  function renderPickerRow(options) {
    var label = options.label || "";
    var value = options.value || "";
    var valueClass = options.valueClass || "field-value";
    var valueId = options.valueId ? ' id="' + options.valueId + '"' : "";
    var pickId = options.pickId || "";
    var disabled = options.disabled ? "disabled" : "";
    return (
      '  <div class="field-row">' +
      '    <div class="field-label">' +
      escapeHtml(label) +
      "</div>" +
      '    <div class="' +
      valueClass +
      '"' +
      valueId +
      ">" +
      escapeHtml(value) +
      "</div>" +
      '    <button class="btn btn-outline field-pick" id="' +
      pickId +
      '" type="button" ' +
      disabled +
      ">+</button>" +
      "  </div>"
    );
  }

  function renderHuField(header, isDraft) {
    var huValue = normalizeValue(header.hu);
    var huLabel = huValue ? "HU: " + huValue : "HU: не задан";
    return (
      '  <div class="field-row field-row-hu" id="huPickerRow">' +
      '    <div class="field-label">HU</div>' +
      '    <div class="field-value" id="huValue">' +
      escapeHtml(huLabel) +
      "</div>" +
      '    <button class="btn btn-ghost field-action" id="huClearBtn" type="button" ' +
      (isDraft ? "" : "disabled") +
      ">Сбросить</button>" +
      "  </div>" +
      '  <div class="field-error" id="huError"></div>'
    );
  }

  function renderMoveInternalRow(header, isDraft) {
    var isChecked = header.move_internal ? "checked" : "";
    var disabled = isDraft ? "" : "disabled";
    return (
      '  <div class="field-row">' +
      '    <div class="field-label">Перемещение</div>' +
      '    <div class="field-value field-value-toggle">' +
      '      <label class="toggle-inline">' +
      '        <input type="checkbox" id="moveInternalToggle" ' +
      isChecked +
      " " +
      disabled +
      " />" +
      "        <span>Внутри склада</span>" +
      "      </label>" +
      "    </div>" +
      "    <div></div>" +
      "  </div>"
    );
  }

  function renderMoveHuField(label, valueId, scanId, errorId, value, isDraft) {
    var huValue = normalizeValue(value);
    var huLabel = huValue ? "HU: " + huValue : "HU: не задан";
    return (
      '  <div class="field-row field-row-hu">' +
      '    <div class="field-label">' +
      escapeHtml(label) +
      "</div>" +
      '    <div class="field-value" id="' +
      valueId +
      '">' +
      escapeHtml(huLabel) +
      "</div>" +
      '    <button class="btn btn-ghost field-action" id="' +
      scanId +
      '" type="button" ' +
      (isDraft ? "" : "disabled") +
      ">Сбросить</button>" +
      "  </div>" +
      '  <div class="field-error" id="' +
      errorId +
      '"></div>'
    );
  }

  document.addEventListener("DOMContentLoaded", function () {
    TsdStorage.init()
      .then(function () {
        return TsdStorage.ensureDefaults();
      })
      .then(function () {
        return TsdStorage.getSetting(SOFT_KEYBOARD_KEY)
          .then(function (value) {
            softKeyboardEnabled = normalizeSoftKeyboardSetting(value);
          })
          .catch(function () {
            softKeyboardEnabled = false;
          });
      })
      .then(function () {
        return initScannerManager();
      })
      .then(function () {
        if (isDebugMode()) {
          setScanDebugEnabled(true);
        }
        pingServer(true);
        window.setInterval(function () {
          pingServer(false);
        }, SERVER_PING_INTERVAL);
        window.addEventListener("online", function () {
          pingServer(true);
        });
        window.addEventListener("offline", function () {
          serverStatus.ok = false;
          serverStatus.checkedAt = Date.now();
          updateNetworkStatus();
        });
        document.addEventListener(
          "pointerdown",
          function (event) {
            if (!scanHandlerActive || isManualOverlayOpen()) {
              return;
            }
            if (!shouldFocusForScan(event.target)) {
              return;
            }
            window.setTimeout(function () {
              ensureScanFocus();
            }, 0);
          },
          true
        );
        window.addEventListener("focus", function () {
          ensureScanFocus();
        });
        document.addEventListener(
          "focusin",
          function (event) {
            if (isSoftKeyboardSuppressed()) {
              if (isScanAllowedElement(event.target)) {
                applyScanInputMode(event.target);
                event.target.setAttribute("data-scan-readonly", "1");
                if (shouldLockKeyboardForInput(event.target)) {
                  event.target.readOnly = true;
                }
              } else {
                if (shouldLockKeyboardForInput(event.target)) {
                  event.target.readOnly = true;
                }
                applyInputMode(event.target, true);
              }
              hideVirtualKeyboard();
            }
          },
          true
        );
        document.addEventListener("visibilitychange", function () {
          if (!document.hidden) {
            if (canRefreshClientBlocksForCurrentRoute()) {
              renderRoute();
            }
            ensureScanFocus();
          }
        });
        window.addEventListener("focus", function () {
          if (canRefreshClientBlocksForCurrentRoute()) {
            renderRoute();
          }
        });
        window.addEventListener("flowstock:block-disabled", function () {
          ensureClientBlocksLoaded(true)
            .catch(function () {
              return null;
            })
            .finally(function () {
              renderRoute();
            });
        });

        if (backBtn) {
          backBtn.addEventListener("click", function () {
            if (!currentRoute) {
              navigate("/home");
              return;
            }
            var origin = getNavOrigin();
            if (currentRoute.name === "home") {
              navigate("/home");
            } else if (currentRoute.name === "operations") {
              navigate("/home");
            } else if (currentRoute.name === "docs") {
              navigate("/operations");
            } else if (currentRoute.name === "doc" || currentRoute.name === "new") {
              if (origin === "history") {
                navigate("/docs");
              } else if (origin === "operations") {
                navigate("/operations");
              } else {
                navigate("/home");
              }
            } else if (currentRoute.name === "orders") {
              navigate("/home");
            } else if (currentRoute.name === "order") {
              navigate("/orders");
            } else if (currentRoute.name === "stock" || currentRoute.name === "settings" || currentRoute.name === "items") {
              navigate("/home");
            } else {
              navigate("/home");
            }
          });
        }

        if (settingsBtn) {
          settingsBtn.addEventListener("click", function () {
            navigate("/settings");
          });
        }

        window.addEventListener("hashchange", renderRoute);
        renderRoute();
      })
      .catch(function () {
        if (app) {
          app.innerHTML = renderError("Ошибка инициализации");
        }
      });
  });
})();


