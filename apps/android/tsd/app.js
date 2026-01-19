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
      '    <button class="btn menu-btn" data-op="INVENTORY">Остатки</button>' +
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
      return (
        '<div class="form-field">' +
        '  <label class="form-label" for="partnerInput">Поставщик</label>' +
        '  <input class="form-input" id="partnerInput" data-header="partner" type="text" value="' +
        escapeHtml(header.partner || "") +
        "" +
        '" ' +
        (isDraft ? "" : "disabled") +
        " />" +
        "</div>" +
        '<div class="form-field">' +
        '  <label class="form-label" for="toInput">Куда</label>' +
        '  <input class="form-input" id="toInput" data-header="to" type="text" value="' +
        escapeHtml(header.to || "") +
        "" +
        '" ' +
        (isDraft ? "" : "disabled") +
        " />" +
        "</div>"
      );
    }

    if (doc.op === "OUTBOUND") {
      return (
        '<div class="form-field">' +
        '  <label class="form-label" for="partnerInput">Покупатель</label>' +
        '  <input class="form-input" id="partnerInput" data-header="partner" type="text" value="' +
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
        '  <input class="form-input" id="fromInput" data-header="from" type="text" value="' +
        escapeHtml(header.from || "") +
        "" +
        '" ' +
        (isDraft ? "" : "disabled") +
        " />" +
        "</div>"
      );
    }

    if (doc.op === "MOVE") {
      return (
        '<div class="form-field">' +
        '  <label class="form-label" for="fromInput">Откуда</label>' +
        '  <input class="form-input" id="fromInput" data-header="from" type="text" value="' +
        escapeHtml(header.from || "") +
        "" +
        '" ' +
        (isDraft ? "" : "disabled") +
        " />" +
        "</div>" +
        '<div class="form-field">' +
        '  <label class="form-label" for="toInput">Куда</label>' +
        '  <input class="form-input" id="toInput" data-header="to" type="text" value="' +
        escapeHtml(header.to || "") +
        "" +
        '" ' +
        (isDraft ? "" : "disabled") +
        " />" +
        "</div>"
      );
    }
    if (doc.op === "WRITE_OFF") {
      var reasonOptions = REASON_CODES.map(function (code) {
        var selected = header.reason_code === code ? "selected" : "";
        return '<option value="' + code + '" ' + selected + ">" + code + "</option>";
      }).join("");
      return (
        '<div class="form-field">' +
        '  <label class="form-label" for="fromInput">Откуда</label>' +
        '  <input class="form-input" id="fromInput" data-header="from" type="text" value="' +
        escapeHtml(header.from || "") +
        "" +
        '" ' +
        (isDraft ? "" : "disabled") +
        " />" +
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
      return (
        '<div class="form-field">' +
        '  <label class="form-label" for="locationInput">Локация</label>' +
        '  <input class="form-input" id="locationInput" data-header="location" type="text" value="' +
        escapeHtml(header.location || "") +
        "" +
        '" ' +
        (isDraft ? "" : "disabled") +
        " />" +
        "</div>"
      );
    }

    return "";
  }

  function renderScanBlock(doc, isDraft) {
    return (
      '<div class="scan-card">' +
      '  <label class="form-label" for="barcodeInput">Штрихкод</label>' +
      '  <input class="form-input scan-input" id="barcodeInput" type="text" ' +
      (isDraft ? "" : "disabled") +
      " />" +
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
        return (
          '<div class="lines-row">' +
          '  <div class="lines-cell">' +
          escapeHtml(line.barcode) +
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
      '    <div class="lines-cell">Штрихкод</div>' +
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

  function wireDoc(doc) {
    doc.lines = doc.lines || [];
    doc.undoStack = doc.undoStack || [];
    doc.header = doc.header || getDefaultHeader(doc.op);

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

    function updateQtyIndicator() {
      if (qtyIndicator) {
        qtyIndicator.textContent = qtyStep;
      }
    }

    function focusBarcode() {
      if (barcodeInput && !barcodeInput.disabled) {
        barcodeInput.focus();
      }
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

      var lineData = buildLineData(doc.op, doc.header);
      var lineIndex = findLineIndex(doc.op, doc.lines, barcode, lineData);
      if (lineIndex >= 0) {
        doc.lines[lineIndex].qty += qtyStep;
      } else {
        doc.lines.push({
          barcode: barcode,
          qty: qtyStep,
          from: lineData.from,
          to: lineData.to,
          reason_code: lineData.reason_code,
        });
      }

      doc.undoStack.push({
        barcode: barcode,
        qtyDelta: qtyStep,
        from: lineData.from,
        to: lineData.to,
        reason_code: lineData.reason_code,
      });

      if (barcodeInput) {
        barcodeInput.value = "";
      }

      saveDocState().then(refreshDocView);
    }

    updateQtyIndicator();

    if (barcodeInput) {
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
        saveDocState();
      };

      if (input.tagName === "SELECT") {
        input.addEventListener("change", handler);
      } else {
        input.addEventListener("input", handler);
      }
    });

    focusBarcode();
  }

  function wireSettings() {
    var input = document.getElementById("deviceIdInput");
    var status = document.getElementById("saveStatus");
    var saveBtn = document.getElementById("saveSettingsBtn");
    var exportBtn = document.getElementById("exportFromSettingsBtn");

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
      return { partner: "", to: "" };
    }
    if (op === "OUTBOUND") {
      return { partner: "", order_ref: "", from: "" };
    }
    if (op === "MOVE") {
      return { from: "", to: "" };
    }
    if (op === "WRITE_OFF") {
      return { from: "", reason_code: REASON_CODES[0] };
    }
    if (op === "INVENTORY") {
      return { location: "" };
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
              partner_id: null,
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
            } else if (currentRoute.name === "doc" || currentRoute.name === "new") {
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
