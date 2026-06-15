(function () {
  "use strict";

  var deps = {};
  var clientBlocks = getDefaultClientBlocks();

  function init(shared) {
    deps = shared || {};
  }

  function getDefaultClientBlocks() {
    return {
      pc_stock: true,
      pc_catalog: true,
      pc_orders: true,
    };
  }

  function applyClientBlocks(raw) {
    var next = getDefaultClientBlocks();
    if (raw && typeof raw === "object") {
      Object.keys(next).forEach(function (key) {
        if (raw[key] === false) {
          next[key] = false;
        }
      });
    }
    clientBlocks = next;
    return clientBlocks;
  }

  function isClientBlockEnabled(key) {
    return clientBlocks[key] !== false;
  }

  function getClientBlocksSignature() {
    return Object.keys(clientBlocks)
      .sort()
      .map(function (key) {
        return key + ":" + (clientBlocks[key] === false ? "0" : "1");
      })
      .join("|");
  }

  function normalizePlatform(value) {
    var normalized = String(value || "").trim().toUpperCase();
    if (normalized === "PC") {
      return "PC";
    }
    if (normalized === "BOTH" || normalized === "PC+TSD" || normalized === "PC_TSD") {
      return "BOTH";
    }
    return "TSD";
  }

  function hasPcAccess(account) {
    return !!account && (account.platform === "PC" || account.platform === "BOTH");
  }

  function loadAccount() {
    try {
      var raw = localStorage.getItem("flowstock_account");
      if (!raw) {
        return null;
      }
      var parsed = JSON.parse(raw);
      if (!parsed || !parsed.device_id) {
        return null;
      }
      return {
        device_id: String(parsed.device_id || "").trim(),
        login: String(parsed.login || "").trim(),
        platform: normalizePlatform(parsed.platform),
      };
    } catch (error) {
      return null;
    }
  }

  function saveAccount(account) {
    try {
      localStorage.setItem("flowstock_account", JSON.stringify(account || {}));
    } catch (error) {
      // ignore storage failures
    }
  }

  function clearAccount() {
    try {
      localStorage.removeItem("flowstock_account");
    } catch (error) {
      // ignore storage failures
    }
  }

  function setAccountLabel(account) {
    var accountLabel = document.getElementById("accountLabel");
    if (!accountLabel) {
      return;
    }
    if (!account) {
      accountLabel.textContent = "Гость";
      return;
    }
    accountLabel.textContent = account.login || account.device_id || "Пользователь";
  }

  function setLoginState(isLoggedIn) {
    document.body.classList.toggle("needs-login", !isLoggedIn);
  }

  function apiLogin(login, password) {
    return deps.fetchJson("/api/tsd/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ login: login, password: password }),
    });
  }

  function loadClientBlocks() {
    return deps
      .fetchJson("/api/client-blocks")
      .then(function (result) {
        return applyClientBlocks(result && result.blocks);
      })
      .catch(function () {
        return applyClientBlocks(null);
      });
  }

  function renderLogin() {
    return (
      '<section class="pc-login-card">' +
      '  <div class="screen-title">Вход</div>' +
      '  <label class="form-label" for="pcLoginInput">Логин</label>' +
      '  <input class="form-input" id="pcLoginInput" type="text" autocomplete="username" />' +
      '  <label class="form-label" for="pcPasswordInput">Пароль</label>' +
      '  <input class="form-input" id="pcPasswordInput" type="password" autocomplete="current-password" />' +
      '  <button id="pcLoginBtn" class="btn primary-btn" type="button">Войти</button>' +
      '  <div id="pcLoginStatus" class="status"></div>' +
      "</section>"
    );
  }

  function wireLogin() {
    var loginInput = document.getElementById("pcLoginInput");
    var passwordInput = document.getElementById("pcPasswordInput");
    var loginBtn = document.getElementById("pcLoginBtn");
    var statusEl = document.getElementById("pcLoginStatus");

    function setStatus(text) {
      if (statusEl) {
        statusEl.textContent = text || "";
      }
    }

    function submit() {
      var login = loginInput && loginInput.value ? loginInput.value.trim() : "";
      var password = passwordInput ? passwordInput.value : "";
      if (!login || !password) {
        setStatus("Введите логин и пароль.");
        return;
      }
      if (loginBtn) {
        loginBtn.disabled = true;
      }
      setStatus("Подключение...");
      apiLogin(login, password)
        .then(function (result) {
          var deviceId = result && result.device_id ? String(result.device_id).trim() : "";
          var platform = normalizePlatform(result && result.platform);
          if (!deviceId) {
            throw new Error("NO_DEVICE_ID");
          }
          if (platform !== "PC" && platform !== "BOTH") {
            throw new Error("WRONG_PLATFORM");
          }
          applyClientBlocks(result && result.blocks);
          var account = { device_id: deviceId, login: login, platform: platform };
          saveAccount(account);
          setAccountLabel(account);
          setLoginState(true);
          if (deps.onLoginSuccess) {
            deps.onLoginSuccess(account);
          }
        })
        .catch(function (error) {
          if (loginBtn) {
            loginBtn.disabled = false;
          }
          var code = error && error.message ? error.message : "";
          var message = "Ошибка входа.";
          if (code === "INVALID_CREDENTIALS") {
            message = "Пользователь не найден. Обратитесь к оператору.";
          } else if (code === "DEVICE_BLOCKED") {
            message = "Аккаунт заблокирован. Обратитесь к оператору.";
          } else if (code === "WRONG_PLATFORM") {
            message = "Этот аккаунт не имеет доступа к ПК.";
          }
          setStatus(message);
        });
    }

    if (loginBtn) {
      loginBtn.addEventListener("click", submit);
    }
    if (passwordInput) {
      passwordInput.addEventListener("keydown", function (event) {
        if (event.key === "Enter") {
          event.preventDefault();
          submit();
        }
      });
    }
    if (loginInput) {
      loginInput.focus();
    }
  }

  window.FlowStockPcAuth = {
    init: init,
    getDefaultClientBlocks: getDefaultClientBlocks,
    applyClientBlocks: applyClientBlocks,
    isClientBlockEnabled: isClientBlockEnabled,
    getClientBlocksSignature: getClientBlocksSignature,
    normalizePlatform: normalizePlatform,
    hasPcAccess: hasPcAccess,
    loadAccount: loadAccount,
    saveAccount: saveAccount,
    clearAccount: clearAccount,
    setAccountLabel: setAccountLabel,
    setLoginState: setLoginState,
    apiLogin: apiLogin,
    loadClientBlocks: loadClientBlocks,
    renderLogin: renderLogin,
    wireLogin: wireLogin,
  };
})();
