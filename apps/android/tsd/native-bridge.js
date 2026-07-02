(function (root) {
  "use strict";

  var NATIVE_UA_TOKEN = "FlowStockTsdNative/1";

  function parseExactQueryMarker(search) {
    var query = typeof search === "string" ? search : "";
    var parts;
    var i;
    var part;
    var equalsAt;
    var name;
    var value;
    if (!query || query.charAt(0) !== "?") {
      return false;
    }
    query = query.slice(1);
    if (!query) {
      return false;
    }
    parts = query.split("&");
    for (i = 0; i < parts.length; i += 1) {
      part = parts[i];
      equalsAt = part.indexOf("=");
      name = equalsAt === -1 ? part : part.slice(0, equalsAt);
      value = equalsAt === -1 ? "" : part.slice(equalsAt + 1);
      if (name === "flowstockNative" && value === "1") {
        return true;
      }
    }
    return false;
  }

  function parseExactCookieMarker(cookieText) {
    var text = typeof cookieText === "string" ? cookieText : "";
    var parts;
    var i;
    var part;
    var equalsAt;
    var name;
    var value;
    if (!text) {
      return false;
    }
    parts = text.split(";");
    for (i = 0; i < parts.length; i += 1) {
      part = parts[i].replace(/^\s+|\s+$/g, "");
      equalsAt = part.indexOf("=");
      name = equalsAt === -1 ? part : part.slice(0, equalsAt);
      value = equalsAt === -1 ? "" : part.slice(equalsAt + 1);
      if (name === "flowstockNative" && value === "1") {
        return true;
      }
    }
    return false;
  }

  function checkUserAgentMarker() {
    var ua = "";
    try {
      ua =
        root.navigator && typeof root.navigator.userAgent === "string"
          ? root.navigator.userAgent
          : "";
      return {
        marker: ua.indexOf(NATIVE_UA_TOKEN) !== -1,
        access: "ok",
      };
    } catch (error) {
      return {
        marker: false,
        access: "error",
      };
    }
  }

  function checkQueryMarker() {
    try {
      return {
        marker: parseExactQueryMarker(root.location ? root.location.search : ""),
        access: "ok",
      };
    } catch (error) {
      return {
        marker: false,
        access: "error",
      };
    }
  }

  function checkCookieMarker() {
    try {
      return {
        marker: parseExactCookieMarker(root.document ? root.document.cookie : ""),
        access: "ok",
      };
    } catch (error) {
      return {
        marker: false,
        access: "error",
      };
    }
  }

  var uaCheck = checkUserAgentMarker();
  var queryCheck = checkQueryMarker();
  var cookieCheck = checkCookieMarker();
  var activated = uaCheck.marker || queryCheck.marker || cookieCheck.marker;
  var activationSource = uaCheck.marker
    ? "ua"
    : queryCheck.marker
      ? "query"
      : cookieCheck.marker
        ? "cookie"
        : "none";

  root.FlowStockNativeActivationDiagnostic = {
    uaMarker: uaCheck.marker,
    uaAccess: uaCheck.access,
    queryMarker: queryCheck.marker,
    queryAccess: queryCheck.access,
    cookieMarker: cookieCheck.marker,
    cookieAccess: cookieCheck.access,
    activated: activated,
    activationSource: activationSource,
  };

  if (!activated) {
    return;
  }

  var callbacks = {};
  var nextId = 1;

  function isFunction(value) {
    return typeof value === "function";
  }

  function unsubscribeById(id) {
    if (callbacks[id]) {
      delete callbacks[id];
      return true;
    }
    return false;
  }

  function makeUnsubscribe(id) {
    var called = false;
    var unsubscribe = function () {
      if (called) {
        return false;
      }
      called = true;
      return unsubscribeById(id);
    };
    unsubscribe.__flowstockSubscriptionId = id;
    return unsubscribe;
  }

  function subscribeScans(callback) {
    if (!isFunction(callback)) {
      throw new Error("FLOWSTOCK_NATIVE_SCAN_CALLBACK_REQUIRED");
    }
    var id = String(nextId);
    nextId += 1;
    callbacks[id] = callback;
    return makeUnsubscribe(id);
  }

  function unsubscribeScans(subscription) {
    if (isFunction(subscription)) {
      return subscription() !== false;
    }
    if (subscription && subscription.__flowstockSubscriptionId) {
      return unsubscribeById(String(subscription.__flowstockSubscriptionId));
    }
    if (subscription != null) {
      return unsubscribeById(String(subscription));
    }
    return false;
  }

  function getActiveScanSubscriptionCount() {
    var count = 0;
    var key;
    for (key in callbacks) {
      if (Object.prototype.hasOwnProperty.call(callbacks, key)) {
        count += 1;
      }
    }
    return count;
  }

  function hasActiveScanSubscribers() {
    return getActiveScanSubscriptionCount() > 0;
  }

  function stopScans() {
    callbacks = {};
    return true;
  }

  function dispatchFromNative(jsonText) {
    var payload;
    var ids;
    var i;
    var callback;
    try {
      payload = JSON.parse(String(jsonText || ""));
    } catch (error) {
      return false;
    }

    ids = [];
    for (i in callbacks) {
      if (Object.prototype.hasOwnProperty.call(callbacks, i)) {
        ids.push(i);
      }
    }

    for (i = 0; i < ids.length; i += 1) {
      callback = callbacks[ids[i]];
      if (!callback) {
        continue;
      }
      try {
        callback(payload);
      } catch (error2) {
        root.setTimeout(
          (function (capturedError) {
            return function () {
              throw capturedError;
            };
          })(error2),
          0
        );
      }
    }

    return ids.length > 0;
  }

  function isElementVisible(element) {
    if (!element || element.disabled) {
      return false;
    }
    if (element.hidden) {
      return false;
    }
    if (typeof element.getClientRects === "function" && element.getClientRects().length === 0) {
      return false;
    }
    return true;
  }

  function handleBack() {
    var doc = root.document;
    var button;
    if (!doc || typeof doc.getElementById !== "function") {
      return false;
    }
    button = doc.getElementById("backBtn");
    if (!isElementVisible(button) || !isFunction(button.click)) {
      return false;
    }
    button.click();
    return true;
  }

  root.FlowStockAndroidBridge = {
    subscribeScans: subscribeScans,
    unsubscribeScans: unsubscribeScans,
    stopScans: stopScans,
    handleBack: handleBack,
    __flowstockNativeDispatchReady: true,
    __dispatchFromNative: dispatchFromNative,
    __getActiveScanSubscriptionCount: getActiveScanSubscriptionCount,
    __hasActiveScanSubscribers: hasActiveScanSubscribers,
    _test: {
      dispatchFromNative: dispatchFromNative,
      getSubscriptionCount: getActiveScanSubscriptionCount,
    },
  };
})(window);
