(function () {
  "use strict";

  var deps = {};
  var cachedItems = [];
  var cachedItemTypes = [];
  var openProductCardController = null;
  var openReferencesController = null;
  var CUSTOMER_PRICE_PAGE_SIZE = 100;
  var LEGACY_SHT_UOM_VALUE = "__flowstock_legacy_sht__";
  var CURRENT_NON_MASTER_UOM_VALUE = "__flowstock_current_non_master_uom__";

  function init(nextDeps) {
    deps = nextDeps || {};
  }

  function canManageCatalog() {
    return !!(deps.hasCapability && deps.hasCapability("ManageCatalog"));
  }

  function setCachedItems(items) {
    cachedItems = Array.isArray(items) ? items.filter(function (item) { return !!item; }) : [];
  }

  function selectVisibleCatalogItems(items) {
    return (Array.isArray(items) ? items : []).filter(function (item) {
      return item && item.is_active !== false && item.item_type_is_visible_in_product_catalog === true;
    });
  }

  function renderCatalog() {
    var admin = canManageCatalog();
    return deps.renderPageShell(
      '<section class="pc-card">' +
      '  <div class="pc-catalog-heading"><div class="section-title">Каталог</div></div>' +
      '  <div class="pc-toolbar pc-catalog-toolbar">' +
      '    <div class="form-field pc-catalog-search-field">' +
      '      <label class="form-label" for="catalogSearchInput">Поиск</label>' +
      '      <input class="form-input" id="catalogSearchInput" type="text" autocomplete="off" placeholder="Название, бренд, объем, SKU, GTIN, штрихкод" />' +
      "    </div>" +
      '    <div class="form-field">' +
      '      <label class="form-label" for="catalogTypeFilter">Тип</label>' +
      '      <select class="form-input" id="catalogTypeFilter"></select>' +
      "    </div>" +
      '    <div class="form-field">' +
      '      <label class="form-label" for="catalogBrandFilter">Бренд</label>' +
      '      <select class="form-input" id="catalogBrandFilter"></select>' +
      "    </div>" +
      '    <div class="form-field">' +
      '      <label class="form-label" for="catalogVolumeFilter">Объём</label>' +
      '      <select class="form-input" id="catalogVolumeFilter"></select>' +
      "    </div>" +
      (admin ? '    <div class="form-field"><label class="form-label" for="catalogActivityFilter">Активность</label><select class="form-input" id="catalogActivityFilter"><option value="">Все</option><option value="active">Активные</option><option value="inactive">Неактивные</option></select></div>' : "") +
      '    <div class="pc-toolbar-actions">' +
      (admin ? '      <button id="catalogCreateBtn" class="btn btn-success" type="button">Добавить</button>' : "") +
      '      <button id="catalogRefreshBtn" class="btn btn-outline" type="button">Обновить</button>' +
      "    </div>" +
      '    <div id="catalogStatus" class="pc-status pc-catalog-status"></div>' +
      "  </div>" +
      '  <div id="catalogTableWrap"></div>' +
      "</section>"
    );
  }

  function buildCatalogRows(items) {
    return (Array.isArray(items) ? items : [])
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
          isActive: item.is_active !== false,
        };
      })
      .sort(function (left, right) {
        var compared = String(left.itemName || "").localeCompare(String(right.itemName || ""), "ru", {
          sensitivity: "base",
          numeric: true,
        });
        return compared !== 0 ? compared : left.itemId - right.itemId;
      });
  }

  function buildExactFilterOptions(items, propertyName) {
    var seen = Object.create(null);
    var values = [];
    (Array.isArray(items) ? items : []).forEach(function (item) {
      var value = item && item[propertyName] != null ? String(item[propertyName]) : "";
      if (!value.trim() || Object.prototype.hasOwnProperty.call(seen, value)) {
        return;
      }
      seen[value] = true;
      values.push(value);
    });
    return values.sort(function (left, right) {
      return left.localeCompare(right, "ru", { sensitivity: "variant", numeric: true });
    });
  }

  function filterCatalogRows(rows, filters) {
    var state = filters || {};
    return (Array.isArray(rows) ? rows : []).filter(function (row) {
      if (state.typeId && row.itemTypeId !== state.typeId) {
        return false;
      }
      if (state.brand && row.brand !== state.brand) {
        return false;
      }
      if (state.volume && row.volume !== state.volume) {
        return false;
      }
      if (state.activity === "active" && !row.isActive) return false;
      if (state.activity === "inactive" && row.isActive) return false;
      return deps.matchesItemSearch(row, state.query || "", false);
    });
  }

  function retainAvailableValue(previous, availableValues) {
    var value = String(previous || "");
    return value && availableValues.indexOf(value) >= 0 ? value : "";
  }

  function renderCatalogItemCell(row) {
    var name = deps.escapeHtml(row.itemName || "-");
    var metaParts = [];
    var gtin = String(row.gtin || "").trim();
    var barcode = String(row.barcode || "").trim();
    if (gtin) {
      metaParts.push("GTIN: " + deps.escapeHtml(gtin));
    }
    if (barcode) {
      metaParts.push("ШК: " + deps.escapeHtml(barcode));
    }
    var metaHtml = metaParts.length
      ? '<div class="pc-catalog-item-meta">' + metaParts.join(" · ") + "</div>"
      : "";
    return (
      '<div class="pc-catalog-item-cell">' +
      '<div class="pc-catalog-item-name">' + name + "</div>" +
      metaHtml +
      "</div>"
    );
  }

  function renderCatalogTable(rows) {
    if (!rows || !rows.length) {
      return '<div class="empty-state">Товары не найдены.</div>';
    }

    var body = rows
      .map(function (row) {
        var itemName = String(row.itemName || "").trim() || "Без названия";
        return (
          '<tr class="pc-catalog-row" tabindex="0" role="button" data-catalog-item-id="' +
          deps.escapeHtml(String(row.itemId)) +
          '" aria-label="Открыть карточку товара ' +
          deps.escapeHtml(itemName) +
          '">' +
          "<td>" + renderCatalogItemCell(row) + "</td>" +
          "<td>" + deps.escapeHtml(row.brand || "-") + "</td>" +
          "<td>" + deps.escapeHtml(row.volume || "-") + "</td>" +
          "<td>" + deps.escapeHtml(row.baseUom || "-") + "</td>" +
          (canManageCatalog() ? '<td><span class="pc-status-badge ' + (row.isActive ? "is-active" : "is-inactive") + '">' + (row.isActive ? "Активен" : "Неактивен") + "</span></td>" : "") +
          "</tr>"
        );
      })
      .join("");

    return (
      '<table class="pc-table pc-catalog-table">' +
      "<thead><tr>" +
      deps.renderSortableHeader("catalog", "itemName", "Товар") +
      deps.renderSortableHeader("catalog", "brand", "Бренд") +
      deps.renderSortableHeader("catalog", "volume", "Объём") +
      deps.renderSortableHeader("catalog", "baseUom", "Ед.") +
      (canManageCatalog() ? '<th>Статус</th>' : "") +
      "</tr></thead><tbody>" + body + "</tbody></table>"
    );
  }

  function loadCatalogData() {
    var admin = canManageCatalog();
    return Promise.all([
      deps.fetchJson(admin ? "/api/items?include_inactive=1" : "/api/items"),
      deps.fetchJson(admin ? "/api/item-types?include_inactive=1" : "/api/item-types"),
    ]).then(function (payloads) {
      var items = Array.isArray(payloads[0]) ? payloads[0] : [];
      cachedItemTypes = Array.isArray(payloads[1]) ? payloads[1].slice() : [];
      setCachedItems(admin ? items : selectVisibleCatalogItems(items));
      return cachedItems;
    });
  }

  function displayText(value) {
    if (value == null || !String(value).trim()) {
      return "—";
    }
    return deps.escapeHtml(String(value));
  }

  function formatDecimal(value, maximumFractionDigits) {
    if (value == null || value === "" || !isFinite(Number(value))) {
      return "—";
    }
    return Number(value).toLocaleString("ru-RU", {
      minimumFractionDigits: 0,
      maximumFractionDigits: maximumFractionDigits,
    });
  }

  function formatVatRate(item) {
    var name = item && item.default_sale_vat_rate_name != null
      ? String(item.default_sale_vat_rate_name).trim()
      : "";
    var hasNumericRate = item && item.default_sale_vat_rate != null && isFinite(Number(item.default_sale_vat_rate));
    var numericRate = hasNumericRate ? Number(item.default_sale_vat_rate) : null;
    var rate = hasNumericRate ? formatDecimal(numericRate, 4) + "%" : "";
    var nameContainsRate = false;
    if (name && hasNumericRate) {
      var percentPattern = /(-?\d+(?:[.,]\d+)?)\s*%/g;
      var match;
      while ((match = percentPattern.exec(name)) !== null) {
        if (Number(match[1].replace(",", ".")) === numericRate) {
          nameContainsRate = true;
          break;
        }
      }
    }
    var value = name && rate ? (nameContainsRate ? name : name + " — " + rate) : name || rate || "—";
    if (value !== "—" && item.default_sale_vat_rate_is_active === false) {
      value += " (неактивна)";
    }
    return deps.escapeHtml(value);
  }

  function renderDetail(label, value, className) {
    return (
      '<div class="pc-product-detail' + (className ? " " + className : "") + '">' +
      '<dt>' + deps.escapeHtml(label) + "</dt>" +
      '<dd>' + value + "</dd>" +
      "</div>"
    );
  }

  function renderProductCardContent(item, itemType) {
    var type = itemType || {};
    var details = [
      renderDetail("Наименование", displayText(item.name), "pc-product-detail-wide"),
      renderDetail("SKU / штрихкод", displayText(item.barcode)),
      renderDetail("GTIN", displayText(item.gtin)),
      renderDetail("Бренд", displayText(item.brand)),
      renderDetail("Фасовка / нетто", displayText(item.volume)),
      renderDetail("Срок годности", item.shelf_life_months == null ? "—" : deps.escapeHtml(String(item.shelf_life_months) + " мес.")),
      renderDetail("Условия хранения", displayText(item.storage_conditions), "pc-product-detail-wide pc-product-storage"),
      renderDetail("Потребительская тара", displayText(item.tara_name)),
      renderDetail("Тип номенклатуры", displayText(item.item_type_name || type.name)),
      renderDetail("Единица складского учёта", displayText(item.base_uom_code || item.base_uom)),
      renderDetail("Цена продажи с НДС", deps.escapeHtml(formatDecimal(item.default_sale_price_gross, 4))),
      renderDetail("Ставка НДС", formatVatRate(item)),
    ];
    if (item.item_type_enable_min_stock_control === true || type.enable_min_stock_control === true) {
      details.push(renderDetail("Минимальный остаток", deps.escapeHtml(formatDecimal(item.min_stock_qty, 3))));
    }
    if (type.enable_hu_distribution === true) {
      details.push(renderDetail("Макс. в 1 HU", deps.escapeHtml(formatDecimal(item.max_qty_per_hu, 3))));
    }
    if (item.item_type_enable_marking === true || type.enable_marking === true) {
      details.push(renderDetail(
        "Маркировка ЧЗ",
        String(item.gtin || "").trim() ? "Да" : "Нет, GTIN не заполнен"
      ));
    }

    return (
      '<div class="pc-modal-card pc-product-card">' +
      '  <div class="pc-modal-header">' +
      '    <div class="pc-modal-title">Карточка товара</div>' +
      '    <button class="btn btn-outline" type="button" data-product-card-close>Закрыть</button>' +
      "  </div>" +
      '  <dl class="pc-product-details">' + details.join("") + "</dl>" +
      '  <div class="pc-product-prices-actions">' +
      '    <button class="btn btn-outline" type="button" data-product-prices-toggle aria-expanded="false">Цены клиентов</button>' +
      "  </div>" +
      '  <section class="pc-product-prices" data-product-prices hidden>' +
      '    <div class="pc-status">Индивидуальные цены будут загружены по запросу.</div>' +
      "  </section>" +
      "</div>"
    );
  }

  function renderCustomerPrices(state) {
    var rows = state.items || [];
    var html = '<div class="pc-product-prices-title">Индивидуальные цены клиентов</div>';
    if (rows.length) {
      html += '<div class="pc-product-price-list">' + rows.map(function (row) {
        var code = String(row.partner_code || "").trim();
        return (
          '<div class="pc-product-price-row">' +
          '<div><strong>' + displayText(row.partner_name) + '</strong>' +
          (code ? '<div class="pc-product-price-code">' + deps.escapeHtml(code) + "</div>" : "") +
          '</div><div class="pc-product-price-value">' +
          '<span class="pc-product-price-label">Цена с НДС: </span>' +
          deps.escapeHtml(formatDecimal(row.unit_price_gross, 4)) +
          '</div><div class="pc-product-price-status ' + (row.is_active === true ? "is-active" : "is-inactive") + '">' +
          (row.is_active === true ? "Активна" : "Неактивна") +
          "</div></div>"
        );
      }).join("") + "</div>";
    } else if (state.loaded && !state.loading && !state.error) {
      html += '<div class="empty-state">Индивидуальные цены клиентов не заданы.</div>';
    }
    if (state.loading) {
      html += '<div class="pc-status">Загрузка цен...</div>';
    }
    if (state.error) {
      html += '<div class="pc-product-prices-error">Не удалось загрузить индивидуальные цены клиентов.</div>' +
        '<button class="btn btn-outline btn-sm" type="button" data-product-prices-retry>Повторить</button>';
    } else if (!state.loading && rows.length < state.totalCount) {
      html += '<button class="btn btn-outline btn-sm" type="button" data-product-prices-more>Показать ещё</button>';
    }
    return html;
  }

  function buildCustomerPricesUrl(itemId, offset) {
    return "/api/partner-item-sale-prices?item_id=" + encodeURIComponent(itemId) +
      "&limit=" + CUSTOMER_PRICE_PAGE_SIZE +
      "&offset=" + (Number(offset) || 0);
  }

  function findItemType(item) {
    var typeId = Number(item && item.item_type_id) || 0;
    for (var index = 0; index < cachedItemTypes.length; index += 1) {
      if (Number(cachedItemTypes[index] && cachedItemTypes[index].id) === typeId) {
        return cachedItemTypes[index];
      }
    }
    return null;
  }

  function renderReferencesShell(admin) {
    return (
      '<div class="pc-references-layout">' +
      '<nav class="pc-reference-nav" aria-label="Справочники">' +
      '<button class="btn btn-outline is-active" data-reference-kind="uoms" type="button">Единицы измерения</button>' +
      '<button class="btn btn-outline" data-reference-kind="taras" type="button">Тара</button>' +
      '<button class="btn btn-outline" data-reference-kind="item-types" type="button">Типы номенклатуры</button>' +
      '<button class="btn btn-outline" data-reference-kind="vat-rates" type="button">Ставки НДС</button>' +
      '<button class="btn btn-outline" data-reference-kind="partners" type="button">Контрагенты</button></nav>' +
      '<section class="pc-reference-content"><div class="section-title" data-reference-title>Единицы измерения</div>' +
      '<div class="pc-toolbar pc-reference-toolbar">' +
      '<div class="form-field"><label class="form-label">Поиск</label><input class="form-input" data-reference-search placeholder="Название, код"></div>' +
      '<div class="pc-toolbar-actions">' +
      (admin ? '<button class="btn btn-success" data-reference-create type="button">Добавить</button>' : '') +
      '<button class="btn btn-outline" data-reference-refresh type="button">Обновить</button></div>' +
      '<div class="pc-status" data-reference-status></div></div>' +
      '<div data-reference-table></div></section></div>'
    );
  }

  function requestJson(url, method, payload) {
    var options = { method: method || "GET" };
    if (payload !== undefined) {
      options.headers = { "Content-Type": "application/json" };
      options.body = JSON.stringify(payload);
    }
    return deps.fetchJson(url, options);
  }

  function reportCatalogError(error, title) {
    var message = error && error.message ? error.message : "Операция не выполнена.";
    if (deps.showAttention) {
      deps.showAttention(title || "Каталог", message);
    }
  }

  function fieldValue(form, name) {
    var control = form && form.elements ? form.elements[name] : null;
    return control ? String(control.value || "").trim() : "";
  }

  function nullableNumber(value) {
    var normalized = String(value == null ? "" : value).trim().replace(",", ".");
    return normalized === "" ? null : Number(normalized);
  }

  function optionRows(rows, selectedId, label) {
    return (rows || []).map(function (row) {
      var id = Number(row.id) || 0;
      var selected = id === Number(selectedId) ? " selected" : "";
      var inactive = row.is_active === false ? " (неактивно)" : "";
      return '<option value="' + deps.escapeHtml(String(id)) + '"' + selected + '>' +
        deps.escapeHtml(label(row) + inactive) + '</option>';
    }).join("");
  }

  function currentBaseUom(item) {
    return String(item && (item.base_uom_code || item.base_uom) || "").trim();
  }

  function normalizedUom(value) {
    return String(value || "").trim().toLocaleLowerCase("ru");
  }

  function renderBaseUomOptions(item, refs) {
    var current = currentBaseUom(item);
    var hasMaster = (refs.uoms || []).some(function (row) {
      return normalizedUom(row && row.name) === normalizedUom(current);
    });
    var currentNonMaster = current && normalizedUom(current) !== "шт" && !hasMaster
      ? '<option value="' + CURRENT_NON_MASTER_UOM_VALUE + '">Текущее значение: ' + deps.escapeHtml(current) + ' (нет в справочнике)</option>'
      : "";
    return '<option value="' + LEGACY_SHT_UOM_VALUE + '">шт (legacy)</option>' +
      currentNonMaster +
      optionRows(refs.uoms, 0, function (row) { return row.name || ""; });
  }

  function loadAdminItemReferences() {
    return Promise.all([
      deps.fetchJson("/api/uoms"),
      deps.fetchJson("/api/taras"),
      deps.fetchJson("/api/item-types?include_inactive=1"),
      deps.fetchJson("/api/vat-rates?include_inactive=1"),
    ]).then(function (payloads) {
      return { uoms: payloads[0] || [], taras: payloads[1] || [], itemTypes: payloads[2] || [], vatRates: payloads[3] || [] };
    });
  }

  function renderAdminItemForm(item, refs) {
    var value = item || {};
    return (
      '<form class="pc-catalog-edit-form" data-item-form>' +
      '<div class="pc-item-form-fields">' +
      '<div class="form-field pc-form-wide"><label class="form-label">Наименование *</label><input class="form-input" name="name" required value="' + deps.escapeHtml(value.name || "") + '"></div>' +
      '<div class="form-field"><label class="form-label">SKU / штрихкод</label><input class="form-input" name="barcode" value="' + deps.escapeHtml(value.barcode || "") + '"></div>' +
      '<div class="form-field"><label class="form-label">GTIN</label><input class="form-input" name="gtin" value="' + deps.escapeHtml(value.gtin || "") + '"></div>' +
      '<div class="form-field"><label class="form-label">Бренд</label><input class="form-input" name="brand" value="' + deps.escapeHtml(value.brand || "") + '"></div>' +
      '<div class="form-field"><label class="form-label">Объём</label><input class="form-input" name="volume" value="' + deps.escapeHtml(value.volume || "") + '"></div>' +
      '<div class="form-field"><label class="form-label">Срок годности, мес.</label><input class="form-input" name="shelf_life_months" type="number" min="1" step="1" value="' + (value.shelf_life_months == null ? "" : deps.escapeHtml(String(value.shelf_life_months))) + '"></div>' +
      '<div class="form-field"><label class="form-label">Ед. учёта *</label><select class="form-input" name="base_uom" required>' + renderBaseUomOptions(value, refs) + '</select></div>' +
      '<div class="form-field"><label class="form-label">Тара</label><select class="form-input" name="tara_id"><option value="">Не задана</option>' + optionRows(refs.taras, value.tara_id, function (row) { return row.name || ""; }) + '</select></div>' +
      '<div class="form-field"><label class="form-label">Тип</label><select class="form-input" name="item_type_id"><option value="">Не задан</option>' + optionRows(refs.itemTypes, value.item_type_id, function (row) { return row.name || ""; }) + '</select></div>' +
      '<div class="form-field"><label class="form-label">НДС</label><select class="form-input" name="default_sale_vat_rate_id"><option value="">Не задан</option>' + optionRows(refs.vatRates.filter(function (row) { return row.is_active !== false || Number(row.id) === Number(value.default_sale_vat_rate_id); }), value.default_sale_vat_rate_id, function (row) { return (row.name || "") + " — " + row.rate + "%"; }) + '</select></div>' +
      '<div class="form-field" data-item-min-stock><label class="form-label">Мин. остаток</label><input class="form-input" name="min_stock_qty" type="number" min="0" step="0.001" value="' + (value.min_stock_qty == null ? "" : value.min_stock_qty) + '"></div>' +
      '<div class="form-field" data-item-max-qty-per-hu><label class="form-label">Макс. в HU</label><input class="form-input" name="max_qty_per_hu" type="number" min="0.001" step="0.001" value="' + (value.max_qty_per_hu == null ? "" : value.max_qty_per_hu) + '"></div>' +
      '<div class="form-field"><label class="form-label">Цена с НДС</label><input class="form-input" name="default_sale_price_gross" type="number" min="0" step="0.0001" value="' + (value.default_sale_price_gross == null ? "" : value.default_sale_price_gross) + '"></div>' +
      '<div class="form-field pc-form-wide"><label class="form-label">Условия хранения</label><textarea class="form-input" name="storage_conditions">' + deps.escapeHtml(value.storage_conditions || "") + '</textarea></div>' +
      '<label class="pc-checkbox"><input name="is_active" type="checkbox"' + (value.is_active === false ? "" : " checked") + '> Активен</label>' +
      '<div class="pc-readonly-field">Маркировка: <strong>' + (value.is_marked ? "Да" : "Нет") + '</strong> (только чтение)</div>' +
      '</div><div class="pc-modal-actions"><button class="btn ' + (value.id ? "btn-primary" : "btn-success") + '" type="submit">Сохранить</button>' +
      (value.id ? '<button class="btn btn-danger" data-item-delete type="button">Удалить</button>' : '') + '</div></form>'
    );
  }

  function selectUomCode(form, refs, item) {
    var select = form.elements.base_uom;
    var code = item ? currentBaseUom(item) : "шт";
    var normalizedCode = normalizedUom(code);
    if (normalizedCode === "шт") {
      select.value = LEGACY_SHT_UOM_VALUE;
      return;
    }
    var match = (refs.uoms || []).find(function (row) {
      return normalizedUom(row && row.name) === normalizedCode;
    });
    select.value = match ? String(match.id) : CURRENT_NON_MASTER_UOM_VALUE;
  }

  function updateItemTypeApplicability(form, refs) {
    var selectedId = Number(fieldValue(form, "item_type_id")) || 0;
    var itemType = (refs.itemTypes || []).find(function (row) { return Number(row.id) === selectedId; }) || {};
    [
      { selector: "[data-item-min-stock]", enabled: itemType.enable_min_stock_control === true },
      { selector: "[data-item-max-qty-per-hu]", enabled: itemType.enable_hu_distribution === true },
    ].forEach(function (state) {
      var field = form.querySelector(state.selector);
      var input = field ? field.querySelector("input") : null;
      if (field) field.hidden = !state.enabled;
      if (input) input.disabled = !state.enabled;
    });
  }

  function wireItemTypeApplicability(form, refs) {
    updateItemTypeApplicability(form, refs);
    var select = form.elements.item_type_id;
    if (select) select.addEventListener("change", function () { updateItemTypeApplicability(form, refs); });
  }

  function buildItemPayload(form, refs, original) {
    var selectedUom = fieldValue(form, "base_uom");
    var uomId = Number(selectedUom);
    var uom = (refs.uoms || []).find(function (row) { return Number(row.id) === uomId; });
    var baseUom = selectedUom === LEGACY_SHT_UOM_VALUE
      ? "шт"
      : selectedUom === CURRENT_NON_MASTER_UOM_VALUE
        ? currentBaseUom(original)
        : (uom ? uom.name : "");
    var itemTypeId = nullableNumber(fieldValue(form, "item_type_id"));
    var itemType = (refs.itemTypes || []).find(function (row) { return Number(row.id) === Number(itemTypeId); }) || {};
    return {
      name: fieldValue(form, "name"), barcode: fieldValue(form, "barcode") || null, gtin: fieldValue(form, "gtin") || null,
      base_uom: baseUom, brand: fieldValue(form, "brand") || null, volume: fieldValue(form, "volume") || null,
      shelf_life_months: nullableNumber(fieldValue(form, "shelf_life_months")), storage_conditions: fieldValue(form, "storage_conditions") || null,
      tara_id: nullableNumber(fieldValue(form, "tara_id")), item_type_id: itemTypeId,
      min_stock_qty: itemType.enable_min_stock_control === true ? nullableNumber(fieldValue(form, "min_stock_qty")) : null,
      max_qty_per_hu: itemType.enable_hu_distribution === true ? nullableNumber(fieldValue(form, "max_qty_per_hu")) : null,
      default_sale_price_gross: nullableNumber(fieldValue(form, "default_sale_price_gross")),
      default_sale_vat_rate_id: nullableNumber(fieldValue(form, "default_sale_vat_rate_id")),
      is_active: !!form.elements.is_active.checked, is_marked: !!(original && original.is_marked)
    };
  }

  function openAdminItemCard(item, onSaved) {
    var snapshot = item ? Object.assign({}, item) : {};
    var modal = document.createElement("div");
    modal.className = "pc-modal";
    modal.innerHTML = '<div class="pc-modal-card pc-catalog-admin-card"><div class="pc-modal-header"><div class="pc-modal-title">' +
      (snapshot.id ? "Карточка товара" : "Новый товар") + '</div><button class="btn btn-outline" data-admin-item-close type="button">Закрыть</button></div>' +
      '<div class="pc-status" data-admin-item-status>Загрузка...</div><div data-admin-item-body></div></div>';
    document.body.appendChild(modal);
    var closed = false;
    var dirty = false;
    var refs = null;
    var body = modal.querySelector("[data-admin-item-body]");
    var status = modal.querySelector("[data-admin-item-status]");
    var disposeDismiss = function () {};

    function close() {
      if (closed) return;
      closed = true;
      disposeDismiss();
      if (openProductCardController && openProductCardController.modal === modal) openProductCardController = null;
      if (modal.parentNode) modal.parentNode.removeChild(modal);
    }

    function refreshAuthoritative() {
      if (!snapshot.id) return Promise.resolve();
      return deps.fetchJson("/api/items?include_inactive=1").then(function (items) {
        var fresh = (items || []).find(function (row) { return Number(row.id) === Number(snapshot.id); });
        if (fresh) snapshot = Object.assign({}, fresh);
      });
    }

    function renderEditor() {
      if (closed) return;
      status.textContent = "";
      body.innerHTML = renderAdminItemForm(snapshot, refs) +
        '<section class="pc-nested-editor" data-packaging-editor>' + (snapshot.id ? "Загрузка упаковок..." : "Сначала сохраните товар.") + '</section>' +
        '<section class="pc-nested-editor" data-price-editor>' + (snapshot.id ? "Загрузка цен..." : "Сначала сохраните товар.") + '</section>';
      var form = body.querySelector("[data-item-form]");
      selectUomCode(form, refs, snapshot);
      wireItemTypeApplicability(form, refs);
      form.addEventListener("input", function () { dirty = true; });
      form.addEventListener("change", function () { dirty = true; });
      form.addEventListener("submit", function (event) {
        event.preventDefault();
        var payload = buildItemPayload(form, refs, snapshot);
        status.textContent = "Сохранение...";
        requestJson(snapshot.id ? "/api/items/" + snapshot.id : "/api/items", "POST", payload)
          .then(function (result) {
            if (!snapshot.id) snapshot.id = Number(result && result.item_id) || 0;
            dirty = false;
            return refreshAuthoritative();
          })
          .then(function () {
            renderEditor();
            if (onSaved) onSaved();
          })
          .catch(function (error) { status.textContent = ""; reportCatalogError(error, "Не удалось сохранить товар"); });
      });
      var deleteButton = body.querySelector("[data-item-delete]");
      if (deleteButton) deleteButton.addEventListener("click", function () {
        if (!window.confirm("Удалить товар? Сервер отклонит удаление, если товар используется.")) return;
        requestJson("/api/items/" + snapshot.id, "DELETE").then(function () { close(); if (onSaved) onSaved(); })
          .catch(function (error) { reportCatalogError(error, "Не удалось удалить товар"); });
      });
      if (snapshot.id) {
        wirePackagingEditor(body.querySelector("[data-packaging-editor]"), snapshot);
        wirePriceEditor(body.querySelector("[data-price-editor]"), snapshot);
      }
    }

    loadAdminItemReferences().then(function (loaded) { refs = loaded; renderEditor(); })
      .catch(function (error) { status.textContent = "Ошибка загрузки справочников"; reportCatalogError(error); });
    disposeDismiss = deps.bindModalDismiss(modal, function () {
      if (!dirty || window.confirm("Закрыть карточку и потерять несохранённые изменения?")) close();
    });
    modal.querySelector("[data-admin-item-close]").addEventListener("click", function () {
      if (!dirty || window.confirm("Закрыть карточку и потерять несохранённые изменения?")) close();
    });
    openProductCardController = { modal: modal, itemId: Number(snapshot.id) || 0, close: close, isDirty: function () { return dirty; } };
    return openProductCardController;
  }

  function openCompactCatalogEditor(html, buildPayload, onSubmit, errorTitle) {
    var modal = document.createElement("div");
    modal.className = "pc-modal";
    modal.innerHTML = html;
    document.body.appendChild(modal);
    var form = modal.querySelector("[data-compact-editor-form]");
    var status = modal.querySelector("[data-compact-editor-status]");
    var closed = false;
    var disposeDismiss = function () {};
    function close() {
      if (closed) return;
      closed = true;
      disposeDismiss();
      if (modal.parentNode) modal.parentNode.removeChild(modal);
    }
    form.addEventListener("submit", function (event) {
      event.preventDefault();
      status.textContent = "Сохранение...";
      Promise.resolve().then(function () { return onSubmit(buildPayload(form)); }).then(close).catch(function (error) {
        status.textContent = "";
        reportCatalogError(error, errorTitle);
      });
    });
    modal.querySelector("[data-compact-editor-close]").addEventListener("click", close);
    modal.querySelector("[data-compact-editor-cancel]").addEventListener("click", close);
    disposeDismiss = deps.bindModalDismiss(modal, close);
    return { modal: modal, close: close };
  }

  function renderPackagingEditorForm(row) {
    return '<div class="pc-modal-card pc-reference-editor-card"><div class="pc-modal-header"><div class="pc-modal-title">Редактировать упаковку</div>' +
      '<button class="btn btn-outline" data-compact-editor-close type="button">Закрыть</button></div>' +
      '<form data-compact-editor-form><div class="pc-form-grid">' +
      '<div class="form-field"><label class="form-label">Код *</label><input class="form-input" name="code" required value="' + deps.escapeHtml(row.code || "") + '"></div>' +
      '<div class="form-field"><label class="form-label">Название *</label><input class="form-input" name="name" required value="' + deps.escapeHtml(row.name || "") + '"></div>' +
      '<div class="form-field"><label class="form-label">Коэффициент *</label><input class="form-input" name="factor_to_base" type="number" min="0.001" step="0.001" required value="' + deps.escapeHtml(String(row.factor_to_base == null ? "" : row.factor_to_base)) + '"></div>' +
      '<div class="form-field"><label class="form-label">Порядок</label><input class="form-input" name="sort_order" type="number" step="1" value="' + deps.escapeHtml(String(row.sort_order == null ? 0 : row.sort_order)) + '"></div>' +
      '</div><div class="pc-status" data-compact-editor-status></div><div class="pc-modal-actions">' +
      '<button class="btn btn-outline" data-compact-editor-cancel type="button">Отмена</button>' +
      '<button class="btn btn-primary" type="submit">Сохранить</button></div></form></div>';
  }

  function openPackagingEditor(item, row, onSubmit) {
    return openCompactCatalogEditor(renderPackagingEditorForm(row), function (form) {
      return {
        item_id: item.id,
        code: fieldValue(form, "code"),
        name: fieldValue(form, "name"),
        factor_to_base: nullableNumber(fieldValue(form, "factor_to_base")),
        sort_order: Number(fieldValue(form, "sort_order")) || 0,
        is_active: row.is_active !== false,
      };
    }, onSubmit, "Не удалось изменить упаковку");
  }

  function wirePackagingEditor(container, item) {
    deps.fetchJson("/api/packagings?item_id=" + encodeURIComponent(item.id) + "&include_inactive=1").then(function (rows) {
      if (!container || container.isConnected === false) return;
      rows = Array.isArray(rows) ? rows : [];
      function reload() { wirePackagingEditor(container, item); }
      function refreshDefaultPackaging() {
        return deps.fetchJson("/api/items?include_inactive=1").then(function (items) {
          var fresh = (items || []).find(function (row) { return Number(row.id) === Number(item.id); });
          if (fresh) Object.assign(item, fresh);
        });
      }
      container.innerHTML = '<div class="pc-product-prices-title">Упаковки</div>' +
        '<form class="pc-inline-form" data-package-create><input class="form-input" name="code" placeholder="Код" required>' +
        '<input class="form-input" name="name" placeholder="Название" required><input class="form-input" name="factor" type="number" min="0.001" step="0.001" placeholder="Коэффициент" required>' +
        '<input class="form-input" name="sort" type="number" step="1" value="0"><button class="btn btn-success" type="submit">Добавить</button></form>' +
        (rows.length ? '<div class="pc-compact-list">' + rows.map(function (row) {
          return '<div class="pc-compact-row"><div><strong>' + deps.escapeHtml(row.name || "") + '</strong> · ' + deps.escapeHtml(row.code || "") +
            ' · ×' + deps.escapeHtml(String(row.factor_to_base)) + (Number(item.default_packaging_id) === Number(row.id) ? ' · по умолчанию' : '') +
            (row.is_active === false ? ' · неактивна' : '') + '</div><div class="pc-row-actions">' +
            '<button class="btn btn-outline btn-sm" data-package-edit="' + row.id + '" type="button">Редактировать</button>' +
            '<button class="btn btn-outline btn-sm" data-package-default="' + row.id + '" type="button">По умолчанию</button>' +
            '<button class="btn btn-danger btn-sm" data-package-deactivate="' + row.id + '" type="button">Деактивировать</button></div></div>';
        }).join("") + '</div>' : '<div class="empty-state">Упаковки не заданы.</div>');
      container.querySelector("[data-package-create]").addEventListener("submit", function (event) {
        event.preventDefault(); var form = event.currentTarget;
        requestJson("/api/packagings", "POST", { item_id: item.id, code: fieldValue(form, "code"), name: fieldValue(form, "name"), factor_to_base: nullableNumber(fieldValue(form, "factor")), sort_order: Number(fieldValue(form, "sort")) || 0, is_active: true })
          .then(reload).catch(function (error) { reportCatalogError(error, "Не удалось добавить упаковку"); });
      });
      container.onclick = function (event) {
        var id = Number(event.target.getAttribute("data-package-edit") || event.target.getAttribute("data-package-default") || event.target.getAttribute("data-package-deactivate"));
        if (!id) return; var row = rows.find(function (value) { return Number(value.id) === id; });
        var operation;
        if (event.target.hasAttribute("data-package-default")) {
          operation = requestJson("/api/items/" + item.id + "/default-packaging", "POST", { packaging_id: id })
            .then(refreshDefaultPackaging);
        }
        else if (event.target.hasAttribute("data-package-deactivate")) {
          if (!window.confirm("Деактивировать упаковку?")) return;
          operation = requestJson("/api/packagings/" + id, "DELETE");
        }
        else {
          openPackagingEditor(item, row, function (payload) {
            return requestJson("/api/packagings/" + id, "POST", payload).then(reload);
          });
          return;
        }
        operation.then(reload).catch(function (error) { reportCatalogError(error, "Не удалось изменить упаковку"); });
      };
    }).catch(function (error) { container.innerHTML = '<div class="pc-status">Не удалось загрузить упаковки.</div>'; reportCatalogError(error); });
  }

  function renderPriceEditorForm(row) {
    return '<div class="pc-modal-card pc-reference-editor-card"><div class="pc-modal-header"><div class="pc-modal-title">Редактировать индивидуальную цену</div>' +
      '<button class="btn btn-outline" data-compact-editor-close type="button">Закрыть</button></div>' +
      '<form data-compact-editor-form><div class="pc-form-grid">' +
      '<div class="pc-readonly-field pc-form-wide">Клиент: <strong>' + deps.escapeHtml(row.partner_name || "") + '</strong></div>' +
      '<div class="form-field"><label class="form-label">Цена с НДС *</label><input class="form-input" name="unit_price_gross" type="number" min="0" step="0.0001" required value="' + deps.escapeHtml(String(row.unit_price_gross == null ? "" : row.unit_price_gross)) + '"></div>' +
      '<label class="pc-checkbox"><input name="is_active" type="checkbox"' + (row.is_active ? " checked" : "") + '> Активна</label>' +
      '</div><div class="pc-status" data-compact-editor-status></div><div class="pc-modal-actions">' +
      '<button class="btn btn-outline" data-compact-editor-cancel type="button">Отмена</button>' +
      '<button class="btn btn-primary" type="submit">Сохранить</button></div></form></div>';
  }

  function openPriceEditor(item, row, onSubmit) {
    return openCompactCatalogEditor(renderPriceEditorForm(row), function (form) {
      return {
        partner_id: row.partner_id,
        item_id: item.id,
        unit_price_gross: nullableNumber(fieldValue(form, "unit_price_gross")),
        is_active: !!form.elements.is_active.checked,
      };
    }, onSubmit, "Не удалось изменить цену");
  }

  function wirePriceEditor(container, item) {
    Promise.all([deps.fetchJson(buildCustomerPricesUrl(item.id, 0)), deps.fetchJson("/api/partners?role=customer")]).then(function (payloads) {
      if (!container || container.isConnected === false) return;
      var rows = (payloads[0] && payloads[0].items) || []; var partners = payloads[1] || [];
      function reload() { wirePriceEditor(container, item); }
      container.innerHTML = '<div class="pc-product-prices-title">Индивидуальные цены клиентов</div>' +
        '<form class="pc-inline-form" data-price-create><select class="form-input" name="partner_id" required><option value="">Клиент</option>' + optionRows(partners, 0, function (row) { return row.name || ""; }) + '</select>' +
        '<input class="form-input" name="price" type="number" min="0" step="0.0001" placeholder="Цена" required><label class="pc-checkbox"><input name="active" type="checkbox" checked> Активна</label><button class="btn btn-success" type="submit">Добавить</button></form>' +
        (rows.length ? '<div class="pc-compact-list">' + rows.map(function (row) { return '<div class="pc-compact-row"><div><strong>' + deps.escapeHtml(row.partner_name || "") + '</strong> · ' + deps.escapeHtml(String(row.unit_price_gross)) + (row.is_active ? '' : ' · неактивна') + '</div><div class="pc-row-actions"><button class="btn btn-outline btn-sm" data-price-edit="' + row.id + '" type="button">Редактировать</button>' + (!row.is_active ? '<button class="btn btn-danger btn-sm" data-price-delete="' + row.id + '" type="button">Удалить</button>' : '') + '</div></div>'; }).join("") + '</div>' : '<div class="empty-state">Цены не заданы.</div>');
      container.querySelector("[data-price-create]").addEventListener("submit", function (event) {
        event.preventDefault(); var form = event.currentTarget;
        requestJson("/api/partner-item-sale-prices", "POST", { partner_id: Number(fieldValue(form, "partner_id")), item_id: item.id, unit_price_gross: nullableNumber(fieldValue(form, "price")), is_active: !!form.elements.active.checked })
          .then(reload).catch(function (error) { reportCatalogError(error, "Не удалось добавить цену"); });
      });
      container.onclick = function (event) {
        var editId = Number(event.target.getAttribute("data-price-edit")); var deleteId = Number(event.target.getAttribute("data-price-delete"));
        if (deleteId) {
          if (!window.confirm("Окончательно удалить индивидуальную цену клиента?")) return;
          requestJson("/api/partner-item-sale-prices/" + deleteId, "DELETE").then(reload).catch(function (error) { reportCatalogError(error, "Не удалось удалить цену"); });
          return;
        }
        if (editId) {
          var row = rows.find(function (value) { return Number(value.id) === editId; });
          openPriceEditor(item, row, function (payload) {
            return requestJson("/api/partner-item-sale-prices/" + editId, "POST", payload).then(reload);
          });
        }
      };
    }).catch(function (error) { container.innerHTML = '<div class="pc-status">Не удалось загрузить цены.</div>'; reportCatalogError(error); });
  }

  function referenceEndpoint(kind) {
    if (kind === "item-types" || kind === "vat-rates") return "/api/" + kind + (canManageCatalog() ? "?include_inactive=1" : "");
    return "/api/" + kind;
  }

  function partnerRoleLabel(value) {
    var normalized = String(value || "Both").toLocaleLowerCase("ru");
    if (normalized === "supplier") return "Поставщик";
    if (normalized === "client") return "Клиент";
    return "Поставщик + клиент";
  }

  function referenceLabel(kind, row) {
    if (kind === "vat-rates") return (row.name || "") + " — " + row.rate + "%";
    if (kind === "item-types") return (row.name || "") + (row.code ? " · " + row.code : "");
    if (kind === "partners") return (row.name || "") + (row.code ? " · ИНН " + row.code : "") + " · " + partnerRoleLabel(row.status);
    return row.name || "";
  }

  function renderReferenceRows(kind, rows, admin) {
    if (!rows.length) return '<div class="empty-state">Записи не найдены.</div>';
    return '<div class="pc-compact-list">' + rows.map(function (row) {
      var canEdit = admin;
      return '<div class="pc-compact-row"><div><strong>' + deps.escapeHtml(referenceLabel(kind, row)) + '</strong>' +
        (row.is_active === false ? ' <span class="pc-status-badge is-inactive">Неактивно</span>' : '') + '</div>' +
        (admin ? '<div class="pc-row-actions">' + (canEdit ? '<button class="btn btn-outline btn-sm" data-reference-edit="' + row.id + '" type="button">Редактировать</button>' : '') +
        '<button class="btn btn-danger btn-sm" data-reference-delete="' + row.id + '" type="button">Удалить</button></div>' : '') + '</div>';
    }).join("") + '</div>';
  }

  function checkedAttribute(value, defaultValue) {
    return (value == null ? defaultValue : value === true) ? " checked" : "";
  }

  function isModalReferenceKind(kind) {
    return kind === "uoms" || kind === "taras" || kind === "partners" || kind === "item-types" || kind === "vat-rates";
  }

  function renderReferenceEditorForm(kind, row) {
    var current = row || {};
    var entityLabel = kind === "uoms" ? "единицу измерения" : kind === "taras" ? "тару" : kind === "partners" ? "контрагента" : kind === "vat-rates" ? "ставку НДС" : "тип номенклатуры";
    var title = (row ? "Редактировать " : "Добавить ") + entityLabel;
    var fields = '<div class="pc-form-grid">' +
      '<div class="form-field pc-form-wide"><label class="form-label">Название *</label><input class="form-input" name="name" required value="' + deps.escapeHtml(current.name || "") + '"></div>';
    if (kind === "partners") {
      var role = String(current.status || "Both").toLocaleLowerCase("ru");
      fields += '<div class="form-field"><label class="form-label">ИНН</label><input class="form-input" name="code" inputmode="numeric" value="' + deps.escapeHtml(current.code || "") + '"></div>' +
        '<div class="form-field"><label class="form-label">Роль *</label><select class="form-input" name="status" required>' +
        '<option value="Supplier"' + (role === "supplier" ? " selected" : "") + '>Поставщик</option>' +
        '<option value="Client"' + (role === "client" ? " selected" : "") + '>Клиент</option>' +
        '<option value="Both"' + (role === "both" ? " selected" : "") + '>Поставщик + клиент</option></select></div>';
    } else if (kind === "vat-rates") {
      fields += '<div class="form-field"><label class="form-label">Ставка, % *</label><input class="form-input" name="rate" type="number" step="0.0001" required value="' + (current.rate == null ? "" : deps.escapeHtml(String(current.rate))) + '"></div>' +
        '<div class="form-field"><label class="form-label">Порядок</label><input class="form-input" name="sort_order" type="number" step="1" value="' + deps.escapeHtml(String(current.sort_order == null ? 0 : current.sort_order)) + '"></div>' +
        '<label class="pc-checkbox"><input name="is_active" type="checkbox"' + checkedAttribute(current.is_active, true) + '> Активна</label>';
    } else if (kind === "item-types") {
      fields += '<div class="form-field"><label class="form-label">Код</label><input class="form-input" name="code" value="' + deps.escapeHtml(current.code || "") + '"></div>' +
        '<div class="form-field"><label class="form-label">Порядок</label><input class="form-input" name="sort_order" type="number" step="1" value="' + deps.escapeHtml(String(current.sort_order == null ? 0 : current.sort_order)) + '"></div>' +
        '<div class="pc-reference-flags pc-form-wide">' +
        '<label class="pc-checkbox"><input name="is_active" type="checkbox"' + checkedAttribute(current.is_active, true) + '> Активен</label>' +
        '<label class="pc-checkbox"><input name="is_visible_in_product_catalog" type="checkbox"' + checkedAttribute(current.is_visible_in_product_catalog, true) + '> Показывать в продуктовом каталоге</label>' +
        '<label class="pc-checkbox"><input name="enable_min_stock_control" type="checkbox"' + checkedAttribute(current.enable_min_stock_control, false) + '> Контроль минимального остатка</label>' +
        '<label class="pc-checkbox"><input name="min_stock_uses_order_binding" type="checkbox"' + checkedAttribute(current.min_stock_uses_order_binding, false) + '> Учитывать привязки заказов в min stock</label>' +
        '<label class="pc-checkbox"><input name="enable_order_reservation" type="checkbox"' + checkedAttribute(current.enable_order_reservation, false) + '> Резервирование заказов</label>' +
        '<label class="pc-checkbox"><input name="enable_hu_distribution" type="checkbox"' + checkedAttribute(current.enable_hu_distribution, false) + '> Распределение по HU</label>' +
        '<label class="pc-checkbox"><input name="enable_marking" type="checkbox"' + checkedAttribute(current.enable_marking, false) + '> Маркировка</label></div>';
    }
    return '<div class="pc-modal-card pc-reference-editor-card"><div class="pc-modal-header"><div class="pc-modal-title">' + title + '</div>' +
      '<button class="btn btn-outline" data-reference-editor-close type="button">Закрыть</button></div>' +
      '<form data-reference-form>' + fields + '</div><div class="pc-status" data-reference-editor-status></div>' +
      '<div class="pc-modal-actions"><button class="btn btn-outline" data-reference-editor-cancel type="button">Отмена</button>' +
      '<button class="btn ' + (row ? "btn-primary" : "btn-success") + '" type="submit">Сохранить</button></div></form></div>';
  }

  function buildReferenceEditorPayload(kind, form) {
    if (kind === "uoms" || kind === "taras") {
      return { name: fieldValue(form, "name") };
    }
    if (kind === "partners") {
      return {
        name: fieldValue(form, "name"),
        code: fieldValue(form, "code") || null,
        status: fieldValue(form, "status"),
      };
    }
    if (kind === "vat-rates") {
      return {
        name: fieldValue(form, "name"),
        rate: Number(String(fieldValue(form, "rate")).replace(",", ".")),
        sort_order: Number(fieldValue(form, "sort_order")) || 0,
        is_active: !!form.elements.is_active.checked,
      };
    }
    return {
      name: fieldValue(form, "name"),
      code: fieldValue(form, "code") || null,
      sort_order: Number(fieldValue(form, "sort_order")) || 0,
      is_active: !!form.elements.is_active.checked,
      is_visible_in_product_catalog: !!form.elements.is_visible_in_product_catalog.checked,
      enable_min_stock_control: !!form.elements.enable_min_stock_control.checked,
      min_stock_uses_order_binding: !!form.elements.min_stock_uses_order_binding.checked,
      enable_order_reservation: !!form.elements.enable_order_reservation.checked,
      enable_hu_distribution: !!form.elements.enable_hu_distribution.checked,
      enable_marking: !!form.elements.enable_marking.checked,
    };
  }

  function openReferenceEditor(kind, row, onSubmit, onClosed) {
    var modal = document.createElement("div");
    modal.className = "pc-modal";
    modal.innerHTML = renderReferenceEditorForm(kind, row);
    document.body.appendChild(modal);
    var form = modal.querySelector("[data-reference-form]");
    var status = modal.querySelector("[data-reference-editor-status]");
    var closed = false;
    var disposeDismiss = function () {};
    function close() {
      if (closed) return;
      closed = true;
      disposeDismiss();
      if (modal.parentNode) modal.parentNode.removeChild(modal);
      if (onClosed) onClosed();
    }
    form.addEventListener("submit", function (event) {
      event.preventDefault();
      var payload = buildReferenceEditorPayload(kind, form);
      status.textContent = "Сохранение...";
      Promise.resolve().then(function () { return onSubmit(payload); }).then(close).catch(function (error) {
        status.textContent = "";
        reportCatalogError(error, "Не удалось сохранить запись");
      });
    });
    modal.querySelector("[data-reference-editor-close]").addEventListener("click", close);
    modal.querySelector("[data-reference-editor-cancel]").addEventListener("click", close);
    disposeDismiss = deps.bindModalDismiss(modal, close);
    return { modal: modal, close: close };
  }

  function referenceTitle(kind) {
    return kind === "uoms" ? "Единицы измерения" :
      kind === "taras" ? "Тара" :
      kind === "item-types" ? "Типы номенклатуры" :
      kind === "vat-rates" ? "Ставки НДС" : "Контрагенты";
  }

  function wireReferences(root) {
    var kindButtons = root.querySelectorAll("[data-reference-kind]");
    var searchInput = root.querySelector("[data-reference-search]");
    var table = root.querySelector("[data-reference-table]");
    var status = root.querySelector("[data-reference-status]");
    var title = root.querySelector("[data-reference-title]");
    var createButton = root.querySelector("[data-reference-create]");
    var refreshButton = root.querySelector("[data-reference-refresh]");
    var rows = [];
    var selectedKind = "uoms";
    var openEditorController = null;
    function closeEditor() {
      if (!openEditorController) return;
      var controller = openEditorController;
      openEditorController = null;
      controller.close();
    }
    function openEditor(selectedKind, row, onSubmit) {
      closeEditor();
      var controller = openReferenceEditor(selectedKind, row, onSubmit, function () {
        if (openEditorController === controller) openEditorController = null;
      });
      openEditorController = controller;
    }
    function kind() { return selectedKind; }
    function render() {
      var query = String(searchInput && searchInput.value || "").trim().toLocaleLowerCase("ru");
      var filtered = rows.filter(function (row) { return !query || referenceLabel(kind(), row).toLocaleLowerCase("ru").indexOf(query) >= 0; });
      table.innerHTML = renderReferenceRows(kind(), filtered, canManageCatalog());
    }
    function load() {
      if (title) title.textContent = referenceTitle(kind());
      status.textContent = "Загрузка...";
      return deps.fetchJson(referenceEndpoint(kind())).then(function (payload) { rows = Array.isArray(payload) ? payload : []; status.textContent = "Записей: " + rows.length; render(); })
        .catch(function (error) { status.textContent = "Ошибка загрузки"; table.innerHTML = '<div class="empty-state">Данные недоступны.</div>'; reportCatalogError(error); });
    }
    kindButtons.forEach(function (button) {
      button.addEventListener("click", function () {
        selectedKind = button.getAttribute("data-reference-kind") || "uoms";
        kindButtons.forEach(function (candidate) { candidate.classList.toggle("is-active", candidate === button); });
        if (searchInput) searchInput.value = "";
        load();
      });
    });
    if (searchInput) searchInput.addEventListener("input", render);
    if (refreshButton) refreshButton.addEventListener("click", load);
    if (createButton) createButton.addEventListener("click", function () {
      var selectedKind = kind();
      if (isModalReferenceKind(selectedKind)) {
        openEditor(selectedKind, null, function (payload) {
          return requestJson("/api/" + selectedKind, "POST", payload).then(load);
        });
      }
    });
    if (table) table.addEventListener("click", function (event) {
      var editId = Number(event.target.getAttribute("data-reference-edit")); var deleteId = Number(event.target.getAttribute("data-reference-delete"));
      if (editId) {
        var selectedKind = kind();
        var row = rows.find(function (value) { return Number(value.id) === editId; });
        if (isModalReferenceKind(selectedKind)) {
          openEditor(selectedKind, row, function (payload) {
            return requestJson("/api/" + selectedKind + "/" + editId, "POST", payload).then(load);
          });
        }
      }
      if (deleteId && window.confirm("Удалить запись? Сервер проверит её использование.")) requestJson("/api/" + kind() + "/" + deleteId, "DELETE").then(load).catch(function (error) { reportCatalogError(error, "Не удалось удалить запись"); });
    });
    load();
    return { closeEditor: closeEditor };
  }

  function openReferencesModal() {
    if (!canManageCatalog()) {
      return null;
    }
    if (openReferencesController && openReferencesController.modal && openReferencesController.modal.isConnected !== false) {
      return openReferencesController;
    }

    var modal = document.createElement("div");
    modal.className = "pc-modal";
    modal.innerHTML = '<div class="pc-modal-card pc-references-modal-card"><div class="pc-modal-header">' +
      '<div class="pc-modal-title">Справочники</div><button class="btn btn-outline" data-references-close type="button">Закрыть</button></div>' +
      renderReferencesShell(true) + '</div>';
    document.body.appendChild(modal);
    var closed = false;
    var disposeDismiss = function () {};
    var referencesController = null;
    function close() {
      if (closed) return;
      closed = true;
      if (referencesController) referencesController.closeEditor();
      disposeDismiss();
      if (modal.parentNode) modal.parentNode.removeChild(modal);
      if (openReferencesController && openReferencesController.modal === modal) openReferencesController = null;
    }
    modal.querySelector("[data-references-close]").addEventListener("click", close);
    disposeDismiss = deps.bindModalDismiss(modal, close);
    referencesController = wireReferences(modal);
    openReferencesController = { modal: modal, close: close };
    return openReferencesController;
  }

  function closeReferencesModal() {
    if (openReferencesController) openReferencesController.close();
  }

  function openProductCard(item) {
    if (canManageCatalog()) {
      return openAdminItemCard(item, deps.reloadCatalog);
    }
    if (!item) {
      return null;
    }
    var snapshot = Object.assign({}, item);
    var typeSnapshot = Object.assign({}, findItemType(item) || {});
    var modal = document.createElement("div");
    modal.className = "pc-modal";
    modal.innerHTML = renderProductCardContent(snapshot, typeSnapshot);
    document.body.appendChild(modal);

    var closed = false;
    var disposeDismiss = function () {};
    var pricesState = { items: [], totalCount: 0, loaded: false, loading: false, error: false };
    var pricesSection = modal.querySelector("[data-product-prices]");
    var pricesToggle = modal.querySelector("[data-product-prices-toggle]");

    function close() {
      if (closed) {
        return;
      }
      closed = true;
      disposeDismiss();
      if (openProductCardController && openProductCardController.modal === modal) {
        openProductCardController = null;
      }
      if (modal.parentNode) {
        modal.parentNode.removeChild(modal);
      }
    }

    function renderPrices() {
      if (closed || modal.isConnected === false || !pricesSection) {
        return;
      }
      pricesSection.innerHTML = renderCustomerPrices(pricesState);
      var retry = pricesSection.querySelector("[data-product-prices-retry]");
      var more = pricesSection.querySelector("[data-product-prices-more]");
      if (retry) {
        retry.addEventListener("click", function () {
          loadPrices(pricesState.items.length === 0);
        });
      }
      if (more) {
        more.addEventListener("click", function () {
          loadPrices(false);
        });
      }
    }

    function loadPrices(reset) {
      if (pricesState.loading || closed) {
        return;
      }
      var offset = reset ? 0 : pricesState.items.length;
      pricesState.loading = true;
      pricesState.error = false;
      renderPrices();
      deps.fetchJson(buildCustomerPricesUrl(snapshot.id, offset)).then(function (payload) {
        if (closed || modal.isConnected === false) {
          return;
        }
        var nextRows = Array.isArray(payload && payload.items) ? payload.items : [];
        pricesState.items = reset ? nextRows.slice() : pricesState.items.concat(nextRows);
        pricesState.totalCount = Number(payload && payload.total_count) || pricesState.items.length;
        pricesState.loaded = true;
        pricesState.error = false;
      }).catch(function () {
        if (closed || modal.isConnected === false) {
          return;
        }
        pricesState.loaded = true;
        pricesState.error = true;
      }).finally(function () {
        if (closed || modal.isConnected === false) {
          return;
        }
        pricesState.loading = false;
        renderPrices();
      });
    }

    disposeDismiss = deps.bindModalDismiss(modal, close);
    modal.querySelector("[data-product-card-close]").addEventListener("click", close);
    if (pricesToggle && pricesSection) {
      pricesToggle.addEventListener("click", function () {
        var expanded = pricesSection.hidden;
        pricesSection.hidden = !expanded;
        pricesToggle.setAttribute("aria-expanded", expanded ? "true" : "false");
        pricesToggle.textContent = expanded ? "Скрыть цены клиентов" : "Цены клиентов";
        if (expanded && !pricesState.loaded && !pricesState.loading) {
          loadPrices(true);
        }
      });
    }

    openProductCardController = { modal: modal, itemId: Number(snapshot.id) || 0, close: close };
    return openProductCardController;
  }

  function wireCatalog() {
    var searchInput = document.getElementById("catalogSearchInput");
    var typeFilter = document.getElementById("catalogTypeFilter");
    var brandFilter = document.getElementById("catalogBrandFilter");
    var volumeFilter = document.getElementById("catalogVolumeFilter");
    var activityFilter = document.getElementById("catalogActivityFilter");
    var createBtn = document.getElementById("catalogCreateBtn");
    var refreshBtn = document.getElementById("catalogRefreshBtn");
    var statusEl = document.getElementById("catalogStatus");
    var tableWrap = document.getElementById("catalogTableWrap");
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
      var rows = filterCatalogRows(buildCatalogRows(cachedItems), {
        query: deps.normalizeSearchQuery(searchInput ? searchInput.value : ""),
        typeId: typeFilter ? Number(typeFilter.value || 0) : 0,
        brand: brandFilter ? String(brandFilter.value || "") : "",
        volume: volumeFilter ? String(volumeFilter.value || "") : "",
        activity: activityFilter ? String(activityFilter.value || "") : "",
      });
      rows = deps.sortRows(rows, "catalog", {
        itemId: { type: "number", getValue: function (row) { return row.itemId; } },
        itemName: { type: "string", getValue: function (row) { return row.itemName; } },
        brand: { type: "string", getValue: function (row) { return row.brand; } },
        volume: { type: "string", getValue: function (row) { return row.volume; } },
        barcode: { type: "string", getValue: function (row) { return row.barcode; } },
        gtin: { type: "string", getValue: function (row) { return row.gtin; } },
        baseUom: { type: "string", getValue: function (row) { return row.baseUom; } },
      });
      setStatus("Товаров: " + rows.length);
      tableWrap.innerHTML = renderCatalogTable(rows);
      deps.bindTableSorting(tableWrap, "catalog", renderRows);
    }

    function fillSelect(select, allLabel, values) {
      if (!select) {
        return;
      }
      var previous = String(select.value || "");
      select.innerHTML = '<option value="">' + deps.escapeHtml(allLabel) + "</option>" +
        values.map(function (value) {
          return '<option value="' + deps.escapeHtml(value) + '">' + deps.escapeHtml(value) + "</option>";
        }).join("");
      select.value = retainAvailableValue(previous, values);
    }

    function fillFilters() {
      var activeTypes = cachedItemTypes.filter(function (type) {
        return type && (canManageCatalog() || type.is_active !== false);
      }).sort(function (left, right) {
        var order = (Number(left.sort_order) || 0) - (Number(right.sort_order) || 0);
        return order !== 0 ? order : String(left.name || "").localeCompare(String(right.name || ""), "ru");
      });
      var typeValues = activeTypes.map(function (type) { return String(Number(type.id) || 0); });
      if (typeFilter) {
        var previousType = String(typeFilter.value || "");
        typeFilter.innerHTML = '<option value="">Все типы</option>' + activeTypes.map(function (type) {
          var id = String(Number(type.id) || 0);
          var name = String(type.name || "").trim() || "Без названия";
          return '<option value="' + deps.escapeHtml(id) + '">' + deps.escapeHtml(name) + "</option>";
        }).join("");
        typeFilter.value = retainAvailableValue(previousType, typeValues);
      }
      fillSelect(brandFilter, "Все бренды", buildExactFilterOptions(cachedItems, "brand"));
      fillSelect(volumeFilter, "Все объёмы", buildExactFilterOptions(cachedItems, "volume"));
    }

    function loadAndRender() {
      setStatus("Загрузка...");
      return loadCatalogData().then(function () {
        fillFilters();
        renderRows();
      }).catch(function () {
        setStatus("Ошибка загрузки каталога");
        if (tableWrap) {
          tableWrap.innerHTML = '<div class="empty-state">Данные недоступны.</div>';
        }
      });
    }

    deps.reloadCatalog = loadAndRender;

    function scheduleRender() {
      if (debounce) {
        clearTimeout(debounce);
      }
      debounce = window.setTimeout(renderRows, 150);
    }

    if (searchInput) searchInput.addEventListener("input", scheduleRender);
    [typeFilter, brandFilter, volumeFilter, activityFilter].forEach(function (filter) {
      if (filter) filter.addEventListener("change", renderRows);
    });
    if (refreshBtn) refreshBtn.addEventListener("click", loadAndRender);
    if (createBtn) createBtn.addEventListener("click", function () { openProductCard(null); });
    if (tableWrap) {
      function activateRow(event) {
        if (event.type === "keydown" && event.key !== "Enter" && event.key !== " ") {
          return;
        }
        var target = event.target;
        while (target && target !== tableWrap && !target.getAttribute("data-catalog-item-id")) {
          target = target.parentNode;
        }
        if (!target || target === tableWrap) {
          return;
        }
        if (event.type === "keydown") {
          event.preventDefault();
        }
        var itemId = Number(target.getAttribute("data-catalog-item-id")) || 0;
        var item = cachedItems.find(function (entry) { return Number(entry.id) === itemId; });
        if (item) openProductCard(item);
      }
      tableWrap.addEventListener("click", activateRow);
      tableWrap.addEventListener("keydown", activateRow);
    }

    deps.setActiveLiveRefreshHandler(loadAndRender);
    loadAndRender();
  }

  window.FlowStockPcCatalog = {
    init: init,
    renderCatalog: renderCatalog,
    wireCatalog: wireCatalog,
    openReferencesModal: openReferencesModal,
    closeReferencesModal: closeReferencesModal,
    loadCatalogData: loadCatalogData,
    testHooks: {
      buildCatalogRows: buildCatalogRows,
      selectVisibleCatalogItems: selectVisibleCatalogItems,
      buildExactFilterOptions: buildExactFilterOptions,
      filterCatalogRows: filterCatalogRows,
      retainAvailableValue: retainAvailableValue,
      renderCatalogTable: renderCatalogTable,
      renderProductCardContent: renderProductCardContent,
      renderCustomerPrices: renderCustomerPrices,
      buildCustomerPricesUrl: buildCustomerPricesUrl,
      formatVatRate: formatVatRate,
      openProductCard: openProductCard,
      canManageCatalog: canManageCatalog,
      renderReferencesShell: renderReferencesShell,
      renderReferenceRows: renderReferenceRows,
      renderReferenceEditorForm: renderReferenceEditorForm,
      renderPackagingEditorForm: renderPackagingEditorForm,
      renderPriceEditorForm: renderPriceEditorForm,
      renderAdminItemForm: renderAdminItemForm,
      buildItemPayload: buildItemPayload,
      selectUomCode: selectUomCode,
      wireItemTypeApplicability: wireItemTypeApplicability,
      wirePriceEditor: wirePriceEditor,
      openReferenceEditor: openReferenceEditor,
      openReferencesModal: openReferencesModal,
      closeReferencesModal: closeReferencesModal,
      getOpenReferencesController: function () { return openReferencesController; },
      partnerRoleLabel: partnerRoleLabel,
      isModalReferenceKind: isModalReferenceKind,
      getOpenProductCardController: function () { return openProductCardController; },
    },
  };
})();
