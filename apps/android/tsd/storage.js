 (function (global) {
  "use strict";

  var DB_NAME = "tsd_app";
  var DB_VERSION = 8;
  var STORE_SETTINGS = "settings";
  var STORE_DOCS = "docs";
  var STORE_META = "meta";
  var STORE_ITEMS = "items";
  var STORE_ITEM_CODES = "itemCodes";
  var STORE_PARTNERS = "partners";
  var STORE_LOCATIONS = "locations";
  var STORE_UOMS = "uoms";
  var STORE_STOCK = "stock";
  var STORE_HU_STOCK = "huStock";
  var STORE_ORDERS = "orders";
  var STORE_ORDER_LINES = "orderLines";
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

  function loadPartnerMap() {
    return new Promise(function (resolve, reject) {
      var tx = db.transaction(STORE_PARTNERS, "readonly");
      var store = tx.objectStore(STORE_PARTNERS);
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

  function loadItemMap() {
    return new Promise(function (resolve, reject) {
      var tx = db.transaction(STORE_ITEMS, "readonly");
      var store = tx.objectStore(STORE_ITEMS);
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
        if (!database.objectStoreNames.contains(STORE_UOMS)) {
          var uomsStore = database.createObjectStore(STORE_UOMS, { keyPath: "uomId" });
          uomsStore.createIndex("codeLower", "codeLower", { unique: false });
          uomsStore.createIndex("nameLower", "nameLower", { unique: false });
        }
        if (!database.objectStoreNames.contains(STORE_ORDERS)) {
          var ordersStore = database.createObjectStore(STORE_ORDERS, { keyPath: "orderId" });
          ordersStore.createIndex("numberLower", "numberLower", { unique: false });
          ordersStore.createIndex("partnerId", "partnerId", { unique: false });
        }
        if (!database.objectStoreNames.contains(STORE_ORDER_LINES)) {
          var orderLinesStore = database.createObjectStore(STORE_ORDER_LINES, {
            keyPath: "lineId",
          });
          orderLinesStore.createIndex("byOrderId", "orderId", { unique: false });
        }
        if (!database.objectStoreNames.contains(STORE_STOCK)) {
          var stockStore = database.createObjectStore(STORE_STOCK, { autoIncrement: true });
          stockStore.createIndex("byItemId", "itemId", { unique: false });
          stockStore.createIndex("byLocationId", "locationId", { unique: false });
        }
        if (!database.objectStoreNames.contains(STORE_HU_STOCK)) {
          database.createObjectStore(STORE_HU_STOCK, { keyPath: "hu" });
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
    return Promise.resolve(true)
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

  function isObject(value) {
    return !!value && typeof value === "object" && !Array.isArray(value);
  }

  function isNonEmptyString(value) {
    return typeof value === "string" && value.trim() !== "";
  }

  function normalizeNumber(value) {
    var num = Number(value);
    return isNaN(num) ? 0 : num;
  }

  function buildImportError(message) {
    var err = new Error(message);
    err.code = "invalid_data";
    return err;
  }

  function validateTsdData(json) {
    if (!isObject(json)) {
      throw buildImportError("Некорректный JSON: ожидается объект.");
    }
    if (!isObject(json.meta)) {
      throw buildImportError("Отсутствует секция meta.");
    }
    var schemaVersionRaw = json.meta.schema_version;
    var schemaNormalized = null;
    if (typeof schemaVersionRaw === "number") {
      schemaNormalized = schemaVersionRaw === 1 ? "v1" : null;
    } else if (typeof schemaVersionRaw === "string") {
      var schemaTrimmed = schemaVersionRaw.trim().toLowerCase();
      if (schemaTrimmed === "1") {
        schemaNormalized = "v1";
      } else if (schemaTrimmed === "v1") {
        schemaNormalized = "v1";
      }
    }
    if (!schemaNormalized) {
      throw buildImportError(
        "Неподдерживаемая версия схемы: " + String(schemaVersionRaw)
      );
    }
    if (!isNonEmptyString(json.meta.exported_at)) {
      throw buildImportError("meta.exported_at должен быть непустой строкой.");
    }

    if (!isObject(json.catalog)) {
      throw buildImportError("Отсутствует секция catalog.");
    }
    if (!Array.isArray(json.catalog.items)) {
      throw buildImportError("catalog.items должен быть массивом.");
    }
    if (!Array.isArray(json.catalog.partners)) {
      throw buildImportError("catalog.partners должен быть массивом.");
    }
    if (!Array.isArray(json.catalog.locations)) {
      throw buildImportError("catalog.locations должен быть массивом.");
    }
    if (!Array.isArray(json.catalog.uoms)) {
      throw buildImportError("catalog.uoms должен быть массивом.");
    }

    if (!isObject(json.stock)) {
      throw buildImportError("Отсутствует секция stock.");
    }
    if (!isNonEmptyString(json.stock.exported_at)) {
      throw buildImportError("stock.exported_at должен быть непустой строкой.");
    }
    if (!Array.isArray(json.stock.rows)) {
      throw buildImportError("stock.rows должен быть массивом.");
    }

    if (!isObject(json.orders)) {
      throw buildImportError("Отсутствует секция orders.");
    }
    if (!Array.isArray(json.orders.orders)) {
      throw buildImportError("orders.orders должен быть массивом.");
    }
    if (!Array.isArray(json.orders.lines)) {
      throw buildImportError("orders.lines должен быть массивом.");
    }

    var huStock = null;
    if (json.hu_stock != null) {
      if (!isObject(json.hu_stock)) {
        throw buildImportError("hu_stock должен быть объектом.");
      }
      if (!isNonEmptyString(json.hu_stock.exported_at)) {
        throw buildImportError("hu_stock.exported_at должен быть непустой строкой.");
      }
      if (!Array.isArray(json.hu_stock.rows)) {
        throw buildImportError("hu_stock.rows должен быть массивом.");
      }
    }

    var uoms = json.catalog.uoms.map(function (uom, index) {
      if (!isObject(uom) || uom.id == null) {
        throw buildImportError("catalog.uoms[" + index + "].id обязателен.");
      }
      var code = String(uom.code || "").trim();
      var name = String(uom.name || "").trim();
      if (!code) {
        throw buildImportError("catalog.uoms[" + index + "].code обязателен.");
      }
      if (!name) {
        throw buildImportError("catalog.uoms[" + index + "].name обязателен.");
      }
      return {
        uomId: uom.id,
        code: code,
        name: name,
        codeLower: code.toLowerCase(),
        nameLower: name.toLowerCase(),
      };
    });

    var itemCodes = [];
    var items = json.catalog.items.map(function (item, index) {
      if (!isObject(item) || item.id == null) {
        throw buildImportError("catalog.items[" + index + "].id обязателен.");
      }
      var name = String(item.name || "").trim();
      if (!name) {
        throw buildImportError("catalog.items[" + index + "].name обязателен.");
      }
      var baseUom = String(item.base_uom_code || "").trim();
      if (!baseUom) {
        throw buildImportError(
          "catalog.items[" + index + "].base_uom_code обязателен."
        );
      }
      var barcode = String(item.barcode || "").trim();
      var gtin = String(item.gtin || "").trim();
      var sku = String(item.sku || "").trim();
      if (!sku) {
        sku = barcode;
      }
      var barcodes = [];
      if (barcode) {
        barcodes.push(barcode);
        itemCodes.push({ code: barcode, itemId: item.id, kind: "barcode" });
      }
      if (gtin && gtin.toLowerCase() !== barcode.toLowerCase()) {
        barcodes.push(gtin);
        itemCodes.push({ code: gtin, itemId: item.id, kind: "gtin" });
      }
      return {
        itemId: item.id,
        name: name,
        sku: sku || null,
        barcode: barcode || null,
        gtin: gtin || null,
        base_uom: baseUom,
        barcodes: barcodes,
        nameLower: name.toLowerCase(),
        skuLower: String(sku || "").toLowerCase(),
        gtinLower: String(gtin || "").toLowerCase(),
      };
    });

    var partners = json.catalog.partners.map(function (partner, index) {
      if (!isObject(partner) || partner.id == null) {
        throw buildImportError("catalog.partners[" + index + "].id обязателен.");
      }
      var name = String(partner.name || "").trim();
      if (!name) {
        throw buildImportError("catalog.partners[" + index + "].name обязателен.");
      }
      return {
        partnerId: partner.id,
        name: name,
        inn: partner.inn || null,
        code: partner.code || null,
        nameLower: name.toLowerCase(),
      };
    });

    var locations = json.catalog.locations.map(function (location, index) {
      if (!isObject(location) || location.id == null) {
        throw buildImportError("catalog.locations[" + index + "].id обязателен.");
      }
      var code = String(location.code || "").trim();
      var name = String(location.name || "").trim();
      if (!code) {
        throw buildImportError("catalog.locations[" + index + "].code обязателен.");
      }
      if (!name) {
        throw buildImportError("catalog.locations[" + index + "].name обязателен.");
      }
      return {
        locationId: location.id,
        code: code,
        name: name,
        codeLower: code.toLowerCase(),
        nameLower: name.toLowerCase(),
      };
    });

    var stockRows = json.stock.rows.map(function (row, index) {
      if (!isObject(row) || row.item_id == null || row.location_id == null) {
        throw buildImportError("stock.rows[" + index + "]: item_id и location_id обязательны.");
      }
      var hu = row.hu != null ? String(row.hu).trim() : "";
      return {
        itemId: row.item_id,
        locationId: row.location_id,
        qtyBase: normalizeNumber(row.qty),
        hu: hu || null,
      };
    });

    if (json.hu_stock != null) {
      var huExportedAt = String(json.hu_stock.exported_at || "").trim();
      var grouped = {};
      json.hu_stock.rows.forEach(function (row, index) {
        if (!isObject(row)) {
          throw buildImportError("hu_stock.rows[" + index + "] должен быть объектом.");
        }
        var huCode = String(row.hu || "").trim();
        if (!huCode) {
          throw buildImportError("hu_stock.rows[" + index + "].hu обязателен.");
        }
        if (row.item_id == null) {
          throw buildImportError("hu_stock.rows[" + index + "].item_id обязателен.");
        }
        var entry = {
          itemId: row.item_id,
          qtyBase: normalizeNumber(row.qty),
          locationId: row.location_id != null ? row.location_id : null,
        };
        if (!grouped[huCode]) {
          grouped[huCode] = [];
        }
        grouped[huCode].push(entry);
      });

      var entries = Object.keys(grouped).map(function (huCode) {
        return {
          hu: huCode,
          exportedAt: huExportedAt,
          rows: grouped[huCode],
        };
      });
      huStock = {
        exportedAt: huExportedAt,
        entries: entries,
      };
    }

    var orders = json.orders.orders.map(function (order, index) {
      if (!isObject(order) || order.id == null) {
        throw buildImportError("orders.orders[" + index + "].id обязателен.");
      }
      var orderRef = String(order.order_ref || "").trim();
      if (!orderRef) {
        throw buildImportError("orders.orders[" + index + "].order_ref обязателен.");
      }
      var partnerId = order.partner_id;
      if (partnerId == null) {
        throw buildImportError("orders.orders[" + index + "].partner_id обязателен.");
      }
      var status = String(order.status || "").trim();
      if (!status) {
        throw buildImportError("orders.orders[" + index + "].status обязателен.");
      }
      var createdAt = String(order.created_at || "").trim();
      if (!createdAt) {
        throw buildImportError("orders.orders[" + index + "].created_at обязателен.");
      }
      var plannedDate = String(order.planned_ship_date || "").trim();
      var shippedAt = String(order.shipped_at || "").trim();
      return {
        orderId: order.id,
        number: orderRef,
        partnerId: partnerId,
        plannedDate: plannedDate || null,
        shippedAt: shippedAt || null,
        createdAt: createdAt,
        status: status,
        numberLower: orderRef.toLowerCase(),
      };
    });

    var orderLines = json.orders.lines.map(function (line, index) {
      if (!isObject(line) || line.id == null) {
        throw buildImportError("orders.lines[" + index + "].id обязателен.");
      }
      var orderId = line.order_id;
      if (orderId == null) {
        throw buildImportError("orders.lines[" + index + "].order_id обязателен.");
      }
      var itemId = line.item_id;
      if (itemId == null) {
        throw buildImportError("orders.lines[" + index + "].item_id обязателен.");
      }
      var orderedQty = normalizeNumber(line.qty_ordered);
      var shippedQty = normalizeNumber(line.qty_shipped);
      return {
        lineId: line.id,
        orderId: orderId,
        itemId: itemId,
        orderedQty: orderedQty,
        shippedQty: shippedQty,
        barcode: line.barcode || line.gtin || null,
        itemName: line.name || null,
      };
    });

    return {
      schemaVersion: schemaNormalized,
      exportedAt: json.meta.exported_at,
      uoms: uoms,
      items: items,
      itemCodes: itemCodes,
      partners: partners,
      locations: locations,
      stockRows: stockRows,
      orders: orders,
      orderLines: orderLines,
      huStock: huStock,
    };
  }

  function importTsdData(json) {
    return init().then(function () {
      var normalized;
      try {
        normalized = validateTsdData(json);
      } catch (error) {
        return Promise.reject(error);
      }

      return new Promise(function (resolve, reject) {
        var tx = db.transaction(
          [
            STORE_META,
            STORE_ITEMS,
            STORE_ITEM_CODES,
            STORE_PARTNERS,
            STORE_LOCATIONS,
            STORE_UOMS,
            STORE_ORDERS,
            STORE_ORDER_LINES,
            STORE_STOCK,
            STORE_HU_STOCK,
          ],
          "readwrite"
        );
        var metaStore = tx.objectStore(STORE_META);
        var itemsStore = tx.objectStore(STORE_ITEMS);
        var codesStore = tx.objectStore(STORE_ITEM_CODES);
        var partnersStore = tx.objectStore(STORE_PARTNERS);
        var locationsStore = tx.objectStore(STORE_LOCATIONS);
        var uomsStore = tx.objectStore(STORE_UOMS);
        var ordersStore = tx.objectStore(STORE_ORDERS);
        var orderLinesStore = tx.objectStore(STORE_ORDER_LINES);
        var stockStore = tx.objectStore(STORE_STOCK);
        var huStockStore = tx.objectStore(STORE_HU_STOCK);

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
          clearStore(uomsStore),
          clearStore(ordersStore),
          clearStore(orderLinesStore),
          clearStore(stockStore),
          clearStore(huStockStore),
        ])
          .then(function () {
            metaStore.put({ key: "dataExportedAt", value: normalized.exportedAt });
            metaStore.put({ key: "schemaVersion", value: normalized.schemaVersion });

            normalized.uoms.forEach(function (uom) {
              uomsStore.put(uom);
            });

            normalized.items.forEach(function (item) {
              itemsStore.put(item);
            });

            normalized.itemCodes.forEach(function (code) {
              codesStore.put(code);
            });

            normalized.partners.forEach(function (partner) {
              partnersStore.put(partner);
            });

            normalized.locations.forEach(function (location) {
              locationsStore.put(location);
            });

            normalized.orders.forEach(function (order) {
              ordersStore.put(order);
            });

            normalized.orderLines.forEach(function (line) {
              orderLinesStore.put(line);
            });

            normalized.stockRows.forEach(function (row) {
              stockStore.put(row);
            });

            if (normalized.huStock && Array.isArray(normalized.huStock.entries)) {
              normalized.huStock.entries.forEach(function (entry) {
                huStockStore.put(entry);
              });
            }
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
      countStore(STORE_UOMS),
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
          uoms: results[2] || 0,
          items: results[3] || 0,
          partners: results[4] || 0,
          locations: results[5] || 0,
          orders: results[6] || 0,
          stock: results[7] || 0,
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
                  hu: entry.hu || null,
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

  function getHuStockByCode(hu) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var code = String(hu || "").trim();
        if (!code) {
          resolve(null);
          return;
        }
        var tx = db.transaction(STORE_HU_STOCK, "readonly");
        var store = tx.objectStore(STORE_HU_STOCK);
        var request = store.get(code);
        request.onsuccess = function () {
          resolve(request.result || null);
        };
        request.onerror = function () {
          reject(request.error);
        };
      });
    });
  }

  function getItemsByIds(itemIds) {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var ids = Array.isArray(itemIds) ? itemIds.slice() : [];
        if (!ids.length) {
          resolve({});
          return;
        }
        var map = {};
        var pending = ids.length;
        var tx = db.transaction(STORE_ITEMS, "readonly");
        var store = tx.objectStore(STORE_ITEMS);
        tx.onerror = function () {
          reject(tx.error);
        };
        ids.forEach(function (id) {
          var request = store.get(id);
          request.onsuccess = function () {
            if (request.result) {
              map[id] = request.result;
            }
            pending -= 1;
            if (pending === 0) {
              resolve(map);
            }
          };
          request.onerror = function () {
            reject(request.error);
          };
        });
      });
    });
  }

  function listUomsFromItems() {
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
  }

  function listUoms() {
    return init().then(function () {
      return new Promise(function (resolve, reject) {
        var uoms = {};
        var tx;
        try {
          tx = db.transaction(STORE_UOMS, "readonly");
        } catch (error) {
          resolve(null);
          return;
        }
        var store = tx.objectStore(STORE_UOMS);
        var request = store.openCursor();
        request.onsuccess = function (event) {
          var cursor = event.target.result;
          if (!cursor) {
            resolve(Object.keys(uoms).sort());
            return;
          }
          var uom = cursor.value || {};
          var code = String(uom.code || "").trim();
          if (code) {
            uoms[code] = true;
          }
          cursor.continue();
        };
        request.onerror = function () {
          reject(request.error);
        };
      }).then(function (result) {
        if (Array.isArray(result) && result.length) {
          return result;
        }
        return listUomsFromItems();
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

  function listOrders(query) {
    var q = "";
    if (typeof query === "string") {
      q = query;
    } else if (query && typeof query.q === "string") {
      q = query.q;
    }
    var needle = String(q || "").toLowerCase();
    return init().then(function () {
      return loadPartnerMap().then(function (partners) {
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
            var order = cursor.value || {};
            var partner = partners[order.partnerId] || {};
            var partnerName = partner.name || "";
            var partnerInn = partner.inn || "";
            var number = order.number || order.orderNumber || "";
            var candidate =
              String(order.numberLower || String(number || "").toLowerCase()) +
              "|" +
              String(partnerName || "").toLowerCase() +
              "|" +
              String(partnerInn || "").toLowerCase();
            if (!needle || candidate.indexOf(needle) !== -1) {
              results.push({
                orderId: order.orderId,
                number: number,
                partnerId: order.partnerId || null,
                partnerName: partnerName,
                partnerInn: partnerInn,
                plannedDate: order.plannedDate || order.planned_date || null,
                shippedAt: order.shippedAt || order.shipped_at || null,
                createdAt: order.createdAt || order.created_at || null,
                status: order.status || null,
              });
            }
            cursor.continue();
          };

          request.onerror = function () {
            reject(request.error);
          };
        });
      });
    });
  }

  function listOrderLines(orderId) {
    return init().then(function () {
      return Promise.all([loadItemMap(), new Promise(function (resolve, reject) {
        var lines = [];
        var tx = db.transaction(STORE_ORDER_LINES, "readonly");
        var index = tx.objectStore(STORE_ORDER_LINES).index("byOrderId");
        var range = IDBKeyRange.only(orderId);
        var request = index.openCursor(range);
        request.onsuccess = function (event) {
          var cursor = event.target.result;
          if (cursor) {
            lines.push(cursor.value);
            cursor.continue();
            return;
          }
          resolve(lines);
        };
        request.onerror = function () {
          reject(request.error);
        };
      })]).then(function (results) {
        var items = results[0] || {};
        var rawLines = results[1] || [];
        return rawLines.map(function (line) {
          var item = items[line.itemId] || {};
          var barcode = line.barcode;
          if (!barcode) {
            if (Array.isArray(item.barcodes) && item.barcodes.length) {
              barcode = item.barcodes[0];
            } else {
              barcode = item.gtin || "";
            }
          }
          var orderedQty = Number(line.orderedQty) || 0;
          var shippedQty = Number(line.shippedQty) || 0;
          var leftQty = Number(line.leftQty);
          if (isNaN(leftQty)) {
            leftQty = Math.max(orderedQty - shippedQty, 0);
          }
          return {
            orderId: line.orderId,
            itemId: line.itemId,
            itemName: line.itemName || item.name || "",
            barcode: barcode,
            orderedQty: orderedQty,
            shippedQty: shippedQty,
            leftQty: leftQty,
          };
        });
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
    listOrders: listOrders,
    listOrderLines: listOrderLines,
    getPartnerById: getPartnerById,
    getOrderById: getOrderById,
    getLocationById: getLocationById,
    findLocationByCode: findLocationByCode,
    getStockByItemId: getStockByItemId,
    getHuStockByCode: getHuStockByCode,
    getItemsByIds: getItemsByIds,
    getTotalStockByItemId: getTotalStockByItemId,
    listLocalItemsForExport: listLocalItemsForExport,
    markLocalItemsExported: markLocalItemsExported,
  };
})(window);
