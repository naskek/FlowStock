(function () {
  "use strict";

  var LEGACY_CSS_CLASS = "tsd-legacy-css";

  function hasClass(element, className) {
    return (" " + String(element.className || "") + " ").indexOf(" " + className + " ") >= 0;
  }

  function addClassOnce(element, className) {
    if (!element || hasClass(element, className)) {
      return;
    }
    var current = String(element.className || "").replace(/^\s+|\s+$/g, "");
    element.className = current ? current + " " + className : className;
  }

  function shouldUseLegacyCss() {
    if (typeof CSS === "undefined" || !CSS || typeof CSS.supports !== "function") {
      return true;
    }

    if (!CSS.supports("display", "grid")) {
      return true;
    }
    if (!CSS.supports("font-size", "clamp(12px, 2vw, 16px)")) {
      return true;
    }
    if (!CSS.supports("width", "min(100%, 640px)")) {
      return true;
    }

    return false;
  }

  function initLegacyCssMode() {
    var root = typeof document !== "undefined" ? document.documentElement : null;
    if (!root) {
      return;
    }

    try {
      if (shouldUseLegacyCss()) {
        addClassOnce(root, LEGACY_CSS_CLASS);
      }
    } catch (error) {
      addClassOnce(root, LEGACY_CSS_CLASS);
    }
  }

  function installPromiseFinallyShimIfNeeded() {
    try {
      if (typeof Promise === "undefined" || typeof Promise.prototype.finally === "function") {
        return;
      }

      Promise.prototype.finally = function (onFinally) {
        var callback = typeof onFinally === "function" ? onFinally : function () {};
        var PromiseCtor = this && this.constructor ? this.constructor : Promise;

        return this.then(
          function (value) {
            return PromiseCtor.resolve(callback()).then(function () {
              return value;
            });
          },
          function (reason) {
            return PromiseCtor.resolve(callback()).then(function () {
              throw reason;
            });
          }
        );
      };
    } catch (error) {
      // CSS capability detection must stay independent from Promise support.
    }
  }

  initLegacyCssMode();
  installPromiseFinallyShimIfNeeded();
})();
