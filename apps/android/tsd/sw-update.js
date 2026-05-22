(function (root) {
  "use strict";

  var reloadScheduled = false;
  var waitingWorker = null;
  var bannerEl = null;
  var applyBtn = null;
  var registrationRef = null;

  function logInfo(message) {
    console.info("[TSD PWA] " + message);
  }

  function logWarn(message, error) {
    if (error) {
      console.warn("[TSD PWA] " + message, error);
      return;
    }
    console.warn("[TSD PWA] " + message);
  }

  function isBusy() {
    if (typeof root.FlowStockTsdIsBusy === "function") {
      try {
        return !!root.FlowStockTsdIsBusy();
      } catch (error) {
        logWarn("Не удалось определить активную операцию", error);
      }
    }
    return false;
  }

  function ensureBanner() {
    if (bannerEl) {
      return;
    }
    bannerEl = document.createElement("div");
    bannerEl.id = "pwaUpdateBanner";
    bannerEl.className = "pwa-update-banner";
    bannerEl.hidden = true;
    bannerEl.innerHTML =
      '<div class="pwa-update-banner-text">Доступна новая версия приложения</div>' +
      '<button type="button" class="btn pwa-update-banner-btn" id="pwaUpdateApplyBtn">Обновить</button>';
    document.body.appendChild(bannerEl);
    applyBtn = document.getElementById("pwaUpdateApplyBtn");
    applyBtn.addEventListener("click", function () {
      applyPendingUpdate();
    });
  }

  function showUpdateBanner() {
    ensureBanner();
    bannerEl.hidden = false;
    logInfo("Показано предложение обновить приложение");
  }

  function hideUpdateBanner() {
    if (bannerEl) {
      bannerEl.hidden = true;
    }
  }

  function trackWaitingWorker(worker) {
    waitingWorker = worker;
    worker.addEventListener("statechange", function () {
      if (worker.state === "activated") {
        logInfo("Новый service worker активирован");
      }
      if (worker.state === "installed" && root.navigator.serviceWorker.controller) {
        logInfo("Обнаружена новая версия приложения (worker installed)");
        showUpdateBanner();
      }
    });
    if (worker.state === "installed" && root.navigator.serviceWorker.controller) {
      logInfo("Обнаружена новая версия приложения (worker waiting)");
      showUpdateBanner();
    }
  }

  function applyPendingUpdate() {
    if (!waitingWorker) {
      logWarn("Обновление недоступно: нет waiting service worker");
      return;
    }
    if (isBusy()) {
      logInfo("Обновление отложено: активна операция сканирования/наполнения");
      showUpdateBanner();
      return;
    }
    logInfo("Запрошена активация новой версии (SKIP_WAITING)");
    waitingWorker.postMessage({ type: "SKIP_WAITING" });
  }

  function scheduleReloadOnce() {
    if (reloadScheduled) {
      return;
    }
    reloadScheduled = true;
    logInfo("Перезагрузка страницы после активации нового service worker");
    root.location.reload();
  }

  function bindRegistration(registration) {
    registrationRef = registration;
    registration.addEventListener("updatefound", function () {
      var installing = registration.installing;
      if (!installing) {
        return;
      }
      logInfo("Найдено обновление service worker");
      trackWaitingWorker(installing);
    });
    if (registration.waiting && root.navigator.serviceWorker.controller) {
      trackWaitingWorker(registration.waiting);
    }
  }

  function checkForUpdates() {
    if (!registrationRef) {
      return Promise.resolve();
    }
    return registrationRef.update().catch(function (error) {
      logWarn("Не удалось проверить обновление service worker", error);
    });
  }

  function registerServiceWorker() {
    if (!("serviceWorker" in root.navigator)) {
      return Promise.resolve();
    }
    return root.navigator.serviceWorker
      .register("./service-worker.js")
      .then(function (registration) {
        logInfo("Service worker зарегистрирован, cache=" + (root.TSD_CACHE_NAME || "unknown"));
        bindRegistration(registration);
        root.navigator.serviceWorker.addEventListener("controllerchange", function () {
          if (!root.navigator.serviceWorker.controller) {
            return;
          }
          scheduleReloadOnce();
        });
        return checkForUpdates();
      })
      .catch(function (error) {
        logWarn("Ошибка регистрации service worker", error);
      });
  }

  function init() {
    ensureBanner();
    registerServiceWorker();
    root.addEventListener("focus", checkForUpdates);
    document.addEventListener("visibilitychange", function () {
      if (document.visibilityState === "visible") {
        checkForUpdates();
      }
    });
  }

  root.TsdSwUpdate = {
    init: init,
    checkForUpdates: checkForUpdates,
    showUpdateBanner: showUpdateBanner,
    hideUpdateBanner: hideUpdateBanner,
    applyPendingUpdate: applyPendingUpdate
  };
})(window);
