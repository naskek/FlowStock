(function (root) {
  "use strict";

  var REPORT_SCHEMA = "flowstock-scanner-diagnostic-report/v1";
  var HISTORY_LIMIT = 20;
  var MAX_EVENTS_PER_STEP = 400;
  var DIAGNOSTIC_RAW_EVENTS = {
    "raw-keydown": true,
    "raw-keyup": true,
    "raw-beforeinput": true,
    "raw-input": true,
    "raw-paste": true,
    "raw-compositionstart": true,
    "raw-compositionupdate": true,
    "raw-compositionend": true,
    focus: true,
    blur: true,
    visibilitychange: true,
  };

  var singletonController = null;

  function escapeHtml(value) {
    return String(value == null ? "" : value)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function clone(value) {
    if (value == null) {
      return value;
    }
    try {
      return JSON.parse(JSON.stringify(value));
    } catch (error) {
      return value;
    }
  }

  function normalizeManifest(manifest) {
    var source = manifest || root.FlowStockScannerDiagnosticManifest || {};
    var steps = (source.steps || []).map(function (step, index) {
      var expectedValue = String(step.expectedValue || "");
      return {
        id: String(step.id || "step-" + (index + 1)),
        number: Number(step.number) || index + 1,
        title: String(step.title || "Шаг " + (index + 1)),
        expectedValue: expectedValue,
        expectedLength: expectedValue.length,
        expectedSymbologies: (step.expectedSymbologies || []).slice(),
      };
    });
    return {
      schemaVersion: Number(source.schemaVersion) || 1,
      setId: String(source.setId || ""),
      printTitle: String(source.printTitle || ""),
      printerArtifact: String(source.printerArtifact || ""),
      label: clone(source.label || {}),
      steps: steps,
    };
  }

  function createSession(manifest) {
    var normalized = normalizeManifest(manifest);
    var startedAt = new Date().toISOString();
    return {
      schema: REPORT_SCHEMA,
      setId: normalized.setId,
      sessionId: createSessionId(),
      startedAt: startedAt,
      finishedAt: "",
      currentStepIndex: 0,
      aborted: false,
      manifest: normalized,
      steps: normalized.steps.map(function (step) {
        return createStepState(step);
      }),
      messages: [],
    };
  }

  function createStepState(step) {
    return {
      stepId: step.id,
      number: step.number,
      title: step.title,
      expectedValue: step.expectedValue,
      expectedLength: step.expectedLength,
      actualValue: "",
      rawValue: "",
      normalizedValue: "",
      actualLength: 0,
      status: "NOT_COMPLETED",
      warnings: [],
      errors: [],
      attempts: [],
      symbology: "",
      source: "",
      terminator: "",
      flushReason: "",
      inputDurationMs: null,
      dispatchCount: 0,
      duplicates: 0,
      rawEvents: [],
      pipelineEvents: [],
      managerReceivedAt: "",
      handlerReceivedAt: "",
      completedAt: "",
      match: false,
    };
  }

  function createSessionId() {
    if (root.crypto && typeof root.crypto.randomUUID === "function") {
      return root.crypto.randomUUID();
    }
    return "scanner-diag-" + Date.now() + "-" + Math.random().toString(16).slice(2);
  }

  function parseTime(value) {
    return Date.parse(value || "") || 0;
  }

  function formatDateTime(value) {
    var date = value ? new Date(value) : null;
    if (!date || isNaN(date.getTime())) {
      return "-";
    }
    return date.toLocaleString("ru-RU");
  }

  function formatFileTimestamp(value) {
    var date = value ? new Date(value) : new Date();
    if (!date || isNaN(date.getTime())) {
      date = new Date();
    }
    function pad(num) {
      var text = String(num);
      return text.length < 2 ? "0" + text : text;
    }
    return (
      String(date.getFullYear()) +
      pad(date.getMonth() + 1) +
      pad(date.getDate()) +
      "-" +
      pad(date.getHours()) +
      pad(date.getMinutes()) +
      pad(date.getSeconds())
    );
  }

  function getReportFileName(report) {
    return (
      "flowstock-scanner-diag_" +
      String((report && report.setId) || "UNKNOWN").replace(/[^A-Za-z0-9_-]+/g, "-") +
      "_" +
      formatFileTimestamp(report && (report.finishedAt || report.startedAt)) +
      ".json"
    );
  }

  function firstMismatch(expected, actual) {
    var a = String(expected || "");
    var b = String(actual || "");
    var max = Math.max(a.length, b.length);
    for (var i = 0; i < max; i += 1) {
      if (a.charAt(i) !== b.charAt(i)) {
        return i + 1;
      }
    }
    return 0;
  }

  function normalizeScanValue(scan) {
    if (scan && typeof scan === "object" && scan.value != null) {
      return String(scan.value || "").trim();
    }
    return String(scan == null ? "" : scan).trim();
  }

  function getRawScanValue(scan) {
    if (scan && typeof scan === "object" && scan.value != null) {
      return String(scan.value || "");
    }
    return String(scan == null ? "" : scan);
  }

  function findManifestStepByValue(manifest, value) {
    var normalized = String(value || "");
    var steps = (manifest && manifest.steps) || [];
    for (var i = 0; i < steps.length; i += 1) {
      if (steps[i].expectedValue === normalized) {
        return steps[i];
      }
    }
    return null;
  }

  function classifyScan(session, scan) {
    var manifest = session.manifest;
    var current = manifest.steps[session.currentStepIndex];
    var rawValue = getRawScanValue(scan);
    var normalizedValue = normalizeScanValue(scan);
    var otherStep = findManifestStepByValue(manifest, normalizedValue);

    if (!current) {
      return {
        kind: "ignored",
        rawValue: rawValue,
        normalizedValue: normalizedValue,
        message: "Диагностика уже завершена.",
      };
    }

    if (normalizedValue === current.expectedValue) {
      return {
        kind: "match",
        step: current,
        rawValue: rawValue,
        normalizedValue: normalizedValue,
        message: "Шаг выполнен.",
      };
    }

    if (otherStep && otherStep.id !== current.id) {
      return {
        kind: "wrong-step",
        step: current,
        otherStep: otherStep,
        rawValue: rawValue,
        normalizedValue: normalizedValue,
        message:
          "Получен код шага " +
          otherStep.number +
          " — " +
          otherStep.title +
          ". Сейчас требуется шаг " +
          current.number +
          " — " +
          current.title +
          ".",
      };
    }

    var mismatch = firstMismatch(current.expectedValue, normalizedValue);
    var sameSetPrefix =
      normalizedValue.indexOf("FLOWSTOCK|SCANNER-DIAG|V1|") === 0 ||
      current.expectedValue.indexOf(normalizedValue) === 0 ||
      normalizedValue.indexOf(current.expectedValue) === 0;
    if (sameSetPrefix || Math.abs(current.expectedValue.length - normalizedValue.length) <= 4) {
      return {
        kind: "damaged",
        step: current,
        rawValue: rawValue,
        normalizedValue: normalizedValue,
        expectedLength: current.expectedLength,
        actualLength: normalizedValue.length,
        firstMismatch: mismatch,
        message: "Значение получено, но не совпадает с ожидаемым.",
      };
    }

    return {
      kind: "unknown",
      step: current,
      rawValue: rawValue,
      normalizedValue: normalizedValue,
      message:
        "Полученное значение не относится к комплекту " +
        manifest.setId +
        ". Повторите сканирование нужной этикетки.",
    };
  }

  function pushLimited(list, value) {
    list.push(value);
    if (list.length > MAX_EVENTS_PER_STEP) {
      list.splice(0, list.length - MAX_EVENTS_PER_STEP);
    }
  }

  function uniquePush(list, value) {
    if (!value) {
      return;
    }
    if (list.indexOf(value) === -1) {
      list.push(value);
    }
  }

  function getLatestPipelineEvent(step, type) {
    for (var i = step.pipelineEvents.length - 1; i >= 0; i -= 1) {
      if (step.pipelineEvents[i].type === type) {
        return step.pipelineEvents[i];
      }
    }
    return null;
  }

  function getStepTone(status) {
    if (status === "PASS") {
      return "success";
    }
    if (status === "WARNING" || status === "NOT_COMPLETED") {
      return "warning";
    }
    return "error";
  }

  function getOverallStatus(steps) {
    var hasNotCompleted = false;
    var hasWarning = false;
    for (var i = 0; i < steps.length; i += 1) {
      if (steps[i].status === "FAIL") {
        return "FAIL";
      }
      if (steps[i].status === "NOT_COMPLETED") {
        hasNotCompleted = true;
      }
      if (steps[i].status === "WARNING") {
        hasWarning = true;
      }
    }
    if (hasNotCompleted) {
      return "NOT_COMPLETED";
    }
    if (hasWarning) {
      return "WARNING";
    }
    return "PASS";
  }

  function summarizeSteps(steps) {
    var durations = [];
    var terminators = {};
    var transports = {};
    var inputStyles = {};
    var duplicateDispatchCount = 0;
    var lostDispatchCount = 0;
    var passed = 0;
    var warning = 0;
    var failed = 0;

    (steps || []).forEach(function (step) {
      if (step.status === "PASS") {
        passed += 1;
      }
      if (step.status === "WARNING") {
        warning += 1;
      }
      if (step.status === "FAIL") {
        failed += 1;
      }
      if (typeof step.inputDurationMs === "number") {
        durations.push(step.inputDurationMs);
      }
      if (step.terminator) {
        terminators[step.terminator] = (terminators[step.terminator] || 0) + 1;
      }
      if (step.source) {
        transports[step.source] = (transports[step.source] || 0) + 1;
      }
      if (step.flushReason) {
        inputStyles[step.flushReason] = (inputStyles[step.flushReason] || 0) + 1;
      }
      if (step.dispatchCount > 1) {
        duplicateDispatchCount += step.dispatchCount - 1;
      }
      if (step.pipelineEvents.some(function (event) { return event.type === "handler-missing"; })) {
        lostDispatchCount += 1;
      }
    });

    return {
      overallStatus: getOverallStatus(steps || []),
      passedSteps: passed,
      warningSteps: warning,
      failedSteps: failed,
      duplicateDispatchCount: duplicateDispatchCount,
      lostDispatchCount: lostDispatchCount,
      averageInputDurationMs: durations.length
        ? Math.round(durations.reduce(function (sum, value) { return sum + value; }, 0) / durations.length)
        : null,
      dominantTransport: getDominantValue(transports) || "unknown",
      dominantInputStyle: getDominantValue(inputStyles) || "unknown",
      dominantTerminator: getDominantValue(terminators) || "Unknown",
    };
  }

  function getDominantValue(map) {
    var bestKey = "";
    var bestCount = 0;
    Object.keys(map || {}).forEach(function (key) {
      if (map[key] > bestCount) {
        bestKey = key;
        bestCount = map[key];
      }
    });
    return bestKey;
  }

  function createRecommendation(summary, scanner) {
    return {
      transport: summary.dominantTransport || scanner.selectedProvider || "keyboard",
      inputStyle: summary.dominantInputStyle || "unknown",
      terminator: summary.dominantTerminator || "Unknown",
      imeMode: summary.dominantInputStyle === "ime",
      recommendedInputDelayMs: 80,
      recommendedKeyDelayMs: 100,
      appliedAutomatically: false,
    };
  }

  function createController(initialDeps) {
    var deps = initialDeps || {};
    var app = deps.app || null;
    var session = null;
    var routeMessage = "";
    var routeMessageType = "info";
    var lastEventAt = 0;
    var observerAttached = false;
    var renderToken = 0;
    var pendingAttemptEvents = {};
    var attemptStepIndex = {};
    var scanStepIndex = {};
    var handlerDispatchCountByAttempt = {};
    var postScanPromiseByAttempt = Object.create(null);
    var processedAttemptIds = Object.create(null);
    var draftSaveChain = Promise.resolve();
    var coalescedDraftSavePending = false;
    var finalizationPending = false;
    var finishing = false;

    function updateDeps(nextDeps) {
      deps = nextDeps || deps || {};
      app = deps.app || app || (root.document ? root.document.getElementById("app") : null);
    }

    function getDiagnosticStore() {
      return deps.diagnosticStore || root.FlowStockScannerDiagnosticsStore || {};
    }

    function getManifest() {
      return normalizeManifest(deps.manifest || root.FlowStockScannerDiagnosticManifest);
    }

    function getScannerManager() {
      return deps.scannerManager || null;
    }

    function getCurrentStep() {
      if (!session) {
        return null;
      }
      return session.steps[session.currentStepIndex] || null;
    }

    function finishRender() {
      if (typeof deps.finishRouteRender === "function") {
        deps.finishRouteRender();
      }
    }

    function navigate(path) {
      if (typeof deps.navigate === "function") {
        deps.navigate(path);
      } else if (root.location) {
        root.location.hash = path;
      }
    }

    function nextRenderToken() {
      renderToken += 1;
      return renderToken;
    }

    function isRenderTokenActive(token) {
      return token === renderToken;
    }

    function setHtml(html, token, beforeFinish) {
      if (token != null && !isRenderTokenActive(token)) {
        return;
      }
      if (app) {
        app.innerHTML = html;
      }
      if (typeof beforeFinish === "function") {
        beforeFinish();
      }
      finishRender();
    }

    function resetAttemptCorrelation() {
      pendingAttemptEvents = {};
      attemptStepIndex = {};
      scanStepIndex = {};
      handlerDispatchCountByAttempt = {};
      finalizationPending = false;
      finishing = false;
    }

    function resetAttemptProcessing() {
      postScanPromiseByAttempt = Object.create(null);
      processedAttemptIds = Object.create(null);
      coalescedDraftSavePending = false;
    }

    function clearCompletedSessionState() {
      session = null;
      finalizationPending = false;
      finishing = false;
      resetAttemptProcessing();
    }

    function getScannerSnapshot() {
      var manager = getScannerManager();
      var settings =
        manager && typeof manager.getSettings === "function"
          ? manager.getSettings()
          : {
              mode: deps.scannerMode || "auto",
              providerType:
                manager && typeof manager.getProviderType === "function"
                  ? manager.getProviderType()
                  : "keyboard",
              dedupeMs: 120,
              inputDelayMs: 60,
              keyDelayMs: 80,
              androidBridgeAvailable: !!(
                root.FlowStockAndroidBridge &&
                typeof root.FlowStockAndroidBridge.subscribeScans === "function"
              ),
            };
      return settings;
    }

    function getEventAttemptId(entry) {
      return entry && entry.detail && entry.detail.attemptId
        ? String(entry.detail.attemptId)
        : "";
    }

    function getEventScanId(entry) {
      return entry && entry.detail && entry.detail.scanId ? String(entry.detail.scanId) : "";
    }

    function getEventValue(entry) {
      if (!entry || !entry.detail) {
        return "";
      }
      return String(
        entry.detail.normalizedValue ||
          entry.detail.value ||
          entry.detail.rawValue ||
          ""
      ).trim();
    }

    function findCompletedStepIndexByValue(value) {
      var normalized = String(value || "");
      if (!session || !normalized) {
        return -1;
      }
      for (var i = 0; i < session.steps.length; i += 1) {
        var step = session.steps[i];
        if (
          step.expectedValue === normalized &&
          (step.match || step.status === "PASS" || step.status === "WARNING" || step.status === "FAIL")
        ) {
          return i;
        }
      }
      return -1;
    }

    function getStepIndexForEntry(entry) {
      if (!session) {
        return -1;
      }
      var attemptId = getEventAttemptId(entry);
      var scanId = getEventScanId(entry);
      if (attemptId && attemptStepIndex[attemptId] != null) {
        return attemptStepIndex[attemptId];
      }
      if (scanId && scanStepIndex[scanId] != null) {
        return scanStepIndex[scanId];
      }
      var valueStepIndex = findCompletedStepIndexByValue(getEventValue(entry));
      if (valueStepIndex >= 0) {
        return valueStepIndex;
      }
      if (getEventValue(entry)) {
        return session.currentStepIndex;
      }
      return -1;
    }

    function rememberAttemptStep(entry, stepIndex) {
      var attemptId = getEventAttemptId(entry);
      var scanId = getEventScanId(entry);
      if (stepIndex < 0) {
        return;
      }
      if (attemptId) {
        attemptStepIndex[attemptId] = stepIndex;
      }
      if (scanId) {
        scanStepIndex[scanId] = stepIndex;
      }
    }

    function appendObserverEntryToStep(step, entry) {
      if (DIAGNOSTIC_RAW_EVENTS[entry.type]) {
        pushLimited(step.rawEvents, entry);
      } else {
        pushLimited(step.pipelineEvents, entry);
      }

      if (entry.type === "manager-received") {
        step.managerReceivedAt = entry.timestamp;
      }
      if (entry.type === "handler-dispatched") {
        step.handlerReceivedAt = entry.timestamp;
        var attemptId = getEventAttemptId(entry) || entry.timestamp;
        handlerDispatchCountByAttempt[attemptId] = (handlerDispatchCountByAttempt[attemptId] || 0) + 1;
        step.dispatchCount = Math.max(step.dispatchCount || 0, handlerDispatchCountByAttempt[attemptId]);
        if (handlerDispatchCountByAttempt[attemptId] > 1) {
          uniquePush(step.errors, "scan принят приложением дважды");
          step.status = "FAIL";
          scheduleCoalescedDraftSave();
        }
      }
      if (entry.type === "dedupe-rejected") {
        step.duplicates += 1;
        uniquePush(step.warnings, "scanner отправил дубликат, подавленный dedupe");
        if (step.status === "PASS") {
          step.status = "WARNING";
        }
      }
      if (entry.type === "handler-missing") {
        uniquePush(step.errors, "ScannerManager сформировал scan, но route handler его не получил");
        step.status = "FAIL";
      }
      if (entry.type === "scanner-error") {
        uniquePush(step.errors, "Произошла ошибка scanner pipeline");
        step.status = "FAIL";
      }
    }

    function flushPendingAttemptEvents(attemptId, stepIndex) {
      if (!attemptId || stepIndex < 0 || !pendingAttemptEvents[attemptId]) {
        return;
      }
      var step = session && session.steps[stepIndex];
      if (!step) {
        return;
      }
      pendingAttemptEvents[attemptId].forEach(function (pendingEntry) {
        rememberAttemptStep(pendingEntry, stepIndex);
        appendObserverEntryToStep(step, pendingEntry);
      });
      delete pendingAttemptEvents[attemptId];
    }

    function recordObserverEvent(event) {
      if (!session) {
        return;
      }
      var timestamp = event && event.timestamp ? event.timestamp : Date.now();
      var entry = {
        type: event && event.type ? event.type : "unknown",
        timestamp: new Date(timestamp).toISOString(),
        deltaMs: lastEventAt ? timestamp - lastEventAt : 0,
        detail: clone((event && event.detail) || {}),
      };
      lastEventAt = timestamp;
      var attemptId = getEventAttemptId(entry);
      var stepIndex = getStepIndexForEntry(entry);
      if (stepIndex < 0 && attemptId) {
        pendingAttemptEvents[attemptId] = pendingAttemptEvents[attemptId] || [];
        pendingAttemptEvents[attemptId].push(entry);
        return;
      }
      if (stepIndex < 0) {
        stepIndex = session.currentStepIndex;
      }
      rememberAttemptStep(entry, stepIndex);
      flushPendingAttemptEvents(attemptId, stepIndex);
      appendObserverEntryToStep(session.steps[stepIndex], entry);
    }

    function attachDiagnosticPipeline() {
      var manager = getScannerManager();
      if (manager && typeof manager.setDiagnosticObserver === "function") {
        manager.setDiagnosticObserver(recordObserverEvent);
        observerAttached = true;
      }
      if (typeof deps.setScanHandler === "function") {
        deps.setScanHandler(handleScan);
      }
    }

    function detachDiagnosticPipeline() {
      var manager = getScannerManager();
      if (observerAttached && manager && typeof manager.clearDiagnosticObserver === "function") {
        manager.clearDiagnosticObserver();
      }
      observerAttached = false;
      if (typeof deps.setScanHandler === "function") {
        deps.setScanHandler(null);
      }
      if (typeof deps.setPreferredScanTarget === "function") {
        deps.setPreferredScanTarget(null);
      }
    }

    function cleanupRoute() {
      nextRenderToken();
      detachDiagnosticPipeline();
    }

    function saveDraft() {
      if (!session) {
        return Promise.resolve(null);
      }
      var sessionId = session.sessionId;
      var storage = getDiagnosticStore();
      if (typeof storage.saveActiveSession !== "function") {
        return Promise.resolve(session);
      }
      return enqueueDraftSave(sessionId);
    }

    function enqueueDraftSave(sessionId) {
      var storage = getDiagnosticStore();
      if (typeof storage.saveActiveSession !== "function") {
        return Promise.resolve(session);
      }
      var saveOperation = function () {
        if (!session || session.sessionId !== sessionId) {
          return null;
        }
        return storage.saveActiveSession(session).catch(function () {
          return session;
        });
      };
      draftSaveChain = draftSaveChain.then(saveOperation, saveOperation);
      return draftSaveChain;
    }

    function scheduleCoalescedDraftSave() {
      if (!session || coalescedDraftSavePending) {
        return draftSaveChain;
      }
      var sessionId = session.sessionId;
      coalescedDraftSavePending = true;
      var savePromise = enqueueDraftSave(sessionId);
      savePromise.then(
        function () {
          coalescedDraftSavePending = false;
        },
        function () {
          coalescedDraftSavePending = false;
        }
      );
      return savePromise;
    }

    function clearDraft() {
      var storage = getDiagnosticStore();
      if (typeof storage.clearActiveSession !== "function") {
        return Promise.resolve(false);
      }
      return storage.clearActiveSession().catch(function () {
        return false;
      });
    }

    function loadDraft() {
      var storage = getDiagnosticStore();
      if (typeof storage.loadActiveSession !== "function") {
        return Promise.resolve(null);
      }
      return storage.loadActiveSession();
    }

    function isValidDraft(draft) {
      var manifest = getManifest();
      if (
        !draft ||
        draft.schema !== REPORT_SCHEMA ||
        draft.setId !== manifest.setId ||
        !draft.sessionId ||
        !draft.startedAt ||
        !draft.manifest ||
        draft.manifest.setId !== manifest.setId ||
        !Array.isArray(draft.manifest.steps) ||
        !Array.isArray(draft.steps) ||
        draft.steps.length !== manifest.steps.length ||
        draft.manifest.steps.length !== manifest.steps.length ||
        Math.floor(draft.currentStepIndex) !== draft.currentStepIndex ||
        draft.currentStepIndex < 0 ||
        draft.currentStepIndex >= manifest.steps.length
      ) {
        return false;
      }
      var allowedStatuses = {
        PASS: true,
        WARNING: true,
        FAIL: true,
        NOT_COMPLETED: true,
      };
      for (var i = 0; i < manifest.steps.length; i += 1) {
        var manifestStep = manifest.steps[i];
        var draftManifestStep = draft.manifest.steps[i] || {};
        var step = draft.steps[i] || {};
        if (
          draftManifestStep.id !== manifestStep.id ||
          draftManifestStep.expectedValue !== manifestStep.expectedValue ||
          step.stepId !== manifestStep.id ||
          step.expectedValue !== manifestStep.expectedValue ||
          !allowedStatuses[step.status] ||
          !Array.isArray(step.attempts) ||
          !Array.isArray(step.rawEvents) ||
          !Array.isArray(step.pipelineEvents) ||
          !Array.isArray(step.warnings) ||
          !Array.isArray(step.errors)
        ) {
          return false;
        }
      }
      return true;
    }

    function startSession() {
      var token = renderToken;
      session = createSession(getManifest());
      resetAttemptCorrelation();
      resetAttemptProcessing();
      routeMessage = "";
      routeMessageType = "info";
      lastEventAt = parseTime(session.startedAt);
      attachDiagnosticPipeline();
      return saveDraft().then(function () {
        if (!isRenderTokenActive(token)) {
          return session;
        }
        renderStep();
        return session;
      });
    }

    function restoreDraft(draft) {
      var previousSessionId = session && session.sessionId;
      session = draft;
      resetAttemptCorrelation();
      if (previousSessionId !== session.sessionId) {
        resetAttemptProcessing();
      }
      routeMessage = "Незавершённая диагностика восстановлена.";
      routeMessageType = "warning";
      lastEventAt = parseTime(session.startedAt);
      attachDiagnosticPipeline();
      renderStep();
      return session;
    }

    function retryStep() {
      if (!session) {
        return Promise.resolve(null);
      }
      var token = renderToken;
      var manifestStep = session.manifest.steps[session.currentStepIndex];
      if (!manifestStep) {
        return Promise.resolve(session);
      }
      session.steps[session.currentStepIndex] = createStepState(manifestStep);
      routeMessage = "Повторите сканирование текущей этикетки.";
      routeMessageType = "warning";
      return saveDraft().then(function () {
        if (!isRenderTokenActive(token)) {
          return session;
        }
        renderStep();
        return session;
      });
    }

    function applyScanMatch(step, scan, outcome) {
      var currentStepIndex = session ? session.currentStepIndex : -1;
      if (scan && currentStepIndex >= 0) {
        if (scan.attemptId) {
          attemptStepIndex[String(scan.attemptId)] = currentStepIndex;
          flushPendingAttemptEvents(String(scan.attemptId), currentStepIndex);
        }
        if (scan.scanId) {
          scanStepIndex[String(scan.scanId)] = currentStepIndex;
        }
      }
      var scanEvent = getLatestPipelineEvent(step, "scan-emitted");
      var detail = (scanEvent && scanEvent.detail) || {};
      var rawValue = outcome.rawValue;
      var normalizedValue = outcome.normalizedValue;

      step.rawValue = rawValue;
      step.actualValue = normalizedValue;
      step.normalizedValue = normalizedValue;
      step.actualLength = normalizedValue.length;
      step.source = (scan && scan.source) || detail.source || getScannerSnapshot().providerType || "keyboard";
      step.symbology = (scan && scan.symbology) || detail.symbology || "";
      step.terminator = detail.terminator || (scan && scan.raw && scan.raw.terminator) || "";
      step.flushReason =
        detail.flushReason || (scan && scan.raw && (scan.raw.flushReason || scan.raw.reason)) || "";
      step.inputDurationMs =
        typeof detail.inputDurationMs === "number" ? detail.inputDurationMs : step.inputDurationMs;
      step.dispatchCount = Math.max(step.dispatchCount || 0, 1);
      step.handlerReceivedAt = new Date().toISOString();
      step.match = normalizedValue === step.expectedValue;

      if (rawValue !== normalizedValue) {
        uniquePush(step.warnings, "присутствует prefix или suffix, который приложение нормализовало");
      }
      if (step.duplicates > 0) {
        uniquePush(step.warnings, "scanner отправил дубликат, подавленный dedupe");
      }
      if (step.dispatchCount > 1) {
        uniquePush(step.errors, "scan принят приложением дважды");
      }
      if (step.source !== "keyboard" && !step.symbology) {
        uniquePush(step.warnings, "provider не сообщил symbology");
      }
      if (step.flushReason === "input-timeout" || step.flushReason === "keydown-timeout") {
        uniquePush(step.warnings, "завершение по timeout");
      }
      if (step.flushReason === "ime") {
        uniquePush(step.warnings, "ввод шёл через Android IME keyCode=229");
      }
      if (step.flushReason === "intent" && step.source === "intent" && !step.terminator) {
        step.terminator = "None";
      }
      if (!step.match) {
        uniquePush(step.errors, "значение искажено");
      }

      step.status = step.errors.length ? "FAIL" : step.warnings.length ? "WARNING" : "PASS";
      step.completedAt = new Date().toISOString();
    }

    function recordRejectedAttempt(step, outcome) {
      var attempt = {
        kind: outcome.kind,
        rawValue: outcome.rawValue,
        normalizedValue: outcome.normalizedValue,
        actualLength: outcome.normalizedValue.length,
        expectedLength: step.expectedLength,
        firstMismatch: outcome.firstMismatch || firstMismatch(step.expectedValue, outcome.normalizedValue),
        message: outcome.message,
        at: new Date().toISOString(),
      };
      step.attempts.push(attempt);
    }

    function getKnownStepIndexForScan(scan) {
      if (!scan || !session) {
        return -1;
      }
      var attemptId = scan.attemptId ? String(scan.attemptId) : "";
      var scanId = scan.scanId ? String(scan.scanId) : "";
      if (attemptId && attemptStepIndex[attemptId] != null) {
        return attemptStepIndex[attemptId];
      }
      if (scanId && scanStepIndex[scanId] != null) {
        return scanStepIndex[scanId];
      }
      return -1;
    }

    function handleScan(scan) {
      if (!session) {
        return Promise.resolve(null);
      }
      if (finalizationPending || finishing) {
        return Promise.resolve(session);
      }
      var attemptId = scan && scan.attemptId ? String(scan.attemptId) : "";
      if (attemptId && postScanPromiseByAttempt[attemptId]) {
        return postScanPromiseByAttempt[attemptId];
      }
      if (attemptId && processedAttemptIds[attemptId]) {
        return scheduleCoalescedDraftSave().then(function () {
          return session;
        });
      }
      if (attemptId) {
        processedAttemptIds[attemptId] = true;
      }
      var promise = processScan(scan);
      if (attemptId) {
        postScanPromiseByAttempt[attemptId] = promise;
        promise.then(
          function () {
            delete postScanPromiseByAttempt[attemptId];
          },
          function () {
            delete postScanPromiseByAttempt[attemptId];
          }
        );
      }
      return promise;
    }

    function processScan(scan) {
      var token = renderToken;
      var step = getCurrentStep();
      if (!step) {
        return Promise.resolve(session);
      }
      var knownStepIndex = getKnownStepIndexForScan(scan);
      if (knownStepIndex >= 0 && knownStepIndex !== session.currentStepIndex) {
        return Promise.resolve().then(function () {
          if (!isRenderTokenActive(token)) {
            return session;
          }
          return saveDraft();
        }).then(function () {
          return session;
        });
      }
      var outcome = classifyScan(session, scan);

      if (outcome.kind === "match") {
        var completedStepIndex = session.currentStepIndex;
        applyScanMatch(step, scan || {}, outcome);
        if (completedStepIndex < session.steps.length - 1) {
          return Promise.resolve().then(function () {
            if (!isRenderTokenActive(token)) {
              return session;
            }
            if (!session || session.currentStepIndex !== completedStepIndex) {
              return session;
            }
            session.currentStepIndex = completedStepIndex + 1;
            routeMessage = "Шаг " + step.number + " выполнен. Перейдите к следующей этикетке.";
            routeMessageType = getStepTone(step.status);
            return saveDraft();
          }).then(function () {
            if (isRenderTokenActive(token)) {
              renderStep();
            }
            return session;
          });
        }
        finalizationPending = true;
        return Promise.resolve().then(function () {
          if (!isRenderTokenActive(token)) {
            finalizationPending = false;
            return session;
          }
          return finishSession(false, token);
        });
      }

      recordRejectedAttempt(step, outcome);
      routeMessage = outcome.message;
      routeMessageType = outcome.kind === "wrong-step" ? "warning" : "error";
      return Promise.resolve().then(function () {
        if (!isRenderTokenActive(token)) {
          return session;
        }
        return saveDraft();
      }).then(function () {
        if (isRenderTokenActive(token)) {
          renderStep();
        }
        return session;
      });
    }

    function buildReport(aborted) {
      if (!session) {
        return null;
      }
      var finishedAt = new Date().toISOString();
      session.finishedAt = finishedAt;
      session.aborted = !!aborted;
      if (aborted) {
        session.steps.forEach(function (step) {
          if (step.status !== "PASS" && step.status !== "WARNING" && step.status !== "FAIL") {
            step.status = "NOT_COMPLETED";
          }
        });
      }

      var scannerSettings = getScannerSnapshot();
      var scanner = {
        configuredMode: scannerSettings.mode || "auto",
        selectedProvider: scannerSettings.providerType || "keyboard",
        androidBridgeAvailable: !!scannerSettings.androidBridgeAvailable,
        dedupeWindowMs: scannerSettings.dedupeMs,
        inputDelayMs: scannerSettings.inputDelayMs,
        keyDelayMs: scannerSettings.keyDelayMs,
      };
      var steps = clone(session.steps);
      var summary = summarizeSteps(steps);
      if (aborted) {
        summary.overallStatus = "NOT_COMPLETED";
      }
      return {
        schema: REPORT_SCHEMA,
        setId: session.setId,
        sessionId: session.sessionId,
        startedAt: session.startedAt,
        finishedAt: finishedAt,
        app: collectAppInfo(deps),
        device: collectDeviceInfo(),
        scanner: scanner,
        steps: steps,
        summary: summary,
        recommendation: createRecommendation(summary, scanner),
      };
    }

    function saveReport(report) {
      var storage = getDiagnosticStore();
      if (!storage || typeof storage.saveReport !== "function") {
        return Promise.resolve(report);
      }
      return storage.saveReport(report);
    }

    function finishSession(aborted, existingToken) {
      if (finishing) {
        return Promise.resolve(null);
      }
      finishing = true;
      var token = existingToken != null ? existingToken : renderToken;
      var report = null;
      return Promise.resolve()
        .then(function () {
          if (!isRenderTokenActive(token)) {
            finishing = false;
            finalizationPending = false;
            return null;
          }
          report = buildReport(aborted);
          detachDiagnosticPipeline();
          return draftSaveChain.catch(function () {
            return null;
          })
            .then(function () {
              return clearDraft();
            })
            .then(function () {
              return saveReport(report);
            })
            .then(function () {
              if (isRenderTokenActive(token)) {
                renderReport(report, "", token);
              }
              return report;
            })
            .catch(function (error) {
              report.saveError = error && error.message ? error.message : String(error || "Ошибка сохранения");
              if (isRenderTokenActive(token)) {
                renderReport(report, "Отчёт сформирован, но не сохранён в истории.", token);
              }
              return report;
            });
        })
        .then(
          function (result) {
            if (report) {
              clearCompletedSessionState();
            }
            return result;
          },
          function (error) {
            if (report) {
              clearCompletedSessionState();
            } else {
              finishing = false;
              finalizationPending = false;
            }
            throw error;
          }
        );
    }

    function abortSession() {
      return finishSession(true);
    }

    function render(route) {
      if (route && route.name === "scannerDiagnosticsHistory") {
        cleanupRoute();
        renderHistory();
        return;
      }
      if (route && route.name === "scannerDiagnosticsReport") {
        cleanupRoute();
        renderStoredReport(route.id);
        return;
      }
      if (session) {
        attachDiagnosticPipeline();
        renderStep();
        return;
      }
      var token = nextRenderToken();
      setHtml(renderLoading(), token);
      loadDraft()
        .then(function (draft) {
          if (!isRenderTokenActive(token)) {
            return null;
          }
          if (draft == null) {
            renderStart("");
            return null;
          }
          if (isValidDraft(draft)) {
            return restoreDraft(draft);
          }
          return clearDraft().then(function () {
            if (isRenderTokenActive(token)) {
              cleanupRoute();
              renderStart("Незавершённая диагностика повреждена и была отменена.");
            }
            return null;
          });
        })
        .catch(function () {
          if (isRenderTokenActive(token)) {
            renderStart("Не удалось восстановить незавершённую диагностику.");
          }
        });
    }

    function renderStart(message) {
      var manifest = getManifest();
      var scanner = getScannerSnapshot();
      var device =
        root.navigator && root.navigator.userAgent
          ? root.navigator.userAgent
          : "неизвестно";
      var html =
        '<section class="screen scanner-diag-screen">' +
        '  <div class="screen-card scanner-diag-card">' +
        '    <h1 class="screen-title">Диагностика сканера</h1>' +
        (message ? renderMessage(message, "warning") : "") +
        '    <div class="scanner-diag-meta">' +
        '      <div><span>Комплект:</span> <strong>' +
        escapeHtml(manifest.setId) +
        "</strong></div>" +
        '      <div><span>Штрихкодов:</span> ' +
        escapeHtml(String(manifest.steps.length)) +
        "</div>" +
        '      <div><span>Версия TSD:</span> ' +
        escapeHtml(String(root.TSD_PWA_VERSION || "неизвестно")) +
        "</div>" +
        '      <div><span>Режим сканера:</span> ' +
        escapeHtml(scanner.mode || "auto") +
        "</div>" +
        '      <div><span>Фактический provider:</span> ' +
        escapeHtml(scanner.providerType || "keyboard") +
        "</div>" +
        '      <div><span>Android Bridge:</span> ' +
        (scanner.androidBridgeAvailable ? "доступен" : "отсутствует") +
        "</div>" +
        '      <div><span>Устройство:</span> ' +
        escapeHtml(device) +
        "</div>" +
        "    </div>" +
        '    <p class="scanner-diag-intro">Понадобятся три диагностические этикетки ' +
        escapeHtml(manifest.setId) +
        ". Сканируйте их в указанном порядке. Диагностические коды не отправляются в складские операции.</p>" +
        '    <div class="scanner-diag-actions">' +
        '      <button class="btn btn-primary" type="button" id="scannerDiagStartBtn">Начать диагностику</button>' +
        '      <button class="btn btn-outline" type="button" id="scannerDiagHistoryBtn">История отчётов</button>' +
        '      <button class="btn btn-outline" type="button" id="scannerDiagBackBtn">Назад</button>' +
        "    </div>" +
        "  </div>" +
        "</section>";
      setHtml(html);
      wireStart();
    }

    function wireStart() {
      bindClick("scannerDiagStartBtn", function () {
        startSession();
      });
      bindClick("scannerDiagHistoryBtn", function () {
        navigate("/scanner-diagnostics/history");
      });
      bindClick("scannerDiagBackBtn", function () {
        navigate("/settings");
      });
    }

    function renderStep() {
      var step = getCurrentStep();
      if (!session || !step) {
        renderStart("");
        return;
      }
      attachDiagnosticPipeline();
      var manifestStep = session.manifest.steps[session.currentStepIndex];
      var technical =
        '<details class="scanner-diag-tech">' +
        '  <summary>Посмотреть технические данные</summary>' +
        '  <div class="scanner-diag-payload">' +
        escapeHtml(manifestStep.expectedValue) +
        "</div>" +
        '  <pre class="scanner-diag-json">' +
        escapeHtml(JSON.stringify(getStepTechnicalSnapshot(step), null, 2)) +
        "</pre>" +
        "</details>";
      var lastAttempt = step.attempts.length ? step.attempts[step.attempts.length - 1] : null;
      var html =
        '<section class="screen scanner-diag-screen">' +
        '  <div class="screen-card scanner-diag-card scanner-diag-step-card">' +
        '    <div class="scanner-diag-step-number">Шаг ' +
        escapeHtml(String(manifestStep.number)) +
        " из " +
        escapeHtml(String(session.manifest.steps.length)) +
        "</div>" +
        '    <h1 class="screen-title">' +
        escapeHtml(manifestStep.title) +
        "</h1>" +
        (routeMessage ? renderMessage(routeMessage, routeMessageType) : "") +
        '    <div class="scanner-diag-expected">' +
        "      <div>Отсканируйте:</div>" +
        "      <strong>" +
        escapeHtml(manifestStep.title) +
        "</strong>" +
        (manifestStep.number === 1
          ? '<div class="scanner-diag-payload-label">Ожидаемое значение:</div><div class="scanner-diag-payload scanner-diag-payload--large">' +
            escapeHtml(manifestStep.expectedValue) +
            "</div>"
          : technical) +
        "    </div>" +
        (lastAttempt ? renderAttempt(lastAttempt) : "") +
        '    <div class="scanner-diag-actions">' +
        '      <input id="scannerDiagScanInput" class="form-input filling-scan-input tsd-scan-input-hidden" type="text" autocomplete="off" autocapitalize="off" autocorrect="off" spellcheck="false" data-scan-allow="1" />' +
        '      <button class="btn btn-outline" type="button" id="scannerDiagRetryBtn">Повторить</button>' +
        '      <button class="btn btn-danger" type="button" id="scannerDiagAbortBtn">Прервать диагностику</button>' +
        '    </div>' +
        "  </div>" +
        "</section>";
      setHtml(html, null, function () {
        var scanInput = root.document ? root.document.getElementById("scannerDiagScanInput") : null;
        if (scanInput && typeof deps.setPreferredScanTarget === "function") {
          deps.setPreferredScanTarget(scanInput);
        }
      });
      bindClick("scannerDiagRetryBtn", function () {
        retryStep();
      });
      bindClick("scannerDiagAbortBtn", function () {
        abortSession();
      });
    }

    function renderHistory() {
      var token = nextRenderToken();
      var storage = getDiagnosticStore();
      setHtml(renderLoading(), token);
      if (!storage || typeof storage.listReports !== "function") {
        setHtml(renderHistoryList([], "История диагностик недоступна на этом устройстве."), token);
        wireHistory();
        return;
      }
      storage
        .listReports()
        .then(function (items) {
          if (!isRenderTokenActive(token)) {
            return;
          }
          setHtml(renderHistoryList((items || []).slice(0, HISTORY_LIMIT)), token);
          wireHistory();
        })
        .catch(function () {
          if (!isRenderTokenActive(token)) {
            return;
          }
          setHtml(renderHistoryList([], "Не удалось загрузить историю диагностик."), token);
          wireHistory();
        });
    }

    function renderStoredReport(id) {
      var token = nextRenderToken();
      var storage = getDiagnosticStore();
      setHtml(renderLoading(), token);
      if (!storage || typeof storage.getReport !== "function") {
        setHtml(renderError("История диагностик недоступна"), token);
        return;
      }
      storage
        .getReport(id)
        .then(function (report) {
          if (!isRenderTokenActive(token)) {
            return;
          }
          if (!report) {
            setHtml(renderError("Отчёт диагностики не найден"), token);
            return;
          }
          renderReport(report, "", token);
        })
        .catch(function () {
          if (isRenderTokenActive(token)) {
            setHtml(renderError("Не удалось открыть отчёт диагностики"), token);
          }
        });
    }

    function renderReport(report, message, token) {
      var summary = report.summary || {};
      var rows = (report.steps || [])
        .map(function (step) {
          return (
            '<div class="scanner-diag-result-row scanner-diag-result-row--' +
            escapeHtml(getStepTone(step.status)) +
            '">' +
            "  <span>" +
            escapeHtml(step.title) +
            "</span>" +
            "  <strong>" +
            escapeHtml(step.status) +
            "</strong>" +
            "</div>"
          );
        })
        .join("");
      var html =
        '<section class="screen scanner-diag-screen">' +
        '  <div class="screen-card scanner-diag-card">' +
        '    <h1 class="screen-title">Диагностика завершена</h1>' +
        (message ? renderMessage(message, "warning") : "") +
        '    <div class="scanner-diag-result scanner-diag-result--' +
        escapeHtml(getStepTone(summary.overallStatus)) +
        '">Общий результат: <strong>' +
        escapeHtml(summary.overallStatus || "UNKNOWN") +
        "</strong></div>" +
        '    <div class="scanner-diag-results">' +
        rows +
        "</div>" +
        '    <div class="scanner-diag-summary">' +
        "      <div>Provider: " +
        escapeHtml((report.scanner && report.scanner.selectedProvider) || "unknown") +
        "</div>" +
        "      <div>Способ ввода: " +
        escapeHtml(summary.dominantInputStyle || "unknown") +
        "</div>" +
        "      <div>Terminator: " +
        escapeHtml(summary.dominantTerminator || "Unknown") +
        "</div>" +
        "      <div>Среднее время скана: " +
        escapeHtml(summary.averageInputDurationMs == null ? "-" : String(summary.averageInputDurationMs) + " мс") +
        "</div>" +
        "      <div>Дубликаты: " +
        escapeHtml(String(summary.duplicateDispatchCount || 0)) +
        "</div>" +
        "      <div>Потерянные dispatch: " +
        escapeHtml(String(summary.lostDispatchCount || 0)) +
        "</div>" +
        "    </div>" +
        renderRecommendation(report.recommendation || {}) +
        '    <details class="scanner-diag-tech">' +
        '      <summary>Raw JSON отчёта</summary>' +
        '      <pre class="scanner-diag-json">' +
        escapeHtml(JSON.stringify(report, null, 2)) +
        "</pre>" +
        "    </details>" +
        '    <div class="scanner-diag-actions">' +
        '      <button class="btn btn-primary" type="button" id="scannerDiagDownloadBtn">Скачать JSON</button>' +
        '      <button class="btn btn-outline" type="button" id="scannerDiagCopyBtn">Копировать отчёт</button>' +
        (root.navigator && typeof root.navigator.share === "function"
          ? '      <button class="btn btn-outline" type="button" id="scannerDiagShareBtn">Поделиться</button>'
          : "") +
        '      <button class="btn btn-outline" type="button" id="scannerDiagHistoryBtn">История отчётов</button>' +
        '      <button class="btn btn-outline" type="button" id="scannerDiagBackBtn">Назад</button>' +
        "    </div>" +
        "  </div>" +
        "</section>";
      setHtml(html, token);
      if (token != null && !isRenderTokenActive(token)) {
        return;
      }
      wireReport(report);
    }

    function wireReport(report) {
      bindClick("scannerDiagDownloadBtn", function () {
        downloadReport(report);
      });
      bindClick("scannerDiagCopyBtn", function () {
        copyReport(report);
      });
      bindClick("scannerDiagShareBtn", function () {
        shareReport(report);
      });
      bindClick("scannerDiagHistoryBtn", function () {
        navigate("/scanner-diagnostics/history");
      });
      bindClick("scannerDiagBackBtn", function () {
        navigate("/settings");
      });
    }

    function wireHistory() {
      bindClick("scannerDiagBackBtn", function () {
        navigate("/scanner-diagnostics");
      });
      var deleteButtons = root.document
        ? root.document.querySelectorAll("[data-scanner-diag-delete]")
        : [];
      Array.prototype.forEach.call(deleteButtons, function (button) {
        button.addEventListener("click", function () {
          var id = button.getAttribute("data-scanner-diag-delete");
          var storage = getDiagnosticStore();
          var token = renderToken;
          if (!storage || typeof storage.deleteReport !== "function") {
            return;
          }
          storage
            .deleteReport(id)
            .then(function () {
              if (isRenderTokenActive(token)) {
                renderHistory();
              }
            })
            .catch(function () {
              if (isRenderTokenActive(token)) {
                renderHistory();
              }
            });
        });
      });
      var downloadButtons = root.document
        ? root.document.querySelectorAll("[data-scanner-diag-download]")
        : [];
      Array.prototype.forEach.call(downloadButtons, function (button) {
        button.addEventListener("click", function () {
          var id = button.getAttribute("data-scanner-diag-download");
          var storage = getDiagnosticStore();
          if (!storage || typeof storage.getReport !== "function") {
            return;
          }
          storage.getReport(id).then(function (report) {
            if (report) {
              downloadReport(report);
            }
          }).catch(function () {});
        });
      });
      var openButtons = root.document
        ? root.document.querySelectorAll("[data-scanner-diag-open]")
        : [];
      Array.prototype.forEach.call(openButtons, function (button) {
        button.addEventListener("click", function () {
          navigate("/scanner-diagnostics/report/" + encodeURIComponent(button.getAttribute("data-scanner-diag-open")));
        });
      });
    }

    function bindClick(id, handler) {
      if (!root.document) {
        return;
      }
      var element = root.document.getElementById(id);
      if (element) {
        element.addEventListener("click", handler);
      }
    }

    return {
      render: render,
      cleanupRoute: cleanupRoute,
      startSession: startSession,
      restoreDraft: restoreDraft,
      retryStep: retryStep,
      abortSession: abortSession,
      handleScan: handleScan,
      classifyScan: function (scan) {
        return session ? classifyScan(session, scan) : null;
      },
      buildReport: buildReport,
      getSession: function () {
        return session;
      },
      setSession: function (nextSession) {
        if (!nextSession || !session || nextSession.sessionId !== session.sessionId) {
          resetAttemptProcessing();
        }
        session = nextSession;
      },
      updateDeps: updateDeps,
    };
  }

  function collectAppInfo(deps) {
    var swState = "unsupported";
    if (root.navigator && root.navigator.serviceWorker) {
      swState = root.navigator.serviceWorker.controller ? "controlled" : "registered";
    }
    return {
      tsdVersion: String(root.TSD_PWA_VERSION || ""),
      buildVersion: "",
      currentRoute: deps && deps.currentRoute ? deps.currentRoute.name || "" : "",
      serviceWorkerState: swState,
      online: !!(root.navigator && root.navigator.onLine),
    };
  }

  function collectDeviceInfo() {
    var nav = root.navigator || {};
    var screen = root.screen || {};
    var userAgentData = nav.userAgentData || null;
    return {
      userAgent: nav.userAgent || "",
      userAgentData: userAgentData ? clone(userAgentData) : null,
      platform: nav.platform || "",
      language: nav.language || "",
      screen: {
        width: screen.width || 0,
        height: screen.height || 0,
      },
      pixelRatio: root.devicePixelRatio || 1,
      timezone:
        typeof Intl !== "undefined" && Intl.DateTimeFormat
          ? Intl.DateTimeFormat().resolvedOptions().timeZone || ""
          : "",
      touchPoints: nav.maxTouchPoints || 0,
      visibilityState: root.document ? root.document.visibilityState || "" : "",
    };
  }

  function getStepTechnicalSnapshot(step) {
    return {
      status: step.status,
      warnings: step.warnings,
      errors: step.errors,
      attempts: step.attempts.slice(-3),
      rawEvents: step.rawEvents.slice(-20),
      pipelineEvents: step.pipelineEvents.slice(-20),
    };
  }

  function renderMessage(message, tone) {
    return (
      '<div class="scanner-diag-message scanner-diag-message--' +
      escapeHtml(tone || "info") +
      '">' +
      escapeHtml(message) +
      "</div>"
    );
  }

  function renderAttempt(attempt) {
    if (!attempt) {
      return "";
    }
    return (
      '<div class="scanner-diag-attempt">' +
      '  <div class="scanner-diag-attempt-title">' +
      escapeHtml(attempt.message) +
      "</div>" +
      '  <div>Ожидаемая длина: ' +
      escapeHtml(String(attempt.expectedLength)) +
      "</div>" +
      '  <div>Полученная длина: ' +
      escapeHtml(String(attempt.actualLength)) +
      "</div>" +
      '  <div>Первое расхождение: позиция ' +
      escapeHtml(String(attempt.firstMismatch || "-")) +
      "</div>" +
      "</div>"
    );
  }

  function renderRecommendation(recommendation) {
    return (
      '<div class="scanner-diag-recommendation">' +
      "  <h2>Предварительно определённый профиль устройства</h2>" +
      "  <div>Transport: " +
      escapeHtml(recommendation.transport || "unknown") +
      "</div>" +
      "  <div>Input style: " +
      escapeHtml(recommendation.inputStyle || "unknown") +
      "</div>" +
      "  <div>Terminator: " +
      escapeHtml(recommendation.terminator || "Unknown") +
      "</div>" +
      "  <div>IME mode: " +
      (recommendation.imeMode ? "true" : "false") +
      "</div>" +
      "  <div>Recommended input delay: " +
      escapeHtml(String(recommendation.recommendedInputDelayMs || 80)) +
      " ms</div>" +
      "  <div>Recommended key delay: " +
      escapeHtml(String(recommendation.recommendedKeyDelayMs || 100)) +
      " ms</div>" +
      "</div>"
    );
  }

  function renderHistoryList(items, message) {
    var rows = (items || [])
      .map(function (report) {
        var summary = report.summary || {};
        var scanner = report.scanner || {};
        return (
          '<div class="scanner-diag-history-row">' +
          '  <div class="scanner-diag-history-main">' +
          escapeHtml(formatDateTime(report.finishedAt || report.startedAt)) +
          " — " +
          escapeHtml(summary.overallStatus || "UNKNOWN") +
          " — " +
          escapeHtml(scanner.selectedProvider || "unknown") +
          "</div>" +
          '  <div class="scanner-diag-history-actions">' +
          '    <button class="btn btn-outline" type="button" data-scanner-diag-open="' +
          escapeHtml(report.sessionId) +
          '">Открыть</button>' +
          '    <button class="btn btn-outline" type="button" data-scanner-diag-download="' +
          escapeHtml(report.sessionId) +
          '">Скачать</button>' +
          '    <button class="btn btn-danger" type="button" data-scanner-diag-delete="' +
          escapeHtml(report.sessionId) +
          '">Удалить</button>' +
          "  </div>" +
          "</div>"
        );
      })
      .join("");
    return (
      '<section class="screen scanner-diag-screen">' +
      '  <div class="screen-card scanner-diag-card">' +
      '    <h1 class="screen-title">История диагностик</h1>' +
      (message ? renderMessage(message, "warning") : "") +
      (rows || '<div class="empty-state">Отчётов пока нет.</div>') +
      '    <div class="scanner-diag-actions">' +
      '      <button class="btn btn-outline" type="button" id="scannerDiagBackBtn">Назад</button>' +
      "    </div>" +
      "  </div>" +
      "</section>"
    );
  }

  function renderLoading() {
    return (
      '<section class="screen scanner-diag-screen">' +
      '  <div class="screen-card scanner-diag-card">' +
      '    <h1 class="screen-title">Диагностика сканера</h1>' +
      '    <div class="empty-state">Загрузка...</div>' +
      "  </div>" +
      "</section>"
    );
  }

  function renderError(message) {
    return (
      '<section class="screen scanner-diag-screen">' +
      '  <div class="screen-card scanner-diag-card">' +
      '    <h1 class="screen-title">Диагностика сканера</h1>' +
      renderMessage(message, "error") +
      "  </div>" +
      "</section>"
    );
  }

  function downloadReport(report) {
    if (!report || !root.document) {
      return;
    }
    var json = JSON.stringify(report, null, 2);
    var blob = new Blob([json], { type: "application/json" });
    var url = root.URL && root.URL.createObjectURL ? root.URL.createObjectURL(blob) : "";
    if (!url) {
      return;
    }
    var link = root.document.createElement("a");
    link.href = url;
    link.download = getReportFileName(report);
    root.document.body.appendChild(link);
    link.click();
    root.document.body.removeChild(link);
    if (root.URL && root.URL.revokeObjectURL) {
      root.URL.revokeObjectURL(url);
    }
  }

  function copyReport(report) {
    var json = JSON.stringify(report, null, 2);
    if (root.navigator && root.navigator.clipboard && root.navigator.clipboard.writeText) {
      return root.navigator.clipboard.writeText(json);
    }
    if (root.prompt) {
      root.prompt("Скопируйте отчёт:", json);
    }
    return Promise.resolve(false);
  }

  function shareReport(report) {
    if (!root.navigator || typeof root.navigator.share !== "function") {
      return Promise.resolve(false);
    }
    var json = JSON.stringify(report, null, 2);
    var payload = {
      title: "FlowStock scanner diagnostics",
      text: json,
    };
    if (typeof File !== "undefined") {
      try {
        payload.files = [
          new File([json], getReportFileName(report), {
            type: "application/json",
          }),
        ];
      } catch (error) {
        delete payload.files;
      }
    }
    return root.navigator.share(payload);
  }

  function render(deps, route) {
    if (!singletonController) {
      singletonController = createController(deps || {});
    } else {
      singletonController.updateDeps(deps || {});
    }
    singletonController.render(route || { name: "scannerDiagnostics" });
  }

  function cleanupRoute() {
    if (singletonController) {
      singletonController.cleanupRoute();
    }
  }

  root.FlowStockScannerDiagnostics = {
    render: render,
    cleanupRoute: cleanupRoute,
    createController: createController,
    getReportFileName: getReportFileName,
    _test: {
      normalizeManifest: normalizeManifest,
      createSession: createSession,
      createController: createController,
      classifyScan: classifyScan,
      firstMismatch: firstMismatch,
      summarizeSteps: summarizeSteps,
      getReportFileName: getReportFileName,
      downloadReport: downloadReport,
      copyReport: copyReport,
      shareReport: shareReport,
      REPORT_SCHEMA: REPORT_SCHEMA,
    },
  };
})(typeof self !== "undefined" ? self : window);
