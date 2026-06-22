(function (root) {
  "use strict";

  var DB_NAME = "FlowStockScannerDiagnostics";
  var DB_VERSION = 1;
  var STORE_REPORTS = "reports";
  var STORE_ACTIVE_SESSION = "active-session";
  var ACTIVE_SESSION_KEY = "active";
  var HISTORY_LIMIT = 20;
  var db = null;

  function isIndexedDbAvailable() {
    return !!root.indexedDB;
  }

  function unavailable() {
    return Promise.reject(new Error("SCANNER_DIAGNOSTICS_INDEXEDDB_UNAVAILABLE"));
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

  function openDb() {
    if (!isIndexedDbAvailable()) {
      return unavailable();
    }
    if (db) {
      return Promise.resolve(db);
    }
    return new Promise(function (resolve, reject) {
      var request = root.indexedDB.open(DB_NAME, DB_VERSION);
      request.onupgradeneeded = function (event) {
        var database = event.target.result;
        if (!database.objectStoreNames.contains(STORE_REPORTS)) {
          var reports = database.createObjectStore(STORE_REPORTS, { keyPath: "sessionId" });
          reports.createIndex("finishedAt", "finishedAt", { unique: false });
          reports.createIndex("setId", "setId", { unique: false });
        }
        if (!database.objectStoreNames.contains(STORE_ACTIVE_SESSION)) {
          database.createObjectStore(STORE_ACTIVE_SESSION, { keyPath: "key" });
        }
      };
      request.onsuccess = function (event) {
        db = event.target.result;
        resolve(db);
      };
      request.onerror = function () {
        reject(request.error || new Error("SCANNER_DIAGNOSTICS_DB_OPEN_FAILED"));
      };
    });
  }

  function getStore(storeName, mode) {
    return openDb().then(function (database) {
      return database.transaction(storeName, mode).objectStore(storeName);
    });
  }

  function sortReportsNewestFirst(items) {
    return (items || []).sort(function (a, b) {
      var aTime = Date.parse((a && (a.finishedAt || a.startedAt || a.savedAt)) || "") || 0;
      var bTime = Date.parse((b && (b.finishedAt || b.startedAt || b.savedAt)) || "") || 0;
      return bTime - aTime;
    });
  }

  function listReports() {
    return openDb().then(function (database) {
      return new Promise(function (resolve, reject) {
        var tx = database.transaction(STORE_REPORTS, "readonly");
        var request = tx.objectStore(STORE_REPORTS).openCursor();
        var items = [];
        request.onsuccess = function (event) {
          var cursor = event.target.result;
          if (cursor) {
            items.push(cursor.value);
            cursor.continue();
            return;
          }
          resolve(sortReportsNewestFirst(items));
        };
        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function trimReports(limit) {
    var max = typeof limit === "number" && limit > 0 ? limit : HISTORY_LIMIT;
    return listReports().then(function (items) {
      var sorted = sortReportsNewestFirst(items || []);
      if (sorted.length <= max) {
        return sorted;
      }
      var extras = sorted.slice(max);
      return openDb().then(function (database) {
        return new Promise(function (resolve, reject) {
          var tx = database.transaction(STORE_REPORTS, "readwrite");
          var store = tx.objectStore(STORE_REPORTS);
          extras.forEach(function (report) {
            if (report && report.sessionId) {
              store.delete(report.sessionId);
            }
          });
          tx.oncomplete = function () {
            resolve(sorted.slice(0, max));
          };
          tx.onerror = function () {
            reject(tx.error);
          };
        });
      });
    });
  }

  function saveReport(report) {
    var record = clone(report) || {};
    record.sessionId = String(record.sessionId || record.id || Date.now());
    record.savedAt = new Date().toISOString();
    return getStore(STORE_REPORTS, "readwrite")
      .then(function (store) {
        return new Promise(function (resolve, reject) {
          var request = store.put(record);
          request.onsuccess = function () {
            resolve(record);
          };
          request.onerror = function () {
            reject(request.error);
          };
        });
      })
      .then(function (saved) {
        return trimReports(HISTORY_LIMIT).then(function () {
          return saved;
        });
      });
  }

  function getReport(sessionId) {
    var key = String(sessionId || "");
    return getStore(STORE_REPORTS, "readonly").then(function (store) {
      return new Promise(function (resolve, reject) {
        var request = store.get(key);
        request.onsuccess = function () {
          resolve(request.result || null);
        };
        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function deleteReport(sessionId) {
    var key = String(sessionId || "");
    return getStore(STORE_REPORTS, "readwrite").then(function (store) {
      return new Promise(function (resolve, reject) {
        var request = store.delete(key);
        request.onsuccess = function () {
          resolve(true);
        };
        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function saveActiveSession(session) {
    var record = {
      key: ACTIVE_SESSION_KEY,
      savedAt: new Date().toISOString(),
      session: clone(session),
    };
    return getStore(STORE_ACTIVE_SESSION, "readwrite").then(function (store) {
      return new Promise(function (resolve, reject) {
        var request = store.put(record);
        request.onsuccess = function () {
          resolve(record.session);
        };
        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function loadActiveSession() {
    return getStore(STORE_ACTIVE_SESSION, "readonly").then(function (store) {
      return new Promise(function (resolve, reject) {
        var request = store.get(ACTIVE_SESSION_KEY);
        request.onsuccess = function () {
          resolve(request.result && request.result.session ? request.result.session : null);
        };
        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function clearActiveSession() {
    return getStore(STORE_ACTIVE_SESSION, "readwrite").then(function (store) {
      return new Promise(function (resolve, reject) {
        var request = store.delete(ACTIVE_SESSION_KEY);
        request.onsuccess = function () {
          resolve(true);
        };
        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  root.FlowStockScannerDiagnosticsStore = {
    saveReport: saveReport,
    listReports: listReports,
    getReport: getReport,
    deleteReport: deleteReport,
    saveActiveSession: saveActiveSession,
    loadActiveSession: loadActiveSession,
    clearActiveSession: clearActiveSession,
    isAvailable: isIndexedDbAvailable,
    _test: {
      DB_NAME: DB_NAME,
      DB_VERSION: DB_VERSION,
      STORE_REPORTS: STORE_REPORTS,
      STORE_ACTIVE_SESSION: STORE_ACTIVE_SESSION,
      HISTORY_LIMIT: HISTORY_LIMIT,
    },
  };
})(typeof self !== "undefined" ? self : window);
