 (function (global) {
  "use strict";

  var DB_NAME = "tsd_app";
  var DB_VERSION = 5;
  var STORE_SETTINGS = "settings";
  var STORE_DOCS = "docs";
  var STORE_META = "meta";
  var STORE_ITEMS = "items";
  var STORE_ITEM_CODES = "itemCodes";
  var STORE_PARTNERS = "partners";
  var STORE_LOCATIONS = "locations";
  var STORE_STOCK = "stock";
  var STORE_ORDERS = "orders";
  var db = null;
  var locationCache = null;

  function invalidateLocationCache() {
    locationCache = null;
  }

  function loadLocationMap() {
    return new Promise(function (resolve, reject) {
      var tx = db.transaction(STORE_LOCATIONS, "readonly");
      var store = tx.objectStore(STORE_LOCATIONS);
      var request = store.openCursor();
      var map = {};
      request.onsuccess = function (event) {
        var cursor = event.target.result;
        if (cursor) {
          map[cursor.key] = cursor.value;
          cursor.continue();
          return;
        }
        resolve(map);
      };
      request.onerror = function () {
        reject(request.error);
      };
    });
  }

  function getLocationMap() {
    if (locationCache) {
      return Promise.resolve(locationCache);
    }
    return init().then(function () {
      return loadLocationMap().then(function (map) {
        locationCache = map;
        return map;
      });
    });
  }

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
          var itemsStore = database.createObjectStore(STORE_ITEMS, { keyPath: "itemId" });
          if (!itemsStore.indexNames.contains("nameLower")) {
            itemsStore.createIndex("nameLower", "nameLower", { unique: false });
          }
          if (!itemsStore.indexNames.contains("skuLower")) {
            itemsStore.createIndex("skuLower", "skuLower", { unique: false });
          }
          if (!itemsStore.indexNames.contains("gtinLower")) {
            itemsStore.createIndex("gtinLower", "gtinLower", { unique: false });
          }
        } else if (event.oldVersion < 4) {
          var existingItemsStore = event.currentTarget.transaction.objectStore(STORE_ITEMS);
          if (!existingItemsStore.indexNames.contains("nameLower")) {
            existingItemsStore.createIndex("nameLower", "nameLower", { unique: false });
          }
          if (!existingItemsStore.indexNames.contains("skuLower")) {
            existingItemsStore.createIndex("skuLower", "skuLower", { unique: false });
          }
          if (!existingItemsStore.indexNames.contains("gtinLower")) {
            existingItemsStore.createIndex("gtinLower", "gtinLower", { unique: false });
          }
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
        if (!database.objectStoreNames.contains(STORE_ORDERS)) {
          var ordersStore = database.createObjectStore(STORE_ORDERS, { keyPath: "orderId" });
          ordersStore.createIndex("numberLower", "numberLower", { unique: false });
          ordersStore.createIndex("partnerId", "partnerId", { unique: false });
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
    })
    .then(function () {
      return getSetting("qtyStep").then(function (value) {
        if (!value || isNaN(Number(value)) || Number(value) < 1) {
          return setSetting("qtyStep", 1);
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
            STORE_ORDERS,
            STORE_STOCK,
          ],
          "readwrite"
        );
        var metaStore = tx.objectStore(STORE_META);
        var itemsStore = tx.objectStore(STORE_ITEMS);
        var codesStore = tx.objectStore(STORE_ITEM_CODES);
        var partnersStore = tx.objectStore(STORE_PARTNERS);
        var locationsStore = tx.objectStore(STORE_LOCATIONS);
        var ordersStore = tx.objectStore(STORE_ORDERS);
        var stockStore = tx.objectStore(STORE_STOCK);

        tx.oncomplete = function () {
          invalidateLocationCache();
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
          clearStore(ordersStore),
          clearStore(stockStore),
        ])
          .then(function () {
            var meta = json && json.meta ? json.meta : {};
            metaStore.put({ key: "dataExportedAt", value: meta.exportedAt || null });
            metaStore.put({ key: "schemaVersion", value: meta.schemaVersion || null });

            (json.items || []).forEach(function (item) {
              var itemRecord = Object.assign({}, item);
              itemRecord.nameLower = String(itemRecord.name || "").toLowerCase();
              itemRecord.skuLower = String(itemRecord.sku || "").toLowerCase();
              itemRecord.gtinLower = String(itemRecord.gtin || "").toLowerCase();
              itemsStore.put(itemRecord);
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

            (json.orders || []).forEach(function (order) {
              var orderId = order.orderId || order.id;
              if (orderId == null) {
                return;
              }
              ordersStore.put({
                orderId: orderId,
                number: order.number || order.orderNumber || "",
                partnerId: order.partnerId || null,
                plannedDate: order.plannedDate || order.planned_date || null,
                status: order.status || null,
                numberLower: String(order.number || order.orderNumber || "").toLowerCase(),
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
      countStore(STORE_ORDERS),
      countStore(STORE_STOCK),
    ]).then(function (results) {
      return {
        exportedAt: results[0] || null,
        schemaVersion: results[1] || null,
        counts: {
          items: results[2] || 0,
          partners: results[3] || 0,
          locations: results[4] || 0,
          orders: results[5] || 0,
          stock: results[6] || 0,
        },
      };
    });
  }

  function getMetaExportedAt() {
    return getMetaValue("dataExportedAt");
  }

  function searchItems(query, limit) {
    var q = String(query || "").toLowerCase();
    var max = typeof limit === "number" ? limit : 20;
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var results = [];
        var tx = db.transaction(STORE_ITEMS, "readonly");
        var store = tx.objectStore(STORE_ITEMS);
        var request = store.openCursor();
        request.onsuccess = function (event) {
          var cursor = event.target.result;
          if (!cursor) {
            resolve(results);
            return;
          }
          var item = cursor.value || {};
          var matches = !q;
          if (!matches) {
            var candidate =
              (item.nameLower || String(item.name || "").toLowerCase()) +
              "|" +
              (item.skuLower || String(item.sku || "").toLowerCase()) +
              "|" +
              (item.gtinLower || String(item.gtin || "").toLowerCase());
            matches = candidate.indexOf(q) !== -1;
          }
          if (matches) {
            results.push({
              itemId: item.itemId,
              name: item.name,
              sku: item.sku,
              gtin: item.gtin,
            });
          }
          if (results.length >= max) {
            resolve(results.slice(0, max));
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

  function getStockByItemId(itemId) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var entries = [];
        var tx = db.transaction(STORE_STOCK, "readonly");
        var index = tx.objectStore(STORE_STOCK).index("byItemId");
        var range = IDBKeyRange.only(itemId);
        var request = index.openCursor(range);
        request.onsuccess = function (event) {
          var cursor = event.target.result;
          if (cursor) {
            entries.push(cursor.value);
            cursor.continue();
            return;
          }
          getLocationMap()
            .then(function (locations) {
              var rows = entries.map(function (entry) {
                var location = locations[entry.locationId] || {};
                return {
                  locationId: entry.locationId,
                  code: location.code || "",
                  name: location.name || "",
                  qtyBase: typeof entry.qtyBase === "number" ? entry.qtyBase : 0,
                };
              });
              rows.sort(function (a, b) {
                var qtyDiff = b.qtyBase - a.qtyBase;
                if (qtyDiff !== 0) {
                  return qtyDiff;
                }
                return (a.code || "").localeCompare(b.code || "");
              });
              resolve(rows);
            })
            .catch(function (error) {
              reject(error);
            });
        };
        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function listUoms() {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var uoms = {};
        var tx = db.transaction(STORE_ITEMS, "readonly");
        var store = tx.objectStore(STORE_ITEMS);
        var request = store.openCursor();
        request.onsuccess = function (event) {
          var cursor = event.target.result;
          if (!cursor) {
            resolve(Object.keys(uoms).sort());
            return;
          }
          var item = cursor.value || {};
          var uom = String(item.base_uom || "").trim();
          if (uom) {
            uoms[uom] = true;
          }
          cursor.continue();
        };
        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function createLocalItem(data) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var record = Object.assign({}, data);
        var barcode = String(record.barcode || "").trim();
        var gtin = String(record.gtin || "").trim();
        if (!barcode) {
          reject({ code: "barcode_required" });
          return;
        }

        var tx = db.transaction([STORE_ITEMS, STORE_ITEM_CODES], "readwrite");
        var itemsStore = tx.objectStore(STORE_ITEMS);
        var codesStore = tx.objectStore(STORE_ITEM_CODES);
        var pending = 0;
        var hasError = false;
        var created = false;

        function fail(err) {
          if (hasError) {
            return;
          }
          hasError = true;
          try {
            tx.abort();
          } catch (abortError) {
            // ignore
          }
          reject(err);
        }

        function maybeCreate() {
          if (hasError || created || pending > 0) {
            return;
          }
          created = true;
          record.itemId = record.itemId;
          record.nameLower = String(record.name || "").toLowerCase();
          record.skuLower = String(record.sku || "").toLowerCase();
          record.gtinLower = String(record.gtin || "").toLowerCase();
          record.barcodes = record.barcodes || [barcode];
          itemsStore.put(record);
          codesStore.put({ code: barcode, itemId: record.itemId, kind: "barcode" });
          if (gtin && gtin !== barcode) {
            codesStore.put({ code: gtin, itemId: record.itemId, kind: "gtin" });
          }
        }

        function checkCode(code) {
          if (!code) {
            return;
          }
          pending += 1;
          var request = codesStore.get(code);
          request.onsuccess = function () {
            pending -= 1;
            if (request.result) {
              fail({ code: "barcode_exists", value: code });
              return;
            }
            maybeCreate();
          };
          request.onerror = function () {
            pending -= 1;
            fail(request.error);
          };
        }

        tx.oncomplete = function () {
          resolve(record);
        };
        tx.onerror = function () {
          if (!hasError) {
            reject(tx.error);
          }
        };

        checkCode(barcode);
        if (gtin && gtin !== barcode) {
          checkCode(gtin);
        }
        if (!gtin || gtin === barcode) {
          maybeCreate();
        }
      });
    });
  }

  function getTotalStockByItemId(itemId) {
    return getStockByItemId(itemId).then(function (rows) {
      return rows.reduce(function (sum, row) {
        return sum + (row.qtyBase || 0);
      }, 0);
    });
  }

  function listLocalItemsForExport() {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var items = [];
        var tx = db.transaction(STORE_ITEMS, "readonly");
        var store = tx.objectStore(STORE_ITEMS);
        var request = store.openCursor();
        request.onsuccess = function (event) {
          var cursor = event.target.result;
          if (!cursor) {
            resolve(items);
            return;
          }
          var item = cursor.value || {};
          if (item.created_on_device && !item.exported_at) {
            items.push(item);
          }
          cursor.continue();
        };
        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function markLocalItemsExported(itemIds, exportedAt) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var ids = Array.isArray(itemIds) ? itemIds : [];
        if (!ids.length) {
          resolve(true);
          return;
        }
        var tx = db.transaction(STORE_ITEMS, "readwrite");
        var store = tx.objectStore(STORE_ITEMS);
        var pending = ids.length;
        var done = false;
        tx.oncomplete = function () {
          if (!done) {
            resolve(true);
          }
        };
        tx.onerror = function () {
          reject(tx.error);
        };
        ids.forEach(function (id) {
          var request = store.get(id);
          request.onsuccess = function () {
            var item = request.result;
            if (item) {
              item.exported_at = exportedAt;
              store.put(item);
            }
            pending -= 1;
            if (pending === 0 && !done) {
              done = true;
              resolve(true);
            }
          };
          request.onerror = function () {
            reject(request.error);
          };
        });
      });
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

  function searchOrders(query) {
    var q = String(query || "").toLowerCase();
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var results = [];
        var request = db.transaction(STORE_ORDERS, "readonly")
          .objectStore(STORE_ORDERS)
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
            if (value.numberLower && value.numberLower.indexOf(q) !== -1) {
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

  function getOrderById(id) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var request = db.transaction(STORE_ORDERS, "readonly")
          .objectStore(STORE_ORDERS)
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

  function findLocationByCode(code) {
    var clean = String(code || "").trim().toLowerCase();
    if (!clean) {
      return Promise.resolve(null);
    }
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var tx = db.transaction(STORE_LOCATIONS, "readonly");
        var index = tx.objectStore(STORE_LOCATIONS).index("codeLower");
        var request = index.get(clean);
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
    getMetaExportedAt: getMetaExportedAt,
    searchItems: searchItems,
    listUoms: listUoms,
    createLocalItem: createLocalItem,
    findItemByCode: findItemByCode,
    searchPartners: searchPartners,
    searchLocations: searchLocations,
    searchOrders: searchOrders,
    getPartnerById: getPartnerById,
    getOrderById: getOrderById,
    getLocationById: getLocationById,
    findLocationByCode: findLocationByCode,
    getStockByItemId: getStockByItemId,
    getTotalStockByItemId: getTotalStockByItemId,
    listLocalItemsForExport: listLocalItemsForExport,
    markLocalItemsExported: markLocalItemsExported,
  };
})(window);
