(function () {
  "use strict";

  var deps = {};
  var CLIENT_BLOCK_HEADER = "X-FlowStock-Block-Key";
  var tableSortState = {
    stock: { key: "", direction: "asc" },
    catalog: { key: "", direction: "asc" },
    orders: { key: "", direction: "asc" },
    productionNeed: { key: "", direction: "asc" },
  };

  function init(shared) {
    deps = shared || {};
  }

  function shouldAttachBlockHeader(url) {
    return url !== "/api/client-blocks" && url !== "/api/tsd/login";
  }

  function createRequestHeaders(source, url) {
    var headers = new Headers(source || {});
    var currentView = deps.getCurrentView ? deps.getCurrentView() : "";
    var blockKey =
      shouldAttachBlockHeader(url) && deps.getBlockKeyForView
        ? deps.getBlockKeyForView(currentView)
        : "";
    if (blockKey && !headers.has(CLIENT_BLOCK_HEADER)) {
      headers.set(CLIENT_BLOCK_HEADER, blockKey);
    }
    return headers;
  }

  function fetchJson(url, options) {
    var controller = null;
    var timer = null;
    if (typeof AbortController !== "undefined") {
      controller = new AbortController();
    }
    var opts = options || {};
    if (controller) {
      opts.signal = controller.signal;
    }
    opts.headers = createRequestHeaders(opts.headers, url);
    timer = window.setTimeout(function () {
      if (controller) {
        controller.abort();
      }
    }, 8000);
    return fetch(url, opts)
      .then(function (response) {
        return response
          .json()
          .catch(function () {
            return null;
          })
          .then(function (payload) {
            if (!response.ok) {
              var message = payload && payload.error ? payload.error : "SERVER_ERROR";
              if (
                message === "BLOCK_DISABLED" &&
                url !== "/api/client-blocks" &&
                deps.handleBlockedClientRequest
              ) {
                deps.handleBlockedClientRequest();
              }
              throw new Error(message);
            }
            return payload;
          });
      })
      .finally(function () {
        if (timer) {
          clearTimeout(timer);
        }
      });
  }

  function formatDate(value) {
    if (!value) {
      return "-";
    }
    var date = new Date(value);
    if (isNaN(date.getTime())) {
      return "-";
    }
    return pad2(date.getDate()) + "." + pad2(date.getMonth() + 1) + "." + date.getFullYear();
  }

  function formatDateTime(value) {
    if (!value) {
      return "-";
    }
    var date = new Date(value);
    if (isNaN(date.getTime())) {
      return "-";
    }
    return (
      pad2(date.getDate()) +
      "." +
      pad2(date.getMonth() + 1) +
      "." +
      date.getFullYear() +
      " " +
      pad2(date.getHours()) +
      ":" +
      pad2(date.getMinutes())
    );
  }

  function pad2(value) {
    var num = Number(value);
    if (isNaN(num)) {
      return "00";
    }
    return num < 10 ? "0" + num : String(num);
  }

  function formatQtyDisplay(qty, itemId) {
    var item = deps.getItemById ? deps.getItemById(Number(itemId)) || {} : {};
    var unit = item.base_uom || "";
    return qty + (unit ? " " + unit : "");
  }

  function formatReportQty(value) {
    var number = Number(value);
    if (!isFinite(number)) {
      number = 0;
    }
    return number.toLocaleString("ru-RU", {
      minimumFractionDigits: 0,
      maximumFractionDigits: 3,
    });
  }

  function formatQuantity(value) {
    var number = Number(value) || 0;
    if (Math.abs(number - Math.round(number)) < 0.000001) {
      return String(Math.round(number));
    }
    return number.toFixed(3).replace(/0+$/, "").replace(/\.$/, "");
  }

  function escapeHtml(value) {
    return String(value || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function normalizeSearchQuery(value) {
    return String(value || "").trim().toLowerCase();
  }

  function matchesItemSearch(entry, normalizedQuery, includeLocationCode) {
    if (!normalizedQuery) {
      return true;
    }
    if (entry.itemName && entry.itemName.toLowerCase().indexOf(normalizedQuery) !== -1) {
      return true;
    }
    if (entry.brand && entry.brand.toLowerCase().indexOf(normalizedQuery) !== -1) {
      return true;
    }
    if (entry.volume && entry.volume.toLowerCase().indexOf(normalizedQuery) !== -1) {
      return true;
    }
    if (entry.barcode && entry.barcode.toLowerCase().indexOf(normalizedQuery) !== -1) {
      return true;
    }
    if (entry.gtin && entry.gtin.toLowerCase().indexOf(normalizedQuery) !== -1) {
      return true;
    }
    if (
      includeLocationCode &&
      entry.locationCode &&
      entry.locationCode.toLowerCase().indexOf(normalizedQuery) !== -1
    ) {
      return true;
    }
    return false;
  }

  function getSortDirectionMark(view, key) {
    var state = tableSortState[view];
    if (!state || state.key !== key) {
      return " ⇅";
    }
    return state.direction === "desc" ? " ▼" : " ▲";
  }

  function renderSortableHeader(view, key, label) {
    var state = tableSortState[view];
    var isActive = !!(state && state.key === key);
    return (
      '<th><button class="pc-table-sort' +
      (isActive ? " is-active" : "") +
      '" type="button" data-sort-view="' +
      escapeHtml(view) +
      '" data-sort-key="' +
      escapeHtml(key) +
      '" aria-label="Сортировать по столбцу ' +
      escapeHtml(label) +
      '">' +
      escapeHtml(label) +
      '<span class="pc-table-sort-mark">' +
      escapeHtml(getSortDirectionMark(view, key)) +
      "</span></button></th>"
    );
  }

  function toggleTableSort(view, key) {
    var state = tableSortState[view];
    if (!state) {
      return;
    }
    if (state.key === key) {
      state.direction = state.direction === "asc" ? "desc" : "asc";
      return;
    }
    state.key = key;
    state.direction = "asc";
  }

  function compareSortValues(left, right, valueType) {
    if (valueType === "number") {
      var leftNum = Number(left);
      var rightNum = Number(right);
      var leftValid = isFinite(leftNum);
      var rightValid = isFinite(rightNum);
      if (!leftValid && !rightValid) return 0;
      if (!leftValid) return 1;
      if (!rightValid) return -1;
      if (leftNum === rightNum) return 0;
      return leftNum < rightNum ? -1 : 1;
    }
    if (valueType === "date") {
      var leftDate = left ? Date.parse(String(left)) : NaN;
      var rightDate = right ? Date.parse(String(right)) : NaN;
      var leftDateValid = isFinite(leftDate);
      var rightDateValid = isFinite(rightDate);
      if (!leftDateValid && !rightDateValid) return 0;
      if (!leftDateValid) return 1;
      if (!rightDateValid) return -1;
      if (leftDate === rightDate) return 0;
      return leftDate < rightDate ? -1 : 1;
    }
    return String(left || "").localeCompare(String(right || ""), "ru", {
      sensitivity: "base",
      numeric: true,
    });
  }

  function sortRows(rows, view, columns) {
    var source = Array.isArray(rows) ? rows.slice() : [];
    var state = tableSortState[view];
    if (!state || !state.key || !columns || !columns[state.key]) {
      return source;
    }
    var column = columns[state.key];
    var direction = state.direction === "desc" ? -1 : 1;
    return source
      .map(function (row, index) {
        return { row: row, index: index };
      })
      .sort(function (left, right) {
        var compared = compareSortValues(
          column.getValue(left.row),
          column.getValue(right.row),
          column.type
        );
        return compared !== 0 ? compared * direction : left.index - right.index;
      })
      .map(function (entry) {
        return entry.row;
      });
  }

  function bindTableSorting(tableWrap, view, rerender) {
    if (!tableWrap || typeof rerender !== "function") {
      return;
    }
    var buttons = tableWrap.querySelectorAll('.pc-table-sort[data-sort-view="' + view + '"]');
    buttons.forEach(function (button) {
      button.addEventListener("click", function () {
        var key = String(button.getAttribute("data-sort-key") || "");
        if (key) {
          toggleTableSort(view, key);
          rerender();
        }
      });
    });
  }

  window.FlowStockPcCore = {
    init: init,
    fetchJson: fetchJson,
    createRequestHeaders: createRequestHeaders,
    escapeHtml: escapeHtml,
    formatDate: formatDate,
    formatDateTime: formatDateTime,
    pad2: pad2,
    formatQtyDisplay: formatQtyDisplay,
    formatReportQty: formatReportQty,
    formatQuantity: formatQuantity,
    normalizeSearchQuery: normalizeSearchQuery,
    matchesItemSearch: matchesItemSearch,
    renderSortableHeader: renderSortableHeader,
    sortRows: sortRows,
    bindTableSorting: bindTableSorting,
  };
})();
