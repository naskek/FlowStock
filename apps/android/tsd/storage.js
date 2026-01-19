(function (global) {
  "use strict";

  var DB_NAME = "tsd_app";
  var DB_VERSION = 2;
  var STORE_SETTINGS = "settings";
  var STORE_DOCS = "docs";
  var db = null;

  function openDb() {
    return new Promise(function (resolve, reject) {
      var request = indexedDB.open(DB_NAME, DB_VERSION);

      request.onupgradeneeded = function (event) {
        var database = event.target.result;
        if (!database.objectStoreNames.contains(STORE_SETTINGS)) {
          database.createObjectStore(STORE_SETTINGS, { keyPath: "key" });
        }
        if (!database.objectStoreNames.contains(STORE_DOCS)) {
          database.createObjectStore(STORE_DOCS, { keyPath: "id" });
        }
      };

      request.onsuccess = function (event) {
        db = event.target.result;
        resolve(db);
      };

      request.onerror = function () {
        reject(request.error);
      };
    });
  }

  function getSettingsStore(mode) {
    return db.transaction(STORE_SETTINGS, mode).objectStore(STORE_SETTINGS);
  }

  function getDocsStore(mode) {
    return db.transaction(STORE_DOCS, mode).objectStore(STORE_DOCS);
  }

  function init() {
    if (db) {
      return Promise.resolve(db);
    }
    return openDb();
  }

  function getSetting(key) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var request = getSettingsStore("readonly").get(key);

        request.onsuccess = function () {
          resolve(request.result ? request.result.value : undefined);
        };

        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function setSetting(key, value) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var request = getSettingsStore("readwrite").put({ key: key, value: value });

        request.onsuccess = function () {
          resolve(true);
        };

        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function getDocCounters() {
    return getSetting("docCounters").then(function (value) {
      return value && typeof value === "object" ? value : {};
    });
  }

  function nextDocCounter(prefix, dateKey) {
    return getDocCounters().then(function (counters) {
      var key = prefix + "-" + dateKey;
      var next = (counters[key] || 0) + 1;
      counters[key] = next;
      return setSetting("docCounters", counters).then(function () {
        return next;
      });
    });
  }

  function ensureDefaults() {
    return getSetting("device_id")
      .then(function (value) {
        if (!value) {
          return setSetting("device_id", "CT48-01");
        }
        return true;
      })
      .then(function () {
        return getSetting("docCounters").then(function (value) {
          if (!value || typeof value !== "object") {
            return setSetting("docCounters", {});
          }
          return true;
        });
      });
  }

  function getDoc(id) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var request = getDocsStore("readonly").get(id);

        request.onsuccess = function () {
          resolve(request.result || null);
        };

        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function saveDoc(doc) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var request = getDocsStore("readwrite").put(doc);

        request.onsuccess = function () {
          resolve(true);
        };

        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function deleteDoc(id) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var request = getDocsStore("readwrite").delete(id);

        request.onsuccess = function () {
          resolve(true);
        };

        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function listDocs() {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var docs = [];
        var request = getDocsStore("readonly").openCursor();

        request.onsuccess = function (event) {
          var cursor = event.target.result;
          if (cursor) {
            docs.push(cursor.value);
            cursor.continue();
          } else {
            resolve(docs);
          }
        };

        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  global.TsdStorage = {
    init: init,
    getSetting: getSetting,
    setSetting: setSetting,
    getDocCounters: getDocCounters,
    nextDocCounter: nextDocCounter,
    ensureDefaults: ensureDefaults,
    getDoc: getDoc,
    saveDoc: saveDoc,
    deleteDoc: deleteDoc,
    listDocs: listDocs,
  };
})(window);
