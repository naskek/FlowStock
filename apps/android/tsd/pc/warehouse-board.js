(function () {
  "use strict";

  var SCENARIO_MOVE = "MOVE_HU";
  var SCENARIO_ADOPT = "ADOPT_PALLET_PLAN";

  var UI_LABELS = {
    PAGE_TITLE: "Задания склада",
    WHAT_TO_DO: "Что нужно сделать?",
    INTRO_HINT:
      "Выберите действие. FlowStock создаст пакет, который уйдёт в WPF на подтверждение. Складские остатки не изменятся до подтверждения исполнения.",
    SAFETY_HINT:
      "Web только планирует действия. Остатки изменятся только после подтверждения в WPF и исполнения задания.",
    COL_ORDERS: "Заказы",
    COL_HU: "HU / Паллеты",
    COL_PACKAGE: "Пакет действий",
    SUBMIT_BTN: "Отправить пакет на подтверждение",
    RESET_SCENARIO: "Сбросить сценарий",
    NEW_PACKAGE: "Новый пакет",
    PREVIEW: "Проверить",
    CLEAR: "Очистить",
    REFRESH: "Обновить",
    BUNDLES_LIST: "Список пакетов",
    EMPTY_PACKAGE: "Пакет пока пустой. Выберите действие сверху.",
    EMPTY_PACKAGE_SCENARIO: "Чтобы добавить действие, выполните шаги слева.",
    CARD_MOVE_TITLE: "Переместить паллету / HU",
    CARD_MOVE_DESC:
      "Выберите HU, затем место назначения. Задание уйдёт на подтверждение в WPF и исполнение на ТСД.",
    CARD_ADOPT_TITLE: "Перенести план паллет",
    CARD_ADOPT_DESC: "Выберите внутренний заказ-источник и клиентский заказ-получатель.",
    CARD_SHIP_TITLE: "Подготовить отгрузку",
    CARD_SHIP_DESC: "Будет доступно после реализации отгрузочных заданий.",
    CARD_MERGE_TITLE: "Объединить заказ",
    CARD_MERGE_DESC: "Перенести потребность внутреннего заказа в клиентский.",
    ADD_MOVE: "Добавить перемещение в пакет",
    ADD_ADOPT: "Добавить перенос плана в пакет",
  };

  var deps = null;
  var board = null;
  var orderFilter = "ALL";
  var activeScenario = null;
  var selectedOrderId = null;
  var selectedHuCode = null;
  var moveTargetLocationId = null;
  var adoptSourceId = null;
  var adoptTargetId = null;
  var draftBundleId = null;
  var draftBundleRef = null;
  var draftBundleStatus = "DRAFT";
  var draftLines = [];
  var previewResult = null;
  var lastSubmittedRef = null;

  var STATUS_LABELS = {
    DRAFT: "Черновик",
    SUBMITTED: "На подтверждении",
    APPROVED: "Подтверждено",
    IN_EXECUTION: "В работе",
    EXECUTED: "Исполнено ТСД",
    COMPLETED: "Проведено",
    REJECTED: "Отклонено",
    CANCELLED: "Отменено",
    FAILED: "Ошибка",
  };

  var ORDER_FILTERS = [
    { value: "ALL", label: "Все" },
    { value: "CUSTOMER", label: "Клиентские" },
    { value: "INTERNAL", label: "Внутренние" },
    { value: "WITH_PLAN", label: "С планом паллет" },
    { value: "ACTIVE", label: "Активные" },
  ];

  function init(shared) {
    deps = shared;
  }

  function esc(value) {
    return deps ? deps.escapeHtml(value) : String(value || "");
  }

  function accountActor() {
    if (!deps || !deps.getAccount) {
      return "WEB_PLANNER";
    }
    var account = deps.getAccount();
    return (account && account.login) || (account && account.device_id) || "WEB_PLANNER";
  }

  function statusLabel(status) {
    var key = String(status || "").trim().toUpperCase();
    return STATUS_LABELS[key] || status || "—";
  }

  function locationDisplay(locationId) {
    if (!board || !board.locations) {
      return "место #" + locationId;
    }
    var found = board.locations.find(function (loc) {
      return loc.id === locationId;
    });
    if (!found) {
      return "место #" + locationId;
    }
    if (found.code && found.name && found.code !== found.name) {
      return found.code + " — " + found.name;
    }
    return found.display || found.code || found.name || "место #" + locationId;
  }

  function orderById(orderId) {
    if (!board || !board.orders || !orderId) {
      return null;
    }
    return (
      board.orders.find(function (order) {
        return order.id === orderId;
      }) || null
    );
  }

  function huByCode(huCode) {
    if (!board || !board.hu_stock || !huCode) {
      return null;
    }
    return (
      board.hu_stock.find(function (row) {
        return String(row.hu_code).toUpperCase() === String(huCode).toUpperCase();
      }) || null
    );
  }

  function formatPlannerError(payload) {
    var parts = [];
    if (payload && Array.isArray(payload.errors)) {
      payload.errors.forEach(function (entry) {
        parts.push(formatPreviewIssue(entry));
      });
    }
    if (payload && payload.message) {
      parts.push(String(payload.message));
    }
    return parts.length ? parts.join(" ") : "Ошибка сервера";
  }

  function formatPreviewIssue(issue) {
    if (!issue) {
      return "";
    }
    var code = String(issue.code || "").toUpperCase();
    if (code === "TARGET_ALREADY_HAS_PALLET_PLAN") {
      return "У клиентского заказа уже есть план паллет. Сначала удалите текущий план.";
    }
    return issue.message || issue.code || "";
  }

  function plannerFetch(url, options) {
    var opts = options || {};
    var headers = new Headers(opts.headers || {});
    if (!headers.has("Content-Type") && opts.body) {
      headers.set("Content-Type", "application/json");
    }
    if (deps && deps.createRequestHeaders) {
      headers = deps.createRequestHeaders(headers, url);
    }
    return fetch(url, {
      method: opts.method || "GET",
      headers: headers,
      body: opts.body,
      cache: "no-store",
    }).then(function (response) {
      return response
        .json()
        .catch(function () {
          return null;
        })
        .then(function (payload) {
          if (!response.ok) {
            throw new Error(formatPlannerError(payload));
          }
          return payload;
        });
    });
  }

  function loadBoardState() {
    return plannerFetch("/api/planner/warehouse-board/state").then(function (payload) {
      board = {
        orders: (payload && payload.orders) || [],
        hu_stock: (payload && payload.hu_stock) || [],
        locations: (payload && payload.locations) || [],
        pallet_plans: (payload && payload.pallet_plans) || [],
      };
      return board;
    });
  }

  function getBundle(id) {
    return plannerFetch("/api/planner/bundles/" + encodeURIComponent(String(id)));
  }

  function createBundle() {
    return plannerFetch("/api/planner/bundles", {
      method: "POST",
      body: JSON.stringify({
        source: "WEB_PLANNER",
        created_by: accountActor(),
        comment: null,
      }),
    });
  }

  function addLine(bundleId, line) {
    return plannerFetch("/api/planner/bundles/" + encodeURIComponent(String(bundleId)) + "/lines", {
      method: "POST",
      body: JSON.stringify(line),
    });
  }

  function submitBundle(bundleId) {
    return plannerFetch("/api/planner/bundles/" + encodeURIComponent(String(bundleId)) + "/submit", {
      method: "POST",
      body: JSON.stringify({ actor: accountActor() }),
    });
  }

  function cancelBundle(bundleId) {
    return plannerFetch("/api/planner/bundles/" + encodeURIComponent(String(bundleId)) + "/cancel", {
      method: "POST",
    });
  }

  function previewLines(lines) {
    return plannerFetch("/api/planner/bundles/preview", {
      method: "POST",
      body: JSON.stringify({ lines: lines }),
    });
  }

  function ensureDraftBundle() {
    if (draftBundleId) {
      return Promise.resolve(draftBundleId);
    }
    return createBundle().then(function (created) {
      draftBundleId = created.bundle_id;
      draftBundleRef = created.bundle_ref || "";
      draftBundleStatus = created.status || "DRAFT";
      draftLines = [];
      return draftBundleId;
    });
  }

  function syncDraftFromServer() {
    if (!draftBundleId) {
      draftLines = [];
      return Promise.resolve();
    }
    return getBundle(draftBundleId).then(function (payload) {
      if (payload && payload.bundle) {
        draftBundleRef = payload.bundle.bundle_ref;
        draftBundleStatus = payload.bundle.status;
      }
      draftLines = (payload && payload.lines) || [];
      return payload;
    });
  }

  function hasBlockingPreviewErrors() {
    return !!(previewResult && previewResult.errors && previewResult.errors.length);
  }

  function canSubmitPackage() {
    return draftLines.length > 0 && !hasBlockingPreviewErrors();
  }

  function packageMiniStatus() {
    if (lastSubmittedRef) {
      return { key: "submitted", label: "Отправлен" };
    }
    if (hasBlockingPreviewErrors()) {
      return { key: "errors", label: "Есть ошибки" };
    }
    if (previewResult && previewResult.valid && draftLines.length) {
      return { key: "checked", label: "Проверен" };
    }
    if (draftLines.length) {
      return { key: "draft", label: "Черновик" };
    }
    return { key: "empty", label: "" };
  }

  function packageStatusText() {
    if (!draftBundleId && !draftLines.length) {
      return "Пакет не создан";
    }
    return "Пакет: " + (draftBundleRef || "…") + " / " + statusLabel(draftBundleStatus);
  }

  function resetScenario() {
    activeScenario = null;
    adoptSourceId = null;
    adoptTargetId = null;
    selectedHuCode = null;
    selectedOrderId = null;
    moveTargetLocationId = null;
    orderFilter = "ALL";
    refreshUi();
  }

  function startScenario(scenario) {
    activeScenario = scenario;
    adoptSourceId = null;
    adoptTargetId = null;
    selectedHuCode = null;
    selectedOrderId = null;
    moveTargetLocationId = null;
    lastSubmittedRef = null;
    if (scenario === SCENARIO_ADOPT) {
      orderFilter = "WITH_PLAN";
    } else {
      orderFilter = "ALL";
    }
    refreshUi();
  }

  function linesToPreviewInput(lines) {
    return (lines || []).map(function (line) {
      return {
        action_type: line.action_type,
        payload_json: line.payload_json || "{}",
        source_order_id: line.source_order_id,
        target_order_id: line.target_order_id,
        item_id: line.item_id,
        hu_code: line.hu_code,
        from_location_id: line.from_location_id,
        to_location_id: line.to_location_id,
        qty: line.qty,
      };
    });
  }

  function formatActionLineLabel(line) {
    if (!line) {
      return "";
    }
    var action = String(line.action_type || "").toUpperCase();
    if (action === "MOVE_HU") {
      return (
        "Переместить " +
        (line.hu_code || "HU") +
        ": " +
        locationDisplay(line.from_location_id) +
        " → " +
        locationDisplay(line.to_location_id)
      );
    }
    if (action === "ADOPT_PALLET_PLAN") {
      var source = orderById(line.source_order_id);
      var target = orderById(line.target_order_id);
      return (
        "Перенести план паллет: заказ " +
        ((source && source.order_ref) || "?") +
        " → заказ " +
        ((target && target.order_ref) || "?")
      );
    }
    return "Действие";
  }

  function filteredOrders() {
    if (!board || !board.orders) {
      return [];
    }
    if (activeScenario === SCENARIO_ADOPT) {
      if (!adoptSourceId) {
        return board.orders.filter(function (order) {
          return (
            order.type === "INTERNAL" &&
            order.has_pallet_plan &&
            order.is_active
          );
        });
      }
      if (!adoptTargetId) {
        return board.orders.filter(function (order) {
          return order.type === "CUSTOMER" && order.is_active;
        });
      }
      return board.orders.filter(function (order) {
        return order.id === adoptSourceId || order.id === adoptTargetId;
      });
    }
    return board.orders.filter(function (order) {
      if (orderFilter === "CUSTOMER" && order.type !== "CUSTOMER") {
        return false;
      }
      if (orderFilter === "INTERNAL" && order.type !== "INTERNAL") {
        return false;
      }
      if (orderFilter === "WITH_PLAN" && !order.has_pallet_plan) {
        return false;
      }
      if (orderFilter === "ACTIVE" && !order.is_active) {
        return false;
      }
      return true;
    });
  }

  function huSelectionSummary(row) {
    if (!row) {
      return "";
    }
    var place = row.location_code || row.location_name || "—";
    if (row.location_name && row.location_code && row.location_code !== row.location_name) {
      place = row.location_code + " — " + row.location_name;
    }
    return (
      "Выбрано: " +
      row.hu_code +
      ", " +
      (row.item_name || "товар") +
      ", " +
      row.qty +
      " шт, место " +
      place
    );
  }

  function onHuRowClick(huCode) {
    if (activeScenario !== SCENARIO_MOVE) {
      return;
    }
    selectedHuCode = huCode;
    moveTargetLocationId = null;
    refreshUi();
  }

  function onOrderRowClick(orderId) {
    if (activeScenario !== SCENARIO_ADOPT) {
      selectedOrderId = orderId;
      refreshUi();
      return;
    }
    var order = orderById(orderId);
    if (!order) {
      return;
    }
    if (!adoptSourceId) {
      if (order.type !== "INTERNAL" || !order.has_pallet_plan) {
        return;
      }
      adoptSourceId = orderId;
      selectedOrderId = orderId;
    } else if (!adoptTargetId) {
      if (order.type !== "CUSTOMER") {
        return;
      }
      adoptTargetId = orderId;
      selectedOrderId = orderId;
    } else {
      if (order.type === "INTERNAL" && order.has_pallet_plan) {
        adoptSourceId = orderId;
        adoptTargetId = null;
        selectedOrderId = orderId;
      } else if (order.type === "CUSTOMER") {
        adoptTargetId = orderId;
        selectedOrderId = orderId;
      }
    }
    refreshUi();
  }

  function moveSameLocationWarning() {
    var row = huByCode(selectedHuCode);
    if (!row || !moveTargetLocationId) {
      return false;
    }
    return Number(moveTargetLocationId) === Number(row.location_id);
  }

  function getSubmitDisabledReason() {
    if (!draftLines.length) {
      return "Пакет пустой";
    }
    if (hasBlockingPreviewErrors()) {
      return "В пакете есть ошибки";
    }
    return "";
  }

  function getAddMoveDisabledReason() {
    if (!selectedHuCode) {
      return "Сначала выберите HU";
    }
    if (!moveTargetLocationId) {
      return "Сначала выберите место назначения";
    }
    if (moveSameLocationWarning()) {
      return "Выберите другое место назначения";
    }
    return "";
  }

  function getAddAdoptDisabledReason() {
    if (!adoptSourceId) {
      return "Выберите внутренний заказ-источник";
    }
    if (!adoptTargetId) {
      return "Выберите клиентский заказ-получатель";
    }
    var target = orderById(adoptTargetId);
    if (target && target.has_pallet_plan) {
      return "У получателя уже есть план паллет";
    }
    return "";
  }

  function btnHtml(id, label, enabled, reason, primary) {
    return (
      '<span class="wp-btn-wrap">' +
      '<button type="button" id="' +
      esc(id) +
      '" class="wp-btn' +
      (primary ? " wp-btn-primary" : "") +
      '"' +
      (enabled ? "" : " disabled") +
      ">" +
      esc(label) +
      "</button>" +
      (!enabled && reason
        ? '<span class="wp-btn-reason" title="' + esc(reason) + '">' + esc(reason) + "</span>"
        : "") +
      "</span>"
    );
  }

  function setNotice(message, kind) {
    var node = document.getElementById("wpNotice");
    if (!node) {
      return;
    }
    if (!message) {
      node.hidden = true;
      node.textContent = "";
      node.className = "wp-notice";
      return;
    }
    node.hidden = false;
    node.textContent = message;
    node.className = "wp-notice" + (kind === "error" ? " is-error" : kind === "ok" ? " is-ok" : "");
  }

  function updateChrome() {
    var statusNode = document.getElementById("wpHeaderStatus");
    if (statusNode) {
      statusNode.textContent = packageStatusText();
    }
    var submitBtn = document.getElementById("wpSubmitBtn");
    if (submitBtn) {
      var canSubmit = canSubmitPackage();
      submitBtn.disabled = !canSubmit;
      var reasonNode = document.getElementById("wpSubmitReason");
      if (reasonNode) {
        reasonNode.textContent = canSubmit ? "" : getSubmitDisabledReason();
      }
    }
  }

  function renderScenarioPicker() {
    var root = document.getElementById("wpScenarioPicker");
    if (!root) {
      return;
    }
    var hidden = !!activeScenario;
    root.hidden = hidden;
    if (hidden) {
      return;
    }
    root.innerHTML =
      "<h3>" +
      esc(UI_LABELS.WHAT_TO_DO) +
      "</h3>" +
      '<p class="wp-intro-hint">' +
      esc(UI_LABELS.INTRO_HINT) +
      "</p>" +
      '<div class="wp-scenario-cards">' +
      '<button type="button" class="wp-scenario-card" data-scenario="' +
      SCENARIO_MOVE +
      '">' +
      "<strong>" +
      esc(UI_LABELS.CARD_MOVE_TITLE) +
      "</strong>" +
      "<span>" +
      esc(UI_LABELS.CARD_MOVE_DESC) +
      "</span></button>" +
      '<button type="button" class="wp-scenario-card" data-scenario="' +
      SCENARIO_ADOPT +
      '">' +
      "<strong>" +
      esc(UI_LABELS.CARD_ADOPT_TITLE) +
      "</strong>" +
      "<span>" +
      esc(UI_LABELS.CARD_ADOPT_DESC) +
      "</span></button>" +
      '<button type="button" class="wp-scenario-card is-disabled" disabled>' +
      "<strong>" +
      esc(UI_LABELS.CARD_SHIP_TITLE) +
      "</strong>" +
      "<span>" +
      esc(UI_LABELS.CARD_SHIP_DESC) +
      "</span></button>" +
      '<button type="button" class="wp-scenario-card is-disabled" disabled>' +
      "<strong>" +
      esc(UI_LABELS.CARD_MERGE_TITLE) +
      "</strong>" +
      "<span>" +
      esc(UI_LABELS.CARD_MERGE_DESC) +
      "</span></button>" +
      "</div>";
    root.querySelectorAll("[data-scenario]").forEach(function (card) {
      card.addEventListener("click", function () {
        startScenario(card.getAttribute("data-scenario"));
      });
    });
  }

  function renderScenarioWizard() {
    var root = document.getElementById("wpScenarioWizard");
    if (!root) {
      return;
    }
    if (!activeScenario) {
      root.hidden = true;
      root.innerHTML = "";
      return;
    }
    root.hidden = false;
    var title =
      activeScenario === SCENARIO_MOVE
        ? UI_LABELS.CARD_MOVE_TITLE
        : UI_LABELS.CARD_ADOPT_TITLE;
    var html =
      '<div class="wp-wizard-head">' +
      "<strong>" +
      esc(title) +
      '</strong><button type="button" class="wp-btn" id="wpResetScenarioBtn">' +
      esc(UI_LABELS.RESET_SCENARIO) +
      "</button></div>";

    if (activeScenario === SCENARIO_MOVE) {
      var huRow = huByCode(selectedHuCode);
      var locOptions = (board.locations || [])
        .filter(function (loc) {
          return !huRow || loc.id !== huRow.location_id;
        })
        .map(function (loc) {
          var sel = moveTargetLocationId === loc.id ? " selected" : "";
          return (
            '<option value="' +
            esc(loc.id) +
            '"' +
            sel +
            ">" +
            esc(loc.display || loc.code) +
            "</option>"
          );
        })
        .join("");
      var addEnabled = !getAddMoveDisabledReason();
      html +=
        '<ol class="wp-steps">' +
        '<li class="wp-step' +
        (selectedHuCode ? " is-done" : " is-active") +
        '">' +
        "<h4>Шаг 1. Выберите HU</h4>" +
        '<p class="wp-step-hint">Кликните по паллете/HU, которую нужно переместить.</p>' +
        (huRow ? '<p class="wp-step-selected">' + esc(huSelectionSummary(huRow)) + "</p>" : "") +
        "</li>" +
        '<li class="wp-step' +
        (selectedHuCode ? " is-active" : "") +
        (moveTargetLocationId && !moveSameLocationWarning() ? " is-done" : "") +
        '">' +
        "<h4>Шаг 2. Выберите место назначения</h4>" +
        (huRow
          ? '<p class="wp-step-readonly"><strong>Сейчас:</strong> ' +
            esc(locationDisplay(huRow.location_id)) +
            "</p>" +
            '<label>Куда переместить<select id="wpMoveToLoc" class="pc-input"' +
            (selectedHuCode ? "" : " disabled") +
            '><option value="">— выберите —</option>' +
            locOptions +
            "</select></label>" +
            (moveSameLocationWarning()
              ? '<p class="wp-feedback is-warn">Нельзя выбрать то же место, что и сейчас.</p>'
              : "")
          : '<p class="wp-step-hint">Сначала выберите HU.</p>') +
        "</li>" +
        '<li class="wp-step">' +
        "<h4>Шаг 3. Добавить в пакет</h4>" +
        btnHtml("wpAddMoveBtn", UI_LABELS.ADD_MOVE, addEnabled, getAddMoveDisabledReason(), true) +
        "</li>" +
        '<li class="wp-step">' +
        "<h4>Шаг 4. Отправить</h4>" +
        '<p class="wp-step-hint">Отправьте пакет на подтверждение в WPF.</p>' +
        "</li></ol>";
    }

    if (activeScenario === SCENARIO_ADOPT) {
      var src = orderById(adoptSourceId);
      var tgt = orderById(adoptTargetId);
      var targetWarn =
        tgt && tgt.has_pallet_plan
          ? '<p class="wp-feedback is-warn">У получателя уже есть план паллет. Сначала удалите текущий план.</p>'
          : "";
      var addAdoptEnabled = !getAddAdoptDisabledReason();
      html +=
        '<ol class="wp-steps">' +
        '<li class="wp-step' +
        (adoptSourceId ? " is-done" : " is-active") +
        '">' +
        "<h4>Шаг 1. Выберите внутренний заказ-источник</h4>" +
        '<p class="wp-step-hint">В таблице заказов — только внутренние с планом паллет.</p>' +
        (src
          ? '<p class="wp-step-selected">Источник: заказ ' +
            esc(src.order_ref) +
            ", паллет: " +
            esc(src.planned_pallet_count || 0) +
            "</p>"
          : "") +
        "</li>" +
        '<li class="wp-step' +
        (adoptSourceId ? " is-active" : "") +
        (adoptTargetId ? " is-done" : "") +
        '">' +
        "<h4>Шаг 2. Выберите клиентский заказ-получатель</h4>" +
        '<p class="wp-step-hint">Кликните по клиентскому заказу без плана (или с предупреждением).</p>' +
        (tgt
          ? '<p class="wp-step-selected">Получатель: заказ ' + esc(tgt.order_ref) + "</p>"
          : "") +
        targetWarn +
        "</li>" +
        '<li class="wp-step">' +
        "<h4>Шаг 3. Добавить в пакет</h4>" +
        btnHtml("wpAddAdoptBtn", UI_LABELS.ADD_ADOPT, addAdoptEnabled, getAddAdoptDisabledReason(), true) +
        "</li>" +
        '<li class="wp-step">' +
        "<h4>Шаг 4. Проверка</h4>" +
        '<p class="wp-step-hint">После добавления выполняется автоматическая проверка.</p>' +
        "</li></ol>";
    }

    root.innerHTML = html;
    var resetBtn = document.getElementById("wpResetScenarioBtn");
    if (resetBtn) {
      resetBtn.addEventListener("click", resetScenario);
    }
    var locSelect = document.getElementById("wpMoveToLoc");
    if (locSelect) {
      locSelect.addEventListener("change", function () {
        moveTargetLocationId = Number(locSelect.value) || null;
        refreshUi();
      });
    }
    var addMove = document.getElementById("wpAddMoveBtn");
    if (addMove) {
      addMove.addEventListener("click", addMoveToPackage);
    }
    var addAdopt = document.getElementById("wpAddAdoptBtn");
    if (addAdopt) {
      addAdopt.addEventListener("click", addAdoptToPackage);
    }
  }

  function applyColumnHighlight() {
    var ordersCol = document.querySelector(".wp-col-orders");
    var huCol = document.querySelector(".wp-col-hu");
    if (!ordersCol || !huCol) {
      return;
    }
    ordersCol.classList.remove("is-highlight", "is-dimmed");
    huCol.classList.remove("is-highlight", "is-dimmed");
    if (!activeScenario) {
      return;
    }
    if (activeScenario === SCENARIO_MOVE) {
      huCol.classList.add("is-highlight");
      ordersCol.classList.add("is-dimmed");
    }
    if (activeScenario === SCENARIO_ADOPT) {
      ordersCol.classList.add("is-highlight");
      huCol.classList.add("is-dimmed");
    }
  }

  function renderFilterPills() {
    var root = document.getElementById("wpOrderFilters");
    if (!root) {
      return;
    }
    var locked = activeScenario === SCENARIO_ADOPT;
    root.innerHTML = ORDER_FILTERS.map(function (f) {
      var active = f.value === orderFilter;
      return (
        '<button type="button" class="wp-filter-pill' +
        (active ? " is-active" : "") +
        '"' +
        (locked ? " disabled" : "") +
        ' data-filter="' +
        esc(f.value) +
        '">' +
        esc(f.label) +
        "</button>"
      );
    }).join("");
    if (activeScenario === SCENARIO_ADOPT && !adoptSourceId) {
      root.insertAdjacentHTML(
        "afterbegin",
        '<span class="wp-filter-note">Показаны: внутренние с планом паллет</span>'
      );
    } else if (activeScenario === SCENARIO_ADOPT && adoptSourceId && !adoptTargetId) {
      root.insertAdjacentHTML(
        "afterbegin",
        '<span class="wp-filter-note">Показаны: клиентские активные</span>'
      );
    }
  }

  function renderOrdersTable() {
    var body = document.getElementById("wpOrdersBody");
    if (!body) {
      return;
    }
    var rows = filteredOrders();
    if (!rows.length) {
      body.innerHTML = '<tr><td colspan="6" class="pc-muted">Заказов нет.</td></tr>';
      return;
    }
    body.innerHTML = rows
      .map(function (order) {
        var isSelected =
          (activeScenario === SCENARIO_ADOPT &&
            (order.id === adoptSourceId || order.id === adoptTargetId)) ||
          (activeScenario !== SCENARIO_ADOPT && selectedOrderId === order.id);
        var isSource = adoptSourceId === order.id;
        var isTarget = adoptTargetId === order.id;
        var planLabel = order.has_pallet_plan
          ? "Есть (" + (order.planned_pallet_count || 0) + ")"
          : "Нет";
        var planBadge =
          activeScenario === SCENARIO_ADOPT &&
          order.type === "CUSTOMER" &&
          order.has_pallet_plan
            ? ' <span class="wp-badge wp-badge-warn">Есть план</span>'
            : "";
        return (
          '<tr class="wp-row' +
          (isSelected ? " is-selected" : "") +
          '" data-order-row="' +
          esc(order.id) +
          '">' +
          "<td>" +
          esc(order.order_ref) +
          (isSource ? ' <span class="wp-badge wp-badge-source">источник</span>' : "") +
          (isTarget ? ' <span class="wp-badge wp-badge-target">получатель</span>' : "") +
          planBadge +
          "</td>" +
          "<td>" +
          esc(order.type_display) +
          "</td>" +
          "<td>" +
          esc(order.status_display) +
          "</td>" +
          "<td>" +
          esc(order.partner_name || "—") +
          "</td>" +
          "<td>" +
          esc(planLabel) +
          "</td>" +
          "<td>" +
          esc(order.filled_pallet_count || 0) +
          "/" +
          esc(order.planned_pallet_count || 0) +
          "</td>" +
          "</tr>"
        );
      })
      .join("");
  }

  function renderHuTable() {
    var body = document.getElementById("wpHuBody");
    if (!body) {
      return;
    }
    var rows = (board && board.hu_stock) || [];
    if (!rows.length) {
      body.innerHTML = '<tr><td colspan="5" class="pc-muted">HU на остатках нет.</td></tr>';
      return;
    }
    body.innerHTML = rows
      .map(function (row) {
        var isSelected =
          activeScenario === SCENARIO_MOVE &&
          selectedHuCode &&
          String(selectedHuCode).toUpperCase() === String(row.hu_code).toUpperCase();
        var reserve =
          row.order_ref ||
          row.reserved_customer_order_ref ||
          row.origin_internal_order_ref ||
          "—";
        return (
          '<tr class="wp-row' +
          (isSelected ? " is-selected" : "") +
          '" data-hu-row="' +
          esc(row.hu_code) +
          '">' +
          "<td>" +
          esc(row.hu_code) +
          "</td>" +
          "<td>" +
          esc(row.item_name || "товар") +
          "</td>" +
          "<td>" +
          esc(row.qty) +
          "</td>" +
          "<td>" +
          esc(row.location_code || row.location_name || "—") +
          "</td>" +
          "<td>" +
          esc(reserve) +
          "</td>" +
          "</tr>"
        );
      })
      .join("");
  }

  function renderPackagePanel() {
    var root = document.getElementById("wpPackagePanel");
    if (!root) {
      return;
    }
    var mini = packageMiniStatus();
    var miniHtml = mini.label
      ? '<span class="wp-mini-status wp-mini-status--' + esc(mini.key) + '">' + esc(mini.label) + "</span>"
      : "";

    var emptyHint = !activeScenario
      ? UI_LABELS.EMPTY_PACKAGE
      : draftLines.length
        ? ""
        : UI_LABELS.EMPTY_PACKAGE_SCENARIO;

    var linesHtml =
      draftLines.length > 0
        ? '<ul class="wp-package-lines">' +
          draftLines
            .map(function (line) {
              return (
                '<li><span class="wp-line-text">' +
                esc(formatActionLineLabel(line)) +
                '</span><button type="button" class="wp-btn wp-btn-danger" data-remove-line="' +
                esc(line.line_no) +
                '">Удалить</button></li>'
              );
            })
            .join("") +
          "</ul>"
        : emptyHint
          ? '<p class="wp-empty-hint">' + esc(emptyHint) + "</p>"
          : "";

    var previewHtml = "";
    if (previewResult) {
      var errors = previewResult.errors || [];
      var warnings = previewResult.warnings || [];
      if (errors.length) {
        previewHtml +=
          '<div class="wp-feedback is-error">' +
          esc(errors.map(formatPreviewIssue).join(" ")) +
          "</div>";
      }
      if (warnings.length) {
        previewHtml +=
          '<div class="wp-feedback is-warn">' +
          esc(warnings.map(formatPreviewIssue).join(" ")) +
          "</div>";
      }
      if (previewResult.valid && !errors.length && !warnings.length) {
        previewHtml = '<div class="wp-feedback is-ok">Проверка пройдена.</div>';
      }
    }
    if (lastSubmittedRef) {
      previewHtml =
        '<div class="wp-feedback is-ok">Пакет ' +
        esc(lastSubmittedRef) +
        " отправлен в WPF на подтверждение.</div>" +
        previewHtml;
    }

    root.innerHTML =
      "<h3>" +
      esc(UI_LABELS.COL_PACKAGE) +
      " " +
      miniHtml +
      "</h3>" +
      linesHtml +
      previewHtml;
    wirePackageActions();
    updateChrome();
  }

  function addMoveToPackage() {
    var reason = getAddMoveDisabledReason();
    if (reason) {
      setNotice(reason, "error");
      return;
    }
    var row = huByCode(selectedHuCode);
    if (!row) {
      return;
    }
    var line = {
      action_type: "MOVE_HU",
      hu_code: row.hu_code,
      item_id: row.item_id,
      qty: row.qty,
      from_location_id: row.location_id,
      to_location_id: moveTargetLocationId,
      payload_json: JSON.stringify({
        hu_code: row.hu_code,
        item_id: row.item_id,
        qty: row.qty,
        from_location_id: row.location_id,
        to_location_id: moveTargetLocationId,
      }),
    };
    ensureDraftBundle()
      .then(function (bundleId) {
        return addLine(bundleId, line);
      })
      .then(function () {
        return syncDraftFromServer();
      })
      .then(function () {
        return runPreview();
      })
      .then(function () {
        setNotice("Добавлено: " + formatActionLineLabel(line), "ok");
        refreshUi();
      })
      .catch(function (e) {
        setNotice(String(e.message || e), "error");
      });
  }

  function addAdoptToPackage() {
    var reason = getAddAdoptDisabledReason();
    if (reason) {
      setNotice(reason, "error");
      return;
    }
    var source = orderById(adoptSourceId);
    var target = orderById(adoptTargetId);
    var line = {
      action_type: "ADOPT_PALLET_PLAN",
      source_order_id: source.id,
      target_order_id: target.id,
      payload_json: JSON.stringify({
        source_internal_order_id: source.id,
        target_customer_order_id: target.id,
      }),
    };
    ensureDraftBundle()
      .then(function (bundleId) {
        return addLine(bundleId, line);
      })
      .then(function () {
        return syncDraftFromServer();
      })
      .then(function () {
        return runPreview();
      })
      .then(function () {
        setNotice("Добавлено: " + formatActionLineLabel(line), "ok");
        refreshUi();
      })
      .catch(function (e) {
        setNotice(String(e.message || e), "error");
      });
  }

  function runPreview() {
    if (!draftLines.length) {
      previewResult = null;
      renderPackagePanel();
      return Promise.resolve();
    }
    return previewLines(linesToPreviewInput(draftLines)).then(function (result) {
      previewResult = result;
      renderPackagePanel();
    });
  }

  function removeLine(lineNo) {
    if (!draftBundleId) {
      return Promise.resolve();
    }
    var remaining = draftLines.filter(function (line) {
      return line.line_no !== lineNo;
    });
    return cancelBundle(draftBundleId)
      .then(function () {
        draftBundleId = null;
        draftBundleRef = null;
        draftLines = [];
        previewResult = null;
        if (!remaining.length) {
          refreshUi();
          return;
        }
        return ensureDraftBundle().then(function () {
          var chain = Promise.resolve();
          remaining.forEach(function (line) {
            chain = chain.then(function () {
              return addLine(draftBundleId, {
                action_type: line.action_type,
                payload_json: line.payload_json || "{}",
                source_order_id: line.source_order_id,
                target_order_id: line.target_order_id,
                item_id: line.item_id,
                hu_code: line.hu_code,
                from_location_id: line.from_location_id,
                to_location_id: line.to_location_id,
                qty: line.qty,
              });
            });
          });
          return chain;
        });
      })
      .then(function () {
        return syncDraftFromServer();
      })
      .then(function () {
        return runPreview();
      })
      .then(refreshUi)
      .catch(function (e) {
        setNotice(String(e.message || e), "error");
      });
  }

  function clearPackage() {
    lastSubmittedRef = null;
    if (!draftBundleId) {
      draftLines = [];
      previewResult = null;
      refreshUi();
      setNotice("Пакет очищен.", "ok");
      return Promise.resolve();
    }
    return cancelBundle(draftBundleId)
      .then(function () {
        draftBundleId = null;
        draftBundleRef = null;
        draftBundleStatus = "DRAFT";
        draftLines = [];
        previewResult = null;
        refreshUi();
        setNotice("Пакет очищен.", "ok");
      })
      .catch(function (e) {
        setNotice(String(e.message || e), "error");
      });
  }

  function submitPackage() {
    var reason = getSubmitDisabledReason();
    if (reason) {
      setNotice(reason, "error");
      return Promise.resolve();
    }
    return runPreview()
      .then(function () {
        if (hasBlockingPreviewErrors()) {
          throw new Error("Исправьте ошибки в пакете перед отправкой.");
        }
        return submitBundle(draftBundleId);
      })
      .then(function (result) {
        lastSubmittedRef = result.bundle_ref || draftBundleRef;
        setNotice("Пакет " + lastSubmittedRef + " отправлен в WPF на подтверждение.", "ok");
        draftBundleId = null;
        draftBundleRef = null;
        draftLines = [];
        previewResult = null;
        resetScenario();
        return loadBoardState();
      })
      .then(function () {
        refreshUi();
      })
      .catch(function (e) {
        setNotice(String(e.message || e), "error");
      });
  }

  function loadBundles() {
    return plannerFetch("/api/planner/bundles").then(function (payload) {
      return (payload && payload.bundles) || [];
    });
  }

  function openBundlesModal() {
    return loadBundles().then(function (rows) {
      var body =
        rows.length > 0
          ? '<table class="pc-table wp-table"><thead><tr><th>№</th><th>Статус</th><th></th></tr></thead><tbody>' +
            rows
              .map(function (row) {
                return (
                  "<tr><td>" +
                  esc(row.bundle_ref) +
                  "</td><td>" +
                  esc(statusLabel(row.status)) +
                  '</td><td><button type="button" class="wp-btn" data-view-bundle="' +
                  esc(row.id) +
                  '">Открыть</button></td></tr>'
                );
              })
              .join("") +
            "</tbody></table>"
          : '<p class="pc-muted">Пакетов нет.</p>';
      var modal = document.createElement("div");
      modal.className = "pc-modal";
      modal.innerHTML =
        '<div class="pc-modal-card"><div class="pc-modal-header"><div class="pc-modal-title">' +
        esc(UI_LABELS.BUNDLES_LIST) +
        '</div></div>' +
        body +
        '<div class="pc-modal-footer"><button type="button" class="wp-btn" data-close-modal>Закрыть</button></div></div>';
      document.body.appendChild(modal);
      modal.querySelector("[data-close-modal]").addEventListener("click", function () {
        modal.remove();
      });
    });
  }

  function wirePackageActions() {
    var panel = document.getElementById("wpPackagePanel");
    if (!panel) {
      return;
    }
    panel.querySelectorAll("[data-remove-line]").forEach(function (btn) {
      btn.addEventListener("click", function () {
        removeLine(Number(btn.getAttribute("data-remove-line")));
      });
    });
  }

  function refreshUi() {
    renderScenarioPicker();
    renderScenarioWizard();
    renderFilterPills();
    renderOrdersTable();
    renderHuTable();
    renderPackagePanel();
    applyColumnHighlight();
    updateChrome();
  }

  function refreshBoard() {
    return loadBoardState()
      .then(refreshUi)
      .catch(function (e) {
        setNotice(String(e.message || e), "error");
      });
  }

  function renderWarehousePlanner() {
    return (
      '<div class="pc-page-shell wp-page-shell">' +
      '<section class="wp-board">' +
      '<header class="wp-header">' +
      "<h2>" +
      esc(UI_LABELS.PAGE_TITLE) +
      "</h2>" +
      '<p class="wp-header-status" id="wpHeaderStatus">Пакет не создан</p>' +
      '<div class="wp-header-actions">' +
      btnHtml("wpNewBundleBtn", UI_LABELS.NEW_PACKAGE, true, "", false) +
      btnHtml("wpPreviewBtn", UI_LABELS.PREVIEW, true, "", false) +
      '<span class="wp-btn-wrap"><button type="button" id="wpSubmitBtn" class="wp-btn wp-btn-primary" disabled>' +
      esc(UI_LABELS.SUBMIT_BTN) +
      '</button><span class="wp-btn-reason" id="wpSubmitReason"></span></span>' +
      btnHtml("wpClearBtn", UI_LABELS.CLEAR, true, "", false) +
      btnHtml("wpRefreshBtn", UI_LABELS.REFRESH, true, "", false) +
      btnHtml("wpBundlesBtn", UI_LABELS.BUNDLES_LIST, true, "", false) +
      "</div></header>" +
      '<div id="wpNotice" class="wp-notice" hidden></div>' +
      '<section id="wpScenarioPicker" class="wp-scenario-picker"><h3>' +
      esc(UI_LABELS.WHAT_TO_DO) +
      "</h3></section>" +
      '<section id="wpScenarioWizard" class="wp-scenario-wizard" hidden></section>' +
      '<div class="wp-columns">' +
      '<div class="wp-col wp-col-orders">' +
      "<h3>" +
      esc(UI_LABELS.COL_ORDERS) +
      "</h3>" +
      '<div class="wp-filters" id="wpOrderFilters"></div>' +
      '<div class="wp-table-wrap"><table class="pc-table wp-table"><thead><tr>' +
      "<th>№</th><th>Тип</th><th>Статус</th><th>Контрагент</th><th>План</th><th>Паллет</th>" +
      '</tr></thead><tbody id="wpOrdersBody"><tr><td colspan="6">Загрузка…</td></tr></tbody></table></div>' +
      "</div>" +
      '<div class="wp-col wp-col-hu">' +
      "<h3>" +
      esc(UI_LABELS.COL_HU) +
      "</h3>" +
      '<div class="wp-table-wrap"><table class="pc-table wp-table"><thead><tr>' +
      "<th>HU</th><th>Товар</th><th>Кол-во</th><th>Место</th><th>Резерв</th>" +
      '</tr></thead><tbody id="wpHuBody"><tr><td colspan="5">Загрузка…</td></tr></tbody></table></div>' +
      "</div>" +
      '<div class="wp-col wp-col-package"><div id="wpPackagePanel"></div></div>' +
      "</div>" +
      '<p class="wp-safety-hint">' +
      esc(UI_LABELS.SAFETY_HINT) +
      "</p>" +
      "</section></div>"
    );
  }

  function wireWarehousePlanner() {
    document.getElementById("wpRefreshBtn").addEventListener("click", function () {
      refreshBoard();
      if (draftBundleId) {
        syncDraftFromServer().then(refreshUi);
      }
    });
    document.getElementById("wpNewBundleBtn").addEventListener("click", function () {
      clearPackage().then(ensureDraftBundle).then(function () {
        setNotice("Создан новый пакет.", "ok");
        refreshUi();
      });
    });
    document.getElementById("wpPreviewBtn").addEventListener("click", function () {
      runPreview().catch(function (e) {
        setNotice(String(e.message || e), "error");
      });
    });
    document.getElementById("wpSubmitBtn").addEventListener("click", submitPackage);
    document.getElementById("wpClearBtn").addEventListener("click", clearPackage);
    document.getElementById("wpBundlesBtn").addEventListener("click", function () {
      openBundlesModal().catch(function (e) {
        setNotice(String(e.message || e), "error");
      });
    });

    document.getElementById("wpOrderFilters").addEventListener("click", function (event) {
      if (activeScenario === SCENARIO_ADOPT) {
        return;
      }
      var btn = event.target;
      if (!btn || !btn.getAttribute || !btn.getAttribute("data-filter")) {
        return;
      }
      orderFilter = btn.getAttribute("data-filter");
      renderFilterPills();
      renderOrdersTable();
    });

    document.getElementById("wpOrdersBody").addEventListener("click", function (event) {
      var row = event.target.closest("[data-order-row]");
      if (row) {
        onOrderRowClick(Number(row.getAttribute("data-order-row")));
      }
    });

    document.getElementById("wpHuBody").addEventListener("click", function (event) {
      var row = event.target.closest("[data-hu-row]");
      if (row) {
        onHuRowClick(row.getAttribute("data-hu-row"));
      }
    });

    refreshBoard();
  }

  window.FlowStockWarehouseBoard = {
    init: init,
    render: renderWarehousePlanner,
    wire: wireWarehousePlanner,
    statusLabel: statusLabel,
    STATUS_LABELS: STATUS_LABELS,
    UI_LABELS: UI_LABELS,
    formatActionLineLabel: formatActionLineLabel,
    formatPreviewIssue: formatPreviewIssue,
  };
})();
