(function () {
  "use strict";

  var deps = {};
  var clientBlocks = getDefaultClientBlocks();
  var currentAccount = null;
  var capabilities = [];

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
    return currentAccount;
  }

  function saveAccount(account) {
    currentAccount = account || null;
  }

  function clearAccount() {
    currentAccount = null;
    capabilities = [];
  }

  function applySession(result) {
    var rawAccount = result && result.account;
    if (!rawAccount || !rawAccount.device_id) {
      throw new Error("INVALID_SESSION");
    }
    currentAccount = {
      device_id: String(rawAccount.device_id || "").trim(),
      login: String(rawAccount.login || "").trim(),
      platform: normalizePlatform(rawAccount.platform),
      access_role: String(rawAccount.access_role || "OPERATOR").trim().toUpperCase(),
    };
    capabilities = Array.isArray(result.capabilities) ? result.capabilities.slice() : [];
    applyClientBlocks(result.blocks);
    return currentAccount;
  }

  function hasCapability(capability) {
    return capabilities.indexOf(capability) >= 0;
  }

  function setAccountLabel(account) {
    var accountLabel = document.getElementById("accountLabel");
    var accountInitial = document.getElementById("accountInitial");
    if (!accountLabel) {
      return;
    }
    var text = !account
      ? "Гость"
      : account.login || account.device_id || "Пользователь";
    accountLabel.textContent = text;
    if (accountInitial) {
      accountInitial.textContent = (text.charAt(0) || "?").toUpperCase();
    }
  }

  function setLoginState(isLoggedIn) {
    document.body.classList.toggle("needs-login", !isLoggedIn);
  }

  function apiLogin(login, password) {
    return deps.fetchJson("/api/pc/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ login: login, password: password }),
    });
  }

  function loadSession() {
    return deps.fetchJson("/api/pc/session").then(applySession);
  }

  function apiLogout() {
    return deps.fetchJson("/api/pc/logout", { method: "POST" }).finally(clearAccount);
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
          var account = applySession(result);
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
          } else if (code === "WRONG_PLATFORM" || code === "PC_ACCESS_DENIED") {
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
    hasCapability: hasCapability,
    loadAccount: loadAccount,
    saveAccount: saveAccount,
    clearAccount: clearAccount,
    setAccountLabel: setAccountLabel,
    setLoginState: setLoginState,
    apiLogin: apiLogin,
    loadSession: loadSession,
    apiLogout: apiLogout,
    loadClientBlocks: loadClientBlocks,
    renderLogin: renderLogin,
    wireLogin: wireLogin,
  };
})();
