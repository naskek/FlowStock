const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const bridgeJs = fs.readFileSync(path.join(__dirname, "native-bridge.js"), "utf8");

function plain(value) {
  return JSON.parse(JSON.stringify(value));
}

function createButton(options) {
  const state = Object.assign({ clicked: false, disabled: false, hidden: false, rects: [1] }, options || {});
  return {
    disabled: state.disabled,
    hidden: state.hidden,
    getClientRects() {
      return state.rects;
    },
    click() {
      state.clicked = true;
    },
    get clicked() {
      return state.clicked;
    },
  };
}

function createSecurityError() {
  const error = new Error("Blocked");
  error.name = "SecurityError";
  return error;
}

function createContext(userAgent, button, locationHref, cookieText, options) {
  const timers = [];
  const href = locationHref || "https://example.test/tsd/";
  const opts = options || {};
  const location = {
    href,
    search: href.indexOf("?") === -1 ? "" : "?" + href.split("?")[1].split("#")[0],
    pathname: "/" + href.split("://")[1].split("/").slice(1).join("/").split("?")[0].split("#")[0],
    hash: href.indexOf("#") === -1 ? "" : "#" + href.split("#")[1],
  };
  const documentObject = {
    cookie: cookieText || "",
    getElementById(id) {
      return id === "backBtn" ? button || null : null;
    },
  };
  if (opts.throwSearch) {
    Object.defineProperty(location, "search", {
      get() {
        throw createSecurityError();
      },
    });
  }
  if (opts.throwCookie) {
    Object.defineProperty(documentObject, "cookie", {
      get() {
        throw createSecurityError();
      },
    });
  }
  const context = {
    window: null,
    navigator: {
      userAgent,
    },
    location,
    document: documentObject,
    setTimeout(fn) {
      timers.push(fn);
      return timers.length;
    },
    clearTimeout() {},
  };
  context.window = context;
  vm.createContext(context);
  vm.runInContext(bridgeJs, context, { filename: "native-bridge.js" });
  context.__runTimers = function () {
    timers.splice(0).forEach((fn) => fn());
  };
  return context;
}

function testNoopWithoutNativeUserAgent() {
  const context = createContext("Mozilla/5.0 Chrome");
  assert.strictEqual(context.FlowStockAndroidBridge, undefined);
}

function testBridgeActivatesWithQueryMarkerWithoutNativeUserAgent() {
  const context = createContext("Mozilla/5.0 Chrome", null, "https://example.test/tsd/?flowstockNative=1");
  assert.strictEqual(typeof context.FlowStockAndroidBridge, "object");
}

function testBridgeActivatesWithCookieMarkerWithoutNativeUserAgentOrQuery() {
  const context = createContext(
    "Mozilla/5.0 Chrome",
    null,
    "https://example.test/tsd/",
    "theme=dark; flowstockNative=1; session=abc"
  );
  assert.strictEqual(typeof context.FlowStockAndroidBridge, "object");
}

function testQuerySecurityErrorDoesNotBlockCookieActivation() {
  let context;
  assert.doesNotThrow(() => {
    context = createContext(
      "Mozilla/5.0 Chrome",
      null,
      "https://example.test/tsd/",
      "flowstockNative=1",
      { throwSearch: true }
    );
  });
  assert.strictEqual(typeof context.FlowStockAndroidBridge, "object");
  assert.deepStrictEqual(plain(context.FlowStockNativeActivationDiagnostic), {
    uaMarker: false,
    uaAccess: "ok",
    queryMarker: false,
    queryAccess: "error",
    cookieMarker: true,
    cookieAccess: "ok",
    activated: true,
    activationSource: "cookie",
  });
}

function testCookieSecurityErrorDoesNotBlockUaActivation() {
  let context;
  assert.doesNotThrow(() => {
    context = createContext(
      "Mozilla/5.0 FlowStockTsdNative/1",
      null,
      "https://example.test/tsd/",
      "",
      { throwCookie: true }
    );
  });
  assert.strictEqual(typeof context.FlowStockAndroidBridge, "object");
  assert.strictEqual(context.FlowStockNativeActivationDiagnostic.uaMarker, true);
  assert.strictEqual(context.FlowStockNativeActivationDiagnostic.cookieAccess, "error");
  assert.strictEqual(context.FlowStockNativeActivationDiagnostic.activationSource, "ua");
}

function testQueryAndCookieSecurityErrorsDoNotEscape() {
  let context;
  assert.doesNotThrow(() => {
    context = createContext(
      "Mozilla/5.0 Chrome",
      null,
      "https://example.test/tsd/",
      "",
      { throwSearch: true, throwCookie: true }
    );
  });
  assert.strictEqual(context.FlowStockAndroidBridge, undefined);
  assert.deepStrictEqual(plain(context.FlowStockNativeActivationDiagnostic), {
    uaMarker: false,
    uaAccess: "ok",
    queryMarker: false,
    queryAccess: "error",
    cookieMarker: false,
    cookieAccess: "error",
    activated: false,
    activationSource: "none",
  });
}

function testBridgeDoesNotActivateForInvalidQueryMarkerValues() {
  assert.strictEqual(
    createContext("Mozilla/5.0 Chrome", null, "https://example.test/tsd/?flowstockNative=0").FlowStockAndroidBridge,
    undefined
  );
  assert.strictEqual(
    createContext("Mozilla/5.0 Chrome", null, "https://example.test/tsd/?flowstockNative=10").FlowStockAndroidBridge,
    undefined
  );
  assert.strictEqual(
    createContext("Mozilla/5.0 Chrome", null, "https://example.test/tsd/?other=flowstockNative%3D1")
      .FlowStockAndroidBridge,
    undefined
  );
}

function testBridgeDoesNotActivateForInvalidCookieMarkerValues() {
  assert.strictEqual(
    createContext("Mozilla/5.0 Chrome", null, "https://example.test/tsd/", "flowstockNative=0")
      .FlowStockAndroidBridge,
    undefined
  );
  assert.strictEqual(
    createContext("Mozilla/5.0 Chrome", null, "https://example.test/tsd/", "flowstockNative=10")
      .FlowStockAndroidBridge,
    undefined
  );
  assert.strictEqual(
    createContext("Mozilla/5.0 Chrome", null, "https://example.test/tsd/", "xflowstockNative=1")
      .FlowStockAndroidBridge,
    undefined
  );
  assert.strictEqual(
    createContext("Mozilla/5.0 Chrome", null, "https://example.test/tsd/", "other=flowstockNative=1")
      .FlowStockAndroidBridge,
    undefined
  );
}

function testBridgeDoesNotActivateFromPathnameOrHashText() {
  assert.strictEqual(
    createContext("Mozilla/5.0 Chrome", null, "https://example.test/flowstockNative=1/tsd/").FlowStockAndroidBridge,
    undefined
  );
  assert.strictEqual(
    createContext("Mozilla/5.0 Chrome", null, "https://example.test/tsd/#flowstockNative=1").FlowStockAndroidBridge,
    undefined
  );
}

function testBridgeSubscribeDispatchAndStop() {
  const context = createContext("Mozilla/5.0 FlowStockTsdNative/1");
  const bridge = context.FlowStockAndroidBridge;
  const scans = [];

  assert.strictEqual(bridge.__getActiveScanSubscriptionCount(), 0);
  assert.strictEqual(bridge.__hasActiveScanSubscribers(), false);

  const unsubscribe = bridge.subscribeScans((payload) => scans.push(payload));

  assert.strictEqual(typeof unsubscribe, "function");
  assert.strictEqual(bridge.__getActiveScanSubscriptionCount(), 1);
  assert.strictEqual(bridge.__hasActiveScanSubscribers(), true);
  assert.strictEqual(bridge._test.getSubscriptionCount(), 1);
  assert.strictEqual(
    bridge.__dispatchFromNative(JSON.stringify({ value: 'A"B\\C\u001d', symbology: "GS1" })),
    true
  );
  assert.strictEqual(JSON.stringify(scans), JSON.stringify([{ value: 'A"B\\C\u001d', symbology: "GS1" }]));

  assert.strictEqual(unsubscribe(), true);
  assert.strictEqual(unsubscribe(), false);
  assert.strictEqual(bridge.__getActiveScanSubscriptionCount(), 0);
  assert.strictEqual(bridge.__hasActiveScanSubscribers(), false);
  assert.strictEqual(bridge._test.getSubscriptionCount(), 0);
  assert.strictEqual(bridge.__dispatchFromNative(JSON.stringify({ value: "AFTER" })), false);

  bridge.subscribeScans((payload) => scans.push(payload));
  assert.strictEqual(bridge.__getActiveScanSubscriptionCount(), 1);
  assert.strictEqual(bridge.stopScans(), true);
  assert.strictEqual(bridge.__getActiveScanSubscriptionCount(), 0);
  assert.strictEqual(bridge.__hasActiveScanSubscribers(), false);
  assert.strictEqual(bridge._test.getSubscriptionCount(), 0);
}

function testUnsubscribeScansVariants() {
  const context = createContext("FlowStockTsdNative/1");
  const bridge = context.FlowStockAndroidBridge;
  const first = bridge.subscribeScans(() => {});
  const second = bridge.subscribeScans(() => {});

  assert.strictEqual(bridge.unsubscribeScans(first), true);
  assert.strictEqual(bridge.unsubscribeScans(second.__flowstockSubscriptionId), true);
  assert.strictEqual(bridge.unsubscribeScans("missing"), false);
  assert.strictEqual(bridge._test.getSubscriptionCount(), 0);
}

function testInvalidJsonDoesNotDispatch() {
  const context = createContext("FlowStockTsdNative/1");
  const bridge = context.FlowStockAndroidBridge;
  let count = 0;
  bridge.subscribeScans(() => {
    count += 1;
  });
  assert.strictEqual(bridge.__dispatchFromNative("{bad json"), false);
  assert.strictEqual(count, 0);
}

function testCallbackErrorIsAsync() {
  const context = createContext("FlowStockTsdNative/1");
  const bridge = context.FlowStockAndroidBridge;
  bridge.subscribeScans(() => {
    throw new Error("boom");
  });
  assert.strictEqual(bridge.__dispatchFromNative(JSON.stringify({ value: "A" })), true);
  assert.throws(() => context.__runTimers(), /boom/);
}

function testBackButtonHandling() {
  const visible = createButton();
  const context = createContext("FlowStockTsdNative/1", visible);
  assert.strictEqual(context.FlowStockAndroidBridge.handleBack(), true);
  assert.strictEqual(visible.clicked, true);

  const hidden = createButton({ rects: [] });
  const hiddenContext = createContext("FlowStockTsdNative/1", hidden);
  assert.strictEqual(hiddenContext.FlowStockAndroidBridge.handleBack(), false);
  assert.strictEqual(hidden.clicked, false);

  const missingContext = createContext("FlowStockTsdNative/1", null);
  assert.strictEqual(missingContext.FlowStockAndroidBridge.handleBack(), false);
}

function testSubscribeRequiresCallback() {
  const context = createContext("FlowStockTsdNative/1");
  assert.throws(
    () => context.FlowStockAndroidBridge.subscribeScans(null),
    /FLOWSTOCK_NATIVE_SCAN_CALLBACK_REQUIRED/
  );
}

testNoopWithoutNativeUserAgent();
testBridgeActivatesWithQueryMarkerWithoutNativeUserAgent();
testBridgeActivatesWithCookieMarkerWithoutNativeUserAgentOrQuery();
testQuerySecurityErrorDoesNotBlockCookieActivation();
testCookieSecurityErrorDoesNotBlockUaActivation();
testQueryAndCookieSecurityErrorsDoNotEscape();
testBridgeDoesNotActivateForInvalidQueryMarkerValues();
testBridgeDoesNotActivateForInvalidCookieMarkerValues();
testBridgeDoesNotActivateFromPathnameOrHashText();
testBridgeSubscribeDispatchAndStop();
testUnsubscribeScansVariants();
testInvalidJsonDoesNotDispatch();
testCallbackErrorIsAsync();
testBackButtonHandling();
testSubscribeRequiresCallback();

console.log("native-bridge.test.js passed");
