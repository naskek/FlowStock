(function () {
  "use strict";

  var app = document.getElementById("app");
  var backBtn = document.getElementById("backBtn");
  var settingsBtn = document.getElementById("settingsBtn");
  var scanSink = document.getElementById("scanSink");
  var currentRoute = null;
  var scanKeydownHandler = null;

  var STATUS_ORDER = {
    DRAFT: 0,
    READY: 1,
    EXPORTED: 2,
  };

  var NAV_ORIGIN_KEY = "tsdNavOrigin";

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
    return !!document.querySelector(".overlay");
  }

  function enterScanMode() {
    var active = document.activeElement;
    if (active && active.blur) {
      active.blur();
    }
    if (scanSink) {
      scanSink.value = "";
      scanSink.focus();
    }
    setScanHighlight(true);
  }

  function setScanHandler(handler) {
    if (scanKeydownHandler) {
      if (scanKeydownHandler.cleanup) {
        scanKeydownHandler.cleanup();
      }
      document.removeEventListener("keydown", scanKeydownHandler, true);
      scanKeydownHandler = null;
    }
    if (handler) {
      scanKeydownHandler = handler;
      document.addEventListener("keydown", scanKeydownHandler, true);
    }
  }

  var STATUS_LABELS = {
    DRAFT: "Черновик",
    READY: "Наполнен",
    CLOSED: "Закрыт",
    EXPORTED: "Передан",
  };
  var preserveScanFocus = false;

  var OPS = {
    INBOUND: { label: "Приемка", prefix: "IN" },
    OUTBOUND: { label: "Отгрузка", prefix: "OUT" },
    MOVE: { label: "Перемещение", prefix: "MOV" },
    WRITE_OFF: { label: "Списание", prefix: "WO" },
    INVENTORY: { label: "Инвентаризация", prefix: "INV" },
  };

  var WRITE_OFF_REASONS = [
    { code: "DAMAGED", label: "Повреждено" },
    { code: "EXPIRED", label: "Просрочено" },
    { code: "DEFECT", label: "Брак" },
    { code: "SAMPLE", label: "Проба" },
    { code: "OTHER", label: "Прочее" },
  ];

  var REQUIRED_FIELDS = {
    INBOUND: ["to"],
    OUTBOUND: ["from"],
    MOVE: ["from", "to"],
    WRITE_OFF: ["from", "reason_code"],
    INVENTORY: ["location"],
  };

  function escapeHtml(value) {
    return String(value || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
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
    if (parts[0] === "doc" && parts[1]) {
      return { name: "doc", id: decodeURIComponent(parts[1]) };
    }
    return { name: parts[0] };
  }

  function navigate(route) {
    if (route) {
      window.location.hash = route;
    }
  }

  function updateHeader(route) {
    if (!backBtn) {
      return;
    }

    if (route.name === "home") {
      backBtn.classList.remove("is-hidden");
    } else {
      backBtn.classList.remove("is-hidden");
    }

    if (settingsBtn) {
      settingsBtn.classList.toggle("is-active", route.name === "settings");
    }
  }

  function renderRoute() {
    setScanHandler(null);
    if (!window.location.hash || window.location.hash === "#") {
      navigate("/home");
      return;
    }
    var route = getRoute();
    currentRoute = route;
    updateHeader(route);

    if (!app) {
      return;
    }

    if (route.name === "docs") {
      app.innerHTML = renderLoading();
      TsdStorage.listDocs()
        .then(function (docs) {
          app.innerHTML = renderDocsList(docs);
          wireDocsList();
        })
        .catch(function () {
          app.innerHTML = renderError("Ошибка загрузки документов");
        });
      return;
    }

    if (route.name === "new") {
      app.innerHTML = renderNewOp();
      wireNewOp();
      return;
    }

    if (route.name === "doc") {
      app.innerHTML = renderLoading();
      TsdStorage.getDoc(route.id)
        .then(function (doc) {
          if (!doc) {
            app.innerHTML = renderError("Документ не найден");
            return;
          }
          app.innerHTML = renderDoc(doc);
          wireDoc(doc);
        })
        .catch(function () {
          app.innerHTML = renderError("Ошибка загрузки документа");
        });
      return;
    }

    if (route.name === "settings") {
      app.innerHTML = renderSettings();
      wireSettings();
      return;
    }

    if (route.name === "stock") {
      app.innerHTML = renderStock();
      wireStock();
      return;
    }

    app.innerHTML = renderHome();
    wireHome();
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

  function renderHome() {
    return (
      '<section class="screen screen-center">' +
      '  <div class="menu-grid">' +
      '    <button class="btn menu-btn" data-op="INBOUND">Приемка</button>' +
      '    <button class="btn menu-btn" data-op="OUTBOUND">Отгрузка</button>' +
      '    <button class="btn menu-btn" data-op="MOVE">Перемещение</button>' +
    '    <button class="btn menu-btn" data-op="WRITE_OFF">Списание</button>' +
    '    <button class="btn menu-btn" data-op="INVENTORY">Инвентаризация</button>' +
    '    <button class="btn menu-btn" data-route="docs">История операций</button>' +
    '    <button class="btn menu-btn" data-route="stock">Остатки</button>' +
      "  </div>" +
      "</section>"
    );
  }
  function renderDocsList(docs) {
    var list = docs || [];
    list.sort(function (a, b) {
      var statusDiff = (STATUS_ORDER[a.status] || 0) - (STATUS_ORDER[b.status] || 0);
      if (statusDiff !== 0) {
        return statusDiff;
      }
      var dateA = a.updatedAt ? new Date(a.updatedAt).getTime() : 0;
      var dateB = b.updatedAt ? new Date(b.updatedAt).getTime() : 0;
      return dateB - dateA;
    });

    var rows = list
      .map(function (doc) {
        var opLabel = OPS[doc.op] ? OPS[doc.op].label : doc.op;
        var statusLabel = STATUS_LABELS[doc.status] || doc.status;
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

    return (
      '<section class="screen">' +
      '  <div class="screen-card doc-screen-card">' +
      '    <div class="section-title">История операций</div>' +
      '    <div class="actions-row">' +
      '      <button class="btn primary-btn" id="newDocBtn">Новая операция</button>' +
      '      <button class="btn btn-outline" id="exportBtn">Экспорт JSONL</button>' +
      "    </div>" +
      '    <div id="exportStatus" class="status"></div>' +
      '    <div class="doc-list">' +
      rows +
      "    </div>" +
      "  </div>" +
      "</section>"
    );
  }

  function renderNewOp() {
    return (
      '<section class="screen">' +
      '  <div class="screen-card">' +
      '    <div class="section-title">Новая операция</div>' +
      '    <div class="menu-grid">' +
      '      <button class="btn menu-btn" data-op="INBOUND">Приемка</button>' +
      '      <button class="btn menu-btn" data-op="OUTBOUND">Отгрузка</button>' +
      '      <button class="btn menu-btn" data-op="MOVE">Перемещение</button>' +
      '      <button class="btn menu-btn" data-op="WRITE_OFF">Списание</button>' +
      '      <button class="btn menu-btn" data-op="INVENTORY">Инвентаризация</button>' +
      "    </div>" +
      "  </div>" +
      "</section>"
    );
  }

  function renderStock() {
    return (
      '<section class="screen">' +
      '  <div class="screen-card">' +
      '    <div class="section-title">Остатки</div>' +
      '    <div id="stockStatus" class="stock-status">Дата актуальности: —</div>' +
      '    <label class="form-label" for="stockSearchInput">Поиск товара</label>' +
      '    <input class="form-input" id="stockSearchInput" type="text" autocomplete="off" placeholder="Сканируйте штрихкод или введите название/GTIN/SKU" />' +
      '    <div id="stockSuggestions" class="stock-suggestions"></div>' +
      '    <div id="stockMessage" class="stock-message"></div>' +
      '    <div id="stockDetails" class="stock-details"></div>' +
      '    <div class="actions-bar">' +
      '      <button class="btn btn-outline" id="stockClearBtn" type="button">Очистить</button>' +
      '      <button class="btn btn-outline" id="stockBackBtn" type="button">Назад</button>' +
      "    </div>" +
      "  </div>" +
      "</section>"
    );
  }

  function wireStock() {
    var statusLabel = document.getElementById("stockStatus");
    var searchInput = document.getElementById("stockSearchInput");
    var suggestions = document.getElementById("stockSuggestions");
    var messageEl = document.getElementById("stockMessage");
    var detailsEl = document.getElementById("stockDetails");
    var clearBtn = document.getElementById("stockClearBtn");
    var backBtn = document.getElementById("stockBackBtn");
    var dataReady = false;

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

    function clearSuggestions() {
      if (suggestions) {
        suggestions.innerHTML = "";
      }
    }

    function renderLocationRows(rows) {
      return rows
        .map(function (row) {
          var qtyText = String(row.qtyBase != null ? row.qtyBase : 0);
          return (
            '<div class="stock-location-row">' +
            '  <div class="stock-location-label">' +
            escapeHtml(formatLocationLabel(row.code, row.name)) +
            "</div>" +
            '  <div class="stock-location-qty">' +
            escapeHtml(qtyText) +
            " шт</div>" +
            "</div>"
          );
        })
        .join("");
    }

    function renderDetails(item, rows) {
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
      var locationsHtml = rows.length
        ? renderLocationRows(rows)
        : '<div class="stock-no-rows">Нет остатков по данным выгрузки.</div>';
      detailsEl.innerHTML =
        '<div class="stock-card">' +
        '<div class="stock-title">' +
        escapeHtml(item.name || "—") +
        "</div>" +
        (skuLine
          ? '<div class="stock-subtitle">' + escapeHtml(skuLine) + "</div>"
          : "") +
        '<div class="stock-total">Итого: ' +
        escapeHtml(total) +
        " шт</div>" +
        '<div class="stock-locations">' +
        locationsHtml +
        "</div>" +
        "</div>";
    }

    function showStockItem(item) {
      if (!item) {
        setStockMessage("Товар не найден в данных");
        return;
      }
      clearSuggestions();
      setStockMessage("");
      TsdStorage.getStockByItemId(item.itemId)
        .then(function (rows) {
          renderDetails(item, rows);
        })
        .catch(function () {
          setStockMessage("Ошибка получения остатков");
        });
    }

    function handleStockSearch(value) {
      var trimmed = String(value || "").trim();
      if (!trimmed || !dataReady) {
        return;
      }
      setStockMessage("Ищем...");
      TsdStorage.findItemByCode(trimmed)
        .then(function (item) {
          if (item) {
            showStockItem(item);
            if (searchInput) {
              searchInput.value = "";
            }
          } else {
            setStockMessage("Товар не найден в данных");
          }
        })
        .catch(function () {
          setStockMessage("Ошибка поиска");
        });
    }

    function updateDataStatus() {
      TsdStorage.getDataStatus()
        .then(function (status) {
          var exported = status && status.exportedAt;
          var ready =
            status &&
            status.counts &&
            status.counts.items > 0 &&
            status.counts.stock > 0;
          dataReady = !!ready;
          setStatusText(
            exported ? "По состоянию на: " + exported : "Дата актуальности данных неизвестна"
          );
          if (!ready) {
            setStockMessage("Нет данных. Загрузите данные с ПК в Настройках.");
            if (searchInput) {
              searchInput.disabled = true;
            }
            clearDetails();
            clearSuggestions();
          } else if (searchInput) {
            searchInput.disabled = false;
            setStockMessage("");
          }
        })
        .catch(function () {
          setStatusText("Дата актуальности данных неизвестна");
          setStockMessage("Нет данных. Загрузите данные с ПК в Настройках.");
          if (searchInput) {
            searchInput.disabled = true;
          }
          dataReady = false;
          clearDetails();
          clearSuggestions();
        });
    }

    function renderSuggestionList(items) {
      clearSuggestions();
      if (!items.length) {
        return;
      }
      items.forEach(function (item) {
        var button = document.createElement("button");
        button.type = "button";
        button.className = "stock-suggestion";
        var title = document.createElement("div");
        title.className = "stock-suggestion-title";
        title.textContent = item.name || "";
        button.appendChild(title);
        var metaParts = [];
        if (item.sku) {
          metaParts.push("SKU: " + item.sku);
        }
        if (item.gtin) {
          metaParts.push("GTIN: " + item.gtin);
        }
        if (metaParts.length) {
          var meta = document.createElement("div");
          meta.className = "stock-suggestion-meta";
          meta.textContent = metaParts.join(" · ");
          button.appendChild(meta);
        }
        button.addEventListener("click", function () {
          showStockItem(item);
        });
        if (suggestions) {
          suggestions.appendChild(button);
        }
      });
    }

    if (searchInput) {
      searchInput.addEventListener("input", function () {
        var value = searchInput.value.trim();
        if (!dataReady || value.length < 2) {
          clearSuggestions();
          return;
        }
        var onlyDigits = /^[0-9]+$/.test(value);
        if (onlyDigits) {
          clearSuggestions();
          return;
        }
        TsdStorage.searchItems(value, 20)
          .then(function (items) {
            renderSuggestionList(items);
          })
          .catch(function () {
            clearSuggestions();
          });
      });
      searchInput.addEventListener("keydown", function (event) {
        if (event.key === "Enter") {
          event.preventDefault();
          handleStockSearch(searchInput.value);
        }
      });
    }

    if (clearBtn) {
      clearBtn.addEventListener("click", function () {
        if (searchInput) {
          searchInput.value = "";
          searchInput.focus();
        }
      clearSuggestions();
      clearDetails();
      setStockMessage("");
    });
  }

    if (backBtn) {
      backBtn.addEventListener("click", function () {
        navigate("/home");
      });
    }

    if (searchInput) {
      searchInput.focus();
    }

    updateDataStatus();
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
    var docRefInputHtml =
      isDraft
        ? '<input class="doc-ref-input" id="docRefInput" type="text" value="' +
          escapeHtml(docRefValue) +
          '" placeholder="—" />'
        : '<span class="doc-ref-text">' + docRefDisplay + "</span>";

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
      '    <div id="docActionStatus" class="status"></div>' +
      '    <div class="actions-bar">' +
      '      <button class="btn btn-outline" id="undoBtn" ' +
      (doc.undoStack && doc.undoStack.length && isDraft ? "" : "disabled") +
      ">Undo</button>" +
      (isDraft
        ? '      <button class="btn primary-btn" id="finishBtn">Завершить</button>'
        : "") +
      (isDraft
        ? '      <button class="btn btn-danger" id="deleteDraftBtn">🗑️ Удалить черновик</button>'
        : "") +
      (isReady
        ? '      <button class="btn btn-outline" id="revertBtn">Вернуть в черновик</button>'
        : "") +
      '      <button class="btn btn-outline" id="backToDocsBtn">Назад</button>' +
      "    </div>" +
      "  </div>" +
      "</section>"
    );
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
        "</div>"
      );
    }

    if (doc.op === "OUTBOUND") {
      var outboundPartnerValue = header.partner || "Не выбран";
      var outboundFromValue = formatLocationLabel(header.from, header.from_name);
      var outboundOrderValue = formatOrderLabel(header.order_ref);
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
        renderPickerRow({
          label: "Заказ",
          value: outboundOrderValue,
          valueId: "orderValue",
          pickId: "orderPickBtn",
          disabled: !isDraft,
        }) +
        '  <div class="field-hint is-hidden" id="orderHint">Нет списка заказов — импортируйте с ПК</div>' +
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
        "</div>"
      );
    }

    return "";
  }

  function renderScanBlock(doc, isDraft) {
    var qtyMode = doc.header && doc.header.qtyMode === "ASK" ? "ASK" : "INC1";
    return (
      '<div class="scan-card">' +
      '  <label class="form-label" for="barcodeInput">Штрихкод</label>' +
      '  <div class="qty-mode-toggle">' +
      '    <button class="btn qty-mode-btn' +
      (qtyMode === "INC1" ? " is-active" : "") +
      '" data-mode="INC1" type="button">+1</button>' +
      '    <button class="btn qty-mode-btn' +
      (qtyMode === "ASK" ? " is-active" : "") +
      '" data-mode="ASK" type="button">Кол-во...</button>' +
      "  </div>" +
      '  <div class="scan-input-row">' +
      '    <input class="form-input scan-input" id="barcodeInput" type="text" inputmode="none" autocomplete="off" autocapitalize="off" autocorrect="off" spellcheck="false" readonly ' +
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

    var rows = lines
      .map(function (line, index) {
        var nameText = line.itemName ? line.itemName : "Неизвестный код";
        var qtyValue = Number(line.qty) || 0;
        var minusDisabledAttr = qtyValue <= 1 ? ' disabled' : "";
        var minusClassDisabled = qtyValue <= 1 ? " is-disabled" : "";
        return (
          '<div class="lines-row" data-line-index="' +
          index +
          '">' +
          '  <div class="lines-cell">' +
          '    <div class="line-name">' +
          escapeHtml(nameText) +
          "</div>" +
          '    <div class="line-barcode">' +
          escapeHtml(line.barcode) +
          "</div>" +
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

  function renderSettings() {
    return (
      '<section class="screen">' +
      '  <div class="screen-card">' +
      '    <h1 class="screen-title">Настройки</h1>' +
      '    <label class="form-label" for="deviceIdInput">ID устройства</label>' +
      '    <input class="form-input" id="deviceIdInput" type="text" ' +
      '      inputmode="text" autocomplete="off" autocapitalize="off" />' +
      '    <button id="saveSettingsBtn" class="btn primary-btn" type="button">' +
      "      Сохранить" +
      "    </button>" +
      '    <div id="saveStatus" class="status"></div>' +
      '    <button id="importDataBtn" class="btn btn-outline" type="button">' +
      "      Загрузить данные с ПК..." +
      "    </button>" +
      '    <input id="dataFileInput" class="file-input" type="file" accept=".json,application/json" />' +
      '    <div id="dataStatus" class="status"></div>' +
      '    <div id="dataCounts" class="status status-muted"></div>' +
      '    <button id="exportFromSettingsBtn" class="btn btn-outline" type="button">' +
      "      Экспорт JSONL" +
      "    </button>" +
      '    <div id="exportStatus" class="status"></div>' +
      '    <button id="exportItemsBtn" class="btn btn-outline" type="button">' +
      "      Экспорт новых товаров (JSONL)" +
      "    </button>" +
      '    <div id="itemsExportStatus" class="status"></div>' +
      '    <div class="version">Версия приложения: 0.1</div>' +
      "  </div>" +
      "</section>"
    );
  }
  function wireHome() {
    var buttons = document.querySelectorAll("[data-op]");
    buttons.forEach(function (btn) {
      btn.addEventListener("click", function () {
        var op = btn.getAttribute("data-op");
        createDocAndOpen(op, "home");
      });
    });
    var routes = document.querySelectorAll("[data-route]");
    routes.forEach(function (btn) {
      btn.addEventListener("click", function () {
        var route = btn.getAttribute("data-route");
        navigate("/" + (route || ""));
      });
    });
  }

  function wireDocsList() {
    var newBtn = document.getElementById("newDocBtn");
    var exportBtn = document.getElementById("exportBtn");
    var docs = document.querySelectorAll("[data-doc]");

    if (newBtn) {
      newBtn.addEventListener("click", function () {
        setNavOrigin("history");
        navigate("/new");
      });
    }

    if (exportBtn) {
      exportBtn.addEventListener("click", function () {
        exportReadyDocs("exportStatus");
      });
    }

    docs.forEach(function (item) {
      item.addEventListener("click", function () {
        var docId = item.getAttribute("data-doc");
        setNavOrigin("history");
        navigate("/doc/" + encodeURIComponent(docId));
      });
    });
  }

  function wireNewOp() {
    var buttons = document.querySelectorAll("[data-op]");
    buttons.forEach(function (btn) {
      btn.addEventListener("click", function () {
        var op = btn.getAttribute("data-op");
        createDocAndOpen(op, "history");
      });
    });
  }

  function buildOverlay(title) {
    var overlay = document.createElement("div");
    overlay.className = "overlay";
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

  function openPartnerPicker(onSelect) {
    setScanHighlight(false);
    var overlay = buildOverlay("Контрагенты");
    var input = overlay.querySelector(".overlay-search");
    var recentsEl = overlay.querySelector(".overlay-recents");
    var resultsEl = overlay.querySelector(".overlay-results");
    var closeBtn = overlay.querySelector(".overlay-close");

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

    function loadRecents() {
      TsdStorage.getSetting("recentPartnerIds")
        .then(function (ids) {
          var list = Array.isArray(ids) ? ids.slice(0, 5) : [];
          return Promise.all(
            list.map(function (id) {
              return TsdStorage.getPartnerById(id).catch(function () {
                return null;
              });
            })
          );
        })
        .then(function (partners) {
          var filtered = partners.filter(function (partner) {
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
      TsdStorage.searchPartners(query)
        .then(function (partners) {
          var list = partners.map(function (partner) {
            var sub = [];
            if (partner.code) {
              sub.push("Код: " + partner.code);
            }
            if (partner.inn) {
              sub.push("ИНН: " + partner.inn);
            }
            partner.subLabel = sub.join(" · ");
            return partner;
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
    input.focus();
  }

  function openLocationPicker(onSelect) {
    setScanHighlight(false);
    var overlay = buildOverlay("Локации");
    var input = overlay.querySelector(".overlay-search");
    var recentsEl = overlay.querySelector(".overlay-recents");
    var resultsEl = overlay.querySelector(".overlay-results");
    var closeBtn = overlay.querySelector(".overlay-close");

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

    function loadRecents() {
      TsdStorage.getSetting("recentLocationIds")
        .then(function (ids) {
          var list = Array.isArray(ids) ? ids.slice(0, 5) : [];
          return Promise.all(
            list.map(function (id) {
              return TsdStorage.getLocationById(id).catch(function () {
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
      TsdStorage.searchLocations(query)
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
    input.focus();
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
    input.focus();
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
    if (searchInput) {
      searchInput.focus();
    }
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

  function openItemCreateOverlay(scannedBarcode, onCreated) {
    setScanHighlight(false);
    var overlay = document.createElement("div");
    overlay.className = "overlay";
    overlay.innerHTML =
      '<div class="overlay-card">' +
      '  <div class="overlay-header">' +
      '    <div class="overlay-title">Новый товар</div>' +
      '    <button class="btn btn-ghost overlay-close" type="button">Закрыть</button>' +
      "  </div>" +
      '  <label class="form-label" for="itemNameInput">Наименование*</label>' +
      '  <input class="form-input" id="itemNameInput" type="text" autocomplete="off" />' +
      '  <div class="field-error" id="itemNameError"></div>' +
      '  <label class="form-label" for="itemBarcodeInput">Штрихкод*</label>' +
      '  <input class="form-input" id="itemBarcodeInput" type="text" autocomplete="off" />' +
      '  <div class="field-error" id="itemBarcodeError"></div>' +
      '  <label class="form-label" for="itemGtinInput">GTIN</label>' +
      '  <div class="input-row gtin-row">' +
      '    <input class="form-input" id="itemGtinInput" type="text" autocomplete="off" />' +
      '    <button class="btn btn-outline btn-icon gtin-copy-btn" type="button" aria-label="Копировать штрихкод в GTIN">' +
      '      <svg viewBox="0 0 24 24" aria-hidden="true">' +
      '        <path d="M8 8h11v11H8z"></path>' +
      '        <path d="M5 5h11v2H7v9H5z"></path>' +
      "      </svg>" +
      "    </button>" +
      "  </div>" +
      '  <div class="field-hint" id="itemGtinHint"></div>' +
      '  <label class="form-label" for="itemUomSelect">Базовая единица*</label>' +
      '  <select class="form-input" id="itemUomSelect"></select>' +
      '  <div class="field-error" id="itemUomError"></div>' +
      '  <div class="overlay-actions">' +
      '    <button class="btn btn-outline overlay-cancel" type="button">Отмена</button>' +
      '    <button class="btn primary-btn overlay-confirm" type="button">Сохранить</button>' +
      "  </div>" +
      "</div>";

    var nameInput = overlay.querySelector("#itemNameInput");
    var barcodeInput = overlay.querySelector("#itemBarcodeInput");
    var gtinInput = overlay.querySelector("#itemGtinInput");
    var uomSelect = overlay.querySelector("#itemUomSelect");
    var nameError = overlay.querySelector("#itemNameError");
    var barcodeError = overlay.querySelector("#itemBarcodeError");
    var uomError = overlay.querySelector("#itemUomError");
    var gtinHint = overlay.querySelector("#itemGtinHint");
    var gtinCopyBtn = overlay.querySelector(".gtin-copy-btn");
    var closeBtn = overlay.querySelector(".overlay-close");
    var cancelBtn = overlay.querySelector(".overlay-cancel");
    var confirmBtn = overlay.querySelector(".overlay-confirm");
    var gtinHintTimer = null;

    function setError(el, message) {
      if (el) {
        el.textContent = message || "";
      }
    }

    function close() {
      document.body.removeChild(overlay);
      document.removeEventListener("keydown", onKeyDown);
      if (gtinHintTimer) {
        clearTimeout(gtinHintTimer);
        gtinHintTimer = null;
      }
      enterScanMode();
    }

    function validate() {
      var valid = true;
      setError(nameError, "");
      setError(barcodeError, "");
      setError(uomError, "");
      var nameValue = nameInput ? nameInput.value.trim() : "";
      var barcodeValue = barcodeInput ? barcodeInput.value.trim() : "";
      var uomValue = uomSelect ? uomSelect.value : "";
      if (!nameValue) {
        setError(nameError, "Введите наименование");
        valid = false;
      }
      if (!barcodeValue) {
        setError(barcodeError, "Введите штрихкод");
        valid = false;
      }
      if (!uomValue) {
        setError(uomError, "Выберите единицу");
        valid = false;
      }
      return valid;
    }

    function buildItemId(deviceId) {
      var safeDevice = String(deviceId || "CT48-01").replace(/[^a-zA-Z0-9_-]/g, "");
      var rand = Math.random().toString(36).slice(2, 6);
      return "local-" + safeDevice + "-" + Date.now() + "-" + rand;
    }

    function submit() {
      if (!validate()) {
        return;
      }
      confirmBtn.disabled = true;
      var nameValue = nameInput ? nameInput.value.trim() : "";
      var barcodeValue = barcodeInput ? barcodeInput.value.trim() : "";
      var gtinValue = gtinInput ? gtinInput.value.trim() : "";
      var uomValue = uomSelect ? uomSelect.value : "";
      TsdStorage.getSetting("device_id")
        .then(function (deviceId) {
          var record = {
            itemId: buildItemId(deviceId),
            name: nameValue,
            barcode: barcodeValue,
            gtin: gtinValue || null,
            base_uom: uomValue,
            created_at: new Date().toISOString(),
            created_on_device: true,
            exported_at: null,
            barcodes: [barcodeValue],
          };
          return TsdStorage.createLocalItem(record);
        })
        .then(function (created) {
          close();
          if (typeof onCreated === "function") {
            onCreated(created);
          }
        })
        .catch(function (error) {
          confirmBtn.disabled = false;
          if (error && error.code === "barcode_exists") {
            setError(barcodeError, "Штрихкод уже существует");
            return;
          }
          setError(barcodeError, "Ошибка сохранения");
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
    document.addEventListener("keydown", onKeyDown);

    overlay.addEventListener("click", function (event) {
      if (event.target === overlay) {
        close();
      }
    });

    closeBtn.addEventListener("click", close);
    cancelBtn.addEventListener("click", close);
    confirmBtn.addEventListener("click", submit);

    if (gtinCopyBtn) {
      gtinCopyBtn.addEventListener("click", function () {
        var barcodeValue = barcodeInput ? barcodeInput.value.trim() : "";
        if (!barcodeValue) {
          if (gtinHint) {
            gtinHint.textContent = "Сначала заполните штрихкод";
          }
          return;
        }
        if (gtinInput) {
          gtinInput.value = barcodeValue;
        }
        if (gtinHint) {
          gtinHint.textContent = "GTIN заполнен";
          if (gtinHintTimer) {
            clearTimeout(gtinHintTimer);
          }
          gtinHintTimer = window.setTimeout(function () {
            if (gtinHint) {
              gtinHint.textContent = "";
            }
            gtinHintTimer = null;
          }, 1500);
        }
      });
    }

    if (barcodeInput) {
      barcodeInput.value = scannedBarcode || "";
    }

    if (uomSelect) {
      uomSelect.innerHTML = '<option value="">Выберите...</option>';
      TsdStorage.listUoms()
        .then(function (uoms) {
          if (!uoms.length) {
            uomSelect.disabled = true;
            confirmBtn.disabled = true;
            return;
          }
          uoms.forEach(function (uom) {
            var option = document.createElement("option");
            option.value = uom;
            option.textContent = uom;
            uomSelect.appendChild(option);
          });
          uomSelect.disabled = false;
          confirmBtn.disabled = false;
        })
        .catch(function () {
          uomSelect.disabled = true;
          confirmBtn.disabled = true;
        });
    }

    if (nameInput) {
      nameInput.focus();
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
    var backToDocsBtn = document.getElementById("backToDocsBtn");
    var docRefInput = document.getElementById("docRefInput");
    var headerInputs = document.querySelectorAll("[data-header]");
    var deleteButtons = document.querySelectorAll(".line-delete");
    var deleteDocBtn = document.getElementById("deleteDraftBtn");
    var manualInputButtons = document.querySelectorAll(".kbd-btn");
    var partnerPickBtn = document.getElementById("partnerPickBtn");
    var toPickBtn = document.getElementById("toPickBtn");
    var fromPickBtn = document.getElementById("fromPickBtn");
    var orderPickBtn = document.getElementById("orderPickBtn");
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
    var orderHint = document.getElementById("orderHint");
    var dataStatus = null;
    var lookupToken = 0;
    var qtyModeButtons = document.querySelectorAll(".qty-mode-btn");
    var qtyOverlay = null;
    var qtyOverlayKeyListener = null;
    var isDraftDoc = doc.status === "DRAFT";
    var docActionStatus = document.getElementById("docActionStatus");
    var scanTarget = { type: "barcode", field: null };
    var scanBuffer = "";
    var scanBufferTimer = null;
    var incBtn = null;
    var incHoldTimer = null;
    var incHoldTriggered = false;

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
      TsdStorage.findItemByCode(trimmed)
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
        .catch(function () {
          if (token === lookupToken) {
            setScanInfo("", false);
          }
        });
    }

    function setScanTarget(type, field) {
      scanTarget.type = type;
      scanTarget.field = field || null;
    }

    function focusBarcode() {
      setScanTarget("barcode");
      enterScanMode();
    }

    function applyCatalogState(status) {
      var hasPartners = status && status.counts && status.counts.partners > 0;
      var hasLocations = status && status.counts && status.counts.locations > 0;
      var hasOrders = status && status.counts && status.counts.orders > 0;

      if (partnerPickerRow) {
        partnerPickerRow.classList.remove("is-hidden");
      }
      if (partnerPickBtn) {
        partnerPickBtn.disabled = !hasPartners || doc.status !== "DRAFT";
      }
      if (toPickBtn) {
        toPickBtn.disabled = !hasLocations || doc.status !== "DRAFT";
      }
      if (fromPickBtn) {
        fromPickBtn.disabled = !hasLocations || doc.status !== "DRAFT";
      }
      if (orderPickBtn) {
        orderPickBtn.disabled = !hasOrders || doc.status !== "DRAFT";
      }
      if (locationPickBtn) {
        locationPickBtn.disabled = !hasLocations || doc.status !== "DRAFT";
      }

      if (orderHint) {
        orderHint.classList.toggle("is-hidden", hasOrders);
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

    function openQuantityOverlay(barcode, onSelect) {
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
      qtyOverlay.innerHTML =
        '<div class="overlay-card">' +
        '  <div class="overlay-header">' +
        '    <div class="overlay-title">Количество</div>' +
        '    <button class="btn btn-ghost overlay-close" type="button">✕</button>' +
        "  </div>" +
        '  <div class="qty-overlay-body">' +
        '    <div class="qty-overlay-barcode">' +
        escapeHtml(barcode) +
        "</div>" +
        '    <div class="qty-overlay-quick">' +
        quickButtons +
        "    </div>" +
        '    <input class="form-input qty-overlay-input" type="number" min="1" value="1" />' +
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

      function tryApplyQuantity() {
        if (!quantityInput) {
          return;
        }
        var value = parseInt(quantityInput.value, 10);
        if (!value || value <= 0) {
          quantityInput.focus();
          quantityInput.select();
          return;
        }
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
        quantityInput.focus();
        quantityInput.select();
      }
    }

    function addLineWithQuantity(barcode, quantity) {
      var qtyValue = Number(quantity) || 0;
      if (!qtyValue || qtyValue <= 0) {
        focusBarcode();
        return;
      }
      var lineData = buildLineData(doc.op, doc.header);
      TsdStorage.findItemByCode(barcode)
        .then(function (item) {
          if (item) {
            finalizeLine(item);
            return;
          }
          setScanInfo("Товар не найден", true);
          openConfirmOverlay("Товар не найден", "Создать?", "Создать", function () {
            openItemCreateOverlay(barcode, function (createdItem) {
              finalizeLine(createdItem);
            });
          });
        })
        .catch(function () {
          setScanInfo("Товар не найден", true);
        });

      function finalizeLine(item) {
        var itemId = item ? item.itemId : null;
        var itemName = item ? item.name : null;
        if (!item) {
          return;
        }
        var lineIndex = findLineIndex(doc.op, doc.lines, barcode, lineData);
        if (lineIndex >= 0) {
          doc.lines[lineIndex].qty += qtyValue;
          if (itemName && !doc.lines[lineIndex].itemName) {
            doc.lines[lineIndex].itemName = itemName;
            doc.lines[lineIndex].itemId = itemId;
          }
        } else {
          doc.lines.push({
            barcode: barcode,
            qty: qtyValue,
            from: lineData.from,
            to: lineData.to,
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
          reason_code: lineData.reason_code,
        });

        if (barcodeInput) {
          barcodeInput.value = "";
        }
        setScanInfo("", false);
        saveDocState().then(refreshDocView);
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
          TsdStorage.getLocationById(doc.header.from_id).then(function (location) {
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
          TsdStorage.getLocationById(doc.header.to_id).then(function (location) {
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
          TsdStorage.getLocationById(doc.header.location_id).then(function (location) {
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
      var qtyMode = doc.header.qtyMode === "ASK" ? "ASK" : "INC1";
      if (qtyMode === "ASK") {
        openQuantityOverlay(barcode, function (quantity) {
          addLineWithQuantity(barcode, quantity);
        });
        return;
      }
      addLineWithQuantity(barcode, qtyStep);
    }

    function validateBeforeFinish() {
      var valid = true;
      setPartnerError("");
      setLocationError("from", "");
      setLocationError("to", "");
      setLocationError("location", "");
      if (doc.op === "INBOUND") {
        if (!doc.header.partner_id) {
          setPartnerError("Выберите поставщика");
          valid = false;
        }
        if (!normalizeValue(doc.header.to) && !doc.header.to_id) {
          setLocationError("to", "Укажите место хранения");
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

      quickBtns.forEach(function (btn) {
        btn.addEventListener("click", function () {
          var value = parseInt(btn.getAttribute("data-step"), 10) || 1;
          submit(value);
        });
      });

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
      if (input) {
        input.focus();
        input.select();
      }
    }

    function setPartnerError(message) {
      if (partnerErrorEl) {
        partnerErrorEl.textContent = message || "";
      }
    }

    function clearScanBuffer() {
      scanBuffer = "";
      if (scanBufferTimer) {
        clearTimeout(scanBufferTimer);
        scanBufferTimer = null;
      }
      if (barcodeInput && scanTarget.type === "barcode") {
        barcodeInput.value = "";
      }
      updateScanInfoByBarcode("");
    }

    function scheduleScanBufferReset() {
      if (scanBufferTimer) {
        clearTimeout(scanBufferTimer);
      }
      scanBufferTimer = window.setTimeout(function () {
        clearScanBuffer();
      }, 400);
    }

    function handleScannedValue(value) {
      var trimmed = normalizeValue(value);
      if (!trimmed) {
        return;
      }
      handleAddLine(trimmed);
    }

    function handleScanKeydown(event) {
      if (isManualOverlayOpen()) {
        return;
      }
      if (!isDraftDoc) {
        return;
      }
      if (event.key === "Enter") {
        if (scanBuffer) {
          var value = scanBuffer;
          clearScanBuffer();
          handleScannedValue(value);
          event.preventDefault();
        }
        return;
      }
      if (
        event.key &&
        event.key.length === 1 &&
        !event.altKey &&
        !event.ctrlKey &&
        !event.metaKey
      ) {
        scanBuffer += event.key;
        scheduleScanBufferReset();
        if (barcodeInput && scanTarget.type === "barcode") {
          barcodeInput.value = scanBuffer;
          updateScanInfoByBarcode(scanBuffer);
        }
      }
    }

    handleScanKeydown.cleanup = function () {
      clearScanBuffer();
    };

    setScanHandler(handleScanKeydown);

    if (barcodeInput) {
      barcodeInput.addEventListener("focus", function () {
        setScanTarget("barcode");
        enterScanMode();
      });
      barcodeInput.addEventListener("click", function () {
        setScanTarget("barcode");
        enterScanMode();
      });
      barcodeInput.addEventListener("input", function () {
        updateScanInfoByBarcode(barcodeInput.value);
      });
      barcodeInput.addEventListener("keydown", function (event) {
        if (event.key === "Enter") {
          event.preventDefault();
          handleAddLine();
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
      saveDocState().then(refreshDocView);
    }

    function applyLocationSelection(field, location) {
      setLocationError(field, "");
      doc.header[field] = location.code || "";
      doc.header[field + "_name"] = location.name || null;
      doc.header[field + "_id"] = location.locationId;
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
        openPartnerPicker(applyPartnerSelection);
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
          return;
        }
        doc.status = "READY";
        saveDocState().then(refreshDocView);
      });
    }

    if (deleteDocBtn) {
      deleteDocBtn.addEventListener("click", function () {
        if (doc.status !== "DRAFT") {
          return;
        }
        openConfirmOverlay(
          "Удалить документ?",
          "Документ-черновик будет удалён без возможности восстановления.",
          "Удалить",
          function () {
            deleteDocBtn.disabled = true;
            TsdStorage.deleteDoc(doc.id)
              .then(function () {
                alert("Черновик удалён");
                setNavOrigin("history");
                navigate("/docs");
              })
              .catch(function () {
                setDocStatus("Ошибка удаления документа");
                deleteDocBtn.disabled = false;
              });
          }
        );
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

    if (backToDocsBtn) {
      backToDocsBtn.addEventListener("click", function () {
        navigate("/docs");
      });
    }

    if (docRefInput) {
      docRefInput.addEventListener("input", function () {
        doc.doc_ref = docRefInput.value.trim();
        saveDocState();
      });
    }

    headerInputs.forEach(function (input) {
      var handler = function () {
        var field = input.getAttribute("data-header");
        if (!field) {
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
              setScanTarget("barcode");
            },
            onSubmit: function (value) {
              setScanTarget("barcode");
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
            setScanTarget("barcode");
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

    TsdStorage.getDataStatus()
      .then(function (status) {
        dataStatus = status;
        applyCatalogState(status);
        hydrateHeaderFromCatalog();
      })
      .catch(function () {
        applyCatalogState(null);
      });

    focusFirstLocationOrBarcode();
  }

  function wireSettings() {
    var input = document.getElementById("deviceIdInput");
    var status = document.getElementById("saveStatus");
    var saveBtn = document.getElementById("saveSettingsBtn");
    var exportBtn = document.getElementById("exportFromSettingsBtn");
    var exportItemsBtn = document.getElementById("exportItemsBtn");
    var importBtn = document.getElementById("importDataBtn");
    var fileInput = document.getElementById("dataFileInput");
    var dataStatusText = document.getElementById("dataStatus");
    var dataCountsText = document.getElementById("dataCounts");

    TsdStorage.getSetting("device_id")
      .then(function (value) {
        if (input) {
          input.value = value || "CT48-01";
        }
      })
      .catch(function () {
        if (input) {
          input.value = "CT48-01";
        }
      });

    function renderDataStatus(statusInfo) {
      if (!dataStatusText || !dataCountsText) {
        return;
      }
      if (!statusInfo || !statusInfo.exportedAt) {
        dataStatusText.textContent = "Данные не загружены";
        dataCountsText.textContent = "";
        return;
      }
      dataStatusText.textContent = "Данные обновлены: " + statusInfo.exportedAt;
      dataCountsText.textContent =
        "Товары: " +
        statusInfo.counts.items +
        " · Контрагенты: " +
        statusInfo.counts.partners +
        " · Локации: " +
        statusInfo.counts.locations +
        " · Остатки: " +
        statusInfo.counts.stock;
    }

    TsdStorage.getDataStatus()
      .then(renderDataStatus)
      .catch(function () {
        renderDataStatus(null);
      });

    if (saveBtn) {
      saveBtn.addEventListener("click", function () {
        var value = input ? input.value.trim() : "";
        if (!value) {
          value = "CT48-01";
        }

        TsdStorage.setSetting("device_id", value).then(function () {
          if (status) {
            status.textContent = "Сохранено";
            setTimeout(function () {
              status.textContent = "";
            }, 1500);
          }
        });
      });
    }

    if (importBtn && fileInput) {
      importBtn.addEventListener("click", function () {
        fileInput.click();
      });

      fileInput.addEventListener("change", function () {
        var file = fileInput.files && fileInput.files[0];
        if (!file) {
          return;
        }
        if (dataStatusText) {
          dataStatusText.textContent = "Загрузка данных...";
        }
        var reader = new FileReader();
        reader.onload = function () {
          try {
            var data = JSON.parse(reader.result);
            if (!data.meta || data.meta.schemaVersion !== 1) {
              if (dataStatusText) {
                dataStatusText.textContent = "Неверная версия схемы данных";
              }
              return;
            }
            TsdStorage.importTsdData(data)
              .then(function () {
                return TsdStorage.getDataStatus();
              })
              .then(function (statusInfo) {
                renderDataStatus(statusInfo);
              })
              .catch(function () {
                if (dataStatusText) {
                  dataStatusText.textContent = "Ошибка импорта данных";
                }
              });
          } catch (error) {
            if (dataStatusText) {
              dataStatusText.textContent = "Некорректный JSON";
            }
          } finally {
            fileInput.value = "";
          }
        };
        reader.onerror = function () {
          if (dataStatusText) {
            dataStatusText.textContent = "Ошибка чтения файла";
          }
          fileInput.value = "";
        };
        reader.readAsText(file);
      });
    }

    if (exportBtn) {
      exportBtn.addEventListener("click", function () {
        exportReadyDocs("exportStatus");
      });
    }

    if (exportItemsBtn) {
      exportItemsBtn.addEventListener("click", function () {
        exportLocalItems("itemsExportStatus");
      });
    }
  }

  function createDocAndOpen(op, navOrigin) {
    if (!OPS[op]) {
      return;
    }

    var now = new Date();
    var dateKey = getDateKey(now);
    var prefix = OPS[op].prefix;

    Promise.all([TsdStorage.getSetting("device_id"), TsdStorage.nextDocCounter(prefix, dateKey)])
      .then(function (results) {
        var deviceId = results[0] || "CT48-01";
        var counter = results[1];
        var docRef =
          prefix + "-" + dateKey + "-" + deviceId + "-" + padNumber(counter, 3);
        var docId = createUuid();
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
        alert("Не удалось создать документ.");
      });
  }

  function getDefaultHeader(op) {
    if (op === "INBOUND") {
      return {
        partner: "",
        partner_id: null,
        to: "",
        to_name: null,
        to_id: null,
        qtyMode: "INC1",
      };
    }
    if (op === "OUTBOUND") {
      return {
        partner: "",
        partner_id: null,
        order_id: null,
        order_ref: "",
        from: "",
        from_name: null,
        from_id: null,
        qtyMode: "INC1",
      };
    }
    if (op === "MOVE") {
      return {
        from: "",
        from_name: null,
        from_id: null,
        to: "",
        to_name: null,
        to_id: null,
        qtyMode: "INC1",
      };
    }
    if (op === "WRITE_OFF") {
      return {
        from: "",
        from_name: null,
        from_id: null,
        reason_code: null,
        reason_label: null,
        qtyMode: "INC1",
      };
    }
    if (op === "INVENTORY") {
      return {
        location: "",
        location_name: null,
        location_id: null,
        qtyMode: "INC1",
      };
    }
    return {};
  }

  function normalizeValue(value) {
    var trimmed = String(value || "").trim();
    return trimmed ? trimmed : "";
  }

  function buildLineData(op, header) {
    var from = null;
    var to = null;
    var reason = null;

    if (op === "INBOUND") {
      to = normalizeValue(header.to) || null;
    } else if (op === "OUTBOUND") {
      from = normalizeValue(header.from) || null;
    } else if (op === "MOVE") {
      from = normalizeValue(header.from) || null;
      to = normalizeValue(header.to) || null;
    } else if (op === "WRITE_OFF") {
      from = normalizeValue(header.from) || null;
      reason = normalizeValue(header.reason_code) || null;
    } else if (op === "INVENTORY") {
      to = normalizeValue(header.location) || null;
    }

    return { from: from, to: to, reason_code: reason };
  }

  function buildLineKey(op, barcode, lineData) {
    var safeBarcode = normalizeValue(barcode);
    if (op === "INBOUND") {
      return safeBarcode + "|" + (lineData.to || "");
    }
    if (op === "OUTBOUND") {
      return safeBarcode + "|" + (lineData.from || "");
    }
    if (op === "MOVE") {
      return safeBarcode + "|" + (lineData.from || "") + "|" + (lineData.to || "");
    }
    if (op === "WRITE_OFF") {
      return safeBarcode + "|" + (lineData.from || "") + "|" + (lineData.reason_code || "");
    }
    if (op === "INVENTORY") {
      return safeBarcode + "|" + (lineData.to || "");
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
  function exportReadyDocs(statusElementId) {
    var statusEl = statusElementId ? document.getElementById(statusElementId) : null;

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

        if (!readyDocs.length) {
          setStatus("Нет готовых операций для экспорта");
          return;
        }

        var now = new Date();
        var nowIso = now.toISOString();
        var dateKey = getDateKey(now);
        var timeKey = getTimeKey(now);
        var filename = "SHIFT_" + dateKey + "_" + deviceId + "_" + timeKey + ".jsonl";
        var lines = [];

        readyDocs.forEach(function (doc) {
          (doc.lines || []).forEach(function (line) {
            var record = {
              event_id: createUuid(),
              ts: nowIso,
              device_id: deviceId,
              op: doc.op,
              doc_ref: doc.doc_ref,
              barcode: line.barcode,
              qty: line.qty,
              from: line.from || null,
              to: line.to || null,
              partner_id: doc.header && doc.header.partner_id ? doc.header.partner_id : null,
              order_id: doc.header && doc.header.order_id ? doc.header.order_id : null,
              order_ref: doc.header && doc.header.order_ref ? doc.header.order_ref : null,
              reason_code: line.reason_code || null,
            };
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
        var saveTasks = readyDocs.map(function (doc) {
          doc.status = "EXPORTED";
          doc.exportedAt = exportedAt;
          doc.updatedAt = exportedAt;
          return TsdStorage.saveDoc(doc);
        });

        Promise.all(saveTasks)
          .then(function () {
            setStatus("Экспортировано: " + readyDocs.length + " операций");
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

  function exportLocalItems(statusElementId) {
    var statusEl = statusElementId ? document.getElementById(statusElementId) : null;

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text;
      }
    }

    Promise.all([TsdStorage.listLocalItemsForExport(), TsdStorage.getSetting("device_id")])
      .then(function (results) {
        var items = results[0] || [];
        var deviceId = results[1] || "CT48-01";
        if (!items.length) {
          setStatus("Нет новых товаров для экспорта");
          return;
        }

        var now = new Date();
        var nowIso = now.toISOString();
        var dateKey = getDateKey(now);
        var timeKey = getTimeKey(now);
        var filename = "ITEMS_" + dateKey + "_" + timeKey + "_" + deviceId + ".jsonl";
        var lines = items.map(function (item) {
          var barcode =
            item.barcode ||
            (Array.isArray(item.barcodes) && item.barcodes[0]) ||
            "";
          var record = {
            event: "ITEM_UPSERT",
            ts: nowIso,
            device_id: deviceId,
            item: {
              item_id: item.itemId,
              name: item.name || "",
              barcode: barcode || "",
              gtin: item.gtin || "",
              base_uom: item.base_uom || "",
            },
          };
          return JSON.stringify(record);
        });

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
          setStatus("Ошибка экспорта товаров");
          return;
        }

        var exportedAt = new Date().toISOString();
        var ids = items.map(function (item) {
          return item.itemId;
        });
        TsdStorage.markLocalItemsExported(ids, exportedAt)
          .then(function () {
            setStatus("Экспортировано товаров: " + items.length);
          })
          .catch(function () {
            setStatus("Ошибка сохранения статусов товаров");
          });
      })
      .catch(function () {
        setStatus("Ошибка экспорта товаров");
      });
  }

  document.addEventListener("DOMContentLoaded", function () {
    TsdStorage.init()
      .then(function () {
        return TsdStorage.ensureDefaults();
      })
      .then(function () {
        if (backBtn) {
          backBtn.addEventListener("click", function () {
            if (!currentRoute) {
              navigate("/docs");
              return;
            }
        var origin = getNavOrigin();
        if (currentRoute.name === "home") {
          navigate("/docs");
        } else if (currentRoute.name === "docs") {
          navigate("/home");
        } else if (currentRoute.name === "doc" || currentRoute.name === "new") {
          if (origin === "history") {
            navigate("/docs");
          } else {
            navigate("/home");
          }
        } else if (currentRoute.name === "stock" || currentRoute.name === "settings") {
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

