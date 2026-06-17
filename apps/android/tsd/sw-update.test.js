const assert = require("assert");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const swUpdateJs = fs.readFileSync(path.join(__dirname, "sw-update.js"), "utf8");
const serviceWorkerJs = fs.readFileSync(path.join(__dirname, "service-worker.js"), "utf8");
const appJs = fs.readFileSync(path.join(__dirname, "app.js"), "utf8");

function createSwUpdateContext(options) {
  options = options || {};
  var waitingMessages = [];
  var reloadCount = 0;
  var bannerStatusText = "";
  var buttonDisabled = false;
  var buttonText = "Обновить";
  var scheduledTimers = [];
  var updateCalls = 0;

  var waitingWorker = options.waitingWorker === undefined
    ? {
        state: "installed",
        postMessage: function (payload) {
          waitingMessages.push(payload);
        },
        addEventListener: function () {}
      }
    : options.waitingWorker;

  var registration = {
    waiting: waitingWorker,
    installing: null,
    update: function () {
      updateCalls += 1;
      if (typeof options.onUpdate === "function") {
        options.onUpdate(registration);
      }
      return Promise.resolve();
    },
    addEventListener: function () {}
  };

  var applyBtn = {
    disabled: false,
    textContent: "Обновить",
    addEventListener: function (eventName, handler) {
      if (eventName === "click") {
        applyBtn._clickHandler = handler;
      }
    }
  };
  Object.defineProperty(applyBtn, "disabled", {
    get: function () {
      return buttonDisabled;
    },
    set: function (value) {
      buttonDisabled = !!value;
    }
  });
  Object.defineProperty(applyBtn, "textContent", {
    get: function () {
      return buttonText;
    },
    set: function (value) {
      buttonText = String(value || "");
    }
  });

  var bannerStatusEl = {
    textContent: "",
    set textContent(value) {
      bannerStatusText = String(value || "");
    },
    get textContent() {
      return bannerStatusText;
    }
  };

  var context = {
    console: { info: function () {}, warn: function () {} },
    addEventListener: function () {},
    document: {
      body: { appendChild: function () {} },
      addEventListener: function () {},
      createElement: function () {
        return {
          id: "",
          className: "",
          hidden: true,
          innerHTML: ""
        };
      },
      getElementById: function (id) {
        if (id === "pwaUpdateApplyBtn") {
          return applyBtn;
        }
        if (id === "pwaUpdateBannerStatus") {
          return bannerStatusEl;
        }
        return null;
      }
    },
    navigator: {
      serviceWorker: {
        controller: options.controller === false ? null : {},
        register: function () {
          return Promise.resolve(registration);
        },
        getRegistration: function () {
          return Promise.resolve(registration);
        },
        addEventListener: function () {}
      }
    },
    location: {
      reload: function () {
        reloadCount += 1;
      }
    },
    FlowStockTsdIsBusy:
      typeof options.busyFn === "function"
        ? options.busyFn
        : function () {
            return options.isBusy === true;
          },
    FlowStockTsdGetBusyDiagnostics: options.getBusyDiagnostics || function () {
      return null;
    },
    TSD_PWA_VERSION: "16",
    TSD_CACHE_NAME: "flowstock-tsd-v16",
    setTimeout: function (fn, ms) {
      if (ms === 4000) {
        scheduledTimers.push(fn);
        return scheduledTimers.length;
      }
      fn();
      return 0;
    },
    clearTimeout: function () {}
  };
  context.window = context;
  vm.runInNewContext(swUpdateJs, context);
  context.TsdSwUpdate.init();
  return {
    context: context,
    waitingMessages: waitingMessages,
    reloadCount: function () {
      return reloadCount;
    },
    bannerStatusText: function () {
      return bannerStatusText;
    },
    buttonDisabled: function () {
      return buttonDisabled;
    },
    buttonText: function () {
      return buttonText;
    },
    scheduledTimers: scheduledTimers,
    updateCalls: function () {
      return updateCalls;
    },
    applyBtn: applyBtn,
    registration: registration
  };
}

async function runApplyUpdateScenario(isBusy) {
  const env = createSwUpdateContext({ isBusy: isBusy });
  await env.context.TsdSwUpdate.checkNow();
  const result = await env.context.TsdSwUpdate.applyUpdate();
  return { env: env, result: result, waitingMessages: env.waitingMessages };
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
    swUpdateJs.includes("Применяем обновление") &&
      swUpdateJs.includes("Обновление активируется") &&
      swUpdateJs.includes("Обновление не найдено. Нажмите Проверить обновления."),
    "applyUpdate should show visible status messages"
  );
  assert(
    swUpdateJs.includes("Обновление отложено: активная операция") &&
      swUpdateJs.includes("Завершите текущую операцию перед обновлением"),
    "applyUpdate should block while busy"
  );
  assert(
    swUpdateJs.includes("busy === true") &&
      swUpdateJs.includes("FlowStockTsdGetBusyDiagnostics"),
    "isBusy should use strict boolean check and log diagnostics"
  );
  assert(
    swUpdateJs.includes("Отправляем SKIP_WAITING") &&
      swUpdateJs.includes('postMessage({ type: "SKIP_WAITING" })'),
    "applyUpdate should post SKIP_WAITING to waiting worker"
  );
  assert(
    swUpdateJs.includes("controllerchange, перезагрузка") &&
      swUpdateJs.includes("controllerchange не сработал, выполняем fallback reload"),
    "applyUpdate should reload on controllerchange or fallback timer"
  );
  assert(
    swUpdateJs.includes("reloadScheduled") && swUpdateJs.includes("clearFallbackReloadTimer"),
    "reload should be guarded and fallback timer cleared"
  );
  assert(
    serviceWorkerJs.includes('event.data.type !== "SKIP_WAITING"') &&
      serviceWorkerJs.includes('[TSD PWA SW] SKIP_WAITING'),
    "service worker should listen for SKIP_WAITING and log activation"
  );
  assert(
    appJs.includes('id="pwaCheckUpdateBtn"') &&
      appJs.includes("Проверить обновления") &&
      appJs.includes("TsdSwUpdate.checkNow"),
    "settings screen should wire manual update check button"
  );

  const blocked = await runApplyUpdateScenario(true);
  assert.strictEqual(blocked.result.ok, false);
  assert.match(blocked.result.message, /Завершите текущую операцию/);
  assert.strictEqual(blocked.waitingMessages.length, 0);
  assert.match(blocked.env.bannerStatusText(), /Завершите текущую операцию/);
  assert.strictEqual(blocked.env.buttonDisabled(), false);

  for (const busyValue of [{}, "busy", null, undefined]) {
    const env = createSwUpdateContext({
      busyFn: function () {
        return busyValue;
      }
    });
    await env.context.TsdSwUpdate.checkNow();
    const result = await env.context.TsdSwUpdate.applyUpdate();
    assert.strictEqual(result.ok, true, "non-boolean busy must not block update: " + String(busyValue));
    assert.strictEqual(env.waitingMessages.length, 1, "SKIP_WAITING should be sent for non-boolean busy");
    assert.strictEqual(env.waitingMessages[0].type, "SKIP_WAITING");
  }

  const allowed = await runApplyUpdateScenario(false);
  assert.strictEqual(allowed.result.ok, true);
  assert.strictEqual(allowed.waitingMessages.length, 1);
  assert.strictEqual(allowed.waitingMessages[0].type, "SKIP_WAITING");
  assert.match(allowed.env.bannerStatusText(), /Обновление активируется/);
  assert.strictEqual(allowed.env.buttonDisabled(), true);
  assert.strictEqual(allowed.env.buttonText(), "Обновляем…");

  const applyingEnv = createSwUpdateContext({ isBusy: false });
  await applyingEnv.context.TsdSwUpdate.checkNow();
  const applyingPromise = applyingEnv.context.TsdSwUpdate.applyUpdate();
  assert.match(applyingEnv.bannerStatusText(), /Применяем обновление/);
  await applyingPromise;

  const noWaitingEnv = createSwUpdateContext({
    waitingWorker: null,
    onUpdate: function () {}
  });
  await noWaitingEnv.context.TsdSwUpdate.showUpdateBanner();
  const noWaitingResult = await noWaitingEnv.context.TsdSwUpdate.applyUpdate();
  assert.strictEqual(noWaitingResult.ok, false);
  assert.ok(noWaitingEnv.updateCalls() >= 1);
  assert.match(noWaitingEnv.bannerStatusText(), /Обновление не найдено/);
  assert.match(noWaitingEnv.bannerStatusText(), /очистите данные PWA/);
  assert.strictEqual(noWaitingEnv.buttonDisabled(), false);

  const fallbackEnv = createSwUpdateContext({ isBusy: false });
  await fallbackEnv.context.TsdSwUpdate.checkNow();
  await fallbackEnv.context.TsdSwUpdate.applyUpdate();
  assert.strictEqual(fallbackEnv.reloadCount(), 0);
  assert.strictEqual(fallbackEnv.scheduledTimers.length, 1);
  fallbackEnv.scheduledTimers[0]();
  assert.strictEqual(fallbackEnv.reloadCount(), 1);
  fallbackEnv.scheduledTimers[0]();
  assert.strictEqual(fallbackEnv.reloadCount(), 1);

  const controllerEnv = createSwUpdateContext({ isBusy: false });
  controllerEnv.context.TsdSwUpdate._test.triggerControllerChangeReload();
  assert.strictEqual(controllerEnv.reloadCount(), 1);
  controllerEnv.context.TsdSwUpdate._test.triggerControllerChangeReload();
  assert.strictEqual(controllerEnv.reloadCount(), 1);

  const buttonEnv = createSwUpdateContext({ isBusy: false });
  await buttonEnv.context.TsdSwUpdate.checkNow();
  const clickPromise = buttonEnv.applyBtn._clickHandler();
  assert.strictEqual(buttonEnv.buttonDisabled(), true);
  const clickResult = await clickPromise;
  assert.strictEqual(clickResult.ok, true);

  const failedButtonEnv = createSwUpdateContext({ waitingWorker: null });
  await failedButtonEnv.context.TsdSwUpdate.showUpdateBanner();
  const failedClickResult = await failedButtonEnv.applyBtn._clickHandler();
  assert.strictEqual(failedClickResult.ok, false);
  assert.strictEqual(failedButtonEnv.buttonDisabled(), false);

  console.log("TSD sw-update tests passed.");
}

main().catch(function (error) {
  console.error(error);
  process.exit(1);
});
