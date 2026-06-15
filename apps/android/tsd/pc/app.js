(function () {
  "use strict";

  var app = document.getElementById("app");
  var header = document.querySelector ? document.querySelector(".pc-header") : null;
  var tabs = document.querySelectorAll(".pc-tab");
  var logoutBtn = document.getElementById("logoutBtn");

  var currentView = "orders";
  var LAST_VIEW_KEY = "flowstock_pc_last_view";
  var VERSION_CHECK_INTERVAL_MS = 600000;
  var versionCheckTimerId = 0;
  var knownServerVersion = "";
  var liveEventSource = null;
  var liveReconnectTimerId = 0;
  var liveRefreshTimerId = 0;
  var LIVE_RECONNECT_DELAY_MS = 2500;
  var LIVE_REFRESH_DEBOUNCE_MS = 300;
  var ORDERS_PAGE_SIZE = 20;
  var ORDERS_FETCH_LIMIT = ORDERS_PAGE_SIZE + 1;
  var activeLiveRefreshHandler = null;
  var core = window.FlowStockPcCore;
  var clientBlocksRefreshInFlight = false;
  var auth = window.FlowStockPcAuth;
  core.init({
    getCurrentView: function () { return currentView; },
    getBlockKeyForView: getBlockKeyForView,
    handleBlockedClientRequest: handleBlockedClientRequest,
  });
  var fetchJson = core.fetchJson;
  var createRequestHeaders = core.createRequestHeaders;
  var formatDate = core.formatDate;
  var formatDateTime = core.formatDateTime;
  var formatReportQty = core.formatReportQty;
  var formatQuantity = core.formatQuantity;
  var escapeHtml = core.escapeHtml;
  var normalizeSearchQuery = core.normalizeSearchQuery;
  var matchesItemSearch = core.matchesItemSearch;
  var renderSortableHeader = core.renderSortableHeader;
  var sortRows = core.sortRows;
  var bindTableSorting = core.bindTableSorting;
  auth.init({
    fetchJson: fetchJson,
    onLoginSuccess: function () {
      startVersionWatcher();
      startLiveUpdates();
      currentView = resolveAllowedView(currentView) || getDefaultView();
      syncTabsVisibility();
      renderView(currentView);
    },
  });
  var getDefaultClientBlocks = auth.getDefaultClientBlocks;
  var applyClientBlocks = auth.applyClientBlocks;
  var isClientBlockEnabled = auth.isClientBlockEnabled;
  var getClientBlocksSignature = auth.getClientBlocksSignature;
  var normalizePlatform = auth.normalizePlatform;
  var hasPcAccess = auth.hasPcAccess;
  var loadAccount = auth.loadAccount;
  var saveAccount = auth.saveAccount;
  var clearAccount = auth.clearAccount;
  var setAccountLabel = auth.setAccountLabel;
  var setLoginState = auth.setLoginState;
  var apiLogin = auth.apiLogin;
  var loadClientBlocks = auth.loadClientBlocks;
  var renderLogin = auth.renderLogin;
  var wireLogin = auth.wireLogin;
  var orderModal = window.FlowStockPcOrderModal;
  orderModal.init({
    fetchJson: fetchJson,
    escapeHtml: escapeHtml,
    formatDate: formatDate,
    formatQuantity: formatQuantity,
    isInternalOrder: isInternalOrder,
    isShippedOrder: isShippedOrder,
    getShipmentReadiness: getShipmentReadiness,
    renderReadinessBadge: renderReadinessBadge,
    applyOrderReadinessFromLines: applyOrderReadinessFromLines,
    translatePalletStatus: translatePalletStatus,
    getOrderLineHighlightState: getOrderLineHighlightState,
    renderLinePalletFillingBadge: renderLinePalletFillingBadge,
    getOrderTypeLabel: getOrderTypeLabel,
  });
  var openOrderModal = orderModal.openOrderModal;
  var renderOrderLinesTable = orderModal.renderOrderLinesTable;
  var getOrderModalContentUpdates = orderModal.getOrderModalContentUpdates;
  var applyOrderModalContentUpdates = orderModal.applyOrderModalContentUpdates;
  var refreshOpenOrderModalIfNeeded = orderModal.refreshOpenOrderModalIfNeeded;
  var clearOpenOrderModalController = orderModal.clearOpenOrderModalController;
  var hasOpenOrderModal = orderModal.hasOpenOrderModal;
  var catalog = window.FlowStockPcCatalog;
  catalog.init({
    fetchJson: fetchJson,
    renderPageShell: renderPageShell,
    setActiveLiveRefreshHandler: setActiveLiveRefreshHandler,
    escapeHtml: escapeHtml,
    normalizeSearchQuery: normalizeSearchQuery,
    matchesItemSearch: matchesItemSearch,
    renderSortableHeader: renderSortableHeader,
    sortRows: sortRows,
    bindTableSorting: bindTableSorting,
  });
  var stock = window.FlowStockPcStock;
  stock.init({
    fetchJson: fetchJson,
    renderPageShell: renderPageShell,
    setActiveLiveRefreshHandler: setActiveLiveRefreshHandler,
    runProductionNeedCreateOrdersFlow: runProductionNeedCreateOrdersFlow,
    translatePalletStatus: translatePalletStatus,
    escapeHtml: escapeHtml,
    formatReportQty: formatReportQty,
    normalizeSearchQuery: normalizeSearchQuery,
    matchesItemSearch: matchesItemSearch,
    renderSortableHeader: renderSortableHeader,
    sortRows: sortRows,
    bindTableSorting: bindTableSorting,
  });

  function isExperimentalWarehouseTasksEnabled() {
    try {
      return localStorage.getItem("flowstock_experimental_warehouse_tasks") === "1";
    } catch (error) {
      return false;
    }
  }

  function getEnabledViews() {
    var views = [];
    if (isClientBlockEnabled("pc_orders")) {
      views.push("orders");
    }
    if (isClientBlockEnabled("pc_stock")) {
      views.push("stock");
    }
    if (isClientBlockEnabled("pc_catalog")) {
      views.push("catalog");
    }
    if (isExperimentalWarehouseTasksEnabled()) {
      views.push("warehouse-board");
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

  function getDefaultView() {
    return resolveAllowedView("orders") || resolveAllowedView("stock") || "orders";
  }

  function syncTabsVisibility() {
    tabs.forEach(function (tab) {
      var view = tab.getAttribute("data-view") || "";
      var visible =
        (view === "stock" && isClientBlockEnabled("pc_stock")) ||
        (view === "catalog" && isClientBlockEnabled("pc_catalog")) ||
        (view === "orders" && isClientBlockEnabled("pc_orders")) ||
        (view === "warehouse-board" && isExperimentalWarehouseTasksEnabled());
      tab.hidden = !visible;
    });
  }

  function refreshClientBlocksIfChanged() {
    var before = getClientBlocksSignature();
    return loadClientBlocks().then(function () {
      syncTabsVisibility();
      var after = getClientBlocksSignature();
      if (before !== after) {
        currentView = resolveAllowedView(currentView) || getDefaultView();
        renderView(currentView);
      }
      return after !== before;
    });
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
    if (view === "production-need") {
      return "pc_stock";
    }
    if (view === "warehouse-board") {
      return "pc_orders";
    }
    return "";
  }

  function handleBlockedClientRequest() {
    if (clientBlocksRefreshInFlight || !hasPcAccess(loadAccount())) {
      return;
    }

    clientBlocksRefreshInFlight = true;
    loadClientBlocks()
      .then(function () {
        syncTabsVisibility();
        currentView = resolveAllowedView(currentView) || getDefaultView();
        renderView(currentView);
      })
      .finally(function () {
        clientBlocksRefreshInFlight = false;
      });
  }

  function renderPageShell(content) {
    return '<div class="pc-page-shell">' + String(content || "") + "</div>";
  }

  function ensureHeaderShell() {
    if (!header || header.querySelector(".pc-header-inner")) {
      return;
    }
    var inner = document.createElement("div");
    inner.className = "pc-header-inner";
    while (header.firstChild) {
      inner.appendChild(header.firstChild);
    }
    header.appendChild(inner);
  }

  function renderNoAccess() {
    return renderPageShell(
      '<section class="pc-card">' +
      '  <div class="section-title">Доступ ограничен</div>' +
      '  <div class="pc-note">Все блоки ПК-клиента сейчас временно отключены администратором.</div>' +
      "</section>"
    );
  }

  function renderProductionNeed() {
    return renderPageShell(
      '<section class="pc-card">' +
      '  <div class="section-title">Потребность производства</div>' +
      '  <div class="pc-toolbar pc-production-need-toolbar">' +
      '    <div class="pc-toolbar-actions">' +
      '      <button id="productionNeedRefreshBtn" class="btn btn-outline" type="button">Обновить</button>' +
      '      <button id="productionNeedCreateOrdersBtn" class="btn btn-primary" type="button">Сформировать заказ</button>' +
      "    </div>" +
      '    <div id="productionNeedStatus" class="pc-status pc-production-need-status"></div>' +
      "  </div>" +
      '  <div id="productionNeedTableWrap"></div>' +
      "</section>"
    );
  }

  function mapProductionNeedRow(row) {
    return {
      needDate: String((row && row.need_date) || ""),
      itemId: Number(row && row.item_id) || 0,
      gtin: String((row && row.gtin) || ""),
      itemName: String((row && row.item_name) || ""),
      itemType: String((row && (row.item_type || row.item_type_name)) || "Без типа"),
      freeStockQty: Number(row && row.free_stock_qty) || 0,
      minStockQty: Number(row && row.min_stock_qty) || 0,
      toCloseOrdersQty: Number(row && row.to_close_orders_qty) || 0,
      toMinStockQty: Number(row && row.to_min_stock_qty) || 0,
      qtyToCreate: Number(row && row.qty_to_create) || 0,
      canCreateOrder: row && row.can_create_order === true,
      reason: String((row && row.reason) || ""),
      openInternalOrderQty: Number(row && row.open_internal_order_qty) || 0,
      openInternalOrderRefs: String((row && row.open_internal_order_refs) || ""),
      plannedPalletQty: Number(row && row.planned_pallet_qty) || 0,
      filledPalletQty: Number(row && row.filled_pallet_qty) || 0,
      plannedPalletCount: Number(row && row.planned_pallet_count) || 0,
      filledPalletCount: Number(row && row.filled_pallet_count) || 0,
      remainingPalletQty: Number(row && row.remaining_pallet_qty) || 0,
      totalToMakeQty: Number(row && row.total_to_make_qty) || 0,
    };
  }

  function mapProductionNeedPreviewRow(row) {
    return {
      itemId: Number(row && row.item_id) || 0,
      gtin: String((row && row.gtin) || ""),
      itemName: String((row && row.item_name) || ""),
      qtyToCreate: Number(row && row.qty_to_create) || 0,
      reason: String((row && row.reason) || ""),
      minStockQty: Number(row && row.min_stock_qty) || 0,
      freeStockQty: Number(row && row.free_stock_qty) || 0,
      openInternalOrderQty: Number(row && row.open_internal_order_qty) || 0,
      plannedPalletQty: Number(row && row.planned_pallet_qty) || 0,
      filledPalletQty: Number(row && row.filled_pallet_qty) || 0,
    };
  }

  function formatPalletProgress(row) {
    if (!row) {
      return "0";
    }

    if ((Number(row.plannedPalletCount) || 0) > 0) {
      return (
        escapeHtml(String(Number(row.filledPalletCount) || 0)) +
        " / " +
        escapeHtml(String(Number(row.plannedPalletCount) || 0)) +
        " паллет, " +
        escapeHtml(formatReportQty(Number(row.filledPalletQty) || 0)) +
        " шт"
      );
    }

    return escapeHtml(formatReportQty(Number(row.filledPalletQty) || 0));
  }

  function renderProductionNeedTable(rows) {
    if (!rows || !rows.length) {
      return '<div class="empty-state">Потребности производства нет.</div>';
    }

    var body = rows
      .map(function (row) {
        return (
        "<tr>" +
        "<td>" +
        '<div class="pc-production-need-item-name">' +
        escapeHtml(row.itemName || "-") +
        "</div>" +
        '<div class="pc-production-need-item-gtin">' +
        escapeHtml(row.gtin || "-") +
        "</div>" +
        "</td>" +
        '<td class="pc-num">' +
        escapeHtml(formatReportQty(row.freeStockQty)) +
        " / " +
        escapeHtml(formatReportQty(row.minStockQty)) +
        "</td>" +
        '<td class="pc-num">' +
        escapeHtml(formatReportQty(row.toCloseOrdersQty)) +
        "</td>" +
        '<td class="pc-num">' +
        escapeHtml(formatReportQty(row.qtyToCreate || row.toMinStockQty)) +
        "</td>" +
        '<td class="pc-num">' +
        escapeHtml(formatReportQty(row.openInternalOrderQty)) +
        "</td>" +
        '<td class="pc-num">' +
        formatPalletProgress(row) +
        "</td>" +
        '<td class="pc-num"><span class="pc-qty pc-production-need-qty">' +
        escapeHtml(formatReportQty(row.totalToMakeQty)) +
        "</span></td>" +
        "</tr>"
        );
      })
      .join("");

    return (
      '<div class="pc-table-scroll">' +
      '<table class="pc-table pc-production-need-table">' +
      "<thead><tr>" +
      '<th class="pc-production-need-col-item">Номенклатура</th>' +
      '<th class="pc-num pc-production-need-col-stock">Остаток</th>' +
      '<th class="pc-num pc-production-need-col-orders">До закрытия заказов</th>' +
      '<th class="pc-num pc-production-need-col-min">На склад до мин.</th>' +
      '<th class="pc-num pc-production-need-col-open">Во внутренних заказах</th>' +
      '<th class="pc-num pc-production-need-col-pallets">Наполнено паллетами</th>' +
      '<th class="pc-num pc-production-need-col-total">Всего произвести</th>' +
      "</tr></thead>" +
      "<tbody>" +
      body +
      "</tbody>" +
      "</table>" +
      "</div>"
    );
  }

  function loadProductionNeedData(includeZero) {
    var url = "/api/reports/production-need";
    if (includeZero) {
      url += "?include_zero=1";
    }
    return fetchJson(url).then(function (payload) {
      return Array.isArray(payload) ? payload.map(mapProductionNeedRow) : [];
    });
  }

  function getProductionNeedCreateOrdersRefreshUrl() {
    return buildOrdersUrl("", ORDERS_FETCH_LIMIT, 0);
  }

  function loadProductionNeedCreateOrdersPreview() {
    return fetchJson("/api/reports/production-need/create-orders/preview", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({}),
    }).then(function (payload) {
      return {
        message: payload && payload.message ? String(payload.message) : "",
        rows: Array.isArray(payload && payload.rows)
          ? payload.rows.map(mapProductionNeedPreviewRow)
          : []
      };
    });
  }

  function openProductionNeedPreviewModal(rows, onConfirm, onCancel) {
    var modal = document.createElement("div");
    var confirmed = false;
    modal.className = "pc-modal";
    modal.innerHTML =
      '<div class="pc-modal-card pc-order-modal-card">' +
      '  <div class="pc-modal-header">' +
      '    <div class="pc-modal-title">Предпросмотр производственного заказа</div>' +
      '    <button class="btn btn-outline" type="button" id="productionNeedPreviewCloseBtn">Закрыть</button>' +
      "  </div>" +
      '  <div class="pc-status">Количество можно изменить. Строки с 0 не будут созданы.</div>' +
      '  <div class="pc-table-scroll">' +
      '    <table class="pc-table">' +
      "      <thead><tr><th>Номенклатура</th><th>GTIN</th><th>Причина</th><th class=\"pc-num\">Количество</th></tr></thead>" +
      '      <tbody>' +
      rows.map(function (row, index) {
        var previewIndex = String(index);
        return (
          "<tr>" +
          "<td>" + escapeHtml(row.itemName || "-") + "</td>" +
          "<td>" + escapeHtml(row.gtin || "-") + "</td>" +
          "<td>" + escapeHtml(row.reason || "Пополнение склада до минимума") + "</td>" +
          '<td class="pc-num"><input class="form-input pc-production-need-qty-input" type="number" min="0" step="0.001" data-preview-index="' + previewIndex + '" value="' + escapeHtml(String(Number(row.qtyToCreate) || 0)) + '" /></td>' +
          "</tr>"
        );
      }).join("") +
      "      </tbody>" +
      "    </table>" +
      "  </div>" +
      '  <div class="pc-modal-footer">' +
      '    <button class="btn btn-outline" type="button" id="productionNeedPreviewCancelBtn">Отмена</button>' +
      '    <button class="btn btn-primary" type="button" id="productionNeedPreviewConfirmBtn">Подтвердить</button>' +
      "  </div>" +
      "</div>";
    document.body.appendChild(modal);

    function close() {
      if (modal.parentNode) {
        modal.parentNode.removeChild(modal);
      }
      if (!confirmed && onCancel) {
        onCancel();
      }
    }

    modal.querySelector("#productionNeedPreviewCloseBtn").addEventListener("click", close);
    modal.querySelector("#productionNeedPreviewCancelBtn").addEventListener("click", close);
    modal.querySelector("#productionNeedPreviewConfirmBtn").addEventListener("click", function () {
      var requestRows = rows.map(function (row, index) {
        var input = modal.querySelector('[data-preview-index="' + index + '"]');
        var qty = input ? Number(input.value) || 0 : 0;
        return {
          item_id: row.itemId,
          qty_ordered: qty
        };
      }).filter(function (row) {
        return row.qty_ordered > 0;
      });

      if (!requestRows.length) {
        window.alert("Нет строк с количеством больше нуля.");
        return;
      }

      confirmed = true;
      close();
      onConfirm(requestRows);
    });
  }

  function runProductionNeedCreateOrdersFlow(onSuccess, onNoRows) {
    return loadProductionNeedCreateOrdersPreview()
      .then(function (preview) {
        if (!preview.rows.length) {
          if (onNoRows) {
            onNoRows(preview.message || "Нет позиций для создания внутреннего заказа");
          }
          return null;
        }

        return new Promise(function (resolve, reject) {
          openProductionNeedPreviewModal(preview.rows, function (requestRows) {
            fetchJson("/api/production-needs/create-orders", {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ rows: requestRows }),
            })
              .then(function (payload) {
                var message = payload && payload.message
                  ? String(payload.message)
                  : "Производственный черновик сформирован.";
                window.alert(message);
                return Promise.resolve(onSuccess ? onSuccess(message) : null).then(function () {
                  return fetchJson(getProductionNeedCreateOrdersRefreshUrl()).catch(function () {
                    return null;
                  });
                });
              })
              .then(resolve)
              .catch(function (error) {
                window.alert(error && error.message ? error.message : "Не удалось сформировать производственный черновик.");
                reject(error);
              });
          }, function () {
            resolve(null);
          });
        });
      })
      .catch(function (error) {
        window.alert(error && error.message ? error.message : "Не удалось получить предпросмотр производственного черновика.");
        throw error;
      });
  }

  function wireProductionNeed() {
    var refreshBtn = document.getElementById("productionNeedRefreshBtn");
    var createOrdersBtn = document.getElementById("productionNeedCreateOrdersBtn");
    var statusEl = document.getElementById("productionNeedStatus");
    var tableWrap = document.getElementById("productionNeedTableWrap");
    var currentRows = [];

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text || "";
      }
    }

    function renderRows(sourceRows) {
      var rows = Array.isArray(sourceRows) ? sourceRows.slice() : [];
      currentRows = rows.slice();
      rows.sort(function (left, right) {
        var totalCompare = Number(right.totalToMakeQty) - Number(left.totalToMakeQty);
        if (totalCompare !== 0) {
          return totalCompare;
        }

        return String(left.itemName || "").localeCompare(String(right.itemName || ""), "ru");
      });
      if (!tableWrap) {
        return;
      }
      tableWrap.innerHTML = renderProductionNeedTable(rows);
      if (createOrdersBtn) {
        createOrdersBtn.disabled = getCreatableProductionNeedRows(currentRows).length === 0;
      }
    }

    function getCreatableProductionNeedRows(rows) {
      return (rows || []).filter(function (row) {
        return row.canCreateOrder === true && Number(row.qtyToCreate) > 0;
      });
    }

    function openProductionNeedPreviewModal(rows, onConfirm) {
      var modal = document.createElement("div");
      modal.className = "pc-modal";
      modal.innerHTML =
        '<div class="pc-modal-card pc-order-modal-card">' +
        '  <div class="pc-modal-header">' +
        '    <div class="pc-modal-title">Предпросмотр производственного заказа</div>' +
        '    <button class="btn btn-outline" type="button" id="productionNeedPreviewCloseBtn">Закрыть</button>' +
        "  </div>" +
        '  <div class="pc-status">Количество можно изменить. Строки с 0 не будут созданы.</div>' +
        '  <div class="pc-table-scroll">' +
        '    <table class="pc-table">' +
        "      <thead><tr><th>Номенклатура</th><th>GTIN</th><th>Причина</th><th class=\"pc-num\">Количество</th></tr></thead>" +
        '      <tbody>' +
        rows.map(function (row, index) {
          var previewIndex = String(index);
          return (
            "<tr>" +
            "<td>" + escapeHtml(row.itemName || "-") + "</td>" +
            "<td>" + escapeHtml(row.gtin || "-") + "</td>" +
            "<td>" + escapeHtml(row.reason || "Пополнение склада до минимума") + "</td>" +
            '<td class="pc-num"><input class="form-input pc-production-need-qty-input" type="number" min="0" step="0.001" data-preview-index="' + previewIndex + '" value="' + escapeHtml(String(Number(row.qtyToCreate) || 0)) + '" /></td>' +
            "</tr>"
          );
        }).join("") +
        "      </tbody>" +
        "    </table>" +
        "  </div>" +
        '  <div class="pc-modal-footer">' +
        '    <button class="btn btn-outline" type="button" id="productionNeedPreviewCancelBtn">Отмена</button>' +
        '    <button class="btn btn-primary" type="button" id="productionNeedPreviewConfirmBtn">Подтвердить</button>' +
        "  </div>" +
        "</div>";
      document.body.appendChild(modal);

      function close() {
        if (modal.parentNode) {
          modal.parentNode.removeChild(modal);
        }
      }

      modal.querySelector("#productionNeedPreviewCloseBtn").addEventListener("click", close);
      modal.querySelector("#productionNeedPreviewCancelBtn").addEventListener("click", close);
      modal.querySelector("#productionNeedPreviewConfirmBtn").addEventListener("click", function () {
        var requestRows = rows.map(function (row, index) {
          var input = modal.querySelector('[data-preview-index="' + index + '"]');
          var qty = input ? Number(input.value) || 0 : 0;
          return {
            item_id: row.itemId,
            qty_ordered: qty
          };
        }).filter(function (row) {
          return row.qty_ordered > 0;
        });

        if (!requestRows.length) {
          window.alert("Нет строк с количеством больше нуля.");
          return;
        }

        close();
        onConfirm(requestRows);
      });
    }

    function loadProductionNeedPreview() {
      return fetchJson("/api/reports/production-need/create-orders/preview", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({}),
      }).then(function (payload) {
        return {
          message: payload && payload.message ? String(payload.message) : "",
          rows: Array.isArray(payload && payload.rows)
            ? payload.rows.map(mapProductionNeedPreviewRow)
            : []
        };
      });
    }

    function loadAndRender() {
      setStatus("Загрузка...");
      return loadProductionNeedData(false)
        .then(function (rows) {
          renderRows(rows);
          setStatus("Обновлено: " + formatDateTime(new Date()));
        })
        .catch(function () {
          setStatus("Ошибка загрузки отчета");
          if (tableWrap) {
            tableWrap.innerHTML = '<div class="empty-state">Данные недоступны.</div>';
          }
        });
    }

    if (refreshBtn) {
      refreshBtn.addEventListener("click", loadAndRender);
    }

    if (createOrdersBtn) {
      createOrdersBtn.addEventListener("click", function () {
        var creatableRows = getCreatableProductionNeedRows(currentRows);
        if (!creatableRows.length) {
          createOrdersBtn.disabled = true;
          setStatus("Нет позиций для создания внутреннего заказа");
          return;
        }

        createOrdersBtn.disabled = true;
        setStatus("Подготовка предпросмотра...");
        loadProductionNeedPreview()
          .then(function (preview) {
            if (!preview.rows.length) {
              setStatus("Нет позиций для создания внутреннего заказа");
              return;
            }

            openProductionNeedPreviewModal(preview.rows, function (requestRows) {
              createOrdersBtn.disabled = true;
              setStatus("Формирование производственного черновика...");
              fetchJson("/api/production-needs/create-orders", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ rows: requestRows }),
              })
                .then(function (payload) {
                  var message = payload && payload.message
                    ? String(payload.message)
                    : "Производственный черновик сформирован.";
                  window.alert(message);
                  return Promise.all([
                    loadAndRender(),
                    fetchJson(getProductionNeedCreateOrdersRefreshUrl()).catch(function () {
                      return null;
                    }),
                  ]);
                })
                .catch(function (error) {
                  setStatus("Ошибка формирования производственного черновика");
                  window.alert(error && error.message ? error.message : "Не удалось сформировать производственный черновик.");
                })
                .finally(function () {
                  createOrdersBtn.disabled = false;
                });
            });
          })
          .catch(function (error) {
            setStatus("Ошибка предпросмотра производственного черновика");
            window.alert(error && error.message ? error.message : "Не удалось получить предпросмотр производственного черновика.");
            createOrdersBtn.disabled = false;
          })
          .finally(function () {
            if (createOrdersBtn.disabled && !document.querySelector(".pc-modal")) {
              createOrdersBtn.disabled = false;
            }
          });
      });
    }

    setActiveLiveRefreshHandler(loadAndRender);
    loadAndRender();
  }

  function translatePalletStatus(status) {
    var original = String(status || "").trim();
    switch (original.toUpperCase()) {
      case "PLANNED":
        return "Ожидает";
      case "PRINTED":
        return "Этикетка напечатана";
      case "FILLED":
        return "Наполнена";
      case "CANCELLED":
        return "Отменена";
      default:
        return original || "—";
    }
  }

  function renderOrders() {
    return renderPageShell(
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
      '  <div id="ordersTableWrap"></div>' +
      '  <div class="pc-load-more-row">' +
      '    <button id="ordersLoadMoreBtn" class="btn btn-outline" type="button">Загрузить еще</button>' +
      "  </div>" +
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
          '<td class="pc-order-status-cell">' +
          getOrderStatusHtml(order) +
          "</td>" +
          '<td class="pc-order-pallet-cell">' +
          renderOrderPalletFillingIndicator(order) +
          "</td>" +
          '<td class="pc-order-marking-cell">' +
          renderOrderMarkingIndicator(order) +
          "</td>" +
          "</tr>"
        );
      })
      .join("");
    return (
      '<table class="pc-table">' +
      "<thead><tr>" +
      renderSortableHeader("orders", "orderRef", "Номер") +
      renderSortableHeader("orders", "orderType", "Тип") +
      renderSortableHeader("orders", "partnerName", "Контрагент") +
      renderSortableHeader("orders", "dueDate", "План") +
      renderSortableHeader("orders", "shippedAt", "Факт") +
      renderSortableHeader("orders", "status", "Статус") +
      renderSortableHeader("orders", "palletFilling", "Наполнение паллет") +
      "<th>ЧЗ</th>" +
      "</tr></thead>" +
      "<tbody>" +
      body +
      "</tbody>" +
      "</table>"
    );
  }

  function buildOrdersUrl(query, limit, offset) {
    var q = String(query || "").trim();
    var url =
      "/api/orders?include_internal=1&include_pending_requests=1&limit=" +
      encodeURIComponent(String(limit)) +
      "&offset=" +
      encodeURIComponent(String(offset));
    if (q) {
      url += "&q=" + encodeURIComponent(q);
    }
    return url;
  }

  function trimOrdersPage(rows) {
    var source = Array.isArray(rows) ? rows : [];
    return {
      rows: source.slice(0, ORDERS_PAGE_SIZE),
      hasMore: source.length > ORDERS_PAGE_SIZE,
    };
  }

  function loadOrders(query, offset) {
    return fetchJson(buildOrdersUrl(query, ORDERS_FETCH_LIMIT, offset || 0)).then(trimOrdersPage);
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

  function toOrderStatusCode(display) {
    var normalized = String(display || "").trim().toLowerCase();
    if (normalized === "готов" || normalized === "готов к отгрузке" || normalized === "принят" || normalized === "accepted") {
      return "ACCEPTED";
    }
    if (normalized === "в процессе" || normalized === "в работе" || normalized === "in_progress") {
      return "IN_PROGRESS";
    }
    if (normalized === "черновик" || normalized === "draft") {
      return "IN_PROGRESS";
    }
    if (normalized === "выполнен" || normalized === "отгружен" || normalized === "shipped") {
      return "SHIPPED";
    }
    if (normalized === "завершен") {
      return "SHIPPED";
    }
    if (normalized === "отменен" || normalized === "отменён" || normalized === "cancelled" || normalized === "canceled") {
      return "CANCELLED";
    }
    return "";
  }

  function loadLastView() {
    try {
      var value = localStorage.getItem(LAST_VIEW_KEY);
      if (!value) {
        return "";
      }
      var normalized = String(value).trim().toLowerCase();
      if (normalized === "stock" || normalized === "catalog" || normalized === "orders" || normalized === "production-need") {
        return normalized;
      }
      return "";
    } catch (error) {
      return "";
    }
  }

  function saveLastView(view) {
    try {
      var normalized = String(view || "").trim().toLowerCase();
      if (normalized === "stock" || normalized === "catalog" || normalized === "orders" || normalized === "production-need") {
        localStorage.setItem(LAST_VIEW_KEY, normalized);
      }
    } catch (error) {
      // ignore storage failures
    }
  }

  function clearLiveReconnectTimer() {
    if (liveReconnectTimerId) {
      clearTimeout(liveReconnectTimerId);
      liveReconnectTimerId = 0;
    }
  }

  function setActiveLiveRefreshHandler(handler) {
    activeLiveRefreshHandler = typeof handler === "function" ? handler : null;
  }

  function scheduleLiveRefresh() {
    if (!hasPcAccess(loadAccount())) {
      return;
    }
    if (!activeLiveRefreshHandler && !hasOpenOrderModal()) {
      return;
    }
    if (liveRefreshTimerId) {
      clearTimeout(liveRefreshTimerId);
    }
    liveRefreshTimerId = window.setTimeout(function () {
      liveRefreshTimerId = 0;
      if (!hasPcAccess(loadAccount())) {
        return;
      }
      if (activeLiveRefreshHandler) {
        activeLiveRefreshHandler();
      }
      refreshOpenOrderModalIfNeeded();
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
    if (!hasPcAccess(loadAccount()) || typeof EventSource === "undefined") {
      return;
    }

    var source = new EventSource("/api/live");
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
  }

  function fetchServerVersion() {
    return fetch("/api/version", {
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
  }

  function checkServerVersionAndReloadIfNeeded() {
    return fetchServerVersion().then(function (version) {
      if (!version) {
        return false;
      }

      if (!knownServerVersion) {
        knownServerVersion = version;
        return false;
      }

      if (version !== knownServerVersion) {
        knownServerVersion = version;
        window.location.reload();
        return true;
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
    }, VERSION_CHECK_INTERVAL_MS);
  }

  function getOrderStatusPresentation(order) {
    if (order && order.is_pending_confirmation) {
      return { label: "Ожидает подтверждения", tone: "warning" };
    }

    var statusCode = getOrderStatusCode(order);
    var label = getOrderStatusDisplay(order, statusCode);
    if (statusCode === "SHIPPED") {
      return { label: label, tone: "completed" };
    }
    if (statusCode === "ACCEPTED") {
      return { label: label, tone: "ready" };
    }
    if (statusCode === "CANCELLED") {
      return { label: label, tone: "cancelled" };
    }

    return { label: label || "В работе", tone: "inprogress" };
  }

  function getOrderStatusCode(order) {
    var raw = String((order && order.order_status) || "").trim().toUpperCase();
    if (raw === "CANCELED") {
      return "CANCELLED";
    }
    if (raw === "DRAFT" || raw === "ACCEPTED" || raw === "IN_PROGRESS" || raw === "SHIPPED" || raw === "CANCELLED") {
      return raw;
    }
    return toOrderStatusCode(order && order.status);
  }

  function getOrderStatusDisplay(order, statusCode) {
    var label = String((order && (order.status || order.order_status_display)) || "").trim();
    if (!label || label.toUpperCase() === statusCode) {
      if (statusCode === "DRAFT" && isInternalOrder(order)) {
        return "Черновик";
      }
      if (statusCode === "SHIPPED") {
        return "Выполнен";
      }
      if (statusCode === "ACCEPTED") {
        return "Готов";
      }
      if (statusCode === "CANCELLED") {
        return "Отменён";
      }
      return "В работе";
    }
    return label;
  }

  function isInternalOrder(order) {
    return String(order && order.order_type ? order.order_type : "").trim().toUpperCase() === "INTERNAL";
  }

  function isShippedOrder(order) {
    return getOrderStatusCode(order) === "SHIPPED";
  }

  function isActiveShipmentOrder(order) {
    var status = getOrderStatusCode(order);
    return (
      order &&
      !order.is_pending_confirmation &&
      !isInternalOrder(order) &&
      (status === "ACCEPTED" || status === "IN_PROGRESS")
    );
  }

  function isPendingConfirmationOrder(order) {
    return !!(order && order.is_pending_confirmation);
  }

  function sortPendingOrdersFirst(rows) {
    var source = Array.isArray(rows) ? rows.slice() : [];
    return source
      .map(function (row, index) {
        return { row: row, index: index };
      })
      .sort(function (leftEntry, rightEntry) {
        var leftPending = isPendingConfirmationOrder(leftEntry.row);
        var rightPending = isPendingConfirmationOrder(rightEntry.row);
        if (leftPending !== rightPending) {
          return leftPending ? -1 : 1;
        }
        return leftEntry.index - rightEntry.index;
      })
      .map(function (entry) {
        return entry.row;
      });
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

  function getCustomerLineHuCoverageQty(line) {
    var explicitKeys = [
      "hu_reserved_qty",
      "reserved_hu_qty",
      "bound_hu_qty",
      "qty_reserved",
      "reserved_qty",
    ];
    for (var index = 0; index < explicitKeys.length; index++) {
      var value = Number(line && line[explicitKeys[index]]);
      if (!isNaN(value)) {
        return Math.max(0, value);
      }
    }

    var producedOrReserved = Number(line && line.qty_produced) || 0;
    var shipped = Number(line && line.qty_shipped) || 0;
    return Math.max(0, producedOrReserved - shipped);
  }

  function getOrderLineHighlightState(line, order) {
    if (!line) {
      return { tone: "neutral", className: "", title: "" };
    }

    var itemName = String(line.item_name || "Товар без названия").trim() || "Товар без названия";
    var fillingState = getLinePalletFillingState(line);
    if (isInternalOrder(order)) {
      var ordered = Number(line.qty_ordered) || 0;
      var produced = Math.max(
        Number(line.qty_produced) || 0,
        fillingState.filledQty
      );
      if (ordered > 0.000001 && produced + 0.000001 >= ordered) {
        return {
          tone: "covered",
          className: "pc-order-line-coverage-covered",
          title: itemName + ": выпущено " + formatQuantity(produced) + " из " + formatQuantity(ordered),
        };
      }
      if (fillingState.filledQty > 0.000001) {
        return {
          tone: "partial",
          className: "pc-order-line-coverage-partial",
          title: itemName + ": наполнено " + formatQuantity(fillingState.filledQty) + " из " + formatQuantity(fillingState.plannedQty || ordered),
        };
      }

      return { tone: "neutral", className: "", title: "" };
    }

    var remaining = getLineRequiredQty(line);
    if (remaining <= 0.000001) {
      return { tone: "neutral", className: "", title: "" };
    }

    if (fillingState.filledQty > 0.000001 && fillingState.plannedQty > 0.000001) {
      if (fillingState.filledQty + 0.000001 >= fillingState.plannedQty) {
        return {
          tone: "covered",
          className: "pc-order-line-coverage-covered",
          title: itemName + ": наполнено " + formatQuantity(fillingState.filledQty) + " из " + formatQuantity(fillingState.plannedQty),
        };
      }

      return {
        tone: "partial",
        className: "pc-order-line-coverage-partial",
        title: itemName + ": частично наполнено " + formatQuantity(fillingState.filledQty) + " из " + formatQuantity(fillingState.plannedQty),
      };
    }

    var covered = Math.min(getCustomerLineHuCoverageQty(line), remaining);
    var missing = Math.max(0, remaining - covered);
    if (missing <= 0.000001) {
      return {
        tone: "covered",
        className: "pc-order-line-coverage-covered",
        title: itemName + ": привязано " + formatQuantity(covered) + " из " + formatQuantity(remaining),
      };
    }

    return {
      tone: "missing",
      className: "pc-order-line-coverage-missing",
      title: itemName + ": привязано " + formatQuantity(covered) + " из " + formatQuantity(remaining) + ", не хватает " + formatQuantity(missing),
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
      text: totalShortage <= 0.000001 ? "Готов" : "Не готов",
    };
  }

  function getOrderStatusHtml(order) {
    var status = getOrderStatusPresentation(order);
    if (getOrderStatusCode(order) === "SHIPPED") {
      return wrapOrderIconCell(renderStatusIconOnly(status.tone, "pc-order-status-icon", status.label));
    }

    return renderStatusBadge(status.label, status.tone);
  }

  function getOrderMarkingPresentation(order) {
    var serverLabel = String((order && order.marking_label) || "").trim();
    if (serverLabel === "Маркировка проведена") {
      return {
        tone: "success",
        label: "Маркировка проведена",
        title: "Маркировка проведена",
      };
    }
    if (serverLabel === "Маркировка не проведена") {
      return {
        tone: "danger",
        label: "Маркировка не проведена",
        title: "Маркировка не проведена",
      };
    }
    if (order && order.marking_completed === true) {
      return {
        tone: "success",
        label: "Маркировка проведена",
        title: "Маркировка проведена",
      };
    }
    var rawEffectiveStatus = String((order && (order.marking_effective_status || order.marking_status)) || "")
      .trim()
      .toUpperCase();
    var effectiveStatus = rawEffectiveStatus;
    var legacyExcelGenerated = effectiveStatus === "EXCEL_GENERATED";
    if (effectiveStatus === "EXCEL_GENERATED") {
      effectiveStatus = "PRINTED";
    }
    if (!effectiveStatus && order && order.marking_required === true) {
      effectiveStatus = "REQUIRED";
    }
    if (effectiveStatus === "PRINTED") {
      return {
        tone: "success",
        label: "Маркировка проведена",
        title: "Маркировка проведена",
      };
    }

    if (effectiveStatus === "REQUIRED") {
      return {
        tone: "danger",
        label: "Маркировка не проведена",
        title: "Маркировка не проведена",
      };
    }

    return {
      tone: "neutral",
      label: "",
      title: "",
    };
  }

  function renderOrderMarkingIndicator(order) {
    var marking = getOrderMarkingPresentation(order);
    if (!marking.label) {
      return "";
    }

    return wrapOrderIconCell(renderStatusIconOnly(marking.tone, "pc-marking-badge", marking.title || marking.label));
  }

  function isCustomerOrderFullyShipped(order) {
    if (!order || isInternalOrder(order)) {
      return false;
    }
    if (isShippedOrder(order)) {
      return true;
    }
    if (order.has_shipment_remaining === false) {
      return true;
    }
    return false;
  }

  function isLineFullyShippedForCustomer(line, order) {
    if (!line || isInternalOrder(order)) {
      return false;
    }
    if (line.line_fully_shipped === true) {
      return true;
    }
    var ordered = Number(line.qty_ordered) || 0;
    var shipped = Number(line.qty_shipped) || 0;
    return ordered > 0.000001 && shipped + 0.000001 >= ordered;
  }

  function getCustomerFullyShippedPalletPresentation(order) {
    var title =
      (order && (order.pallet_fill_title || order.palletFillTitle)) || "Заказ полностью отгружен";
    var tone = (order && (order.pallet_fill_tone || order.palletFillTone)) || "completed";
    return {
      label: "",
      tone: tone,
      title: title,
      sortValue: 4,
      iconOnly: true,
    };
  }

  function wrapOrderIconCell(html) {
    if (!html) {
      return "";
    }
    return '<span class="pc-order-icon-cell-inner">' + html + "</span>";
  }

  function buildPalletFillProgressLabel(filledCount, plannedCount) {
    return (
      "Наполнено " + formatQuantity(filledCount) + " / " + formatQuantity(plannedCount)
    );
  }

  function buildOrderPalletFillProgressPresentation(filledCount, plannedCount, titleParts) {
    var parts = Array.isArray(titleParts) ? titleParts.slice() : [];
    parts.push(
      "Паллеты: " + formatQuantity(filledCount) + " / " + formatQuantity(plannedCount)
    );
    var title = parts.filter(Boolean).join(". ");
    var label = buildPalletFillProgressLabel(filledCount, plannedCount);
    if (plannedCount > 0 && filledCount >= plannedCount) {
      return {
        label: label,
        tone: "completed",
        title: title,
        sortValue: 4,
      };
    }

    return {
      label: label,
      tone: "inprogress",
      title: title,
      sortValue: 3,
    };
  }

  function getOrderPalletFillingPresentation(order) {
    if (order && (order.pallet_fill_show_completed_icon === true || order.palletFillShowCompletedIcon === true)) {
      return getCustomerFullyShippedPalletPresentation(order);
    }
    if (isCustomerOrderFullyShipped(order)) {
      return getCustomerFullyShippedPalletPresentation(order);
    }

    var plannedCount = Number(order && (order.planned_pallet_count != null ? order.planned_pallet_count : order.plannedPalletCount)) || 0;
    var filledCount = Number(order && (order.filled_pallet_count != null ? order.filled_pallet_count : order.filledPalletCount)) || 0;
    var filledQty = Number(order && (order.filled_qty != null ? order.filled_qty : order.filledQty)) || 0;
    var plannedQty = Number(order && (order.planned_qty != null ? order.planned_qty : order.plannedQty)) || 0;
    var hasPlan = plannedCount > 0 || (order && (order.has_production_pallet_plan === true || order.hasProductionPalletPlan === true));
    var needsPlan = order && (order.needs_production_pallet_plan === true || order.needsProductionPalletPlan === true);
    var serverLabel = String((order && (order.pallet_plan_status || order.palletPlanStatus)) || "").trim();
    var shipmentPallets = getOrderShipmentPalletReadinessPresentation(order);

    if (!hasPlan && !needsPlan && shipmentPallets) {
      return shipmentPallets;
    }

    if (!hasPlan && !needsPlan && !serverLabel) {
      return { label: "", tone: "neutral", title: "", sortValue: 0 };
    }

    if (!hasPlan && serverLabel) {
      var lowerServerLabel = serverLabel.toLowerCase();
      if (lowerServerLabel.indexOf("план сформирован") >= 0) {
        return { label: "", tone: "neutral", title: "", sortValue: 0 };
      }
      var serverTone = lowerServerLabel.indexOf("не сформирован") >= 0
        ? "warning"
        : lowerServerLabel.indexOf("наполн") >= 0
          ? "inprogress"
          : "neutral";
      return {
        label: serverLabel,
        tone: serverTone,
        title: serverLabel,
        sortValue: serverTone === "warning" ? 1 : 2,
      };
    }

    if (!hasPlan) {
      return {
        label: "План не сформирован",
        tone: "warning",
        title: "Для заказа требуется план паллет",
        sortValue: 1,
      };
    }

    var titleParts = [];
    if (serverLabel && serverLabel.toLowerCase().indexOf("план сформирован") < 0) {
      titleParts.push(serverLabel);
    }
    if (shipmentPallets && shipmentPallets.title) {
      titleParts.push(shipmentPallets.title);
    }

    if (plannedCount > 0) {
      return buildOrderPalletFillProgressPresentation(filledCount, plannedCount, titleParts);
    }

    if (hasPlan && (filledCount > 0 || filledQty > 0 || plannedQty > 0)) {
      return {
        label: "Наполнено " + formatQuantity(filledQty),
        tone: filledQty > 0 && plannedQty > 0 && filledQty + 0.000001 >= plannedQty ? "completed" : "inprogress",
        title: titleParts.join(". ") || "Паллетный план без счётчика паллет",
        sortValue: 3,
      };
    }

    return { label: "", tone: "neutral", title: titleParts.join(". "), sortValue: 0 };
  }

  function renderOrderPalletFillingIndicator(order) {
    var filling = getOrderPalletFillingPresentation(order);
    if (!filling.label) {
      if (filling.iconOnly || filling.tone === "completed") {
        return wrapOrderIconCell(
          renderStatusIconOnly(
            filling.tone || "completed",
            "pc-pallet-filling-badge",
            filling.title || "Заказ полностью отгружен"
          )
        );
      }
      return "";
    }

    if (filling.tone === "completed") {
      return wrapOrderIconCell(
        renderStatusIconOnly(filling.tone, "pc-pallet-filling-badge", filling.title || filling.label)
      );
    }

    return renderStatusBadge(filling.label, filling.tone, "pc-pallet-filling-badge", filling.title);
  }

  function getLinePalletFillingState(line) {
    var plannedCount = Number(line && (line.planned_pallet_count != null ? line.planned_pallet_count : line.plannedPalletCount)) || 0;
    var filledCount = Number(line && (line.filled_pallet_count != null ? line.filled_pallet_count : line.filledPalletCount)) || 0;
    var plannedQty = Number(line && (
      line.pallet_planned_qty != null
        ? line.pallet_planned_qty
        : line.planned_pallet_qty != null
          ? line.planned_pallet_qty
          : line.plannedPalletQty
    )) || 0;
    var filledQty = Number(line && (
      line.pallet_filled_qty != null
        ? line.pallet_filled_qty
        : line.filled_pallet_qty != null
          ? line.filled_pallet_qty
          : line.filledPalletQty
    )) || 0;
    var hasPlan = plannedCount > 0 || plannedQty > 0;
    var hasFilled = filledCount > 0 || filledQty > 0;
    var complete = hasPlan
      && (plannedQty <= 0 || filledQty + 0.000001 >= plannedQty)
      && (plannedCount <= 0 || filledCount >= plannedCount);

    return {
      plannedCount: plannedCount,
      filledCount: filledCount,
      plannedQty: plannedQty,
      filledQty: filledQty,
      hasPlan: hasPlan,
      hasFilled: hasFilled,
      complete: complete,
    };
  }

  function renderLinePalletFillingBadge(line, order) {
    if (isLineFullyShippedForCustomer(line, order)) {
      var shippedTitle =
        (line && (line.pallet_fill_title || line.palletFillTitle)) || "Строка полностью отгружена";
      return wrapOrderIconCell(renderStatusIconOnly("completed", "pc-line-pallet-badge", shippedTitle));
    }
    if (line && line.hide_pallet_fill_indicator === true) {
      return "";
    }
    if (line && line.pallet_fill_label) {
      var lineState = getLinePalletFillingState(line);
      var serverTone = String(line.pallet_fill_tone || "neutral");
      var serverLabel = String(line.pallet_fill_label || "");
      if (serverLabel.toLowerCase().indexOf("план ") === 0 && lineState.plannedCount > 0) {
        serverLabel = buildPalletFillProgressLabel(lineState.filledCount, lineState.plannedCount);
        serverTone = "inprogress";
      }
      if (serverTone === "completed") {
        return renderStatusBadge(
          serverLabel || buildPalletFillProgressLabel(lineState.filledCount, lineState.plannedCount),
          "completed",
          "pc-line-pallet-badge",
          line.pallet_fill_title || serverLabel
        );
      }
      if (serverTone === "ready") {
        serverTone = "inprogress";
      }
      return renderStatusBadge(
        serverLabel,
        serverTone,
        "pc-line-pallet-badge",
        line.pallet_fill_title || serverLabel
      );
    }

    var state = getLinePalletFillingState(line);
    if (!state.hasPlan) {
      return "";
    }

    if (state.plannedCount > 0 && state.filledCount <= 0 && state.filledQty > 0.000001 && state.plannedQty > 0.000001 && !state.complete) {
      var partialQtyLabel = "Наполнено " + formatQuantity(state.filledQty) + " / " + formatQuantity(state.plannedQty);
      var partialQtyTitle =
        "Наполнение по строке: " +
        formatQuantity(state.filledQty) +
        " / " +
        formatQuantity(state.plannedQty);
      return renderStatusBadge(partialQtyLabel, "warning", "pc-line-pallet-badge", partialQtyTitle);
    }

    if (state.plannedCount > 0) {
      var palletLabel = buildPalletFillProgressLabel(state.filledCount, state.plannedCount);
      var palletTitle =
        "Наполнение по строке: " +
        formatQuantity(state.filledCount) +
        " / " +
        formatQuantity(state.plannedCount) +
        " паллет";
      if (state.complete) {
        return renderStatusBadge(palletLabel, "completed", "pc-line-pallet-badge", palletTitle);
      }

      return renderStatusBadge(palletLabel, "inprogress", "pc-line-pallet-badge", palletTitle);
    }

    if (state.plannedQty > 0) {
      var qtyLabel = "Наполнено " + formatQuantity(state.filledQty) + " / " + formatQuantity(state.plannedQty);
      var qtyTitle =
        "Наполнение по строке: " +
        formatQuantity(state.filledQty) +
        " / " +
        formatQuantity(state.plannedQty);
      if (state.complete) {
        return renderStatusBadge(qtyLabel, "completed", "pc-line-pallet-badge", qtyTitle);
      }

      return renderStatusBadge(qtyLabel, "inprogress", "pc-line-pallet-badge", qtyTitle);
    }

    return "";
  }

  function getOrderShipmentPalletReadinessPresentation(order) {
    var readyCount = Number(order && (order.shipment_pallet_ready_count != null ? order.shipment_pallet_ready_count : order.shipmentPalletReadyCount)) || 0;
    var totalCount = Number(order && (order.shipment_pallet_total_count != null ? order.shipment_pallet_total_count : order.shipmentPalletTotalCount)) || 0;
    if (totalCount <= 0) {
      return null;
    }

    var safeReadyCount = Math.min(Math.max(0, readyCount), totalCount);
    var title = "К отгрузке готово " + formatQuantity(safeReadyCount) + " из " + formatQuantity(totalCount) + " паллет по заказу";
    return {
      label: "К отгрузке " + formatQuantity(safeReadyCount) + " / " + formatQuantity(totalCount) + " паллет",
      tone: safeReadyCount >= totalCount ? "completed" : "ready",
      title: title,
      sortValue: safeReadyCount >= totalCount ? 4 : 3,
    };
  }

  function getLineHuCodes(line) {
    if (!line) {
      return [];
    }
    if (Array.isArray(line.production_hu_codes)) {
      return line.production_hu_codes
        .map(function (code) { return String(code || "").trim(); })
        .filter(Boolean);
    }

    return String(line.production_hu_codes_display || "")
      .split(",")
      .map(function (code) { return code.trim(); })
      .filter(Boolean);
  }

  function applyOrderLineShipmentPalletReadiness(order, lines) {
    if (!order || isInternalOrder(order)) {
      return order;
    }

    var source = Array.isArray(lines) ? lines : [];
    var totalCodes = {};
    var readyCodes = {};

    source.forEach(function (line) {
      var codes = getLineHuCodes(line);
      if (!codes.length) {
        return;
      }

      codes.forEach(function (code) {
        totalCodes[code.toUpperCase()] = true;
      });

      var required = getLineRequiredQty(line);
      var canShip = Number(line && (line.can_ship_now != null ? line.can_ship_now : line.qty_available)) || 0;
      var ready = required <= 0.000001 || canShip + 0.000001 >= required || getAvailabilityState(line).ready;
      if (!ready) {
        return;
      }

      codes.forEach(function (code) {
        readyCodes[code.toUpperCase()] = true;
      });
    });

    var totalCount = Object.keys(totalCodes).length;
    if (totalCount <= 0) {
      return order;
    }

    var readyCount = Math.min(Object.keys(readyCodes).length, totalCount);
    order.shipment_pallet_ready_count = readyCount;
    order.shipment_pallet_total_count = totalCount;
    order.shipmentPalletReadyCount = readyCount;
    order.shipmentPalletTotalCount = totalCount;
    return order;
  }

  function summarizeOrderPalletFillingFromLines(lines) {
    var source = Array.isArray(lines) ? lines : [];
    var summary = {
      plannedCount: 0,
      filledCount: 0,
      plannedQty: 0,
      filledQty: 0,
    };

    source.forEach(function (line) {
      var state = getLinePalletFillingState(line);
      if (!state.hasPlan) {
        return;
      }

      summary.plannedCount += state.plannedCount;
      summary.filledCount += state.filledCount;
      summary.plannedQty += state.plannedQty;
      summary.filledQty += state.filledQty;
    });

    if (summary.plannedCount <= 0 && summary.plannedQty <= 0) {
      return null;
    }

    return summary;
  }

  function hasOrderPalletFillingData(order) {
    if (!order) {
      return false;
    }

    return Number(order.planned_pallet_count != null ? order.planned_pallet_count : order.plannedPalletCount) > 0
      || order.has_production_pallet_plan === true
      || order.hasProductionPalletPlan === true
      || order.needs_production_pallet_plan === true
      || order.needsProductionPalletPlan === true
      || !!String(order.pallet_plan_status || order.palletPlanStatus || "").trim();
  }

  function applyOrderLinePalletFillingFallback(order, lines) {
    if (!order || hasOrderPalletFillingData(order)) {
      return order;
    }

    var summary = summarizeOrderPalletFillingFromLines(lines);
    if (!summary) {
      return order;
    }

    order.has_production_pallet_plan = true;
    order.hasProductionPalletPlan = true;
    order.planned_pallet_count = summary.plannedCount;
    order.filled_pallet_count = summary.filledCount;
    order.planned_qty = summary.plannedQty;
    order.filled_qty = summary.filledQty;
    order.plannedPalletCount = summary.plannedCount;
    order.filledPalletCount = summary.filledCount;
    order.plannedQty = summary.plannedQty;
    order.filledQty = summary.filledQty;
    return order;
  }

  function normalizeMarkingTaskRows(rows) {
    return (Array.isArray(rows) ? rows : []).filter(function (row) {
      if (!row) {
        return false;
      }

      return !!row.marking_order_id || Number(row.order_id) > 0;
    });
  }

  function renderReadinessBadge(readiness) {
    if (!readiness || !readiness.text) {
      return "";
    }
    return renderStatusBadge(readiness.text, readiness.ready ? "success" : "warning", "pc-status-badge-inline");
  }

  function sortOrdersNewestFirst(rows) {
    var source = Array.isArray(rows) ? rows.slice() : [];

    function getCanonicalStatusRank(order) {
      if (isPendingConfirmationOrder(order)) {
        return 0; // Ожидает подтверждения
      }

      var statusCode = getOrderStatusCode(order);
      if (statusCode === "IN_PROGRESS") {
        return 1; // В работе
      }
      if (statusCode === "ACCEPTED") {
        return 2; // Готов
      }
      if (statusCode === "DRAFT") {
        return 3; // Черновик
      }
      if (statusCode === "SHIPPED") {
        return 4; // Выполнен
      }
      if (statusCode === "CANCELLED") {
        return 5; // Отменен
      }
      return 99; // Неизвестные/старые статусы
    }

    function getDueDateSortValue(order) {
      var parsed = Date.parse(order && order.due_date ? String(order.due_date) : "");
      return isNaN(parsed) ? Number.POSITIVE_INFINITY : parsed;
    }

    function compareOrderRefDescending(leftOrder, rightOrder) {
      var leftRef = leftOrder && leftOrder.order_ref != null ? String(leftOrder.order_ref).trim() : "";
      var rightRef = rightOrder && rightOrder.order_ref != null ? String(rightOrder.order_ref).trim() : "";
      var leftIsNumeric = /^\d+$/.test(leftRef);
      var rightIsNumeric = /^\d+$/.test(rightRef);
      if (leftIsNumeric && rightIsNumeric) {
        var leftValue = Number(leftRef);
        var rightValue = Number(rightRef);
        if (leftValue !== rightValue) {
          return rightValue - leftValue;
        }
      }

      var compared = rightRef.localeCompare(leftRef, "ru", { sensitivity: "base", numeric: true });
      if (compared !== 0) {
        return compared;
      }

      return 0;
    }

    return source
      .map(function (row, index) {
        return { row: row, index: index };
      })
      .sort(function (leftEntry, rightEntry) {
        var left = leftEntry.row;
        var right = rightEntry.row;

        var leftRank = getCanonicalStatusRank(left);
        var rightRank = getCanonicalStatusRank(right);
        if (leftRank !== rightRank) {
          return leftRank - rightRank;
        }

        var byOrderRef = compareOrderRefDescending(left, right);
        if (byOrderRef !== 0) {
          return byOrderRef;
        }

        var leftDueDate = getDueDateSortValue(left);
        var rightDueDate = getDueDateSortValue(right);
        if (leftDueDate !== rightDueDate) {
          return leftDueDate - rightDueDate;
        }

        var leftTime = Date.parse(left && left.created_at ? String(left.created_at) : "") || 0;
        var rightTime = Date.parse(right && right.created_at ? String(right.created_at) : "") || 0;
        if (leftTime !== rightTime) {
          return rightTime - leftTime;
        }

        var leftId = Number(left && left.id) || 0;
        var rightId = Number(right && right.id) || 0;
        if (leftId !== rightId) {
          return rightId - leftId;
        }

        return leftEntry.index - rightEntry.index;
      })
      .map(function (entry) {
        return entry.row;
      });
  }

  function getStatusToneIcon(tone) {
    var normalizedTone = tone || "neutral";
    if (normalizedTone === "success" || normalizedTone === "ready") {
      return "✓";
    }
    if (normalizedTone === "warning" || normalizedTone === "danger") {
      return "!";
    }
    if (normalizedTone === "completed") {
      return "✓";
    }
    if (normalizedTone === "cancelled") {
      return "×";
    }

    return "•";
  }

  function renderStatusBadge(text, tone, extraClass, title) {
    var normalizedTone = tone || "neutral";
    var icon = getStatusToneIcon(normalizedTone);
    var className = "pc-status-badge pc-status-badge-" + normalizedTone;
    if (extraClass) {
      className += " " + extraClass;
    }
    var label = text || "-";
    var tooltip = title || label;

    return (
      '<span class="' +
      className +
      '" title="' +
      escapeHtml(tooltip) +
      '" aria-label="' +
      escapeHtml(tooltip) +
      '">' +
      '<span class="pc-status-badge-icon" aria-hidden="true">' +
      escapeHtml(icon) +
      "</span>" +
      "<span>" +
      escapeHtml(label) +
      "</span>" +
      "</span>"
    );
  }

  function renderStatusIconOnly(tone, extraClass, title) {
    var normalizedTone = tone || "neutral";
    var className = "pc-icon-status pc-icon-status-" + normalizedTone;
    if (extraClass) {
      className += " " + extraClass;
    }
    var tooltip = title || "";

    return (
      '<span class="' +
      className +
      '" title="' +
      escapeHtml(tooltip) +
      '" aria-label="' +
      escapeHtml(tooltip) +
      '" role="img">' +
      escapeHtml(getStatusToneIcon(normalizedTone)) +
      "</span>"
    );
  }

  function getOrderReadinessNeeds(order) {
    var needsReadiness = isActiveShipmentOrder(order);
    var palletPresentation = getOrderPalletFillingPresentation(order);
    var needsPalletFallback = !!(
      order &&
      order.id != null &&
      !order.is_pending_confirmation &&
      !palletPresentation.label &&
      !palletPresentation.iconOnly
    );
    return {
      needsReadiness: needsReadiness,
      needsPalletFallback: needsPalletFallback,
      needsLines: needsReadiness || needsPalletFallback,
    };
  }

  function applyOrderReadinessFromLines(order, lines, needs) {
    var state = needs || getOrderReadinessNeeds(order);
    if (state.needsReadiness) {
      order.shipment_readiness = getShipmentReadiness(lines);
    }
    applyOrderLineShipmentPalletReadiness(order, lines);
    if (state.needsPalletFallback) {
      applyOrderLinePalletFillingFallback(order, lines);
    }
    return order;
  }

  function fetchOrderLinesBatch(orderIds) {
    var ids = (Array.isArray(orderIds) ? orderIds : [])
      .map(function (id) { return Number(id) || 0; })
      .filter(function (id, index, source) {
        return id > 0 && source.indexOf(id) === index;
      });
    if (!ids.length) {
      return Promise.resolve({});
    }

    return fetchJson("/api/orders/lines?ids=" + encodeURIComponent(ids.join(",")))
      .then(function (payload) {
        var result = {};
        if (!Array.isArray(payload)) {
          return result;
        }

        payload.forEach(function (entry) {
          var orderId = Number(entry && entry.order_id) || 0;
          if (!orderId) {
            return;
          }

          result[String(orderId)] = Array.isArray(entry.lines) ? entry.lines : [];
        });
        return result;
      });
  }

  function loadOrderReadiness(order) {
    var needs = getOrderReadinessNeeds(order);
    if (!needs.needsLines) {
      return Promise.resolve(order);
    }

    return fetchJson("/api/orders/" + encodeURIComponent(order.id) + "/lines")
      .then(function (lines) {
        return applyOrderReadinessFromLines(order, lines, needs);
      })
      .catch(function () {
        if (needs.needsReadiness) {
          order.shipment_readiness = null;
        }
        return order;
      });
  }

  function enrichOrdersWithReadiness(rows) {
    var source = Array.isArray(rows) ? rows : [];
    var needsByOrderId = {};
    var ids = [];
    source.forEach(function (order) {
      var needs = getOrderReadinessNeeds(order);
      if (!needs.needsLines) {
        return;
      }

      var orderId = Number(order && order.id) || 0;
      if (!orderId) {
        return;
      }

      needsByOrderId[String(orderId)] = needs;
      ids.push(orderId);
    });

    if (!ids.length) {
      return Promise.resolve(source);
    }

    return fetchOrderLinesBatch(ids)
      .then(function (linesByOrderId) {
        return source.map(function (order) {
          var orderId = String(Number(order && order.id) || 0);
          var needs = needsByOrderId[orderId];
          if (!needs) {
            return order;
          }

          return applyOrderReadinessFromLines(order, linesByOrderId[orderId] || [], needs);
        });
      })
      .catch(function () {
        return source.map(function (order) {
          var orderId = String(Number(order && order.id) || 0);
          var needs = needsByOrderId[orderId];
          if (needs && needs.needsReadiness) {
            order.shipment_readiness = null;
          }
          return order;
        });
      });
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
      '      <label class="form-label" for="newOrderPartnerInput">Контрагент</label>' +
      '      <input class="form-input" id="newOrderPartnerInput" type="text" autocomplete="off" placeholder="Введите имя или код" />' +
      '      <label class="pc-inline-check pc-order-internal-check">' +
      '        <input id="newOrderInternalInput" type="checkbox" />' +
      "        Внутренний заказ" +
      "      </label>" +
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
      "  </div>" +
      '  <div id="newOrderLinesWrap" class="pc-order-lines"></div>' +
      '  <div class="pc-modal-footer">' +
      '    <button class="btn primary-btn pc-order-submit-btn" type="button" id="newOrderSubmitBtn">Отправить</button>' +
      '    <div id="newOrderStatus" class="status"></div>' +
      "  </div>" +
      "</div>";
    document.body.appendChild(modal);

    var refs = {
      card: modal.querySelector(".pc-modal-card"),
      closeBtn: modal.querySelector("#newOrderCloseBtn"),
      partnerInput: modal.querySelector("#newOrderPartnerInput"),
      internalInput: modal.querySelector("#newOrderInternalInput"),
      dueDateInput: modal.querySelector("#newOrderDueDateInput"),
      commentInput: modal.querySelector("#newOrderCommentInput"),
      linesWrap: modal.querySelector("#newOrderLinesWrap"),
      submitBtn: modal.querySelector("#newOrderSubmitBtn"),
      statusEl: modal.querySelector("#newOrderStatus"),
    };
    var items = [];
    var partners = [];
    function createEmptyLine() {
      return { item_id: 0, qty_ordered: "", query: "", locked: false };
    }
    var linesState = [createEmptyLine()];
    var activeLineIndex = 0;
    var selectedPartnerId = 0;
    var activeSuggestIndex = -1;
    var duplicateWarningTimer = 0;
    var suggestionOverlay = document.createElement("div");
    suggestionOverlay.className = "pc-order-suggest pc-order-suggest-floating";
    document.body.appendChild(suggestionOverlay);
    var partnerSuggestionOverlay = document.createElement("div");
    partnerSuggestionOverlay.className = "pc-order-suggest pc-order-suggest-floating";
    document.body.appendChild(partnerSuggestionOverlay);

    function setStatus(text) {
      if (refs.statusEl) {
        if (duplicateWarningTimer) {
          window.clearTimeout(duplicateWarningTimer);
          duplicateWarningTimer = 0;
        }
        refs.statusEl.classList.remove("is-warning");
        refs.statusEl.textContent = text || "";
      }
    }

    function setWarningStatus(text) {
      if (refs.statusEl) {
        if (duplicateWarningTimer) {
          window.clearTimeout(duplicateWarningTimer);
        }
        refs.statusEl.classList.add("is-warning");
        refs.statusEl.textContent = "⚠ " + (text || "");
      }
    }

    function clearDuplicateWarning() {
      if (duplicateWarningTimer) {
        window.clearTimeout(duplicateWarningTimer);
        duplicateWarningTimer = 0;
      }
      if (refs.statusEl) {
        refs.statusEl.classList.remove("is-warning");
        refs.statusEl.textContent = "";
      }
      linesState.forEach(function (line) {
        line.isDuplicateTarget = false;
      });
      renderLines();
    }

    function scheduleDuplicateWarningClear() {
      if (duplicateWarningTimer) {
        window.clearTimeout(duplicateWarningTimer);
      }
      duplicateWarningTimer = window.setTimeout(clearDuplicateWarning, 5000);
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
      var filtered = filterPartners(query);
      if (!filtered.length) {
        hidePartnerSuggestionOverlay();
        return;
      }

      showPartnerSuggestionOverlay(refs.partnerInput, filtered);
    }

    function applySuggestedItem(index, selectedId) {
      if (!linesState[index]) {
        return;
      }

      var duplicateIndex = linesState.findIndex(function (line, lineIndex) {
        return lineIndex !== index &&
          Number(line.item_id) === Number(selectedId);
      });
      if (duplicateIndex >= 0) {
        hideSuggestionOverlay();
        setWarningStatus("Товар уже добавлен. Измените количество в существующей строке при необходимости.");
        activeLineIndex = duplicateIndex;
        linesState.forEach(function (line) {
          line.isDuplicateTarget = false;
        });
        linesState[duplicateIndex].isDuplicateTarget = true;
        if (!Number(linesState[index].item_id)) {
          linesState[index].query = "";
          linesState[index].locked = false;
        }
        renderLines();
        scheduleDuplicateWarningClear();
        var existingQtyInput = refs.linesWrap
          ? refs.linesWrap.querySelector('.line-qty[data-index="' + duplicateIndex + '"]')
          : null;
        if (existingQtyInput) {
          existingQtyInput.focus();
          if (typeof existingQtyInput.select === "function") {
            existingQtyInput.select();
          }
        }
        return;
      }

      linesState[index].item_id = selectedId;
      var selectedItem = getItemById(selectedId);
      if (selectedItem) {
        linesState[index].query = buildItemLabel(selectedItem);
      }
      linesState[index].locked = true;
      linesState[index].isDuplicateTarget = false;
      activeLineIndex = Math.max(0, Math.min(index, linesState.length - 1));
      renderLines();
      var qtyInput = refs.linesWrap
        ? refs.linesWrap.querySelector('.line-qty[data-index="' + index + '"]')
        : null;
      if (qtyInput) {
        qtyInput.focus();
        if (typeof qtyInput.select === "function") {
          qtyInput.select();
        }
      }
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
        if (document.activeElement === refs.partnerInput) {
          showPartnerSuggestionOverlay(refs.partnerInput, filterPartners(""));
        } else {
          hidePartnerSuggestionOverlay();
        }
        return;
      }
      var partner = findPartnerByQuery(value);
      if (partner) {
        selectedPartnerId = Number(partner.id) || 0;
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
      refs.partnerInput.focus();
    }

    function isInternalOrderRequested() {
      return !!(refs.internalInput && refs.internalInput.checked);
    }

    function getDefaultProductionPurpose() {
      return isInternalOrderRequested() ? "INTERNAL_STOCK" : "CUSTOMER_ORDER";
    }

    function syncInternalOrderState() {
      if (!refs.partnerInput) {
        return;
      }

      var internal = isInternalOrderRequested();
      refs.partnerInput.disabled = internal;
      if (internal) {
        selectedPartnerId = 0;
        refs.partnerInput.value = "";
        hidePartnerSuggestionOverlay();
      } else {
        updatePartnerHint();
      }
      renderLines();
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
      if (line.locked) {
        if (activeSuggestIndex === index) {
          hideSuggestionOverlay();
        }
        if (hintEl) {
          hintEl.textContent = "";
        }
        return;
      }
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

    function focusLineItemQuery(index) {
      if (!refs.linesWrap) {
        return;
      }

      var itemInput = refs.linesWrap.querySelector('.line-item-query[data-index="' + index + '"]');
      if (!itemInput) {
        return;
      }

      itemInput.focus();
      if (typeof itemInput.setSelectionRange === "function") {
        var length = String(itemInput.value || "").length;
        itemInput.setSelectionRange(length, length);
      }
      if (typeof itemInput.scrollIntoView === "function") {
        itemInput.scrollIntoView({ block: "nearest" });
      }
    }

    function renderLines() {
      if (!refs.linesWrap) {
        return;
      }
      if (!linesState.length) {
        linesState.push(createEmptyLine());
      }
      if (activeLineIndex < 0 || activeLineIndex >= linesState.length) {
        activeLineIndex = linesState.length - 1;
      }

      hideSuggestionOverlay();
      var linesHtml = linesState
        .map(function (line, index) {
          var query = String(line.query || "");
          var itemCellHtml = "";
          if (line.locked && Number(line.item_id) > 0) {
            itemCellHtml =
              '<div class="pc-order-line-selected" title="' +
              escapeHtml(query) +
              '">' +
              escapeHtml(query) +
              "</div>";
          } else {
            itemCellHtml =
              '<div class="pc-order-line-autocomplete">' +
              '<input class="form-input line-item-query" data-index="' +
              index +
              '" type="text" autocomplete="off" placeholder="Введите GTIN/SKU или название" value="' +
              escapeHtml(query) +
              '" />' +
              "</div>" +
              '<div class="pc-order-line-hint line-item-hint" data-index="' +
              index +
              '"></div>';
          }
          var rowHtml =
            '<div class="pc-order-line-row' +
            (line.isDuplicateTarget ? " is-duplicate-target" : "") +
            '">' +
            '<div class="pc-order-line-item-cell">' +
            itemCellHtml +
            "</div>" +
            '<input class="form-input line-qty" data-index="' +
              index +
              '" type="number" min="0" step="1" placeholder="Кол-во" value="' +
              escapeHtml(String(line.qty_ordered || "")) +
              '" />' +
            (linesState.length > 1
              ? '<button class="btn btn-ghost line-remove-btn line-remove-icon-btn" type="button" title="Удалить строку" aria-label="Удалить строку" data-index="' +
                index +
                '">🗑</button>'
              : "") +
            "</div>";
          return rowHtml;
        })
        .join("");
      refs.linesWrap.innerHTML =
        linesHtml +
        '<div class="pc-order-line-add-row">' +
        '  <button class="btn btn-outline line-add-btn" type="button" id="newOrderAddLineBtn">' +
        '    <span class="pc-plus-circle-icon" aria-hidden="true">+</span> Добавить строку' +
        "  </button>" +
        "</div>";

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
          linesState[index].locked = false;
          linesState[index].isDuplicateTarget = false;
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
          if (activeLineIndex !== index) {
            activeLineIndex = index;
            renderLines();
            var refreshedInput = refs.linesWrap
              ? refs.linesWrap.querySelector('.line-item-query[data-index="' + index + '"]')
              : null;
            if (refreshedInput) {
              refreshedInput.focus();
              var length = String(refreshedInput.value || "").length;
              if (typeof refreshedInput.setSelectionRange === "function") {
                refreshedInput.setSelectionRange(length, length);
              }
            }
            return;
          }
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
        inputEl.addEventListener("keydown", function (event) {
          if (event.key !== "ArrowUp" && event.key !== "ArrowDown") {
            return;
          }

          event.preventDefault();
          var index = Number(inputEl.getAttribute("data-index"));
          if (!linesState[index]) {
            return;
          }

          var current = Number(inputEl.value);
          if (!isFinite(current)) {
            current = 0;
          }

          var next = event.key === "ArrowUp"
            ? current + 1
            : Math.max(0, current - 1);
          inputEl.value = String(next);
          linesState[index].qty_ordered = next;
          linesState[index].isDuplicateTarget = false;
        });

        inputEl.addEventListener("input", function () {
          var index = Number(inputEl.getAttribute("data-index"));
          if (!linesState[index]) {
            return;
          }
          linesState[index].qty_ordered = String(inputEl.value || "").trim();
          linesState[index].isDuplicateTarget = false;
        });
        inputEl.addEventListener("focus", function () {
          var index = Number(inputEl.getAttribute("data-index"));
          if (activeLineIndex !== index) {
            activeLineIndex = index;
            renderLines();
            var refreshedInput = refs.linesWrap
              ? refs.linesWrap.querySelector('.line-qty[data-index="' + index + '"]')
              : null;
            if (refreshedInput) {
              refreshedInput.focus();
            }
          }
        });
      });

      var removeButtons = refs.linesWrap.querySelectorAll(".line-remove-btn");
      removeButtons.forEach(function (btn) {
        btn.addEventListener("click", function () {
          var index = Number(btn.getAttribute("data-index"));
          if (linesState.length <= 1) {
            linesState = [createEmptyLine()];
            activeLineIndex = 0;
            renderLines();
            return;
          }
          linesState.splice(index, 1);
          activeLineIndex = Math.max(0, Math.min(index, linesState.length - 1));
          renderLines();
        });
      });

      var addLineBtn = refs.linesWrap.querySelector("#newOrderAddLineBtn");
      if (addLineBtn) {
        addLineBtn.addEventListener("click", function () {
          linesState.push(createEmptyLine());
          activeLineIndex = linesState.length - 1;
          renderLines();
          focusLineItemQuery(activeLineIndex);
        });
      }
    }

    function submit() {
      var internalOrder = isInternalOrderRequested();
      var selectedPartner = selectedPartnerId ? getPartnerById(selectedPartnerId) : null;
      if (!internalOrder && !selectedPartner && refs.partnerInput) {
        selectedPartner = findPartnerByQuery(refs.partnerInput.value);
      }
      var partnerId = internalOrder ? 0 : (selectedPartner ? Number(selectedPartner.id) : 0);
      var dueDate = refs.dueDateInput ? String(refs.dueDateInput.value || "").trim() : "";
      var comment = refs.commentInput ? String(refs.commentInput.value || "").trim() : "";
      var account = loadAccount();
      var lines = [];
      var unresolvedLines = [];

      linesState.forEach(function (line, index) {
        var itemId = Number(line.item_id) || 0;
        if (!itemId) {
          if (!normalizeText(line.query)) {
            return;
          }
          unresolvedLines.push(index + 1);
          return;
        }
        var qty = Number(line.qty_ordered) || 0;
        if (qty <= 0) {
          return;
        }

        lines.push({
          item_id: itemId,
          qty_ordered: qty,
          production_purpose: internalOrder ? "INTERNAL_STOCK" : "CUSTOMER_ORDER",
        });
      });

      if (!internalOrder && !partnerId) {
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
      if (!window.confirm("Полностью ли заполнен заказ?")) {
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
          order_type: internalOrder ? "INTERNAL" : "CUSTOMER",
          partner_id: internalOrder ? null : partnerId,
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
    if (refs.partnerInput) {
      refs.partnerInput.addEventListener("input", updatePartnerHint);
      refs.partnerInput.addEventListener("change", applyPartnerInputSelection);
      refs.partnerInput.addEventListener("blur", applyPartnerInputSelection);
      refs.partnerInput.addEventListener("focus", updatePartnerHint);
    }
    if (refs.internalInput) {
      refs.internalInput.addEventListener("change", syncInternalOrderState);
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

    setStatus("Загрузка справочников...");
    Promise.all([loadOrderReferenceData()])
      .then(function (payload) {
        var refsData = payload[0];

        partners = refsData.partners;
        items = refsData.items.sort(function (a, b) {
          var left = String(a.name || "").toLowerCase();
          var right = String(b.name || "").toLowerCase();
          return left < right ? -1 : left > right ? 1 : 0;
        });

        updatePartnerHint();
        if (refs.internalInput) {
          refs.internalInput.checked = false;
        }
        syncInternalOrderState();
        linesState = [createEmptyLine()];
        activeLineIndex = 0;
        renderLines();
        setStatus("");
      })
      .catch(function () {
        setStatus("Ошибка загрузки справочников.");
      });

    renderLines();
  }

  function wireOrders() {
    var searchInput = document.getElementById("ordersSearchInput");
    var statusEl = document.getElementById("ordersStatus");
    var tableWrap = document.getElementById("ordersTableWrap");
    var newBtn = document.getElementById("ordersNewBtn");
    var refreshBtn = document.getElementById("ordersRefreshBtn");
    var loadMoreBtn = document.getElementById("ordersLoadMoreBtn");
    var debounce = null;
    var currentRows = [];
    var currentQuery = "";
    var ordersHasMore = false;
    var ordersLoading = false;
    var ordersLoadingMore = false;

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text || "";
      }
    }

    function sortOrderRows(rows) {
      var sortedRows = sortRows(rows, "orders", {
        orderRef: { type: "number", getValue: function (row) { return Number(String(row.order_ref || "").trim()) || 0; } },
        orderType: { type: "string", getValue: function (row) { return getOrderTypeLabel(row.order_type); } },
        partnerName: { type: "string", getValue: function (row) { return row.partner_name; } },
        dueDate: { type: "date", getValue: function (row) { return row.due_date; } },
        shippedAt: { type: "date", getValue: function (row) { return row.shipped_at; } },
        status: { type: "string", getValue: function (row) { return getOrderStatusPresentation(row).label; } },
        palletFilling: { type: "number", getValue: function (row) { return getOrderPalletFillingPresentation(row).sortValue; } },
      });
      return sortedRows;
    }

    function renderTable(rows, options) {
      if (!tableWrap) {
        return;
      }
      var preserveServerOrder = !!(options && options.preserveServerOrder);
      currentRows = Array.isArray(rows) ? rows.slice() : [];
      var sortedRows = preserveServerOrder ? currentRows.slice() : sortOrderRows(currentRows);
      tableWrap.innerHTML = renderOrdersTable(sortedRows);
      bindTableSorting(tableWrap, "orders", function () {
        renderTable(currentRows);
      });
      var items = tableWrap.querySelectorAll("[data-order]");
      items.forEach(function (item) {
        item.addEventListener("click", function () {
          var id = item.getAttribute("data-order");
          var target = sortedRows.find(function (entry) {
            return String(entry.id) === String(id);
          });
          if (target) {
            openOrderModal(target, runSearch);
          }
        });
      });
    }

    function updateLoadMoreButton() {
      if (!loadMoreBtn) {
        return;
      }
      loadMoreBtn.hidden = !ordersHasMore && !ordersLoadingMore;
      loadMoreBtn.disabled = ordersLoading || ordersLoadingMore;
      loadMoreBtn.textContent = ordersLoadingMore ? "Загрузка..." : "Загрузить еще";
    }

    function runSearch() {
      currentQuery = searchInput ? searchInput.value.trim() : "";
      currentRows = [];
      ordersHasMore = false;
      ordersLoading = true;
      ordersLoadingMore = false;
      updateLoadMoreButton();
      setStatus("Загрузка...");
      loadOrders(currentQuery, 0)
        .then(function (page) {
          ordersHasMore = page.hasMore;
          var source = Array.isArray(page.rows) ? page.rows.slice() : [];
          renderTable(source, { preserveServerOrder: true });
          if (!source.length) {
            setStatus("Заказов нет");
            return source;
          }
          setStatus("Загрузка готовности...");
          return enrichOrdersWithReadiness(source).then(function (enrichedRows) {
            renderTable(enrichedRows, { preserveServerOrder: true });
            setStatus("Данные с сервера");
            return enrichedRows;
          });
        })
        .catch(function () {
          ordersHasMore = false;
          renderTable([]);
          setStatus("Ошибка загрузки заказов");
        })
        .finally(function () {
          ordersLoading = false;
          updateLoadMoreButton();
        });
    }

    function loadMoreOrders() {
      if (ordersLoading || ordersLoadingMore || !ordersHasMore) {
        return;
      }

      ordersLoadingMore = true;
      updateLoadMoreButton();
      setStatus("Загрузка...");
      loadOrders(currentQuery, currentRows.length)
        .then(function (page) {
          ordersHasMore = page.hasMore;
          var appendedRows = currentRows.concat(page.rows);
          renderTable(appendedRows, { preserveServerOrder: true });
          setStatus(page.rows.length ? "Данные с сервера" : "Больше заказов нет");
          if (!page.rows.length) {
            ordersHasMore = false;
          }
          return enrichOrdersWithReadiness(page.rows).then(function (enrichedRows) {
            if (!enrichedRows.length) {
              return appendedRows;
            }
            var enrichedById = {};
            enrichedRows.forEach(function (row) {
              enrichedById[String(row.id)] = row;
            });
            var mergedRows = currentRows.map(function (row) {
              return enrichedById[String(row.id)] || row;
            });
            renderTable(mergedRows, { preserveServerOrder: true });
            return mergedRows;
          });
        })
        .catch(function () {
          setStatus("Ошибка загрузки заказов");
        })
        .finally(function () {
          ordersLoadingMore = false;
          updateLoadMoreButton();
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
    if (loadMoreBtn) {
      loadMoreBtn.addEventListener("click", loadMoreOrders);
    }

    setActiveLiveRefreshHandler(runSearch);
    updateLoadMoreButton();
    runSearch();
  }

  function renderView(view) {
    if (!app) {
      return;
    }
    setActiveLiveRefreshHandler(null);

    syncTabsVisibility();
    var allowedView = resolveAllowedView(view);
    if (allowedView !== "warehouse-board") {
      app.classList.remove("pc-content--warehouse");
    }
    if (!allowedView) {
      currentView = getDefaultView();
      setActiveTab("");
      app.innerHTML = renderNoAccess();
      return;
    }

    currentView = allowedView;
    saveLastView(currentView);
    setActiveTab(allowedView);

    if (allowedView === "catalog") {
      app.innerHTML = catalog.renderCatalog();
      catalog.wireCatalog();
      return;
    }

    if (allowedView === "orders") {
      app.innerHTML = renderOrders();
      wireOrders();
      return;
    }

    if (allowedView === "production-need") {
      app.innerHTML = renderProductionNeed();
      wireProductionNeed();
      return;
    }

    if (allowedView === "warehouse-board") {
      app.classList.add("pc-content--warehouse");
      if (window.FlowStockWarehouseBoard && window.FlowStockWarehouseBoard.render) {
        app.innerHTML = window.FlowStockWarehouseBoard.render();
        window.FlowStockWarehouseBoard.wire();
      } else {
        app.innerHTML = renderPageShell(
          '<section class="pc-card"><div class="pc-status">Модуль «Задания склада» не загружен.</div></section>'
        );
      }
      return;
    }

    app.innerHTML = stock.renderStock();
    stock.wireStock();
  }

  function setActiveTab(view) {
    tabs.forEach(function (tab) {
      var match = tab.getAttribute("data-view") === view;
      tab.classList.toggle("is-active", match);
    });
  }

  if (window.FlowStockWarehouseBoard) {
    window.FlowStockWarehouseBoard.init({
      getAccount: loadAccount,
      fetchJson: fetchJson,
      createRequestHeaders: createRequestHeaders,
      renderPageShell: renderPageShell,
      escapeHtml: escapeHtml,
      formatDateTime: formatDateTime,
      formatDate: formatDate,
    });
  }

  function init() {
    ensureHeaderShell();
    var rememberedView = loadLastView();
    if (rememberedView) {
      currentView = rememberedView;
    }

    var account = loadAccount();
    if (!hasPcAccess(account)) {
      stopVersionWatcher();
      stopLiveUpdates();
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
    startVersionWatcher();
    startLiveUpdates();
    syncTabsVisibility();
    if (app) {
      app.innerHTML = renderPageShell('<section class="pc-card"><div class="pc-status">Загрузка...</div></section>');
    }
    loadClientBlocks().then(function () {
      currentView = resolveAllowedView(currentView) || getDefaultView();
      renderView(currentView);
    });
  }

  if (window.FlowStockPcTestHooks) {
    window.FlowStockPcTestHooks.getOrderStatusPresentation = getOrderStatusPresentation;
    window.FlowStockPcTestHooks.getOrderMarkingPresentation = getOrderMarkingPresentation;
    window.FlowStockPcTestHooks.renderOrderMarkingIndicator = renderOrderMarkingIndicator;
    window.FlowStockPcTestHooks.getOrderPalletFillingPresentation = getOrderPalletFillingPresentation;
    window.FlowStockPcTestHooks.renderOrderPalletFillingIndicator = renderOrderPalletFillingIndicator;
    window.FlowStockPcTestHooks.applyOrderLinePalletFillingFallback = applyOrderLinePalletFillingFallback;
    window.FlowStockPcTestHooks.applyOrderLineShipmentPalletReadiness = applyOrderLineShipmentPalletReadiness;
    window.FlowStockPcTestHooks.getOrderLineHighlightState = getOrderLineHighlightState;
    window.FlowStockPcTestHooks.renderOrdersTable = renderOrdersTable;
    window.FlowStockPcTestHooks.renderOrderLinesTable = renderOrderLinesTable;
    window.FlowStockPcTestHooks.normalizeMarkingTaskRows = normalizeMarkingTaskRows;
    window.FlowStockPcTestHooks.getEnabledViews = getEnabledViews;
    window.FlowStockPcTestHooks.renderStock = stock.testHooks.renderStock;
    window.FlowStockPcTestHooks.renderStockTable = stock.testHooks.renderStockTable;
    window.FlowStockPcTestHooks.mapWarehouseProductionStateRow = stock.testHooks.mapWarehouseProductionStateRow;
    window.FlowStockPcTestHooks.translatePalletStatus = translatePalletStatus;
    window.FlowStockPcTestHooks.renderCatalogTable = catalog.testHooks.renderCatalogTable;
    window.FlowStockPcTestHooks.sortOrdersNewestFirst = sortOrdersNewestFirst;
    window.FlowStockPcTestHooks.buildOrdersUrl = buildOrdersUrl;
    window.FlowStockPcTestHooks.getProductionNeedCreateOrdersRefreshUrl = getProductionNeedCreateOrdersRefreshUrl;
    window.FlowStockPcTestHooks.mapProductionNeedRow = mapProductionNeedRow;
    window.FlowStockPcTestHooks.renderProductionNeedTable = renderProductionNeedTable;
    window.FlowStockPcTestHooks.openProductionNeedPreviewModal = openProductionNeedPreviewModal;
    window.FlowStockPcTestHooks.trimOrdersPage = trimOrdersPage;
    window.FlowStockPcTestHooks.getOrderModalContentUpdates = getOrderModalContentUpdates;
    window.FlowStockPcTestHooks.applyOrderModalContentUpdates = applyOrderModalContentUpdates;
    window.FlowStockPcTestHooks.refreshOpenOrderModalIfNeeded = refreshOpenOrderModalIfNeeded;
    window.FlowStockPcTestHooks.clearOpenOrderModalController = clearOpenOrderModalController;
    window.FlowStockPcTestHooks.getOpenOrderModalController = orderModal.getOpenOrderModalController;
    window.FlowStockPcTestHooks.__setOpenOrderModalControllerForTest =
      orderModal.__setOpenOrderModalControllerForTest;
    return;
  }

  tabs.forEach(function (tab) {
    tab.addEventListener("click", function () {
      var view = tab.getAttribute("data-view") || getDefaultView();
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
      stopVersionWatcher();
      stopLiveUpdates();
      knownServerVersion = "";
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

  window.addEventListener("beforeunload", function () {
    stopLiveUpdates();
  });

  init();
})();
