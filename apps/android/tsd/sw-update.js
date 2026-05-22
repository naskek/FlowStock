(function (root) {
  "use strict";

  var reloadScheduled = false;
  var waitingWorker = null;
  var bannerEl = null;
  var applyBtn = null;
  var bannerStatusEl = null;
  var registrationRef = null;
  var registrationBound = false;
  var controllerChangeBound = false;
  var fallbackReloadTimerId = null;
  var applyInProgress = false;
  var FALLBACK_RELOAD_MS = 4000;

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

  function clearFallbackReloadTimer() {
    if (fallbackReloadTimerId != null) {
      root.clearTimeout(fallbackReloadTimerId);
      fallbackReloadTimerId = null;
    }
  }

  function setBannerStatus(message) {
    if (bannerStatusEl) {
      bannerStatusEl.textContent = message || "";
    }
  }

  function setApplyButtonState(disabled, label) {
    if (!applyBtn) {
      return;
    }
    applyBtn.disabled = !!disabled;
    applyBtn.textContent = label || "Обновить";
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
      '<div class="pwa-update-banner-body">' +
      '<div class="pwa-update-banner-text">Доступна новая версия приложения</div>' +
      '<div class="pwa-update-banner-status" id="pwaUpdateBannerStatus"></div>' +
      "</div>" +
      '<button type="button" class="btn pwa-update-banner-btn" id="pwaUpdateApplyBtn">Обновить</button>';
    document.body.appendChild(bannerEl);
    bannerStatusEl = document.getElementById("pwaUpdateBannerStatus");
    applyBtn = document.getElementById("pwaUpdateApplyBtn");
    applyBtn.addEventListener("click", function () {
      return applyUpdate().then(function (result) {
        if (result && !result.ok && !applyInProgress) {
          setApplyButtonState(false, "Обновить");
        }
        return result;
      });
    });
  }

  function showUpdateBanner() {
    ensureBanner();
    bannerEl.hidden = false;
    setBannerStatus("");
    logInfo("Показано предложение обновить приложение");
  }

  function hideUpdateBanner() {
    if (bannerEl) {
      bannerEl.hidden = true;
    }
    setBannerStatus("");
  }

  function syncWaitingFromRegistration(registration) {
    if (registration && registration.waiting) {
      trackWaitingWorker(registration.waiting);
      return true;
    }
    return !!waitingWorker;
  }

  function trackWaitingWorker(worker) {
    if (!worker) {
      return;
    }
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

  function resolveWaitingWorker() {
    if (!("serviceWorker" in root.navigator)) {
      return Promise.resolve(null);
    }
    return root.navigator.serviceWorker.getRegistration().then(function (registration) {
      if (!registration) {
        return null;
      }
      bindRegistration(registration);
      var worker = waitingWorker || registration.waiting;
      if (worker) {
        waitingWorker = worker;
        return worker;
      }
      return registration
        .update()
        .catch(function (error) {
          logWarn("Не удалось проверить обновление service worker", error);
          return null;
        })
        .then(function () {
          worker = waitingWorker || registration.waiting;
          if (worker) {
            waitingWorker = worker;
            return worker;
          }
          return null;
        });
    });
  }

  function startApplyWithWorker(worker) {
    waitingWorker = worker;
    applyInProgress = true;
    setBannerStatus("Обновление активируется…");
    setApplyButtonState(true, "Обновляем…");
    logInfo("Отправляем SKIP_WAITING");
    worker.postMessage({ type: "SKIP_WAITING" });
    clearFallbackReloadTimer();
    fallbackReloadTimerId = root.setTimeout(function () {
      fallbackReloadTimerId = null;
      if (reloadScheduled) {
        return;
      }
      logWarn("controllerchange не сработал, выполняем fallback reload");
      scheduleReloadOnce();
    }, FALLBACK_RELOAD_MS);
  }

  function applyUpdate() {
    ensureBanner();
    setBannerStatus("Применяем обновление…");
    setApplyButtonState(true, "Обновляем…");

    if (isBusy()) {
      logInfo("Обновление отложено: активная операция");
      setBannerStatus("Завершите текущую операцию перед обновлением");
      setApplyButtonState(false, "Обновить");
      applyInProgress = false;
      return Promise.resolve({
        ok: false,
        message: "Завершите текущую операцию перед обновлением"
      });
    }

    return resolveWaitingWorker().then(function (worker) {
      if (!worker) {
        logWarn("Обновление не найдено: нет waiting service worker");
        setBannerStatus(
          "Обновление не найдено. Нажмите Проверить обновления. " +
            "Не удалось применить обновление автоматически. Закройте и откройте приложение или очистите данные PWA."
        );
        setApplyButtonState(false, "Обновить");
        applyInProgress = false;
        return {
          ok: false,
          message: "Обновление не найдено. Нажмите Проверить обновления."
        };
      }

      startApplyWithWorker(worker);
      return {
        ok: true,
        message: "Обновление активируется…"
      };
    });
  }

  function scheduleReloadOnce() {
    if (reloadScheduled) {
      return;
    }
    reloadScheduled = true;
    applyInProgress = false;
    clearFallbackReloadTimer();
    logInfo("Перезагрузка страницы после активации нового service worker");
    root.location.reload();
  }

  function ensureControllerChangeListener() {
    if (controllerChangeBound || !("serviceWorker" in root.navigator)) {
      return;
    }
    controllerChangeBound = true;
    root.navigator.serviceWorker.addEventListener("controllerchange", function () {
      if (!root.navigator.serviceWorker.controller) {
        return;
      }
      logInfo("controllerchange, перезагрузка");
      clearFallbackReloadTimer();
      scheduleReloadOnce();
    });
  }

  function bindRegistration(registration) {
    registrationRef = registration;
    ensureControllerChangeListener();
    if (registrationBound) {
      syncWaitingFromRegistration(registration);
      return;
    }
    registrationBound = true;
    registration.addEventListener("updatefound", function () {
      var installing = registration.installing;
      if (!installing) {
        return;
      }
      logInfo("Найдено обновление service worker");
      trackWaitingWorker(installing);
    });
    syncWaitingFromRegistration(registration);
  }

  function waitForInstallingWorker(registration, timeoutMs) {
    return new Promise(function (resolve) {
      var settled = false;
      function finish(result) {
        if (settled) {
          return;
        }
        settled = true;
        resolve(result);
      }

      function inspectWaiting() {
        if (registration.waiting && root.navigator.serviceWorker.controller) {
          trackWaitingWorker(registration.waiting);
          logInfo("Обновление найдено");
          showUpdateBanner();
          finish({
            status: "update_available",
            message: "Доступна новая версия приложения"
          });
          return true;
        }
        return false;
      }

      if (inspectWaiting()) {
        return;
      }

      var installing = registration.installing;
      if (installing) {
        installing.addEventListener("statechange", function () {
          if (installing.state === "installed" && inspectWaiting()) {
            return;
          }
          if (installing.state === "installed" && !root.navigator.serviceWorker.controller) {
            finish({
              status: "up_to_date",
              message: "Установлена актуальная версия"
            });
          }
        });
      }

      root.setTimeout(function () {
        if (inspectWaiting()) {
          return;
        }
        logInfo("Новая версия не найдена");
        finish({
          status: "up_to_date",
          message: "Установлена актуальная версия"
        });
      }, timeoutMs);
    });
  }

  function checkForUpdates() {
    if (!registrationRef) {
      return root.navigator.serviceWorker.getRegistration().then(function (registration) {
        if (!registration) {
          return null;
        }
        bindRegistration(registration);
        return registrationRef.update();
      });
    }
    return registrationRef.update().catch(function (error) {
      logWarn("Не удалось проверить обновление service worker", error);
    });
  }

  function checkNow() {
    logInfo("Ручная проверка обновления");
    if (!("serviceWorker" in root.navigator)) {
      return Promise.resolve({
        status: "unsupported",
        message: "Service Worker не поддерживается"
      });
    }

    return root.navigator.serviceWorker
      .getRegistration()
      .then(function (registration) {
        if (!registration) {
          return {
            status: "no_registration",
            message: "Service Worker не зарегистрирован"
          };
        }

        bindRegistration(registration);
        return registration
          .update()
          .catch(function (error) {
            logWarn("Не удалось проверить обновление service worker", error);
            return null;
          })
          .then(function () {
            if (registration.waiting && root.navigator.serviceWorker.controller) {
              trackWaitingWorker(registration.waiting);
              logInfo("Обновление найдено");
              showUpdateBanner();
              return {
                status: "update_available",
                message: "Доступна новая версия приложения"
              };
            }
            if (registration.installing) {
              return waitForInstallingWorker(registration, 2500);
            }
            logInfo("Новая версия не найдена");
            return {
              status: "up_to_date",
              message: "Установлена актуальная версия"
            };
          });
      })
      .catch(function (error) {
        logWarn("Ошибка ручной проверки обновления", error);
        return {
          status: "error",
          message: "Не удалось проверить обновление"
        };
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
        return checkForUpdates();
      })
      .catch(function (error) {
        logWarn("Ошибка регистрации service worker", error);
      });
  }

  function init() {
    ensureBanner();
    ensureControllerChangeListener();
    registerServiceWorker();
    root.addEventListener("focus", checkForUpdates);
    document.addEventListener("visibilitychange", function () {
      if (document.visibilityState === "visible") {
        checkForUpdates();
      }
    });
  }

  function getAppVersionLabel() {
    var version = root.TSD_PWA_VERSION ? String(root.TSD_PWA_VERSION).trim() : "";
    return version ? "Версия приложения: " + version : "Версия приложения: неизвестно";
  }

  root.TsdSwUpdate = {
    init: init,
    checkForUpdates: checkForUpdates,
    checkNow: checkNow,
    applyUpdate: applyUpdate,
    applyPendingUpdate: applyUpdate,
    showUpdateBanner: showUpdateBanner,
    hideUpdateBanner: hideUpdateBanner,
    getAppVersionLabel: getAppVersionLabel,
    _test: {
      scheduleReloadOnce: scheduleReloadOnce,
      clearFallbackReloadTimer: clearFallbackReloadTimer,
      getReloadScheduled: function () {
        return reloadScheduled;
      },
      getFallbackReloadTimerId: function () {
        return fallbackReloadTimerId;
      },
      triggerControllerChangeReload: function () {
        logInfo("controllerchange, перезагрузка");
        clearFallbackReloadTimer();
        scheduleReloadOnce();
      },
      FALLBACK_RELOAD_MS: FALLBACK_RELOAD_MS
    }
  };
})(window);
