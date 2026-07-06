const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const rootDir = __dirname;
const compatPath = path.join(rootDir, "compat.js");
const storagePath = path.join(rootDir, "storage.js");

function read(file) {
  return fs.readFileSync(file, "utf8");
}

function runCompat(context) {
  vm.runInNewContext(read(compatPath), context, { filename: "compat.js" });
}

function createDocument(className) {
  return {
    documentElement: {
      className: className || "",
    },
  };
}

function createCssSupport(options) {
  const config = options || {};
  return {
    supports: function supports(property, value) {
      if (config.throws) {
        throw new Error("css supports failed");
      }
      if (property === "display" && value === "grid") {
        return config.grid !== false;
      }
      if (property === "font-size" && String(value).indexOf("clamp(") !== -1) {
        return config.clamp !== false;
      }
      if (property === "width" && String(value).indexOf("min(") !== -1) {
        return config.min !== false;
      }
      return true;
    },
  };
}

function legacyClassCount(document) {
  const className = String(document.documentElement.className || "");
  const matches = className.match(/(?:^|\s)tsd-legacy-css(?:\s|$)/g);
  return matches ? matches.length : 0;
}

function assertLegacyClass(document, expected, message) {
  assert.strictEqual(
    legacyClassCount(document),
    expected ? 1 : 0,
    message
  );
}

async function withPromiseFinally(descriptor, fn) {
  const originalDescriptor = Object.getOwnPropertyDescriptor(Promise.prototype, "finally");
  delete Promise.prototype.finally;
  if (descriptor) {
    Object.defineProperty(Promise.prototype, "finally", descriptor);
  }
  try {
    await fn();
  } finally {
    delete Promise.prototype.finally;
    if (originalDescriptor) {
      Object.defineProperty(Promise.prototype, "finally", originalDescriptor);
    }
  }
}

async function withMissingFinally(fn) {
  await withPromiseFinally(null, fn);
}

async function assertRejectsSame(promise, expected) {
  try {
    await promise;
  } catch (error) {
    assert.strictEqual(error, expected);
    return;
  }
  assert.fail("Expected promise to reject");
}

function createStorageContext(options) {
  const calls = [];
  const timeouts = [];
  const clearedTimeouts = [];
  const loginResponse = options && options.loginResponse;

  function HeadersShim(source) {
    this.values = {};
    if (source) {
      Object.keys(source).forEach((key) => {
        this.values[key.toLowerCase()] = source[key];
      });
    }
  }

  HeadersShim.prototype.has = function has(name) {
    return Object.prototype.hasOwnProperty.call(this.values, String(name).toLowerCase());
  };

  HeadersShim.prototype.set = function set(name, value) {
    this.values[String(name).toLowerCase()] = value;
  };

  function response(ok, body, status) {
    return {
      ok: ok,
      status: status || (ok ? 200 : 500),
      json: function json() {
        return Promise.resolve(body);
      },
      text: function text() {
        return Promise.resolve(JSON.stringify(body));
      },
    };
  }

  function AbortControllerShim() {
    this.signal = {};
    this.abort = function abort() {
      this.aborted = true;
    };
  }

  const context = {
    Promise: Promise,
    JSON: JSON,
    Date: Date,
    Error: Error,
    TypeError: TypeError,
    encodeURIComponent: encodeURIComponent,
    navigator: { onLine: true },
    console: { log: function () {}, warn: function () {}, error: function () {} },
    Headers: HeadersShim,
    AbortController: AbortControllerShim,
    setTimeout: function setTimeoutShim(callback, delay) {
      const id = timeouts.length + 1;
      timeouts.push({ id: id, delay: delay, callback: callback });
      return id;
    },
    clearTimeout: function clearTimeoutShim(id) {
      clearedTimeouts.push(id);
    },
    fetch: function fetchShim(url, init) {
      calls.push({ url: String(url), method: init && init.method ? init.method : "GET" });
      if (String(url).indexOf("/api/ping") !== -1) {
        return Promise.resolve(response(true, { ok: true }, 200));
      }
      if (String(url).indexOf("/api/tsd/login") !== -1) {
        if (loginResponse === "reject") {
          return Promise.reject(new Error("network"));
        }
        if (loginResponse === "http-error") {
          return Promise.resolve(response(false, { error: "INVALID_CREDENTIALS" }, 401));
        }
        return Promise.resolve(response(true, { operator: "test" }, 200));
      }
      return Promise.reject(new Error("unexpected fetch"));
    },
  };

  context.window = context;
  context.self = context;
  context.location = { origin: "https://flowstock.test" };
  context.window.location = context.location;
  context.sessionStorage = { getItem: function () { return null; }, setItem: function () {}, removeItem: function () {} };
  context.localStorage = { getItem: function () { return null; }, setItem: function () {}, removeItem: function () {} };

  vm.createContext(context);
  runCompat(context);
  vm.runInContext(read(storagePath), context, { filename: "storage.js" });

  return { context, calls, timeouts, clearedTimeouts };
}

function scriptSrcs(file) {
  const html = read(file);
  const result = [];
  const re = /<script\b([^>]*)>/gi;
  let match;
  while ((match = re.exec(html))) {
    const srcMatch = /\bsrc=["']([^"']+)["']/i.exec(match[1]);
    result.push(srcMatch ? srcMatch[1] : null);
  }
  return result;
}

function assertBefore(srcs, earlier, later, fileLabel) {
  const earlierIndex = srcs.indexOf(earlier);
  const laterIndex = srcs.indexOf(later);
  assert.notStrictEqual(earlierIndex, -1, `${fileLabel}: missing ${earlier}`);
  assert.notStrictEqual(laterIndex, -1, `${fileLabel}: missing ${later}`);
  assert(earlierIndex < laterIndex, `${fileLabel}: ${earlier} must load before ${later}`);
}

async function testPromiseFinallyShim() {
  await withMissingFinally(async () => {
    runCompat({ Promise: Promise, window: {} });
    assert.strictEqual(typeof Promise.prototype.finally, "function");

    let resolveCalls = 0;
    const resolved = await Promise.resolve("value").finally(function () {
      resolveCalls += 1;
    });
    assert.strictEqual(resolved, "value");
    assert.strictEqual(resolveCalls, 1);

    let rejectCalls = 0;
    const originalReason = new Error("original");
    await assertRejectsSame(Promise.reject(originalReason).finally(function () {
      rejectCalls += 1;
    }), originalReason);
    assert.strictEqual(rejectCalls, 1);

    const thrown = new Error("thrown");
    await assertRejectsSame(Promise.resolve("value").finally(function () {
      throw thrown;
    }), thrown);

    const rejectedFromCallback = new Error("callback rejected");
    await assertRejectsSame(Promise.reject(originalReason).finally(function () {
      return Promise.reject(rejectedFromCallback);
    }), rejectedFromCallback);

    const returned = Promise.resolve("value").finally(function () {});
    assert.strictEqual(typeof returned.then, "function");
    await returned;
  });
}

async function testNativeFinallyIsNotOverwritten() {
  const sentinel = function sentinelFinally() {};
  await withPromiseFinally({
    configurable: true,
    writable: true,
    value: sentinel,
  }, async () => {
    runCompat({ Promise: Promise, window: {} });
    assert.strictEqual(Promise.prototype.finally, sentinel);
  });
}

async function testCssCapabilityDetection() {
  const noCss = createDocument();
  runCompat({ Promise: Promise, document: noCss });
  assertLegacyClass(noCss, true, "missing CSS should enable legacy CSS");

  const noSupports = createDocument();
  runCompat({ Promise: Promise, CSS: {}, document: noSupports });
  assertLegacyClass(noSupports, true, "missing CSS.supports should enable legacy CSS");

  const noGrid = createDocument();
  runCompat({ Promise: Promise, CSS: createCssSupport({ grid: false }), document: noGrid });
  assertLegacyClass(noGrid, true, "missing grid support should enable legacy CSS");

  const noClamp = createDocument();
  runCompat({ Promise: Promise, CSS: createCssSupport({ clamp: false }), document: noClamp });
  assertLegacyClass(noClamp, true, "missing clamp support should enable legacy CSS");

  const noMin = createDocument();
  runCompat({ Promise: Promise, CSS: createCssSupport({ min: false }), document: noMin });
  assertLegacyClass(noMin, true, "missing min() support should enable legacy CSS");

  const modern = createDocument("existing-theme");
  runCompat({ Promise: Promise, CSS: createCssSupport(), document: modern });
  assertLegacyClass(modern, false, "full modern support should not enable legacy CSS");
  assert.strictEqual(modern.documentElement.className, "existing-theme");

  const throwsDoc = createDocument("existing");
  runCompat({ Promise: Promise, CSS: createCssSupport({ throws: true }), document: throwsDoc });
  assertLegacyClass(throwsDoc, true, "CSS.supports exceptions should enable legacy CSS");
  assert(throwsDoc.documentElement.className.indexOf("existing") !== -1);

  runCompat({ Promise: Promise, CSS: createCssSupport({ throws: true }), document: throwsDoc });
  assertLegacyClass(throwsDoc, true, "repeat compat load should not duplicate legacy class");

  const nativeFinallyNoGrid = createDocument();
  runCompat({ Promise: Promise, CSS: createCssSupport({ grid: false }), document: nativeFinallyNoGrid });
  assertLegacyClass(
    nativeFinallyNoGrid,
    true,
    "native Promise.finally must not skip CSS detection"
  );

  const noPromiseNoGrid = createDocument();
  runCompat({ Promise: undefined, CSS: createCssSupport({ grid: false }), document: noPromiseNoGrid });
  assertLegacyClass(
    noPromiseNoGrid,
    true,
    "missing Promise must not skip CSS detection"
  );

  await withMissingFinally(async () => {
    const cssThrows = createDocument();
    runCompat({ Promise: Promise, CSS: createCssSupport({ throws: true }), document: cssThrows });
    assertLegacyClass(cssThrows, true, "CSS detection failure should still fail closed");
    assert.strictEqual(typeof Promise.prototype.finally, "function");
  });

  const modernWithFinally = createDocument();
  runCompat({ Promise: Promise, CSS: createCssSupport(), document: modernWithFinally });
  assertLegacyClass(
    modernWithFinally,
    false,
    "modern CSS with native Promise.finally should stay modern"
  );
}

async function testLoginFlowWithoutNativeFinally() {
  await withMissingFinally(async () => {
    const success = createStorageContext();
    const loginResult = await success.context.TsdStorage.apiLogin("operator", "password");
    assert.deepStrictEqual(loginResult, { operator: "test" });
    assert(success.calls.some((call) => call.url.endsWith("/api/ping")));
    assert(success.calls.some((call) => call.url.endsWith("/api/tsd/login") && call.method === "POST"));
    assert(success.clearedTimeouts.length >= 2, "expected ping and login timeouts to be cleared");

    const failure = createStorageContext({ loginResponse: "http-error" });
    const status = await Promise.race([
      failure.context.TsdStorage.apiLogin("operator", "bad").then(
        function () { return "resolved"; },
        function () { return "rejected"; }
      ),
      new Promise((resolve) => global.setTimeout(function () { resolve("pending"); }, 50)),
    ]);
    assert.strictEqual(status, "rejected");
    assert(failure.calls.some((call) => call.url.endsWith("/api/tsd/login") && call.method === "POST"));
    assert(failure.clearedTimeouts.length >= 2, "expected error path timeouts to be cleared");
  });
}

function testScriptLoadOrder() {
  const indexHtml = read(path.join(rootDir, "index.html"));
  const indexCompatMatches = indexHtml.match(/<script\b[^>]*\bsrc=["']compat\.js["'][^>]*>/g) || [];
  assert.strictEqual(indexCompatMatches.length, 1, "index.html should load compat.js exactly once");
  assert(
    indexHtml.indexOf('src="compat.js"') < indexHtml.indexOf('href="styles.css"'),
    "index.html must load compat.js before styles.css"
  );

  const mainScripts = scriptSrcs(path.join(rootDir, "index.html"));
  assertBefore(mainScripts, "compat.js", "storage.js", "index.html");
  assertBefore(mainScripts, "compat.js", "app.js", "index.html");

  const pcHtml = read(path.join(rootDir, "pc", "index.html"));
  const pcCompatMatches = pcHtml.match(/<script\b[^>]*\bsrc=["']\.\.\/compat\.js["'][^>]*>/g) || [];
  assert.strictEqual(pcCompatMatches.length, 1, "pc/index.html should load compat.js exactly once");

  const pcScripts = scriptSrcs(path.join(rootDir, "pc", "index.html"));
  assertBefore(pcScripts, "../compat.js", "./pc-core.js", "pc/index.html");
  assertBefore(pcScripts, "../compat.js", "./pc-order-modal.js", "pc/index.html");
  assertBefore(pcScripts, "../compat.js", "./pc-stock.js", "pc/index.html");
  assertBefore(pcScripts, "../compat.js", "./app.js", "pc/index.html");

  const scannerScripts = scriptSrcs(path.join(rootDir, "scanner_tests.html"));
  assert(scannerScripts.indexOf("compat.js") !== -1, "scanner_tests.html: missing compat.js");

  const scanTestScripts = scriptSrcs(path.join(rootDir, "scan_test.html"));
  assert(scanTestScripts.indexOf("compat.js") !== -1, "scan_test.html: missing compat.js");
}

function testServiceWorkerAndVersion() {
  const serviceWorker = read(path.join(rootDir, "service-worker.js"));
  assert(serviceWorker.indexOf('"./compat.js"') !== -1, "service-worker.js must precache compat.js");

  const appVersion = read(path.join(rootDir, "app-version.js"));
  assert(appVersion.indexOf('version = "71"') !== -1, "app-version.js must be bumped to 71");
}

(async function main() {
  await testPromiseFinallyShim();
  await testNativeFinallyIsNotOverwritten();
  await testCssCapabilityDetection();
  await testLoginFlowWithoutNativeFinally();
  testScriptLoadOrder();
  testServiceWorkerAndVersion();
  console.log("compat tests passed");
})().catch((error) => {
  console.error(error && error.stack ? error.stack : error);
  process.exit(1);
});
