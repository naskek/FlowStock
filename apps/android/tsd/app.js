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
  var TSD_THEME_KEY = "flowstock.tsd.theme";
  var TSD_THEME_LIGHT = "light";
  var TSD_THEME_DARK = "dark";
  var tsdTheme = TSD_THEME_LIGHT;
  var SERVER_PING_INTERVAL = 15000;
  var VERSION_CHECK_INTERVAL = 600000;
  var versionCheckTimerId = 0;
  var knownServerVersion = "";
  var liveEventSource = null;
  var liveReconnectTimerId = 0;
  var liveRefreshTimerId = 0;
  var LIVE_RECONNECT_DELAY_MS = 2500;
  var LIVE_REFRESH_DEBOUNCE_MS = 300;
  var ROUTE_TRANSITION_MS = 190;
  var activeLiveRefreshHandler = null;
  var serverStatus = { ok: null, checkedAt: 0 };
  var lastRouteTransitionKey = "";
  var routeTransitionTimerId = 0;
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
      tsd_warehouse_tasks: false,
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
    if (route.name === "filling" || route.name === "fillingDoc") {
      return getOperationBlockKey("PRODUCTION_RECEIPT") || "tsd_operations";
    }
    if (route.name === "outbound" || route.name === "outboundOrder") {
      return getOperationBlockKey("OUTBOUND") || "tsd_operations";
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
    if (route.name === "tasks" || route.name === "taskDoc") {
      return "tsd_warehouse_tasks";
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

  function finishRouteRender() {
    applySoftKeyboardSetting(app);
    if (!scanHandlerActive) {
      installGlobalHuScanHandler();
    }
    ensureScanFocus();
  }

  function normalizeTsdTheme(value) {
    return String(value || "").trim().toLowerCase() === TSD_THEME_DARK
      ? TSD_THEME_DARK
      : TSD_THEME_LIGHT;
  }

  function getStoredTsdTheme() {
    try {
      return normalizeTsdTheme(window.localStorage.getItem(TSD_THEME_KEY));
    } catch (error) {
      return TSD_THEME_LIGHT;
    }
  }

  function applyTsdTheme(theme) {
    tsdTheme = normalizeTsdTheme(theme);
    var root = document.documentElement;
    if (root) {
      root.classList.toggle("tsd-theme-dark", tsdTheme === TSD_THEME_DARK);
      root.classList.toggle("tsd-theme-light", tsdTheme !== TSD_THEME_DARK);
    }
    try {
      window.localStorage.setItem(TSD_THEME_KEY, tsdTheme);
    } catch (error) {
      return false;
    }
    return true;
  }

  function initTsdTheme() {
    applyTsdTheme(getStoredTsdTheme());
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

  function fetchServerVersion() {
    return getServerBaseUrl()
      .then(function (baseUrl) {
        return fetch(baseUrl + "/api/version", {
          method: "GET",
          cache: "no-store",
          headers: { "Cache-Control": "no-cache" },
        })
          .then(function (response) {
            if (!response.ok) {
              return "";
            }
            return response
              .json()
              .then(function (payload) {
                return payload && payload.version ? String(payload.version).trim() : "";
              })
              .catch(function () {
                return "";
              });
          })
          .catch(function () {
            return "";
          });
      })
      .catch(function () {
        return "";
      });
  }

  function isSensitiveOperationActive() {
    if (scanHandlerActive || pendingScanHandler) {
      return true;
    }
    if (document.querySelector(".filling-preview-overlay")) {
      return true;
    }
    if (!currentRoute) {
      return false;
    }
    return (
      currentRoute.name === "fillingDoc" ||
      currentRoute.name === "outboundOrder" ||
      currentRoute.name === "doc" ||
      currentRoute.name === "new" ||
      currentRoute.name === "taskDoc"
    );
  }

  window.FlowStockTsdIsBusy = isSensitiveOperationActive;

  function checkServerVersionAndReloadIfNeeded() {
    return fetchServerVersion().then(function (version) {
      if (!version) {
        return false;
      }

      if (!knownServerVersion) {
        knownServerVersion = version;
        console.info("[TSD PWA] Версия API сервера: " + version);
        return false;
      }

      if (version !== knownServerVersion) {
        knownServerVersion = version;
        console.info(
          "[TSD PWA] На сервере новая версия API (" +
            version +
            "). Обновление shell выполняется через service worker."
        );
        if (window.TsdSwUpdate && typeof window.TsdSwUpdate.checkForUpdates === "function") {
          window.TsdSwUpdate.checkForUpdates();
        }
      }

      return false;
    });
  }

  function stopVersionWatcher() {
    if (versionCheckTimerId) {
      clearInterval(versionCheckTimerId);
      versionCheckTimerId = 0;
    }
  }

  function startVersionWatcher() {
    stopVersionWatcher();
    checkServerVersionAndReloadIfNeeded();
    versionCheckTimerId = window.setInterval(function () {
      checkServerVersionAndReloadIfNeeded();
    }, VERSION_CHECK_INTERVAL);
  }

  function clearLiveReconnectTimer() {
    if (liveReconnectTimerId) {
      clearTimeout(liveReconnectTimerId);
      liveReconnectTimerId = 0;
    }
  }

  function setLiveRefreshHandler(handler) {
    activeLiveRefreshHandler = typeof handler === "function" ? handler : null;
  }

  function scheduleLiveRefresh() {
    if (!activeLiveRefreshHandler) {
      return;
    }
    if (liveRefreshTimerId) {
      clearTimeout(liveRefreshTimerId);
    }
    liveRefreshTimerId = window.setTimeout(function () {
      liveRefreshTimerId = 0;
      if (!activeLiveRefreshHandler) {
        return;
      }
      activeLiveRefreshHandler();
    }, LIVE_REFRESH_DEBOUNCE_MS);
  }

  function stopLiveUpdates() {
    clearLiveReconnectTimer();
    if (liveRefreshTimerId) {
      clearTimeout(liveRefreshTimerId);
      liveRefreshTimerId = 0;
    }
    if (liveEventSource) {
      try {
        liveEventSource.close();
      } catch (error) {
        // ignore close errors
      }
      liveEventSource = null;
    }
  }

  function startLiveUpdates() {
    stopLiveUpdates();
    if (typeof EventSource === "undefined") {
      return;
    }

    getServerBaseUrl()
      .then(function (baseUrl) {
        var source = new EventSource(baseUrl + "/api/live");
        liveEventSource = source;

        source.addEventListener("changed", function () {
          scheduleLiveRefresh();
        });
        source.onmessage = function () {
          scheduleLiveRefresh();
        };
        source.onerror = function () {
          if (liveEventSource !== source) {
            return;
          }
          try {
            source.close();
          } catch (error) {
            // ignore close errors
          }
          liveEventSource = null;
          clearLiveReconnectTimer();
          liveReconnectTimerId = window.setTimeout(function () {
            liveReconnectTimerId = 0;
            startLiveUpdates();
          }, LIVE_RECONNECT_DELAY_MS);
        };
      })
      .catch(function () {
        clearLiveReconnectTimer();
        liveReconnectTimerId = window.setTimeout(function () {
          liveReconnectTimerId = 0;
          startLiveUpdates();
        }, LIVE_RECONNECT_DELAY_MS);
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

  function clearNavOrigin() {
    if (!window.sessionStorage) {
      return;
    }
    try {
      sessionStorage.removeItem(NAV_ORIGIN_KEY);
    } catch (error) {
      // ignore
    }
  }

  function isRouteOrigin(origin) {
    return typeof origin === "string" && origin.charAt(0) === "/";
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
    DRAFT: "В работе",
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
    PRODUCTION_RECEIPT: [],
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
    if (route.name === "filling" || route.name === "fillingDoc") {
      return isOperationEnabled("PRODUCTION_RECEIPT");
    }
    if (route.name === "outbound" || route.name === "outboundOrder") {
      return isOperationEnabled("OUTBOUND");
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
    if (route.name === "tasks" || route.name === "taskDoc") {
      return isClientBlockEnabled("tsd_warehouse_tasks");
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

  function getOrderStatusInfoForOrder(order) {
    var statusCode = order && order.status;
    var statusDisplay =
      (order && (order.statusDisplay || order.orderStatusDisplay || order.order_status_display)) || "";
    var normalized = normalizeOrderStatusCode(statusCode || statusDisplay);
    if (normalized === "IN_PROGRESS") {
      return { label: "В работе", className: "order-status-pill order-status-progress" };
    }
    if (normalized === "ACCEPTED") {
      return { label: String(statusDisplay || "Готов"), className: "order-status-pill order-status-accepted" };
    }
    if (normalized === "SHIPPED") {
      return { label: String(statusDisplay || "Выполнен"), className: "order-status-pill order-status-shipped" };
    }
    return getOrderStatusInfo(statusDisplay || statusCode);
  }

  function formatPalletCountValue(value) {
    var count = Number(value);
    if (isNaN(count) || count < 0) {
      return "0";
    }
    return String(count);
  }

  function getOrderTypeValue(order) {
    return String((order && (order.orderType || order.order_type)) || "")
      .trim()
      .toUpperCase();
  }

  function isInternalOrder(order) {
    var orderType = getOrderTypeValue(order);
    if (orderType === "INTERNAL" || orderType.indexOf("ВНУТР") !== -1) {
      return true;
    }
    var partnerName = String((order && order.partnerName) || "").trim();
    var partnerId = Number(order && order.partnerId);
    return !partnerName && !partnerId;
  }

  function getOrderPartnerLabel(order) {
    if (isInternalOrder(order)) {
      return "Внутренний заказ";
    }
    var partnerLabel = (order && order.partnerName) || "—";
    if (order && order.partnerInn) {
      partnerLabel += " · ИНН: " + order.partnerInn;
    }
    return partnerLabel;
  }

  function getProductionPalletPlanInfo(order) {
    if (!order || order.needsProductionPalletPlan !== true) {
      return { label: "", className: "order-plan-neutral" };
    }

    if (order.palletPlanStatus) {
      if (order.palletPlanStatus.indexOf("не сформирован") >= 0) {
        return { label: order.palletPlanStatus, className: "order-plan-missing" };
      }
      if (order.palletPlanStatus.indexOf("Наполнение") >= 0) {
        return { label: order.palletPlanStatus, className: "order-plan-ready" };
      }
      return { label: order.palletPlanStatus, className: "order-plan-ready" };
    }

    if (order.hasProductionPalletPlan === true) {
      return { label: "План паллет: сформирован", className: "order-plan-ready" };
    }

    return { label: "План паллет: не сформирован", className: "order-plan-missing" };
  }

  function normalizeOrderStatusCode(status) {
    var raw = String(status || "").trim().toUpperCase();
    if (!raw) {
      return "";
    }
    if (
      raw === "IN_PROGRESS" ||
      raw === "DRAFT" ||
      raw.indexOf("РАБОТ") >= 0 ||
      raw.indexOf("PROGRESS") >= 0 ||
      raw.indexOf("PROCESS") >= 0
    ) {
      return "IN_PROGRESS";
    }
    if (raw === "ACCEPTED" || raw.indexOf("ГОТОВ") >= 0 || raw.indexOf("READY") >= 0) {
      return "ACCEPTED";
    }
    if (
      raw === "SHIPPED" ||
      raw.indexOf("ВЫПОЛН") >= 0 ||
      raw.indexOf("ОТГРУЖ") >= 0 ||
      raw.indexOf("SHIPP") >= 0
    ) {
      return "SHIPPED";
    }
    return raw;
  }

  function isOrderAvailableForShipment(order) {
    var status = normalizeOrderStatusCode(order && order.status);
    return (
      !isInternalOrder(order) &&
      (status === "ACCEPTED" || status === "IN_PROGRESS") &&
      order &&
      order.hasShipmentRemaining === true
    );
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
    if (parts[0] === "filling" && parts[1]) {
      return { name: "fillingDoc", id: decodeURIComponent(parts[1]) };
    }
    if (parts[0] === "filling") {
      return { name: "filling" };
    }
    if (parts[0] === "outbound" && parts[1]) {
      return { name: "outboundOrder", id: decodeURIComponent(parts[1]) };
    }
    if (parts[0] === "outbound") {
      return { name: "outbound" };
    }
    if (parts[0] === "tasks" && parts[1]) {
      return { name: "taskDoc", id: decodeURIComponent(parts[1]) };
    }
    if (parts[0] === "tasks") {
      return { name: "tasks" };
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
    if (parts[0] === "hu" && parts[1]) {
      return { name: "huCard", id: decodeURIComponent(parts[1]) };
    }
    return { name: parts[0] };
  }

  var LAST_ROUTE_KEY = "flowstock_tsd_last_route";

  function normalizeRouteForRestore(route) {
    var value = String(route || "").trim();
    if (!value) {
      return "";
    }

    if (value.charAt(0) !== "/") {
      value = "/" + value.replace(/^#+/, "");
    }

    var routeInfo = null;
    if (value.charAt(0) === "/") {
      routeInfo = getRouteFromPath(value);
    }

    if (!routeInfo || !routeInfo.name || routeInfo.name === "login") {
      return "";
    }

    return value;
  }

  function getRouteFromPath(path) {
    var cleanPath = String(path || "").trim();
    if (cleanPath.charAt(0) === "#") {
      cleanPath = cleanPath.slice(1);
    }
    if (cleanPath.indexOf("/") === 0) {
      cleanPath = cleanPath.slice(1);
    }
    if (!cleanPath) {
      return { name: "home" };
    }

    var parts = cleanPath.split("/");
    if (parts[0] === "operations") {
      return { name: "operations" };
    }
    if (parts[0] === "filling" && parts[1]) {
      return { name: "fillingDoc", id: decodeURIComponent(parts[1]) };
    }
    if (parts[0] === "filling") {
      return { name: "filling" };
    }
    if (parts[0] === "outbound" && parts[1]) {
      return { name: "outboundOrder", id: decodeURIComponent(parts[1]) };
    }
    if (parts[0] === "outbound") {
      return { name: "outbound" };
    }
    if (parts[0] === "tasks" && parts[1]) {
      return { name: "taskDoc", id: decodeURIComponent(parts[1]) };
    }
    if (parts[0] === "tasks") {
      return { name: "tasks" };
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
    if (parts[0] === "hu" && parts[1]) {
      return { name: "huCard", id: decodeURIComponent(parts[1]) };
    }
    return { name: parts[0] };
  }

  function loadLastRoute() {
    try {
      return normalizeRouteForRestore(localStorage.getItem(LAST_ROUTE_KEY));
    } catch (error) {
      return "";
    }
  }

  function saveLastRoute(route) {
    var normalized = normalizeRouteForRestore(route);
    if (!normalized) {
      return;
    }

    try {
      localStorage.setItem(LAST_ROUTE_KEY, normalized);
    } catch (error) {
      // ignore storage failures
    }
  }

  function getRouteTransitionKey(route) {
    if (!route || !route.name) {
      return "home";
    }
    var parts = [route.name];
    if (route.id != null && route.id !== "") {
      parts.push(String(route.id));
    }
    if (route.op != null && route.op !== "") {
      parts.push(String(route.op));
    }
    return parts.join(":");
  }

  function markRouteTransitionExit() {
    if (!app || !app.classList) {
      return;
    }
    app.classList.add("route-transition-exit");
  }

  function prepareRouteTransition(route) {
    if (!app || !app.classList) {
      return false;
    }
    var key = getRouteTransitionKey(route);
    var shouldAnimate = !!lastRouteTransitionKey && key !== lastRouteTransitionKey;
    lastRouteTransitionKey = key;

    if (routeTransitionTimerId) {
      window.clearTimeout(routeTransitionTimerId);
      routeTransitionTimerId = 0;
    }

    app.classList.remove("route-transition-active");
    if (!shouldAnimate) {
      app.classList.remove("route-transition-exit");
      return false;
    }

    app.classList.add("route-transition-active");
    routeTransitionTimerId = window.setTimeout(function () {
      if (app && app.classList) {
        app.classList.remove("route-transition-active");
        app.classList.remove("route-transition-exit");
      }
      routeTransitionTimerId = 0;
    }, ROUTE_TRANSITION_MS + 40);
    return true;
  }

  function navigate(route) {
    if (route) {
      saveLastRoute(route);
      markRouteTransitionExit();
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

  function getBackRouteForRoute(route, origin) {
    if (!route) {
      return "/home";
    }
    var routeOrigin = isRouteOrigin(origin) ? origin : "";
    if (route.name === "home") {
      return "/home";
    }
    if (route.name === "operations") {
      return "/home";
    }
    if (route.name === "filling") {
      return "/operations";
    }
    if (route.name === "fillingDoc") {
      if (routeOrigin) {
        return routeOrigin;
      }
      return "/filling";
    }
    if (route.name === "outbound") {
      return "/operations";
    }
    if (route.name === "outboundOrder") {
      if (routeOrigin) {
        return routeOrigin;
      }
      return "/outbound";
    }
    if (route.name === "docs") {
      return "/operations";
    }
    if (route.name === "doc" && routeOrigin) {
      return routeOrigin;
    }
    if (route.name === "doc" || route.name === "new") {
      if (origin === "history") {
        return "/docs";
      }
      if (origin === "operations") {
        return "/operations";
      }
      return "/home";
    }
    if (route.name === "orders") {
      return "/home";
    }
    if (route.name === "order") {
      if (routeOrigin) {
        return routeOrigin;
      }
      return "/orders";
    }
    if (route.name === "huCard") {
      return routeOrigin || "/hu";
    }
    if (route.name === "stock" || route.name === "settings" || route.name === "items") {
      return "/home";
    }
    return "/home";
  }

  function navigateBack(route) {
    var backRoute = getBackRouteForRoute(route || currentRoute, getNavOrigin());
    navigate(backRoute);
    clearNavOrigin();
    return backRoute;
  }

  function renderRoute() {
    setScanHandler(null);
    setScanInputHandlers(null, null);
    setPreferredScanTarget(null);
    setLiveRefreshHandler(null);
    if (!window.location.hash || window.location.hash === "#") {
      navigate(loadLastRoute() || "/home");
      return;
    }
    var route = getRoute();
    saveLastRoute("/" + (window.location.hash || "").replace(/^#\/?/, ""));
    currentRoute = route;
    prepareRouteTransition(route);
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
      finishRouteRender();
      return;
    }

    if (route.name === "docs") {
      if (!route.op || !isOperationEnabled(route.op)) {
        navigate("/home");
        return;
      }
      if (String(route.op || "").toUpperCase() === "OUTBOUND") {
        navigate("/outbound");
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
          finishRouteRender();
        })
        .catch(function () {
          app.innerHTML = renderError("Ошибка загрузки документов");
          finishRouteRender();
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
      finishRouteRender();
      return;
    }

    if (route.name === "filling") {
      if (!isOperationEnabled("PRODUCTION_RECEIPT")) {
        navigate("/operations");
        return;
      }
      setCurrentClientBlockContext(getOperationBlockKey("PRODUCTION_RECEIPT") || "tsd_operations");
      app.innerHTML = renderFillingLoading();
      TsdStorage.apiGetProductionFillingOrders()
        .then(function (items) {
          app.innerHTML = renderFillingList(items || []);
          wireFillingList();
          finishRouteRender();
        })
        .catch(function (error) {
          console.error(error);
          app.innerHTML = renderError("Не удалось загрузить заказы для наполнения");
          finishRouteRender();
        });
      return;
    }

    if (route.name === "fillingDoc") {
      if (!isOperationEnabled("PRODUCTION_RECEIPT")) {
        navigate("/operations");
        return;
      }
      setCurrentClientBlockContext(getOperationBlockKey("PRODUCTION_RECEIPT") || "tsd_operations");
      app.innerHTML = renderLoading();
      loadFillingContext(route.id)
        .then(function (context) {
          renderFillingScanScreen(context, { message: "", messageType: "", preview: null });
        })
        .catch(function (error) {
          console.error(error);
          app.innerHTML = renderError(String(error && error.message ? error.message : "Ошибка загрузки наполнения"));
          finishRouteRender();
        });
      return;
    }

    if (route.name === "outbound") {
      if (!isOperationEnabled("OUTBOUND")) {
        navigate("/operations");
        return;
      }
      setCurrentClientBlockContext(getOperationBlockKey("OUTBOUND") || "tsd_operations");
      app.innerHTML = renderLoading();
      TsdStorage.apiGetOutboundPickingOrders()
        .then(function (orders) {
          app.innerHTML = renderOutboundPickingList(orders || []);
          wireOutboundPickingList();
          finishRouteRender();
        })
        .catch(function (error) {
          console.error(error);
          app.innerHTML = renderError("Не удалось загрузить заказы для отгрузки");
          finishRouteRender();
        });
      return;
    }

    if (route.name === "outboundOrder") {
      if (!isOperationEnabled("OUTBOUND")) {
        navigate("/operations");
        return;
      }
      setCurrentClientBlockContext(getOperationBlockKey("OUTBOUND") || "tsd_operations");
      app.innerHTML = renderLoading();
      TsdStorage.apiGetOutboundPickingOrder(route.id)
        .then(function (order) {
          renderOutboundPickingOrder(order, { message: "", messageType: "" });
        })
        .catch(function (error) {
          console.error(error);
          app.innerHTML = renderError(String(error && error.message ? error.message : "Ошибка загрузки отгрузки"));
          finishRouteRender();
        });
      return;
    }

    if (route.name === "tasks") {
      if (!isClientBlockEnabled("tsd_warehouse_tasks")) {
        navigate("/home");
        return;
      }
      setCurrentClientBlockContext("tsd_warehouse_tasks");
      app.innerHTML = renderLoading();
      getWarehouseTaskDeviceId()
        .then(function (deviceId) {
          return TsdStorage.apiListWarehouseTasks(deviceId);
        })
        .then(function (tasks) {
          app.innerHTML = renderWarehouseTasksList(tasks || []);
          wireWarehouseTasksList();
          finishRouteRender();
        })
        .catch(function (error) {
          console.error(error);
          app.innerHTML = renderError(mapWarehouseTaskError(error));
          finishRouteRender();
        });
      return;
    }

    if (route.name === "taskDoc") {
      if (!isClientBlockEnabled("tsd_warehouse_tasks")) {
        navigate("/home");
        return;
      }
      setCurrentClientBlockContext("tsd_warehouse_tasks");
      app.innerHTML = renderLoading();
      openWarehouseTaskDetail(route.id, { message: "", messageType: "" });
      return;
    }

    if (route.name === "doc") {
      app.innerHTML = renderLoading();
      if (isServerDocId(route.id)) {
        TsdStorage.apiGetDocById(route.id)
          .then(function (doc) {
            if (!doc) {
              app.innerHTML = renderError("Документ не найден");
              finishRouteRender();
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
                finishRouteRender();
              })
              .catch(function () {
                app.innerHTML = renderError("Ошибка загрузки строк документа");
                finishRouteRender();
              });
          })
          .catch(function () {
            app.innerHTML = renderError("Ошибка загрузки документа");
            finishRouteRender();
          });
      } else {
        TsdStorage.getDoc(route.id)
          .then(function (doc) {
            if (!doc) {
              app.innerHTML = renderError("Документ не найден");
              finishRouteRender();
              return;
            }
            if (!isOperationEnabled(doc.op)) {
              navigate("/home");
              return;
            }
            setCurrentClientBlockContext(getOperationBlockKey(doc.op) || currentClientBlockContext);
            app.innerHTML = renderDoc(doc);
            wireDoc(doc);
            finishRouteRender();
          })
          .catch(function () {
            app.innerHTML = renderError("Ошибка загрузки документа");
            finishRouteRender();
          });
      }
      return;
    }

    if (route.name === "settings") {
      app.innerHTML = renderSettings();
      wireSettings();
      finishRouteRender();
      return;
    }

    if (route.name === "orders") {
      app.innerHTML = renderOrders();
      wireOrders();
      finishRouteRender();
      return;
    }

    if (route.name === "order") {
      app.innerHTML = renderLoading();
      TsdStorage.getOrderById(route.id)
        .then(function (order) {
          if (!order) {
            app.innerHTML = renderError("Заказ не найден");
            finishRouteRender();
            return;
          }
          return Promise.all([
            TsdStorage.listOrderLines(route.id),
            TsdStorage.apiGetOrderBoundHu
              ? TsdStorage.apiGetOrderBoundHu(route.id).catch(function () {
                  return [];
                })
              : Promise.resolve([]),
          ])
            .then(function (results) {
              app.innerHTML = renderOrderDetails(order, results[0] || [], results[1] || []);
              wireOrderDetails();
              finishRouteRender();
            })
            .catch(function () {
              app.innerHTML = renderError("Ошибка загрузки строк заказа");
              finishRouteRender();
            });
        })
        .catch(function () {
          app.innerHTML = renderError("Ошибка загрузки заказа");
          finishRouteRender();
        });
      return;
    }

    if (route.name === "items") {
      app.innerHTML = renderItems();
      wireItems();
      finishRouteRender();
      return;
    }

    if (route.name === "stock") {
      app.innerHTML = renderStock();
      wireStock();
      finishRouteRender();
      return;
    }

    if (route.name === "hu") {
      app.innerHTML = renderHuLookup();
      wireHuLookup();
      finishRouteRender();
      return;
    }

    if (route.name === "huCard") {
      app.innerHTML = renderLoading();
      TsdStorage.apiGetHuCard(route.id)
        .then(function (card) {
          app.innerHTML = renderTsdHuCard(card);
          wireTsdHuCard(card);
          finishRouteRender();
        })
        .catch(function () {
          app.innerHTML = renderError("Ошибка загрузки карточки HU");
          finishRouteRender();
        });
      return;
    }

    app.innerHTML = renderHome();
    wireHome();
    finishRouteRender();
  }

  function canRefreshClientBlocksForCurrentRoute() {
    if (!currentRoute) {
      return false;
    }

    return (
      currentRoute.name === "home" ||
      currentRoute.name === "operations" ||
      currentRoute.name === "filling" ||
      currentRoute.name === "fillingDoc" ||
      currentRoute.name === "docs" ||
      currentRoute.name === "stock" ||
      currentRoute.name === "items" ||
      currentRoute.name === "orders" ||
      currentRoute.name === "tasks" ||
      currentRoute.name === "taskDoc"
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

  function buildMenuTile(actionAttr, title, subtitle) {
    return (
      '<button class="home-menu-tile" type="button" ' +
      actionAttr +
      ">" +
      '  <span class="home-menu-tile__content">' +
      '    <span class="home-menu-tile__title">' +
      escapeHtml(title) +
      "</span>" +
      '    <span class="home-menu-tile__subtitle">' +
      escapeHtml(subtitle) +
      "</span>" +
      "  </span>" +
      "</button>"
    );
  }

  function buildHomeMenuTile(route, title, subtitle, iconBubble, iconSrc) {
    return (
      '<button class="home-menu-tile" type="button" data-route="' +
      escapeHtml(route) +
      '">' +
      '  <span class="home-menu-tile__content">' +
      '    <span class="home-menu-icon-bubble home-menu-icon-bubble--' +
      escapeHtml(iconBubble) +
      '">' +
      '      <img class="home-menu-icon" src="' +
      escapeHtml(iconSrc) +
      '" alt="" aria-hidden="true">' +
      "    </span>" +
      '    <span class="home-menu-tile__title">' +
      escapeHtml(title) +
      "</span>" +
      '    <span class="home-menu-tile__subtitle">' +
      escapeHtml(subtitle) +
      "</span>" +
      "  </span>" +
      "</button>"
    );
  }

  function buildHomeMenuButtonsHtml() {
    var tiles = [
      buildHomeMenuTile(
        "operations",
        "Операции",
        "Приём, отгрузка, перемещения",
        "operations",
        "img/home/operations.png"
      ),
      buildHomeMenuTile(
        "items",
        "Каталог",
        "Товары и номенклатура",
        "catalogue",
        "img/home/catalogue.png"
      ),
      buildHomeMenuTile(
        "orders",
        "Заказы",
        "Заказы и документы",
        "orders",
        "img/home/orders.png"
      ),
      buildHomeMenuTile(
        "settings",
        "Информация",
        "Синхронизация, статус и полезные сведения",
        "info",
        "img/home/info.png"
      ),
    ];
    return tiles.join("");
  }

  var showTsdBelowMinimumEntry = false;
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
    var tiles = [];
    if (isOperationEnabled("PRODUCTION_RECEIPT")) {
      tiles.push(
        buildMenuTile('data-route="filling"', "Наполнение", "Паллеты производства")
      );
    }
    Object.keys(OPS).forEach(function (op) {
      if (op === "PRODUCTION_RECEIPT") {
        return;
      }
      if (!isOperationEnabled(op)) {
        return;
      }
      if (op === "OUTBOUND") {
        tiles.push(buildMenuTile('data-route="outbound"', "Отгрузка", "Отгрузка заказов"));
        return;
      }
      tiles.push(
        buildMenuTile(
          'data-op="' + escapeHtml(op) + '"',
          OPS[op].label,
          getOperationsMenuSubtitle(op)
        )
      );
    });
    if (!tiles.length) {
      return '<div class="empty-state">Все операции сейчас временно отключены.</div>';
    }
    return tiles.join("");
  }

  function getOperationsMenuSubtitle(op) {
    var subtitles = {
      INBOUND: "Приход на склад",
      MOVE: "Между локациями",
      WRITE_OFF: "Списание товара",
      INVENTORY: "Пересчёт остатков",
    };
    return subtitles[op] || "";
  }

  function renderHome() {
    var lowStockHtml = showTsdBelowMinimumEntry
      ? '  <div id="homeLowStockWrap" class="home-low-stock-wrap"></div>'
      : "";
    return (
      '<section class="screen home-screen home-screen--centered">' +
      '  <div class="home-menu-wrap">' +
      '    <div class="home-menu-grid">' +
      buildHomeMenuButtonsHtml() +
      "    </div>" +
      "  </div>" +
      lowStockHtml +
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
      '    <div id="ordersFilterActions" class="actions-row">' +
      '      <button class="btn btn-outline" id="ordersToggleReadyBtn" type="button">Показать готовые</button>' +
      '      <button class="btn btn-outline" id="ordersToggleDoneBtn" type="button">Показать выполненные</button>' +
      "    </div>" +
      "  </div>" +
      "</section>"
    );
  }

  function readOrderLineNumber(source, names) {
    if (!source) {
      return null;
    }
    for (var i = 0; i < names.length; i += 1) {
      var name = names[i];
      if (!Object.prototype.hasOwnProperty.call(source, name)) {
        continue;
      }
      var value = source[name];
      if (value == null || value === "") {
        continue;
      }
      var num = Number(value);
      if (isFinite(num)) {
        return num;
      }
    }
    return null;
  }

  function formatOrderQtyValue(value) {
    if (value == null) {
      return "—";
    }
    var num = Number(value);
    if (!isFinite(num)) {
      return "—";
    }
    if (Math.abs(num - Math.round(num)) < 0.000001) {
      return String(Math.round(num));
    }
    return num.toFixed(3).replace(/0+$/, "").replace(/\.$/, "");
  }

  function isInternalOrder(order) {
    var type = String((order && (order.orderType || order.order_type || order.type)) || "").trim().toUpperCase();
    return type === "INTERNAL";
  }

  function capOrderLineReadyQty(line, value) {
    if (value == null) {
      return null;
    }
    var ready = Number(value);
    if (!isFinite(ready)) {
      return null;
    }
    ready = Math.max(ready, 0);
    var ordered = readOrderLineNumber(line, ["orderedQty", "qty_ordered"]);
    if (ordered != null && ordered >= 0) {
      ready = Math.min(ready, ordered);
    }
    return ready;
  }

  function getOrderLineReadyToShipQty(order, line) {
    var value = null;
    if (isInternalOrder(order)) {
      value = readOrderLineNumber(line, ["qtyProduced", "qty_produced"]);
      if (value == null) {
        value = readOrderLineNumber(line, ["palletFilledQty", "pallet_filled_qty"]);
      }
      return capOrderLineReadyQty(line, value);
    }

    value = readOrderLineNumber(line, ["readyToShipQty", "ready_to_ship_qty"]);
    if (value == null) {
      value = readOrderLineNumber(line, ["canShipNow", "can_ship_now"]);
    }
    if (value == null) {
      value = readOrderLineNumber(line, ["qtyAvailable", "qty_available"]);
    }
    if (value == null) {
      value = readOrderLineNumber(line, ["qtyProduced", "qty_produced"]);
    }
    return capOrderLineReadyQty(line, value);
  }

  function getOrderLineProductionHuCodes(line) {
    var values = [];
    if (Array.isArray(line && line.productionHuCodes)) {
      values = line.productionHuCodes;
    } else if (Array.isArray(line && line.production_hu_codes)) {
      values = line.production_hu_codes;
    } else {
      var display = String((line && (line.productionHuCodesDisplay || line.production_hu_codes_display)) || "").trim();
      if (display) {
        values = display.split(",");
      }
    }
    var seen = {};
    return values
      .map(function (hu) {
        return String(hu || "").trim();
      })
      .filter(function (hu) {
        var key = hu.toUpperCase();
        if (!hu || seen[key]) {
          return false;
        }
        seen[key] = true;
        return true;
      })
      .sort(function (left, right) {
        return String(left).localeCompare(String(right), "ru-RU", {
          numeric: true,
          sensitivity: "base",
        });
      });
  }

  function buildOrderBoundHuByItem(boundHuRows) {
    var result = {};
    (Array.isArray(boundHuRows) ? boundHuRows : []).forEach(function (row) {
      var itemId = Number(row && (row.itemId != null ? row.itemId : row.item_id));
      var hu = String((row && (row.hu || row.hu_code || row.huCode)) || "").trim();
      if (!itemId || !hu) {
        return;
      }
      if (!result[itemId]) {
        result[itemId] = [];
      }
      if (
        result[itemId].some(function (existing) {
          return String(existing).toUpperCase() === hu.toUpperCase();
        })
      ) {
        return;
      }
      result[itemId].push(hu);
    });
    Object.keys(result).forEach(function (key) {
      result[key].sort(function (left, right) {
        return String(left).localeCompare(String(right), "ru-RU", {
          numeric: true,
          sensitivity: "base",
        });
      });
    });
    return result;
  }

  function renderOrderLineHuList(title, hus, tone) {
    if (!hus || !hus.length) {
      return "";
    }
    return (
      '<div class="order-line-hu-section">' +
      '  <div class="order-line-hu-section-title">' +
      escapeHtml(title) +
      "</div>" +
      '  <ul class="order-line-hu-list">' +
      hus
        .map(function (hu) {
          return (
            '<li class="order-line-hu-item order-line-hu-item--' +
            escapeHtml(tone || "neutral") +
            '">' +
            '  <span class="order-line-hu-code">' +
            escapeHtml(hu) +
            "</span>" +
            "</li>"
          );
        })
        .join("") +
      "  </ul>" +
      "</div>"
    );
  }

  function renderOrderLineHuDetails(line, boundHuRows) {
    var productionHuCodes = getOrderLineProductionHuCodes(line);
    var plannedQty = readOrderLineNumber(line, ["palletPlannedQty", "pallet_planned_qty"]);
    var filledQty = readOrderLineNumber(line, ["palletFilledQty", "pallet_filled_qty"]);
    var plannedCount = readOrderLineNumber(line, ["plannedPalletCount", "planned_pallet_count"]);
    var filledCount = readOrderLineNumber(line, ["filledPalletCount", "filled_pallet_count"]);
    var summaryParts = [];
    var sections = "";

    if (plannedQty != null || filledQty != null) {
      summaryParts.push(
        "Наполнение: " +
          formatOrderQtyValue(filledQty || 0) +
          " / " +
          formatOrderQtyValue(plannedQty || 0) +
          " шт"
      );
    }
    if ((plannedCount != null && plannedCount > 0) || (filledCount != null && filledCount > 0)) {
      summaryParts.push(
        "Паллеты: " +
          formatOrderQtyValue(filledCount || 0) +
          " / " +
          formatOrderQtyValue(plannedCount || 0)
      );
    }

    sections += renderOrderLineHuList("Производственные HU", productionHuCodes, "production");
    sections += renderOrderLineHuList("Складские HU по товару", boundHuRows || [], "warehouse");

    if (!summaryParts.length && !sections) {
      return '<div class="order-line-hu-empty">HU по строке не найдены в read-model.</div>';
    }

    return (
      (summaryParts.length
        ? '<div class="order-line-hu-summary">' + escapeHtml(summaryParts.join(" · ")) + "</div>"
        : "") + sections
    );
  }

  function renderOrderDetails(order, lines, boundHuRows) {
    order = order || {};
    var boundHuByItem = buildOrderBoundHuByItem(boundHuRows);
    var statusInfo = getOrderStatusInfoForOrder(order);
    var orderNumber =
      order.number || order.orderNumber || order.order_ref || order.orderRef || "—";
    var partnerLabel = getOrderPartnerLabel(order);
    var plannedDate = formatDate(order.plannedDate || order.planned_date);
    var shippedDate = formatDate(order.shippedAt || order.shipped_at);
    var createdAt = formatDateTime(order.createdAt || order.created_at);

    var rows = (lines || [])
      .map(function (line, index) {
        var ordered = readOrderLineNumber(line, ["orderedQty", "qty_ordered"]) || 0;
        var ready = getOrderLineReadyToShipQty(order, line);
        var left = ready == null ? null : Math.max(ordered - ready, 0);
        var itemId = Number(line && (line.itemId != null ? line.itemId : line.item_id)) || 0;
        var panelId = "orderLineHuPanel" + index;
        var lineCode = line.barcode || line.gtin || line.gtin14 || "";
        return (
          '<div class="order-line-entry">' +
          '  <div class="order-line-row order-line-row--expandable" role="button" tabindex="0" data-order-line-toggle="' +
          escapeHtml(String(index)) +
          '" aria-expanded="false" aria-controls="' +
          escapeHtml(panelId) +
          '">' +
          '    <div class="order-line-item">' +
          '      <span class="order-line-expand-icon" aria-hidden="true">▾</span>' +
          '      <div class="order-line-item-text">' +
          '        <div class="order-line-name">' +
          escapeHtml(line.itemName || line.item_name || "-") +
          "</div>" +
          (lineCode
            ? '        <div class="order-line-barcode">' + escapeHtml(lineCode) + "</div>"
            : "") +
          "      </div>" +
          "    </div>" +
          '    <div class="order-line-qty">' +
          escapeHtml(formatOrderQtyValue(ordered)) +
          "</div>" +
          '    <div class="order-line-qty">' +
          escapeHtml(formatOrderQtyValue(ready)) +
          "</div>" +
          '    <div class="order-line-qty">' +
          escapeHtml(formatOrderQtyValue(left)) +
          "</div>" +
          "  </div>" +
          '  <div class="order-line-hu-panel" id="' +
          escapeHtml(panelId) +
          '" data-order-line-panel="' +
          escapeHtml(String(index)) +
          '" hidden>' +
          renderOrderLineHuDetails(line, itemId && boundHuByItem[itemId] ? boundHuByItem[itemId] : []) +
          "  </div>" +
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
        '  <div class="order-line-head">Готово к отгрузке</div>' +
        '  <div class="order-line-head">Осталось</div>' +
        "</div>" +
        rows;
    }

    return (
      '<section class="screen order-details-screen">' +
      '  <div class="screen-card doc-screen-card order-details-card">' +
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
      '<section class="screen operations-screen operations-screen--centered">' +
      '  <div class="operations-menu-wrap">' +
      '    <div class="operations-menu-grid operations-menu-grid--2x6">' +
      buildOperationsMenuButtonsHtml() +
      "    </div>" +
      "  </div>" +
      "</section>"
    );
  }

  function getFillingWorkOrderRef(item) {
    return String((item && (item.orderRef || item.order_ref)) || "").trim() || "-";
  }

  function parseFillingOrderSortNumber(value) {
    if (value == null || value === "") {
      return null;
    }
    var text = String(value).trim();
    if (!text) {
      return null;
    }
    var direct = Number(text);
    if (Number.isFinite(direct)) {
      return direct;
    }
    var matches = text.match(/\d+/g);
    if (!matches || !matches.length) {
      return null;
    }
    var parsed = Number(matches[matches.length - 1]);
    return Number.isFinite(parsed) ? parsed : null;
  }

  function getFillingWorkOrderSortValue(item) {
    if (!item) {
      return null;
    }
    var candidates = [
      item.orderRef,
      item.order_ref,
      item.orderNumber,
      item.order_number,
      item.number,
      item.orderId,
      item.order_id,
      item.id,
    ];
    for (var i = 0; i < candidates.length; i += 1) {
      var parsed = parseFillingOrderSortNumber(candidates[i]);
      if (parsed != null) {
        return parsed;
      }
    }
    return null;
  }

  function compareFillingListItems(left, right) {
    var leftValue = getFillingWorkOrderSortValue(left);
    var rightValue = getFillingWorkOrderSortValue(right);
    var leftHasValue = leftValue != null;
    var rightHasValue = rightValue != null;
    if (leftHasValue && rightHasValue) {
      if (leftValue !== rightValue) {
        return leftValue - rightValue;
      }
      return 0;
    }
    if (leftHasValue) {
      return -1;
    }
    if (rightHasValue) {
      return 1;
    }
    return 0;
  }

  function sortFillingListItems(items) {
    return (Array.isArray(items) ? items.slice() : []).sort(compareFillingListItems);
  }

  function getFillingWorkPrdRef(item) {
    return String((item && (item.prdDocRef || item.prd_doc_ref)) || "").trim();
  }

  function getFillingSummary(itemOrDocument) {
    return (itemOrDocument && itemOrDocument.summary) || {};
  }

  var FILLING_PALLET_EMPTY_GROUP_LABEL = "Без товара";

  function getFillingPalletItemLabel(pallet) {
    if (!pallet) {
      return "";
    }
    if (pallet.isMixedPallet === true) {
      return "Микс-паллета";
    }
    var itemName = String(pallet.itemName || pallet.item_name || "").trim();
    if (!itemName && Array.isArray(pallet.lines) && pallet.lines.length) {
      itemName = String(
        (pallet.lines[0] && (pallet.lines[0].itemName || pallet.lines[0].item_name)) || ""
      ).trim();
    }
    return itemName;
  }

  function getFillingPalletGroupLabel(pallet) {
    var label = getFillingPalletItemLabel(pallet);
    return label || FILLING_PALLET_EMPTY_GROUP_LABEL;
  }

  function parseFillingHuSortNumber(huCode) {
    return parseFillingOrderSortNumber(huCode);
  }

  function compareFillingPalletHuRows(left, right) {
    var leftValue = parseFillingHuSortNumber(left && (left.huCode || left.hu_code));
    var rightValue = parseFillingHuSortNumber(right && (right.huCode || right.hu_code));
    var leftHasValue = leftValue != null;
    var rightHasValue = rightValue != null;
    if (leftHasValue && rightHasValue) {
      if (leftValue !== rightValue) {
        return leftValue - rightValue;
      }
      return String((left && left.huCode) || "").localeCompare(
        String((right && right.huCode) || ""),
        "ru-RU",
        { numeric: true, sensitivity: "base" }
      );
    }
    if (leftHasValue) {
      return -1;
    }
    if (rightHasValue) {
      return 1;
    }
    return 0;
  }

  function compareFillingPalletGroupLabels(leftLabel, rightLabel) {
    var leftEmpty = leftLabel === FILLING_PALLET_EMPTY_GROUP_LABEL;
    var rightEmpty = rightLabel === FILLING_PALLET_EMPTY_GROUP_LABEL;
    if (leftEmpty && rightEmpty) {
      return 0;
    }
    if (leftEmpty) {
      return 1;
    }
    if (rightEmpty) {
      return -1;
    }
    return String(leftLabel || "").localeCompare(String(rightLabel || ""), "ru-RU", {
      numeric: true,
      sensitivity: "base",
    });
  }

  function buildFillingPalletGroups(pallets) {
    var grouped = {};
    var labels = [];
    (Array.isArray(pallets) ? pallets : []).forEach(function (pallet) {
      var label = getFillingPalletGroupLabel(pallet);
      if (!grouped[label]) {
        grouped[label] = [];
        labels.push(label);
      }
      grouped[label].push(pallet);
    });
    return labels
      .sort(compareFillingPalletGroupLabels)
      .map(function (label) {
        return {
          label: label,
          pallets: grouped[label].slice().sort(compareFillingPalletHuRows),
        };
      });
  }

  function isMixedFillingPallet(pallet) {
    return !!(
      pallet &&
      (pallet.isMixedPallet === true || (Array.isArray(pallet.lines) && pallet.lines.length > 1))
    );
  }

  function countFillingMixedCompletedLines(pallet) {
    var lines = Array.isArray(pallet && pallet.lines) ? pallet.lines : [];
    if (!lines.length) {
      return Number(pallet && pallet.filledComponentCount) || 0;
    }
    return lines.filter(function (line) {
      return line.isCompleted === true;
    }).length;
  }

  function getFillingMixedComponentTotal(pallet) {
    var total = Number(pallet && pallet.totalComponentCount) || 0;
    if (total > 0) {
      return total;
    }
    return Array.isArray(pallet && pallet.lines) ? pallet.lines.length : 0;
  }

  function formatFillingMixedComponentQty(line, pallet) {
    var planned = Number(line.plannedQty != null ? line.plannedQty : line.qty) || 0;
    var filled = Number(line.filledQty) || 0;
    var uom = String(line.uom || "шт");
    if (line.isCompleted === true || filled > 0) {
      return (
        formatQtyWithUnit(filled, uom) +
        " / " +
        formatQtyWithUnit(planned, uom)
      );
    }
    if (countFillingMixedCompletedLines(pallet) > 0) {
      return (
        formatQtyWithUnit(0, uom) +
        " / " +
        formatQtyWithUnit(planned, uom)
      );
    }
    return formatQtyWithUnit(planned, uom);
  }

  function renderFillingMixedPalletLines(pallet) {
    var lines = Array.isArray(pallet && pallet.lines) ? pallet.lines : [];
    var html = lines
      .map(function (line) {
        var completed = line.isCompleted === true;
        return (
          '<div class="filling-mixed-component-line ' +
          (completed ? "is-completed" : "is-pending") +
          '">' +
          '  <span class="filling-mixed-component-indicator" aria-hidden="true">' +
          (completed ? "✅" : "☐") +
          "</span>" +
          '  <span class="filling-mixed-component-name">' +
          escapeHtml(line.itemName || "-") +
          "</span>" +
          '  <strong class="filling-mixed-component-qty">' +
          escapeHtml(formatFillingMixedComponentQty(line, pallet)) +
          "</strong>" +
          "</div>"
        );
      })
      .join("");
    return html
      ? '<div class="outbound-picking-hu-lines filling-mixed-pallet-lines">' + html + "</div>"
      : "";
  }

  function getFillingPalletGroupDisplayTitle(group) {
    var label = group && group.label ? group.label : "";
    if (label !== "Микс-паллета") {
      return label;
    }
    var mixedPallets = (group.pallets || []).filter(isMixedFillingPallet);
    if (mixedPallets.length !== 1) {
      return label;
    }
    var pallet = mixedPallets[0];
    var total = getFillingMixedComponentTotal(pallet);
    if (total <= 0) {
      return label;
    }
    var filled = countFillingMixedCompletedLines(pallet);
    return "Микс-паллета · " + filled + " / " + total;
  }

  function renderFillingPalletHuRow(pallet) {
    var isFilled = isFillingPalletCompleted(pallet);
    var huCode = pallet && pallet.huCode ? pallet.huCode : "-";
    if (isMixedFillingPallet(pallet)) {
      var componentLines = renderFillingMixedPalletLines(pallet);
      return (
        '<li class="filling-pallet-item filling-pallet-item--compact filling-mixed-pallet-item outbound-picking-hu-item ' +
        (isFilled ? "is-filled" : "is-pending") +
        '">' +
        '  <span class="filling-pallet-dot" aria-hidden="true"></span>' +
        '  <div class="outbound-picking-hu-main">' +
        '    <div class="filling-pallet-code">' +
        escapeHtml(huCode) +
        "</div>" +
        componentLines +
        "  </div>" +
        "</li>"
      );
    }
    return (
      '<li class="filling-pallet-item filling-pallet-item--compact ' +
      (isFilled ? "is-filled" : "is-pending") +
      '">' +
      '  <span class="filling-pallet-dot" aria-hidden="true"></span>' +
      '  <div class="filling-pallet-code">' +
      escapeHtml(huCode) +
      "</div>" +
      "</li>"
    );
  }

  function isFillingPalletCompleted(pallet) {
    var status = String(
      (pallet && (pallet.effectiveStatus || pallet.effective_status || pallet.status)) || ""
    ).toUpperCase();
    return status === "FILLED";
  }

  function renderFillingPalletStatusList(pallets) {
    var groups = buildFillingPalletGroups(pallets);
    var items = groups
      .map(function (group) {
        var rows = (group.pallets || []).map(renderFillingPalletHuRow).join("");
        if (!rows) {
          return "";
        }
        return (
          '<li class="filling-pallet-group">' +
          '  <div class="filling-pallet-group-title">' +
          escapeHtml(getFillingPalletGroupDisplayTitle(group)) +
          "</div>" +
          '  <ul class="filling-pallet-group-items">' +
          rows +
          "  </ul>" +
          "</li>"
        );
      })
      .join("");

    if (!items) {
      return "";
    }

    return (
      '<div class="filling-pallet-list-card">' +
      '  <div class="filling-pallet-list-title">Паллеты к наполнению</div>' +
      '  <ul class="filling-pallet-list filling-pallet-list--scroll-breathing">' +
      items +
      "  </ul>" +
      "</div>"
    );
  }

  function renderFillingList(items) {
    var rows = sortFillingListItems(items)
      .map(function (item) {
        var summary = getFillingSummary(item);
        var statusInfo = getOrderStatusInfoForOrder({
          status: item.orderStatus,
          statusDisplay: item.orderStatusDisplay || item.order_status_display,
        });
        var partnerHtml = item.partnerName
          ? '<div class="filling-doc-meta">Клиент: ' + escapeHtml(item.partnerName) + "</div>"
          : "";
        return (
          '<button class="filling-doc-card" data-filling-order="' +
          escapeHtml(item.orderId || item.order_id || "") +
          '">' +
          '  <div class="filling-doc-main">' +
          '    <div class="filling-doc-title">Заказ № ' +
          escapeHtml(getFillingWorkOrderRef(item)) +
          "</div>" +
          '    <div class="filling-doc-subtitle">' +
          escapeHtml(item.orderTypeDisplay || item.order_type_display || "") +
          "</div>" +
          partnerHtml +
          '    <div class="' +
          escapeHtml(statusInfo.className) +
          '">' +
          escapeHtml(statusInfo.label) +
          "</div>" +
          "  </div>" +
          '  <div class="filling-doc-progress">' +
          '    <div>Запланировано паллет: <strong>' +
          escapeHtml(formatPalletCountValue(summary.plannedPalletCount)) +
          "</strong></div>" +
          '    <div>Наполнено паллет: <strong>' +
          escapeHtml(formatPalletCountValue(summary.filledPalletCount)) +
          " / " +
          escapeHtml(formatPalletCountValue(summary.plannedPalletCount)) +
          "</strong></div>" +
          '    <div>Осталось наполнить: <strong>' +
          escapeHtml(formatQtyWithUnit(summary.remainingQty || 0, "шт")) +
          "</strong></div>" +
          "  </div>" +
          "</button>"
        );
      })
      .join("");

    if (!rows) {
      rows = '<div class="empty-state">Нет заказов с подготовленными паллетами для наполнения</div>';
    }

    return (
      '<section class="screen filling-screen">' +
      '  <div class="screen-card filling-card">' +
      '    <div class="section-title">Наполнение</div>' +
      '    <div class="field-hint">Выберите заказ с подготовленными паллетами.</div>' +
      '    <div class="filling-doc-list">' +
      rows +
      "    </div>" +
      "  </div>" +
      "</section>"
    );
  }

  function buildFillingContext(context) {
    context = context || {};
    return {
      workItem: {
        orderId: context.orderId,
        orderRef: context.orderRef,
        orderType: context.orderType,
        orderTypeDisplay: context.orderTypeDisplay,
        orderStatus: context.orderStatus,
        orderStatusDisplay: context.orderStatusDisplay,
        partnerName: context.partnerName,
        prdDocId: context.prdDocId,
        prdDocRef: context.prdDocRef,
        summary: context.document ? context.document.summary : null,
      },
      document: context.document || null,
      progress: {
        requiredPallets: Number(context.requiredPallets) || 0,
        scannedPallets: Number(context.scannedPallets) || 0,
        remainingPallets: Number(context.remainingPallets) || 0,
        canClose: context.canClose === true,
        isClosed: context.isClosed === true,
        operationFingerprint: String(context.operationFingerprint || ""),
      },
      doc: null,
    };
  }

  function buildClosePromptStateKey(kind, operation) {
    operation = operation || {};
    return [kind, String(operation.operationFingerprint || ""), Number(operation.requiredPallets) || 0,
      Number(operation.scannedPallets) || 0, Number(operation.remainingPallets) || 0].join("|");
  }

  function shouldPromptOperationClose(kind, operation) {
    var canClose =
      !!operation &&
      (operation.canClose === true ||
        (kind === "outbound" && isOutboundPickingOperationComplete(operation)));
    return canClose && operation.isClosed !== true &&
      (typeof sessionStorage === "undefined" ||
        sessionStorage.getItem("flowstock-close-prompt-declined") !== buildClosePromptStateKey(kind, operation));
  }

  function declineOperationClosePrompt(kind, operation) {
    if (typeof sessionStorage !== "undefined") {
      sessionStorage.setItem("flowstock-close-prompt-declined", buildClosePromptStateKey(kind, operation));
    }
  }

  function loadFillingContext(orderId) {
    return TsdStorage.apiGetProductionFillingContext(orderId).then(buildFillingContext);
  }

  function buildFillingScanSummaryLine(work, summary) {
    summary = summary || {};
    return (
      "Заказ " +
      getFillingWorkOrderRef(work) +
      " · " +
      formatPalletCountValue(summary.filledPalletCount) +
      " / " +
      formatPalletCountValue(summary.plannedPalletCount) +
      " паллет"
    );
  }

  function buildFillingScanHeaderLine(work, summary) {
    return "Наполнение · " + buildFillingScanSummaryLine(work, summary);
  }

  function renderFillingScan(context, state) {
    var work = context.workItem || {};
    var document = context.document || {};
    var summary = getFillingSummary(document.summary ? document : work);
    var palletListHtml = renderFillingPalletStatusList(document.pallets);
    var message = state && state.message ? String(state.message) : "";
    var messageType = state && state.messageType ? String(state.messageType) : "";
    var messageHtml = message
      ? '<div class="filling-message filling-message-' + escapeHtml(messageType || "info") + '">' + escapeHtml(message) + "</div>"
      : "";

    return (
      '<section class="screen filling-screen filling-screen--scan">' +
      '  <div class="screen-card filling-card filling-card--scan">' +
      '    <div class="filling-scan-header">' +
      escapeHtml(buildFillingScanHeaderLine(work, summary)) +
      "</div>" +
      messageHtml +
      (context.progress && context.progress.canClose && !context.progress.isClosed
        ? '    <div class="filling-message filling-message-success">Все паллеты отсканированы. Документ не закрыт.</div>' +
          '    <div class="actions-bar"><button class="btn primary-btn" id="fillingCompleteBtn" type="button">Закрыть документ</button></div>'
        : "") +
      '    <div class="filling-scan-card filling-scan-card--compact filling-scan-slot">' +
      '      <input class="form-input filling-scan-input tsd-scan-input-hidden filling-scan-input-hidden" id="fillingScanInput" type="text" autocomplete="off" autocapitalize="off" autocorrect="off" spellcheck="false" data-scan-allow="1" placeholder="HU-000001" />' +
      "    </div>" +
      palletListHtml +
      "  </div>" +
      "</section>"
    );
  }

  function buildFillingPreviewHtml(preview, work) {
    var isMixed = preview && preview.isMixedPallet === true;
    var brandHtml = !isMixed && preview && preview.itemBrand
      ? '<div class="filling-preview-brand">' + escapeHtml(preview.itemBrand) + "</div>"
      : "";
    var compositionHtml = "";
    if (isMixed && preview && Array.isArray(preview.lines) && preview.lines.length) {
      compositionHtml =
        '<div class="filling-preview-composition-title">Компоненты · ' +
        escapeHtml(String(preview.filledComponentCount || 0)) + " / " +
        escapeHtml(String(preview.totalComponentCount || preview.lines.length)) + "</div>" +
        '<div class="filling-preview-composition">' +
        preview.lines.map(function (line) {
          var completed = line.isCompleted === true;
          return '<label class="filling-component-line' + (completed ? " is-completed" : "") + '">' +
            '<input type="checkbox" class="filling-component-checkbox" value="' +
            escapeHtml(String(line.componentLineId || 0)) + '"' +
            (completed ? " checked disabled" : "") + " />" +
            "<span>" +
            escapeHtml(line.itemName || "-") +
            " — " +
            escapeHtml(formatQtyWithUnit(line.plannedQty || line.qty || 0, line.uom || "шт")) +
            (completed ? " · наполнено" : "") +
            "</span></label>";
        }).join("") +
        "</div>";
    }

    return (
      '<div class="filling-preview-card filling-preview-overlay-card filling-preview-overlay-card--compact">' +
      '  <div class="filling-preview-title">' + (isMixed ? "Микс-паллета" : "Наполнение паллеты") + "</div>" +
      '  <div class="filling-preview-line">Заказ: <strong>' +
      escapeHtml((preview && preview.orderRef) || getFillingWorkOrderRef(work)) +
      "</strong></div>" +
      '  <div class="filling-preview-line">HU: <strong>' +
      escapeHtml((preview && preview.huCode) || "") +
      "</strong></div>" +
      '  <div class="filling-preview-line">Паллета: <strong>' +
      escapeHtml((preview && preview.palletIndex) || 0) +
      " / " +
      escapeHtml((preview && preview.palletCount) || 0) +
      "</strong></div>" +
      '  <div class="filling-preview-item">' +
      escapeHtml(isMixed ? "Микс-паллета" : (preview && preview.itemName) || "-") +
      "</div>" +
      brandHtml +
      (isMixed ? compositionHtml : '  <div class="filling-preview-qty">' +
      escapeHtml(formatQtyWithUnit((preview && preview.plannedQty) || 0, (preview && preview.baseUom) || "шт")) +
      "</div>") +
      '  <div class="filling-preview-error" id="fillingPreviewError"></div>' +
      '  <div class="filling-preview-actions">' +
      '    <button class="btn primary-btn filling-ok-btn" id="fillingOverlayConfirmBtn" type="button">Подтвердить</button>' +
      '    <button class="btn btn-outline filling-cancel-btn" id="fillingOverlayCancelBtn" type="button">Отмена</button>' +
      "  </div>" +
      "</div>"
    );
  }

  function openFillingPreviewOverlay(context, preview) {
    if (!preview || preview.alreadyFilled === true) {
      return;
    }

    var overlay = document.createElement("div");
    overlay.className = "overlay overlay--centered filling-preview-overlay";
    overlay.setAttribute("tabindex", "-1");
    overlay.innerHTML =
      '<div class="overlay-card filling-preview-overlay-shell filling-preview-overlay-shell--compact">' +
      '  <div class="overlay-body filling-preview-overlay-body">' +
      buildFillingPreviewHtml(preview, context.workItem || {}) +
      "  </div>" +
      "</div>";

    var cancelBtn = overlay.querySelector("#fillingOverlayCancelBtn");
    var confirmBtn = overlay.querySelector("#fillingOverlayConfirmBtn");
    var errorEl = overlay.querySelector("#fillingPreviewError");
    var busy = false;

    function updateMixedConfirmState() {
      if (!confirmBtn || preview.isMixedPallet !== true || busy) {
        return;
      }
      confirmBtn.disabled = !overlay.querySelector(".filling-component-checkbox:not(:disabled):checked");
    }

    function closeOverlay() {
      unlockOverlayScroll();
      if (overlay.parentNode) {
        overlay.parentNode.removeChild(overlay);
      }
    }

    function cancelOverlay() {
      closeOverlay();
      renderFillingScanScreen(context, { message: "", messageType: "", preview: null });
    }

    function setOverlayError(message) {
      if (errorEl) {
        errorEl.textContent = message || "";
      }
    }

    if (preview.isMixedPallet === true) {
      overlay.querySelectorAll(".filling-component-checkbox:not(:disabled)").forEach(function (checkbox) {
        checkbox.addEventListener("change", updateMixedConfirmState);
      });
      updateMixedConfirmState();
    }

    if (confirmBtn) {
      confirmBtn.addEventListener("click", function () {
        if (busy) {
          return;
        }

        busy = true;
        confirmBtn.disabled = true;
        if (cancelBtn) {
          cancelBtn.disabled = true;
        }
        setOverlayError("");

        getFillingDeviceId()
          .then(function (deviceId) {
            if (preview.isMixedPallet === true) {
              var selectedComponentIds = Array.from(
                overlay.querySelectorAll(".filling-component-checkbox:not(:disabled):checked")
              ).map(function (checkbox) {
                return Number(checkbox.value) || 0;
              }).filter(function (id) {
                return id > 0;
              });
              if (!selectedComponentIds.length) {
                throw new Error("COMPONENT_LINE_IDS_REQUIRED");
              }
              return TsdStorage.apiFillMixedProductionPalletComponents({
                huCode: preview.huCode,
                orderId: preview.orderId,
                prdDocId: preview.prdDocId,
                deviceId: deviceId,
                componentLineIds: selectedComponentIds,
              });
            }
            return TsdStorage.apiFillProductionPallet({
              huCode: preview.huCode,
              orderId: preview.orderId,
              prdDocId: preview.prdDocId,
              deviceId: deviceId,
            });
          })
          .then(function (result) {
            closeOverlay();
            return handleProductionFillSuccess(context, preview, result);
          })
          .catch(function (error) {
            busy = false;
            confirmBtn.disabled = false;
            if (cancelBtn) {
              cancelBtn.disabled = false;
            }
            setOverlayError(mapFillingError(error));
          });
      });
    }

    if (cancelBtn) {
      cancelBtn.addEventListener("click", cancelOverlay);
    }
    overlay.addEventListener("click", function (event) {
      if (event.target === overlay) {
        cancelOverlay();
      }
    });

    document.body.appendChild(overlay);
    lockOverlayScroll();
    focusOverlay(overlay);
  }

  function renderFillingScanScreen(context, state) {
    app.innerHTML = renderFillingScan(context, state || {});
    wireFillingScan(context, state || {});
    finishRouteRender();
  }

  function renderFillingLoading() {
    return (
      '<section class="screen filling-screen">' +
      '  <div class="screen-card filling-card">' +
      '    <div class="section-title">Наполнение</div>' +
      '    <div class="status">Загрузка...</div>' +
      "  </div>" +
      "</section>"
    );
  }

  function getOutboundPickingStatusLabel(status) {
    var normalized = String(status || "").trim().toUpperCase();
    if (normalized === "PICKED") {
      return "Подобрано";
    }
    if (normalized === "PENDING") {
      return "Ожидает";
    }
    return status || "-";
  }

  function getOutboundPickingHuItemLabel(hu) {
    var normalized = normalizeOutboundPickingHuView(hu);
    if (normalized.isMixedPallet === true) {
      return "Микс-паллета";
    }
    var lines = normalized.lines || [];
    if (lines.length > 1) {
      return "Микс-паллета";
    }
    if (lines.length === 1) {
      var lineName = String(lines[0].itemName || "").trim();
      if (lineName) {
        return lineName;
      }
    }
    var itemName = String(
      normalized.itemName ||
        normalized.item_name ||
        normalized.productName ||
        normalized.product_name ||
        normalized.lineName ||
        normalized.line_name ||
        ""
    ).trim();
    if (itemName) {
      return itemName;
    }
    var summary = String(normalized.itemSummary || "").trim();
    if (summary) {
      var commaIndex = summary.indexOf(",");
      return commaIndex >= 0 ? summary.slice(0, commaIndex).trim() : summary;
    }
    return "";
  }

  function getOutboundPickingHuGroupLabel(hu) {
    var label = getOutboundPickingHuItemLabel(hu);
    return label || FILLING_PALLET_EMPTY_GROUP_LABEL;
  }

  function buildOutboundPickingHuGroups(hus) {
    var grouped = {};
    var labels = [];
    (Array.isArray(hus) ? hus : []).forEach(function (hu) {
      var normalized = normalizeOutboundPickingHuView(hu);
      var label = getOutboundPickingHuGroupLabel(normalized);
      if (!grouped[label]) {
        grouped[label] = [];
        labels.push(label);
      }
      grouped[label].push(normalized);
    });
    return labels
      .sort(compareFillingPalletGroupLabels)
      .map(function (label) {
        return {
          label: label,
          hus: grouped[label].slice().sort(compareFillingPalletHuRows),
        };
      });
  }

  function renderOutboundPickingHuRow(hu) {
    var normalized = normalizeOutboundPickingHuView(hu);
    var picked = String(normalized.status || "").toUpperCase() === "PICKED";
    var componentLines =
      normalized.isMixedPallet === true || (normalized.lines || []).length > 1
        ? renderOutboundPickingHuLines(normalized.lines)
        : "";
    return (
      '<li class="filling-pallet-item filling-pallet-item--compact outbound-picking-hu-item ' +
      (picked ? "is-filled" : "is-pending") +
      '">' +
      '  <span class="filling-pallet-dot" aria-hidden="true"></span>' +
      '  <div class="outbound-picking-hu-main">' +
      '  <div class="filling-pallet-code">' +
      escapeHtml(normalized.huCode || "-") +
      "</div>" +
      componentLines +
      "</div>" +
      "</li>"
    );
  }

  function buildOutboundPickingSummaryLine(order) {
    order = normalizeOutboundPickingOrderView(order);
    return (
      "Заказ " +
      (order.orderRef || "-") +
      " · " +
      (Number(order.pickedHuCount) || 0) +
      " / " +
      (Number(order.expectedHuCount) || 0) +
      " паллет"
    );
  }

  function buildOutboundPickingHeaderLine(order) {
    return "Отгрузка · " + buildOutboundPickingSummaryLine(order);
  }

  function resolveFillingOrderId(source, fallbackOrderId) {
    if (!source) {
      return Number(fallbackOrderId) || 0;
    }
    var raw =
      source.orderId != null && source.orderId !== "" ? source.orderId : source.order_id;
    if (raw != null && raw !== "") {
      return Number(raw) || Number(fallbackOrderId) || 0;
    }
    return Number(fallbackOrderId) || 0;
  }

  function pickOutboundViewField(row, camelKey, snakeKey) {
    if (!row) {
      return undefined;
    }
    var camelValue = row[camelKey];
    if (camelValue != null && camelValue !== "") {
      return camelValue;
    }
    return row[snakeKey];
  }

  function pickOutboundViewValue(normalized, raw, camelKey, snakeKey) {
    var value = pickOutboundViewField(normalized, camelKey, snakeKey);
    if (value != null && value !== "") {
      return value;
    }
    return pickOutboundViewField(raw, camelKey, snakeKey);
  }

  function normalizeOutboundPickingLineView(line) {
    var raw = line || {};
    return {
      itemId: Number(pickOutboundViewValue({}, raw, "itemId", "item_id")) || 0,
      itemName: String(pickOutboundViewValue({}, raw, "itemName", "item_name") || ""),
      orderLineId: Number(pickOutboundViewValue({}, raw, "orderLineId", "order_line_id")) || 0,
      locationCode: String(pickOutboundViewValue({}, raw, "locationCode", "location_code") || ""),
      qty: Number(raw.qty) || 0,
    };
  }

  function normalizeOutboundPickingHuView(hu) {
    var raw = hu || {};
    return {
      huCode: String(pickOutboundViewValue({}, raw, "huCode", "hu_code") || ""),
      status: String(raw.status || "PENDING"),
      qty: Number(raw.qty) || 0,
      itemSummary: String(pickOutboundViewValue({}, raw, "itemSummary", "item_summary") || ""),
      orderLineId: Number(pickOutboundViewValue({}, raw, "orderLineId", "order_line_id")) || 0,
      locationCode: String(pickOutboundViewValue({}, raw, "locationCode", "location_code") || ""),
      isMixedPallet:
        raw.isMixedPallet === true ||
        raw.is_mixed_pallet === true ||
        (Array.isArray(raw.lines) && raw.lines.length > 1),
      lines: Array.isArray(raw.lines)
        ? raw.lines.map(normalizeOutboundPickingLineView)
        : [],
    };
  }

  function normalizeOutboundPickingOrderView(order) {
    var raw = order || {};
    var normalized =
      typeof TsdStorage !== "undefined" &&
      TsdStorage &&
      typeof TsdStorage.normalizeOutboundPickingOrder === "function"
        ? TsdStorage.normalizeOutboundPickingOrder(raw) || {}
        : {};
    return {
      orderId:
        Number(pickOutboundViewValue(normalized, raw, "orderId", "order_id")) || 0,
      orderRef: String(pickOutboundViewValue(normalized, raw, "orderRef", "order_ref") || ""),
      partnerName: String(pickOutboundViewValue(normalized, raw, "partnerName", "partner_name") || ""),
      status: String((normalized && normalized.status) || (raw && raw.status) || ""),
      expectedHuCount:
        Number(pickOutboundViewValue(normalized, raw, "expectedHuCount", "expected_hu_count")) || 0,
      pickedHuCount:
        Number(pickOutboundViewValue(normalized, raw, "pickedHuCount", "picked_hu_count")) || 0,
      orderedQty:
        Number(pickOutboundViewValue(normalized, raw, "orderedQty", "ordered_qty")) || 0,
      shippedQty:
        Number(pickOutboundViewValue(normalized, raw, "shippedQty", "shipped_qty")) || 0,
      remainingQty:
        Number(pickOutboundViewValue(normalized, raw, "remainingQty", "remaining_qty")) || 0,
      scannedQty:
        Number(pickOutboundViewValue(normalized, raw, "scannedQty", "scanned_qty")) || 0,
      isComplete:
        pickOutboundViewValue(normalized, raw, "isComplete", "is_complete") === true,
      requiredPallets:
        Number(pickOutboundViewValue(normalized, raw, "requiredPallets", "required_pallets")) || 0,
      scannedPallets:
        Number(pickOutboundViewValue(normalized, raw, "scannedPallets", "scanned_pallets")) || 0,
      remainingPallets:
        Number(pickOutboundViewValue(normalized, raw, "remainingPallets", "remaining_pallets")) || 0,
      canClose:
        pickOutboundViewValue(normalized, raw, "canClose", "can_close") === true,
      isClosed:
        pickOutboundViewValue(normalized, raw, "isClosed", "is_closed") === true,
      operationFingerprint: String(
        pickOutboundViewValue(normalized, raw, "operationFingerprint", "operation_fingerprint") || ""
      ),
      draftOutboundDocId:
        Number(pickOutboundViewValue(normalized, raw, "draftOutboundDocId", "draft_outbound_doc_id")) || 0,
      draftOutboundDocRef: String(
        pickOutboundViewValue(normalized, raw, "draftOutboundDocRef", "draft_outbound_doc_ref") || ""
      ),
      hus: Array.isArray(normalized && normalized.hus)
        ? normalized.hus.map(normalizeOutboundPickingHuView)
        : Array.isArray(raw && raw.hus)
          ? raw.hus.map(normalizeOutboundPickingHuView)
          : [],
    };
  }

  function isOutboundPickingOperationComplete(order) {
    order = order || {};
    var remainingQty = Number(order.remainingQty) || 0;
    var scannedQty = Number(order.scannedQty) || 0;
    return (
      order.canClose === true ||
      order.isComplete === true ||
      (remainingQty > 0 && scannedQty >= remainingQty)
    );
  }

  function isOutboundPickingOperationPartial(order) {
    order = order || {};
    var remainingQty = Number(order.remainingQty) || 0;
    var scannedQty = Number(order.scannedQty) || 0;
    return (
      !isOutboundPickingOperationComplete(order) &&
      scannedQty > 0 &&
      scannedQty < remainingQty
    );
  }

  function normalizeOutboundPickingScanText(value) {
    return normalizeValue(value)
      .toUpperCase()
      .replace(/\u041D/g, "H")
      .replace(/\u0423/g, "U");
  }

  function compactOutboundPickingScanText(value) {
    return normalizeOutboundPickingScanText(value).replace(/[^A-Z0-9]/g, "");
  }

  function resolveOutboundPickingScannedHu(rawValue, order) {
    var rawText = String(rawValue || "").trim();
    var extractedHu = extractHuCode(rawValue);
    var fallback = extractedHu || rawText;
    var normalizedOrder = normalizeOutboundPickingOrderView(order);
    var rawComparable = normalizeOutboundPickingScanText(rawValue);
    var rawCompact = compactOutboundPickingScanText(rawValue);
    var rawDigitsOnly = rawCompact && /^[0-9]+$/.test(rawCompact);

    for (var index = 0; index < normalizedOrder.hus.length; index++) {
      var expectedHu = normalizeOutboundPickingHuView(normalizedOrder.hus[index]).huCode;
      var canonicalHu = String(expectedHu || "").trim();
      if (!canonicalHu) {
        continue;
      }

      var expectedComparable = normalizeOutboundPickingScanText(canonicalHu);
      var expectedCompact = compactOutboundPickingScanText(canonicalHu);
      var expectedDigits = expectedCompact.replace(/[^0-9]/g, "");
      if (!expectedComparable || !expectedCompact) {
        continue;
      }

      if (rawComparable === expectedComparable || rawCompact === expectedCompact) {
        return canonicalHu;
      }

      if (extractedHu && compactOutboundPickingScanText(extractedHu) === expectedCompact) {
        return canonicalHu;
      }

      if (rawDigitsOnly && expectedDigits && rawCompact === expectedDigits) {
        return canonicalHu;
      }

      if (rawComparable.indexOf(expectedComparable) >= 0 || rawCompact.indexOf(expectedCompact) >= 0) {
        return canonicalHu;
      }
    }

    return fallback;
  }

  function resolveFillingScannedHu(rawValue, context) {
    var rawText = String(rawValue || "").trim();
    var extractedHu = extractHuCode(rawValue);
    var fallback = extractedHu || rawText;
    var pallets =
      context && context.document && Array.isArray(context.document.pallets)
        ? context.document.pallets
        : [];
    var rawComparable = normalizeOutboundPickingScanText(rawValue);
    var rawCompact = compactOutboundPickingScanText(rawValue);
    var rawDigitsOnly = rawCompact && /^[0-9]+$/.test(rawCompact);

    for (var index = 0; index < pallets.length; index++) {
      var pallet = pallets[index] || {};
      var canonicalHu = String(pallet.huCode || pallet.hu_code || "").trim();
      if (!canonicalHu) {
        continue;
      }

      var expectedComparable = normalizeOutboundPickingScanText(canonicalHu);
      var expectedCompact = compactOutboundPickingScanText(canonicalHu);
      var expectedDigits = expectedCompact.replace(/[^0-9]/g, "");
      if (!expectedComparable || !expectedCompact) {
        continue;
      }

      if (rawComparable === expectedComparable || rawCompact === expectedCompact) {
        return canonicalHu;
      }

      if (extractedHu && compactOutboundPickingScanText(extractedHu) === expectedCompact) {
        return canonicalHu;
      }

      if (rawDigitsOnly && expectedDigits && rawCompact === expectedDigits) {
        return canonicalHu;
      }

      if (rawComparable.indexOf(expectedComparable) >= 0 || rawCompact.indexOf(expectedCompact) >= 0) {
        return canonicalHu;
      }
    }

    return fallback;
  }

  var FILLING_SCAN_DEDUP_MS = 1200;
  var fillingScanInFlightKey = "";
  var fillingLastScanKey = "";
  var fillingLastScanAt = 0;

  function resetFillingScanGuards() {
    fillingScanInFlightKey = "";
    fillingLastScanKey = "";
    fillingLastScanAt = 0;
  }

  function getFillingVisiblePallets(context) {
    return context && context.document && Array.isArray(context.document.pallets)
      ? context.document.pallets
      : [];
  }

  function isProbablyCompleteHuScan(value, context) {
    var rawText = String(value || "").trim();
    if (!rawText) {
      return false;
    }

    var upper = rawText
      .toUpperCase()
      .replace(/\u041D/g, "H")
      .replace(/\u0423/g, "U");
    if (upper === "H" || upper === "HU" || upper === "HU-") {
      return false;
    }

    var normalizedHu = resolveFillingScannedHu(value, context);
    if (!normalizedHu || !/^HU-/i.test(normalizedHu)) {
      return false;
    }

    var pallets = getFillingVisiblePallets(context);
    if (!pallets.length) {
      return !!extractHuCode(value);
    }

    var normalizedUpper = normalizedHu.toUpperCase();
    for (var index = 0; index < pallets.length; index++) {
      var canonical = String(pallets[index].huCode || pallets[index].hu_code || "")
        .trim()
        .toUpperCase();
      if (canonical === normalizedUpper) {
        return /^HU-\d{6,}$/i.test(canonical);
      }
    }

    return false;
  }

  function submitFillingScan(rawValue, context, options) {
    options = options || {};
    if (!isProbablyCompleteHuScan(rawValue, context)) {
      return Promise.resolve({ accepted: false, reason: "incomplete" });
    }

    var normalizedHu = resolveFillingScannedHu(rawValue, context);
    var orderId = context.workItem && context.workItem.orderId;
    var prdDocId = context.workItem && context.workItem.prdDocId;
    var key =
      String(orderId == null ? "" : orderId) +
      "|" +
      String(prdDocId == null ? "" : prdDocId) +
      "|" +
      String(normalizedHu).toUpperCase();
    var now = Date.now();

    if (fillingScanInFlightKey === key) {
      return Promise.resolve({ accepted: false, reason: "in_flight" });
    }
    if (fillingLastScanKey === key && now - fillingLastScanAt < FILLING_SCAN_DEDUP_MS) {
      return Promise.resolve({ accepted: false, reason: "duplicate" });
    }

    fillingScanInFlightKey = key;
    fillingLastScanKey = key;
    fillingLastScanAt = now;

    if (typeof options.executeScan !== "function") {
      fillingScanInFlightKey = "";
      return Promise.resolve({ accepted: false, reason: "no_handler" });
    }

    return Promise.resolve()
      .then(function () {
        return options.executeScan(normalizedHu);
      })
      .then(function (result) {
        return { accepted: true, huCode: normalizedHu, result: result };
      })
      .finally(function () {
        if (fillingScanInFlightKey === key) {
          fillingScanInFlightKey = "";
        }
      });
  }

  function renderOutboundPickingList(orders) {
    var rows = (orders || [])
      .map(function (order) {
        order = normalizeOutboundPickingOrderView(order);
        var orderId = order.orderId || 0;
        var picked = Number(order.pickedHuCount) || 0;
        var expected = Number(order.expectedHuCount) || 0;
        return (
          '<button class="filling-doc-card outbound-picking-order-card" data-outbound-order="' +
          escapeHtml(orderId) +
          '">' +
          '  <div class="filling-doc-main">' +
          '    <div class="filling-doc-title">Заказ № ' +
          escapeHtml(order.orderRef || "-") +
          "</div>" +
          (order.partnerName
            ? '    <div class="filling-doc-meta">Клиент: ' + escapeHtml(order.partnerName) + "</div>"
            : "") +
          '    <div class="order-status-pill order-status-accepted">' +
          escapeHtml(order.status || "Готов") +
          "</div>" +
          "  </div>" +
          '  <div class="filling-doc-progress">' +
          "    <div>Подобрано <strong>" +
          escapeHtml(picked + "/" + expected) +
          "</strong></div>" +
          "  </div>" +
          "</button>"
        );
      })
      .join("");

    if (!rows) {
      rows = '<div class="empty-state">Нет готовых клиентских заказов с ожидаемыми HU к отгрузке.</div>';
    }

    return (
      '<section class="screen filling-screen outbound-picking-screen">' +
      '  <div class="screen-card filling-card">' +
      '    <div class="section-title">Отгрузка</div>' +
      '    <div class="field-hint">Выберите готовый клиентский заказ для подбора паллет.</div>' +
      '    <div class="filling-doc-list">' +
      rows +
      "    </div>" +
      "  </div>" +
      "</section>"
    );
  }

  function renderOutboundPickingHuLines(lines) {
    var html = (Array.isArray(lines) ? lines : [])
      .map(function (line) {
        line = normalizeOutboundPickingLineView(line);
        return (
          '<div class="outbound-picking-hu-line">' +
          "  <span>" +
          escapeHtml(line.itemName || "-") +
          "</span>" +
          "  <strong>" +
          escapeHtml(formatQtyWithUnit(line.qty || 0, "шт")) +
          "</strong>" +
          "</div>"
        );
      })
      .join("");
    return html ? '<div class="outbound-picking-hu-lines">' + html + "</div>" : "";
  }

  function renderOutboundPickingHuList(hus) {
    var groups = buildOutboundPickingHuGroups(hus);
    var items = groups
      .map(function (group) {
        var rows = (group.hus || []).map(renderOutboundPickingHuRow).join("");
        if (!rows) {
          return "";
        }
        return (
          '<li class="filling-pallet-group">' +
          '  <div class="filling-pallet-group-title">' +
          escapeHtml(group.label) +
          "</div>" +
          '  <ul class="filling-pallet-group-items">' +
          rows +
          "  </ul>" +
          "</li>"
        );
      })
      .join("");

    if (!items) {
      return '<div class="empty-state">Нет ожидаемых HU к отгрузке.</div>';
    }

    return (
      '<div class="filling-pallet-list-card">' +
      '  <div class="filling-pallet-list-title">Паллеты к отгрузке</div>' +
      '  <ul class="filling-pallet-list filling-pallet-list--scroll-breathing outbound-picking-hu-list">' +
      items +
      "  </ul>" +
      "</div>"
    );
  }

  function renderOutboundPickingOrder(order, state) {
    order = normalizeOutboundPickingOrderView(order);
    var complete = isOutboundPickingOperationComplete(order);
    var message = state && state.message ? String(state.message) : "";
    var messageType = state && state.messageType ? String(state.messageType) : "";
    var messageHtml = message
      ? '<div class="filling-message filling-message-' + escapeHtml(messageType || "info") + '">' + escapeHtml(message) + "</div>"
      : "";

    app.innerHTML =
      '<section class="screen filling-screen outbound-picking-screen outbound-picking-screen--scan">' +
      '  <div class="screen-card filling-card filling-card--scan">' +
      '    <div class="filling-scan-header">' +
      escapeHtml(buildOutboundPickingHeaderLine(order)) +
      "</div>" +
      messageHtml +
      '    <div class="outbound-picking-progress">' +
      '      <div>Заказано <strong>' + escapeHtml(formatOrderQtyValue(order.orderedQty)) + "</strong></div>" +
      '      <div>Уже отгружено <strong>' + escapeHtml(formatOrderQtyValue(order.shippedQty)) + "</strong></div>" +
      '      <div>Осталось <strong>' + escapeHtml(formatOrderQtyValue(order.remainingQty)) + "</strong></div>" +
      '      <div>Отсканировано сейчас <strong>' + escapeHtml(formatOrderQtyValue(order.scannedQty)) + "</strong></div>" +
      "    </div>" +
      (complete && !order.isClosed
        ? '    <div class="filling-message filling-message-success">Все паллеты отсканированы. Документ не закрыт.</div>'
        : "") +
      '    <div class="filling-scan-card filling-scan-card--compact filling-scan-slot">' +
      '      <input class="form-input filling-scan-input tsd-scan-input-hidden" id="outboundPickingScanInput" type="text" autocomplete="off" autocapitalize="off" autocorrect="off" spellcheck="false" data-scan-allow="1" placeholder="HU-000001" />' +
      "    </div>" +
      renderOutboundPickingHuList(order.hus) +
      (order.scannedQty > 0 && !order.isClosed
        ? '    <div class="actions-bar"><button class="btn btn-primary" id="outboundPickingCompleteBtn" type="button">' +
          (complete ? "Закрыть документ" : "Завершить частичную отгрузку") + "</button></div>"
        : "") +
      "  </div>" +
      "</section>";

    wireOutboundPickingOrder(order || {}, state || {});
    finishRouteRender();
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

  function getCatalogItemBrand(item) {
    return String((item && item.brand) || "").trim();
  }

  function getCatalogItemVolume(item) {
    return String((item && item.volume) || "").trim();
  }

  function normalizeCatalogText(value) {
    return String(value || "").trim().toLowerCase();
  }

  function parseCatalogVolumeSort(value) {
    var label = String(value || "").trim();
    if (!label) {
      return { known: false, type: 9, value: 9007199254740991, label: "Без объема" };
    }
    var normalized = label.toLowerCase().replace(",", ".");
    var match = normalized.match(/(\d+(?:\.\d+)?)\s*(мл|ml|л|l|кг|kg|гр|г|g)/);
    if (!match) {
      return { known: true, type: 8, value: 9007199254740990, label: label };
    }
    var amount = Number(match[1]);
    var unit = match[2];
    var type = 0;
    var value = amount;
    if (unit === "кг" || unit === "kg") {
      value = amount * 1000;
    } else if (unit === "л" || unit === "l") {
      type = 1;
      value = amount * 1000;
    } else if (unit === "мл" || unit === "ml") {
      type = 1;
    }
    return { known: true, type: type, value: value, label: label };
  }

  function compareCatalogVolumeLabels(left, right) {
    var leftSort = parseCatalogVolumeSort(left);
    var rightSort = parseCatalogVolumeSort(right);
    if (leftSort.known !== rightSort.known) {
      return leftSort.known ? -1 : 1;
    }
    if (leftSort.type !== rightSort.type) {
      return leftSort.type - rightSort.type;
    }
    if (leftSort.value !== rightSort.value) {
      return leftSort.value - rightSort.value;
    }
    return String(leftSort.label).localeCompare(String(rightSort.label), "ru-RU", {
      numeric: true,
      sensitivity: "base",
    });
  }

  function buildCatalogBrandOptions(items) {
    var seen = {};
    var brands = [];
    (Array.isArray(items) ? items : []).forEach(function (item) {
      var brand = getCatalogItemBrand(item);
      var key = brand.toUpperCase();
      if (!brand || seen[key]) {
        return;
      }
      seen[key] = true;
      brands.push(brand);
    });
    return brands.sort(function (left, right) {
      return String(left).localeCompare(String(right), "ru-RU", {
        numeric: true,
        sensitivity: "base",
      });
    });
  }

  function filterCatalogItems(items, query, brand) {
    var q = normalizeCatalogText(query);
    var selectedBrand = normalizeCatalogText(brand);
    return (Array.isArray(items) ? items : []).filter(function (item) {
      if (selectedBrand && normalizeCatalogText(getCatalogItemBrand(item)) !== selectedBrand) {
        return false;
      }
      if (!q) {
        return true;
      }
      var haystack = [
        item.name,
        item.sku,
        item.gtin,
        item.barcode,
        item.brand,
        item.volume,
      ]
        .map(normalizeCatalogText)
        .join(" ");
      return haystack.indexOf(q) >= 0;
    });
  }

  function groupCatalogItemsByVolume(items) {
    var groups = {};
    var labels = [];
    (Array.isArray(items) ? items : []).forEach(function (item) {
      var label = getCatalogItemVolume(item) || "Без объема";
      if (!groups[label]) {
        groups[label] = [];
        labels.push(label);
      }
      groups[label].push(item);
    });
    return labels
      .sort(compareCatalogVolumeLabels)
      .map(function (label) {
        return {
          label: label,
          items: groups[label].slice().sort(function (left, right) {
            return String(left.name || "").localeCompare(String(right.name || ""), "ru-RU", {
              numeric: true,
              sensitivity: "base",
            });
          }),
        };
      });
  }

  function renderItems() {
    return (
      '<section class="screen">' +
      '  <div class="screen-card doc-screen-card">' +
      '    <div class="section-title">Каталог</div>' +
      '    <div class="items-toolbar">' +
      '      <div class="items-filter-field items-filter-field--search">' +
      '        <label class="form-label" for="itemsSearchInput">Поиск</label>' +
      '        <input class="form-input" id="itemsSearchInput" type="text" autocomplete="off" autocapitalize="off" autocorrect="off" spellcheck="false" placeholder="Название, бренд, объем, SKU, GTIN или штрихкод" />' +
      "      </div>" +
      '      <div class="items-filter-field">' +
      '        <label class="form-label" for="itemsBrandFilter">Бренд</label>' +
      '        <select class="form-input" id="itemsBrandFilter"></select>' +
      "      </div>" +
      "    </div>" +
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

  var globalHuResolvePending = false;

  function getCurrentRoutePath() {
    var hash = String((window.location && window.location.hash) || "").replace(/^#/, "");
    return hash && hash.charAt(0) === "/" ? hash : "/home";
  }

  function installGlobalHuScanHandler() {
    if (scanHandlerActive || isManualOverlayOpen() || (currentRoute && currentRoute.name === "login")) {
      return false;
    }
    setScanHandler(handleGlobalHuScan);
    return true;
  }

  function handleGlobalHuScan(scan, options) {
    var opts = options || {};
    var rawValue = scan && scan.value ? scan.value : scan;
    var huCode = extractHuCode(rawValue);
    if (!huCode || globalHuResolvePending) {
      return Promise.resolve({ accepted: false });
    }
    if (!opts.allowActiveInput && shouldBlockGlobalHuScanForActiveInput()) {
      return Promise.resolve({ accepted: false, blocked: "active-input" });
    }
    globalHuResolvePending = true;
    var resolveHu = opts.resolveHu || TsdStorage.apiResolveHu;
    var notify = opts.notify || function (message) { window.alert(message); };
    return resolveHu(huCode)
      .then(function (result) {
        if (!result || result.known !== true) {
          notify("HU неизвестен: " + huCode);
          return { accepted: true, known: false };
        }
        setNavOrigin(getCurrentRoutePath());
        navigate("/hu/" + encodeURIComponent(huCode));
        return { accepted: true, known: true, result: result };
      })
      .catch(function (error) {
        var message = error && error.status
          ? "Ошибка проверки HU. Попробуйте ещё раз."
          : "Нет связи с сервером. HU не проверен.";
        notify(message);
        return { accepted: true, error: error };
      })
      .finally(function () {
        globalHuResolvePending = false;
      });
  }

  function isTextEntryElement(el) {
    if (!el) {
      return false;
    }
    if (isScanAllowedElement(el)) {
      return false;
    }
    if (el.isContentEditable) {
      return true;
    }
    var tag = el.tagName ? String(el.tagName).toUpperCase() : "";
    return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT";
  }

  function shouldBlockGlobalHuScanForActiveInput() {
    var route = getRouteFromPath(getCurrentRoutePath());
    return route && route.name === "order" && isTextEntryElement(document.activeElement);
  }

  function handleNeutralHuScan(scan, fallback) {
    var rawValue = scan && scan.value ? scan.value : scan;
    if (extractHuCode(rawValue)) {
      return handleGlobalHuScan(scan);
    }
    if (typeof fallback === "function") {
      return fallback(rawValue, scan);
    }
    return Promise.resolve({ accepted: false });
  }

  function handleNeutralHuInputEnter(event, input, fallback) {
    if (!event || event.key !== "Enter") {
      return false;
    }
    var rawValue = input && input.value ? input.value : "";
    if (extractHuCode(rawValue)) {
      event.preventDefault();
      handleGlobalHuScan(rawValue, { allowActiveInput: true });
      return true;
    }
    if (typeof fallback === "function") {
      return fallback(event, rawValue) === true;
    }
    return false;
  }

  function executeTsdHuAction(action) {
    if (!action) {
      return false;
    }
    var type = String(action.type || "").toUpperCase();
    if (type === "OPEN_HU_CARD" && action.huCode) {
      setNavOrigin(getCurrentRoutePath());
      navigate("/hu/" + encodeURIComponent(action.huCode));
      return true;
    }
    if (type === "OPEN_FILLING" && action.orderId) {
      setNavOrigin(getCurrentRoutePath());
      navigate("/filling/" + encodeURIComponent(action.orderId));
      return true;
    }
    if (type === "OPEN_OUTBOUND" && action.orderId) {
      setNavOrigin(getCurrentRoutePath());
      navigate("/outbound/" + encodeURIComponent(action.orderId));
      return true;
    }
    if (type === "OPEN_ORDER" && action.orderId) {
      setNavOrigin(getCurrentRoutePath());
      navigate("/order/" + encodeURIComponent(action.orderId));
      return true;
    }
    if (type === "OPEN_DOCUMENT" && action.docId) {
      setNavOrigin(getCurrentRoutePath());
      navigate("/doc/" + encodeURIComponent(action.docId));
      return true;
    }
    if (type === "SHOW_MESSAGE") {
      window.alert(action.message || action.label || "");
      return true;
    }
    return false;
  }

  function openGlobalHuChoiceOverlay(result) {
    var overlay = document.createElement("div");
    overlay.className = "overlay overlay--centered global-hu-overlay";
    overlay.setAttribute("tabindex", "-1");
    var actions = Array.isArray(result.documentActions) ? result.documentActions : [];
    var relatedLabel = actions.length > 1 ? "Связанные документы" : "Связанный документ";
    overlay.innerHTML =
      '<div class="overlay-card confirm-card">' +
      '  <div class="overlay-header">' +
      '    <div class="overlay-title">' + escapeHtml(result.huCode + " найден") + "</div>" +
      '    <button class="btn btn-ghost overlay-close" type="button">Закрыть</button>' +
      "  </div>" +
      '  <div class="confirm-message"><strong>' + escapeHtml(result.title || result.state) + "</strong><br>" +
      escapeHtml(result.description || "") + "</div>" +
      '  <div class="global-hu-related-list" id="globalHuRelatedList"></div>' +
      '  <div class="confirm-actions">' +
      '    <button class="btn primary-btn" id="globalHuCardBtn" type="button">Карточка паллеты / HU</button>' +
      (actions.length
        ? '    <button class="btn btn-outline" id="globalHuRelatedBtn" type="button">' + escapeHtml(relatedLabel) + "</button>"
        : "") +
      '    <button class="btn btn-outline overlay-cancel" type="button">Назад</button>' +
      "  </div>" +
      "</div>";

    function close() {
      if (!overlay.parentNode) {
        return;
      }
      unlockOverlayScroll();
      overlay.parentNode.removeChild(overlay);
      document.removeEventListener("keydown", onKeyDown);
      ensureScanFocus();
    }

    function choose(action) {
      close();
      executeTsdHuAction(action);
    }

    function showRelatedActions() {
      var list = overlay.querySelector("#globalHuRelatedList");
      if (!list) {
        return;
      }
      list.innerHTML = actions.map(function (action, index) {
        return '<button class="btn btn-outline global-hu-related-action" type="button" data-global-hu-action="' +
          index + '">' + escapeHtml(action.label || action.type) + "</button>";
      }).join("");
      list.querySelectorAll("[data-global-hu-action]").forEach(function (button) {
        button.addEventListener("click", function () {
          choose(actions[Number(button.getAttribute("data-global-hu-action"))]);
        });
      });
    }

    function onKeyDown(event) {
      if (event.key === "Escape") {
        close();
      }
    }

    document.body.appendChild(overlay);
    lockOverlayScroll();
    document.addEventListener("keydown", onKeyDown);
    overlay.querySelector(".overlay-close").addEventListener("click", close);
    overlay.querySelector(".overlay-cancel").addEventListener("click", close);
    overlay.querySelector("#globalHuCardBtn").addEventListener("click", function () {
      choose(result.cardAction);
    });
    var relatedBtn = overlay.querySelector("#globalHuRelatedBtn");
    if (relatedBtn) {
      relatedBtn.addEventListener("click", function () {
        if (actions.length === 1) {
          choose(actions[0]);
          return;
        }
        showRelatedActions();
      });
    }
    focusOverlay(overlay);
  }

  function getTsdHuRowValue(row, camel, snake) {
    if (!row) {
      return null;
    }
    return row[camel] != null ? row[camel] : row[snake];
  }

  function normalizeTsdHuCodeValue(value) {
    return String(value || "").trim().toUpperCase();
  }

  function getTsdHuStatusLabel(state) {
    var normalized = normalizeTsdHuCodeValue(state);
    if (normalized === "PLANNED_PRODUCTION") {
      return "Запланирована к наполнению";
    }
    if (normalized === "PLANNED") {
      return "Запланирована";
    }
    if (normalized === "FILLED_PRODUCTION_PALLET") {
      return "Наполнена";
    }
    if (normalized === "FILLED") {
      return "Наполнена";
    }
    if (normalized === "CLOSED") {
      return "Закрыта";
    }
    if (normalized === "WAREHOUSE_FREE" || normalized === "WAREHOUSE_RESERVED") {
      return "На складе";
    }
    if (normalized === "OUTBOUND_EXPECTED") {
      return "Ожидается к отгрузке";
    }
    if (normalized === "OUTBOUND_PICKED") {
      return "В отгрузке";
    }
    if (normalized === "SHIPPED") {
      return "Отгружена";
    }
    if (normalized === "UNKNOWN") {
      return "Неизвестна";
    }
    if (normalized === "AMBIGUOUS") {
      return "Требует выбора";
    }
    if (normalized === "HISTORY_ONLY") {
      return "История HU";
    }
    return state ? String(state) : "Неизвестна";
  }

  function getTsdHuDocTypeLabel(value) {
    var normalized = normalizeTsdHuCodeValue(value);
    if (normalized === "PRODUCTION_RECEIPT") {
      return "Выпуск";
    }
    if (normalized === "OUTBOUND") {
      return "Отгрузка";
    }
    if (normalized === "INBOUND") {
      return "Приемка";
    }
    if (normalized === "MOVE") {
      return "Перемещение";
    }
    if (normalized === "WRITE_OFF") {
      return "Списание";
    }
    if (normalized === "INVENTORY" || normalized === "INVENTORY_CORRECTION") {
      return "Инвентаризация";
    }
    return value ? String(value) : "Документ";
  }

  function getTsdHuDocStatusLabel(value) {
    var normalized = normalizeTsdHuCodeValue(value);
    if (normalized === "CLOSED") {
      return "Закрыт";
    }
    if (normalized === "FILLED") {
      return "Наполнен";
    }
    if (normalized === "PLANNED") {
      return "Запланирован";
    }
    if (normalized === "DRAFT") {
      return "Черновик";
    }
    if (normalized === "CANCELLED") {
      return "Отменен";
    }
    return value ? String(value) : "";
  }

  function getTsdHuOrderTypeLabel(value) {
    var normalized = normalizeTsdHuCodeValue(value);
    if (normalized === "CUSTOMER") {
      return "Клиентский заказ";
    }
    if (normalized === "INTERNAL") {
      return "Внутренний заказ";
    }
    return "Заказ";
  }

  function getTsdHuActionLabel(action, documents) {
    var type = normalizeTsdHuCodeValue(action && action.type);
    if (type === "OPEN_DOCUMENT") {
      var docId = Number(action && action.docId) || Number(action && action.doc_id) || 0;
      var documentRow = (Array.isArray(documents) ? documents : []).filter(function (row) {
        return Number(getTsdHuRowValue(row, "docId", "doc_id")) === docId;
      })[0] || null;
      var docType = normalizeTsdHuCodeValue(getTsdHuRowValue(documentRow, "docType", "doc_type"));
      if (docType === "PRODUCTION_RECEIPT") {
        return "Открыть документ выпуска";
      }
      if (docType === "OUTBOUND") {
        return "Открыть документ отгрузки";
      }
      return "Открыть документ";
    }
    if (type === "OPEN_ORDER") {
      return (action && action.label) || "Открыть заказ";
    }
    if (type === "OPEN_FILLING") {
      return (action && action.label) || "Открыть наполнение";
    }
    if (type === "OPEN_OUTBOUND") {
      return (action && action.label) || "Открыть отгрузку";
    }
    if (type === "OPEN_HU_CARD") {
      return (action && action.label) || "Открыть карточку HU";
    }
    return (action && (action.label || action.type)) || "";
  }

  function buildTsdHuContentRows(stock, pallets, reservations, documents) {
    var rows = [];
    (Array.isArray(pallets) ? pallets : []).forEach(function (pallet) {
      var components = Array.isArray(pallet.components) ? pallet.components : [];
      components.forEach(function (component) {
        rows.push({
          itemName: getTsdHuRowValue(component, "itemName", "item_name") || "Товар",
          qty: getTsdHuRowValue(component, "filledQty", "filled_qty") ||
            getTsdHuRowValue(component, "plannedQty", "planned_qty"),
          uom: getTsdHuRowValue(component, "uom", "uom") || "шт",
        });
      });
    });
    if (!rows.length) {
      (Array.isArray(stock) ? stock : []).forEach(function (row) {
        rows.push({
          itemName: getTsdHuRowValue(row, "itemName", "item_name") || "Товар",
          qty: getTsdHuRowValue(row, "qty", "qty"),
          uom: getTsdHuRowValue(row, "uom", "uom") || "шт",
        });
      });
    }
    if (!rows.length) {
      (Array.isArray(reservations) ? reservations : []).forEach(function (row) {
        rows.push({
          itemName: getTsdHuRowValue(row, "itemName", "item_name") || "Товар",
          qty: getTsdHuRowValue(row, "qty", "qty"),
          uom: "шт",
        });
      });
    }
    if (!rows.length) {
      (Array.isArray(documents) ? documents : []).forEach(function (row) {
        rows.push({
          itemName: getTsdHuRowValue(row, "itemName", "item_name") || "Товар",
          qty: getTsdHuRowValue(row, "qty", "qty"),
          uom: getTsdHuRowValue(row, "uom", "uom") || "шт",
        });
      });
    }
    return rows;
  }

  function renderTsdHuContentRows(rows) {
    if (!rows.length) {
      return '<div class="status-muted">Содержимое не найдено</div>';
    }
    var totalQty = 0;
    var totalUom = rows[0].uom || "шт";
    var canShowTotal = rows.length > 1;
    var html = rows.map(function (row) {
      var qty = Number(row.qty);
      if (!isFinite(qty)) {
        canShowTotal = false;
        qty = 0;
      }
      if ((row.uom || "шт") !== totalUom) {
        canShowTotal = false;
      }
      totalQty += qty;
      return '<div class="hu-line"><div>' +
        escapeHtml(row.itemName || "Товар") +
        "</div><div>" +
        escapeHtml(formatQtyWithUnit(qty, row.uom || "шт")) +
        "</div></div>";
    }).join("");
    if (canShowTotal) {
      html += '<div class="hu-line hu-line-total"><div>Итого</div><div>' +
        escapeHtml(formatQtyWithUnit(totalQty, totalUom)) +
        "</div></div>";
    }
    return html;
  }

  function renderTsdHuLocationRows(stock, state) {
    var positiveStock = (Array.isArray(stock) ? stock : []).filter(function (row) {
      return Number(getTsdHuRowValue(row, "qty", "qty")) > 0;
    });
    if (!positiveStock.length) {
      return '<div class="status-muted">' +
        escapeHtml(normalizeTsdHuCodeValue(state) === "PLANNED_PRODUCTION" ? "Еще не на складе" : "Нет на складе") +
        "</div>";
    }
    var seen = {};
    var rows = positiveStock.map(function (row) {
      var code = getTsdHuRowValue(row, "locationCode", "location_code") || "-";
      var name = getTsdHuRowValue(row, "locationName", "location_name") || "";
      var label = name ? code + " - " + name : code;
      if (seen[label]) {
        return "";
      }
      seen[label] = true;
      return '<div class="hu-line"><div>' + escapeHtml(label) + "</div></div>";
    }).filter(Boolean).join("");
    return rows || '<div class="status-muted">Нет на складе</div>';
  }

  function findTsdHuOrderRow(reservations, pallets, documents) {
    var sources = []
      .concat(Array.isArray(reservations) ? reservations : [])
      .concat(Array.isArray(pallets) ? pallets : [])
      .concat(Array.isArray(documents) ? documents : []);
    for (var i = 0; i < sources.length; i += 1) {
      var row = sources[i];
      if (getTsdHuRowValue(row, "orderId", "order_id") || getTsdHuRowValue(row, "orderRef", "order_ref")) {
        return row;
      }
    }
    return null;
  }

  function renderTsdHuOrderRows(reservations, pallets, documents) {
    var row = findTsdHuOrderRow(reservations, pallets, documents);
    if (!row) {
      return '<div class="status-muted">Не привязана</div>';
    }
    var orderRef = getTsdHuRowValue(row, "orderRef", "order_ref") || getTsdHuRowValue(row, "orderId", "order_id") || "-";
    var orderType = getTsdHuOrderTypeLabel(getTsdHuRowValue(row, "orderType", "order_type"));
    var partnerName = getTsdHuRowValue(row, "partnerName", "partner_name") || "";
    return '<div class="hu-line"><div>' +
      escapeHtml(orderRef + " - " + orderType) +
      (partnerName ? '<div class="hu-card-meta">Контрагент: ' + escapeHtml(partnerName) + "</div>" : "") +
      "</div></div>";
  }

  function buildTsdHuTechnicalRows(card, pallets, documents) {
    var rows = [];
    var seen = {};
    function add(value) {
      var text = String(value || "").trim();
      if (!text || seen[text]) {
        return;
      }
      seen[text] = true;
      rows.push(text);
    }
    (Array.isArray(documents) ? documents : []).forEach(function (row) {
      var docRef = getTsdHuRowValue(row, "docRef", "doc_ref") || "Документ";
      var docType = getTsdHuDocTypeLabel(getTsdHuRowValue(row, "docType", "doc_type"));
      var docStatus = getTsdHuDocStatusLabel(getTsdHuRowValue(row, "docStatus", "doc_status"));
      add([docRef, docType, docStatus].filter(Boolean).join(" · "));
    });
    (Array.isArray(pallets) ? pallets : []).forEach(function (row) {
      var prdRef = getTsdHuRowValue(row, "prdDocRef", "prd_doc_ref");
      if (prdRef) {
        add([prdRef, "Выпуск", getTsdHuDocStatusLabel(getTsdHuRowValue(row, "prdDocStatus", "prd_doc_status"))].filter(Boolean).join(" · "));
      }
    });
    var movement = card && card.latestMovement;
    if (movement) {
      add("Последнее движение: " +
        (getTsdHuRowValue(movement, "docRef", "doc_ref") || "-") +
        " · " +
        formatDateTime(getTsdHuRowValue(movement, "timestamp", "timestamp")));
    }
    return rows;
  }

  function renderTsdHuCard(card) {
    if (!card || card.known !== true) {
      return renderError("HU не найден");
    }
    var stock = Array.isArray(card.stock) ? card.stock : [];
    var pallets = Array.isArray(card.productionPallets) ? card.productionPallets : [];
    var reservations = Array.isArray(card.reservations) ? card.reservations : [];
    var documents = Array.isArray(card.documents) ? card.documents : [];
    var actions = Array.isArray(card.documentActions) ? card.documentActions : [];
    var contentHtml = renderTsdHuContentRows(buildTsdHuContentRows(stock, pallets, reservations, documents));
    var locationHtml = renderTsdHuLocationRows(stock, card.state);
    var orderHtml = renderTsdHuOrderRows(reservations, pallets, documents);
    var technicalRows = buildTsdHuTechnicalRows(card, pallets, documents);
    var technicalHtml = technicalRows.length
      ? '<details class="hu-technical"><summary>Техническая информация</summary><div class="hu-lines">' +
        technicalRows.map(function (row) {
          return '<div class="hu-line"><div>' + escapeHtml(row) + "</div></div>";
        }).join("") +
        "</div></details>"
      : "";
    var actionHtml = actions.map(function (action, index) {
      return '<button class="btn btn-outline" type="button" data-hu-card-action="' + index + '">' +
        escapeHtml(getTsdHuActionLabel(action, documents)) + "</button>";
    }).join("");

    return '<section class="screen"><div class="screen-card">' +
      '<div class="section-title">' + escapeHtml(card.huCode) + "</div>" +
      '<div class="hu-card"><div class="hu-card-title">Статус: ' + escapeHtml(getTsdHuStatusLabel(card.state || card.title)) + "</div></div>" +
      '<div class="section-title">Содержимое</div><div class="hu-lines">' + contentHtml + "</div>" +
      '<div class="section-title">Местоположение</div><div class="hu-lines">' + locationHtml + "</div>" +
      '<div class="section-title">Заказ</div><div class="hu-lines">' + orderHtml + "</div>" +
      (actionHtml ? '<div class="hu-actions">' + actionHtml + "</div>" : "") +
      technicalHtml +
      "</div></section>";
  }

  function wireTsdHuCard(card) {
    var actions = Array.isArray(card && card.documentActions) ? card.documentActions : [];
    document.querySelectorAll("[data-hu-card-action]").forEach(function (button) {
      button.addEventListener("click", function () {
        executeTsdHuAction(actions[Number(button.getAttribute("data-hu-card-action"))]);
      });
    });
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
      if (extractHuCode(trimmed)) {
        handleGlobalHuScan(trimmed);
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
    setLiveRefreshHandler(function () {
      if (!currentRoute || currentRoute.name !== "stock") {
        return;
      }
      updateDataStatus();
    });
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
      "      </div>" +
      "    </div>" +
      '    <div class="form-grid doc-form-grid">' +
      headerFields +
      "    </div>" +
      (isExported
        ? '<div class="notice">Документ экспортирован, редактирование недоступно.</div>'
        : "") +
      (doc.op === "PRODUCTION_RECEIPT"
        ? ""
        : '    <div class="section-subtitle">Сканирование</div>' + renderScanBlock(doc, isDraft)) +
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
      var value = scan && scan.value ? scan.value : scan;
      if (!lookupOverlay) {
        handleNeutralHuScan(value);
        return;
      }
      setLookupValue(value, true);
    }

    function openLookupOverlay() {
      if (lookupOverlay) {
        return;
      }
      lookupOverlay = document.createElement("div");
      lookupOverlay.className = "overlay overlay--centered hu-lookup-overlay";
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
      var receiptOrderValue = formatOrderLabel(header.order_ref);
      var receiptOrderId = header.order_id || null;
      return (
        '<div class="header-fields">' +
        '  <div class="field-row field-row-4">' +
        '    <div class="field-label">Заказ</div>' +
        '    <div class="field-value" id="orderValue">' +
        escapeHtml(receiptOrderValue) +
        "</div>" +
        '    <button class="btn btn-outline field-info-btn" id="orderInfoBtn" type="button" ' +
        (receiptOrderId ? "" : "disabled") +
        '>i</button>' +
        '    <button class="btn btn-outline field-pick" id="orderPickBtn" type="button" ' +
        (isDraft ? "" : "disabled") +
        ">+</button>" +
        "  </div>" +
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
    if (doc && doc.op === "PRODUCTION_RECEIPT") {
      return "";
    }
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
    var showProductionReceiptControls = doc.op === "PRODUCTION_RECEIPT";
    var isDraft = doc.status === "DRAFT";
    var selectedIndex =
      typeof doc.selectedLineIndex === "number" ? doc.selectedLineIndex : -1;

    var rows = lines
      .map(function (line, index) {
        var nameText = line.itemName ? line.itemName : "Неизвестный код";
        var qtyValue = Number(line.qty) || 0;
        var minusDisabledAttr = qtyValue <= 1 ? ' disabled' : "";
        var minusClassDisabled = qtyValue <= 1 ? " is-disabled" : "";
        var lineHu = showInventoryHu ? normalizeHuCode(line.to_hu) : null;
        var productionLineHu = showProductionReceiptControls
          ? normalizeHuCode(line.to_hu) || extractHuCode(line.to_hu) || normalizeValue(line.to_hu)
          : "";
        var productionLineLocation = showProductionReceiptControls
          ? normalizeValue(line.to || "")
          : "";
        var productionPackSingleHu = !!(line && (line.pack_single_hu || line.packSingleHu));
        var productionHuBadge = showProductionReceiptControls
          ? '<div class="line-prd-hu">HU: ' +
            escapeHtml(productionLineHu || "не назначен") +
            "</div>"
          : "";
        var productionLocationBadge = showProductionReceiptControls
          ? '<div class="line-prd-loc">Куда: ' +
            escapeHtml(productionLineLocation || "не назначено") +
            "</div>"
          : "";
        var productionToggleHtml = showProductionReceiptControls
          ? '<label class="line-prd-pack">' +
            "  <span>Общий HU</span>" +
            '  <span class="toggle-switch">' +
            '    <input type="checkbox" class="line-pack-toggle-input" data-index="' +
            index +
            '"' +
            (productionPackSingleHu ? " checked" : "") +
            (isDraft ? "" : " disabled") +
            " />" +
            '    <span class="toggle-slider"></span>' +
            "  </span>" +
            "</label>"
          : "";
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
          productionHuBadge +
          productionLocationBadge +
          productionToggleHtml +
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
      '      <span class="toggle-label">Темная тема</span>' +
      '      <span class="toggle-switch">' +
      '        <input type="checkbox" id="tsdThemeToggle" />' +
      '        <span class="toggle-slider"></span>' +
      "      </span>" +
      "    </label>" +
      (debugPanel ? debugPanel : "") +
      '    <button class="btn btn-outline" type="button" id="pwaCheckUpdateBtn">Проверить обновления</button>' +
      '    <div class="settings-version settings-version--centered" id="pwaAppVersion"></div>' +
      '    <div class="field-hint" id="pwaUpdateStatus"></div>' +
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
      header.order_type = getOrderTypeValue(doc) || null;
    } else if (op === "OUTBOUND") {
      header.from = fromLocation;
      header.hu = fromHu || "";
      header.partner_id = partnerId;
      header.order_id = orderId;
      header.order_ref = doc.order_ref || "";
      header.order_type = getOrderTypeValue(doc) || null;
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
          pack_single_hu: !!(line.packSingleHu || line.pack_single_hu),
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
          pack_single_hu: !!(line.packSingleHu || line.pack_single_hu),
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

    function refreshHomeLowStock() {
      var requestId = ++homeLowStockRequestSeq;
      if (!homeLowStockWrap || !isClientBlockEnabled("tsd_stock")) {
        return;
      }
      homeLowStockWrap.innerHTML = '<div class="status">Загрузка позиций ниже минимума...</div>';
      loadHomeLowStockRows()
        .then(function (rows) {
          if (!homeLowStockWrap || !currentRoute || currentRoute.name !== "home") {
            return;
          }
          if (requestId !== homeLowStockRequestSeq) {
            return;
          }
          homeLowStockWrap.innerHTML = renderHomeLowStockRows(rows || []);
        })
        .catch(function () {
          if (homeLowStockWrap && currentRoute && currentRoute.name === "home") {
            homeLowStockWrap.innerHTML = '<div class="status">Не удалось загрузить позиции ниже минимума.</div>';
          }
        });
    }

    if (showTsdBelowMinimumEntry && homeLowStockWrap && isClientBlockEnabled("tsd_stock")) {
      if (homeLowStockRequestId !== homeLowStockRequestSeq) {
        return;
      }
      refreshHomeLowStock();
    } else if (homeLowStockWrap) {
      homeLowStockWrap.innerHTML = "";
    }

    setLiveRefreshHandler(function () {
      if (!showTsdBelowMinimumEntry || !currentRoute || currentRoute.name !== "home") {
        return;
      }
      refreshHomeLowStock();
    });
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

  function getWarehouseTaskDeviceId() {
    return TsdStorage.getSetting("device_id").then(function (value) {
      return String(value || "").trim();
    });
  }

  function mapWarehouseTaskError(error) {
    var message = String(error && error.message ? error.message : error || "").trim();
    if (!message || message === "Failed to fetch" || message === "AbortError") {
      return "Нет связи с сервером. Задание не выполнено.";
    }
    if (message === "SERVER_ERROR" || message === "INVALID_RESPONSE") {
      return "Сервер вернул ошибку. Проверьте лог FlowStock Server.";
    }
    var translations = {
      TASK_NOT_IN_EXECUTION: "Сначала начните задание.",
      TASK_ALREADY_COMPLETED: "Задание уже завершено.",
      HU_NOT_IN_TASK: "HU не из задания.",
      HU_ALREADY_SCANNED: "HU уже отсканирован.",
      LOCATION_NOT_FOUND: "Место не найдено.",
      WRONG_LOCATION: "Место назначения неверное.",
      SCAN_HU_FIRST: "Сначала отсканируйте HU.",
      TASK_LINES_INCOMPLETE: "Не все шаги сканирования выполнены.",
      MISSING_BARCODE: "Отсканируйте штрихкод.",
      TASK_NOT_FOUND: "Задание не найдено."
    };
    if (translations[message]) {
      return translations[message];
    }
    return message;
  }

  function formatWarehouseTaskStatus(status) {
    var normalized = String(status || "").trim().toUpperCase();
    if (normalized === "NEW" || normalized === "ASSIGNED") {
      return "Назначено";
    }
    if (normalized === "IN_EXECUTION") {
      return "В работе";
    }
    if (normalized === "EXECUTED") {
      return "Выполнено (ожидает подтверждения)";
    }
    if (normalized === "CONFIRMED") {
      return "Подтверждено";
    }
    if (normalized === "CANCELLED") {
      return "Отменено";
    }
    return status || "-";
  }

  function renderWarehouseTasksList(tasks) {
    var rows = (tasks || [])
      .map(function (task) {
        var taskId = task.id || task.task_id;
        var hu = task.expected_hu_code || task.expectedHuCode || "-";
        return (
          '<button class="doc-item" data-warehouse-task="' +
          escapeHtml(taskId) +
          '">' +
          '  <div class="doc-main">' +
          '    <div class="doc-title">' +
          escapeHtml(task.task_ref || task.taskRef || "Задание") +
          "</div>" +
          '    <div class="doc-ref">HU: ' +
          escapeHtml(hu) +
          "</div>" +
          '    <div class="doc-created">' +
          escapeHtml(formatWarehouseTaskStatus(task.status)) +
          "</div>" +
          "  </div>" +
          "</button>"
        );
      })
      .join("");

    if (!rows) {
      rows = '<div class="empty-state">Активных заданий нет.</div>';
    }

    return (
      '<section class="screen">' +
      '  <div class="screen-card doc-screen-card">' +
      '    <div class="section-title">Задания склада</div>' +
      '    <div class="doc-list">' +
      rows +
      "</div>" +
      "</div>" +
      "</section>"
    );
  }

  function renderWarehouseTaskDetail(payload, state) {
    var task = (payload && payload.task) || {};
    var lines = (payload && payload.lines) || [];
    var line = lines[0] || {};
    var status = String(task.status || "").toUpperCase();
    var scanType = resolveWarehouseTaskScanType(line);
    var prompt =
      scanType === "HU"
        ? "Отсканируйте HU: " + (line.expected_hu_code || line.expectedHuCode || "-")
        : scanType === "LOCATION"
          ? "Отсканируйте место назначения"
          : "Все шаги выполнены";
    var messageHtml =
      state && state.message
        ? '<div class="status ' +
          escapeHtml(state.messageType || "") +
          '">' +
          escapeHtml(state.message) +
          "</div>"
        : "";
    var completeDisabled =
      status === "EXECUTED" || status === "CONFIRMED" || scanType !== "DONE" ? " disabled" : "";
    var scanDisabled = status === "EXECUTED" || status === "CONFIRMED" || scanType === "DONE" ? " disabled" : "";
    var doneNotice =
      status === "EXECUTED" || status === "CONFIRMED"
        ? '<div class="status ok">Отгрузка проведена на ТСД.</div>'
        : "";

    return (
      '<section class="screen">' +
      '  <div class="screen-card">' +
      '    <div class="section-title">' +
      escapeHtml(task.task_ref || task.taskRef || "Задание") +
      "</div>" +
      '    <div class="doc-created">' +
      escapeHtml(formatWarehouseTaskStatus(task.status)) +
      "</div>" +
      doneNotice +
      messageHtml +
      '    <div class="filling-preview-title">' +
      escapeHtml(prompt) +
      "</div>" +
      '    <div class="scan-row">' +
      '      <input id="warehouseTaskScanInput" class="scan-input" type="text" placeholder="Скан штрихкода"' +
      scanDisabled +
      " />" +
      '      <button class="btn primary-btn" id="warehouseTaskScanBtn"' +
      scanDisabled +
      ">Скан</button>" +
      "</div>" +
      '    <div class="actions-row">' +
      '      <button class="btn" data-route="tasks">К списку</button>' +
      '      <button class="btn primary-btn" id="warehouseTaskCompleteBtn"' +
      completeDisabled +
      ">Завершить</button>" +
      "</div>" +
      "  </div>" +
      "</section>"
    );
  }

  function resolveWarehouseTaskScanType(line) {
    if (!line) {
      return "HU";
    }
    var status = String(line.status || "").toUpperCase();
    if (status === "DONE" || status === "CANCELLED") {
      return "DONE";
    }
    if (line.scanned_hu_code || line.scannedHuCode || status === "SCANNED") {
      return "LOCATION";
    }
    return "HU";
  }

  function isWarehouseTaskLineDone(line) {
    return String((line && line.status) || "").toUpperCase() === "DONE";
  }

  function wireWarehouseTasksList() {
    var items = document.querySelectorAll("[data-warehouse-task]");
    items.forEach(function (item) {
      item.addEventListener("click", function () {
        var taskId = item.getAttribute("data-warehouse-task");
        if (taskId) {
          navigate("/tasks/" + encodeURIComponent(taskId));
        }
      });
    });

    setLiveRefreshHandler(function () {
      if (!currentRoute || currentRoute.name !== "tasks") {
        return;
      }
      getWarehouseTaskDeviceId()
        .then(function (deviceId) {
          return TsdStorage.apiListWarehouseTasks(deviceId);
        })
        .then(function (tasks) {
          if (currentRoute && currentRoute.name === "tasks") {
            app.innerHTML = renderWarehouseTasksList(tasks || []);
            wireWarehouseTasksList();
            finishRouteRender();
          }
        })
        .catch(function () {
          // keep current list
        });
    });
  }

  function openWarehouseTaskDetail(taskId, state) {
    return TsdStorage.apiGetWarehouseTask(taskId)
      .then(function (payload) {
        var task = payload.task || {};
        var status = String(task.status || "").toUpperCase();
        var startPromise =
          status === "IN_EXECUTION" || status === "EXECUTED" || status === "CONFIRMED"
            ? Promise.resolve(payload)
            : getWarehouseTaskDeviceId().then(function (deviceId) {
                return TsdStorage.apiStartWarehouseTask(taskId, deviceId).then(function () {
                  return TsdStorage.apiGetWarehouseTask(taskId);
                });
              });
        return startPromise.then(function (freshPayload) {
          app.innerHTML = renderWarehouseTaskDetail(freshPayload, state || {});
          wireWarehouseTaskDetail(taskId);
          finishRouteRender();
        });
      })
      .catch(function (error) {
        console.error(error);
        app.innerHTML = renderError(mapWarehouseTaskError(error));
        finishRouteRender();
      });
  }

  function wireWarehouseTaskDetail(taskId) {
    var scanInput = document.getElementById("warehouseTaskScanInput");
    var scanBtn = document.getElementById("warehouseTaskScanBtn");
    var completeBtn = document.getElementById("warehouseTaskCompleteBtn");
    var scanBusy = false;

    function focusScan() {
      if (!scanInput || scanInput.disabled) {
        return;
      }
      setPreferredScanTarget(scanInput);
      scanInput.focus();
    }

    function submitScan() {
      if (!scanInput || scanInput.disabled || scanBusy) {
        return;
      }
      var barcode = String(scanInput.value || "").trim();
      if (!barcode) {
        return;
      }
      scanBusy = true;
      getWarehouseTaskDeviceId()
        .then(function (deviceId) {
          return TsdStorage.apiGetWarehouseTask(taskId).then(function (payload) {
            var line = (payload.lines || [])[0] || {};
            var scanType = resolveWarehouseTaskScanType(line);
            return TsdStorage.apiScanWarehouseTask(taskId, {
              barcode: barcode,
              scanType: scanType,
              deviceId: deviceId
            });
          });
        })
        .then(function () {
          scanInput.value = "";
          return openWarehouseTaskDetail(taskId, {
            message: "Скан принят.",
            messageType: "ok"
          });
        })
        .catch(function (error) {
          scanBusy = false;
          return openWarehouseTaskDetail(taskId, {
            message: mapWarehouseTaskError(error),
            messageType: "error"
          });
        });
    }

    if (scanBtn) {
      scanBtn.addEventListener("click", submitScan);
    }
    if (scanInput) {
      scanInput.addEventListener("keydown", function (event) {
        if (event.key === "Enter") {
          event.preventDefault();
          submitScan();
        }
      });
      setScanHandler(function (value) {
        if (!scanInput || scanInput.disabled) {
          return;
        }
        scanInput.value = String(value || "").trim();
        submitScan();
      });
      focusScan();
    }

    if (completeBtn) {
      completeBtn.addEventListener("click", function () {
        if (completeBtn.disabled) {
          return;
        }
        getWarehouseTaskDeviceId()
          .then(function (deviceId) {
            return TsdStorage.apiCompleteWarehouseTask(taskId, deviceId);
          })
          .then(function () {
            return openWarehouseTaskDetail(taskId, {
              message: "Задание завершено на ТСД.",
              messageType: "ok"
            });
          })
          .catch(function (error) {
            return openWarehouseTaskDetail(taskId, {
              message: mapWarehouseTaskError(error),
              messageType: "error"
            });
          });
      });
    }

    var backButtons = document.querySelectorAll('[data-route="tasks"]');
    backButtons.forEach(function (btn) {
      btn.addEventListener("click", function () {
        navigate("/tasks");
      });
    });
  }

  function wireFillingList() {
    var orders = document.querySelectorAll("[data-filling-order]");
    orders.forEach(function (item) {
      item.addEventListener("click", function () {
        var orderId = item.getAttribute("data-filling-order");
        if (orderId) {
          setNavOrigin("/filling");
          navigate("/filling/" + encodeURIComponent(orderId));
        }
      });
    });

    setLiveRefreshHandler(function () {
      if (!currentRoute || currentRoute.name !== "filling") {
        return;
      }
      TsdStorage.apiGetProductionFillingOrders()
        .then(function (items) {
          if (currentRoute && currentRoute.name === "filling") {
            app.innerHTML = renderFillingList(items || []);
            wireFillingList();
            finishRouteRender();
          }
        })
        .catch(function () {
          // Keep the current list visible while the live refresh is unavailable.
        });
    });
  }

  function getFillingDeviceId() {
    return TsdStorage.getSetting("device_id").then(function (value) {
      return String(value || "").trim();
    });
  }

  function buildProductionFillSuccessMessage(result, remainingPalletCount) {
    var alreadyFilled = !!(result && (result.alreadyFilled || result.already_filled));
    var prdRef = result && (result.closedPrdDocRef || result.closed_prd_doc_ref);
    prdRef = prdRef ? String(prdRef).trim() : "";
    var prdClosed = !!(result && (result.prdAutoClosed || result.prd_auto_closed));
    var orderCompleted = Number(remainingPalletCount) <= 0;
    var serverMessage = result && result.message ? String(result.message).trim() : "";
    var parts = [];

    if (prdClosed && prdRef) {
      parts.push(alreadyFilled ? "Паллета уже проведена." : "Паллета проведена.");
      parts.push("PRD " + prdRef + " закрыт.");
      if (orderCompleted) {
        parts.push("Заказ выполнен: все паллеты наполнены.");
      }
      return parts.join(" ");
    }

    if (serverMessage) {
      return serverMessage;
    }

    if (orderCompleted) {
      return alreadyFilled
        ? "Паллета уже наполнена. Заказ выполнен: все паллеты наполнены."
        : "Паллета наполнена. Заказ выполнен: все паллеты наполнены.";
    }

    return alreadyFilled ? "Паллета уже наполнена." : "Паллета наполнена.";
  }

  function isProductionFillPrdClosed(result) {
    return !!(
      result &&
      (result.prdAutoClosed === true ||
        result.prd_auto_closed === true ||
        result.closedPrdDocId ||
        result.closed_prd_doc_id ||
        result.closedPrdDocRef ||
        result.closed_prd_doc_ref)
    );
  }

  function isProductionFillOrderCompleted(result) {
    return !!(
      result &&
      (result.orderCompleted === true ||
        result.order_completed === true ||
        result.isOrderCompleted === true ||
        result.is_order_completed === true)
    );
  }

  function isFillingContextUnavailableAfterSuccessfulFill(error) {
    var message = String(error && error.message ? error.message : error || "").trim();
    return (
      message === "Заказ недоступен для наполнения." ||
      message === "Выпуск по заказу уже завершён. Нет паллет к наполнению." ||
      message.indexOf("Нет паллет к наполнению") >= 0
    );
  }

  function getRemainingPalletCountFromFillingContext(context) {
    return context && context.document && context.document.summary
      ? Number(context.document.summary.remainingPalletCount) || 0
      : 0;
  }

  function getRemainingPalletCountFromFillResult(result) {
    if (!result || !result.document || !result.document.summary) {
      return null;
    }
    var remaining = Number(result.document.summary.remainingPalletCount);
    return isFinite(remaining) ? remaining : null;
  }

  function getRemainingFillablePalletCountFromFillResult(result) {
    if (!result) {
      return null;
    }
    var raw =
      result.remainingFillablePalletCount != null
        ? result.remainingFillablePalletCount
        : result.remaining_fillable_pallet_count;
    if (raw == null || raw === "") {
      return null;
    }
    var remaining = Number(raw);
    return isFinite(remaining) ? remaining : null;
  }

  function isProductionFillFinal(result) {
    if (!result || result.ok === false) {
      return false;
    }
    if (isProductionFillOrderCompleted(result)) {
      return true;
    }
    var remainingFillable = getRemainingFillablePalletCountFromFillResult(result);
    if (remainingFillable !== null && remainingFillable <= 0) {
      return true;
    }
    return false;
  }

  function handleProductionFillSuccess(context, preview, result) {
    var fillOrderId = resolveFillingOrderId(
      preview,
      context.workItem && context.workItem.orderId
    );
    return loadFillingContext(fillOrderId)
      .then(function (nextContext) {
        if (shouldPromptOperationClose("filling", nextContext.progress)) {
          if (typeof window.confirm === "function" && window.confirm("Все паллеты отсканированы.\nЗакрыть документ?")) {
            return TsdStorage.apiCompleteProductionFilling(fillOrderId).then(function () {
              renderFillingCompletionScreen(nextContext, result, null);
            });
          }
          declineOperationClosePrompt("filling", nextContext.progress);
        }
        var remainingPalletCount = getRemainingPalletCountFromFillingContext(nextContext);
        var successMessage = buildProductionFillSuccessMessage(result, remainingPalletCount);
        var successType =
          result && (result.alreadyFilled || result.already_filled) ? "warn" : "success";
        renderFillingScanScreen(nextContext, {
          message: successMessage,
          messageType: successType,
          preview: null,
        });
      })
      .catch(function (reloadError) {
        if (shouldRenderProductionFillCompletion(result, null, reloadError)) {
          renderFillingCompletionScreen(context, result, reloadError);
          return;
        }
        throw reloadError;
      });
  }

  function shouldRenderProductionFillCompletion(result, nextContext, reloadError) {
    if (reloadError) {
      return isFillingContextUnavailableAfterSuccessfulFill(reloadError) ||
        isProductionFillOrderCompleted(result);
    }
    if (isProductionFillOrderCompleted(result)) {
      return true;
    }
    return !!nextContext && getRemainingPalletCountFromFillingContext(nextContext) <= 0;
  }

  function buildProductionFillCompletionMessage(result, reloadError) {
    var alreadyFilled = !!(result && (result.alreadyFilled || result.already_filled));
    var prefix = alreadyFilled ? "Паллета уже наполнена." : "Паллета наполнена.";
    if (isProductionFillOrderCompleted(result) || isFillingContextUnavailableAfterSuccessfulFill(reloadError)) {
      return prefix + " Заказ выполнен.";
    }
    if (isProductionFillPrdClosed(result)) {
      return prefix + " Выпуск закрыт.";
    }
    return prefix + " Заказ выполнен.";
  }

  function renderFillingCompletion(context, result, reloadError) {
    var work = (context && context.workItem) || {};
    var prdRef = result && (result.closedPrdDocRef || result.closed_prd_doc_ref || getFillingWorkPrdRef(work));
    return (
      '<section class="screen filling-screen">' +
      '  <div class="screen-card filling-card filling-completion-card">' +
      '    <div class="section-title">Наполнение</div>' +
      '    <div class="filling-message filling-message-success">' +
      escapeHtml(buildProductionFillCompletionMessage(result, reloadError)) +
      "</div>" +
      '    <div class="filling-context-card">' +
      '      <div>Заказ: <strong>' +
      escapeHtml(getFillingWorkOrderRef(work)) +
      "</strong></div>" +
      (prdRef ? '      <div>PRD: <strong>' + escapeHtml(prdRef) + "</strong></div>" : "") +
      "    </div>" +
      '    <div class="actions-bar">' +
      '      <button class="btn primary-btn" id="fillingCompletionListBtn" type="button">К списку наполнения</button>' +
      "    </div>" +
      "  </div>" +
      "</section>"
    );
  }

  function renderFillingCompletionScreen(context, result, reloadError) {
    setScanHandler(null);
    setPreferredScanTarget(null);
    app.innerHTML = renderFillingCompletion(context, result, reloadError);
    var listBtn = document.getElementById("fillingCompletionListBtn");
    if (listBtn) {
      listBtn.addEventListener("click", function () {
        navigate("/filling");
      });
    }
    finishRouteRender();
  }

  function getTsdErrorDetails(error) {
    var payload = error && error.payload ? error.payload : null;
    var code = String(
      (error && error.code) ||
        (payload && payload.error) ||
        ""
    ).trim();
    var payloadMessage = String((payload && payload.message) || "").trim();
    var rawMessage = String(error && error.message ? error.message : error || "").trim();
    return {
      code: code,
      message: payloadMessage || (rawMessage && rawMessage !== code ? rawMessage : ""),
      rawMessage: rawMessage,
    };
  }

  function mapFillingError(error) {
    var details = getTsdErrorDetails(error);
    var code = details.code;
    var message = details.message || details.rawMessage;
    if (!message || details.rawMessage === "Failed to fetch" || details.rawMessage === "AbortError") {
      return "Нет связи с сервером. Наполнение не подтверждено.";
    }
    if (code === "SERVER_ERROR" || code === "INVALID_RESPONSE" || message === "SERVER_ERROR" || message === "INVALID_RESPONSE") {
      return "Сервер вернул ошибку при наполнении. Проверьте лог FlowStock Server.";
    }
    if (message === "Паллета не найдена в плане выпуска") {
      return message;
    }
    if (message === "Эта паллета относится к другому заказу") {
      return message;
    }
    if (message === "Паллета отменена" || message === "Паллета отменена и не может быть наполнена.") {
      return message;
    }
    if (message === "Выпуск превышает остаток по строке заказа") {
      return message;
    }
    if (message === "Документ выпуска уже закрыт.") {
      return "Документ выпуска уже закрыт.";
    }
    if (code === "MIXED_COMPONENT_SELECTION_REQUIRED" || code === "COMPONENT_LINE_IDS_REQUIRED" ||
        message === "MIXED_COMPONENT_SELECTION_REQUIRED" || message === "COMPONENT_LINE_IDS_REQUIRED") {
      return "Выберите хотя бы один незаполненный компонент микс-паллеты.";
    }
    if (code === "PRODUCTION_AUTO_CLOSE_REQUIRED" || message === "PRODUCTION_AUTO_CLOSE_REQUIRED") {
      return "Частичное наполнение mixed HU требует включённого автоматического проведения выпуска.";
    }
    if (code === "COMPONENT_NOT_IN_PALLET" || message === "COMPONENT_NOT_IN_PALLET") {
      return "Состав паллеты изменился. Отсканируйте HU повторно.";
    }
    return message;
  }

  function wireFillingScan(context, state) {
    var scanInput = document.getElementById("fillingScanInput");
    var completeBtn = document.getElementById("fillingCompleteBtn");
    var activePreview = state && state.preview;

    function focusScan() {
      if (!scanInput) {
        return;
      }
      setPreferredScanTarget(scanInput);
      window.setTimeout(function () {
        if (scanInput && scanInput.isConnected) {
          scanInput.value = "";
          scanInput.focus();
        }
      }, 30);
    }

    function refreshContext(nextState) {
      return loadFillingContext(context.workItem && context.workItem.orderId)
        .then(function (nextContext) {
          renderFillingScanScreen(nextContext, nextState || {});
        })
        .catch(function () {
          renderFillingScanScreen(context, nextState || {});
        });
    }

    function handleScannedValue(value) {
      submitFillingScan(value, context, {
        executeScan: function (huCode) {
          renderFillingScanScreen(context, {
            message: "Проверяем паллету...",
            messageType: "info",
            preview: null,
          });

          return getFillingDeviceId()
            .then(function (deviceId) {
              return TsdStorage.apiScanProductionPallet({
                orderId: context.workItem && context.workItem.orderId,
                prdDocId: context.workItem && context.workItem.prdDocId,
                huCode: huCode,
                deviceId: deviceId,
              });
            })
            .then(function (preview) {
              if (preview.alreadyFilled) {
                return refreshContext({
                  message: "Паллета уже наполнена",
                  messageType: "warn",
                  preview: null,
                });
              }
              var scanOrderId = resolveFillingOrderId(
                preview,
                context.workItem && context.workItem.orderId
              );
              return loadFillingContext(scanOrderId)
                .then(function (nextContext) {
                  renderFillingScanScreen(nextContext, {
                    message: "",
                    messageType: "",
                    preview: preview,
                  });
                })
                .catch(function () {
                  renderFillingScanScreen(context, {
                    message: "",
                    messageType: "",
                    preview: preview,
                  });
                });
            })
            .catch(function (error) {
              return refreshContext({
                message: mapFillingError(error),
                messageType: "error",
                preview: null,
              });
            });
        },
      }).then(function (outcome) {
        if (!outcome || !outcome.accepted) {
          focusScan();
        }
      });
    }

    if (scanInput) {
      scanInput.addEventListener("keydown", function (event) {
        if (event.key === "Enter") {
          event.preventDefault();
          handleScannedValue(scanInput.value);
        }
      });
      focusScan();
    }

    if (completeBtn) {
      completeBtn.addEventListener("click", function () {
        completeBtn.disabled = true;
        TsdStorage.apiCompleteProductionFilling(context.workItem && context.workItem.orderId)
          .then(function () {
            renderFillingCompletionScreen(context, { message: "Операция наполнения завершена." }, null);
          })
          .catch(function (error) {
            refreshContext({ message: mapFillingError(error), messageType: "error", preview: null });
          });
      });
    }

    setScanHandler(function (scan) {
      var value = scan && scan.value ? scan.value : scan;
      handleScannedValue(value);
    });

    if (activePreview) {
      openFillingPreviewOverlay(context, activePreview);
    }
  }

  function mapOutboundPickingError(error) {
    var details = getTsdErrorDetails(error);
    var code = details.code || details.rawMessage;
    if (details.rawMessage === "Failed to fetch" || details.rawMessage === "AbortError") {
      return "Нет связи с сервером. Подбор не сохранен.";
    }
    if (code === "HU_REQUIRED") {
      return "Отсканируйте HU.";
    }
    if (code === "HU_NOT_EXPECTED") {
      return "HU не ожидается для выбранного заказа.";
    }
    if (code === "HU_PICKED_IN_OTHER_OUTBOUND") {
      return "HU уже подобрана в другом открытом документе отгрузки.";
    }
    if (code === "PICKING_INCOMPLETE" || code === "PARTIAL_CONFIRMATION_REQUIRED") {
      return "Подтвердите частичную отгрузку.";
    }
    if (code === "HU_ALREADY_SHIPPED") {
      return "HU уже отгружен.";
    }
    if (code === "NO_SHIPMENT_REMAINING" || code === "SHIPMENT_REMAINING_EXCEEDED") {
      return "По заказу не осталось количества к отгрузке для этой HU.";
    }
    return details.message || "Ошибка скана HU. Проверьте паллету и заказ.";
  }

  function wireOutboundPickingList() {
    var orders = document.querySelectorAll("[data-outbound-order]");
    orders.forEach(function (item) {
      item.addEventListener("click", function () {
        var orderId = item.getAttribute("data-outbound-order");
        if (orderId) {
          setNavOrigin("/outbound");
          navigate("/outbound/" + encodeURIComponent(orderId));
        }
      });
    });

    setLiveRefreshHandler(function () {
      if (!currentRoute || currentRoute.name !== "outbound") {
        return;
      }
      TsdStorage.apiGetOutboundPickingOrders()
        .then(function (orders) {
          if (currentRoute && currentRoute.name === "outbound") {
            app.innerHTML = renderOutboundPickingList(orders || []);
            wireOutboundPickingList();
            finishRouteRender();
          }
        })
        .catch(function () {
          // Keep the current list visible while the live refresh is unavailable.
        });
    });
  }

  function wireOutboundPickingOrder(order, state) {
    order = normalizeOutboundPickingOrderView(order);
    var scanInput = document.getElementById("outboundPickingScanInput");
    var completeBtn = document.getElementById("outboundPickingCompleteBtn");
    var orderId = order.orderId;
    var scanBusy = false;
    var completeBusy = false;

    function focusScan() {
      if (!scanInput) {
        return;
      }
      setPreferredScanTarget(scanInput);
      window.setTimeout(function () {
        if (scanInput && scanInput.isConnected) {
          scanInput.value = "";
          scanInput.focus();
        }
      }, 30);
    }

    function refreshOrder(nextState) {
      return TsdStorage.apiGetOutboundPickingOrder(orderId)
        .then(function (nextOrder) {
          renderOutboundPickingOrder(nextOrder, nextState || {});
        })
        .catch(function () {
          renderOutboundPickingOrder(order, nextState || {});
        });
    }

    function handleScannedValue(value) {
      var rawScanValue = value;
      var huCode = resolveOutboundPickingScannedHu(rawScanValue, order);
      if (!huCode || scanBusy) {
        focusScan();
        return;
      }

      scanBusy = true;
      renderOutboundPickingOrder(order, {
        message: "Подбираем HU...",
        messageType: "info",
      });

      TsdStorage.apiScanOutboundPickingHu(orderId, huCode)
        .then(function (result) {
          var nextOrder = normalizeOutboundPickingOrderView(
            (result && result.order) || order
          );
          var complete = isOutboundPickingOperationComplete(nextOrder);
          if (shouldPromptOperationClose("outbound", nextOrder)) {
            if (typeof window.confirm === "function" && window.confirm("Все паллеты отсканированы.\nЗакрыть документ?")) {
              return TsdStorage.apiCompleteOutboundPicking(orderId, false).then(function () {
                navigate("/outbound");
              });
            }
            declineOperationClosePrompt("outbound", nextOrder);
          }
          renderOutboundPickingOrder(nextOrder, {
            message: complete
              ? "Все паллеты отсканированы. Готово к закрытию."
              : (result && result.message) || "HU подобрана.",
            messageType: result && result.alreadyPicked ? "warn" : "success",
          });
        })
        .catch(function (error) {
          if (typeof console !== "undefined" && console && typeof console.warn === "function") {
            console.warn("Outbound picking scan failed", {
              rawScanValue: rawScanValue,
              resolvedHuCode: huCode,
              orderId: orderId,
              status: error && error.status,
              code: error && error.code,
              message: error && error.message,
            });
          }
          return refreshOrder({
            message: mapOutboundPickingError(error),
            messageType: "error",
          });
        });
    }

    if (scanInput) {
      scanInput.addEventListener("keydown", function (event) {
        if (event.key === "Enter") {
          event.preventDefault();
          handleScannedValue(scanInput.value);
        }
      });
      focusScan();
    }

    if (completeBtn) {
      completeBtn.addEventListener("click", function () {
        if (completeBusy) {
          return;
        }
        completeBusy = true;
        completeBtn.disabled = true;
        var allowPartial = isOutboundPickingOperationPartial(order);
        if (
          allowPartial &&
          typeof window.confirm === "function" &&
          !window.confirm(
            "Отгружено " +
              formatOrderQtyValue(order.scannedQty) +
              " из " +
              formatOrderQtyValue(order.remainingQty) +
              ". Закрыть частичную отгрузку?"
          )
        ) {
          completeBusy = false;
          completeBtn.disabled = false;
          return;
        }

        TsdStorage.apiCompleteOutboundPicking(orderId, allowPartial)
          .then(function (result) {
            navigate("/outbound");
          })
          .catch(function (error) {
            completeBusy = false;
            completeBtn.disabled = false;
            refreshOrder({
              message: mapOutboundPickingError(error),
              messageType: "error",
            });
          });
      });
    }

    setScanHandler(function (scan) {
      var value = scan && scan.value ? scan.value : scan;
      handleScannedValue(value);
    });

    setLiveRefreshHandler(function () {
      if (!currentRoute || currentRoute.name !== "outboundOrder") {
        return;
      }
      refreshOrder(state || {});
    });
  }

  function wireOrders() {
    var searchInput = document.getElementById("ordersSearchInput");
    var listEl = document.getElementById("ordersList");
    var statusEl = document.getElementById("ordersStatus");
    var toggleReadyBtn = document.getElementById("ordersToggleReadyBtn");
    var toggleDoneBtn = document.getElementById("ordersToggleDoneBtn");
    var showReady = false;
    var showDone = false;
    var allOrders = [];

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text || "";
      }
    }

    function classifyOrderStatus(order) {
      var status = normalizeOrderStatusCode(
        (order && (order.status || order.statusDisplay || order.orderStatusDisplay || order.order_status_display)) || ""
      );
      var internalOrder = isInternalOrder(order);
      if (!status) {
        return "in_work";
      }
      if (status === "IN_PROGRESS") {
        return "in_work";
      }
      if (status === "ACCEPTED") {
        if (internalOrder) {
          return "in_work";
        }
        return "ready";
      }
      if (status === "SHIPPED") {
        return "done";
      }
      return "in_work";
    }

    function getOrdersStats(orders) {
      var stats = { ready: 0, done: 0 };
      (orders || []).forEach(function (order) {
        var bucket = classifyOrderStatus(order);
        if (bucket === "ready") {
          stats.ready += 1;
        } else if (bucket === "done") {
          stats.done += 1;
        }
      });
      return stats;
    }

    function updateFilterButtons(stats) {
      if (toggleReadyBtn) {
        toggleReadyBtn.textContent =
          (showReady ? "Скрыть готовые" : "Показать готовые") +
          (stats.ready > 0 ? " (" + stats.ready + ")" : "");
      }
      if (toggleDoneBtn) {
        toggleDoneBtn.textContent =
          (showDone ? "Скрыть выполненные" : "Показать выполненные") +
          (stats.done > 0 ? " (" + stats.done + ")" : "");
      }
    }

    function getFilteredOrders(orders) {
      return (orders || []).filter(function (order) {
        var bucket = classifyOrderStatus(order);
        if (bucket === "ready") {
          return showReady;
        }
        if (bucket === "done") {
          return showDone;
        }
        return true;
      });
    }

    function renderList(orders) {
      if (!listEl) {
        return;
      }
      var rows = (orders || [])
        .map(function (order) {
          var statusInfo = getOrderStatusInfoForOrder(order);
          var planInfo = getProductionPalletPlanInfo(order);
          var orderNumber =
            order.number || order.orderNumber || order.order_ref || order.orderRef || "—";
          var partnerLabel = getOrderPartnerLabel(order);
          var plannedDate = formatDate(order.plannedDate || order.planned_date);
          var shippedDate = formatDate(order.shippedAt || order.shipped_at);
          var createdAt = formatDateTime(order.createdAt || order.created_at);
          return (
            '<button class="doc-item order-item' +
            (planInfo.className === "order-plan-missing" ? " order-item-needs-plan" : "") +
            '" data-order="' +
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
            (planInfo.label
              ? '      <div class="order-meta-row ' + escapeHtml(planInfo.className) + '">' + escapeHtml(planInfo.label) + "</div>"
              : "") +
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
          setNavOrigin("/orders");
          navigate("/order/" + encodeURIComponent(orderId));
        });
      });
    }

    function applyOrdersView() {
      var stats = getOrdersStats(allOrders);
      var filteredOrders = getFilteredOrders(allOrders);
      updateFilterButtons(stats);
      renderList(filteredOrders);
      if (!allOrders || !allOrders.length) {
        setStatus("Заказов нет");
        return;
      }
      if (!filteredOrders.length) {
        setStatus("Нет заказов по выбранным фильтрам");
        return;
      }
      setStatus("Данные с сервера");
    }

    function loadOrders(query) {
      setStatus("Загрузка...");
      TsdStorage.listOrders({ q: query })
        .then(function (orders) {
          allOrders = Array.isArray(orders) ? orders : [];
          applyOrdersView();
        })
        .catch(function () {
          setStatus("Ошибка загрузки заказов");
          allOrders = [];
          updateFilterButtons({ ready: 0, done: 0 });
          renderList([]);
        });
    }

    if (searchInput) {
      searchInput.addEventListener("input", function () {
        loadOrders(searchInput.value);
      });
      searchInput.addEventListener("keydown", function (event) {
        handleNeutralHuInputEnter(event, searchInput);
      });
    }

    if (toggleReadyBtn) {
      toggleReadyBtn.addEventListener("click", function () {
        showReady = !showReady;
        applyOrdersView();
      });
    }

    if (toggleDoneBtn) {
      toggleDoneBtn.addEventListener("click", function () {
        showDone = !showDone;
        applyOrdersView();
      });
    }

    updateFilterButtons({ ready: 0, done: 0 });
    setLiveRefreshHandler(function () {
      if (!currentRoute || currentRoute.name !== "orders") {
        return;
      }
      loadOrders(searchInput ? searchInput.value : "");
    });
    loadOrders("");
  }

  function wireItems() {
    var searchInput = document.getElementById("itemsSearchInput");
    var brandSelect = document.getElementById("itemsBrandFilter");
    var listEl = document.getElementById("itemsList");
    var statusEl = document.getElementById("itemsStatus");
    var searchToken = 0;
    var cachedItems = [];

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
      if (item.brand) {
        parts.push("Бренд: " + item.brand);
      }
      return parts.join(" · ");
    }

    function renderList(items) {
      if (!listEl) {
        return;
      }
      var rows = groupCatalogItemsByVolume(items)
        .map(function (group) {
          var itemRows = group.items
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
          return (
            '<div class="catalog-volume-group">' +
            '  <div class="catalog-volume-title">' +
            escapeHtml(group.label) +
            "</div>" +
            '  <div class="catalog-volume-items">' +
            itemRows +
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

    function populateBrandFilter(items) {
      if (!brandSelect) {
        return;
      }
      var current = brandSelect.value;
      var brands = buildCatalogBrandOptions(items);
      brandSelect.innerHTML =
        '<option value="">Все бренды</option>' +
        brands
          .map(function (brand) {
            return '<option value="' + escapeHtml(brand) + '">' + escapeHtml(brand) + "</option>";
          })
          .join("");
      if (
        current &&
        brands.some(function (brand) {
          return normalizeCatalogText(brand) === normalizeCatalogText(current);
        })
      ) {
        brandSelect.value = current;
      }
    }

    function applyFilters() {
      var filtered = filterCatalogItems(
        cachedItems,
        searchInput ? searchInput.value : "",
        brandSelect ? brandSelect.value : ""
      );
      renderList(filtered);
      if (!cachedItems.length) {
        setStatus("Товаров нет");
      } else if (!filtered.length) {
        setStatus("Ничего не найдено");
      } else {
        setStatus("Данные с сервера");
      }
    }

    function loadItems() {
      var token = (searchToken += 1);
      setStatus("Загрузка...");
      TsdStorage.apiSearchItems("")
        .then(function (items) {
          if (token !== searchToken) {
            return;
          }
          cachedItems = Array.isArray(items) ? items : [];
          populateBrandFilter(cachedItems);
          applyFilters();
        })
        .catch(function () {
          if (token !== searchToken) {
            return;
          }
          setStatus("Ошибка загрузки товаров");
          cachedItems = [];
          populateBrandFilter(cachedItems);
          renderList([]);
        });
    }

    if (searchInput) {
      searchInput.addEventListener("input", function () {
        applyFilters();
      });
      searchInput.addEventListener("keydown", function (event) {
        handleNeutralHuInputEnter(event, searchInput);
      });
    }
    if (brandSelect) {
      brandSelect.addEventListener("change", applyFilters);
    }

    setLiveRefreshHandler(function () {
      if (!currentRoute || currentRoute.name !== "items") {
        return;
      }
      loadItems();
    });
    loadItems();
  }

  function wireOrderDetails() {
    var toggles = document.querySelectorAll("[data-order-line-toggle]");
    toggles.forEach(function (toggle) {
      function setExpanded(expanded) {
        var key = toggle.getAttribute("data-order-line-toggle");
        var panel = document.querySelector('[data-order-line-panel="' + key + '"]');
        toggle.setAttribute("aria-expanded", expanded ? "true" : "false");
        toggle.classList.toggle("is-expanded", expanded);
        if (panel) {
          panel.hidden = !expanded;
        }
      }

      toggle.addEventListener("click", function () {
        setExpanded(toggle.getAttribute("aria-expanded") !== "true");
      });
      toggle.addEventListener("keydown", function (event) {
        if (event.key !== "Enter" && event.key !== " ") {
          return;
        }
        event.preventDefault();
        setExpanded(toggle.getAttribute("aria-expanded") !== "true");
      });
    });
  }

  function wireOperationsMenu() {
    var buttons = document.querySelectorAll("[data-op]");
    var routes = document.querySelectorAll("[data-route]");
    buttons.forEach(function (btn) {
      btn.addEventListener("click", function () {
        var op = btn.getAttribute("data-op");
        if (!op || !isOperationEnabled(op)) {
          return;
        }
        createDocAndOpen(op, "operations");
      });
    });
    routes.forEach(function (btn) {
      btn.addEventListener("click", function () {
        var route = btn.getAttribute("data-route");
        if (!route) {
          return;
        }
        navigate("/" + route);
      });
    });
  }

  function buildOverlay(title) {
    var overlay = document.createElement("div");
    overlay.className = "overlay overlay--centered";
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

  function openOrderPicker(onSelect, options) {
    setScanHighlight(false);
    var pickerOptions = options || {};
    var allowInternal = pickerOptions.allowInternal !== false;
    var onlyInProgress = pickerOptions.onlyInProgress === true;
    var onlyShipmentAvailable = pickerOptions.onlyShipmentAvailable === true;
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
      TsdStorage.listOrders({ q: query })
        .then(function (orders) {
          var filteredOrders = (orders || []).filter(function (order) {
            if (onlyShipmentAvailable) {
              return isOrderAvailableForShipment(order);
            }
            if (onlyInProgress && normalizeOrderStatusCode(order && order.status) !== "IN_PROGRESS") {
              return false;
            }
            if (allowInternal) {
              return true;
            }
            return !isInternalOrder(order);
          });

          var list = filteredOrders.map(function (order) {
            var meta = [];
            var orderType = getOrderTypeValue(order);
            if (orderType === "INTERNAL") {
              meta.push("Тип: Внутренний");
            } else {
              meta.push("Тип: Клиентский");
            }
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
    overlay.className = "overlay overlay--centered";
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
    overlay.className = "overlay overlay--centered manual-input-overlay";
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
    overlay.className = "overlay overlay--centered";
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
    var productionLinePackToggleInputs = document.querySelectorAll(".line-pack-toggle-input");
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

    function isProductionReceiptOrderLocked() {
      return (
        doc.op === "PRODUCTION_RECEIPT" &&
        !!(doc.header.order_id || normalizeValue(doc.header.order_ref))
      );
    }

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
        if (isProductionReceiptOrderLocked()) {
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
      qtyOverlay.className = "overlay overlay--centered qty-overlay";
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

      function maybeValidateMoveQty(item, nextQty) {
        if (doc.op !== "MOVE" && doc.op !== "OUTBOUND" && doc.op !== "WRITE_OFF") {
          return Promise.resolve(true);
        }
        if (!doc.header.from_id) {
          return Promise.resolve(true);
        }
        var sourceHu = normalizeHuCode(lineData.from_hu);
        if (doc.op === "MOVE" && doc.header.move_internal && !sourceHu) {
          return Promise.resolve(true);
        }
        var stockLookupCode =
          (item && (item.barcode || item.gtin || item.sku)) || barcode;
        if (!normalizeValue(stockLookupCode)) {
          return Promise.resolve(true);
        }
        return TsdStorage.apiGetStockByBarcode(stockLookupCode)
          .then(function (stock) {
            var available = 0;
            if (sourceHu) {
              var rows = stock && Array.isArray(stock.byHu) ? stock.byHu : [];
              rows.forEach(function (row) {
                if (row.locationId === doc.header.from_id && normalizeHuCode(row.hu) === sourceHu) {
                  available += Number(row.qty) || 0;
                }
              });
            } else {
              var totals = stock && Array.isArray(stock.totalsByLocation) ? stock.totalsByLocation : [];
              totals.forEach(function (row) {
                if (row.locationId === doc.header.from_id) {
                  available += Number(row.qty) || 0;
                }
              });
            }

            if (nextQty > available) {
              if (sourceHu) {
                alert("Недостаточно остатка на выбранном HU.");
              } else {
                alert("Недостаточно остатка на выбранном месте хранения.");
              }
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

        maybeValidateMoveQty(item, nextQty).then(function (ok) {
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
                pack_single_hu: false,
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

      if (
        doc.header.order_id &&
        (!doc.header.order_ref || !normalizeValue(doc.header.order_type))
      ) {
        updates.push(
          TsdStorage.getOrderById(doc.header.order_id).then(function (order) {
            if (order) {
              if (!doc.header.order_ref) {
                doc.header.order_ref = order.number || order.orderNumber || "";
              }
              var orderType = getOrderTypeValue(order);
              if (orderType) {
                doc.header.order_type = orderType;
              }
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
        if (doc.op !== "PRODUCTION_RECEIPT" && !validateHuHeader(true)) {
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
      } else if (doc.op === "OUTBOUND") {
        if (!doc.header.partner_id) {
          setPartnerError("Выберите покупателя");
          valid = false;
        }
        var selectedOrderType = String(doc.header.order_type || "").trim().toUpperCase();
        if (selectedOrderType === "INTERNAL") {
          setPartnerError("Внутренний заказ нельзя использовать в отгрузке.");
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
      overlay.className = "overlay overlay--centered qty-overlay";
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

    function isProductionLineSharedHu(line) {
      return !!(line && (line.pack_single_hu || line.packSingleHu));
    }

    function setProductionLineSharedHu(line, value) {
      if (!line) {
        return;
      }
      var normalizedValue = !!value;
      line.pack_single_hu = normalizedValue;
      line.packSingleHu = normalizedValue;
    }

    function getProductionLineIndexForAssignment() {
      if (!Array.isArray(doc.lines) || !doc.lines.length) {
        return -1;
      }
      var selected =
        typeof doc.selectedLineIndex === "number" ? doc.selectedLineIndex : -1;
      if (selected >= 0 && selected < doc.lines.length) {
        return selected;
      }
      return 0;
    }

    function applyProductionHuToLine(index, huCode, clearValue) {
      if (doc.op !== "PRODUCTION_RECEIPT") {
        return;
      }
      if (!Array.isArray(doc.lines) || !doc.lines.length) {
        return;
      }
      if (index < 0 || index >= doc.lines.length) {
        return;
      }
      var line = doc.lines[index];
      var normalizedHu = clearValue ? "" : normalizeHuCode(huCode) || extractHuCode(huCode) || "";
      if (!clearValue && !normalizedHu) {
        alert("Это не HU-код. HU должен начинаться с HU-.");
        return;
      }

      var targetIndexes = [index];
      if (isProductionLineSharedHu(line)) {
        targetIndexes = doc.lines
          .map(function (entry, idx) {
            return isProductionLineSharedHu(entry) ? idx : -1;
          })
          .filter(function (idx) {
            return idx >= 0;
          });
      }

      targetIndexes.forEach(function (targetIndex) {
        var targetLine = doc.lines[targetIndex];
        targetLine.to_hu = normalizedHu || null;
      });
      doc.selectedLineIndex = index;
      doc.updatedAt = new Date().toISOString();
      setDocStatus(
        normalizedHu
          ? "HU " + normalizedHu + " назначен строке."
          : "HU в строке сброшен."
      );
      saveDocState().then(refreshDocView);
    }

    function toggleProductionLineSharedHu(index, nextValue) {
      if (doc.op !== "PRODUCTION_RECEIPT") {
        return;
      }
      if (!Array.isArray(doc.lines) || !doc.lines.length) {
        return;
      }
      if (index < 0 || index >= doc.lines.length) {
        return;
      }

      var line = doc.lines[index];
      setProductionLineSharedHu(line, nextValue);
      if (nextValue) {
        var sharedHu = normalizeHuCode(line.to_hu) || "";
        if (!sharedHu) {
          for (var i = 0; i < doc.lines.length; i += 1) {
            if (i === index) {
              continue;
            }
            if (!isProductionLineSharedHu(doc.lines[i])) {
              continue;
            }
            sharedHu = normalizeHuCode(doc.lines[i].to_hu) || "";
            if (sharedHu) {
              break;
            }
          }
        }
        if (sharedHu) {
          doc.lines.forEach(function (entry) {
            if (isProductionLineSharedHu(entry)) {
              entry.to_hu = sharedHu;
            }
          });
        }
      }

      doc.selectedLineIndex = index;
      doc.updatedAt = new Date().toISOString();
      saveDocState().then(refreshDocView);
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
      if (doc.op === "INBOUND") {
        return "to";
      }
      if (doc.op === "PRODUCTION_RECEIPT") {
        return null;
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

      if (doc.op === "INBOUND") {
        if (!hasSelectedLocation("to")) {
          var inboundMessage = getLocationErrorMessage("to");
          setLocationError("to", inboundMessage);
          valid = false;
          alertMessage = alertMessage || inboundMessage;
        }
      } else if (doc.op === "PRODUCTION_RECEIPT") {
        valid = true;
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
      if (doc.op === "PRODUCTION_RECEIPT") {
        var productionLineIndex = getProductionLineIndexForAssignment();
        if (productionLineIndex < 0) {
          setDocStatus("Нет строк для назначения HU.");
          focusBarcode();
          return;
        }
        applyProductionHuToLine(productionLineIndex, normalized, false);
        focusBarcode();
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
      huOverlay.className = "overlay overlay--centered hu-overlay";
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
      if (doc.op === "OUTBOUND" && isInternalOrder(order)) {
        alert("Внутренний заказ нельзя использовать в отгрузке.");
        return;
      }
      doc.header.order_id = order.orderId || order.order_id || order.id || null;
      doc.header.order_ref =
        order.number || order.orderNumber || order.order_id || order.orderId || "";
      doc.header.order_type = getOrderTypeValue(order) || null;
      if (doc.op === "OUTBOUND") {
        applyOutboundOrderLines(order);
        return;
      }
      if (doc.op === "PRODUCTION_RECEIPT") {
        applyProductionReceiptOrderLines(order);
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
        TsdStorage.apiGetOrderShipmentRemaining(orderId)
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

    function applyProductionReceiptOrderLines(order) {
      var orderId = order.orderId || order.order_id || order.id || null;
      if (!orderId) {
        saveDocState().then(refreshDocView);
        return;
      }

      function applyLines() {
        setDocStatus("Загрузка строк заказа...");
        TsdStorage.apiGetOrderReceiptRemaining(orderId)
          .then(function (receiptLines) {
            var sourceLines = Array.isArray(receiptLines) ? receiptLines : [];
            var activeLines = sourceLines.filter(function (line) {
              return (Number(line.remainingQty) || 0) > 0;
            });
            if (!activeLines.length) {
              doc.lines = [];
              doc.selectedLineIndex = null;
              doc.updatedAt = new Date().toISOString();
              saveDocState().then(refreshDocView);
              alert("По заказу нет позиций для выпуска.");
              return;
            }
            activeLines.sort(function (left, right) {
              var leftOrder = Number(left && left.sortOrder) || 0;
              var rightOrder = Number(right && right.sortOrder) || 0;
              return leftOrder - rightOrder;
            });
            var nextLines = activeLines.map(function (line) {
              var assignedHu =
                normalizeHuCode(line && line.toHu) ||
                extractHuCode(line && line.toHu) ||
                normalizeValue(line && line.toHu) ||
                null;
              var lineLocationId = Number(line && line.toLocationId) || null;
              var lineLocationCode = normalizeValue(line && line.toLocation);
              return {
                barcode: "",
                qty: Number(line.remainingQty) || 0,
                from: null,
                to: lineLocationCode || "",
                from_id: null,
                to_id: lineLocationId,
                from_hu: null,
                to_hu: assignedHu,
                reason_code: null,
                itemId: line.itemId || null,
                itemName: line.itemName || "",
                orderLineId: line.orderLineId || null,
                pack_single_hu: false,
              };
            });

            var firstWithLocation = nextLines.find(function (line) {
              return !!(line && line.to_id && line.to);
            });
            doc.header.to = firstWithLocation ? firstWithLocation.to : "";
            doc.header.to_name = null;
            doc.header.to_id = firstWithLocation ? firstWithLocation.to_id : null;
            doc.lines = nextLines;
            doc.selectedLineIndex = nextLines.length ? 0 : null;
            doc.updatedAt = new Date().toISOString();
            return saveDocState().then(refreshDocView);
          })
          .catch(function (error) {
            var message = error && error.message ? error.message : "";
            alert(message || "Не удалось загрузить строки заказа для выпуска.");
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
        openOrderPicker(applyOrderSelection, {
          allowInternal: doc.op !== "OUTBOUND",
          onlyInProgress: doc.op === "PRODUCTION_RECEIPT",
          onlyShipmentAvailable: doc.op === "OUTBOUND",
        });
      });
    }

    if (orderInfoBtn) {
      orderInfoBtn.addEventListener("click", function () {
        if (!doc.header.order_id) {
          return;
        }
        setNavOrigin(getCurrentRoutePath());
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
          if (doc.op === "INBOUND" && missingFields.indexOf("to") >= 0) {
            alert(
              "Для приемки выберите место хранения (Куда)."
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
        if (isProductionReceiptOrderLocked()) {
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

    Array.prototype.forEach.call(productionLinePackToggleInputs, function (input) {
      input.addEventListener("change", function () {
        if (!isDraftDoc || doc.op !== "PRODUCTION_RECEIPT") {
          return;
        }
        var index = parseInt(input.getAttribute("data-index"), 10);
        if (isNaN(index) || index < 0 || index >= doc.lines.length) {
          return;
        }
        toggleProductionLineSharedHu(index, !!input.checked);
      });
    });

    applyCatalogState();
    hydrateHeaderFromCatalog();

    refreshHuHeaderDisplay();
    focusFirstLocationOrBarcode();
  }

  function wireSettings() {
    var deviceIdValue = document.getElementById("deviceIdValue");
    var tsdThemeToggle = document.getElementById("tsdThemeToggle");
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
    var pwaAppVersion = document.getElementById("pwaAppVersion");
    var pwaCheckUpdateBtn = document.getElementById("pwaCheckUpdateBtn");
    var pwaUpdateStatus = document.getElementById("pwaUpdateStatus");

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

    if (tsdThemeToggle) {
      tsdThemeToggle.checked = tsdTheme === TSD_THEME_DARK;
      tsdThemeToggle.addEventListener("change", function () {
        applyTsdTheme(tsdThemeToggle.checked ? TSD_THEME_DARK : TSD_THEME_LIGHT);
      });
    }

    if (pwaAppVersion) {
      pwaAppVersion.textContent =
        window.TsdSwUpdate && typeof window.TsdSwUpdate.getAppVersionLabel === "function"
          ? window.TsdSwUpdate.getAppVersionLabel()
          : window.TSD_PWA_VERSION
            ? "Версия приложения: " + String(window.TSD_PWA_VERSION)
            : "Версия приложения: неизвестно";
    }

    function setPwaUpdateStatus(message) {
      if (!pwaUpdateStatus) {
        return;
      }
      pwaUpdateStatus.textContent = message || "";
    }

    if (pwaCheckUpdateBtn) {
      pwaCheckUpdateBtn.addEventListener("click", function () {
        if (!window.TsdSwUpdate || typeof window.TsdSwUpdate.checkNow !== "function") {
          setPwaUpdateStatus("Проверка обновлений недоступна");
          return;
        }
        pwaCheckUpdateBtn.disabled = true;
        setPwaUpdateStatus("Проверка обновления...");
        window.TsdSwUpdate.checkNow()
          .then(function (result) {
            result = result || {};
            setPwaUpdateStatus(result.message || "Новая версия не найдена");
            if (result.status === "update_available" && typeof window.TsdSwUpdate.showUpdateBanner === "function") {
              window.TsdSwUpdate.showUpdateBanner();
            }
          })
          .catch(function () {
            setPwaUpdateStatus("Не удалось проверить обновление");
          })
          .then(function () {
            pwaCheckUpdateBtn.disabled = false;
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

    ensureServerAvailable()
      .then(function () {
        return TsdStorage.apiGetNextDocRef(op);
      })
      .then(function (docRef) {
        docRef = String(docRef || "");
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
        alert("Не удалось зарезервировать номер документа. Проверьте связь с сервером.");
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
        order_id: null,
        order_ref: "",
        order_type: null,
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
        order_type: null,
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
    var match = cleaned.match(/HU-?(\d{6,})/);
    if (!match) {
      return "";
    }
    var digits = match[1];
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

    if (doc.op === "INBOUND") {
      if (!normalizeValue(header.to) && !header.to_id) {
        missing.push("to");
      }
      return missing;
    }

    if (doc.op === "PRODUCTION_RECEIPT") {
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

  function pickDefaultProductionReceiptLocation(locations) {
    var source = Array.isArray(locations) ? locations.slice() : [];
    if (!source.length) {
      return null;
    }
    source.sort(function (left, right) {
      var leftCode = String((left && left.code) || "");
      var rightCode = String((right && right.code) || "");
      return leftCode.localeCompare(rightCode, "ru", { sensitivity: "base" });
    });
    for (var i = 0; i < source.length; i += 1) {
      if (source[i] && source[i].autoHuDistributionEnabled) {
        return source[i];
      }
    }
    return source[0];
  }

  function requestProductionHuScanConfirmation(huCodes) {
    var expected = (huCodes || [])
      .map(function (value) {
        return normalizeHuCode(value) || extractHuCode(value);
      })
      .filter(function (value) {
        return !!value;
      });
    var uniqueMap = {};
    expected.forEach(function (value) {
      uniqueMap[value] = true;
    });
    var pending = Object.keys(uniqueMap);
    if (!pending.length) {
      return Promise.resolve(true);
    }

    return new Promise(function (resolve, reject) {
      var previousScanHandler = activeScanHandler;
      var previousScanTarget = getPreferredScanTarget();
      var scannedMap = {};
      var closed = false;
      var overlay = document.createElement("div");
      overlay.className = "overlay overlay--centered hu-confirm-overlay";
      overlay.setAttribute("data-scan-allow", "1");
      overlay.innerHTML =
        '<div class="overlay-card hu-confirm-card">' +
        '  <div class="overlay-header">' +
        '    <div class="overlay-title">Подтверждение HU</div>' +
        '    <button class="btn btn-ghost overlay-close" type="button">Закрыть</button>' +
        "  </div>" +
        '  <div class="hu-confirm-body">' +
        '    <div class="hu-confirm-hint">Отсканируйте нанесенные HU перед проведением.</div>' +
        '    <div class="field-error hu-confirm-error"></div>' +
        '    <div class="hu-confirm-progress"></div>' +
        '    <div class="hu-confirm-list"></div>' +
        '    <div class="overlay-actions">' +
        '      <button class="btn btn-outline hu-confirm-cancel" type="button">Отмена</button>' +
        '      <button class="btn primary-btn hu-confirm-submit" type="button" disabled>Подтвердить</button>' +
        "    </div>" +
        "  </div>" +
        "</div>";

      function cleanup() {
        if (closed) {
          return;
        }
        closed = true;
        setScanHandler(previousScanHandler);
        setPreferredScanTarget(previousScanTarget);
        if (overlay.parentNode) {
          unlockOverlayScroll();
          overlay.parentNode.removeChild(overlay);
        }
      }

      function renderState() {
        if (closed) {
          return;
        }
        var progressEl = overlay.querySelector(".hu-confirm-progress");
        var listEl = overlay.querySelector(".hu-confirm-list");
        var confirmBtn = overlay.querySelector(".hu-confirm-submit");
        var done = pending.filter(function (hu) {
          return !!scannedMap[hu];
        }).length;
        var left = pending.length - done;
        if (progressEl) {
          progressEl.textContent = "Подтверждено: " + done + " из " + pending.length + ". Осталось: " + left + ".";
        }
        if (confirmBtn) {
          confirmBtn.disabled = left > 0;
        }
        if (listEl) {
          listEl.innerHTML = pending
            .map(function (hu) {
              return (
                '<div class="hu-confirm-item' +
                (scannedMap[hu] ? " is-done" : "") +
                '">' +
                escapeHtml(hu) +
                "</div>"
              );
            })
            .join("");
        }
      }

      function setOverlayError(message) {
        var errorEl = overlay.querySelector(".hu-confirm-error");
        if (errorEl) {
          errorEl.textContent = message || "";
        }
      }

      function closeAsReject(message) {
        cleanup();
        reject(new Error(message));
      }

      function closeAsResolve() {
        cleanup();
        resolve(true);
      }

      var lastAcceptedHu = "";
      var lastAcceptedAt = 0;

      function resolveScannedHu(raw) {
        var directHu = normalizeHuCode(raw) || extractHuCode(raw);
        if (directHu && uniqueMap[directHu]) {
          return directHu;
        }
        var normalizedRaw = normalizeValue(raw)
          .toUpperCase()
          .replace(/\u041D/g, "H")
          .replace(/\u0423/g, "U");
        if (!normalizedRaw) {
          return "";
        }
        var compactRaw = normalizedRaw.replace(/\s+/g, "");
        var directMatches = compactRaw.match(/HU-[A-Z0-9-]+/g) || [];
        for (var i = 0; i < directMatches.length; i += 1) {
          if (uniqueMap[directMatches[i]]) {
            return directMatches[i];
          }
        }
        var cleanedRaw = normalizedRaw.replace(/[^A-Z0-9-]/g, "");
        for (var p = 0; p < pending.length; p += 1) {
          if (cleanedRaw.indexOf(pending[p]) >= 0) {
            return pending[p];
          }
        }
        return "";
      }

      function handleScan(scan) {
        if (closed) {
          return;
        }
        var raw = scan && scan.value ? scan.value : scan;
        var hu = resolveScannedHu(raw);
        if (!hu) {
          setOverlayError("Отсканированное значение не является HU.");
          return;
        }
        var now = Date.now();
        if (hu === lastAcceptedHu && now - lastAcceptedAt < 120) {
          return;
        }
        if (!uniqueMap[hu]) {
          setOverlayError("HU " + hu + " не относится к этому документу.");
          return;
        }
        lastAcceptedHu = hu;
        lastAcceptedAt = now;
        scannedMap[hu] = true;
        setOverlayError("");
        renderState();
      }

      var closeBtn = overlay.querySelector(".overlay-close");
      var cancelBtn = overlay.querySelector(".hu-confirm-cancel");
      var submitBtn = overlay.querySelector(".hu-confirm-submit");
      if (closeBtn) {
        closeBtn.addEventListener("click", function () {
          closeAsReject("Подтверждение HU отменено.");
        });
      }
      if (cancelBtn) {
        cancelBtn.addEventListener("click", function () {
          closeAsReject("Подтверждение HU отменено.");
        });
      }
      if (submitBtn) {
        submitBtn.addEventListener("click", function () {
          if (submitBtn.disabled) {
            return;
          }
          closeAsResolve();
        });
      }
      overlay.addEventListener("click", function (event) {
        if (event.target === overlay) {
          closeAsReject("Подтверждение HU отменено.");
        }
      });

      document.body.appendChild(overlay);
      lockOverlayScroll();
      focusOverlay(overlay);
      setPreferredScanTarget(null);
      setScanHandler(handleScan);
      enterScanMode();
      renderState();
    });
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

    function fetchJson(url) {
      var blockKey = getOperationBlockKey(doc.op) || currentClientBlockContext;
      return fetch(url, {
        method: "GET",
        headers: createBlockHeaders({}, blockKey),
      }).then(function (response) {
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

      if (currentDoc.op === "INBOUND") {
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

        if (doc.op === "INBOUND") {
          toHu = mainHu || toHu;
        } else if (doc.op === "OUTBOUND" || doc.op === "WRITE_OFF") {
          fromHu = mainHu || fromHu;
        }

        function collectProductionHuCodesFromLocalLines() {
          var huMap = {};
          (doc.lines || []).forEach(function (line) {
            var hu =
              normalizeHuCode(line && line.to_hu) ||
              extractHuCode(line && line.to_hu) ||
              normalizeValue(line && line.to_hu) ||
              "";
            if (hu) {
              huMap[hu] = true;
            }
          });
          return Object.keys(huMap);
        }

        function ensureProductionReceiptAssignmentsConfirmed() {
          if (doc.op !== "PRODUCTION_RECEIPT") {
            return Promise.resolve(true);
          }
          var lines = Array.isArray(doc.lines) ? doc.lines : [];
          if (!lines.length) {
            throw new Error("Нет строк для выпуска продукции.");
          }
          var hasInvalidLine = lines.some(function (line) {
            var lineToId = line && line.to_id != null ? Number(line.to_id) : null;
            var lineToHu = normalizeHuCode(line && line.to_hu) || extractHuCode(line && line.to_hu);
            return !lineToId || !lineToHu;
          });
          if (hasInvalidLine) {
            throw new Error("Для выпуска продукции должны быть назначены локация приемки и HU в каждой строке.");
          }
          var firstLineLocationId = Number(lines[0] && lines[0].to_id) || null;
          if (!toLocationId && firstLineLocationId) {
            toLocationId = firstLineLocationId;
            header.to_id = firstLineLocationId;
          }

          var huCodes = collectProductionHuCodesFromLocalLines();
          if (!huCodes.length) {
            throw new Error("Не найдены HU для подтверждения.");
          }

          return requestProductionHuScanConfirmation(huCodes);
        }

        function applyProductionPackFlags(serverDocId, postedLines) {
          if (doc.op !== "PRODUCTION_RECEIPT" || !serverDocId) {
            return Promise.resolve(true);
          }
          var targets = (postedLines || []).filter(function (entry) {
            return (
              entry &&
              entry.serverLineId &&
              entry.localLine &&
              !!(entry.localLine.pack_single_hu || entry.localLine.packSingleHu)
            );
          });
          if (!targets.length) {
            return Promise.resolve(true);
          }
          return targets.reduce(function (chain, entry) {
            return chain.then(function () {
              return postJson(
                baseUrl +
                  "/api/docs/" +
                  encodeURIComponent(serverDocId) +
                  "/lines/" +
                  encodeURIComponent(entry.serverLineId) +
                  "/pack-single-hu",
                { pack_single_hu: true }
              );
            });
          }, Promise.resolve(true));
        }

        return ensureProductionReceiptAssignmentsConfirmed().then(function () {
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
            var serverDocId = docInfo && docInfo.id != null ? Number(docInfo.id) : null;
            if (serverDocId != null && isNaN(serverDocId)) {
              serverDocId = null;
            }
            if (docInfo && docInfo.doc_ref) {
              var resolvedRef = String(docInfo.doc_ref);
              if (resolvedRef) {
                doc.doc_ref = resolvedRef;
              }
              if (docInfo.doc_ref_changed) {
                alert("Номер документа занят. Присвоен новый: " + resolvedRef);
              }
            }

            var postedLines = [];
            return doc.lines
              .reduce(function (chain, line) {
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
                  return postJson(baseUrl + "/api/docs/" + encodeURIComponent(docUid) + "/lines", linePayload).then(
                    function (lineResult) {
                      var lineInfo = lineResult && lineResult.line ? lineResult.line : null;
                      var serverLineId = lineInfo && lineInfo.id != null ? Number(lineInfo.id) : null;
                      if (serverLineId != null && isNaN(serverLineId)) {
                        serverLineId = null;
                      }
                      postedLines.push({
                        localLine: line,
                        serverLineId: serverLineId,
                      });
                      return true;
                    }
                  );
                });
              }, Promise.resolve(true))
              .then(function () {
                return applyProductionPackFlags(serverDocId, postedLines);
              })
              .then(function () {
                return postJson(baseUrl + "/api/docs/" + encodeURIComponent(docUid) + "/close", {
                  event_id: createUuid(),
                  device_id: deviceId,
                });
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
      return safeBarcode + "|" + (lineData.to || "") + "|" + (lineData.to_hu || "");
    }
    if (op === "OUTBOUND") {
      return safeBarcode + "|" + (lineData.from || "") + "|" + (lineData.from_hu || "");
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
      return (
        safeBarcode +
        "|" +
        (lineData.from || "") +
        "|" +
        (lineData.from_hu || "") +
        "|" +
        (lineData.reason_code || "")
      );
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

  if (window.FlowStockTsdTestHooks) {
    window.FlowStockTsdTestHooks.buildProductionFillCompletionMessage = buildProductionFillCompletionMessage;
    window.FlowStockTsdTestHooks.isFillingContextUnavailableAfterSuccessfulFill = isFillingContextUnavailableAfterSuccessfulFill;
    window.FlowStockTsdTestHooks.renderFillingCompletion = renderFillingCompletion;
    window.FlowStockTsdTestHooks.shouldRenderProductionFillCompletion = shouldRenderProductionFillCompletion;
    window.FlowStockTsdTestHooks.getRemainingPalletCountFromFillResult = getRemainingPalletCountFromFillResult;
    window.FlowStockTsdTestHooks.getRemainingFillablePalletCountFromFillResult =
      getRemainingFillablePalletCountFromFillResult;
    window.FlowStockTsdTestHooks.isProductionFillFinal = isProductionFillFinal;
    window.FlowStockTsdTestHooks.handleProductionFillSuccess = handleProductionFillSuccess;
    window.FlowStockTsdTestHooks.mapFillingError = mapFillingError;
    window.FlowStockTsdTestHooks.mapOutboundPickingError = mapOutboundPickingError;
    window.FlowStockTsdTestHooks.normalizeOutboundPickingOrderView = normalizeOutboundPickingOrderView;
    window.FlowStockTsdTestHooks.isOutboundPickingOperationComplete = isOutboundPickingOperationComplete;
    window.FlowStockTsdTestHooks.isOutboundPickingOperationPartial = isOutboundPickingOperationPartial;
    window.FlowStockTsdTestHooks.resolveOutboundPickingScannedHu = resolveOutboundPickingScannedHu;
    window.FlowStockTsdTestHooks.resolveFillingScannedHu = resolveFillingScannedHu;
    window.FlowStockTsdTestHooks.isProbablyCompleteHuScan = isProbablyCompleteHuScan;
    window.FlowStockTsdTestHooks.submitFillingScan = submitFillingScan;
    window.FlowStockTsdTestHooks.resetFillingScanGuards = resetFillingScanGuards;
    window.FlowStockTsdTestHooks.renderOutboundPickingList = renderOutboundPickingList;
    window.FlowStockTsdTestHooks.renderOutboundPickingOrder = renderOutboundPickingOrder;
    window.FlowStockTsdTestHooks.renderOutboundPickingHuList = renderOutboundPickingHuList;
    window.FlowStockTsdTestHooks.wireOutboundPickingList = wireOutboundPickingList;
    window.FlowStockTsdTestHooks.buildOutboundPickingSummaryLine = buildOutboundPickingSummaryLine;
    window.FlowStockTsdTestHooks.buildOutboundPickingHeaderLine = buildOutboundPickingHeaderLine;
    window.FlowStockTsdTestHooks.getOutboundPickingHuItemLabel = getOutboundPickingHuItemLabel;
    window.FlowStockTsdTestHooks.buildOutboundPickingHuGroups = buildOutboundPickingHuGroups;
    window.FlowStockTsdTestHooks.getRouteTransitionKey = getRouteTransitionKey;
    window.FlowStockTsdTestHooks.prepareRouteTransition = prepareRouteTransition;
    window.FlowStockTsdTestHooks.getBackRouteForRoute = getBackRouteForRoute;
    window.FlowStockTsdTestHooks.setNavOrigin = setNavOrigin;
    window.FlowStockTsdTestHooks.getNavOrigin = getNavOrigin;
    window.FlowStockTsdTestHooks.clearNavOrigin = clearNavOrigin;
    window.FlowStockTsdTestHooks.navigateBack = navigateBack;
    window.FlowStockTsdTestHooks.getOrderLineReadyToShipQty = getOrderLineReadyToShipQty;
    window.FlowStockTsdTestHooks.renderOrderDetails = renderOrderDetails;
    window.FlowStockTsdTestHooks.renderOrderLineHuDetails = renderOrderLineHuDetails;
    window.FlowStockTsdTestHooks.buildOrderBoundHuByItem = buildOrderBoundHuByItem;
    window.FlowStockTsdTestHooks.renderItems = renderItems;
    window.FlowStockTsdTestHooks.buildCatalogBrandOptions = buildCatalogBrandOptions;
    window.FlowStockTsdTestHooks.filterCatalogItems = filterCatalogItems;
    window.FlowStockTsdTestHooks.groupCatalogItemsByVolume = groupCatalogItemsByVolume;
    window.FlowStockTsdTestHooks.compareCatalogVolumeLabels = compareCatalogVolumeLabels;
    window.FlowStockTsdTestHooks.buildFillingScanSummaryLine = buildFillingScanSummaryLine;
    window.FlowStockTsdTestHooks.buildFillingScanHeaderLine = buildFillingScanHeaderLine;
    window.FlowStockTsdTestHooks.renderFillingScan = renderFillingScan;
    window.FlowStockTsdTestHooks.buildFillingPreviewHtml = buildFillingPreviewHtml;
    window.FlowStockTsdTestHooks.sortFillingListItems = sortFillingListItems;
    window.FlowStockTsdTestHooks.renderFillingList = renderFillingList;
    window.FlowStockTsdTestHooks.getFillingPalletItemLabel = getFillingPalletItemLabel;
    window.FlowStockTsdTestHooks.getFillingPalletGroupLabel = getFillingPalletGroupLabel;
    window.FlowStockTsdTestHooks.buildFillingPalletGroups = buildFillingPalletGroups;
    window.FlowStockTsdTestHooks.renderFillingPalletStatusList = renderFillingPalletStatusList;
    window.FlowStockTsdTestHooks.renderFillingPalletHuRow = renderFillingPalletHuRow;
    window.FlowStockTsdTestHooks.isMixedFillingPallet = isMixedFillingPallet;
    window.FlowStockTsdTestHooks.renderFillingMixedPalletLines = renderFillingMixedPalletLines;
    window.FlowStockTsdTestHooks.getFillingPalletGroupDisplayTitle = getFillingPalletGroupDisplayTitle;
    window.FlowStockTsdTestHooks.isFillingPalletCompleted = isFillingPalletCompleted;
    window.FlowStockTsdTestHooks.renderHome = renderHome;
    window.FlowStockTsdTestHooks.renderSettings = renderSettings;
    window.FlowStockTsdTestHooks.applyTsdTheme = applyTsdTheme;
    window.FlowStockTsdTestHooks.getStoredTsdTheme = getStoredTsdTheme;
    window.FlowStockTsdTestHooks.normalizeTsdTheme = normalizeTsdTheme;
    window.FlowStockTsdTestHooks.initTsdTheme = initTsdTheme;
    window.FlowStockTsdTestHooks.extractHuCode = extractHuCode;
    window.FlowStockTsdTestHooks.installGlobalHuScanHandler = installGlobalHuScanHandler;
    window.FlowStockTsdTestHooks.handleGlobalHuScan = handleGlobalHuScan;
    window.FlowStockTsdTestHooks.handleNeutralHuScan = handleNeutralHuScan;
    window.FlowStockTsdTestHooks.handleNeutralHuInputEnter = handleNeutralHuInputEnter;
    window.FlowStockTsdTestHooks.setScanHandler = setScanHandler;
    window.FlowStockTsdTestHooks.getActiveScanHandler = function () { return activeScanHandler; };
    window.FlowStockTsdTestHooks.isScanHandlerActive = function () { return scanHandlerActive; };
    window.FlowStockTsdTestHooks.initScannerManager = initScannerManager;
    window.FlowStockTsdTestHooks.finishRouteRender = finishRouteRender;
    window.FlowStockTsdTestHooks.renderRouteInternal = renderRouteInternal;
    window.FlowStockTsdTestHooks.renderFillingScanScreen = renderFillingScanScreen;
    window.FlowStockTsdTestHooks.openGlobalHuChoiceOverlay = openGlobalHuChoiceOverlay;
    window.FlowStockTsdTestHooks.executeTsdHuAction = executeTsdHuAction;
    window.FlowStockTsdTestHooks.renderTsdHuCard = renderTsdHuCard;
    window.FlowStockTsdTestHooks.getRouteFromPath = getRouteFromPath;
  }

  if (document.documentElement) {
    initTsdTheme();
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
        startVersionWatcher();
        startLiveUpdates();
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
            navigateBack();
          });
        }

        if (settingsBtn) {
          settingsBtn.addEventListener("click", function () {
            navigate("/settings");
          });
        }

        window.addEventListener("hashchange", renderRoute);
        window.addEventListener("beforeunload", function () {
          stopVersionWatcher();
          stopLiveUpdates();
        });
        renderRoute();
      })
      .catch(function () {
        if (app) {
          app.innerHTML = renderError("Ошибка инициализации");
        }
      });
  });
})();
