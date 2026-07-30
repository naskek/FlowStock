const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const tsdDir = __dirname;
const appVersionJs = fs.readFileSync(path.join(tsdDir, "app-version.js"), "utf8");
const serviceWorkerJs = fs.readFileSync(path.join(tsdDir, "service-worker.js"), "utf8");

const appVersionMatch = appVersionJs.match(/\bvar version = "([^"]+)";/);
assert(appVersionMatch, "app-version.js must define a string version");
const appVersion = appVersionMatch[1];

const workerVersionMatch = serviceWorkerJs.match(
  /\bvar TSD_SERVICE_WORKER_VERSION = "([^"]+)";/
);
assert(workerVersionMatch, "service-worker.js must define TSD_SERVICE_WORKER_VERSION");
const workerVersion = workerVersionMatch[1];

assert.strictEqual(
  workerVersion,
  appVersion,
  "service-worker.js marker must match TSD_PWA_VERSION from app-version.js"
);
assert(
  /importScripts\("\.\/app-version\.js\?v=" \+ TSD_SERVICE_WORKER_VERSION\);/.test(
    serviceWorkerJs
  ),
  "service-worker.js must import app-version.js with its version marker in the URL"
);
assert(
  /\bCACHE_NAME = self\.TSD_CACHE_NAME\b/.test(serviceWorkerJs),
  "service-worker.js must use the cache name exported by app-version.js"
);

const context = { self: {} };
vm.runInNewContext(appVersionJs, context);
assert.strictEqual(context.self.TSD_PWA_VERSION, appVersion);
assert.strictEqual(
  context.self.TSD_CACHE_NAME,
  "flowstock-tsd-v" + appVersion,
  "cache name must be computed from TSD_PWA_VERSION"
);

console.log("TSD service worker version contract tests passed.");
