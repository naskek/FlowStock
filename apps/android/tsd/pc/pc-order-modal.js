(function () {
  "use strict";

  var deps = {};
  var openOrderModalController = null;
  var fetchJson;
  var escapeHtml;
  var formatDate;
  var formatQuantity;
  var isInternalOrder;
  var isShippedOrder;
  var getShipmentReadiness;
  var renderReadinessBadge;
  var applyOrderReadinessFromLines;
  var translatePalletStatus;
  var getOrderLineHighlightState;
  var renderLinePalletFillingBadge;
  var getOrderTypeLabel;

  function init(shared) {
    deps = shared || {};
    fetchJson = deps.fetchJson;
    escapeHtml = deps.escapeHtml;
    formatDate = deps.formatDate;
    formatQuantity = deps.formatQuantity;
    isInternalOrder = deps.isInternalOrder;
    isShippedOrder = deps.isShippedOrder;
    getShipmentReadiness = deps.getShipmentReadiness;
    renderReadinessBadge = deps.renderReadinessBadge;
    applyOrderReadinessFromLines = deps.applyOrderReadinessFromLines;
    translatePalletStatus = deps.translatePalletStatus;
    getOrderLineHighlightState = deps.getOrderLineHighlightState;
    renderLinePalletFillingBadge = deps.renderLinePalletFillingBadge;
    getOrderTypeLabel = deps.getOrderTypeLabel;
  }

  function clearOpenOrderModalController() {
    openOrderModalController = null;
  }

  function refreshOpenOrderModalIfNeeded() {
    var controller = openOrderModalController;
    if (!controller || typeof controller.refresh !== "function") {
      return;
    }
    if (controller.modal && !controller.modal.isConnected) {
      clearOpenOrderModalController();
      return;
    }
    controller.refresh();
  }

  function hasOpenOrderModal() {
    return !!openOrderModalController;
  }

  function getOpenOrderModalController() {
    return openOrderModalController;
  }

  function setOpenOrderModalControllerForTest(controller) {
    openOrderModalController = controller || null;
  }
  function getOrderModalContentUpdates(order, lines, expandedOrderLineIds) {
    var isPending = order && order.is_pending_confirmation;
    var isInternal = isInternalOrder(order);
    var showAvailableColumn = !isShippedOrder(order);
    var sourceLines = Array.isArray(lines) ? lines : [];
    var readiness = !isPending && !isInternal && showAvailableColumn ? getShipmentReadiness(sourceLines) : null;

    return {
      datesText:
        "План: " +
        formatDate(order.due_date) +
        " · Факт: " +
        formatDate(order.shipped_at),
      readinessBadgeHtml: renderReadinessBadge(readiness),
      linesHtml: sourceLines.length
        ? renderOrderLinesTable(sourceLines, order, expandedOrderLineIds)
        : "<div>Строк нет.</div>",
    };
  }

  function applyOrderModalContentUpdates(modal, updates) {
    if (!modal || !updates) {
      return;
    }

    var datesEl = modal.querySelector("#orderDatesStatus");
    if (datesEl) {
      datesEl.textContent = updates.datesText;
    }

    var readinessBadge = modal.querySelector("#orderReadinessBadge");
    if (readinessBadge) {
      readinessBadge.outerHTML = updates.readinessBadgeHtml || '<span id="orderReadinessBadge"></span>';
    }

    var wrap = modal.querySelector("#orderLinesWrap");
    if (wrap) {
      wrap.innerHTML = updates.linesHtml;
    }
  }

  function loadOrderModalData(order) {
    var isPending = order && order.is_pending_confirmation;
    if (isPending) {
      return Promise.resolve({
        order: order,
        lines: Array.isArray(order.lines) ? order.lines : [],
      });
    }

    return Promise.all([
      fetchJson("/api/orders/" + encodeURIComponent(order.id)),
      fetchJson("/api/orders/" + encodeURIComponent(order.id) + "/lines"),
    ]).then(function (results) {
      var freshOrder = results[0] || order;
      var lines = Array.isArray(results[1]) ? results[1] : [];
      applyOrderReadinessFromLines(freshOrder, lines);
      return {
        order: freshOrder,
        lines: lines,
      };
    });
  }

  function populateOrderModalContent(modal, order, lines) {
    var controller =
      openOrderModalController && openOrderModalController.modal === modal
        ? openOrderModalController
        : null;
    var expandedOrderLineIds = controller ? controller.expandedOrderLineIds : {};
    if (controller) {
      controller.lines = Array.isArray(lines) ? lines : [];
    }
    applyOrderModalContentUpdates(modal, getOrderModalContentUpdates(order, lines, expandedOrderLineIds));
    bindOrderLineExpansion(modal, order, lines, expandedOrderLineIds);
  }

  function createOrderModalRefreshHandler(modal, order) {
    var refreshInFlight = false;

    return function refreshOrderModalContent() {
      if (!modal || !modal.isConnected) {
        clearOpenOrderModalController();
        return Promise.resolve();
      }
      if (refreshInFlight) {
        return Promise.resolve();
      }

      refreshInFlight = true;
      return loadOrderModalData(order)
        .then(function (payload) {
          if (!modal.isConnected) {
            clearOpenOrderModalController();
            return;
          }

          var freshOrder = payload && payload.order ? payload.order : order;
          if (freshOrder && freshOrder !== order) {
            Object.keys(freshOrder).forEach(function (key) {
              order[key] = freshOrder[key];
            });
          }

          populateOrderModalContent(modal, order, payload.lines);
        })
        .catch(function () {
          var wrap = modal.querySelector("#orderLinesWrap");
          if (wrap) {
            wrap.textContent = "Ошибка загрузки строк.";
          }
        })
        .finally(function () {
          refreshInFlight = false;
        });
    };
  }

  function renderOrderHuRowsTable(rows, columns, emptyText) {
    var sourceRows = Array.isArray(rows) ? rows : [];
    if (!sourceRows.length) {
      return '<div class="pc-order-line-detail-empty">' + escapeHtml(emptyText || "Нет данных") + "</div>";
    }

    var head = columns
      .map(function (column) {
        return "<th>" + escapeHtml(column.label) + "</th>";
      })
      .join("");
    var body = sourceRows
      .map(function (row) {
        return (
          "<tr>" +
          columns
            .map(function (column) {
              return "<td>" + escapeHtml(column.value(row)) + "</td>";
            })
            .join("") +
          "</tr>"
        );
      })
      .join("");
    return (
      '<div class="pc-order-line-detail-table-wrap">' +
      '<table class="pc-table pc-order-line-detail-table"><thead><tr>' +
      head +
      "</tr></thead><tbody>" +
      body +
      "</tbody></table></div>"
    );
  }

  function renderOrderLineCoverage(line, order) {
    var coverage = line && line.coverage;
    var ordered = Number(coverage && coverage.ordered_qty);
    var covered = Number(coverage && coverage.covered_qty);
    var missing = Number(coverage && coverage.missing_qty);
    if (
      !coverage ||
      typeof coverage !== "object" ||
      isNaN(ordered) ||
      isNaN(covered) ||
      isNaN(missing)
    ) {
      var hasExistingShortage = !!(line && line.shortage != null);
      var existingShortage = hasExistingShortage ? Number(line.shortage) : NaN;
      return (
        '<div class="pc-order-line-detail-empty">Точный итог покрытия недоступен.</div>' +
        (!isNaN(existingShortage)
          ? '<div class="pc-order-line-existing-shortage">Существующий серверный дефицит: ' +
            escapeHtml(formatQuantity(existingShortage)) +
            "</div>"
          : "")
      );
    }

    var toneClass = missing <= 0.000001 ? " is-covered" : " is-missing";
    var coveredLabel = isInternalOrder(order) ? "Выпущено" : "Покрыто";
    var missingLabel = isInternalOrder(order) ? "Осталось выпустить" : "Не хватает";
    return (
      '<div class="pc-order-line-coverage-grid">' +
      '<div><span>Заказано</span><strong>' +
      escapeHtml(formatQuantity(ordered)) +
      '</strong></div><div><span>' +
      coveredLabel +
      "</span><strong>" +
      escapeHtml(formatQuantity(covered)) +
      "</strong></div>" +
      '<div class="pc-order-line-missing' +
      toneClass +
      '"><span>' +
      missingLabel +
      "</span><strong>" +
      escapeHtml(formatQuantity(missing)) +
      "</strong></div>" +
      "</div>"
    );
  }

  function formatProductionHuFate(row, order) {
    if (!row || !row.fate_label) {
      return "—";
    }

    var fateOrderRef = String(row.fate_order_ref || "").trim();
    var currentOrderRef = String(order && order.order_ref ? order.order_ref : "").trim();
    var isTransferredToAnotherOrder =
      String(row.fate_code || "").trim().toUpperCase() === "SHIPPED" &&
      fateOrderRef &&
      currentOrderRef &&
      fateOrderRef !== currentOrderRef;
    var parts = [
      isTransferredToAnotherOrder ? "Передано в заказ " + fateOrderRef : String(row.fate_label),
    ];
    if (row.fate_doc_ref) {
      parts.push("OUT: " + String(row.fate_doc_ref));
    }
    if (row.fate_qty != null && !isNaN(Number(row.fate_qty))) {
      parts.push(formatQuantity(row.fate_qty));
    }
    return parts.join(" · ");
  }

  function renderOrderLineDetails(line, order) {
    var warehouseRows = Array.isArray(line && line.warehouse_hu_rows) ? line.warehouse_hu_rows : [];
    var productionRows = Array.isArray(line && line.production_hu_rows) ? line.production_hu_rows : [];
    var shippedRows = Array.isArray(line && line.shipped_hu_rows) ? line.shipped_hu_rows : [];
    var hasHuRows = warehouseRows.length || productionRows.length || shippedRows.length;
    var isInternal = isInternalOrder(order);
    var customerHuRows = warehouseRows.concat(
      shippedRows.map(function (row) {
        return {
          hu_code: row.hu_code,
          qty: row.qty,
          display_status: "Отгружен",
        };
      })
    );
    var customerHuColumns = [
      { label: "HU", value: function (row) { return row.hu_code || "-"; } },
      { label: "Кол-во", value: function (row) { return formatQuantity(row.qty || 0); } },
      {
        label: "Локация",
        value: function (row) {
          return row.location_name || row.location_code || "-";
        },
      },
      {
        label: "Статус",
        value: function (row) {
          return row.display_status ||
            (row.stock_status === "LEDGER_STOCK" ? "На складе" : row.stock_status || "-");
        },
      },
      {
        label: "Привязка",
        value: function (row) {
          return row.is_bound_to_order ? "Резерв этого заказа" : "-";
        },
      },
    ];

    return (
      '<div class="pc-order-line-detail-block">' +
      (isInternal && !hasHuRows ? '<div class="pc-order-line-no-hu">HU не привязаны</div>' : "") +
      (!isInternal
        ? '<section class="pc-order-line-detail-section"><div class="pc-order-line-detail-title">HU по строке заказа</div>' +
          renderOrderHuRowsTable(customerHuRows, customerHuColumns, "HU не привязаны") +
          "</section>"
        : "") +
      '<section class="pc-order-line-detail-section"><div class="pc-order-line-detail-title">Производство / план паллет</div>' +
      renderOrderHuRowsTable(
        productionRows,
        [
          { label: "HU", value: function (row) { return row.hu_code || "-"; } },
          { label: "Статус", value: function (row) { return translatePalletStatus(row.pallet_status); } },
          { label: "План", value: function (row) { return formatQuantity(row.planned_qty || 0); } },
          { label: "Наполнено", value: function (row) { return formatQuantity(row.filled_qty || 0); } },
          { label: "PRD", value: function (row) { return row.prd_ref || "-"; } },
          {
            label: "Движение HU",
            value: function (row) {
              return formatProductionHuFate(row, order);
            },
          },
        ],
        "Производственные HU отсутствуют"
      ) +
      "</section>" +
      (!isInternal
        ? '<section class="pc-order-line-detail-section"><div class="pc-order-line-detail-title">Отгрузка этой строки заказа</div>' +
          '<div class="pc-order-line-shipped-summary">Отгружено по строке: ' +
          escapeHtml(
            formatQuantity(
              line && line.coverage && line.coverage.shipped_qty != null
                ? line.coverage.shipped_qty
                : line && line.qty_shipped != null
                  ? line.qty_shipped
                  : 0
            )
          ) +
          "</div>" +
          renderOrderHuRowsTable(
            shippedRows,
            [
              { label: "HU", value: function (row) { return row.hu_code || "-"; } },
              { label: "Отгружено", value: function (row) { return formatQuantity(row.qty || 0); } },
            ],
            "По этой строке заказа отгрузки нет"
          ) +
          "</section>"
        : "") +
      '<section class="pc-order-line-detail-section"><div class="pc-order-line-detail-title">' +
      (isInternal ? "Итог выпуска" : "Итог") +
      "</div>" +
      renderOrderLineCoverage(line, order) +
      "</section>" +
      "</div>"
    );
  }

  function bindOrderLineExpansion(modal, order, lines, expandedOrderLineIds) {
    if (!modal || typeof modal.querySelectorAll !== "function") {
      return;
    }
    var sourceLines = Array.isArray(lines) ? lines : [];
    var rows = modal.querySelectorAll("[data-order-line-toggle]");
    rows.forEach(function (row) {
      function toggleRow() {
        var lineId = Number(row.getAttribute("data-order-line-toggle")) || 0;
        if (!lineId) {
          return;
        }
        expandedOrderLineIds[lineId] = !expandedOrderLineIds[lineId];
        populateOrderModalContent(modal, order, sourceLines);
      }

      row.addEventListener("click", toggleRow);
      row.addEventListener("keydown", function (event) {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          toggleRow();
        }
      });
    });
  }

  function renderOrderLinesTable(lines, order, expandedOrderLineIds) {
    var body = (Array.isArray(lines) ? lines : [])
      .map(function (line) {
        var lineId = Number(line && line.id) || 0;
        var isExpanded = !!(expandedOrderLineIds && expandedOrderLineIds[lineId]);
        var highlightState = getOrderLineHighlightState(line, order);
        var rowClass = "pc-order-line-parent-row" + (highlightState.className ? " " + highlightState.className : "");
        var detailRow = isExpanded
          ? '<tr class="pc-order-line-detail-row"><td colspan="6">' +
            renderOrderLineDetails(line, order) +
            "</td></tr>"
          : "";
        return (
          '<tr class="' +
          escapeHtml(rowClass) +
          '" title="' +
          escapeHtml(highlightState.title || "") +
          '" data-order-line-toggle="' +
          escapeHtml(String(lineId)) +
          '" tabindex="0" role="button" aria-expanded="' +
          (isExpanded ? "true" : "false") +
          '">' +
          '<td class="pc-order-line-caret-cell"><span class="pc-order-line-caret' +
          (isExpanded ? " is-expanded" : "") +
          '">▸</span></td>' +
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
          escapeHtml(formatQuantity(line.qty_ordered || 0)) +
          "</td>" +
          "<td>" +
          renderLinePalletFillingBadge(line, order) +
          "</td>" +
          "</tr>" +
          detailRow
        );
      })
      .join("");

    return (
      '<div class="pc-order-lines-table-wrap">' +
      '<table class="pc-table pc-order-lines-table">' +
      "<colgroup>" +
      '<col class="pc-order-lines-col-caret" />' +
      '<col class="pc-order-lines-col-item" />' +
      '<col class="pc-order-lines-col-sku" />' +
      '<col class="pc-order-lines-col-gtin" />' +
      '<col class="pc-order-lines-col-ordered" />' +
      '<col class="pc-order-lines-col-filling" />' +
      "</colgroup>" +
      "<thead><tr>" +
      '<th aria-label="Раскрыть строку"></th>' +
      "<th>Товар</th>" +
      "<th>SKU / ШК</th>" +
      "<th>GTIN</th>" +
      "<th>Заказано</th>" +
      "<th>Наполнение</th>" +
      "</tr></thead>" +
      "<tbody>" +
      body +
      "</tbody>" +
      "</table>" +
      "</div>"
    );
  }

  function openOrderModal(order, onSubmitted) {
    var isPending = order && order.is_pending_confirmation;
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
      '  <div class="pc-status" id="orderDatesStatus">План: ' +
      escapeHtml(formatDate(order.due_date)) +
      " · Факт: " +
      escapeHtml(formatDate(order.shipped_at)) +
      "</div>" +
      '  <div class="pc-status">Комментарий: ' +
      escapeHtml(order && order.comment ? order.comment : "-") +
      "</div>" +
      '  <div class="pc-order-status-box">' +
      '    <div class="pc-status">' +
      (isPending
        ? "Заказ ожидает подтверждения в WPF."
        : "Статус формируется автоматически по выпуску и отгрузке.") +
      "</div>" +
      "  </div>" +
      '  <div id="orderLinesWrap" class="pc-status" style="margin-top:12px;">Загрузка строк...</div>' +
      "</div>";
    document.body.appendChild(modal);

    function close() {
      if (openOrderModalController && openOrderModalController.modal === modal) {
        clearOpenOrderModalController();
      }
      if (modal.parentNode) {
        modal.parentNode.removeChild(modal);
      }
    }

    var closeBtn = modal.querySelector("#modalCloseBtn");
    if (closeBtn) {
      closeBtn.addEventListener("click", close);
    }

    var refreshOrderModalContent = createOrderModalRefreshHandler(modal, order);
    openOrderModalController = {
      orderId: order.id,
      modal: modal,
      order: order,
      lines: [],
      expandedOrderLineIds: {},
      refresh: refreshOrderModalContent,
      close: close,
    };

    refreshOrderModalContent();
  }

  window.FlowStockPcOrderModal = {
    init: init,
    openOrderModal: openOrderModal,
    renderOrderLinesTable: renderOrderLinesTable,
    getOrderModalContentUpdates: getOrderModalContentUpdates,
    applyOrderModalContentUpdates: applyOrderModalContentUpdates,
    refreshOpenOrderModalIfNeeded: refreshOpenOrderModalIfNeeded,
    clearOpenOrderModalController: clearOpenOrderModalController,
    hasOpenOrderModal: hasOpenOrderModal,
    getOpenOrderModalController: getOpenOrderModalController,
    __setOpenOrderModalControllerForTest: setOpenOrderModalControllerForTest,
  };
})();