(function () {
  "use strict";

  var deps = {};
  var cachedItems = [];
  var cachedItemTypes = [];
  var openProductCardController = null;
  var CUSTOMER_PRICE_PAGE_SIZE = 100;

  function init(nextDeps) {
    deps = nextDeps || {};
  }

  function setCachedItems(items) {
    cachedItems = Array.isArray(items)
      ? items.filter(function (item) {
          return item && item.is_active !== false;
        })
      : [];
  }

  function selectVisibleCatalogItems(items) {
    return (Array.isArray(items) ? items : []).filter(function (item) {
      return item && item.is_active !== false && item.item_type_is_visible_in_product_catalog === true;
    });
  }

  function renderCatalog() {
    return deps.renderPageShell(
      '<section class="pc-card">' +
      '  <div class="section-title">Каталог товаров</div>' +
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
      '    <div class="pc-toolbar-actions">' +
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
      "</tr></thead><tbody>" + body + "</tbody></table>"
    );
  }

  function loadCatalogData() {
    return Promise.all([
      deps.fetchJson("/api/items"),
      deps.fetchJson("/api/item-types?include_inactive=1"),
    ]).then(function (payloads) {
      var items = Array.isArray(payloads[0]) ? payloads[0] : [];
      cachedItemTypes = Array.isArray(payloads[1]) ? payloads[1].slice() : [];
      setCachedItems(selectVisibleCatalogItems(items));
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

  function openProductCard(item) {
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
        return type && type.is_active !== false;
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

    function scheduleRender() {
      if (debounce) {
        clearTimeout(debounce);
      }
      debounce = window.setTimeout(renderRows, 150);
    }

    if (searchInput) searchInput.addEventListener("input", scheduleRender);
    [typeFilter, brandFilter, volumeFilter].forEach(function (filter) {
      if (filter) filter.addEventListener("change", renderRows);
    });
    if (refreshBtn) refreshBtn.addEventListener("click", loadAndRender);
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
      getOpenProductCardController: function () { return openProductCardController; },
    },
  };
})();
