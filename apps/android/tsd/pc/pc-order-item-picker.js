(function () {
  "use strict";

  var filterKeys = ["brand", "volume", "baseUom"];
  var filterTitles = { brand: "Бренд", volume: "Объём", baseUom: "Базовая единица" };

  function text(value) {
    return String(value == null ? "" : value).trim();
  }

  function normalize(value) {
    return text(value).toLocaleLowerCase("ru");
  }

  function field(item, key) {
    if (key === "baseUom") return text(item.base_uom_code || item.base_uom);
    return text(item[key]);
  }

  function createModel() {
    var items = [];
    var query = "";
    var selected = { brand: null, volume: null, baseUom: null };

    function options(key) {
      var found = new Set();
      items.forEach(function (item) { found.add(field(item, key)); });
      return Array.from(found).sort(function (a, b) {
        return a.localeCompare(b, "ru", { numeric: true, sensitivity: "base" });
      });
    }

    function isSelected(key, value) {
      return selected[key] === null || selected[key].has(value);
    }

    function filtered() {
      var needle = normalize(query);
      return items.filter(function (item) {
        if (!filterKeys.every(function (key) { return isSelected(key, field(item, key)); })) {
          return false;
        }
        if (!needle) return true;
        return [item.name, item.brand, item.volume, item.base_uom_code, item.base_uom,
          item.barcode, item.gtin].some(function (value) {
          return normalize(value).indexOf(needle) !== -1;
        });
      });
    }

    return {
      setItems: function (rows) {
        items = (Array.isArray(rows) ? rows : []).filter(function (item) {
          return item && item.is_active !== false && Number(item.id) > 0;
        }).slice().sort(function (a, b) {
          var cmp = text(a.name).localeCompare(text(b.name), "ru", { numeric: true, sensitivity: "base" });
          return cmp || Number(a.id) - Number(b.id);
        });
      },
      itemById: function (id) {
        return items.find(function (item) { return Number(item.id) === Number(id); }) || null;
      },
      getQuery: function () { return query; },
      setQuery: function (value) { query = String(value || ""); },
      options: options,
      isSelected: isSelected,
      setAll: function (key) {
        if (filterKeys.indexOf(key) < 0) return;
        selected[key] = null;
      },
      setNone: function (key) {
        if (filterKeys.indexOf(key) < 0) return;
        selected[key] = new Set();
      },
      setSelected: function (key, value, checked) {
        if (filterKeys.indexOf(key) < 0 || options(key).indexOf(value) < 0) return;
        if (selected[key] === null) selected[key] = new Set(options(key));
        if (checked) selected[key].add(value);
        else selected[key].delete(value);
      },
      reset: function () {
        query = "";
        filterKeys.forEach(function (key) { selected[key] = null; });
      },
      filtered: filtered
    };
  }

  function create(config) {
    var deps = config || {};
    var model = createModel();
    var overlay = null;
    var card = null;
    var disposeDismiss = function () {};
    var lineIndex = -1;
    var originalParentHidden = null;
    var lastTrigger = null;
    var escape = deps.escapeHtml || function (value) { return text(value); };
    model.setItems(deps.items);

    function renderFilterGroup(key) {
      var values = model.options(key);
      return '<fieldset class="pc-order-picker-filter">' +
        '<legend>' + escape(filterTitles[key]) + '</legend>' +
        '<div class="pc-order-picker-filter-actions">' +
        '<button class="btn btn-ghost" type="button" data-picker-action="all" data-picker-group="' + key + '">Все</button>' +
        '<button class="btn btn-ghost" type="button" data-picker-action="none" data-picker-group="' + key + '">Нет</button>' +
        '</div><div class="pc-order-picker-options">' +
        values.map(function (value) {
          return '<label class="pc-inline-check"><input type="checkbox" data-picker-filter="' + key +
            '" data-picker-value="' + escape(value) + '"' +
            (model.isSelected(key, value) ? ' checked' : '') + ' />' +
            escape(value || "Не указано") + '</label>';
        }).join("") +
        (values.length ? "" : '<span class="pc-order-picker-empty-option">Нет значений</span>') +
        '</div></fieldset>';
    }

    function renderFilters() {
      if (!overlay) return;
      var area = overlay.querySelector("[data-picker-filters]");
      if (area) area.innerHTML = filterKeys.map(renderFilterGroup).join("");
    }

    function renderResults() {
      if (!overlay) return;
      var rows = model.filtered();
      var count = overlay.querySelector("[data-picker-count]");
      var wrap = overlay.querySelector("[data-picker-results]");
      if (count) count.textContent = "Найдено товаров: " + rows.length;
      if (!wrap) return;
      if (!rows.length) {
        wrap.innerHTML = '<div class="pc-order-picker-empty">Товары не найдены. Измените поиск или фильтры.</div>';
        return;
      }
      wrap.innerHTML = '<table class="pc-table pc-order-picker-table"><thead><tr>' +
        '<th>Товар</th><th>Бренд</th><th>Объём</th><th>Ед.</th><th></th>' +
        '</tr></thead><tbody>' +
        rows.map(function (item) {
          var meta = [];
          if (item.barcode) meta.push("SKU: " + escape(item.barcode));
          if (item.gtin) meta.push("GTIN: " + escape(item.gtin));
          return '<tr><td><strong>' + escape(item.name || "Без названия") + '</strong>' +
            (meta.length ? '<div class="pc-order-picker-meta">' + meta.join(" · ") + '</div>' : "") +
            '</td><td>' + escape(field(item, "brand") || "—") +
            '</td><td>' + escape(field(item, "volume") || "—") +
            '</td><td>' + escape(field(item, "baseUom") || "—") +
            '</td><td><button type="button" class="btn btn-outline pc-order-picker-select"' +
            ' data-picker-action="select" data-picker-item="' + escape(String(item.id)) +
            '">Выбрать</button></td></tr>';
        }).join("") + '</tbody></table>';
    }

    function close(restoreFocus) {
      if (!overlay) return;
      disposeDismiss();
      if (overlay.parentNode) overlay.parentNode.removeChild(overlay);
      overlay = null;
      card = null;
      if (deps.parentModal) {
        if (originalParentHidden === null) deps.parentModal.removeAttribute("aria-hidden");
        else deps.parentModal.setAttribute("aria-hidden", originalParentHidden);
      }
      originalParentHidden = null;
      if (restoreFocus && lastTrigger && lastTrigger.isConnected !== false) lastTrigger.focus();
      lastTrigger = null;
      lineIndex = -1;
    }

    function onClick(event) {
      var action = event.target.closest("[data-picker-action]");
      if (!action || !overlay || !overlay.contains(action)) return;
      var kind = action.getAttribute("data-picker-action");
      var key = action.getAttribute("data-picker-group");
      if (kind === "return") {
        close(true);
      } else if (kind === "reset") {
        model.reset();
        var search = overlay.querySelector("[data-picker-search]");
        if (search) search.value = "";
        renderFilters();
        renderResults();
        if (search) search.focus();
      } else if (kind === "all" || kind === "none") {
        if (kind === "all") model.setAll(key); else model.setNone(key);
        renderFilters();
        renderResults();
        var again = overlay.querySelector('[data-picker-action="' + kind + '"][data-picker-group="' + key + '"]');
        if (again) again.focus();
      } else if (kind === "select") {
        var id = Number(action.getAttribute("data-picker-item")) || 0;
        if (!model.itemById(id)) return;
        var targetLine = lineIndex;
        close(false);
        if (typeof deps.onSelect === "function") deps.onSelect(targetLine, id);
      }
    }

    function onInput(event) {
      if (event.target.matches("[data-picker-search]")) {
        model.setQuery(event.target.value);
        renderResults();
      }
    }

    function onChange(event) {
      var input = event.target;
      if (!input.matches("[data-picker-filter]")) return;
      model.setSelected(input.getAttribute("data-picker-filter"),
        input.getAttribute("data-picker-value"), input.checked);
      renderResults();
    }

    function onKeyDown(event) {
      if (event.key !== "Tab" || !card) return;
      var focusables = Array.from(card.querySelectorAll('button:not([disabled]), input:not([disabled])'));
      if (!focusables.length) return;
      var first = focusables[0];
      var last = focusables[focusables.length - 1];
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault(); last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault(); first.focus();
      }
    }

    return {
      setItems: function (items) {
        model.setItems(items);
        renderFilters();
        renderResults();
      },
      open: function (index, trigger) {
        if (overlay || index < 0) return;
        lineIndex = index;
        lastTrigger = trigger || null;
        overlay = document.createElement("div");
        overlay.className = "pc-modal pc-order-picker-modal";
        overlay.innerHTML = '<div class="pc-modal-card pc-order-picker-card" role="dialog" aria-modal="true" aria-labelledby="orderPickerTitle">' +
          '<div class="pc-modal-header"><div>' +
          '<div class="pc-modal-title" id="orderPickerTitle">Выбор товара из каталога</div>' +
          '<div class="pc-order-picker-line">Строка заказа № ' + (index + 1) + '</div>' +
          '</div><button type="button" class="btn btn-outline" data-picker-action="return">Вернуться к заказу</button></div>' +
          '<div class="pc-order-picker-tools"><div class="form-field"><label class="form-label" for="orderPickerSearch">Поиск по каталогу</label>' +
          '<input class="form-input" id="orderPickerSearch" data-picker-search type="search" autocomplete="off"' +
          ' placeholder="Название, бренд, объём, SKU, GTIN, штрихкод" value="' + escape(model.getQuery()) + '" /></div>' +
          '<button class="btn btn-outline" type="button" data-picker-action="reset">Сбросить фильтры</button></div>' +
          '<div class="pc-order-picker-filters" data-picker-filters></div>' +
          '<div class="pc-order-picker-count" aria-live="polite" data-picker-count></div>' +
          '<div class="pc-order-picker-results" data-picker-results></div></div>';
        document.body.appendChild(overlay);
        card = overlay.querySelector(".pc-order-picker-card");
        if (deps.parentModal) {
          originalParentHidden = deps.parentModal.getAttribute("aria-hidden");
          deps.parentModal.setAttribute("aria-hidden", "true");
        }
        disposeDismiss = deps.bindModalDismiss(overlay, function () { close(true); });
        overlay.addEventListener("click", onClick);
        overlay.addEventListener("input", onInput);
        overlay.addEventListener("change", onChange);
        overlay.addEventListener("keydown", onKeyDown);
        renderFilters();
        renderResults();
        var search = overlay.querySelector("[data-picker-search]");
        if (search) search.focus();
      },
      close: function () { close(true); },
      dispose: function () { close(false); },
      isOpen: function () { return !!overlay; },
      model: model
    };
  }

  window.FlowStockPcOrderItemPicker = {
    create: create,
    testHooks: { createModel: createModel }
  };
})();
