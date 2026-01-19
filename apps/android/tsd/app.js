(function () {
  "use strict";

  var app = document.getElementById("app");
  var backBtn = document.getElementById("backBtn");
  var settingsBtn = document.getElementById("settingsBtn");
  var currentRoute = null;

  var STATUS_ORDER = {
    DRAFT: 0,
    READY: 1,
    EXPORTED: 2,
  };

  var STATUS_LABELS = {
    DRAFT: "Черновик",
    READY: "Готово",
    EXPORTED: "Экспортировано",
  };

  var OPS = {
    INBOUND: { label: "Приемка", prefix: "IN" },
    OUTBOUND: { label: "Отгрузка", prefix: "OUT" },
    MOVE: { label: "Перемещение", prefix: "MOV" },
    WRITE_OFF: { label: "Списание", prefix: "WO" },
    INVENTORY: { label: "Инвентаризация", prefix: "INV" },
  };

  var REASON_CODES = ["DAMAGE", "EXPIRED", "DEFECT", "LOSS", "OTHER"];

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
      backBtn.textContent = "Документы";
      backBtn.classList.remove("is-hidden");
    } else {
      backBtn.textContent = "Назад";
      backBtn.classList.remove("is-hidden");
    }

    if (settingsBtn) {
      settingsBtn.disabled = route.name === "settings";
    }
  }

  function renderRoute() {
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
      '  <div class="screen-card">' +
      '    <div class="section-title">Список операций</div>' +
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
        navigate("/docs");
      });
    }

    if (searchInput) {
      searchInput.focus();
    }

    updateDataStatus();
  }

  function renderDoc(doc) {
    var headerFields = renderHeaderFields(doc);
    var linesHtml = renderLines(doc);
    var statusLabel = STATUS_LABELS[doc.status] || doc.status;
    var isDraft = doc.status === "DRAFT";
    var isReady = doc.status === "READY";
    var isExported = doc.status === "EXPORTED";

    return (
      '<section class="screen">' +
      '  <div class="screen-card">' +
      '    <div class="section-title">Операция</div>' +
      '    <div class="doc-meta">' +
      '      <div class="doc-meta-row">' +
      '        <span class="meta-label">Тип</span>' +
      '        <span class="meta-value">' +
      escapeHtml(OPS[doc.op] ? OPS[doc.op].label : doc.op) +
      "</span>" +
      "      </div>" +
      '      <div class="doc-meta-row">' +
      '        <label class="meta-label" for="docRefInput">Номер</label>' +
      '        <input class="form-input" id="docRefInput" type="text" value="' +
      escapeHtml(doc.doc_ref || "") +
      "" +
      '" ' +
      (isDraft ? "" : "disabled") +
      " />" +
      "      </div>" +
      '      <div class="doc-meta-row">' +
      '        <span class="meta-label">Статус</span>' +
      '        <span class="status-pill">' +
      escapeHtml(statusLabel) +
      "</span>" +
      "      </div>" +
      "    </div>" +
      '    <div class="section-subtitle">Шапка</div>' +
      '    <div class="form-grid">' +
      headerFields +
      "    </div>" +
      (isExported
        ? '<div class="notice">Документ экспортирован, редактирование недоступно.</div>'
        : "") +
      '    <div class="section-subtitle">Сканирование</div>' +
      renderScanBlock(doc, isDraft) +
      '    <div class="section-subtitle">Строки</div>' +
      linesHtml +
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
        '<div class="form-field">' +
        '  <label class="form-label" for="partnerInput">Поставщик</label>' +
        '  <div class="picker-row" id="partnerPickerRow">' +
        '    <div class="picker-value" id="partnerValue">' +
        escapeHtml(inboundPartnerValue) +
        "</div>" +
        '    <button class="btn btn-outline picker-btn" id="partnerPickBtn" type="button" ' +
        (isDraft ? "" : "disabled") +
        ">Выбрать...</button>" +
        "  </div>" +
        '  <input class="form-input picker-fallback" id="partnerInput" data-header="partner" type="text" value="' +
        escapeHtml(header.partner || "") +
        "" +
        '" ' +
        (isDraft ? "" : "disabled") +
        " />" +
        "</div>" +
        '<div class="form-field">' +
        '  <label class="form-label" for="toInput">Куда</label>' +
        '  <div class="picker-row" id="toPickerRow">' +
        '    <div class="picker-value" id="toValue">' +
        escapeHtml(inboundToValue) +
        "</div>" +
        '    <button class="btn btn-outline picker-btn" id="toPickBtn" type="button" ' +
        (isDraft ? "" : "disabled") +
        ">Выбрать...</button>" +
        "  </div>" +
        '  <input class="form-input picker-fallback" id="toInput" data-header="to" data-location-field="to" type="text" value="' +
        escapeHtml(header.to || "") +
        "" +
        '" ' +
        (isDraft ? "" : "disabled") +
        " />" +
        '<div class="field-error" id="toError"></div>' +
        "</div>"
      );
    }

    if (doc.op === "OUTBOUND") {
      var outboundPartnerValue = header.partner || "Не выбран";
      var outboundFromValue = formatLocationLabel(header.from, header.from_name);
      return (
        '<div class="form-field">' +
        '  <label class="form-label" for="partnerInput">Покупатель</label>' +
        '  <div class="picker-row" id="partnerPickerRow">' +
        '    <div class="picker-value" id="partnerValue">' +
        escapeHtml(outboundPartnerValue) +
        "</div>" +
        '    <button class="btn btn-outline picker-btn" id="partnerPickBtn" type="button" ' +
        (isDraft ? "" : "disabled") +
        ">Выбрать...</button>" +
        "  </div>" +
        '  <input class="form-input picker-fallback" id="partnerInput" data-header="partner" type="text" value="' +
        escapeHtml(header.partner || "") +
        "" +
        '" ' +
        (isDraft ? "" : "disabled") +
        " />" +
        "</div>" +
        '<div class="form-field">' +
        '  <label class="form-label" for="orderRefInput">Заказ</label>' +
        '  <input class="form-input" id="orderRefInput" data-header="order_ref" type="text" value="' +
        escapeHtml(header.order_ref || "") +
        "" +
        '" ' +
        (isDraft ? "" : "disabled") +
        " />" +
        "</div>" +
        '<div class="form-field">' +
        '  <label class="form-label" for="fromInput">Откуда</label>' +
        '  <div class="picker-row" id="fromPickerRow">' +
        '    <div class="picker-value" id="fromValue">' +
        escapeHtml(outboundFromValue) +
        "</div>" +
        '    <button class="btn btn-outline picker-btn" id="fromPickBtn" type="button" ' +
        (isDraft ? "" : "disabled") +
        ">Выбрать...</button>" +
        "  </div>" +
        '  <input class="form-input picker-fallback" id="fromInput" data-header="from" data-location-field="from" type="text" value="' +
        escapeHtml(header.from || "") +
        "" +
        '" ' +
        (isDraft ? "" : "disabled") +
        " />" +
        '<div class="field-error" id="fromError"></div>' +
        "</div>"
      );
    }

    if (doc.op === "MOVE") {
      var moveFromValue = formatLocationLabel(header.from, header.from_name);
      var moveToValue = formatLocationLabel(header.to, header.to_name);
      return (
        '<div class="form-field">' +
        '  <label class="form-label" for="fromInput">Откуда</label>' +
        '  <div class="picker-row" id="fromPickerRow">' +
        '    <div class="picker-value" id="fromValue">' +
        escapeHtml(moveFromValue) +
        "</div>" +
        '    <button class="btn btn-outline picker-btn" id="fromPickBtn" type="button" ' +
        (isDraft ? "" : "disabled") +
        ">Выбрать...</button>" +
        "  </div>" +
        '  <input class="form-input picker-fallback" id="fromInput" data-header="from" data-location-field="from" type="text" value="' +
        escapeHtml(header.from || "") +
        "" +
        '" ' +
        (isDraft ? "" : "disabled") +
        " />" +
        '<div class="field-error" id="fromError"></div>' +
        "</div>" +
        '<div class="form-field">' +
        '  <label class="form-label" for="toInput">Куда</label>' +
        '  <div class="picker-row" id="toPickerRow">' +
        '    <div class="picker-value" id="toValue">' +
        escapeHtml(moveToValue) +
        "</div>" +
        '    <button class="btn btn-outline picker-btn" id="toPickBtn" type="button" ' +
        (isDraft ? "" : "disabled") +
        ">Выбрать...</button>" +
        "  </div>" +
        '  <input class="form-input picker-fallback" id="toInput" data-header="to" data-location-field="to" type="text" value="' +
        escapeHtml(header.to || "") +
        "" +
        '" ' +
        (isDraft ? "" : "disabled") +
        " />" +
        '<div class="field-error" id="toError"></div>' +
        "</div>"
      );
    }
    if (doc.op === "WRITE_OFF") {
      var reasonOptions = REASON_CODES.map(function (code) {
        var selected = header.reason_code === code ? "selected" : "";
        return '<option value="' + code + '" ' + selected + ">" + code + "</option>";
      }).join("");
      var writeoffFromValue = formatLocationLabel(header.from, header.from_name);
      return (
        '<div class="form-field">' +
        '  <label class="form-label" for="fromInput">Откуда</label>' +
        '  <div class="picker-row" id="fromPickerRow">' +
        '    <div class="picker-value" id="fromValue">' +
        escapeHtml(writeoffFromValue) +
        "</div>" +
        '    <button class="btn btn-outline picker-btn" id="fromPickBtn" type="button" ' +
        (isDraft ? "" : "disabled") +
        ">Выбрать...</button>" +
        "  </div>" +
        '  <input class="form-input picker-fallback" id="fromInput" data-header="from" data-location-field="from" type="text" value="' +
        escapeHtml(header.from || "") +
        "" +
        '" ' +
        (isDraft ? "" : "disabled") +
        " />" +
        '<div class="field-error" id="fromError"></div>' +
        "</div>" +
        '<div class="form-field">' +
        '  <label class="form-label" for="reasonSelect">Причина</label>' +
        '  <select class="form-input" id="reasonSelect" data-header="reason_code" ' +
        (isDraft ? "" : "disabled") +
        ">" +
        reasonOptions +
        "</select>" +
        "</div>"
      );
    }

    if (doc.op === "INVENTORY") {
      var inventoryValue = formatLocationLabel(header.location, header.location_name);
      return (
        '<div class="form-field">' +
        '  <label class="form-label" for="locationInput">Локация</label>' +
        '  <div class="picker-row" id="locationPickerRow">' +
        '    <div class="picker-value" id="locationValue">' +
        escapeHtml(inventoryValue) +
        "</div>" +
        '    <button class="btn btn-outline picker-btn" id="locationPickBtn" type="button" ' +
        (isDraft ? "" : "disabled") +
        ">Выбрать...</button>" +
        "  </div>" +
        '  <input class="form-input picker-fallback" id="locationInput" data-header="location" data-location-field="location" type="text" value="' +
        escapeHtml(header.location || "") +
        "" +
        '" ' +
        (isDraft ? "" : "disabled") +
        " />" +
        '<div class="field-error" id="locationError"></div>' +
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
      '  <input class="form-input scan-input" id="barcodeInput" type="text" ' +
      (isDraft ? "" : "disabled") +
      " />" +
      '  <div id="scanItemInfo" class="scan-info"></div>' +
      '  <div class="scan-actions">' +
      '    <div class="qty-indicator">Шаг: <span id="qtyStepValue">1</span> шт</div>' +
      '    <div class="qty-buttons">' +
      '      <button class="btn qty-btn" data-step="1" type="button">1</button>' +
      '      <button class="btn qty-btn" data-step="6" type="button">6</button>' +
      '      <button class="btn qty-btn" data-step="10" type="button">10</button>' +
      '      <button class="btn qty-btn" data-step="12" type="button">12</button>' +
      '      <button class="btn qty-btn" data-step="15" type="button">15</button>' +
      "    </div>" +
      "  </div>" +
      '  <button class="btn primary-btn" id="addLineBtn" type="button" ' +
      (isDraft ? "" : "disabled") +
      ">Добавить</button>" +
      "</div>"
    );
  }

  function renderLines(doc) {
    var lines = doc.lines || [];
    if (!lines.length) {
      return '<div class="empty-state">Строк пока нет.</div>';
    }

    var rows = lines
      .map(function (line, index) {
        var nameText = line.itemName ? line.itemName : "Неизвестный код";
        return (
          '<div class="lines-row">' +
          '  <div class="lines-cell">' +
          '    <div class="line-name">' +
          escapeHtml(nameText) +
          "</div>" +
          '    <div class="line-barcode">' +
          escapeHtml(line.barcode) +
          "</div>" +
          "</div>" +
          '  <div class="lines-cell">' +
          escapeHtml(line.qty) +
          " шт</div>" +
          '  <div class="lines-cell">' +
          '    <button class="btn btn-danger line-delete" data-index="' +
          index +
          '">Удалить</button>' +
          "  </div>" +
          "</div>"
        );
      })
      .join("");

    return (
      '<div class="lines-table">' +
      '  <div class="lines-header">' +
      '    <div class="lines-cell">Товар</div>' +
      '    <div class="lines-cell">Кол-во</div>' +
      '    <div class="lines-cell"></div>' +
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
        createDocAndOpen(op);
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
        navigate("/doc/" + encodeURIComponent(docId));
      });
    });
  }

  function wireNewOp() {
    var buttons = document.querySelectorAll("[data-op]");
    buttons.forEach(function (btn) {
      btn.addEventListener("click", function () {
        var op = btn.getAttribute("data-op");
        createDocAndOpen(op);
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
    var overlay = buildOverlay("Контрагенты");
    var input = overlay.querySelector(".overlay-search");
    var recentsEl = overlay.querySelector(".overlay-recents");
    var resultsEl = overlay.querySelector(".overlay-results");
    var closeBtn = overlay.querySelector(".overlay-close");

    function close() {
      document.body.removeChild(overlay);
      document.removeEventListener("keydown", onKeyDown);
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
    var overlay = buildOverlay("Локации");
    var input = overlay.querySelector(".overlay-search");
    var recentsEl = overlay.querySelector(".overlay-recents");
    var resultsEl = overlay.querySelector(".overlay-results");
    var closeBtn = overlay.querySelector(".overlay-close");

    function close() {
      document.body.removeChild(overlay);
      document.removeEventListener("keydown", onKeyDown);
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
    var qtyIndicator = document.getElementById("qtyStepValue");
    var barcodeInput = document.getElementById("barcodeInput");
    var addLineBtn = document.getElementById("addLineBtn");
    var undoBtn = document.getElementById("undoBtn");
    var finishBtn = document.getElementById("finishBtn");
    var revertBtn = document.getElementById("revertBtn");
    var backToDocsBtn = document.getElementById("backToDocsBtn");
    var docRefInput = document.getElementById("docRefInput");
    var headerInputs = document.querySelectorAll("[data-header]");
    var qtyButtons = document.querySelectorAll(".qty-btn");
    var deleteButtons = document.querySelectorAll(".line-delete");
    var partnerPickBtn = document.getElementById("partnerPickBtn");
    var toPickBtn = document.getElementById("toPickBtn");
    var fromPickBtn = document.getElementById("fromPickBtn");
    var locationPickBtn = document.getElementById("locationPickBtn");
    var partnerPickerRow = document.getElementById("partnerPickerRow");
    var toPickerRow = document.getElementById("toPickerRow");
    var fromPickerRow = document.getElementById("fromPickerRow");
    var locationPickerRow = document.getElementById("locationPickerRow");
    var partnerInput = document.getElementById("partnerInput");
    var toInput = document.getElementById("toInput");
    var fromInput = document.getElementById("fromInput");
    var locationInput = document.getElementById("locationInput");
    var scanItemInfo = document.getElementById("scanItemInfo");
    var dataStatus = null;
    var lookupToken = 0;
    var qtyModeButtons = document.querySelectorAll(".qty-mode-btn");
    var qtyOverlay = null;
    var qtyOverlayKeyListener = null;

    function updateQtyIndicator() {
      if (qtyIndicator) {
        qtyIndicator.textContent = qtyStep;
      }
    }

    function setScanInfo(text, isUnknown) {
      if (!scanItemInfo) {
        return;
      }
      scanItemInfo.textContent = text || "";
      scanItemInfo.classList.toggle("scan-info-unknown", !!isUnknown);
    }

    function focusBarcode() {
      if (barcodeInput && !barcodeInput.disabled) {
        barcodeInput.focus();
      }
    }

    function applyCatalogState(status) {
      var hasPartners = status && status.counts && status.counts.partners > 0;
      var hasLocations = status && status.counts && status.counts.locations > 0;

      if (partnerPickerRow && partnerInput) {
        partnerPickerRow.classList.toggle("is-hidden", !hasPartners);
        partnerInput.classList.toggle("is-hidden", hasPartners);
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
      if (locationPickBtn) {
        locationPickBtn.disabled = !hasLocations || doc.status !== "DRAFT";
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
          finalizeLine(item);
        })
        .catch(function () {
          finalizeLine(null);
        });

      function finalizeLine(item) {
        var itemId = item ? item.itemId : null;
        var itemName = item ? item.name : null;
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

    function focusFirstLocationOrBarcode() {
      var candidateFields = [];
      if (doc.op === "INBOUND") {
        candidateFields = ["to"];
      } else if (doc.op === "OUTBOUND") {
        candidateFields = ["from"];
      } else if (doc.op === "MOVE") {
        candidateFields = ["from", "to"];
      } else if (doc.op === "WRITE_OFF") {
        candidateFields = ["from"];
      } else if (doc.op === "INVENTORY") {
        candidateFields = ["location"];
      }
      for (var i = 0; i < candidateFields.length; i += 1) {
        var field = candidateFields[i];
        if (!normalizeValue(doc.header[field])) {
          var element = document.getElementById(field + "Input");
          if (element) {
            element.focus();
            return;
          }
        }
      }
      focusBarcode();
    }

    function handleLocationEntry(field) {
      if (doc.status !== "DRAFT") {
        return;
      }
      var inputEl = document.getElementById(field + "Input");
      var value = inputEl ? inputEl.value.trim() : "";
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

    function handleAddLine() {
      if (doc.status !== "DRAFT") {
        return;
      }
      var barcode = barcodeInput ? barcodeInput.value.trim() : "";
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

    updateQtyIndicator();

    if (barcodeInput) {
      barcodeInput.addEventListener("input", function () {
        var value = barcodeInput.value.trim();
        if (!value) {
          setScanInfo("", false);
          return;
        }
        var token = (lookupToken += 1);
        TsdStorage.findItemByCode(value)
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
        if (!mode || doc.header.qtyMode === mode) {
          return;
        }
        doc.header.qtyMode = mode;
        saveDocState().then(refreshDocView);
      });
    });

    qtyButtons.forEach(function (btn) {
      btn.addEventListener("click", function () {
        qtyStep = parseInt(btn.getAttribute("data-step"), 10) || 1;
        updateQtyIndicator();
        focusBarcode();
      });
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
      btn.addEventListener("click", function () {
        if (doc.status !== "DRAFT") {
          return;
        }
        var index = parseInt(btn.getAttribute("data-index"), 10);
        if (isNaN(index)) {
          return;
        }
        if (!confirm("Удалить строку?")) {
          return;
        }
        doc.lines.splice(index, 1);
        saveDocState().then(refreshDocView);
      });
    });

    function applyPartnerSelection(partner) {
      doc.header.partner_id = partner.partnerId;
      doc.header.partner = partner.name || "";
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
    if (finishBtn) {
      finishBtn.addEventListener("click", function () {
        if (doc.status !== "DRAFT") {
          return;
        }
        if (!isHeaderComplete(doc.op, doc.header)) {
          alert("Заполните обязательные поля шапки.");
          return;
        }
        doc.status = "READY";
        saveDocState().then(refreshDocView);
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
  }

  function createDocAndOpen(op) {
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
        reason_code: REASON_CODES[0],
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
            if (currentRoute.name === "home") {
              navigate("/docs");
            } else if (currentRoute.name === "docs") {
              navigate("/");
            } else if (currentRoute.name === "doc" || currentRoute.name === "new" || currentRoute.name === "stock") {
              navigate("/docs");
            } else {
              navigate("/");
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
