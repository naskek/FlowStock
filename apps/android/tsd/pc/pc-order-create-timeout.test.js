const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

async function timeoutScenario(timeoutOverride, expectedDelay) {
  const timers = [];
  const requests = [];
  const context = {
    Headers,
    AbortController,
    console,
    clearTimeout() {},
    fetch(url, options) {
      requests.push({ url, options });
      return new Promise((_resolve, reject) => {
        options.signal.addEventListener("abort", () =>
          reject(new Error("signal is aborted without reason")));
      });
    },
    window: {
      setTimeout(fn, delay) { timers.push({ fn, delay }); return timers.length; },
      clearTimeout() {},
    },
  };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(path.join(__dirname, "pc-core.js"), "utf8"), context);
  context.window.FlowStockPcCore.init({ getCurrentView: () => "orders" });
  const promise = context.window.FlowStockPcCore.fetchJson("/api/orders/requests/create",
    { method: "POST", timeoutMs: timeoutOverride, body: "{}" });
  assert.equal(requests.length, 1);
  assert.equal(timers[0].delay, expectedDelay);
  assert.equal(requests[0].options.timeoutMs, undefined);
  timers[0].fn();
  await assert.rejects(promise, error =>
    error.code === "REQUEST_TIMEOUT" && error.message === "REQUEST_TIMEOUT");
}

(async function () {
  await timeoutScenario(30000, 30000);
  await timeoutScenario(undefined, 8000);

  // The new-order modal must retain one stable operation key and exactly the
  // original payload across a retry, rather than constructing a second order.
  const app = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");
  assert.match(app, /if \(pendingSubmission\) \{\s*submitPreparedOrder\(null, true\)/);
  assert.match(app, /submission = \{ key: key, body: body \}/);
  assert.match(app, /"X-FlowStock-Order-Create-Key": submission\.key/);
  assert.match(app, /body: submission\.body,/);
  assert.match(app, /pending_submission: pendingSubmission/);
  assert.match(app, /setUncertainSubmissionMode\(\)/);
  assert.match(app, /if \(confirmedSubmission\) return;/);
  assert.match(app, /confirmedSubmission = true;/);
  assert.match(app, /!confirmedSubmission && modal\.isConnected/);
  assert.match(app, /lockSubmissionControls\(\);\s*setStatus\("Проверка сессии/);
  assert.match(app, /unlockSubmissionControls\(\)/);
  console.log("PC order create timeout and retry contract passed.");
})().catch(error => { console.error(error); process.exitCode = 1; });
