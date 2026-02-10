(function () {
  "use strict";

  var DEFAULT_DEDUP_MS = 120;
  var DEFAULT_INPUT_DELAY_MS = 60;
  var DEFAULT_KEY_DELAY_MS = 80;

  function nowTs() {
    return Date.now();
  }

  function normalizeValue(value) {
    var trimmed = String(value || "").trim();
    return trimmed ? trimmed : "";
  }

  function normalizeMode(value) {
    var mode = String(value || "").toLowerCase();
    if (mode === "keyboard" || mode === "intent" || mode === "auto") {
      return mode;
    }
    return "auto";
  }

  function isBridgeAvailable() {
    return (
      window.FlowStockAndroidBridge &&
      typeof window.FlowStockAndroidBridge.subscribeScans === "function"
    );
  }

  function normalizeScanPayload(payload) {
    var ts = nowTs();
    if (payload == null) {
      return { value: "", ts: ts, raw: payload };
    }
    if (typeof payload === "string") {
      return { value: normalizeValue(payload), ts: ts, raw: payload };
    }
    if (typeof payload === "object") {
      var value =
        payload.value ||
        payload.data ||
        payload.barcode ||
        payload.scanData ||
        payload.scan_data ||
        payload.code ||
        payload.text ||
        "";
      var symbology =
        payload.symbology || payload.symbologyName || payload.type || payload.format || undefined;
      return {
        value: normalizeValue(value),
        symbology: symbology || undefined,
        ts: payload.ts || ts,
        raw: payload,
      };
    }
    return { value: normalizeValue(payload), ts: ts, raw: payload };
  }

  function createDedupe(windowMs) {
    var lastValue = "";
    var lastAt = 0;
    var threshold = windowMs || DEFAULT_DEDUP_MS;
    return function (value, ts) {
      var time = typeof ts === "number" ? ts : nowTs();
      if (value && value === lastValue && time - lastAt < threshold) {
        return false;
      }
      lastValue = value;
      lastAt = time;
      return true;
    };
  }

  function createKeyboardWedgeScanner(options) {
    var config = options || {};
    var scanSink = config.scanSink || null;
    var canScan = typeof config.canScan === "function" ? config.canScan : function () { return true; };
    var onScan = null;
    var onError = null;
    var buffer = "";
    var bufferTimer = null;
    var dedupe = createDedupe(config.dedupeMs);
    var inputDelayMs = config.inputDelayMs || DEFAULT_INPUT_DELAY_MS;
    var keyDelayMs = config.keyDelayMs || DEFAULT_KEY_DELAY_MS;
    var docKeydownHandler = null;
    var targetReadTimer = null;
    var readOnlyTimers = new WeakMap();
    var docInputHandler = null;
    var sinkInputHandler = null;
    var sinkKeydownHandler = null;

    function emit(value, meta) {
      var trimmed = normalizeValue(value);
      if (!trimmed) {
        return;
      }
      var ts = nowTs();
      if (!dedupe(trimmed, ts)) {
        return;
      }
      if (onScan) {
        onScan({
          value: trimmed,
          symbology: undefined,
          raw: meta || null,
          ts: ts,
          source: "keyboard",
        });
      }
    }

    function clearBuffer() {
      buffer = "";
      if (bufferTimer) {
        clearTimeout(bufferTimer);
        bufferTimer = null;
      }
      if (scanSink) {
        scanSink.value = "";
      }
    }

    function flushBuffer(reason) {
      if (!buffer) {
        return;
      }
      var value = buffer;
      clearBuffer();
      emit(value, { reason: reason });
    }

    function scheduleFlush(delay, reason) {
      if (bufferTimer) {
        clearTimeout(bufferTimer);
      }
      bufferTimer = window.setTimeout(function () {
        flushBuffer(reason);
      }, delay);
    }

    function scheduleTargetRead(target, reason) {
      if (targetReadTimer) {
        clearTimeout(targetReadTimer);
      }
      targetReadTimer = window.setTimeout(function () {
        if (!canScan()) {
          return;
        }
        if (!target || typeof target.value !== "string") {
          return;
        }
        var value = target.value || "";
        if (!value) {
          return;
        }
        buffer = value;
        flushBuffer(reason);
      }, inputDelayMs);
    }

    function unlockScanTarget(target) {
      if (!target || typeof target.getAttribute !== "function") {
        return;
      }
      if (target.getAttribute("data-scan-readonly") !== "1") {
        return;
      }
      if (!target.readOnly) {
        return;
      }
      target.readOnly = false;
      var existing = readOnlyTimers.get(target);
      if (existing) {
        clearTimeout(existing);
      }
      var timer = window.setTimeout(function () {
        target.readOnly = true;
        readOnlyTimers.delete(target);
      }, inputDelayMs + 80);
      readOnlyTimers.set(target, timer);
    }

    function isScanAllowedTarget(target) {
      if (!target || typeof target.getAttribute !== "function") {
        return false;
      }
      return target.getAttribute("data-scan-allow") === "1";
    }

    function isEditableTarget(target) {
      if (!target) {
        return false;
      }
      if (scanSink && target === scanSink) {
        return false;
      }
      if (isScanAllowedTarget(target)) {
        return false;
      }
      if (target.isContentEditable) {
        return true;
      }
      var tag = target.tagName;
      if (!tag) {
        return false;
      }
      tag = tag.toUpperCase();
      if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") {
        return true;
      }
      return false;
    }

    function handleSinkInput() {
      if (!scanSink) {
        return;
      }
      if (!canScan()) {
        return;
      }
      var value = scanSink.value || "";
      if (!value) {
        return;
      }
      buffer = value;
      scheduleFlush(inputDelayMs, "input");
    }

    function handleSinkKeydown(event) {
      if (!scanSink) {
        return;
      }
      if (!canScan()) {
        return;
      }
      if (event.key === "Enter") {
        if (!buffer && scanSink.value) {
          buffer = scanSink.value;
        }
        flushBuffer("enter");
        event.preventDefault();
      }
    }

    function handleDocKeydown(event) {
      if (!canScan()) {
        return;
      }
      var isScanTarget = isScanAllowedTarget(event.target);
      if (scanSink && document.activeElement === scanSink) {
        return;
      }
      if (isEditableTarget(event.target)) {
        return;
      }
      if (event.key === "Enter") {
        if (isScanTarget) {
          unlockScanTarget(event.target);
        }
        if (!buffer && isScanTarget && event.target && event.target.value) {
          buffer = event.target.value;
        }
        if (buffer) {
          flushBuffer("enter");
          event.preventDefault();
        }
        return;
      }
      if (
        isScanTarget &&
        (event.key === "Unidentified" || event.keyCode === 229 || event.which === 229)
      ) {
        unlockScanTarget(event.target);
        scheduleTargetRead(event.target, "ime");
        return;
      }
      if (
        event.key &&
        event.key.length === 1 &&
        !event.altKey &&
        !event.ctrlKey &&
        !event.metaKey
      ) {
        if (isScanTarget) {
          unlockScanTarget(event.target);
        }
        buffer += event.key;
        scheduleFlush(keyDelayMs, "keydown");
      }
    }

    function handleDocInput(event) {
      if (!canScan()) {
        return;
      }
      var target = event.target;
      if (!target || target === scanSink) {
        return;
      }
      if (!isScanAllowedTarget(target)) {
        return;
      }
      var value = target.value || "";
      if (!value) {
        return;
      }
      buffer = value;
      scheduleFlush(inputDelayMs, "input");
    }

    function start() {
      stop();
      if (scanSink) {
        sinkInputHandler = handleSinkInput;
        sinkKeydownHandler = handleSinkKeydown;
        scanSink.addEventListener("input", sinkInputHandler);
        scanSink.addEventListener("keydown", sinkKeydownHandler);
      }
      docKeydownHandler = handleDocKeydown;
      document.addEventListener("keydown", docKeydownHandler, true);
      docInputHandler = handleDocInput;
      document.addEventListener("input", docInputHandler, true);
    }

    function stop() {
      if (scanSink && sinkInputHandler) {
        scanSink.removeEventListener("input", sinkInputHandler);
        sinkInputHandler = null;
      }
      if (scanSink && sinkKeydownHandler) {
        scanSink.removeEventListener("keydown", sinkKeydownHandler);
        sinkKeydownHandler = null;
      }
      if (docKeydownHandler) {
        document.removeEventListener("keydown", docKeydownHandler, true);
        docKeydownHandler = null;
      }
      if (docInputHandler) {
        document.removeEventListener("input", docInputHandler, true);
        docInputHandler = null;
      }
      if (targetReadTimer) {
        clearTimeout(targetReadTimer);
        targetReadTimer = null;
      }
      clearBuffer();
    }

    return {
      type: "keyboard",
      start: start,
      stop: stop,
      onScan: function (handler) {
        onScan = handler;
      },
      onError: function (handler) {
        onError = handler;
      },
      emitError: function (error) {
        if (onError) {
          onError(error);
        }
      },
    };
  }

  function createAndroidIntentScanner(options) {
    var config = options || {};
    var canScan = typeof config.canScan === "function" ? config.canScan : function () { return true; };
    var onScan = null;
    var onError = null;
    var dedupe = createDedupe(config.dedupeMs);
    var subscription = null;

    function handlePayload(payload) {
      if (!canScan()) {
        return;
      }
      var scan = normalizeScanPayload(payload);
      if (!scan.value) {
        return;
      }
      var ts = scan.ts || nowTs();
      if (!dedupe(scan.value, ts)) {
        return;
      }
      scan.ts = ts;
      scan.source = "intent";
      if (onScan) {
        onScan(scan);
      }
    }

    function start() {
      stop();
      if (!isBridgeAvailable()) {
        if (onError) {
          onError(new Error("INTENT_BRIDGE_UNAVAILABLE"));
        }
        return;
      }
      try {
        subscription = window.FlowStockAndroidBridge.subscribeScans(handlePayload);
      } catch (error) {
        if (onError) {
          onError(error);
        }
      }
    }

    function stop() {
      if (subscription) {
        try {
          if (typeof subscription === "function") {
            subscription();
          } else if (
            window.FlowStockAndroidBridge &&
            typeof window.FlowStockAndroidBridge.unsubscribeScans === "function"
          ) {
            window.FlowStockAndroidBridge.unsubscribeScans(subscription);
          } else if (
            window.FlowStockAndroidBridge &&
            typeof window.FlowStockAndroidBridge.stopScans === "function"
          ) {
            window.FlowStockAndroidBridge.stopScans();
          }
        } catch (error) {
          if (onError) {
            onError(error);
          }
        }
      }
      subscription = null;
    }

    return {
      type: "intent",
      start: start,
      stop: stop,
      onScan: function (handler) {
        onScan = handler;
      },
      onError: function (handler) {
        onError = handler;
      },
    };
  }

  function selectProvider(mode) {
    var normalized = normalizeMode(mode);
    if (normalized === "keyboard") {
      return "keyboard";
    }
    if (normalized === "intent") {
      return isBridgeAvailable() ? "intent" : "keyboard";
    }
    return isBridgeAvailable() ? "intent" : "keyboard";
  }

  function createScannerManager(options) {
    var config = options || {};
    var scanSink = config.scanSink || null;
    var canScan = typeof config.canScan === "function" ? config.canScan : function () { return true; };
    var mode = normalizeMode(config.mode);
    var providerType = null;
    var provider = null;
    var scanHandler = null;
    var errorHandler = null;
    var keyboardScanner = createKeyboardWedgeScanner({
      scanSink: scanSink,
      canScan: canScan,
      dedupeMs: config.dedupeMs,
      inputDelayMs: config.inputDelayMs,
      keyDelayMs: config.keyDelayMs,
    });
    var intentScanner = createAndroidIntentScanner({
      canScan: canScan,
      dedupeMs: config.dedupeMs,
    });

    function attachProvider(nextProvider) {
      if (!nextProvider) {
        return;
      }
      provider = nextProvider;
      provider.onScan(function (scan) {
        if (scanHandler) {
          scanHandler(scan);
        }
      });
      provider.onError(function (error) {
        if (errorHandler) {
          errorHandler(error);
        }
      });
    }

    function start() {
      stop();
      providerType = selectProvider(mode);
      if (providerType === "intent") {
        attachProvider(intentScanner);
      } else {
        attachProvider(keyboardScanner);
      }
      if (provider && provider.start) {
        provider.start();
      }
    }

    function stop() {
      if (provider && provider.stop) {
        provider.stop();
      }
      provider = null;
      providerType = null;
    }

    function setMode(nextMode) {
      mode = normalizeMode(nextMode);
      start();
    }

    function setHandler(handler) {
      scanHandler = handler || null;
    }

    function setErrorHandler(handler) {
      errorHandler = handler || null;
    }

    function focus() {
      if (scanSink) {
        scanSink.value = "";
        scanSink.focus();
      }
    }

    return {
      start: start,
      stop: stop,
      setMode: setMode,
      getMode: function () {
        return mode;
      },
      getProviderType: function () {
        return providerType || selectProvider(mode);
      },
      setHandler: setHandler,
      setErrorHandler: setErrorHandler,
      focus: focus,
    };
  }

  window.FlowStockScanner = {
    createScannerManager: createScannerManager,
    _test: {
      normalizeScanPayload: normalizeScanPayload,
      createDedupe: createDedupe,
      selectProvider: selectProvider,
      normalizeMode: normalizeMode,
    },
  };
})();
