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
  var cachedItemReplenishmentContext = {
    status: "loading",
    rowsByItemId: Object.create(null),
  };

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
      '<section class="pc-card pc-stock-card">' +
      '  <div class="section-title">Состояние склада</div>' +
      '  <div id="stockReplenishmentWrap">' + renderStockReplenishmentPreview({ status: "loading" }) + "</div>" +
      '  <section class="pc-stock-list-section" data-stock-section="list">' +
      '    <div class="pc-stock-list-title">Товары на складе</div>' +
      '    <div class="pc-stock-list-toolbar">' +
      '      <div class="form-field pc-stock-search-field">' +
      '        <label class="form-label" for="stockSearchInput">Поиск по товарам</label>' +
      '        <input class="form-input" id="stockSearchInput" type="text" autocomplete="off" placeholder="Название, бренд, объем, SKU, GTIN, штрихкод" />' +
      "      </div>" +
      '      <div id="stockStatus" class="pc-status pc-stock-list-status"></div>' +
      "    </div>" +
      '    <div id="stockLowWrap"></div>' +
      '    <div id="stockTableWrap"></div>' +
      "  </section>" +
      "</section>"
    );
  }

  function renderStockReplenishmentPreview(state) {
    var source = state || {};
    var status = String(source.status || "loading");
    var count = Math.max(0, Number(source.count) || 0);
    var statusText = "Загрузка…";
    if (status === "ready") {
      statusText = "Требуется пополнение · " + count + " " + getPositionWord(count);
    } else if (status === "empty") {
      statusText = "Дополнительное пополнение не требуется";
    } else if (status === "error") {
      statusText = "Не удалось загрузить предпросмотр";
    }
    var disabled = status !== "ready" || count < 1;
    return (
      '<section class="pc-stock-replenishment-card is-' + deps.escapeHtml(status) + '" data-stock-section="replenishment">' +
      '  <span class="pc-stock-replenishment-icon" aria-hidden="true">' +
      '    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">' +
      '      <path d="M4 7.5 12 3l8 4.5-8 4.5-8-4.5Z" />' +
      '      <path d="M4 7.5v9L12 21l8-4.5v-9M12 12v9M8 5.25l8 4.5v4" />' +
      "    </svg>" +
      "  </span>" +
      '  <div class="pc-stock-replenishment-title">Пополнение склада</div>' +
      '  <div class="pc-stock-replenishment-status-slot">' +
      '    <div id="stockReplenishmentStatus" class="pc-stock-replenishment-status" aria-live="polite">' + deps.escapeHtml(statusText) + "</div>" +
      "  </div>" +
      '  <button id="stockCreateProductionOrderBtn" class="btn btn-outline" type="button"' +
      (disabled ? ' disabled aria-disabled="true"' : "") +
      ">Подготовить заказ</button>" +
      "</section>"
    );
  }

  function getPositionWord(count) {
    var value = Math.abs(Number(count) || 0) % 100;
    var lastDigit = value % 10;
    if (value > 10 && value < 20) return "позиций";
    if (lastDigit === 1) return "позиция";
    if (lastDigit > 1 && lastDigit < 5) return "позиции";
    return "позиций";
  }

  function createItemReplenishmentContext(source) {
    var input = source || {};
    var status = String(input.status || "loading");
    var rowsByItemId = Object.create(null);
    (Array.isArray(input.rows) ? input.rows : []).forEach(function (previewRow) {
      var itemId = Number(previewRow && previewRow.itemId) || 0;
      if (itemId) rowsByItemId[itemId] = previewRow;
    });
    return {
      status: status,
      rowsByItemId: rowsByItemId,
    };
  }

  function resolveItemStockAttention(row, context) {
    if (!(Number(row && row.belowMinQty) > 0.000001)) {
      return { state: "normal", dotClass: "", warningClass: "", message: "", previewRow: null };
    }
    var source = context || cachedItemReplenishmentContext;
    if (source.status === "success") {
      var previewRow = source.rowsByItemId[Number(row.itemId) || 0] || null;
      if (previewRow) {
        return {
          state: "action-required",
          dotClass: " is-action-required",
          warningClass: " is-action-required",
          message: "Требуется дополнительное пополнение: " + formatWarehouseStateQty(previewRow.qtyToCreate, row.baseUom),
          previewRow: previewRow,
        };
      }
      return {
        state: "covered",
        dotClass: " is-covered",
        warningClass: " is-covered",
        message: "",
        previewRow: null,
      };
    }
    return {
      state: "unknown",
      dotClass: " is-unknown",
      warningClass: " is-unknown",
      message: source.status === "error"
        ? "Не удалось проверить необходимость дополнительного пополнения"
        : "Проверяем необходимость дополнительного пополнения…",
      previewRow: null,
    };
  }

  function renderStockBelowMinWarning(row, attention) {
    if (!attention || attention.state === "normal") return "";
    var conclusion = "";
    if (attention.state === "covered") {
      conclusion =
        '<div class="pc-stock-detail-warning-conclusion">Пополнение уже запланировано</div>' +
        '<div class="pc-stock-detail-warning-note">Дополнительный заказ не требуется</div>';
    } else {
      conclusion = '<div class="pc-stock-detail-warning-conclusion">' + deps.escapeHtml(attention.message) + "</div>";
    }
    return (
      '<div class="pc-stock-detail-warning' + attention.warningClass + '">' +
      '<div class="pc-stock-detail-warning-free">Свободный остаток: <strong>' + deps.escapeHtml(row.freeQtyDisplay) + "</strong></div>" +
      '<div class="pc-stock-detail-warning-deficit">Ниже минимального запаса на ' + deps.escapeHtml(row.belowMinQtyDisplay) + "</div>" +
      conclusion + "</div>"
    );
  }

  function renderStockTable(rows, expandedItemIds, itemReplenishmentSource) {
    if (!rows || !rows.length) {
      return '<div class="empty-state">Нет данных по остаткам.</div>';
    }
    var itemReplenishmentContext = itemReplenishmentSource
      ? createItemReplenishmentContext(itemReplenishmentSource)
      : cachedItemReplenishmentContext;
    var body = rows
      .map(function (row) {
        var itemId = Number(row.itemId) || 0;
        var isExpanded = !!(expandedItemIds && expandedItemIds[itemId]);
        var stockAttention = resolveItemStockAttention(row, itemReplenishmentContext);
        var belowMinIndicator = stockAttention.state !== "normal"
          ? '<span class="pc-stock-below-dot' + stockAttention.dotClass + '" role="img" aria-label="Ниже минимального запаса" title="Ниже минимального запаса"></span>'
          : "";
        var detailRow = "";
        if (isExpanded) {
          detailRow =
            '<tr class="pc-stock-detail-row"><td colspan="4" class="pc-stock-detail-cell">' +
            '<div class="pc-stock-detail-block"><div class="pc-stock-detail-layout">' +
            '<aside class="pc-stock-detail-summary">' +
            '<div class="pc-stock-detail-summary-title">На складе</div>' +
            '<div class="pc-stock-detail-summary-qty">' + deps.escapeHtml(row.stockQtyDisplay || "Нет на складе") + "</div>" +
            '<div class="pc-stock-detail-min">Минимальный запас: ' + deps.escapeHtml(row.minStockQtyDisplay || "—") + "</div>" +
            renderStockBelowMinWarning(row, stockAttention) +
            "</aside>" +
            '<section class="pc-stock-detail-section pc-stock-pallet-section"><div class="pc-stock-detail-title">Паллеты</div>' +
            renderWarehouseStatePallets(row.palletRows || []) +
            "</section></div></div></td></tr>";
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
          '<td class="pc-num"><span class="pc-qty pc-stock-parent-qty">' +
          belowMinIndicator +
          deps.escapeHtml(row.stockQtyDisplay || "Нет на складе") +
          "</span></td>" +
          '<td class="pc-num">' + deps.escapeHtml(row.customerRemainingToShipDisplay || "—") + "</td>" +
          '<td class="pc-stock-pallet-summary"><div class="pc-stock-pallet-count">' +
          deps.escapeHtml(row.palletSummary || "Паллет нет") + "</div>" +
          (row.palletStateSummary
            ? '<div class="pc-stock-pallet-breakdown">' + deps.escapeHtml(row.palletStateSummary) + "</div>"
            : "") +
          "</td></tr>" +
          detailRow
        );
      })
      .join("");
    return (
      '<table class="pc-table pc-stock-table"><colgroup>' +
      '<col class="pc-stock-col-nomenclature" /><col class="pc-stock-col-qty" />' +
      '<col class="pc-stock-col-customer" /><col class="pc-stock-col-pallets" />' +
      "</colgroup><thead><tr>" +
      deps.renderSortableHeader("stock", "itemName", "Товар") +
      deps.renderSortableHeader("stock", "stockQty", "На складе", "pc-num") +
      deps.renderSortableHeader("stock", "customerRemainingToShipQty", "Осталось отгрузить", "pc-num") +
      deps.renderSortableHeader("stock", "palletCount", "Паллеты") +
      "</tr></thead><tbody>" +
      body +
      "</tbody></table>"
    );
  }

  function renderWarehouseStatePallets(pallets) {
    if (!Array.isArray(pallets) || !pallets.length) {
      return '<div class="pc-stock-details-empty">Паллет нет</div>';
    }
    var body = pallets
      .map(function (pallet) {
        var statusClass = getPalletStateBadgeClass(pallet.stateCode);
        return (
          "<tr><td>" + deps.escapeHtml(pallet.huCode || "—") + "</td>" +
          '<td class="pc-stock-pallet-state"><span class="pc-stock-pallet-state-badge ' + statusClass + '">' +
          deps.escapeHtml(pallet.stateLabel || "—") + "</span></td>" +
          '<td class="pc-num">' + deps.escapeHtml(pallet.qtyDisplay || "—") + "</td>" +
          "<td>" + deps.escapeHtml(pallet.orderRef || "—") + "</td>" +
          '<td class="pc-stock-pallet-location">' + deps.escapeHtml(pallet.location || "—") + "</td></tr>"
        );
      })
      .join("");
    return (
      '<div class="pc-stock-detail-table-wrap"><table class="pc-table pc-stock-detail-table">' +
      '<thead><tr><th>Паллета</th><th>Статус</th><th class="pc-num">Кол-во</th><th>Заказ</th><th>Локация</th></tr></thead>' +
      "<tbody>" + body + "</tbody></table></div>"
    );
  }

  function getPalletStateBadgeClass(stateCode) {
    switch (String(stateCode || "").trim().toUpperCase()) {
      case "ON_STOCK": return "pc-stock-pallet-state--on-stock";
      case "RESERVED": return "pc-stock-pallet-state--reserved";
      case "AWAITING_SHIPMENT": return "pc-stock-pallet-state--awaiting-shipment";
      case "AWAITING_FILL": return "pc-stock-pallet-state--awaiting-fill";
      case "SHIPPED": return "pc-stock-pallet-state--shipped";
      case "INCONSISTENT": return "pc-stock-pallet-state--inconsistent";
      default: return "pc-stock-pallet-state--unknown";
    }
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

  function normalizeWarehouseStateHuCode(value) {
    return String(value || "").trim().toUpperCase();
  }

  function uniqueNonEmptyStrings(values) {
    var seen = Object.create(null);
    var result = [];
    (Array.isArray(values) ? values : []).forEach(function (value) {
      var normalized = String(value || "").trim();
      if (!normalized || seen[normalized]) return;
      seen[normalized] = true;
      result.push(normalized);
    });
    return result;
  }

  function getCanonicalHuStates(operatorPresentation) {
    var presentation = operatorPresentation && typeof operatorPresentation === "object"
      ? operatorPresentation
      : {};
    return [presentation.operational_hu, presentation.production_task]
      .map(function (entry) {
        var state = entry && entry.state && typeof entry.state === "object" ? entry.state : {};
        return {
          code: String(state.code || "").trim().toUpperCase(),
          label: String(state.label || "").trim(),
        };
      })
      .filter(function (state) { return state.code && state.label; });
  }

  function resolveMergedCanonicalState(states) {
    var unique = Object.create(null);
    var resolved = [];
    (Array.isArray(states) ? states : []).forEach(function (state) {
      var key = state.code + "\n" + state.label;
      if (unique[key]) return;
      unique[key] = true;
      resolved.push(state);
    });
    return resolved.length === 1 ? resolved[0] : { code: "", label: "" };
  }

  function resolveMergedOrderRef(group) {
    var sources = [group.reservedOrderRefs, group.sourceOrderRefs, group.originOrderRefs];
    for (var index = 0; index < sources.length; index += 1) {
      var refs = uniqueNonEmptyStrings(sources[index]);
      if (!refs.length) continue;
      return refs.length === 1 ? refs[0] : "—";
    }
    return "—";
  }

  function formatPalletCount(value) {
    var count = Math.max(0, Math.floor(Number(value) || 0));
    var mod100 = count % 100;
    var mod10 = count % 10;
    var word = "паллет";
    if (mod100 < 11 || mod100 > 14) {
      if (mod10 === 1) word = "паллета";
      else if (mod10 >= 2 && mod10 <= 4) word = "паллеты";
    }
    return count + " " + word;
  }

  function getPalletStateDisplayRank(stateCode) {
    switch (String(stateCode || "").trim().toUpperCase()) {
      case "INCONSISTENT": return 0;
      case "ON_STOCK": return 1;
      case "RESERVED": return 2;
      case "AWAITING_SHIPMENT": return 3;
      case "AWAITING_FILL": return 4;
      case "SHIPPED": return 5;
      default: return 6;
    }
  }

  function comparePalletDisplayText(left, right) {
    return String(left || "").localeCompare(String(right || ""));
  }

  function sortWarehouseStatePalletsForDisplay(pallets) {
    return (Array.isArray(pallets) ? pallets : []).slice().sort(function (left, right) {
      var rankDiff = getPalletStateDisplayRank(left && left.stateCode) - getPalletStateDisplayRank(right && right.stateCode);
      if (rankDiff) return rankDiff;
      return comparePalletDisplayText(left && left.huCode, right && right.huCode) ||
        comparePalletDisplayText(left && left.orderRef, right && right.orderRef) ||
        comparePalletDisplayText(left && left.location, right && right.location);
    });
  }

  function formatPalletStateSummary(pallets) {
    var countsByLabel = Object.create(null);
    var labels = [];
    var unknownCount = 0;
    (Array.isArray(pallets) ? pallets : []).forEach(function (pallet) {
      var stateCode = String((pallet && pallet.stateCode) || "").trim();
      var stateLabel = String((pallet && pallet.stateLabel) || "").trim();
      if (!stateCode || !stateLabel || stateLabel === "—") {
        unknownCount += 1;
        return;
      }
      if (!countsByLabel[stateLabel]) {
        countsByLabel[stateLabel] = 0;
        labels.push(stateLabel);
      }
      countsByLabel[stateLabel] += 1;
    });
    var parts = labels.map(function (label) {
      return label + ": " + countsByLabel[label];
    });
    if (unknownCount) parts.push("Состояние не определено: " + unknownCount);
    return parts.join(" · ");
  }

  function mergeWarehouseStatePallets(row, baseUom) {
    var groups = Object.create(null);
    var keys = [];

    function getGroup(huCode) {
      var normalizedHu = normalizeWarehouseStateHuCode(huCode);
      if (!normalizedHu) return null;
      if (!groups[normalizedHu]) {
        groups[normalizedHu] = {
          huCode: normalizedHu,
          huRows: [],
          productionRows: [],
          states: [],
          reservedOrderRefs: [],
          sourceOrderRefs: [],
          originOrderRefs: [],
        };
        keys.push(normalizedHu);
      }
      return groups[normalizedHu];
    }

    (Array.isArray(row && row.hu_rows) ? row.hu_rows : []).forEach(function (hu) {
      var group = getGroup(hu && hu.hu_code);
      if (!group) return;
      group.huRows.push(hu || {});
      group.states = group.states.concat(getCanonicalHuStates(hu && hu.operator_presentation));
      group.reservedOrderRefs.push(hu && hu.reserved_customer_order_ref);
      group.originOrderRefs.push(hu && hu.origin_internal_order_ref);
    });

    (Array.isArray(row && row.production_receipts) ? row.production_receipts : []).forEach(function (pallet) {
      var group = getGroup(pallet && pallet.hu_code);
      if (!group) return;
      group.productionRows.push(pallet || {});
      group.states = group.states.concat(getCanonicalHuStates(pallet && pallet.operator_presentation));
      group.sourceOrderRefs.push(pallet && pallet.source_order_ref);
    });

    return keys.sort(function (left, right) { return left.localeCompare(right); }).map(function (key) {
      var group = groups[key];
      var qty = 0;
      var qtyKnown = true;
      var location = "—";

      if (group.huRows.length) {
        group.huRows.forEach(function (hu) {
          var value = Number(hu && hu.qty);
          if (!isFinite(value)) {
            qtyKnown = false;
            return;
          }
          qty += value;
        });
        var locations = uniqueNonEmptyStrings(group.huRows.map(function (hu) { return hu && hu.location; }));
        if (locations.length === 1) location = locations[0];
        else if (locations.length > 1) location = "Несколько локаций";
      } else {
        var palletIds = uniqueNonEmptyStrings(group.productionRows.map(function (pallet) {
          return pallet && pallet.pallet_id != null ? String(pallet.pallet_id) : "";
        }));
        qtyKnown = palletIds.length === 1 || (palletIds.length === 0 && group.productionRows.length === 1);
        if (qtyKnown) {
          group.productionRows.forEach(function (pallet) {
            var value = Number(pallet && pallet.qty);
            if (!isFinite(value)) {
              qtyKnown = false;
              return;
            }
            qty += value;
          });
        }
      }

      var state = resolveMergedCanonicalState(group.states);
      return {
        huCode: group.huCode,
        qty: qtyKnown ? qty : null,
        qtyKnown: qtyKnown,
        qtyDisplay: qtyKnown ? formatWarehouseStateQty(qty, baseUom) : "—",
        orderRef: resolveMergedOrderRef(group),
        location: location,
        stateCode: state.code,
        stateLabel: state.label || "—",
      };
    });
  }

  function mapWarehouseProductionStateRow(row) {
    var itemId = Number(row && row.item_id) || 0;
    var cachedItem = cachedItemsById[itemId] || {};
    var baseUom = String((row && row.base_uom) || cachedItem.base_uom || "шт").trim();
    var barcode = String((row && row.barcode) || cachedItem.barcode || "").trim();
    var gtin = String((row && row.gtin) || cachedItem.gtin || "").trim();
    var itemTypeName = String((row && (row.item_type || row.item_type_name)) || cachedItem.itemTypeName || "Без типа").trim();
    var stockQty = Number(row && row.stock_qty) || 0;
    var freeQty = Number(row && row.free_qty) || 0;
    var minStockQty = Number(row && row.min_stock_qty) || 0;
    var belowMinQty = Number(row && row.below_min_qty) || 0;
    var customerRemainingToShipQty = Number(row && row.customer_remaining_to_ship_qty) || 0;
    var palletRows = sortWarehouseStatePalletsForDisplay(mergeWarehouseStatePallets(row, baseUom));
    var palletQtyKnown = palletRows.every(function (pallet) { return pallet.qtyKnown; });
    var palletQty = palletRows.reduce(function (sum, pallet) {
      return sum + (pallet.qtyKnown ? pallet.qty : 0);
    }, 0);
    var palletSummary = palletRows.length ? formatPalletCount(palletRows.length) : "Паллет нет";
    var palletStateSummary = formatPalletStateSummary(palletRows);
    var productMetaParts = [];
    if (barcode) productMetaParts.push("ШК: " + barcode);
    if (gtin && gtin !== barcode) productMetaParts.push("GTIN: " + gtin);
    if (itemTypeName) productMetaParts.push(itemTypeName);
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
      freeQty: freeQty,
      minStockQty: minStockQty,
      belowMinQty: belowMinQty,
      customerRemainingToShipQty: customerRemainingToShipQty,
      palletCount: palletRows.length,
      palletQty: palletQtyKnown ? palletQty : null,
      palletQtyKnown: palletQtyKnown,
      stockQtyDisplay: Math.abs(stockQty) <= 0.000001
        ? "Нет на складе"
        : formatWarehouseStateQty(stockQty, baseUom),
      freeQtyDisplay: formatWarehouseStateQty(freeQty, baseUom),
      minStockQtyDisplay: formatWarehouseStateQty(minStockQty, baseUom),
      belowMinQtyDisplay: formatWarehouseStateQty(belowMinQty, baseUom),
      customerRemainingToShipDisplay: formatWarehouseStateQty(customerRemainingToShipQty, baseUom),
      palletSummary: palletSummary,
      palletStateSummary: palletStateSummary,
      productMeta: productMetaParts.join(" · "),
      palletRows: palletRows,
    };
  }

  function shouldShowStockRow(row) {
    var qtyTolerance = 0.000001;
    return !!row && (
      Math.abs(Number(row.stockQty) || 0) > qtyTolerance ||
      (Number(row.minStockQty) || 0) > qtyTolerance ||
      (Number(row.belowMinQty) || 0) > qtyTolerance ||
      (Number(row.customerRemainingToShipQty) || 0) > qtyTolerance ||
      (Number(row.palletCount) || 0) > 0
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
    var replenishmentWrap = document.getElementById("stockReplenishmentWrap");
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
        customerRemainingToShipQty: { type: "number", getValue: function (row) { return row.customerRemainingToShipQty; } },
        palletCount: { type: "number", getValue: function (row) { return row.palletCount; } },
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
      setStatus("Загрузка списка...");
      return loadStockData().then(renderRows).catch(function () {
        setStatus("Не удалось загрузить объединённое состояние склада");
        if (tableWrap) tableWrap.innerHTML = '<div class="empty-state">Не удалось загрузить объединённое состояние склада.</div>';
      });
    }
    function setReplenishmentPreview(state) {
      if (!replenishmentWrap) return;
      replenishmentWrap.innerHTML = renderStockReplenishmentPreview(state);
      bindReplenishmentAction();
    }
    function loadReplenishmentPreview() {
      cachedItemReplenishmentContext = createItemReplenishmentContext({ status: "loading", rows: [] });
      setReplenishmentPreview({ status: "loading" });
      renderRows();
      return deps.loadProductionNeedCreateOrdersPreview().then(function (preview) {
        var rows = Array.isArray(preview && preview.rows) ? preview.rows : [];
        cachedItemReplenishmentContext = createItemReplenishmentContext({ status: "success", rows: rows });
        setReplenishmentPreview({
          status: rows.length ? "ready" : "empty",
          count: rows.length,
        });
        renderRows();
      }).catch(function () {
        cachedItemReplenishmentContext = createItemReplenishmentContext({ status: "error", rows: [] });
        setReplenishmentPreview({ status: "error" });
        renderRows();
      });
    }
    function refreshStockPage() {
      return Promise.all([loadAndRender(), loadReplenishmentPreview()]);
    }
    function bindReplenishmentAction() {
        var createOrdersBtn = document.getElementById("stockCreateProductionOrderBtn");
      if (!createOrdersBtn || createOrdersBtn.disabled) return;
      createOrdersBtn.addEventListener("click", function () {
        createOrdersBtn.disabled = true;
        var previewStatus = document.getElementById("stockReplenishmentStatus");
        var previousStatusText = previewStatus ? previewStatus.textContent : "";
        var openingStatusText = "Открываем актуальный предпросмотр…";
        if (previewStatus) previewStatus.textContent = openingStatusText;
        deps.runProductionNeedCreateOrdersFlow(function () {
          return refreshStockPage();
        }, function () {
          cachedItemReplenishmentContext = createItemReplenishmentContext({ status: "success", rows: [] });
          setReplenishmentPreview({ status: "empty" });
          renderRows();
        }).then(function () {
          if (document.getElementById("stockReplenishmentStatus") === previewStatus &&
              previewStatus.textContent === openingStatusText) {
            previewStatus.textContent = previousStatusText;
          }
        }).catch(function () {
          setReplenishmentPreview({ status: "error" });
        }).finally(function () {
          createOrdersBtn.disabled = false;
        });
      });
    }
    if (searchInput) {
      searchInput.addEventListener("input", function () {
        if (debounce) clearTimeout(debounce);
        debounce = window.setTimeout(renderRows, 150);
      });
    }
    deps.setActiveLiveRefreshHandler(refreshStockPage);
    refreshStockPage();
  }

  window.FlowStockPcStock = {
    init: init,
    renderStock: renderStock,
    wireStock: wireStock,
    loadStockData: loadStockData,
    testHooks: {
      renderStock: renderStock,
      renderStockReplenishmentPreview: renderStockReplenishmentPreview,
      renderStockTable: renderStockTable,
      mapWarehouseProductionStateRow: mapWarehouseProductionStateRow,
    },
  };
})();
