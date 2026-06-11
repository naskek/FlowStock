(function () {
  "use strict";

  var deps = {};
  var cachedItems = [];
  var cachedItemTypes = [];

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

  function renderCatalog() {
    return deps.renderPageShell(
      '<section class="pc-card">' +
      '  <div class="section-title">Каталог товаров</div>' +
      '  <div class="pc-toolbar">' +
      '    <div class="form-field">' +
      '      <label class="form-label" for="catalogSearchInput">Поиск</label>' +
      '      <input class="form-input" id="catalogSearchInput" type="text" autocomplete="off" placeholder="Название, бренд, объем, SKU, GTIN, штрихкод" />' +
      "    </div>" +
      '    <div class="form-field">' +
      '      <label class="form-label" for="catalogTypeFilter">Тип</label>' +
      '      <select class="form-input" id="catalogTypeFilter"></select>' +
      "    </div>" +
      '    <div class="pc-toolbar-actions">' +
      '      <button id="catalogRefreshBtn" class="btn btn-outline" type="button">Обновить</button>' +
      "    </div>" +
      '    <div id="catalogStatus" class="pc-status"></div>' +
      "  </div>" +
      '  <div id="catalogTableWrap"></div>' +
      "</section>"
    );
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
      '<div class="pc-catalog-item-name">' +
      name +
      "</div>" +
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
        return (
          "<tr>" +
          "<td>" +
          renderCatalogItemCell(row) +
          "</td>" +
          "<td>" +
          deps.escapeHtml(row.brand || "-") +
          "</td>" +
          "<td>" +
          deps.escapeHtml(row.volume || "-") +
          "</td>" +
          "<td>" +
          deps.escapeHtml(row.baseUom || "-") +
          "</td>" +
          "</tr>"
        );
      })
      .join("");

    return (
      '<table class="pc-table pc-catalog-table">' +
      "<thead><tr>" +
      deps.renderSortableHeader("catalog", "itemName", "Товар") +
      deps.renderSortableHeader("catalog", "brand", "Бренд") +
      deps.renderSortableHeader("catalog", "volume", "Объем") +
      deps.renderSortableHeader("catalog", "baseUom", "Ед.") +
      "</tr></thead>" +
      "<tbody>" +
      body +
      "</tbody>" +
      "</table>"
    );
  }

  function loadCatalogData() {
    return Promise.all([
      deps.fetchJson("/api/items"),
      deps.fetchJson("/api/item-types?include_inactive=0"),
    ]).then(function (payloads) {
      var items = payloads[0];
      var itemTypes = payloads[1];
      cachedItemTypes = Array.isArray(itemTypes) ? itemTypes.slice() : [];
      var sourceItems = Array.isArray(items) ? items : [];
      var visibleItems = sourceItems.filter(function (item) {
        return item && item.item_type_is_visible_in_product_catalog === true;
      });
      if (visibleItems.length === 0 && sourceItems.length > 0) {
        // Fallback for legacy/inconsistent type visibility setup:
        // keep catalog usable instead of showing an empty screen.
        visibleItems = sourceItems.slice();
      }
      setCachedItems(visibleItems);
      return cachedItems;
    });
  }

  function wireCatalog() {
    var searchInput = document.getElementById("catalogSearchInput");
    var typeFilter = document.getElementById("catalogTypeFilter");
    var refreshBtn = document.getElementById("catalogRefreshBtn");
    var statusEl = document.getElementById("catalogStatus");
    var tableWrap = document.getElementById("catalogTableWrap");
    var debounce = null;

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text || "";
      }
    }

    function buildRows() {
      return cachedItems
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
          var leftName = String(left.itemName || "").toLowerCase();
          var rightName = String(right.itemName || "").toLowerCase();
          if (leftName !== rightName) {
            return leftName < rightName ? -1 : 1;
          }
          return left.itemId - right.itemId;
        });
    }

    function renderRows() {
      if (!tableWrap) {
        return;
      }

      var query = deps.normalizeSearchQuery(searchInput ? searchInput.value : "");
      var typeId = typeFilter ? Number(typeFilter.value || 0) : 0;
      var rows = buildRows().filter(function (row) {
        if (typeId && row.itemTypeId !== typeId) {
          return false;
        }
        return deps.matchesItemSearch(row, query, false);
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

    function fillTypeFilter() {
      if (!typeFilter) {
        return;
      }

      var previous = String(typeFilter.value || "");
      var options =
        '<option value="">Все типы</option>' +
        cachedItemTypes
          .slice()
          .sort(function (left, right) {
            var leftOrder = Number(left && left.sort_order) || 0;
            var rightOrder = Number(right && right.sort_order) || 0;
            if (leftOrder !== rightOrder) {
              return leftOrder - rightOrder;
            }
            var leftName = String((left && left.name) || "").toLowerCase();
            var rightName = String((right && right.name) || "").toLowerCase();
            return leftName < rightName ? -1 : leftName > rightName ? 1 : 0;
          })
          .map(function (type) {
            var id = Number(type && type.id) || 0;
            var name = String((type && type.name) || "").trim() || "Без названия";
            return '<option value="' + deps.escapeHtml(String(id)) + '">' + deps.escapeHtml(name) + "</option>";
          })
          .join("");

      typeFilter.innerHTML = options;
      if (previous) {
        var hasPrevious = Array.prototype.some.call(typeFilter.options || [], function (option) {
          return String(option.value || "") === previous;
        });
        if (hasPrevious) {
          typeFilter.value = previous;
        }
      }
    }

    function loadAndRender() {
      setStatus("Загрузка...");
      loadCatalogData()
        .then(function () {
          fillTypeFilter();
          renderRows();
        })
        .catch(function () {
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

    if (searchInput) {
      searchInput.addEventListener("input", scheduleRender);
    }
    if (typeFilter) {
      typeFilter.addEventListener("change", renderRows);
    }
    if (refreshBtn) {
      refreshBtn.addEventListener("click", loadAndRender);
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
      renderCatalogTable: renderCatalogTable,
    },
  };
})();
