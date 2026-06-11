(function () {
  "use strict";

  var deps = {};
  var cachedItems = [];
  var cachedItemsById = {};
  var cachedLocations = [];
  var cachedLocationsById = {};
  var cachedStockRows = [];
  var cachedStockRowsForMin = [];
  var cachedHuRows = [];
  var cachedCombinedRows = [];

  function init(nextDeps) {
    deps = nextDeps || {};
  }

  function setCachedItems(items) {
    cachedItems = Array.isArray(items)
      ? items.filter(function (item) {
          return item && item.is_active !== false;
        })
      : [];
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
        itemTypeMinStockUsesOrderBinding: item.item_type_min_stock_uses_order_binding === true,
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

  function formatQtyDisplay(qty, itemId) {
    var item = cachedItemsById[Number(itemId)] || {};
    var unit = item.base_uom || "";
    return qty + (unit ? " " + unit : "");
  }

  function renderStock() {
    return deps.renderPageShell(
      '<section class="pc-card">' +
      '  <div class="section-title">Состояние склада</div>' +
      '  <div class="pc-toolbar">' +
      '    <div class="pc-toolbar-actions">' +
      '      <button id="stockCreateProductionOrderBtn" class="btn btn-primary" type="button">Сформировать заказ</button>' +
      "    </div>" +
      '    <div class="form-field">' +
      '      <label class="form-label" for="stockSearchInput">Поиск</label>' +
      '      <input class="form-input" id="stockSearchInput" type="text" autocomplete="off" placeholder="Название, бренд, объем, SKU, GTIN, штрихкод" />' +
      "    </div>" +
      '    <div id="stockStatus" class="pc-status"></div>' +
      "  </div>" +
      '  <div id="stockLowWrap"></div>' +
      '  <div id="stockTableWrap"></div>' +
      "</section>"
    );
  }

  function renderStockTable(rows, expandedItemIds) {
    if (!rows || !rows.length) {
      return '<div class="empty-state">Нет данных по остаткам.</div>';
    }
    var body = rows
      .map(function (row) {
        var itemId = Number(row.itemId) || 0;
        var isExpanded = !!(expandedItemIds && expandedItemIds[itemId]);
        var detailRow = "";
        if (isExpanded) {
          detailRow =
            '<tr class="pc-stock-detail-row"><td colspan="5" class="pc-stock-detail-cell">' +
            '<div class="pc-stock-detail-block">' +
            '<section class="pc-stock-detail-section"><div class="pc-stock-detail-title">Складские HU</div>' +
            renderWarehouseStateWarehouseHus(row.warehouseHuRows || row.huRows || []) +
            "</section>" +
            '<section class="pc-stock-detail-section"><div class="pc-stock-detail-title">План / производство</div>' +
            renderWarehouseStatePallets(row.productionReceipts || []) +
            "</section>" +
            '<section class="pc-stock-detail-section"><div class="pc-stock-detail-title">Расчёт потребности</div>' +
            renderWarehouseStateNeedBreakdown(row) +
            "</section></div></td></tr>";
        }
        return (
          '<tr class="pc-stock-parent-row" data-stock-toggle-item="' +
          deps.escapeHtml(String(itemId)) +
          '" tabindex="0" role="button" aria-expanded="' +
          (isExpanded ? "true" : "false") +
          '"><td class="pc-stock-nomenclature-cell"><div class="pc-stock-item-cell">' +
          '<span class="pc-stock-caret' +
          (isExpanded ? " is-expanded" : "") +
          '">▸</span><div class="pc-stock-item-copy"><span class="pc-stock-item-name">' +
          deps.escapeHtml(row.itemName || "-") +
          "</span>" +
          (row.productMeta ? '<span class="pc-stock-item-meta">' + deps.escapeHtml(row.productMeta) + "</span>" : "") +
          "</div></div></td>" +
          '<td><span class="pc-qty pc-stock-parent-qty">' +
          deps.escapeHtml(row.stockQtyDisplay || "—") +
          '</span></td><td class="' +
          (row.belowMinQty > 0 ? "pc-stock-warning-cell" : "") +
          '">' +
          deps.escapeHtml(row.minStockSummary || "—") +
          "</td><td>" +
          renderSummaryLines(row.needSummaryLines) +
          '</td><td class="pc-stock-plan-cell">' +
          renderSummaryLines(row.planSummaryLines) +
          "</td></tr>" +
          detailRow
        );
      })
      .join("");
    return (
      '<table class="pc-table pc-stock-table"><colgroup>' +
      '<col class="pc-stock-col-nomenclature" /><col class="pc-stock-col-qty" />' +
      '<col class="pc-stock-col-min" /><col class="pc-stock-col-need" /><col class="pc-stock-col-plan" />' +
      "</colgroup><thead><tr>" +
      deps.renderSortableHeader("stock", "itemName", "Товар") +
      deps.renderSortableHeader("stock", "stockQty", "На складе") +
      deps.renderSortableHeader("stock", "minStockQty", "Минимум") +
      "<th>Потребность</th><th>План</th></tr></thead><tbody>" +
      body +
      "</tbody></table>"
    );
  }

  function renderSummaryLines(lines) {
    var source = Array.isArray(lines) ? lines.filter(Boolean) : [];
    if (!source.length) {
      return "—";
    }
    return source
      .map(function (line) {
        return '<div class="pc-stock-summary-line">' + deps.escapeHtml(line) + "</div>";
      })
      .join("");
  }

  function renderWarehouseStateWarehouseHus(huRows) {
    if (!Array.isArray(huRows) || !huRows.length) {
      return '<div class="pc-stock-details-empty">Складские HU отсутствуют.</div>';
    }
    var body = huRows
      .map(function (hu) {
        return (
          "<tr><td>" + deps.escapeHtml(hu.huCode || "—") + "</td>" +
          '<td class="pc-num">' + deps.escapeHtml(hu.qtyDisplay || "—") + "</td>" +
          "<td>" + deps.escapeHtml(hu.stockStatus || "—") + "</td>" +
          "<td>" + deps.escapeHtml(hu.location || "—") + "</td></tr>"
        );
      })
      .join("");
    return (
      '<div class="pc-stock-detail-table-wrap"><table class="pc-table pc-stock-detail-table">' +
      '<thead><tr><th>HU</th><th class="pc-num">Кол-во</th><th>Статус</th><th>Локация</th></tr></thead>' +
      "<tbody>" + body + "</tbody></table></div>"
    );
  }

  function renderWarehouseStatePallets(pallets) {
    if (!Array.isArray(pallets) || !pallets.length) {
      return '<div class="pc-stock-details-empty">План / производство не сформирован.</div>';
    }
    var body = pallets
      .map(function (pallet) {
        return (
          "<tr><td>" + deps.escapeHtml(pallet.huCode || "—") + "</td>" +
          "<td>" + deps.escapeHtml(pallet.palletStatus || "—") + "</td>" +
          '<td class="pc-num">' + deps.escapeHtml(pallet.qtyDisplay || "—") + "</td>" +
          "<td>" + deps.escapeHtml(pallet.sourceOrderRef || "—") + "</td>" +
          "<td>" + deps.escapeHtml(pallet.prdRef || "—") + "</td>" +
          "<td>" + deps.escapeHtml(pallet.statusNote || "—") + "</td></tr>"
        );
      })
      .join("");
    return (
      '<div class="pc-stock-detail-table-wrap"><table class="pc-table pc-stock-detail-table">' +
      '<thead><tr><th>HU</th><th>Статус</th><th class="pc-num">Кол-во</th><th>Заказ</th><th>PRD</th><th>Примечание</th></tr></thead>' +
      "<tbody>" + body + "</tbody></table></div>"
    );
  }

  function renderWarehouseStateNeedBreakdown(row) {
    if (!row || row.hasNeedBreakdown === false) {
      return '<div class="pc-stock-details-empty">Потребности нет.</div>';
    }
    return (
      '<div class="pc-stock-detail-table-wrap"><table class="pc-table pc-stock-detail-table">' +
      '<thead><tr><th class="pc-num">Всего в заказах для клиентов</th><th class="pc-num">До минимума</th>' +
      '<th class="pc-num">Во внутренних заказах</th></tr></thead><tbody><tr>' +
      '<td class="pc-num">' + deps.escapeHtml(row.customerDemandDisplay || "—") + "</td>" +
      '<td class="pc-num">' + deps.escapeHtml(row.minDemandDisplay || "—") + "</td>" +
      '<td class="pc-num">' + deps.escapeHtml(row.internalPlanDisplay || "—") + "</td>" +
      "</tr></tbody></table></div>"
    );
  }

  function renderLowStockTable(rows) {
    if (!rows || !rows.length) {
      return "";
    }
    var body = rows
      .map(function (row) {
        return (
          "<tr><td>" + deps.escapeHtml(row.itemName || "-") + "</td>" +
          "<td>" + deps.escapeHtml(row.itemTypeName || "-") + "</td>" +
          '<td><span class="pc-qty">' + deps.escapeHtml(row.qtyDisplay || "0") + "</span></td>" +
          "<td>" + deps.escapeHtml(row.minStockDisplay || "-") + "</td>" +
          "<td>" + deps.escapeHtml(row.shortageDisplay || "-") + "</td></tr>"
        );
      })
      .join("");
    return (
      '<section class="pc-low-stock-card"><div class="pc-low-stock-title">Позиции ниже минимума: ' +
      rows.length +
      '</div><div class="pc-low-stock-table-wrap"><table class="pc-table pc-low-stock-table">' +
      '<colgroup><col class="pc-low-stock-col-item" /><col class="pc-low-stock-col-type" />' +
      '<col class="pc-low-stock-col-qty" /><col class="pc-low-stock-col-min" /><col class="pc-low-stock-col-shortage" />' +
      "</colgroup><thead><tr><th>Товар</th><th>Тип</th><th>В наличии</th><th>Минимум</th><th>Нехватка</th></tr></thead>" +
      "<tbody>" + body + "</tbody></table></div></section>"
    );
  }

  function formatWarehouseStateQty(value, baseUom) {
    var number = Number(value);
    if (!isFinite(number)) {
      number = 0;
    }
    var formatted = deps.formatReportQty(number);
    var unit = String(baseUom || "").trim();
    return formatted + (unit ? " " + unit : "");
  }

  function formatPositiveWarehouseStateQty(value, baseUom) {
    return Number(value) > 0.000001 ? formatWarehouseStateQty(value, baseUom) : "";
  }

  function mapWarehouseProductionStateRow(row) {
    var itemId = Number(row && row.item_id) || 0;
    var cachedItem = cachedItemsById[itemId] || {};
    var baseUom = String((row && row.base_uom) || cachedItem.base_uom || "шт").trim();
    var barcode = String((row && row.barcode) || cachedItem.barcode || "").trim();
    var gtin = String((row && row.gtin) || cachedItem.gtin || "").trim();
    var itemTypeName = String((row && (row.item_type || row.item_type_name)) || cachedItem.itemTypeName || "Без типа").trim();
    var stockQty = Number(row && row.stock_qty) || 0;
    var minStockQty = Number(row && row.min_stock_qty) || 0;
    var belowMinQty = Number(row && row.below_min_qty) || 0;
    var customerDemandQty = Number(row && row.customer_open_demand_qty) || 0;
    var internalRemainingQty = Number(row && row.internal_remaining_qty) || 0;
    var prdPlannedQty = Number(row && row.prd_planned_qty) || 0;
    var prdFilledQty = Number(row && row.prd_filled_qty) || 0;
    var remainingNeedQty = Number(row && row.remaining_need_qty) || 0;
    var needBreakdown = row && row.need_breakdown && typeof row.need_breakdown === "object" ? row.need_breakdown : {};
    var demandToClose = Number(needBreakdown.demand_to_close_customer_orders);
    var demandToMin = Number(needBreakdown.demand_to_min_stock);
    var alreadyPlannedInternal = Number(needBreakdown.already_planned_internal);
    var remainingToCreate = Number(needBreakdown.remaining_to_create);
    if (!isFinite(demandToClose)) demandToClose = customerDemandQty;
    if (!isFinite(demandToMin)) demandToMin = belowMinQty;
    if (!isFinite(alreadyPlannedInternal)) alreadyPlannedInternal = internalRemainingQty;
    if (!isFinite(remainingToCreate)) remainingToCreate = remainingNeedQty;

    var warehouseHuRows = Array.isArray(row && row.hu_rows)
      ? row.hu_rows.map(function (hu) {
          var qty = Number(hu && hu.qty) || 0;
          return {
            location: String((hu && hu.location) || "").trim(),
            huCode: String((hu && hu.hu_code) || "").trim(),
            qty: qty,
            qtyDisplay: formatWarehouseStateQty(qty, baseUom),
            stockStatus: String((hu && hu.stock_status) || "На складе").trim() || "На складе",
          };
        })
      : [];
    var productionReceipts = Array.isArray(row && row.production_receipts)
      ? row.production_receipts.map(function (pallet) {
          var plannedQty = Number(pallet && pallet.planned_qty) || 0;
          var filledQty = Number(pallet && pallet.filled_qty) || 0;
          var qty = Number(pallet && pallet.qty);
          if (!isFinite(qty) || qty <= 0) {
            qty = filledQty > 0.000001 ? filledQty : plannedQty;
          }
          var statusDisplay = String((pallet && pallet.pallet_status_display) || "").trim();
          return {
            huCode: String((pallet && pallet.hu_code) || "").trim() || "—",
            prdRef: String((pallet && pallet.prd_ref) || "").trim() || "—",
            palletStatus: statusDisplay || deps.translatePalletStatus((pallet && pallet.pallet_status) || ""),
            sourceOrderRef: String((pallet && pallet.source_order_ref) || "").trim() || "—",
            statusNote: String((pallet && pallet.status_note) || "").trim(),
            plannedQty: plannedQty,
            filledQty: filledQty,
            qty: qty,
            qtyDisplay: formatWarehouseStateQty(qty, baseUom),
            plannedQtyDisplay: formatWarehouseStateQty(plannedQty, baseUom),
            filledQtyDisplay: formatWarehouseStateQty(filledQty, baseUom),
            composition: String((pallet && pallet.composition) || (row && row.item_name) || cachedItem.name || "—").trim(),
          };
        })
      : [];
    var productMetaParts = [];
    if (barcode) productMetaParts.push("ШК: " + barcode);
    if (gtin && gtin !== barcode) productMetaParts.push("GTIN: " + gtin);
    if (itemTypeName) productMetaParts.push(itemTypeName);
    var needSummaryLines = [];
    var customerDemandDisplay = formatPositiveWarehouseStateQty(demandToClose, baseUom);
    var minDemandDisplay = formatPositiveWarehouseStateQty(demandToMin, baseUom);
    if (customerDemandDisplay) needSummaryLines.push("Клиенты: " + customerDemandDisplay);
    if (minDemandDisplay) needSummaryLines.push("До мин.: " + minDemandDisplay);
    var planSummaryLines = [];
    var internalPlanDisplay = formatPositiveWarehouseStateQty(alreadyPlannedInternal, baseUom);
    var prdPlanDisplay = formatPositiveWarehouseStateQty(prdPlannedQty, baseUom);
    if (internalPlanDisplay) planSummaryLines.push("Внутр.: " + internalPlanDisplay);
    if (prdPlanDisplay) planSummaryLines.push("PRD: " + prdPlanDisplay);
    var filledSummary = formatPositiveWarehouseStateQty(prdFilledQty, baseUom) || "—";
    var remainingNeedQtyDisplay = formatWarehouseStateQty(remainingToCreate, baseUom);
    var hasNeedOrPlan = demandToClose > 0.000001 || demandToMin > 0.000001 ||
      alreadyPlannedInternal > 0.000001 || prdPlannedQty > 0.000001 || prdFilledQty > 0.000001;
    return {
      itemId: itemId,
      itemName: String((row && row.item_name) || cachedItem.name || "-"),
      itemTypeId: Number(cachedItem.itemTypeId || cachedItem.item_type_id) || 0,
      itemTypeName: itemTypeName,
      barcode: barcode,
      gtin: gtin,
      brand: String((row && row.brand) || cachedItem.brand || ""),
      volume: String(cachedItem.volume || ""),
      baseUom: baseUom,
      stockQty: stockQty,
      minStockQty: minStockQty,
      belowMinQty: belowMinQty,
      customerDemandQty: demandToClose,
      internalRemainingQty: alreadyPlannedInternal,
      prdPlannedQty: prdPlannedQty,
      prdFilledQty: prdFilledQty,
      remainingNeedQty: remainingToCreate,
      stockQtyDisplay: formatWarehouseStateQty(stockQty, baseUom),
      minStockSummary: minStockQty > 0.000001 ? formatWarehouseStateQty(minStockQty, baseUom) : "—",
      needSummaryLines: needSummaryLines,
      planSummaryLines: planSummaryLines,
      filledSummary: filledSummary,
      customerDemandDisplay: customerDemandDisplay || "—",
      minDemandDisplay: minDemandDisplay || "—",
      internalPlanDisplay: internalPlanDisplay || "—",
      remainingNeedQtyDisplay: remainingNeedQtyDisplay,
      remainingNeedSummary: remainingToCreate > 0.000001 ? "Произвести: " + remainingNeedQtyDisplay : (hasNeedOrPlan ? "Покрыто" : "—"),
      remainingNeedClass: remainingToCreate > 0.000001 ? "pc-stock-remaining-need" : (hasNeedOrPlan ? "pc-stock-covered" : ""),
      productMeta: productMetaParts.join(" · "),
      warehouseHuRows: warehouseHuRows,
      huRows: warehouseHuRows,
      productionReceipts: productionReceipts,
      hasNeedBreakdown: hasNeedOrPlan,
    };
  }

  function shouldShowStockRow(row) {
    var qtyTolerance = 0.000001;
    return !!row && (
      Math.abs(Number(row.stockQty) || 0) > qtyTolerance ||
      (Number(row.minStockQty) || 0) > qtyTolerance ||
      (Number(row.belowMinQty) || 0) > qtyTolerance ||
      (Number(row.internalRemainingQty) || 0) > qtyTolerance ||
      (Number(row.prdPlannedQty) || 0) > qtyTolerance ||
      (Number(row.prdFilledQty) || 0) > qtyTolerance
    );
  }

  function loadStockData() {
    return Promise.all([
      deps.fetchJson("/api/items"),
      deps.fetchJson("/api/reports/warehouse-production-state"),
    ]).then(function (payloads) {
      setCachedItems(payloads[0]);
      cachedStockRows = Array.isArray(payloads[1]) ? payloads[1].map(mapWarehouseProductionStateRow) : [];
      cachedHuRows = [];
      cachedStockRowsForMin = [];
      cachedCombinedRows = cachedStockRows.slice();
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
      var diff = row.qty - (totalsByKey[key] || 0);
      if (Math.abs(diff) < 0.000001) return;
      combined.push({
        itemId: row.itemId, locationId: row.locationId, qty: diff,
        qtyDisplay: formatQtyDisplay(diff, row.itemId), itemName: row.itemName,
        barcode: row.barcode, gtin: row.gtin, brand: row.brand, volume: row.volume,
        itemTypeId: Number(row.itemTypeId) || 0, itemTypeName: row.itemTypeName || "",
        locationCode: row.locationCode, hu: "",
      });
    });
    cachedCombinedRows = combined;
  }

  function wireStock() {
    var searchInput = document.getElementById("stockSearchInput");
    var createOrdersBtn = document.getElementById("stockCreateProductionOrderBtn");
    var statusEl = document.getElementById("stockStatus");
    var lowWrap = document.getElementById("stockLowWrap");
    var tableWrap = document.getElementById("stockTableWrap");
    var debounce = null;
    var expandedItemIds = {};
    function setStatus(text) {
      if (statusEl) statusEl.textContent = text || "";
    }
    function renderRows() {
      if (!tableWrap) return;
      var query = deps.normalizeSearchQuery(searchInput ? searchInput.value : "");
      var rows = cachedStockRows.filter(shouldShowStockRow).filter(function (row) {
        return deps.matchesItemSearch(row, query, true);
      });
      rows = deps.sortRows(rows, "stock", {
        itemName: { type: "string", getValue: function (row) { return row.itemName; } },
        stockQty: { type: "number", getValue: function (row) { return row.stockQty; } },
        minStockQty: { type: "number", getValue: function (row) { return row.minStockQty; } },
        prdFilledQty: { type: "number", getValue: function (row) { return row.prdFilledQty; } },
        remainingNeedQty: { type: "number", getValue: function (row) { return row.remainingNeedQty; } },
      });
      setStatus("Позиций: " + rows.length);
      if (lowWrap) lowWrap.innerHTML = "";
      tableWrap.innerHTML = renderStockTable(rows, expandedItemIds);
      deps.bindTableSorting(tableWrap, "stock", renderRows);
      tableWrap.querySelectorAll("[data-stock-toggle-item]").forEach(function (expandableRow) {
        function toggleRow() {
          var itemId = Number(expandableRow.getAttribute("data-stock-toggle-item")) || 0;
          if (!itemId) return;
          expandedItemIds[itemId] = !expandedItemIds[itemId];
          renderRows();
        }
        expandableRow.addEventListener("click", toggleRow);
        expandableRow.addEventListener("keydown", function (event) {
          if (event.key === "Enter" || event.key === " ") {
            event.preventDefault();
            toggleRow();
          }
        });
      });
    }
    function loadAndRender() {
      setStatus("Загрузка...");
      return loadStockData().then(renderRows).catch(function () {
        setStatus("Не удалось загрузить объединённое состояние склада");
        if (tableWrap) tableWrap.innerHTML = '<div class="empty-state">Не удалось загрузить объединённое состояние склада.</div>';
      });
    }
    if (searchInput) {
      searchInput.addEventListener("input", function () {
        if (debounce) clearTimeout(debounce);
        debounce = window.setTimeout(renderRows, 150);
      });
    }
    if (createOrdersBtn) {
      createOrdersBtn.addEventListener("click", function () {
        createOrdersBtn.disabled = true;
        setStatus("Подготовка предпросмотра...");
        deps.runProductionNeedCreateOrdersFlow(function (message) {
          setStatus(message || "Производственный черновик сформирован.");
          return loadAndRender();
        }, function (message) {
          setStatus(message || "Нет позиций для создания внутреннего заказа");
        }).catch(function () {
          setStatus("Ошибка формирования производственного черновика");
        }).finally(function () {
          createOrdersBtn.disabled = false;
        });
      });
    }
    deps.setActiveLiveRefreshHandler(loadAndRender);
    loadAndRender();
  }

  window.FlowStockPcStock = {
    init: init,
    renderStock: renderStock,
    wireStock: wireStock,
    loadStockData: loadStockData,
    testHooks: {
      renderStock: renderStock,
      renderStockTable: renderStockTable,
      mapWarehouseProductionStateRow: mapWarehouseProductionStateRow,
    },
  };
})();
