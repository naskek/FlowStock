(function (root) {
  "use strict";

  var REPORT_SCHEMA = "flowstock-scanner-lifecycle-report/v1";
  var DB_NAME = "FlowStockScannerLifecycleDiagnostics";
  var DB_VERSION = 1;
  var STORE_SESSIONS = "sessions";
  var SESSION_LIMIT = 5;
  var MEMORY_EVENT_LIMIT = 500;
  var PERSISTED_EVENT_LIMIT = 2000;
  var SAVE_DELAY_MS = 150;
  var sessionId = createSessionId();
  var createdAt = new Date().toISOString();
  var seq = 0;
  var events = [];
  var saveTimer = 0;
  var savePromise = Promise.resolve();
  var db = null;
  var stateProvider = null;

  function nowIso() {
    return new Date().toISOString();
  }

  function createSessionId() {
    var stamp = new Date().toISOString().replace(/[^0-9T]/g, "");
    var random = Math.random().toString(36).slice(2, 8);
    return "tsd-life-" + stamp + "-" + random;
  }

  function isObject(value) {
    return !!value && typeof value === "object";
  }

  function hashString(value) {
    var text = String(value == null ? "" : value);
    var hash = 2166136261;
    for (var i = 0; i < text.length; i += 1) {
      hash ^= text.charCodeAt(i);
      hash += (hash << 1) + (hash << 4) + (hash << 7) + (hash << 8) + (hash << 24);
    }
    return ("00000000" + (hash >>> 0).toString(16)).slice(-8);
  }

  function maskValue(value) {
    var text = String(value == null ? "" : value);
    if (!text) {
      return "";
    }
    if (text.length <= 6) {
      return text.slice(0, 1) + "…";
    }
    return text.slice(0, 3) + "…" + text.slice(-4);
  }

  function addMaskedValue(target, key, value) {
    var text = String(value == null ? "" : value);
    target[key + "Length"] = text.length;
    target[key + "Hash"] = text ? "h32:" + hashString(text) : "";
    target[key + "Masked"] = text ? maskValue(text) : "";
  }

  function isSensitivePayloadKey(key) {
    var normalized = String(key || "").toLowerCase();
    return (
      normalized === "value" ||
      normalized === "rawvalue" ||
      normalized === "normalizedvalue" ||
      normalized === "buffer" ||
      normalized === "rawscanvalue" ||
      normalized === "barcode" ||
      normalized === "hucode" ||
      normalized === "payload" ||
      normalized === "data" ||
      normalized === "code" ||
      normalized === "text" ||
      normalized === "scandata" ||
      normalized === "scan_data"
    );
  }

  function addSanitizedSensitiveValue(target, key, value) {
    if (typeof value === "string") {
      addMaskedValue(target, key, value);
      return;
    }
    if (Array.isArray(value) || isObject(value)) {
      target[key] = sanitizeValue(value);
      return;
    }
    target[key] = value;
  }

  function sanitizeTarget(target) {
    if (!isObject(target)) {
      return target || null;
    }
    var result = {};
    Object.keys(target).forEach(function (key) {
      if (key === "valueSnapshot" || key === "value" || key === "rawValue") {
        addSanitizedSensitiveValue(result, "value", target[key]);
        return;
      }
      result[key] = sanitizeValue(target[key]);
    });
    if (typeof target.valueSnapshot === "string" && result.valueLength == null) {
      addMaskedValue(result, "value", target.valueSnapshot);
    }
    return result;
  }

  function sanitizeRawPayload(payload) {
    if (payload == null) {
      return payload;
    }
    if (typeof payload === "string") {
      var result = {};
      addMaskedValue(result, "payload", payload);
      return result;
    }
    if (!isObject(payload)) {
      return payload;
    }
    var resultObj = {};
    Object.keys(payload).forEach(function (key) {
      if (isSensitivePayloadKey(key)) {
        addSanitizedSensitiveValue(resultObj, key, payload[key]);
        return;
      }
      resultObj[key] = sanitizeValue(payload[key]);
    });
    return resultObj;
  }

  function sanitizeValue(value) {
    if (Array.isArray(value)) {
      return value.slice(0, 20).map(sanitizeValue);
    }
    if (!isObject(value)) {
      return value;
    }
    return sanitizeDetail(value);
  }

  function sanitizeDetail(detail) {
    var source = isObject(detail) ? detail : {};
    var result = {};
    Object.keys(source).forEach(function (key) {
      var value = source[key];
      if (key === "target" || key === "activeElement" || key === "preferredTarget") {
        result[key] = sanitizeTarget(value);
        return;
      }
      if (key === "rawPayload") {
        result[key] = sanitizeRawPayload(value);
        return;
      }
      if (isSensitivePayloadKey(key)) {
        addSanitizedSensitiveValue(result, key, value);
        return;
      }
      result[key] = sanitizeValue(value);
    });
    return result;
  }

  function getStateSnapshot() {
    if (typeof stateProvider !== "function") {
      return {};
    }
    try {
      return stateProvider() || {};
    } catch (error) {
      return { stateError: error && error.message ? error.message : String(error || "state-error") };
    }
  }

  function trimMemory() {
    if (events.length > MEMORY_EVENT_LIMIT) {
      events = events.slice(events.length - MEMORY_EVENT_LIMIT);
    }
  }

  function clone(value) {
    try {
      return JSON.parse(JSON.stringify(value));
    } catch (error) {
      return value;
    }
  }

  function record(type, detail) {
    var state = getStateSnapshot();
    var entry = {
      seq: ++seq,
      ts: nowIso(),
      type: String(type || "event"),
      routeName: state.routeName || "",
      routeKey: state.routeKey || "",
      routeGeneration: state.routeGeneration || 0,
      detail: sanitizeDetail(detail || {}),
      state: sanitizeDetail(state),
    };
    events.push(entry);
    trimMemory();
    scheduleSave();
    return entry;
  }

  function isIndexedDbAvailable() {
    return !!root.indexedDB;
  }

  function openDb() {
    if (!isIndexedDbAvailable()) {
      return Promise.reject(new Error("SCANNER_LIFECYCLE_INDEXEDDB_UNAVAILABLE"));
    }
    if (db) {
      return Promise.resolve(db);
    }
    return new Promise(function (resolve, reject) {
      var request = root.indexedDB.open(DB_NAME, DB_VERSION);
      request.onupgradeneeded = function (event) {
        var database = event.target.result;
        if (!database.objectStoreNames.contains(STORE_SESSIONS)) {
          database.createObjectStore(STORE_SESSIONS, { keyPath: "sessionId" });
        }
      };
      request.onsuccess = function (event) {
        db = event.target.result;
        resolve(db);
      };
      request.onerror = function () {
        reject(request.error || new Error("SCANNER_LIFECYCLE_DB_OPEN_FAILED"));
      };
    });
  }

  function listSessions() {
    return openDb().then(function (database) {
      return new Promise(function (resolve, reject) {
        var request = database.transaction(STORE_SESSIONS, "readonly").objectStore(STORE_SESSIONS).openCursor();
        var rows = [];
        request.onsuccess = function (event) {
          var cursor = event.target.result;
          if (!cursor) {
            rows.sort(function (left, right) {
              return Date.parse(right.updatedAt || right.createdAt || "") - Date.parse(left.updatedAt || left.createdAt || "");
            });
            resolve(rows);
            return;
          }
          rows.push(cursor.value);
          cursor.continue();
        };
        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function trimSessions() {
    return listSessions().then(function (rows) {
      var extra = rows.slice(SESSION_LIMIT);
      if (!extra.length) {
        return rows.slice(0, SESSION_LIMIT);
      }
      return openDb().then(function (database) {
        return new Promise(function (resolve, reject) {
          var tx = database.transaction(STORE_SESSIONS, "readwrite");
          var store = tx.objectStore(STORE_SESSIONS);
          extra.forEach(function (row) {
            store.delete(row.sessionId);
          });
          tx.oncomplete = function () {
            resolve(rows.slice(0, SESSION_LIMIT));
          };
          tx.onerror = function () {
            reject(tx.error);
          };
        });
      });
    });
  }

  function classify(reportEvents) {
    var lastRaw = null;
    var lastScan = null;
    var lastManager = null;
    var lastHandler = null;
    var lastHandlerMissing = null;
    var lastBusinessStarted = null;
    var lastBusinessDone = null;
    var lastBlockReason = "";
    (reportEvents || []).forEach(function (event) {
      if (/^raw-/.test(event.type)) {
        lastRaw = event;
      }
      if (event.type === "scan-emitted") {
        lastScan = event;
      }
      if (event.type === "manager-received") {
        lastManager = event;
      }
      if (event.type === "handler-dispatched") {
        lastHandler = event;
      }
      if (event.type === "handler-missing") {
        lastHandlerMissing = event;
      }
      if (event.type === "business-handler-started") {
        lastBusinessStarted = event;
      }
      if (event.type === "business-handler-completed" || event.type === "business-handler-failed") {
        lastBusinessDone = event;
      }
      if (event.type === "can-scan-decision" && event.detail && event.detail.blockReason) {
        lastBlockReason = event.detail.blockReason;
      }
    });
    var classification = "INSUFFICIENT_EVIDENCE";
    if (lastRaw && !lastScan) {
      classification = "B_PROVIDER_NO_SCAN";
    }
    if (lastScan && (!lastManager || (lastManager.seq || 0) < (lastScan.seq || 0))) {
      classification = "C_MANAGER_MISSING";
    }
    if (lastHandlerMissing || (lastManager && (!lastHandler || (lastHandler.seq || 0) < (lastManager.seq || 0)))) {
      classification = "C_HANDLER_MISSING";
    }
    if (lastHandler && lastBusinessStarted && (!lastBusinessDone || (lastBusinessDone.seq || 0) < (lastBusinessStarted.seq || 0))) {
      classification = "D_BUSINESS_INCOMPLETE";
    }
    return {
      classification: classification,
      lastRawEventAt: lastRaw ? lastRaw.ts : null,
      lastScanEmittedAt: lastScan ? lastScan.ts : null,
      lastManagerReceivedAt: lastManager ? lastManager.ts : null,
      lastHandlerDispatchedAt: lastHandler ? lastHandler.ts : null,
      lastBlockReason: lastBlockReason,
    };
  }

  function buildReport() {
    var reportEvents = events.slice(-PERSISTED_EVENT_LIMIT);
    var state = getStateSnapshot();
    return {
      schema: REPORT_SCHEMA,
      appVersion: String(root.TSD_PWA_VERSION || ""),
      sessionId: sessionId,
      createdAt: createdAt,
      exportedAt: nowIso(),
      route: {
        name: state.routeName || "",
        key: state.routeKey || "",
      },
      summary: classify(reportEvents),
      state: sanitizeDetail(state),
      events: clone(reportEvents),
    };
  }

  function saveNow() {
    if (!isIndexedDbAvailable()) {
      return Promise.resolve(null);
    }
    var report = buildReport();
    return openDb()
      .then(function (database) {
        return new Promise(function (resolve, reject) {
          var tx = database.transaction(STORE_SESSIONS, "readwrite");
          var store = tx.objectStore(STORE_SESSIONS);
          var record = clone(report);
          record.updatedAt = nowIso();
          store.put(record);
          tx.oncomplete = function () {
            resolve(record);
          };
          tx.onerror = function () {
            reject(tx.error);
          };
        });
      })
      .then(function (record) {
        return trimSessions().then(function () {
          return record;
        });
      })
      .catch(function () {
        return null;
      });
  }

  function scheduleSave() {
    if (!isIndexedDbAvailable()) {
      return;
    }
    if (saveTimer) {
      return;
    }
    saveTimer = root.setTimeout(function () {
      saveTimer = 0;
      savePromise = savePromise.then(saveNow, saveNow);
    }, SAVE_DELAY_MS);
  }

  function flush() {
    if (saveTimer) {
      root.clearTimeout(saveTimer);
      saveTimer = 0;
    }
    savePromise = savePromise.then(saveNow, saveNow);
    return savePromise;
  }

  function clear() {
    events = [];
    seq = 0;
    sessionId = createSessionId();
    createdAt = nowIso();
    if (!isIndexedDbAvailable()) {
      return Promise.resolve(true);
    }
    return openDb().then(function (database) {
      return new Promise(function (resolve, reject) {
        var tx = database.transaction(STORE_SESSIONS, "readwrite");
        tx.objectStore(STORE_SESSIONS).clear();
        tx.oncomplete = function () {
          resolve(true);
        };
        tx.onerror = function () {
          reject(tx.error);
        };
      });
    });
  }

  function getReportFileName(report) {
    var stamp = String((report && report.exportedAt) || nowIso()).replace(/[^0-9T]/g, "").slice(0, 15);
    return "flowstock-scanner-lifecycle_" + stamp + ".json";
  }

  function downloadReport(report) {
    report = report || buildReport();
    if (!root.document || typeof Blob === "undefined" || !root.URL || !root.URL.createObjectURL) {
      return false;
    }
    var json = JSON.stringify(report, null, 2);
    var blob = new Blob([json], { type: "application/json" });
    var url = root.URL.createObjectURL(blob);
    var link = root.document.createElement("a");
    link.href = url;
    link.download = getReportFileName(report);
    root.document.body.appendChild(link);
    link.click();
    root.setTimeout(function () {
      if (link.parentNode) {
        link.parentNode.removeChild(link);
      }
      root.URL.revokeObjectURL(url);
    }, 0);
    return true;
  }

  function copyReport(report) {
    var json = JSON.stringify(report || buildReport(), null, 2);
    if (root.navigator && root.navigator.clipboard && root.navigator.clipboard.writeText) {
      return root.navigator.clipboard.writeText(json).then(function () {
        return true;
      });
    }
    if (typeof root.prompt === "function") {
      root.prompt("Скопируйте отчёт:", json);
      return Promise.resolve(true);
    }
    return Promise.resolve(false);
  }

  function setStateProvider(provider) {
    stateProvider = typeof provider === "function" ? provider : null;
  }

  root.FlowStockScannerLifecycleDiagnostics = {
    record: record,
    flush: flush,
    clear: clear,
    buildReport: buildReport,
    downloadReport: downloadReport,
    copyReport: copyReport,
    listReports: listSessions,
    setStateProvider: setStateProvider,
    _test: {
      DB_NAME: DB_NAME,
      DB_VERSION: DB_VERSION,
      REPORT_SCHEMA: REPORT_SCHEMA,
      MEMORY_EVENT_LIMIT: MEMORY_EVENT_LIMIT,
      PERSISTED_EVENT_LIMIT: PERSISTED_EVENT_LIMIT,
      SESSION_LIMIT: SESSION_LIMIT,
      sanitizeDetail: sanitizeDetail,
      maskValue: maskValue,
      hashString: hashString,
      classify: classify,
    },
  };
})(typeof self !== "undefined" ? self : window);
