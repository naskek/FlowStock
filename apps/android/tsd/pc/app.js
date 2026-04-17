(function () {
  "use strict";

  var app = document.getElementById("app");
  var tabs = document.querySelectorAll(".pc-tab");
  var logoutBtn = document.getElementById("logoutBtn");
  var accountLabel = document.getElementById("accountLabel");

  var currentView = "stock";
  var cachedItems = [];
  var cachedItemsById = {};
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
      cachedItemsById[Number(item.id)] = {
        itemId: Number(item.id),
        name: item.name || "",
        barcode: item.barcode || "",
        gtin: item.gtin || "",
        brand: item.brand || "",
        volume: item.volume || "",
        base_uom: item.base_uom_code || item.base_uom || "",
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
      '  <div class="section-title">Остатки</div>' +
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
      '      <label class="form-label" for="stockHuFilter">HU</label>' +
      '      <select class="form-input" id="stockHuFilter"></select>' +
      "    </div>" +
      '    <div id="stockStatus" class="pc-status"></div>' +
      "  </div>" +
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
    return fetchJson("/api/items").then(function (items) {
      setCachedItems(items);
      return cachedItems;
    });
  }

  function loadStockData() {
    return Promise.all([
      fetchJson("/api/items"),
      fetchJson("/api/locations"),
      fetchJson("/api/stock"),
      fetchJson("/api/hu-stock"),
    ]).then(function (payloads) {
      setCachedItems(payloads[0]);
      setCachedLocations(payloads[1]);
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
        locationCode: row.locationCode,
        hu: "",
      });
    });

    cachedCombinedRows = combined;
  }

  function wireStock() {
    var searchInput = document.getElementById("stockSearchInput");
    var locationSelect = document.getElementById("stockLocationFilter");
    var huSelect = document.getElementById("stockHuFilter");
    var statusEl = document.getElementById("stockStatus");
    var tableWrap = document.getElementById("stockTableWrap");
    var debounce = null;

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text || "";
      }
    }

    function renderRows() {
      if (!tableWrap) {
        return;
      }
      var query = normalizeSearchQuery(searchInput ? searchInput.value : "");
      var locationId = locationSelect ? Number(locationSelect.value) : 0;
      var hu = huSelect ? String(huSelect.value || "").trim() : "";
      var source = cachedCombinedRows.length ? cachedCombinedRows : cachedStockRows;

      var rows = source.filter(function (row) {
        if (locationId && Number(row.locationId) !== locationId) {
          return false;
        }
        if (hu && row.hu !== hu) {
          return false;
        }
        return matchesItemSearch(row, query, true);
      });

      setStatus("Строк: " + rows.length);
      tableWrap.innerHTML = renderStockTable(rows);
    }

    function updateHuOptions() {
      if (!huSelect) {
        return;
      }

      var locationId = locationSelect ? Number(locationSelect.value) : 0;
      var previous = String(huSelect.value || "");
      var hus = cachedHuRows
        .filter(function (row) {
          return !locationId || Number(row.locationId) === locationId;
        })
        .map(function (row) {
          return row.hu;
        })
        .filter(function (value) {
          return !!value;
        })
        .filter(function (value, index, arr) {
          return arr.indexOf(value) === index;
        })
        .sort();
      var huOptions =
        '<option value="">Все HU</option>' +
        hus
          .map(function (code) {
            return '<option value="' + escapeHtml(code) + '">' + escapeHtml(code) + "</option>";
          })
          .join("");
      huSelect.innerHTML = huOptions;

      if (previous && hus.indexOf(previous) !== -1) {
        huSelect.value = previous;
        return;
      }
      huSelect.value = "";
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

      updateHuOptions();
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
      locationSelect.addEventListener("change", function () {
        updateHuOptions();
        renderRows();
      });
    }
    if (huSelect) {
      huSelect.addEventListener("change", renderRows);
    }
  }

  function wireCatalog() {
    var searchInput = document.getElementById("catalogSearchInput");
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
      var rows = buildRows().filter(function (row) {
        return matchesItemSearch(row, query, false);
      });

      setStatus("Товаров: " + rows.length);
      tableWrap.innerHTML = renderCatalogTable(rows);
    }

    function loadAndRender() {
      setStatus("Загрузка...");
      loadCatalogData()
        .then(function () {
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
          (isPending ? "" : ' data-order="' + escapeHtml(String(order.id)) + '"') +
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
          escapeHtml(order.status || (isPending ? "Ожидает подтверждения" : "-")) +
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
    var url = "/api/orders?include_internal=1";
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
      '      <input class="form-input" id="newOrderPartnerInput" type="text" list="newOrderPartnerList" autocomplete="off" placeholder="Введите имя или код" />' +
      '      <datalist id="newOrderPartnerList"></datalist>' +
      '      <div class="pc-order-line-hint" id="newOrderPartnerHint"></div>' +
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
      partnerDatalist: modal.querySelector("#newOrderPartnerList"),
      partnerHint: modal.querySelector("#newOrderPartnerHint"),
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
    var activeSuggestIndex = -1;
    var suggestionOverlay = document.createElement("div");
    suggestionOverlay.className = "pc-order-suggest pc-order-suggest-floating";
    document.body.appendChild(suggestionOverlay);

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

    function positionSuggestionOverlay(queryEl) {
      if (!queryEl) {
        return;
      }

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

      suggestionOverlay.style.left = left + "px";
      suggestionOverlay.style.top = top + "px";
      suggestionOverlay.style.width = width + "px";
      suggestionOverlay.style.maxHeight = maxHeight + "px";
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
      if (refs.card) {
        refs.card.removeEventListener("scroll", syncSuggestionOverlay);
      }
      hideSuggestionOverlay();
      if (suggestionOverlay && suggestionOverlay.parentNode) {
        suggestionOverlay.parentNode.removeChild(suggestionOverlay);
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

    function buildPartnerOptions() {
      if (!refs.partnerDatalist) {
        return;
      }
      refs.partnerDatalist.innerHTML = partners
        .map(function (partner) {
          return '<option value="' + escapeHtml(buildPartnerLabel(partner)) + '"></option>';
        })
        .join("");
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

    function updatePartnerHint() {
      if (!refs.partnerInput || !refs.partnerHint) {
        return;
      }
      var value = String(refs.partnerInput.value || "").trim();
      if (!value) {
        refs.partnerHint.textContent = "";
        return;
      }
      var partner = findPartnerByQuery(value);
      refs.partnerHint.textContent = partner ? "" : "Выберите контрагента из выпадающего списка.";
    }

    function applyPartnerInputSelection() {
      if (!refs.partnerInput) {
        return;
      }

      var partner = findPartnerByQuery(refs.partnerInput.value);
      if (partner) {
        refs.partnerInput.value = buildPartnerLabel(partner);
      }
      updatePartnerHint();
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
      var selectedPartner = refs.partnerInput ? findPartnerByQuery(refs.partnerInput.value) : null;
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
    window.addEventListener("resize", syncSuggestionOverlay);
    if (refs.card) {
      refs.card.addEventListener("scroll", syncSuggestionOverlay);
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

        buildPartnerOptions();
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
    var currentStatusCode = toOrderStatusCode(order.status);
    var canChangeStatus = currentStatusCode === "ACCEPTED" || currentStatusCode === "IN_PROGRESS";
    var isInternalOrder = String(order.order_type || "").trim().toUpperCase() === "INTERNAL";
    var modal = document.createElement("div");
    modal.className = "pc-modal";
    modal.innerHTML =
      '<div class="pc-modal-card">' +
      '  <div class="pc-modal-header">' +
      '    <div class="pc-modal-title">Заказ ' +
      escapeHtml(order.order_ref || "-") +
      "</div>" +
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
        : '    <div class="pc-status">Статус этого заказа нельзя менять из веб-интерфейса.</div>') +
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

    fetchJson("/api/orders/" + encodeURIComponent(order.id) + "/lines")
      .then(function (lines) {
        var wrap = modal.querySelector("#orderLinesWrap");
        if (!wrap) {
          return;
        }
        if (!lines || !lines.length) {
          wrap.innerHTML = "<div>Строк нет.</div>";
          return;
        }
        var processedHeader = isInternalOrder ? "Выпущено" : "Отгружено";
        var body = lines
          .map(function (line) {
            var processedQty = isInternalOrder ? line.qty_produced || 0 : line.qty_shipped || 0;
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
              "<td>" +
              escapeHtml(String(line.qty_left || 0)) +
              "</td>" +
              "</tr>"
            );
          })
          .join("");
        wrap.innerHTML =
          '<table class="pc-table">' +
          "<thead><tr>" +
          "<th>Товар</th>" +
          "<th>SKU / ШК</th>" +
          "<th>GTIN</th>" +
          "<th>Заказано</th>" +
          "<th>" +
          escapeHtml(processedHeader) +
          "</th>" +
          "<th>Осталось</th>" +
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
          renderTable(rows);
          setStatus(rows && rows.length ? "Данные с сервера" : "Заказов нет");
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
