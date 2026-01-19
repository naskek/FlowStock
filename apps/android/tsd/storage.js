(function (global) {
  "use strict";

  var DB_NAME = "tsd_app";
  var DB_VERSION = 3;
  var STORE_SETTINGS = "settings";
  var STORE_DOCS = "docs";
  var STORE_META = "meta";
  var STORE_ITEMS = "items";
  var STORE_ITEM_CODES = "itemCodes";
  var STORE_PARTNERS = "partners";
  var STORE_LOCATIONS = "locations";
  var STORE_STOCK = "stock";
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
        if (!database.objectStoreNames.contains(STORE_META)) {
          database.createObjectStore(STORE_META, { keyPath: "key" });
        }
        if (!database.objectStoreNames.contains(STORE_ITEMS)) {
          database.createObjectStore(STORE_ITEMS, { keyPath: "itemId" });
        }
        if (!database.objectStoreNames.contains(STORE_ITEM_CODES)) {
          var itemCodesStore = database.createObjectStore(STORE_ITEM_CODES, { keyPath: "code" });
          itemCodesStore.createIndex("code", "code", { unique: true });
        }
        if (!database.objectStoreNames.contains(STORE_PARTNERS)) {
          var partnersStore = database.createObjectStore(STORE_PARTNERS, { keyPath: "partnerId" });
          partnersStore.createIndex("nameLower", "nameLower", { unique: false });
        }
        if (!database.objectStoreNames.contains(STORE_LOCATIONS)) {
          var locationsStore = database.createObjectStore(STORE_LOCATIONS, {
            keyPath: "locationId",
          });
          locationsStore.createIndex("codeLower", "codeLower", { unique: false });
          locationsStore.createIndex("nameLower", "nameLower", { unique: false });
        }
        if (!database.objectStoreNames.contains(STORE_STOCK)) {
          var stockStore = database.createObjectStore(STORE_STOCK, { autoIncrement: true });
          stockStore.createIndex("byItemId", "itemId", { unique: false });
          stockStore.createIndex("byLocationId", "locationId", { unique: false });
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

  function init() {
    if (db) {
      return Promise.resolve(db);
    }
    return openDb();
  }

  function getSettingsStore(mode) {
    return db.transaction(STORE_SETTINGS, mode).objectStore(STORE_SETTINGS);
  }

  function getDocsStore(mode) {
    return db.transaction(STORE_DOCS, mode).objectStore(STORE_DOCS);
  }

  function getMetaStore(mode) {
    return db.transaction(STORE_META, mode).objectStore(STORE_META);
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
      })
      .then(function () {
        return getSetting("recentPartnerIds").then(function (value) {
          if (!Array.isArray(value)) {
            return setSetting("recentPartnerIds", []);
          }
          return true;
        });
      })
      .then(function () {
        return getSetting("recentLocationIds").then(function (value) {
          if (!Array.isArray(value)) {
            return setSetting("recentLocationIds", []);
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

  function clearStore(store) {
    return new Promise(function (resolve, reject) {
      var request = store.clear();
      request.onsuccess = function () {
        resolve(true);
      };
      request.onerror = function () {
        reject(request.error);
      };
    });
  }

  function importTsdData(json) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var tx = db.transaction(
          [
            STORE_META,
            STORE_ITEMS,
            STORE_ITEM_CODES,
            STORE_PARTNERS,
            STORE_LOCATIONS,
            STORE_STOCK,
          ],
          "readwrite"
        );
        var metaStore = tx.objectStore(STORE_META);
        var itemsStore = tx.objectStore(STORE_ITEMS);
        var codesStore = tx.objectStore(STORE_ITEM_CODES);
        var partnersStore = tx.objectStore(STORE_PARTNERS);
        var locationsStore = tx.objectStore(STORE_LOCATIONS);
        var stockStore = tx.objectStore(STORE_STOCK);

        tx.oncomplete = function () {
          resolve(true);
        };
        tx.onerror = function () {
          reject(tx.error);
        };

        Promise.all([
          clearStore(metaStore),
          clearStore(itemsStore),
          clearStore(codesStore),
          clearStore(partnersStore),
          clearStore(locationsStore),
          clearStore(stockStore),
        ])
          .then(function () {
            var meta = json && json.meta ? json.meta : {};
            metaStore.put({ key: "dataExportedAt", value: meta.exportedAt || null });
            metaStore.put({ key: "schemaVersion", value: meta.schemaVersion || null });

            (json.items || []).forEach(function (item) {
              itemsStore.put(item);
              var codes = [];
              if (Array.isArray(item.barcodes)) {
                codes = codes.concat(item.barcodes);
              }
              if (item.gtin) {
                codes.push(item.gtin);
              }
              codes.forEach(function (code) {
                var clean = String(code || "").trim();
                if (!clean) {
                  return;
                }
                codesStore.put({
                  code: clean,
                  itemId: item.itemId,
                  kind: code === item.gtin ? "gtin" : "barcode",
                });
              });
            });

            (json.partners || []).forEach(function (partner) {
              partnersStore.put({
                partnerId: partner.partnerId,
                name: partner.name,
                inn: partner.inn || null,
                code: partner.code || null,
                nameLower: String(partner.name || "").toLowerCase(),
              });
            });

            (json.locations || []).forEach(function (location) {
              locationsStore.put({
                locationId: location.locationId,
                code: location.code,
                name: location.name,
                codeLower: String(location.code || "").toLowerCase(),
                nameLower: String(location.name || "").toLowerCase(),
              });
            });

            (json.stock || []).forEach(function (stock) {
              stockStore.put({
                itemId: stock.itemId,
                locationId: stock.locationId,
                qtyBase: stock.qtyBase,
              });
            });
          })
          .catch(function (error) {
            try {
              tx.abort();
            } catch (abortError) {
              reject(abortError);
              return;
            }
            reject(error);
          });
      });
    });
  }

  function countStore(storeName) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var tx = db.transaction(storeName, "readonly");
        var store = tx.objectStore(storeName);
        var request = store.count();
        request.onsuccess = function () {
          resolve(request.result || 0);
        };
        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function getMetaValue(key) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var request = getMetaStore("readonly").get(key);
        request.onsuccess = function () {
          resolve(request.result ? request.result.value : undefined);
        };
        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function getDataStatus() {
    return Promise.all([
      getMetaValue("dataExportedAt"),
      getMetaValue("schemaVersion"),
      countStore(STORE_ITEMS),
      countStore(STORE_PARTNERS),
      countStore(STORE_LOCATIONS),
      countStore(STORE_STOCK),
    ]).then(function (results) {
      return {
        exportedAt: results[0] || null,
        schemaVersion: results[1] || null,
        counts: {
          items: results[2] || 0,
          partners: results[3] || 0,
          locations: results[4] || 0,
          stock: results[5] || 0,
        },
      };
    });
  }

  function findItemByCode(code) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var clean = String(code || "").trim();
        if (!clean) {
          resolve(null);
          return;
        }
        var codesRequest = db.transaction(STORE_ITEM_CODES, "readonly")
          .objectStore(STORE_ITEM_CODES)
          .get(clean);
        codesRequest.onsuccess = function () {
          var mapping = codesRequest.result;
          if (!mapping) {
            resolve(null);
            return;
          }
          var itemRequest = db.transaction(STORE_ITEMS, "readonly")
            .objectStore(STORE_ITEMS)
            .get(mapping.itemId);
          itemRequest.onsuccess = function () {
            resolve(itemRequest.result || null);
          };
          itemRequest.onerror = function () {
            reject(itemRequest.error);
          };
        };
        codesRequest.onerror = function () {
          reject(codesRequest.error);
        };
      });
    });
  }

  function searchPartners(query) {
    var q = String(query || "").toLowerCase();
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var results = [];
        var request = db.transaction(STORE_PARTNERS, "readonly")
          .objectStore(STORE_PARTNERS)
          .openCursor();

        request.onsuccess = function (event) {
          var cursor = event.target.result;
          if (!cursor) {
            resolve(results);
            return;
          }
          var value = cursor.value;
          if (!q) {
            results.push(value);
          } else {
            var code = String(value.code || "").toLowerCase();
            var inn = String(value.inn || "").toLowerCase();
            if (
              (value.nameLower && value.nameLower.indexOf(q) !== -1) ||
              (code && code.indexOf(q) !== -1) ||
              (inn && inn.indexOf(q) !== -1)
            ) {
              results.push(value);
            }
          }
          if (results.length >= 100) {
            resolve(results);
            return;
          }
          cursor.continue();
        };

        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function searchLocations(query) {
    var q = String(query || "").toLowerCase();
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var results = [];
        var request = db.transaction(STORE_LOCATIONS, "readonly")
          .objectStore(STORE_LOCATIONS)
          .openCursor();

        request.onsuccess = function (event) {
          var cursor = event.target.result;
          if (!cursor) {
            resolve(results);
            return;
          }
          var value = cursor.value;
          if (!q) {
            results.push(value);
          } else {
            if (
              (value.codeLower && value.codeLower.indexOf(q) !== -1) ||
              (value.nameLower && value.nameLower.indexOf(q) !== -1)
            ) {
              results.push(value);
            }
          }
          if (results.length >= 100) {
            resolve(results);
            return;
          }
          cursor.continue();
        };

        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function getPartnerById(id) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var request = db.transaction(STORE_PARTNERS, "readonly")
          .objectStore(STORE_PARTNERS)
          .get(id);
        request.onsuccess = function () {
          resolve(request.result || null);
        };
        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function getLocationById(id) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var request = db.transaction(STORE_LOCATIONS, "readonly")
          .objectStore(STORE_LOCATIONS)
          .get(id);
        request.onsuccess = function () {
          resolve(request.result || null);
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
    importTsdData: importTsdData,
    getDataStatus: getDataStatus,
    findItemByCode: findItemByCode,
    searchPartners: searchPartners,
    searchLocations: searchLocations,
    getPartnerById: getPartnerById,
    getLocationById: getLocationById,
  };
})(window);
