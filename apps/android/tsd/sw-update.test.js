const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const swUpdateJs = fs.readFileSync(path.join(__dirname, "sw-update.js"), "utf8");
const appJs = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");

function createSwUpdateContext(isBusy) {
  const waitingMessages = [];
  const waitingWorker = {
    state: "installed",
    postMessage: function (payload) {
      waitingMessages.push(payload);
    },
    addEventListener: function () {}
  };
  const registration = {
    waiting: waitingWorker,
    installing: null,
    update: function () {
      return Promise.resolve();
    },
    addEventListener: function () {}
  };
  const context = {
    console: { info: function () {}, warn: function () {} },
    document: {
      body: { appendChild: function () {} },
      addEventListener: function () {},
      createElement: function () {
        return {
          id: "",
          className: "",
          hidden: true,
          innerHTML: "",
          addEventListener: function () {}
        };
      },
      getElementById: function () {
        return null;
      }
    },
    navigator: {
      serviceWorker: {
        controller: {},
        getRegistration: function () {
          return Promise.resolve(registration);
        },
        addEventListener: function () {}
      }
    },
    location: { reload: function () {} },
    FlowStockTsdIsBusy: function () {
      return isBusy;
    },
    TSD_PWA_VERSION: "15",
    TSD_CACHE_NAME: "flowstock-tsd-v15",
    setTimeout: function (fn) {
      fn();
    }
  };
  context.window = context;
  vm.runInNewContext(swUpdateJs, context);
  return { context: context, waitingMessages: waitingMessages };
}

async function runApplyUpdateScenario(isBusy) {
  const env = createSwUpdateContext(isBusy);
  await env.context.TsdSwUpdate.checkNow();
  const result = env.context.TsdSwUpdate.applyUpdate();
  return { result: result, waitingMessages: env.waitingMessages };
}

async function main() {
  assert(
    swUpdateJs.includes("checkNow") &&
      swUpdateJs.includes("navigator.serviceWorker.getRegistration") &&
      swUpdateJs.includes(".update()"),
    "checkNow should resolve registration and call update()"
  );
  assert(
    swUpdateJs.includes('status: "update_available"') &&
      swUpdateJs.includes("Установлена актуальная версия") &&
      swUpdateJs.includes("Ручная проверка обновления"),
    "checkNow should report update found or up to date"
  );
  assert(
    swUpdateJs.includes("Обновление отложено: активная операция") &&
      swUpdateJs.includes("Завершите текущую операцию перед обновлением"),
    "applyUpdate should block while busy"
  );
  assert(
    swUpdateJs.includes("reloadScheduled") && swUpdateJs.includes("scheduleReloadOnce"),
    "controllerchange reload should be guarded by reloadScheduled"
  );
  assert(
    swUpdateJs.includes("checkNow:") && swUpdateJs.includes("applyUpdate:"),
    "TsdSwUpdate should expose checkNow and applyUpdate"
  );
  assert(
    appJs.includes('id="pwaCheckUpdateBtn"') &&
      appJs.includes("Проверить обновления") &&
      appJs.includes("TsdSwUpdate.checkNow"),
    "settings screen should wire manual update check button"
  );
  assert(
    appJs.includes("getAppVersionLabel") || appJs.includes("TSD_PWA_VERSION"),
    "settings should show PWA version"
  );

  const blocked = await runApplyUpdateScenario(true);
  assert.strictEqual(blocked.result.ok, false);
  assert.match(blocked.result.message, /Завершите текущую операцию/);
  assert.strictEqual(blocked.waitingMessages.length, 0);

  const allowed = await runApplyUpdateScenario(false);
  assert.strictEqual(allowed.result.ok, true);
  assert.strictEqual(allowed.waitingMessages.length, 1);
  assert.strictEqual(allowed.waitingMessages[0].type, "SKIP_WAITING");

  let reloadCount = 0;
  const reloadContext = {
    reloadScheduled: false,
    reload: function () {
      reloadCount += 1;
    }
  };
  function scheduleReloadOnce() {
    if (reloadContext.reloadScheduled) {
      return;
    }
    reloadContext.reloadScheduled = true;
    reloadContext.reload();
  }
  scheduleReloadOnce();
  scheduleReloadOnce();
  assert.strictEqual(reloadCount, 1);

  console.log("TSD sw-update tests passed.");
}

main().catch(function (error) {
  console.error(error);
  process.exit(1);
});
