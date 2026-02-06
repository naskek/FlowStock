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

  function normalizePlatform(value) {
    var normalized = String(value || "").trim().toUpperCase();
    return normalized === "PC" ? "PC" : "TSD";
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

  function escapeHtml(value) {
    return String(value || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
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
          if (platform !== "PC") {
            throw new Error("WRONG_PLATFORM");
          }
          var account = { device_id: deviceId, login: login, platform: platform };
          saveAccount(account);
          setAccountLabel(account);
          setLoginState(true);
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
            message = "Этот аккаунт предназначен для ТСД.";
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
      '      <input class="form-input" id="stockSearchInput" type="text" autocomplete="off" placeholder="Название, SKU, GTIN, штрихкод" />' +
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

  function renderStockTable(rows, useHu) {
    if (!rows || !rows.length) {
      return '<div class="empty-state">Нет данных по остаткам.</div>';
    }
    var huColumn = useHu ? "<th>HU</th>" : "";
    var body = rows
      .map(function (row) {
        var qtyLabel = row.qtyDisplay || row.qty;
        return (
          "<tr>" +
          "<td>" +
          escapeHtml(row.itemName || "-") +
          "</td>" +
          "<td>" +
          escapeHtml(row.barcode || "-") +
          "</td>" +
          "<td>" +
          escapeHtml(row.gtin || "-") +
          "</td>" +
          "<td>" +
          escapeHtml(row.locationCode || "-") +
          "</td>" +
          (useHu ? "<td>" + escapeHtml(row.hu || "-") + "</td>" : "") +
          "<td>" +
          escapeHtml(String(qtyLabel)) +
          "</td>" +
          "</tr>"
        );
      })
      .join("");
    return (
      '<table class="pc-table">' +
      "<thead><tr>" +
      "<th>Товар</th>" +
      "<th>SKU / ШК</th>" +
      "<th>GTIN</th>" +
      "<th>Место</th>" +
      huColumn +
      "<th>Кол-во</th>" +
      "</tr></thead>" +
      "<tbody>" +
      body +
      "</tbody>" +
      "</table>"
    );
  }

  function loadStockData() {
    return Promise.all([
      fetchJson("/api/items"),
      fetchJson("/api/locations"),
      fetchJson("/api/stock"),
      fetchJson("/api/hu-stock"),
    ]).then(function (payloads) {
      cachedItems = Array.isArray(payloads[0]) ? payloads[0] : [];
      cachedLocations = Array.isArray(payloads[1]) ? payloads[1] : [];
      var stockRows = Array.isArray(payloads[2]) ? payloads[2] : [];
      var huRows = Array.isArray(payloads[3]) ? payloads[3] : [];

      cachedItemsById = {};
      cachedItems.forEach(function (item) {
        cachedItemsById[Number(item.id)] = {
          itemId: Number(item.id),
          name: item.name || "",
          barcode: item.barcode || "",
          gtin: item.gtin || "",
          base_uom: item.base_uom_code || item.base_uom || "",
        };
      });

      cachedLocationsById = {};
      cachedLocations.forEach(function (loc) {
        cachedLocationsById[Number(loc.id)] = {
          locationId: Number(loc.id),
          code: loc.code || "",
          name: loc.name || "",
        };
      });

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
          locationCode: loc.code || "",
          hu: row.hu || "",
        };
      });
    });
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
      var query = searchInput ? searchInput.value.trim().toLowerCase() : "";
      var locationId = locationSelect ? Number(locationSelect.value) : 0;
      var hu = huSelect ? String(huSelect.value || "").trim() : "";
      var source = hu ? cachedHuRows : cachedStockRows;

      var rows = source.filter(function (row) {
        if (locationId && Number(row.locationId) !== locationId) {
          return false;
        }
        if (hu && row.hu !== hu) {
          return false;
        }
        if (!query) {
          return true;
        }
        return (
          (row.itemName && row.itemName.toLowerCase().indexOf(query) !== -1) ||
          (row.barcode && row.barcode.toLowerCase().indexOf(query) !== -1) ||
          (row.gtin && row.gtin.toLowerCase().indexOf(query) !== -1) ||
          (row.locationCode && row.locationCode.toLowerCase().indexOf(query) !== -1)
        );
      });

      setStatus("Строк: " + rows.length);
      tableWrap.innerHTML = renderStockTable(rows, !!hu);
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

      if (huSelect) {
        var hus = cachedHuRows
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
      }
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
    if (huSelect) {
      huSelect.addEventListener("change", renderRows);
    }
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
      '    <div id="ordersStatus" class="pc-status"></div>' +
      "  </div>" +
      '  <div id="ordersTableWrap"></div>' +
      "</section>"
    );
  }

  function renderOrdersTable(rows) {
    if (!rows || !rows.length) {
      return '<div class="empty-state">Заказов нет.</div>';
    }
    var body = rows
      .map(function (order) {
        return (
          '<tr data-order="' +
          escapeHtml(String(order.id)) +
          '">' +
          "<td>" +
          escapeHtml(order.order_ref || "-") +
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
          escapeHtml(order.status || "-") +
          "</td>" +
          "</tr>"
        );
      })
      .join("");
    return (
      '<table class="pc-table">' +
      "<thead><tr>" +
      "<th>Номер</th>" +
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
    var url = "/api/orders";
    if (q) {
      url += "?q=" + encodeURIComponent(q);
    }
    return fetchJson(url);
  }

  function openOrderModal(order) {
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
      '  <div class="pc-status">Контрагент: ' +
      escapeHtml(order.partner_name || "-") +
      "</div>" +
      '  <div class="pc-status">План: ' +
      escapeHtml(formatDate(order.due_date)) +
      " · Факт: " +
      escapeHtml(formatDate(order.shipped_at)) +
      "</div>" +
      '  <div id="orderLinesWrap" class="pc-status" style="margin-top:12px;">Загрузка строк...</div>' +
      "</div>";
    document.body.appendChild(modal);

    function close() {
      document.body.removeChild(modal);
    }

    modal.addEventListener("click", function (event) {
      if (event.target === modal) {
        close();
      }
    });
    var closeBtn = modal.querySelector("#modalCloseBtn");
    if (closeBtn) {
      closeBtn.addEventListener("click", close);
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
        var body = lines
          .map(function (line) {
            return (
              "<tr>" +
              "<td>" +
              escapeHtml(line.item_name || "-") +
              "</td>" +
              "<td>" +
              escapeHtml(line.barcode || "-") +
              "</td>" +
              "<td>" +
              escapeHtml(String(line.qty_ordered || 0)) +
              "</td>" +
              "<td>" +
              escapeHtml(String(line.qty_shipped || 0)) +
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
          "<th>ШК</th>" +
          "<th>Заказано</th>" +
          "<th>Отгружено</th>" +
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
            openOrderModal(target);
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

    runSearch();
  }

  function renderView(view) {
    if (!app) {
      return;
    }

    if (view === "orders") {
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
    if (!account || account.platform !== "PC") {
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
    renderView(currentView);
  }

  tabs.forEach(function (tab) {
    tab.addEventListener("click", function () {
      var view = tab.getAttribute("data-view") || "stock";
      currentView = view;
      setActiveTab(view);
      renderView(view);
    });
  });

  if (logoutBtn) {
    logoutBtn.addEventListener("click", function () {
      clearAccount();
      setAccountLabel(null);
      setLoginState(false);
      if (app) {
        app.innerHTML = renderLogin();
        wireLogin();
      }
    });
  }

  init();
})();
