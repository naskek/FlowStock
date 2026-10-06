(function () {
  "use strict";

  var stock = window.FlowStockPcStock;
  if (!stock || typeof stock.wireStock !== "function") {
    return;
  }

  function isCollapseTrigger(event, row) {
    if (!event || !row || row.getAttribute("aria-expanded") !== "true") {
      return false;
    }
    if (event.type === "click") {
      return true;
    }
    return event.type === "keydown" && (event.key === "Enter" || event.key === " ");
  }

  function findToggleRow(target, container) {
    if (!target || typeof target.closest !== "function") {
      return null;
    }
    var row = target.closest("[data-stock-toggle-item]");
    if (!row) {
      return null;
    }
    if (container && typeof container.contains === "function" && !container.contains(row)) {
      return null;
    }
    return row;
  }

  function restoreRowViewportPosition(container, itemId, beforeTop) {
    if (!container || !itemId || !isFinite(beforeTop) || typeof container.querySelector !== "function") {
      return 0;
    }

    var replacement = container.querySelector('[data-stock-toggle-item="' + itemId + '"]');
    if (!replacement || replacement.getAttribute("aria-expanded") !== "false" ||
        typeof replacement.getBoundingClientRect !== "function") {
      return 0;
    }

    var afterTop = Number(replacement.getBoundingClientRect().top);
    if (!isFinite(afterTop)) {
      return 0;
    }

    var delta = afterTop - beforeTop;
    if (Math.abs(delta) < 0.5 || typeof window.scrollBy !== "function") {
      return 0;
    }

    window.scrollBy(0, delta);
    return delta;
  }

  function scheduleRestore(container, itemId, beforeTop) {
    var callback = function () {
      restoreRowViewportPosition(container, itemId, beforeTop);
    };
    if (typeof window.requestAnimationFrame === "function") {
      window.requestAnimationFrame(callback);
      return;
    }
    window.setTimeout(callback, 0);
  }

  function bind(container) {
    if (!container || container.__flowStockCollapseAnchorBound) {
      return;
    }

    function captureCollapse(event) {
      var row = findToggleRow(event && event.target, container);
      if (!isCollapseTrigger(event, row) || typeof row.getBoundingClientRect !== "function") {
        return;
      }

      var itemId = Number(row.getAttribute("data-stock-toggle-item")) || 0;
      var beforeTop = Number(row.getBoundingClientRect().top);
      if (!itemId || !isFinite(beforeTop)) {
        return;
      }

      scheduleRestore(container, itemId, beforeTop);
    }

    container.addEventListener("click", captureCollapse, true);
    container.addEventListener("keydown", captureCollapse, true);
    container.__flowStockCollapseAnchorBound = true;
  }

  var originalWireStock = stock.wireStock;
  stock.wireStock = function () {
    var result = originalWireStock.apply(this, arguments);
    bind(document.getElementById("stockTableWrap"));
    return result;
  };

  window.FlowStockPcStockScrollAnchorTestHooks = {
    isCollapseTrigger: isCollapseTrigger,
    restoreRowViewportPosition: restoreRowViewportPosition,
    bind: bind,
  };
})();
