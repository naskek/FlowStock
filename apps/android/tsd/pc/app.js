(function () {
  "use strict";

  var app = document.getElementById("app");
  var tabs = document.querySelectorAll(".pc-tab");
  var logoutBtn = document.getElementById("logoutBtn");
  var accountLabel = document.getElementById("accountLabel");

  var currentView = "stock";
  var cachedItems = [];
  var cachedItemsById = {};
  var cachedItemTypes = [];
  var cachedLocations = [];
  var cachedLocationsById = {};
  var cachedStockRows = [];
  var cachedHuRows = [];
  var cachedCombinedRows = [];
  var clientBlocks = getDefaultClientBlocks();
  var clientBlocksRefreshInFlight = false;
  var CLIENT_BLOCK_HEADER = "X-FlowStock-Block-Key";

  function getDefaultClientBlocks() {
    return {
      pc_stock: true,
      pc_catalog: true,
      pc_orders: true,
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

  function isClientBlockEnabled(key) {
    return clientBlocks[key] !== false;
  }

  function getEnabledViews() {
    var views = [];
    if (isClientBlockEnabled("pc_stock")) {
      views.push("stock");
    }
    if (isClientBlockEnabled("pc_catalog")) {
      views.push("catalog");
    }
    if (isClientBlockEnabled("pc_orders")) {
      views.push("orders");
    }
    return views;
  }

  function resolveAllowedView(view) {
    var enabledViews = getEnabledViews();
    if (!enabledViews.length) {
      return null;
    }
    if (enabledViews.indexOf(view) >= 0) {
      return view;
    }
    return enabledViews[0];
  }

  function syncTabsVisibility() {
    tabs.forEach(function (tab) {
      var view = tab.getAttribute("data-view") || "";
      var visible =
        (view === "stock" && isClientBlockEnabled("pc_stock")) ||
        (view === "catalog" && isClientBlockEnabled("pc_catalog")) ||
        (view === "orders" && isClientBlockEnabled("pc_orders"));
      tab.hidden = !visible;
    });
  }

  function getClientBlocksSignature() {
    return Object.keys(clientBlocks)
      .sort()
      .map(function (key) {
        return key + ":" + (clientBlocks[key] === false ? "0" : "1");
      })
      .join("|");
  }

  function refreshClientBlocksIfChanged() {
    var before = getClientBlocksSignature();
    return loadClientBlocks().then(function () {
      syncTabsVisibility();
      var after = getClientBlocksSignature();
      if (before !== after) {
        currentView = resolveAllowedView(currentView) || "stock";
        renderView(currentView);
      }
      return after !== before;
    });
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

  function hasPcAccess(account) {
    return !!account && (account.platform === "PC" || account.platform === "BOTH");
  }

  function loadAccount() {
    try {
      var raw = localStorage.getItem("flowstock_account");
      if (!raw) {
        return null;
      }
      var parsed = JSON.parse(raw);
      if (!parsed || !parsed.device_id) {
        return null;
      }
      return {
        device_id: String(parsed.device_id || "").trim(),
        login: String(parsed.login || "").trim(),
        platform: normalizePlatform(parsed.platform),
      };
    } catch (error) {
      return null;
    }
  }

  function saveAccount(account) {
    try {
      localStorage.setItem("flowstock_account", JSON.stringify(account || {}));
    } catch (error) {
      // ignore storage failures
    }
  }

  function clearAccount() {
    try {
      localStorage.removeItem("flowstock_account");
    } catch (error) {
      // ignore storage failures
    }
  }

  function setAccountLabel(account) {
    if (!accountLabel) {
      return;
    }
    if (!account) {
      accountLabel.textContent = "Гость";
      return;
    }
    var label = account.login || account.device_id || "Пользователь";
    accountLabel.textContent = label;
  }

  function setLoginState(isLoggedIn) {
    document.body.classList.toggle("needs-login", !isLoggedIn);
  }

  function getBlockKeyForView(view) {
    if (view === "catalog") {
      return "pc_catalog";
    }
    if (view === "orders") {
      return "pc_orders";
    }
    if (view === "stock") {
      return "pc_stock";
    }
    return "";
  }

  function shouldAttachBlockHeader(url) {
    return url !== "/api/client-blocks" && url !== "/api/tsd/login";
  }

  function createRequestHeaders(source, url) {
    var headers = new Headers(source || {});
    var blockKey = shouldAttachBlockHeader(url) ? getBlockKeyForView(currentView) : "";
    if (blockKey && !headers.has(CLIENT_BLOCK_HEADER)) {
      headers.set(CLIENT_BLOCK_HEADER, blockKey);
    }
    return headers;
  }

  function handleBlockedClientRequest() {
    if (clientBlocksRefreshInFlight || !hasPcAccess(loadAccount())) {
      return;
    }

    clientBlocksRefreshInFlight = true;
    loadClientBlocks()
      .then(function () {
        syncTabsVisibility();
        currentView = resolveAllowedView(currentView) || "stock";
        renderView(currentView);
      })
      .finally(function () {
        clientBlocksRefreshInFlight = false;
      });
  }

  function fetchJson(url, options) {
    var controller = null;
    var timer = null;
    if (typeof AbortController !== "undefined") {
      controller = new AbortController();
    }
    var opts = options || {};
    if (controller) {
      opts.signal = controller.signal;
    }
    opts.headers = createRequestHeaders(opts.headers, url);
    timer = window.setTimeout(function () {
      if (controller) {
        controller.abort();
      }
    }, 8000);
    return fetch(url, opts)
      .then(function (response) {
        return response
          .json()
          .catch(function () {
            return null;
          })
          .then(function (payload) {
            if (!response.ok) {
              var message = payload && payload.error ? payload.error : "SERVER_ERROR";
              if (message === "BLOCK_DISABLED" && url !== "/api/client-blocks") {
                handleBlockedClientRequest();
              }
              throw new Error(message);
            }
            return payload;
          });
      })
      .finally(function () {
        if (timer) {
          clearTimeout(timer);
        }
      });
  }

  function apiLogin(login, password) {
    return fetchJson("/api/tsd/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ login: login, password: password }),
    });
  }

  function loadClientBlocks() {
    return fetchJson("/api/client-blocks")
      .then(function (result) {
        return applyClientBlocks(result && result.blocks);
      })
      .catch(function () {
        return applyClientBlocks(null);
      });
  }

  function formatDate(value) {
    if (!value) {
      return "-";
    }
    var date = new Date(value);
    if (isNaN(date.getTime())) {
      return "-";
    }
    return (
      pad2(date.getDate()) +
      "." +
      pad2(date.getMonth() + 1) +
      "." +
      date.getFullYear()
    );
  }

  function formatDateTime(value) {
    if (!value) {
      return "-";
    }
    var date = new Date(value);
    if (isNaN(date.getTime())) {
      return "-";
    }
    return (
      pad2(date.getDate()) +
      "." +
      pad2(date.getMonth() + 1) +
      "." +
      date.getFullYear() +
      " " +
      pad2(date.getHours()) +
      ":" +
      pad2(date.getMinutes())
    );
  }

  function pad2(value) {
    var num = Number(value);
    if (isNaN(num)) {
      return "00";
    }
    return num < 10 ? "0" + num : String(num);
  }

  function formatQtyDisplay(qty, itemId) {
    var item = cachedItemsById[Number(itemId)] || {};
    var unit = item.base_uom || "";
    return qty + (unit ? " " + unit : "");
  }

  function escapeHtml(value) {
    return String(value || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function normalizeSearchQuery(value) {
    return String(value || "").trim().toLowerCase();
  }

  function matchesItemSearch(entry, normalizedQuery, includeLocationCode) {
    if (!normalizedQuery) {
      return true;
    }

    if (entry.itemName && entry.itemName.toLowerCase().indexOf(normalizedQuery) !== -1) {
      return true;
    }
    if (entry.brand && entry.brand.toLowerCase().indexOf(normalizedQuery) !== -1) {
      return true;
    }
    if (entry.volume && entry.volume.toLowerCase().indexOf(normalizedQuery) !== -1) {
      return true;
    }
    if (entry.barcode && entry.barcode.toLowerCase().indexOf(normalizedQuery) !== -1) {
      return true;
    }
    if (entry.gtin && entry.gtin.toLowerCase().indexOf(normalizedQuery) !== -1) {
      return true;
    }
    if (includeLocationCode && entry.locationCode && entry.locationCode.toLowerCase().indexOf(normalizedQuery) !== -1) {
      return true;
    }

    return false;
  }

  function setCachedItems(items) {
    cachedItems = Array.isArray(items) ? items : [];
    cachedItemsById = {};
    cachedItems.forEach(function (item) {
      var minStockQty = Number(item.min_stock_qty);
      if (!isFinite(minStockQty)) {
        minStockQty = null;
      }
      cachedItemsById[Number(item.id)] = {
        itemId: Number(item.id),
        name: item.name || "",
        barcode: item.barcode || "",
        gtin: item.gtin || "",
        brand: item.brand || "",
        volume: item.volume || "",
        base_uom: item.base_uom_code || item.base_uom || "",
        itemTypeId: Number(item.item_type_id) || 0,
        itemTypeName: item.item_type_name || "",
        itemTypeEnableMinStockControl: item.item_type_enable_min_stock_control === true,
        minStockQty: minStockQty,
      };
    });
  }

  function setCachedLocations(locations) {
    cachedLocations = Array.isArray(locations) ? locations : [];
    cachedLocationsById = {};
    cachedLocations.forEach(function (loc) {
      cachedLocationsById[Number(loc.id)] = {
        locationId: Number(loc.id),
        code: loc.code || "",
        name: loc.name || "",
      };
    });
  }

  function renderLogin() {
    return (
      '<section class="pc-login-card">' +
      '  <div class="screen-title">Вход</div>' +
      '  <label class="form-label" for="pcLoginInput">Логин</label>' +
      '  <input class="form-input" id="pcLoginInput" type="text" autocomplete="username" />' +
      '  <label class="form-label" for="pcPasswordInput">Пароль</label>' +
      '  <input class="form-input" id="pcPasswordInput" type="password" autocomplete="current-password" />' +
      '  <button id="pcLoginBtn" class="btn primary-btn" type="button">Войти</button>' +
      '  <div id="pcLoginStatus" class="status"></div>' +
      "</section>"
    );
  }

  function wireLogin() {
    var loginInput = document.getElementById("pcLoginInput");
    var passwordInput = document.getElementById("pcPasswordInput");
    var loginBtn = document.getElementById("pcLoginBtn");
    var statusEl = document.getElementById("pcLoginStatus");

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text || "";
      }
    }

    function submit() {
      var login = loginInput && loginInput.value ? loginInput.value.trim() : "";
      var password = passwordInput ? passwordInput.value : "";
      if (!login || !password) {
        setStatus("Введите логин и пароль.");
        return;
      }
      if (loginBtn) {
        loginBtn.disabled = true;
      }
      setStatus("Подключение...");
      apiLogin(login, password)
        .then(function (result) {
          var deviceId = result && result.device_id ? String(result.device_id).trim() : "";
          var platform = normalizePlatform(result && result.platform);
          if (!deviceId) {
            throw new Error("NO_DEVICE_ID");
          }
          if (platform !== "PC" && platform !== "BOTH") {
            throw new Error("WRONG_PLATFORM");
          }
          applyClientBlocks(result && result.blocks);
          var account = { device_id: deviceId, login: login, platform: platform };
          saveAccount(account);
          setAccountLabel(account);
          setLoginState(true);
          currentView = resolveAllowedView(currentView) || "stock";
          syncTabsVisibility();
          renderView(currentView);
        })
        .catch(function (error) {
          if (loginBtn) {
            loginBtn.disabled = false;
          }
          var code = error && error.message ? error.message : "";
          var message = "Ошибка входа.";
          if (code === "INVALID_CREDENTIALS") {
            message = "Пользователь не найден. Обратитесь к оператору.";
          } else if (code === "DEVICE_BLOCKED") {
            message = "Аккаунт заблокирован. Обратитесь к оператору.";
          } else if (code === "WRONG_PLATFORM") {
            message = "Этот аккаунт не имеет доступа к ПК.";
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

  function renderStock() {
    return (
      '<section class="pc-card">' +
      '  <div class="section-title">Состояние склада</div>' +
      '  <div class="pc-toolbar">' +
      '    <div class="form-field">' +
      '      <label class="form-label" for="stockSearchInput">Поиск</label>' +
      '      <input class="form-input" id="stockSearchInput" type="text" autocomplete="off" placeholder="Название, бренд, объем, SKU, GTIN, штрихкод" />' +
      "    </div>" +
      '    <div class="form-field">' +
      '      <label class="form-label" for="stockLocationFilter">Место хранения</label>' +
      '      <select class="form-input" id="stockLocationFilter"></select>' +
      "    </div>" +
      '    <div class="form-field">' +
      '      <label class="form-label" for="stockTypeFilter">Тип</label>' +
      '      <select class="form-input" id="stockTypeFilter"></select>' +
      "    </div>" +
      '    <div class="form-field">' +
      '      <label class="form-label" for="stockHuInput">HU (только цифры)</label>' +
      '      <div class="pc-hu-inline">' +
      '        <span class="pc-hu-prefix">HU-</span>' +
      '        <input class="form-input" id="stockHuInput" type="text" autocomplete="off" inputmode="numeric" placeholder="например, 00010" />' +
      "      </div>" +
      '      <div id="stockHuHint" class="pc-input-hint" hidden>Можно вводить только цифры.</div>' +
      "    </div>" +
      '    <div id="stockStatus" class="pc-status"></div>' +
      "  </div>" +
      '  <div id="stockLowWrap"></div>' +
      '  <div id="stockTableWrap"></div>' +
      "</section>"
    );
  }

  function renderCatalog() {
    return (
      '<section class="pc-card">' +
      '  <div class="section-title">Каталог товаров</div>' +
      '  <div class="pc-toolbar">' +
      '    <div class="form-field">' +
      '      <label class="form-label" for="catalogSearchInput">Поиск</label>' +
      '      <input class="form-input" id="catalogSearchInput" type="text" autocomplete="off" placeholder="Название, бренд, объем, SKU, GTIN, штрихкод" />' +
      "    </div>" +
      '    <div class="form-field">' +
      '      <label class="form-label" for="catalogTypeFilter">Тип</label>' +
      '      <select class="form-input" id="catalogTypeFilter"></select>' +
      "    </div>" +
      '    <div class="pc-toolbar-actions">' +
      '      <button id="catalogRefreshBtn" class="btn btn-outline" type="button">Обновить</button>' +
      "    </div>" +
      '    <div id="catalogStatus" class="pc-status"></div>' +
      "  </div>" +
      '  <div class="pc-note">Поиск работает по тем же полям, что и в остатках: название, бренд, объем, SKU, GTIN и штрихкод.</div>' +
      '  <div id="catalogTableWrap"></div>' +
      "</section>"
    );
  }

  function renderNoAccess() {
    return (
      '<section class="pc-card">' +
      '  <div class="section-title">Доступ ограничен</div>' +
      '  <div class="pc-note">Все блоки ПК-клиента сейчас временно отключены администратором.</div>' +
      "</section>"
    );
  }

  function renderStockTable(rows) {
    if (!rows || !rows.length) {
      return '<div class="empty-state">Нет данных по остаткам.</div>';
    }
    var body = rows
      .map(function (row) {
        var qtyLabel = row.qtyDisplay || row.qty;
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

  function renderCatalogTable(rows) {
    if (!rows || !rows.length) {
      return '<div class="empty-state">Товары не найдены.</div>';
    }

    var body = rows
      .map(function (row) {
        return (
          "<tr>" +
          "<td>" +
          escapeHtml(String(row.itemId || "-")) +
          "</td>" +
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
          escapeHtml(row.gtin || "-") +
          "</td>" +
          "<td>" +
          escapeHtml(row.baseUom || "-") +
          "</td>" +
          "</tr>"
        );
      })
      .join("");

    return (
      '<table class="pc-table">' +
      "<thead><tr>" +
      "<th>ID</th>" +
      "<th>Товар</th>" +
      "<th>Бренд</th>" +
      "<th>Объем</th>" +
      "<th>SKU / ШК</th>" +
      "<th>GTIN</th>" +
      "<th>Ед.</th>" +
      "</tr></thead>" +
      "<tbody>" +
      body +
      "</tbody>" +
      "</table>"
    );
  }

  function loadCatalogData() {
    return Promise.all([
      fetchJson("/api/items"),
      fetchJson("/api/item-types?include_inactive=0"),
    ]).then(function (payloads) {
      var items = payloads[0];
      var itemTypes = payloads[1];
      cachedItemTypes = Array.isArray(itemTypes) ? itemTypes.slice() : [];
      var sourceItems = Array.isArray(items) ? items : [];
      var visibleItems = sourceItems.filter(function (item) {
        return item && item.item_type_is_visible_in_product_catalog === true;
      });
      if (visibleItems.length === 0 && sourceItems.length > 0) {
        // Fallback for legacy/inconsistent type visibility setup:
        // keep catalog usable instead of showing an empty screen.
        visibleItems = sourceItems.slice();
      }
      setCachedItems(visibleItems);
      return cachedItems;
    });
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

  function loadStockData() {
    return Promise.all([
      fetchJson("/api/items"),
      fetchJson("/api/locations"),
      fetchJson("/api/stock"),
      fetchJson("/api/hu-stock"),
      fetchJson("/api/item-types?include_inactive=0"),
    ]).then(function (payloads) {
      setCachedItems(payloads[0]);
      setCachedLocations(payloads[1]);
      cachedItemTypes = Array.isArray(payloads[4]) ? payloads[4] : [];
      var stockRows = Array.isArray(payloads[2]) ? payloads[2] : [];
      var huRows = Array.isArray(payloads[3]) ? payloads[3] : [];

      cachedStockRows = stockRows.map(function (row) {
        var item = cachedItemsById[Number(row.item_id)] || {};
        var loc = cachedLocationsById[Number(row.location_id)] || {};
        var qty = Number(row.qty) || 0;
        var qtyLabel = qty + (item.base_uom ? " " + item.base_uom : "");
        return {
          itemId: Number(row.item_id),
          locationId: Number(row.location_id),
          qty: qty,
          qtyDisplay: qtyLabel,
          itemName: item.name || "-",
          barcode: item.barcode || "",
          gtin: item.gtin || "",
          brand: item.brand || "",
          volume: item.volume || "",
          itemTypeId: Number(item.itemTypeId) || 0,
          itemTypeName: item.itemTypeName || "",
          locationCode: loc.code || "",
        };
      });

      cachedHuRows = huRows.map(function (row) {
        var item = cachedItemsById[Number(row.item_id)] || {};
        var loc = cachedLocationsById[Number(row.location_id)] || {};
        var qty = Number(row.qty) || 0;
        var qtyLabel = qty + (item.base_uom ? " " + item.base_uom : "");
        return {
          itemId: Number(row.item_id),
          locationId: Number(row.location_id),
          qty: qty,
          qtyDisplay: qtyLabel,
          itemName: item.name || "-",
          barcode: item.barcode || "",
          gtin: item.gtin || "",
          brand: item.brand || "",
          volume: item.volume || "",
          itemTypeId: Number(item.itemTypeId) || 0,
          itemTypeName: item.itemTypeName || "",
          locationCode: loc.code || "",
          hu: row.hu || "",
        };
      });

      buildCombinedRows();
    });
  }

  function buildCombinedRows() {
    var totalsByKey = {};
    cachedHuRows.forEach(function (row) {
      var key = row.itemId + "|" + row.locationId;
      totalsByKey[key] = (totalsByKey[key] || 0) + row.qty;
    });

    var combined = cachedHuRows.slice();
    cachedStockRows.forEach(function (row) {
      var key = row.itemId + "|" + row.locationId;
      var huQty = totalsByKey[key] || 0;
      var diff = row.qty - huQty;
      if (Math.abs(diff) < 0.000001) {
        return;
      }

      combined.push({
        itemId: row.itemId,
        locationId: row.locationId,
        qty: diff,
        qtyDisplay: formatQtyDisplay(diff, row.itemId),
        itemName: row.itemName,
        barcode: row.barcode,
        gtin: row.gtin,
        brand: row.brand,
        volume: row.volume,
        itemTypeId: Number(row.itemTypeId) || 0,
        itemTypeName: row.itemTypeName || "",
        locationCode: row.locationCode,
        hu: "",
      });
    });

    cachedCombinedRows = combined;
  }

  function wireStock() {
    var searchInput = document.getElementById("stockSearchInput");
    var locationSelect = document.getElementById("stockLocationFilter");
    var typeFilter = document.getElementById("stockTypeFilter");
    var huInput = document.getElementById("stockHuInput");
    var huHint = document.getElementById("stockHuHint");
    var statusEl = document.getElementById("stockStatus");
    var lowWrap = document.getElementById("stockLowWrap");
    var tableWrap = document.getElementById("stockTableWrap");
    var debounce = null;

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text || "";
      }
    }

    function setHuValidationState(isValid) {
      if (!huInput) {
        return;
      }
      huInput.classList.toggle("form-input-error", !isValid);
      if (huHint) {
        huHint.hidden = isValid;
      }
    }

    function getHuDigitsFilter() {
      if (!huInput) {
        return "";
      }
      var raw = String(huInput.value || "").trim();
      var isValid = /^\d*$/.test(raw);
      setHuValidationState(isValid);
      if (!isValid) {
        return "";
      }
      return raw;
    }

    function getTypeFilterId() {
      if (!typeFilter) {
        return 0;
      }
      return Number(typeFilter.value || 0) || 0;
    }

    function buildLowStockRows(typeId) {
      var totalsByItem = {};
      cachedStockRows.forEach(function (row) {
        var itemId = Number(row.itemId) || 0;
        if (!itemId) {
          return;
        }
        totalsByItem[itemId] = (totalsByItem[itemId] || 0) + (Number(row.qty) || 0);
      });

      return cachedItems
        .map(function (item) {
          var itemId = Number(item.id) || 0;
          var cached = cachedItemsById[itemId] || {};
          var minStockQty = Number(cached.minStockQty);
          var itemTypeId = Number(cached.itemTypeId) || 0;
          if (typeId && itemTypeId !== typeId) {
            return null;
          }
          if (!(cached.itemTypeEnableMinStockControl === true) || !isFinite(minStockQty)) {
            return null;
          }

          var qty = Number(totalsByItem[itemId] || 0);
          if (qty >= minStockQty) {
            return null;
          }

          var shortage = Math.max(0, minStockQty - qty);
          return {
            itemName: cached.name || item.name || "-",
            itemTypeName: cached.itemTypeName || "-",
            qtyDisplay: formatQtyDisplay(qty, itemId),
            minStockDisplay: formatQtyDisplay(minStockQty, itemId),
            shortageDisplay: formatQtyDisplay(shortage, itemId),
            shortage: shortage,
          };
        })
        .filter(function (row) {
          return !!row;
        })
        .sort(function (a, b) {
          if (b.shortage !== a.shortage) {
            return b.shortage - a.shortage;
          }
          return String(a.itemName || "").localeCompare(String(b.itemName || ""), "ru");
        });
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
      var query = normalizeSearchQuery(searchInput ? searchInput.value : "");
      var locationId = locationSelect ? Number(locationSelect.value) : 0;
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
        return matchesItemSearch(row, query, true);
      });

      setStatus("Строк: " + rows.length);
      renderLowStock();
      tableWrap.innerHTML = renderStockTable(rows);
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
            var leftOrder = Number(left && left.sort_order) || 0;
            var rightOrder = Number(right && right.sort_order) || 0;
            if (leftOrder !== rightOrder) {
              return leftOrder - rightOrder;
            }
            var leftName = String((left && left.name) || "").toLowerCase();
            var rightName = String((right && right.name) || "").toLowerCase();
            return leftName < rightName ? -1 : leftName > rightName ? 1 : 0;
          })
          .map(function (type) {
            var id = Number(type && type.id) || 0;
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
          cachedLocations
            .map(function (loc) {
              var label = loc.code ? loc.code + " — " + (loc.name || "") : loc.name || "";
              return (
                '<option value="' +
                escapeHtml(String(loc.id)) +
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

    function scheduleRender() {
      if (debounce) {
        clearTimeout(debounce);
      }
      debounce = window.setTimeout(renderRows, 150);
    }

    setStatus("Загрузка...");
    loadStockData()
      .then(function () {
        fillFilters();
        renderRows();
      })
      .catch(function () {
        setStatus("Ошибка загрузки остатков");
        if (tableWrap) {
          tableWrap.innerHTML = '<div class="empty-state">Данные недоступны.</div>';
        }
      });

    if (searchInput) {
      searchInput.addEventListener("input", scheduleRender);
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
  }

  function wireCatalog() {
    var searchInput = document.getElementById("catalogSearchInput");
    var typeFilter = document.getElementById("catalogTypeFilter");
    var refreshBtn = document.getElementById("catalogRefreshBtn");
    var statusEl = document.getElementById("catalogStatus");
    var tableWrap = document.getElementById("catalogTableWrap");
    var debounce = null;

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text || "";
      }
    }

    function buildRows() {
      return cachedItems
        .map(function (item) {
          return {
            itemId: Number(item.id) || 0,
            itemTypeId: Number(item.item_type_id) || 0,
            itemTypeName: item.item_type_name || "",
            itemName: item.name || "",
            brand: item.brand || "",
            volume: item.volume || "",
            barcode: item.barcode || "",
            gtin: item.gtin || "",
            baseUom: item.base_uom_code || item.base_uom || "",
          };
        })
        .sort(function (left, right) {
          var leftName = String(left.itemName || "").toLowerCase();
          var rightName = String(right.itemName || "").toLowerCase();
          if (leftName !== rightName) {
            return leftName < rightName ? -1 : 1;
          }
          return left.itemId - right.itemId;
        });
    }

    function renderRows() {
      if (!tableWrap) {
        return;
      }

      var query = normalizeSearchQuery(searchInput ? searchInput.value : "");
      var typeId = typeFilter ? Number(typeFilter.value || 0) : 0;
      var rows = buildRows().filter(function (row) {
        if (typeId && row.itemTypeId !== typeId) {
          return false;
        }
        return matchesItemSearch(row, query, false);
      });

      setStatus("Товаров: " + rows.length);
      tableWrap.innerHTML = renderCatalogTable(rows);
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
            var leftOrder = Number(left && left.sort_order) || 0;
            var rightOrder = Number(right && right.sort_order) || 0;
            if (leftOrder !== rightOrder) {
              return leftOrder - rightOrder;
            }
            var leftName = String((left && left.name) || "").toLowerCase();
            var rightName = String((right && right.name) || "").toLowerCase();
            return leftName < rightName ? -1 : leftName > rightName ? 1 : 0;
          })
          .map(function (type) {
            var id = Number(type && type.id) || 0;
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

    function loadAndRender() {
      setStatus("Загрузка...");
      loadCatalogData()
        .then(function () {
          fillTypeFilter();
          renderRows();
        })
        .catch(function () {
          setStatus("Ошибка загрузки каталога");
          if (tableWrap) {
            tableWrap.innerHTML = '<div class="empty-state">Данные недоступны.</div>';
          }
        });
    }

    function scheduleRender() {
      if (debounce) {
        clearTimeout(debounce);
      }
      debounce = window.setTimeout(renderRows, 150);
    }

    if (searchInput) {
      searchInput.addEventListener("input", scheduleRender);
    }
    if (typeFilter) {
      typeFilter.addEventListener("change", renderRows);
    }
    if (refreshBtn) {
      refreshBtn.addEventListener("click", loadAndRender);
    }

    loadAndRender();
  }

  function renderOrders() {
    return (
      '<section class="pc-card">' +
      '  <div class="section-title">Заказы</div>' +
      '  <div class="pc-toolbar">' +
      '    <div class="form-field">' +
      '      <label class="form-label" for="ordersSearchInput">Поиск</label>' +
      '      <input class="form-input" id="ordersSearchInput" type="text" autocomplete="off" placeholder="Номер заказа или контрагент" />' +
      "    </div>" +
      '    <div class="pc-toolbar-actions">' +
      '      <button id="ordersNewBtn" class="btn" type="button">Новый заказ</button>' +
      '      <button id="ordersRefreshBtn" class="btn btn-outline" type="button">Обновить</button>' +
      "    </div>" +
      '    <div id="ordersStatus" class="pc-status"></div>' +
      "  </div>" +
      '  <div class="pc-note">Создание и смена статуса отправляются как заявки. Применение происходит после подтверждения в WPF.</div>' +
      '  <div id="ordersTableWrap"></div>' +
      "</section>"
    );
  }

  function getOrderTypeLabel(orderType) {
    var normalized = String(orderType || "").trim().toUpperCase();
    if (normalized === "INTERNAL") {
      return "Внутренний выпуск";
    }
    return "Клиентский";
  }

  function renderOrdersTable(rows) {
    if (!rows || !rows.length) {
      return '<div class="empty-state">Заказов нет.</div>';
    }
    var body = rows
      .map(function (order) {
        var isPending = order && order.is_pending_confirmation;
        return (
          "<tr" +
          ' data-order="' + escapeHtml(String(order.id)) + '"' +
          ">" +
          "<td>" +
          escapeHtml(order.order_ref || "-") +
          "</td>" +
          "<td>" +
          escapeHtml(getOrderTypeLabel(order.order_type)) +
          "</td>" +
          "<td>" +
          escapeHtml(order.partner_name || "-") +
          "</td>" +
          "<td>" +
          escapeHtml(formatDate(order.due_date)) +
          "</td>" +
          "<td>" +
          escapeHtml(formatDate(order.shipped_at)) +
          "</td>" +
          "<td>" +
          getOrderStatusHtml(order) +
          "</td>" +
          "</tr>"
        );
      })
      .join("");
    return (
      '<table class="pc-table">' +
      "<thead><tr>" +
      "<th>Номер</th>" +
      "<th>Тип</th>" +
      "<th>Контрагент</th>" +
      "<th>План</th>" +
      "<th>Факт</th>" +
      "<th>Статус</th>" +
      "</tr></thead>" +
      "<tbody>" +
      body +
      "</tbody>" +
      "</table>"
    );
  }

  function loadOrders(query) {
    var q = String(query || "").trim();
    var url = "/api/orders?include_internal=1&include_pending_requests=1";
    if (q) {
      url += "&q=" + encodeURIComponent(q);
    }
    return fetchJson(url);
  }

  function loadOrderReferenceData() {
    return Promise.all([fetchJson("/api/partners?role=customer"), fetchJson("/api/items")]).then(function (payloads) {
      var partners = Array.isArray(payloads[0]) ? payloads[0] : [];
      var items = Array.isArray(payloads[1]) ? payloads[1] : [];
      return {
        partners: partners,
        items: items,
      };
    });
  }

  function loadNextOrderRef() {
    return fetchJson("/api/orders/next-ref").then(function (payload) {
      if (!payload || !payload.order_ref) {
        return "";
      }
      return String(payload.order_ref).trim();
    });
  }

  function toOrderStatusCode(display) {
    var normalized = String(display || "").trim().toLowerCase();
    if (normalized === "принят" || normalized === "accepted") {
      return "ACCEPTED";
    }
    if (normalized === "в процессе" || normalized === "in_progress") {
      return "IN_PROGRESS";
    }
    if (normalized === "черновик" || normalized === "draft") {
      return "DRAFT";
    }
    if (normalized === "отгружен" || normalized === "shipped") {
      return "SHIPPED";
    }
    return "";
  }

  function formatQuantity(value) {
    var number = Number(value) || 0;
    if (Math.abs(number - Math.round(number)) < 0.000001) {
      return String(Math.round(number));
    }
    return number.toFixed(3).replace(/0+$/, "").replace(/\.$/, "");
  }

  function isInternalOrder(order) {
    return String(order && order.order_type ? order.order_type : "").trim().toUpperCase() === "INTERNAL";
  }

  function isShippedOrder(order) {
    return toOrderStatusCode(order && order.status) === "SHIPPED";
  }

  function isActiveShipmentOrder(order) {
    var status = toOrderStatusCode(order && order.status);
    return (
      order &&
      !order.is_pending_confirmation &&
      !isInternalOrder(order) &&
      (status === "ACCEPTED" || status === "IN_PROGRESS")
    );
  }

  function getLineRequiredQty(line) {
    var left = Number(line && (line.qty_left != null ? line.qty_left : line.qty_remaining));
    if (!isNaN(left)) {
      return Math.max(0, left);
    }
    var ordered = Number(line && line.qty_ordered) || 0;
    var shipped = Number(line && line.qty_shipped) || 0;
    return Math.max(0, ordered - shipped);
  }

  function getLineAvailableQty(line) {
    return Number(line && line.qty_available) || 0;
  }

  function getAvailabilityState(line) {
    var required = getLineRequiredQty(line);
    var available = getLineAvailableQty(line);
    var shortage = Math.max(0, required - available);
    return {
      required: required,
      available: available,
      shortage: shortage,
      ready: shortage <= 0.000001,
    };
  }

  function getShipmentReadiness(lines) {
    var source = Array.isArray(lines) ? lines : [];
    if (!source.length) {
      return null;
    }

    var totalShortage = 0;
    var hasRequiredQty = false;
    source.forEach(function (line) {
      var state = getAvailabilityState(line);
      if (state.required > 0.000001) {
        hasRequiredQty = true;
      }
      totalShortage += state.shortage;
    });

    if (!hasRequiredQty) {
      return null;
    }

    return {
      ready: totalShortage <= 0.000001,
      shortage: totalShortage,
      text: totalShortage <= 0.000001 ? "Готов к отгрузке" : "Не готов к отгрузке",
    };
  }

  function getOrderStatusHtml(order) {
    var readiness = order && order.shipment_readiness;
    if (readiness && readiness.text) {
      return renderStatusBadge(readiness.text, readiness.ready ? "success" : "warning");
    }
    if (isShippedOrder(order)) {
      return renderStatusBadge((order && order.status) || "Отгружен", "completed");
    }
    return escapeHtml((order && order.status) || (order && order.is_pending_confirmation ? "Ожидает подтверждения" : "-"));
  }

  function renderReadinessBadge(readiness) {
    if (!readiness || !readiness.text) {
      return "";
    }
    return renderStatusBadge(readiness.text, readiness.ready ? "success" : "warning", "pc-status-badge-inline");
  }

  function renderStatusBadge(text, tone, extraClass) {
    var normalizedTone = tone || "neutral";
    var icon = "•";
    if (normalizedTone === "success") {
      icon = "✓";
    } else if (normalizedTone === "warning") {
      icon = "!";
    } else if (normalizedTone === "completed") {
      icon = "✓";
    }

    var className = "pc-status-badge pc-status-badge-" + normalizedTone;
    if (extraClass) {
      className += " " + extraClass;
    }

    return (
      '<span class="' +
      className +
      '">' +
      '<span class="pc-status-badge-icon" aria-hidden="true">' +
      escapeHtml(icon) +
      "</span>" +
      "<span>" +
      escapeHtml(text || "-") +
      "</span>" +
      "</span>"
    );
  }

  function loadOrderReadiness(order) {
    if (!isActiveShipmentOrder(order)) {
      return Promise.resolve(order);
    }

    return fetchJson("/api/orders/" + encodeURIComponent(order.id) + "/lines")
      .then(function (lines) {
        order.shipment_readiness = getShipmentReadiness(lines);
        return order;
      })
      .catch(function () {
        order.shipment_readiness = null;
        return order;
      });
  }

  function enrichOrdersWithReadiness(rows) {
    var source = Array.isArray(rows) ? rows : [];
    return Promise.all(
      source.map(function (order) {
        return loadOrderReadiness(order);
      })
    );
  }

  function openNewOrderModal(onSubmitted) {
    var modal = document.createElement("div");
    modal.className = "pc-modal";
    modal.innerHTML =
      '<div class="pc-modal-card">' +
      '  <div class="pc-modal-header">' +
      '    <div class="pc-modal-title">Новый заказ</div>' +
      '    <button class="btn btn-outline" type="button" id="newOrderCloseBtn">Закрыть</button>' +
      "  </div>" +
      '  <div class="pc-order-form">' +
      '    <div class="form-field">' +
      '      <label class="form-label" for="newOrderRefInput">Номер заказа</label>' +
      '      <input class="form-input" id="newOrderRefInput" type="text" autocomplete="off" />' +
      "    </div>" +
      '    <div class="form-field">' +
      '      <label class="form-label" for="newOrderPartnerInput">Контрагент</label>' +
      '      <input class="form-input" id="newOrderPartnerInput" type="text" autocomplete="off" placeholder="Введите имя или код" />' +
      "    </div>" +
      '    <div class="form-field">' +
      '      <label class="form-label" for="newOrderDueDateInput">Плановая дата</label>' +
      '      <input class="form-input" id="newOrderDueDateInput" type="date" />' +
      "    </div>" +
      '    <div class="form-field">' +
      '      <label class="form-label" for="newOrderCommentInput">Комментарий</label>' +
      '      <input class="form-input" id="newOrderCommentInput" type="text" autocomplete="off" />' +
      "    </div>" +
      "  </div>" +
      '  <div class="pc-order-lines-header">' +
      '    <div class="pc-modal-title">Строки заказа</div>' +
      '    <button class="btn btn-ghost" type="button" id="newOrderAddLineBtn">Добавить строку</button>' +
      "  </div>" +
      '  <div id="newOrderLinesWrap" class="pc-order-lines"></div>' +
      '  <div class="pc-modal-footer">' +
      '    <button class="btn primary-btn" type="button" id="newOrderSubmitBtn">Отправить заявку</button>' +
      '    <div id="newOrderStatus" class="status"></div>' +
      "  </div>" +
      "</div>";
    document.body.appendChild(modal);

    var refs = {
      card: modal.querySelector(".pc-modal-card"),
      closeBtn: modal.querySelector("#newOrderCloseBtn"),
      orderRefInput: modal.querySelector("#newOrderRefInput"),
      partnerInput: modal.querySelector("#newOrderPartnerInput"),
      dueDateInput: modal.querySelector("#newOrderDueDateInput"),
      commentInput: modal.querySelector("#newOrderCommentInput"),
      linesWrap: modal.querySelector("#newOrderLinesWrap"),
      addLineBtn: modal.querySelector("#newOrderAddLineBtn"),
      submitBtn: modal.querySelector("#newOrderSubmitBtn"),
      statusEl: modal.querySelector("#newOrderStatus"),
    };
    var items = [];
    var partners = [];
    var linesState = [];
    var selectedPartnerId = 0;
    var activeSuggestIndex = -1;
    var suggestionOverlay = document.createElement("div");
    suggestionOverlay.className = "pc-order-suggest pc-order-suggest-floating";
    document.body.appendChild(suggestionOverlay);
    var partnerSuggestionOverlay = document.createElement("div");
    partnerSuggestionOverlay.className = "pc-order-suggest pc-order-suggest-floating";
    document.body.appendChild(partnerSuggestionOverlay);

    function setStatus(text) {
      if (refs.statusEl) {
        refs.statusEl.textContent = text || "";
      }
    }

    function hideSuggestionOverlay() {
      activeSuggestIndex = -1;
      suggestionOverlay.classList.remove("is-open");
      suggestionOverlay.innerHTML = "";
      suggestionOverlay.removeAttribute("data-index");
      suggestionOverlay.style.left = "";
      suggestionOverlay.style.top = "";
      suggestionOverlay.style.width = "";
      suggestionOverlay.style.maxHeight = "";
    }

    function hidePartnerSuggestionOverlay() {
      partnerSuggestionOverlay.classList.remove("is-open");
      partnerSuggestionOverlay.innerHTML = "";
      partnerSuggestionOverlay.style.left = "";
      partnerSuggestionOverlay.style.top = "";
      partnerSuggestionOverlay.style.width = "";
      partnerSuggestionOverlay.style.maxHeight = "";
    }

    function positionSuggestionOverlay(queryEl, overlay) {
      if (!queryEl) {
        return;
      }

      var targetOverlay = overlay || suggestionOverlay;
      var rect = queryEl.getBoundingClientRect();
      var viewportMargin = 12;
      var maxWidth = Math.max(180, window.innerWidth - viewportMargin * 2);
      var minWidth = Math.min(220, maxWidth);
      var width = Math.max(minWidth, Math.min(rect.width, maxWidth));
      var left = rect.left;
      if (left + width > window.innerWidth - viewportMargin) {
        left = window.innerWidth - viewportMargin - width;
      }
      left = Math.max(viewportMargin, left);

      var availableBelow = window.innerHeight - rect.bottom - viewportMargin;
      var availableAbove = rect.top - viewportMargin;
      var openUpward = availableBelow < 180 && availableAbove > availableBelow;
      var availableHeight = openUpward ? availableAbove : availableBelow;
      var maxHeight = Math.max(120, Math.min(260, availableHeight - 8));
      var top = openUpward ? rect.top - maxHeight - 4 : rect.bottom + 4;

      if (top + maxHeight > window.innerHeight - viewportMargin) {
        top = window.innerHeight - viewportMargin - maxHeight;
      }
      top = Math.max(viewportMargin, top);

      targetOverlay.style.left = left + "px";
      targetOverlay.style.top = top + "px";
      targetOverlay.style.width = width + "px";
      targetOverlay.style.maxHeight = maxHeight + "px";
    }

    function showSuggestionOverlay(index, queryEl, filteredItems, selectedId) {
      var source = Array.isArray(filteredItems) ? filteredItems : [];
      if (!queryEl || !source.length) {
        hideSuggestionOverlay();
        return;
      }

      activeSuggestIndex = index;
      suggestionOverlay.setAttribute("data-index", String(index));
      suggestionOverlay.innerHTML = buildItemSuggestionList(source, selectedId);
      suggestionOverlay.classList.add("is-open");
      positionSuggestionOverlay(queryEl);
    }

    function showPartnerSuggestionOverlay(queryEl, filteredPartners) {
      var source = Array.isArray(filteredPartners) ? filteredPartners : [];
      if (!queryEl || !source.length) {
        hidePartnerSuggestionOverlay();
        return;
      }

      partnerSuggestionOverlay.innerHTML = buildPartnerSuggestionList(source, selectedPartnerId);
      partnerSuggestionOverlay.classList.add("is-open");
      positionSuggestionOverlay(queryEl, partnerSuggestionOverlay);
    }

    function syncSuggestionOverlay() {
      if (activeSuggestIndex < 0 || !refs.linesWrap || !linesState[activeSuggestIndex]) {
        hideSuggestionOverlay();
        return;
      }

      var queryEl = refs.linesWrap.querySelector('.line-item-query[data-index="' + activeSuggestIndex + '"]');
      if (!queryEl || document.activeElement !== queryEl) {
        hideSuggestionOverlay();
        return;
      }

      var line = linesState[activeSuggestIndex];
      var normalizedQuery = normalizeText(line.query);
      var filtered = normalizedQuery ? filterItems(line.query) : [];
      if (!normalizedQuery || !filtered.length) {
        hideSuggestionOverlay();
        return;
      }

      showSuggestionOverlay(activeSuggestIndex, queryEl, filtered, line.item_id);
    }

    function syncPartnerSuggestionOverlay() {
      if (!refs.partnerInput || document.activeElement !== refs.partnerInput) {
        hidePartnerSuggestionOverlay();
        return;
      }

      var query = String(refs.partnerInput.value || "").trim();
      var filtered = query ? filterPartners(query) : [];
      if (!query || !filtered.length || findPartnerByQuery(query)) {
        hidePartnerSuggestionOverlay();
        return;
      }

      showPartnerSuggestionOverlay(refs.partnerInput, filtered);
    }

    function applySuggestedItem(index, selectedId) {
      if (!linesState[index]) {
        return;
      }

      linesState[index].item_id = selectedId;
      var selectedItem = getItemById(selectedId);
      if (selectedItem) {
        linesState[index].query = buildItemLabel(selectedItem);
      }

      var queryEl = refs.linesWrap.querySelector('.line-item-query[data-index="' + index + '"]');
      if (queryEl) {
        queryEl.value = linesState[index].query;
        queryEl.focus();
      }

      updateLineControls(index);
    }

    function close() {
      window.removeEventListener("resize", syncSuggestionOverlay);
      window.removeEventListener("resize", syncPartnerSuggestionOverlay);
      if (refs.card) {
        refs.card.removeEventListener("scroll", syncSuggestionOverlay);
        refs.card.removeEventListener("scroll", syncPartnerSuggestionOverlay);
      }
      hideSuggestionOverlay();
      hidePartnerSuggestionOverlay();
      if (suggestionOverlay && suggestionOverlay.parentNode) {
        suggestionOverlay.parentNode.removeChild(suggestionOverlay);
      }
      if (partnerSuggestionOverlay && partnerSuggestionOverlay.parentNode) {
        partnerSuggestionOverlay.parentNode.removeChild(partnerSuggestionOverlay);
      }
      if (modal && modal.parentNode) {
        modal.parentNode.removeChild(modal);
      }
    }

    function buildPartnerLabel(partner) {
      if (!partner) {
        return "";
      }
      return partner.code ? partner.code + " — " + (partner.name || "") : partner.name || "";
    }

    function findPartnerByQuery(query) {
      var normalized = normalizeText(query);
      if (!normalized) {
        return null;
      }

      var exact = null;
      var exactCount = 0;
      partners.forEach(function (partner) {
        var label = normalizeText(buildPartnerLabel(partner));
        var name = normalizeText(partner.name);
        var code = normalizeText(partner.code);
        if (label === normalized || name === normalized || code === normalized) {
          exact = partner;
          exactCount += 1;
        }
      });

      return exactCount === 1 ? exact : null;
    }

    function getPartnerById(partnerId) {
      var targetId = Number(partnerId);
      if (!targetId) {
        return null;
      }

      for (var i = 0; i < partners.length; i += 1) {
        if (Number(partners[i].id) === targetId) {
          return partners[i];
        }
      }
      return null;
    }

    function getPartnerMatchRank(partner, normalizedQuery) {
      var code = normalizeText(partner.code);
      var name = normalizeText(partner.name);
      var label = normalizeText(buildPartnerLabel(partner));

      if (code && code === normalizedQuery) {
        return 0;
      }
      if (name && name === normalizedQuery) {
        return 1;
      }
      if (code && code.indexOf(normalizedQuery) === 0) {
        return 2;
      }
      if (name && name.indexOf(normalizedQuery) === 0) {
        return 3;
      }
      if (label && label.indexOf(normalizedQuery) !== -1) {
        return 4;
      }
      return -1;
    }

    function filterPartners(query) {
      var normalized = normalizeText(query);
      if (!normalized) {
        return partners.slice(0, 50);
      }

      var ranked = [];
      partners.forEach(function (partner) {
        var rank = getPartnerMatchRank(partner, normalized);
        if (rank < 0) {
          return;
        }
        ranked.push({
          partner: partner,
          rank: rank,
          code: normalizeText(partner.code),
          name: normalizeText(partner.name),
        });
      });

      ranked.sort(function (left, right) {
        if (left.rank !== right.rank) {
          return left.rank - right.rank;
        }
        if (left.name !== right.name) {
          return left.name < right.name ? -1 : 1;
        }
        if (left.code !== right.code) {
          return left.code < right.code ? -1 : 1;
        }
        return (Number(left.partner.id) || 0) - (Number(right.partner.id) || 0);
      });

      return ranked.slice(0, 50).map(function (entry) {
        return entry.partner;
      });
    }

    function buildPartnerSuggestionList(filteredPartners, selectedId) {
      var source = Array.isArray(filteredPartners) ? filteredPartners.slice(0, 30) : [];
      return source
        .map(function (partner) {
          var selected = Number(selectedId) === Number(partner.id) ? " is-selected" : "";
          return (
            '<button class="pc-order-suggest-partner' +
            selected +
            '" type="button" data-partner-id="' +
            escapeHtml(String(partner.id)) +
            '">' +
            escapeHtml(buildPartnerLabel(partner)) +
            "</button>"
          );
        })
        .join("");
    }

    function updatePartnerHint() {
      if (!refs.partnerInput) {
        return;
      }
      var value = String(refs.partnerInput.value || "").trim();
      if (!value) {
        selectedPartnerId = 0;
        hidePartnerSuggestionOverlay();
        return;
      }
      var partner = findPartnerByQuery(value);
      if (partner) {
        selectedPartnerId = Number(partner.id) || 0;
        hidePartnerSuggestionOverlay();
        return;
      }

      var filtered = filterPartners(value);
      var shouldOpen = filtered.length > 0 && document.activeElement === refs.partnerInput;
      if (shouldOpen) {
        showPartnerSuggestionOverlay(refs.partnerInput, filtered);
      } else {
        hidePartnerSuggestionOverlay();
      }
    }

    function applyPartnerInputSelection() {
      if (!refs.partnerInput) {
        return;
      }

      var partner = findPartnerByQuery(refs.partnerInput.value);
      if (partner) {
        selectedPartnerId = Number(partner.id) || 0;
        refs.partnerInput.value = buildPartnerLabel(partner);
      }
      updatePartnerHint();
    }

    function applySuggestedPartner(selectedId) {
      var partner = getPartnerById(selectedId);
      if (!partner || !refs.partnerInput) {
        return;
      }

      selectedPartnerId = Number(partner.id) || 0;
      refs.partnerInput.value = buildPartnerLabel(partner);
      hidePartnerSuggestionOverlay();
      updatePartnerHint();
      refs.partnerInput.focus();
    }

    function buildItemLabel(item) {
      if (!item) {
        return "Без названия";
      }
      var label = item.name || "Без названия";
      if (item.gtin) {
        label += " · GTIN: " + item.gtin;
      }
      if (item.barcode) {
        label += " · SKU: " + item.barcode;
      }
      return label;
    }

    function getItemById(itemId) {
      var targetId = Number(itemId);
      if (!targetId) {
        return null;
      }
      for (var i = 0; i < items.length; i += 1) {
        if (Number(items[i].id) === targetId) {
          return items[i];
        }
      }
      return null;
    }

    function normalizeText(value) {
      return String(value || "").trim().toLowerCase();
    }

    function isDigitsOnly(value) {
      var normalized = String(value || "");
      if (!normalized) {
        return false;
      }
      for (var i = 0; i < normalized.length; i += 1) {
        var code = normalized.charCodeAt(i);
        if (code < 48 || code > 57) {
          return false;
        }
      }
      return true;
    }

    function endsWithToken(source, token) {
      if (!source || !token || token.length > source.length) {
        return false;
      }
      return source.lastIndexOf(token) === source.length - token.length;
    }

    function getItemMatchRank(item, normalizedQuery) {
      var name = normalizeText(item.name);
      var barcode = normalizeText(item.barcode);
      var gtin = normalizeText(item.gtin);
      var numericQuery = isDigitsOnly(normalizedQuery);

      if (numericQuery) {
        if (normalizedQuery.length >= 3) {
          if (endsWithToken(gtin, normalizedQuery)) {
            return 0;
          }
          if (endsWithToken(barcode, normalizedQuery)) {
            return 1;
          }
        }
        if (gtin.indexOf(normalizedQuery) !== -1) {
          return 2;
        }
        if (barcode.indexOf(normalizedQuery) !== -1) {
          return 3;
        }
        if (name.indexOf(normalizedQuery) !== -1) {
          return 4;
        }
        return -1;
      }

      if (name.indexOf(normalizedQuery) === 0) {
        return 0;
      }
      if (name.indexOf(normalizedQuery) !== -1) {
        return 1;
      }
      if (gtin.indexOf(normalizedQuery) === 0 || barcode.indexOf(normalizedQuery) === 0) {
        return 2;
      }
      if (gtin.indexOf(normalizedQuery) !== -1 || barcode.indexOf(normalizedQuery) !== -1) {
        return 3;
      }
      return -1;
    }

    function filterItems(query) {
      var normalized = normalizeText(query);
      if (!normalized) {
        return items.slice(0, 200);
      }

      var ranked = [];
      items.forEach(function (item) {
        var rank = getItemMatchRank(item, normalized);
        if (rank < 0) {
          return;
        }
        ranked.push({
          item: item,
          rank: rank,
          name: normalizeText(item.name),
          gtin: normalizeText(item.gtin),
          barcode: normalizeText(item.barcode),
        });
      });

      ranked.sort(function (left, right) {
        if (left.rank !== right.rank) {
          return left.rank - right.rank;
        }
        if (left.gtin !== right.gtin) {
          return left.gtin < right.gtin ? -1 : 1;
        }
        if (left.barcode !== right.barcode) {
          return left.barcode < right.barcode ? -1 : 1;
        }
        if (left.name !== right.name) {
          return left.name < right.name ? -1 : 1;
        }
        return (Number(left.item.id) || 0) - (Number(right.item.id) || 0);
      });

      return ranked.slice(0, 200).map(function (entry) {
        return entry.item;
      });
    }

    function findExactItem(query) {
      var normalized = normalizeText(query);
      if (!normalized) {
        return null;
      }

      for (var i = 0; i < items.length; i += 1) {
        var item = items[i];
        if (normalizeText(item.gtin) === normalized) {
          return item;
        }
        if (normalizeText(item.barcode) === normalized) {
          return item;
        }
        if (normalizeText(item.name) === normalized) {
          return item;
        }
      }

      return null;
    }

    function buildItemSuggestionList(filteredItems, selectedId) {
      var source = Array.isArray(filteredItems) ? filteredItems.slice(0, 30) : [];
      return source
        .map(function (item) {
          var selected = Number(selectedId) === Number(item.id) ? " is-selected" : "";
          return (
            '<button class="pc-order-suggest-item' +
            selected +
            '" type="button" data-item-id="' +
            escapeHtml(String(item.id)) +
            '">' +
            escapeHtml(buildItemLabel(item)) +
            "</button>"
          );
        })
        .join("");
    }

    function updateLineControls(index) {
      if (!refs.linesWrap || !linesState[index]) {
        return;
      }

      var line = linesState[index];
      var queryEl = refs.linesWrap.querySelector('.line-item-query[data-index="' + index + '"]');
      var hintEl = refs.linesWrap.querySelector('.line-item-hint[data-index="' + index + '"]');
      if (!queryEl) {
        return;
      }

      var normalizedQuery = normalizeText(line.query);
      var filtered = normalizedQuery ? filterItems(line.query) : [];
      var shouldOpen =
        normalizedQuery.length > 0 &&
        filtered.length > 0 &&
        document.activeElement === queryEl;
      if (shouldOpen) {
        showSuggestionOverlay(index, queryEl, filtered, line.item_id);
      } else if (activeSuggestIndex === index) {
        hideSuggestionOverlay();
      }

      var selectedItem = getItemById(line.item_id);
      if (hintEl) {
        if (selectedItem) {
          hintEl.textContent = "";
        } else if (normalizedQuery && filtered.length === 0) {
          hintEl.textContent = "Совпадения не найдены.";
        } else if (normalizedQuery) {
          hintEl.textContent = "Выберите товар из выпадающего списка.";
        } else {
          hintEl.textContent = "";
        }
      }
    }

    function renderLines() {
      if (!refs.linesWrap) {
        return;
      }
      if (!linesState.length) {
        linesState.push({ item_id: 0, qty_ordered: 1, query: "" });
      }

      hideSuggestionOverlay();
      refs.linesWrap.innerHTML = linesState
        .map(function (line, index) {
          var query = String(line.query || "");
          return (
            '<div class="pc-order-line-row">' +
            '<div class="pc-order-line-item-cell">' +
            '<div class="pc-order-line-autocomplete">' +
            '<input class="form-input line-item-query" data-index="' +
            index +
            '" type="text" autocomplete="off" placeholder="Введите GTIN/SKU или название" value="' +
            escapeHtml(query) +
            '" />' +
            "</div>" +
            '<div class="pc-order-line-hint line-item-hint" data-index="' +
            index +
            '"></div>' +
            "</div>" +
            '<input class="form-input line-qty" data-index="' +
            index +
            '" type="number" min="0.001" step="0.001" value="' +
            escapeHtml(String(line.qty_ordered || "")) +
            '" />' +
            '<button class="btn btn-ghost line-remove-btn" type="button" data-index="' +
            index +
            '">Удалить</button>' +
            "</div>"
          );
        })
        .join("");

      linesState.forEach(function (_line, index) {
        updateLineControls(index);
      });

      var queryInputs = refs.linesWrap.querySelectorAll(".line-item-query");
      queryInputs.forEach(function (inputEl) {
        inputEl.addEventListener("input", function () {
          var index = Number(inputEl.getAttribute("data-index"));
          if (!linesState[index]) {
            return;
          }

          linesState[index].query = String(inputEl.value || "");
          var exactItem = findExactItem(linesState[index].query);
          if (exactItem) {
            linesState[index].item_id = Number(exactItem.id) || 0;
          } else {
            linesState[index].item_id = 0;
          }
          updateLineControls(index);
        });

        inputEl.addEventListener("focus", function () {
          var index = Number(inputEl.getAttribute("data-index"));
          updateLineControls(index);
        });

        inputEl.addEventListener("blur", function () {
          var index = Number(inputEl.getAttribute("data-index"));
          window.setTimeout(function () {
            updateLineControls(index);
          }, 120);
        });
      });

      var qtyInputs = refs.linesWrap.querySelectorAll(".line-qty");
      qtyInputs.forEach(function (inputEl) {
        inputEl.addEventListener("input", function () {
          var index = Number(inputEl.getAttribute("data-index"));
          if (!linesState[index]) {
            return;
          }
          linesState[index].qty_ordered = Number(inputEl.value) || 0;
        });
      });

      var removeButtons = refs.linesWrap.querySelectorAll(".line-remove-btn");
      removeButtons.forEach(function (btn) {
        btn.addEventListener("click", function () {
          var index = Number(btn.getAttribute("data-index"));
          linesState.splice(index, 1);
          renderLines();
        });
      });
    }

    function submit() {
      var orderRef = refs.orderRefInput && refs.orderRefInput.value ? refs.orderRefInput.value.trim() : "";
      var selectedPartner = selectedPartnerId ? getPartnerById(selectedPartnerId) : null;
      if (!selectedPartner && refs.partnerInput) {
        selectedPartner = findPartnerByQuery(refs.partnerInput.value);
      }
      var partnerId = selectedPartner ? Number(selectedPartner.id) : 0;
      var dueDate = refs.dueDateInput ? String(refs.dueDateInput.value || "").trim() : "";
      var comment = refs.commentInput ? String(refs.commentInput.value || "").trim() : "";
      var account = loadAccount();
      var lines = [];
      var unresolvedLines = [];

      linesState.forEach(function (line, index) {
        var qty = Number(line.qty_ordered) || 0;
        if (qty <= 0) {
          return;
        }

        var itemId = Number(line.item_id) || 0;
        if (!itemId) {
          unresolvedLines.push(index + 1);
          return;
        }

        lines.push({
          item_id: itemId,
          qty_ordered: qty,
        });
      });

      if (!orderRef) {
        setStatus("Укажите номер заказа.");
        return;
      }
      if (!partnerId) {
        setStatus("Выберите контрагента из списка.");
        return;
      }
      if (!lines.length) {
        setStatus("Добавьте хотя бы одну строку заказа.");
        return;
      }
      if (unresolvedLines.length) {
        setStatus("Строки " + unresolvedLines.join(", ") + ": выберите товар (доступен поиск по GTIN/названию).");
        return;
      }
      if (!hasPcAccess(account)) {
        setStatus("Сессия неактивна. Войдите повторно.");
        return;
      }

      if (refs.submitBtn) {
        refs.submitBtn.disabled = true;
      }
      setStatus("Отправка заявки...");

      fetchJson("/api/orders/requests/create", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          order_ref: orderRef,
          partner_id: partnerId,
          due_date: dueDate || null,
          comment: comment || null,
          lines: lines,
          login: account.login || null,
          device_id: account.device_id || null,
        }),
      })
        .then(function (result) {
          var requestId = result && result.request_id ? String(result.request_id) : "-";
          setStatus("Заявка #" + requestId + " отправлена. Ожидается подтверждение в WPF.");
          if (typeof onSubmitted === "function") {
            onSubmitted();
          }
          window.setTimeout(close, 500);
        })
        .catch(function (error) {
          var message = error && error.message ? error.message : "REQUEST_FAILED";
          setStatus("Ошибка отправки: " + message);
        })
        .finally(function () {
          if (refs.submitBtn) {
            refs.submitBtn.disabled = false;
          }
        });
    }

    if (refs.closeBtn) {
      refs.closeBtn.addEventListener("click", close);
    }
    if (refs.addLineBtn) {
      refs.addLineBtn.addEventListener("click", function () {
        linesState.push({ item_id: 0, qty_ordered: 1, query: "" });
        renderLines();
      });
    }
    if (refs.partnerInput) {
      refs.partnerInput.addEventListener("input", updatePartnerHint);
      refs.partnerInput.addEventListener("change", applyPartnerInputSelection);
      refs.partnerInput.addEventListener("blur", applyPartnerInputSelection);
      refs.partnerInput.addEventListener("focus", updatePartnerHint);
    }
    if (refs.submitBtn) {
      refs.submitBtn.addEventListener("click", submit);
    }
    suggestionOverlay.addEventListener("mousedown", function (event) {
      var target = event.target;
      while (
        target &&
        target !== suggestionOverlay &&
        !(target.classList && target.classList.contains("pc-order-suggest-item"))
      ) {
        target = target.parentNode;
      }
      if (!target || target === suggestionOverlay) {
        return;
      }

      event.preventDefault();
      var index = activeSuggestIndex;
      var selectedId = Number(target.getAttribute("data-item-id")) || 0;
      if (!selectedId || index < 0) {
        return;
      }

      applySuggestedItem(index, selectedId);
    });
    partnerSuggestionOverlay.addEventListener("mousedown", function (event) {
      var target = event.target;
      while (
        target &&
        target !== partnerSuggestionOverlay &&
        !(target.classList && target.classList.contains("pc-order-suggest-partner"))
      ) {
        target = target.parentNode;
      }
      if (!target || target === partnerSuggestionOverlay) {
        return;
      }

      event.preventDefault();
      var selectedId = Number(target.getAttribute("data-partner-id")) || 0;
      if (!selectedId) {
        return;
      }

      applySuggestedPartner(selectedId);
    });
    window.addEventListener("resize", syncSuggestionOverlay);
    window.addEventListener("resize", syncPartnerSuggestionOverlay);
    if (refs.card) {
      refs.card.addEventListener("scroll", syncSuggestionOverlay);
      refs.card.addEventListener("scroll", syncPartnerSuggestionOverlay);
    }

    if (refs.orderRefInput) {
      refs.orderRefInput.disabled = true;
    }

    setStatus("Загрузка справочников...");
    Promise.all([loadOrderReferenceData(), loadNextOrderRef()])
      .then(function (payload) {
        var refsData = payload[0];
        var nextOrderRef = payload[1];

        partners = refsData.partners;
        items = refsData.items.sort(function (a, b) {
          var left = String(a.name || "").toLowerCase();
          var right = String(b.name || "").toLowerCase();
          return left < right ? -1 : left > right ? 1 : 0;
        });

        updatePartnerHint();
        linesState = [{ item_id: 0, qty_ordered: 1, query: "" }];
        if (refs.orderRefInput) {
          refs.orderRefInput.value = nextOrderRef || "";
          refs.orderRefInput.disabled = false;
        }
        renderLines();
        setStatus("");
      })
      .catch(function () {
        if (refs.orderRefInput) {
          refs.orderRefInput.disabled = false;
        }
        setStatus("Ошибка загрузки справочников.");
      });
  }

  function openOrderModal(order, onSubmitted) {
    var isPending = order && order.is_pending_confirmation;
    var currentStatusCode = toOrderStatusCode(order.status);
    var canChangeStatus = !isPending && (currentStatusCode === "ACCEPTED" || currentStatusCode === "IN_PROGRESS");
    var isInternal = isInternalOrder(order);
    var showAvailableColumn = !isShippedOrder(order);
    var modal = document.createElement("div");
    modal.className = "pc-modal";
    modal.innerHTML =
      '<div class="pc-modal-card pc-order-modal-card">' +
      '  <div class="pc-modal-header">' +
      '    <div class="pc-modal-title">Заказ ' +
      escapeHtml(order.order_ref || "-") +
      ' <span id="orderReadinessBadge"></span></div>' +
      '    <button class="btn btn-outline" type="button" id="modalCloseBtn">Закрыть</button>' +
      "  </div>" +
      '  <div class="pc-status">Тип: ' +
      escapeHtml(getOrderTypeLabel(order.order_type)) +
      " · Контрагент: " +
      escapeHtml(order.partner_name || "-") +
      "</div>" +
      '  <div class="pc-status">План: ' +
      escapeHtml(formatDate(order.due_date)) +
      " · Факт: " +
      escapeHtml(formatDate(order.shipped_at)) +
      "</div>" +
      '  <div class="pc-order-status-box">' +
      (canChangeStatus
        ? '    <div class="pc-order-status-row">' +
          '      <label class="form-label" for="orderStatusSelect">Новый статус</label>' +
          '      <select class="form-input" id="orderStatusSelect">' +
          '        <option value="ACCEPTED"' +
          (currentStatusCode === "ACCEPTED" ? ' selected="selected"' : "") +
          ">Принят</option>" +
          '        <option value="IN_PROGRESS"' +
          (currentStatusCode === "IN_PROGRESS" ? ' selected="selected"' : "") +
          ">В процессе</option>" +
          "      </select>" +
          '      <button class="btn" type="button" id="orderStatusRequestBtn">Отправить заявку</button>' +
          "    </div>"
        : '    <div class="pc-status">' +
          (isPending ? "Заказ ожидает подтверждения в WPF." : "Статус этого заказа нельзя менять из веб-интерфейса.") +
          "</div>") +
      '    <div id="orderRequestStatus" class="status"></div>' +
      "  </div>" +
      '  <div id="orderLinesWrap" class="pc-status" style="margin-top:12px;">Загрузка строк...</div>' +
      "</div>";
    document.body.appendChild(modal);

    function close() {
      document.body.removeChild(modal);
    }

    var closeBtn = modal.querySelector("#modalCloseBtn");
    if (closeBtn) {
      closeBtn.addEventListener("click", close);
    }

    var statusSelect = modal.querySelector("#orderStatusSelect");
    var statusBtn = modal.querySelector("#orderStatusRequestBtn");
    var requestStatusEl = modal.querySelector("#orderRequestStatus");

    function setRequestStatus(text) {
      if (requestStatusEl) {
        requestStatusEl.textContent = text || "";
      }
    }

    if (statusBtn) {
      statusBtn.addEventListener("click", function () {
        var nextStatus = statusSelect ? String(statusSelect.value || "").trim() : "";
        var account = loadAccount();

        if (!nextStatus) {
          setRequestStatus("Выберите статус.");
          return;
        }
        if (nextStatus === currentStatusCode) {
          setRequestStatus("Выбран текущий статус.");
          return;
        }
        if (!hasPcAccess(account)) {
          setRequestStatus("Сессия неактивна. Войдите повторно.");
          return;
        }

        statusBtn.disabled = true;
        setRequestStatus("Отправка заявки...");
        fetchJson("/api/orders/requests/status", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            order_id: Number(order.id),
            status: nextStatus,
            login: account.login || null,
            device_id: account.device_id || null,
          }),
        })
          .then(function (result) {
            var requestId = result && result.request_id ? String(result.request_id) : "-";
            setRequestStatus("Заявка #" + requestId + " отправлена. Ожидается подтверждение в WPF.");
            if (typeof onSubmitted === "function") {
              onSubmitted();
            }
          })
          .catch(function (error) {
            var message = error && error.message ? error.message : "REQUEST_FAILED";
            setRequestStatus("Ошибка отправки: " + message);
          })
          .finally(function () {
            statusBtn.disabled = false;
          });
      });
    }

    var linesPromise = isPending
      ? Promise.resolve(Array.isArray(order.lines) ? order.lines : [])
      : fetchJson("/api/orders/" + encodeURIComponent(order.id) + "/lines");

    linesPromise
      .then(function (lines) {
        var wrap = modal.querySelector("#orderLinesWrap");
        if (!wrap) {
          return;
        }
        if (!lines || !lines.length) {
          wrap.innerHTML = "<div>Строк нет.</div>";
          return;
        }
        var readiness = !isInternal && showAvailableColumn ? getShipmentReadiness(lines) : null;
        var readinessBadge = modal.querySelector("#orderReadinessBadge");
        if (readinessBadge) {
          readinessBadge.outerHTML = renderReadinessBadge(readiness);
        }
        var processedHeader = isInternal ? "Выпущено" : "Отгружено";
        var body = lines
          .map(function (line) {
            var processedQty = isInternal ? line.qty_produced || 0 : line.qty_shipped || 0;
            var availabilityState = getAvailabilityState(line);
            var shortageTitle = availabilityState.ready
              ? ""
              : ' title="Не хватает: ' + escapeHtml(formatQuantity(availabilityState.shortage)) + '"';
            var availabilityClass = availabilityState.ready ? "pc-availability-ready" : "pc-availability-short";
            return (
              "<tr>" +
              "<td>" +
              escapeHtml(line.item_name || "-") +
              "</td>" +
              "<td>" +
              escapeHtml(line.barcode || "-") +
              "</td>" +
              "<td>" +
              escapeHtml(line.gtin || "-") +
              "</td>" +
              "<td>" +
              escapeHtml(String(line.qty_ordered || 0)) +
              "</td>" +
              "<td>" +
              escapeHtml(String(processedQty)) +
              "</td>" +
              (showAvailableColumn
                ? "<td>" +
                  '<span class="pc-availability ' +
                  availabilityClass +
                  '"' +
                  shortageTitle +
                  ">" +
                  escapeHtml(formatQuantity(availabilityState.available)) +
                  "</span>" +
                  "</td>"
                : "") +
              "</tr>"
            );
          })
          .join("");
        wrap.innerHTML =
          '<table class="pc-table pc-order-lines-table">' +
          "<thead><tr>" +
          "<th>Товар</th>" +
          "<th>SKU / ШК</th>" +
          "<th>GTIN</th>" +
          "<th>Заказано</th>" +
          "<th>" +
          escapeHtml(processedHeader) +
          "</th>" +
          (showAvailableColumn ? "<th>В наличии</th>" : "") +
          "</tr></thead>" +
          "<tbody>" +
          body +
          "</tbody>" +
          "</table>";
      })
      .catch(function () {
        var wrap = modal.querySelector("#orderLinesWrap");
        if (wrap) {
          wrap.textContent = "Ошибка загрузки строк.";
        }
      });
  }

  function wireOrders() {
    var searchInput = document.getElementById("ordersSearchInput");
    var statusEl = document.getElementById("ordersStatus");
    var tableWrap = document.getElementById("ordersTableWrap");
    var newBtn = document.getElementById("ordersNewBtn");
    var refreshBtn = document.getElementById("ordersRefreshBtn");
    var debounce = null;

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text || "";
      }
    }

    function renderTable(rows) {
      if (!tableWrap) {
        return;
      }
      tableWrap.innerHTML = renderOrdersTable(rows);
      var items = tableWrap.querySelectorAll("[data-order]");
      items.forEach(function (item) {
        item.addEventListener("click", function () {
          var id = item.getAttribute("data-order");
          var target = rows.find(function (entry) {
            return String(entry.id) === String(id);
          });
          if (target) {
            openOrderModal(target, runSearch);
          }
        });
      });
    }

    function runSearch() {
      var query = searchInput ? searchInput.value.trim() : "";
      setStatus("Загрузка...");
      loadOrders(query)
        .then(function (rows) {
          var source = Array.isArray(rows) ? rows : [];
          renderTable(source);
          if (!source.length) {
            setStatus("Заказов нет");
            return source;
          }
          setStatus("Загрузка готовности...");
          return enrichOrdersWithReadiness(source).then(function (enrichedRows) {
            renderTable(enrichedRows);
            setStatus("Данные с сервера");
            return enrichedRows;
          });
        })
        .catch(function () {
          renderTable([]);
          setStatus("Ошибка загрузки заказов");
        });
    }

    function scheduleSearch() {
      if (debounce) {
        clearTimeout(debounce);
      }
      debounce = window.setTimeout(runSearch, 200);
    }

    if (searchInput) {
      searchInput.addEventListener("input", scheduleSearch);
    }
    if (newBtn) {
      newBtn.addEventListener("click", function () {
        openNewOrderModal(runSearch);
      });
    }
    if (refreshBtn) {
      refreshBtn.addEventListener("click", runSearch);
    }

    runSearch();
  }

  function renderView(view) {
    if (!app) {
      return;
    }

    syncTabsVisibility();
    var allowedView = resolveAllowedView(view);
    if (!allowedView) {
      currentView = "stock";
      setActiveTab("");
      app.innerHTML = renderNoAccess();
      return;
    }

    currentView = allowedView;
    setActiveTab(allowedView);

    if (allowedView === "catalog") {
      app.innerHTML = renderCatalog();
      wireCatalog();
      return;
    }

    if (allowedView === "orders") {
      app.innerHTML = renderOrders();
      wireOrders();
      return;
    }

    app.innerHTML = renderStock();
    wireStock();
  }

  function setActiveTab(view) {
    tabs.forEach(function (tab) {
      var match = tab.getAttribute("data-view") === view;
      tab.classList.toggle("is-active", match);
    });
  }

  function init() {
    var account = loadAccount();
    if (!hasPcAccess(account)) {
      applyClientBlocks(null);
      syncTabsVisibility();
      setLoginState(false);
      setAccountLabel(null);
      if (app) {
        app.innerHTML = renderLogin();
        wireLogin();
      }
      return;
    }

    setLoginState(true);
    setAccountLabel(account);
    syncTabsVisibility();
    if (app) {
      app.innerHTML = '<section class="pc-card"><div class="pc-status">Загрузка...</div></section>';
    }
    loadClientBlocks().then(function () {
      currentView = resolveAllowedView(currentView) || "stock";
      renderView(currentView);
    });
  }

  tabs.forEach(function (tab) {
    tab.addEventListener("click", function () {
      var view = tab.getAttribute("data-view") || "stock";
      if (resolveAllowedView(view) !== view) {
        return;
      }
      currentView = view;
      renderView(view);
    });
  });

  if (logoutBtn) {
    logoutBtn.addEventListener("click", function () {
      clearAccount();
      applyClientBlocks(null);
      syncTabsVisibility();
      setAccountLabel(null);
      setLoginState(false);
      if (app) {
        app.innerHTML = renderLogin();
        wireLogin();
      }
    });
  }

  window.addEventListener("focus", function () {
    if (!hasPcAccess(loadAccount())) {
      return;
    }
    refreshClientBlocksIfChanged();
  });

  document.addEventListener("visibilitychange", function () {
    if (document.hidden || !hasPcAccess(loadAccount())) {
      return;
    }
    refreshClientBlocksIfChanged();
  });

  init();
})();
