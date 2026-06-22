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

  function clonePlain(value) {
    if (value == null) {
      return value;
    }
    if (typeof value !== "object") {
      return value;
    }
    try {
      return JSON.parse(JSON.stringify(value));
    } catch (error) {
      return String(value);
    }
  }

  function getCodePoints(value) {
    var text = String(value == null ? "" : value);
    var points = [];
    for (var i = 0; i < text.length; i += 1) {
      var hex = text.charCodeAt(i).toString(16).toUpperCase();
      while (hex.length < 4) {
        hex = "0" + hex;
      }
      points.push(hex);
    }
    return points;
  }

  function describeTarget(target) {
    if (!target) {
      return null;
    }
    var value = typeof target.value === "string" ? target.value : "";
    return {
      tagName: target.tagName || "",
      id: target.id || "",
      scanAllow:
        typeof target.getAttribute === "function"
          ? target.getAttribute("data-scan-allow") || ""
          : "",
      valueLength: value.length,
      valueSnapshot: value,
    };
  }

  function describeActiveElement() {
    if (typeof document === "undefined") {
      return null;
    }
    return describeTarget(document.activeElement);
  }

  function normalizeTerminator(key) {
    if (key === "Enter") {
      return "Enter";
    }
    if (key === "Tab") {
      return "Tab";
    }
    if (key === "\r") {
      return "CR";
    }
    if (key === "\n") {
      return "LF";
    }
    return key ? "Unknown" : "None";
  }

  function mapFlushReason(reason) {
    if (reason === "enter") {
      return "enter";
    }
    if (reason === "tab") {
      return "tab";
    }
    if (reason === "input") {
      return "input-timeout";
    }
    if (reason === "keydown") {
      return "keydown-timeout";
    }
    if (reason === "ime") {
      return "ime";
    }
    if (reason === "paste") {
      return "paste";
    }
    if (reason === "intent") {
      return "intent";
    }
    return reason || "unknown";
  }

  function eventTelemetry(event, extra) {
    var value = event && event.data != null ? event.data : event && event.key != null ? event.key : "";
    var payload = {
      eventType: event && event.type ? event.type : "",
      key: event && event.key != null ? event.key : undefined,
      code: event && event.code != null ? event.code : undefined,
      keyCode: event && event.keyCode != null ? event.keyCode : undefined,
      which: event && event.which != null ? event.which : undefined,
      inputType: event && event.inputType != null ? event.inputType : undefined,
      data: event && event.data != null ? event.data : undefined,
      isComposing: !!(event && event.isComposing),
      repeat: !!(event && event.repeat),
      altKey: !!(event && event.altKey),
      ctrlKey: !!(event && event.ctrlKey),
      shiftKey: !!(event && event.shiftKey),
      metaKey: !!(event && event.metaKey),
      target: describeTarget(event && event.target),
      activeElement: describeActiveElement(),
      visibilityState:
        typeof document !== "undefined" && document.visibilityState
          ? document.visibilityState
          : "",
      documentHasFocus:
        typeof document !== "undefined" && typeof document.hasFocus === "function"
          ? document.hasFocus()
          : undefined,
      escapedValue: JSON.stringify(String(value == null ? "" : value)),
      unicodeCodePoints: getCodePoints(value),
      hexBytes: getCodePoints(value),
    };
    if (extra) {
      Object.keys(extra).forEach(function (key) {
        payload[key] = extra[key];
      });
    }
    return payload;
  }

  function notifyDiagnostic(observer, type, detail) {
    if (typeof observer !== "function") {
      return;
    }
    try {
      observer({
        type: type,
        timestamp: nowTs(),
        detail: clonePlain(detail || {}),
      });
    } catch (error) {
      // Diagnostic observers must never affect the scanner transport.
    }
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
    var getDiagnosticObserver =
      typeof config.getDiagnosticObserver === "function"
        ? config.getDiagnosticObserver
        : function () {
            return null;
          };
    var onScan = null;
    var onError = null;
    var buffer = "";
    var bufferStartAt = 0;
    var characterEventCount = 0;
    var inputEventCount = 0;
    var attemptSeq = 0;
    var scanSeq = 0;
    var activeAttemptId = "";
    var pendingObservedTerminator = "";
    var lastAcceptedScanByValue = {};
    var bufferTimer = null;
    var dedupe = createDedupe(config.dedupeMs);
    var inputDelayMs = config.inputDelayMs || DEFAULT_INPUT_DELAY_MS;
    var keyDelayMs = config.keyDelayMs || DEFAULT_KEY_DELAY_MS;
    var docKeydownHandler = null;
    var docKeyupHandler = null;
    var docBeforeInputHandler = null;
    var docRawInputHandler = null;
    var docPasteHandler = null;
    var docCompositionStartHandler = null;
    var docCompositionUpdateHandler = null;
    var docCompositionEndHandler = null;
    var docFocusHandler = null;
    var docBlurHandler = null;
    var docVisibilityHandler = null;
    var pendingTargetRead = null;
    var readOnlyTimers = new WeakMap();
    var docInputHandler = null;
    var sinkInputHandler = null;
    var sinkKeydownHandler = null;

    function hasDiagnosticObserver() {
      return typeof getDiagnosticObserver() === "function";
    }

    function notify(type, detail) {
      var observer = getDiagnosticObserver();
      if (typeof observer !== "function") {
        return;
      }
      notifyDiagnostic(observer, type, detail);
    }

    function notifyRaw(type, event, extra) {
      var observer = getDiagnosticObserver();
      if (typeof observer !== "function") {
        return;
      }
      notifyDiagnostic(observer, type, eventTelemetry(event, extra));
    }

    function ensureAttemptId() {
      if (!activeAttemptId) {
        attemptSeq += 1;
        activeAttemptId = "keyboard-" + attemptSeq;
      }
      return activeAttemptId;
    }

    function nextScanId() {
      scanSeq += 1;
      return ensureAttemptId() + "-scan-" + scanSeq;
    }

    function resetAttempt() {
      activeAttemptId = "";
      pendingObservedTerminator = "";
    }

    function emit(value, meta) {
      var trimmed = normalizeValue(value);
      if (!trimmed) {
        return;
      }
      var ts = nowTs();
      var attemptId = (meta && meta.attemptId) || ensureAttemptId();
      var scanId = (meta && meta.scanId) || nextScanId();
      var eventBufferStartAt = meta && meta.bufferStartAt ? meta.bufferStartAt : bufferStartAt;
      var eventCharacterCount =
        meta && typeof meta.characterEventCount === "number"
          ? meta.characterEventCount
          : characterEventCount;
      var eventInputCount =
        meta && typeof meta.inputEventCount === "number" ? meta.inputEventCount : inputEventCount;
      if (!dedupe(trimmed, ts)) {
        var duplicateOf = lastAcceptedScanByValue[trimmed] || {};
        notify("dedupe-rejected", {
          providerType: "keyboard",
          attemptId: attemptId,
          scanId: scanId,
          duplicateOfScanId: duplicateOf.scanId || undefined,
          duplicateOfAttemptId: duplicateOf.attemptId || undefined,
          dedupeAccepted: false,
          value: trimmed,
          rawValue: value,
          duplicateAt: ts,
        });
        if (!(meta && meta.keepAttemptOpen)) {
          resetAttempt();
        }
        return;
      }
      notify("dedupe-accepted", {
        providerType: "keyboard",
        attemptId: attemptId,
        scanId: scanId,
        dedupeAccepted: true,
        value: trimmed,
        rawValue: value,
      });
      lastAcceptedScanByValue[trimmed] = {
        attemptId: attemptId,
        scanId: scanId,
        acceptedAt: ts,
      };
      notify("scan-emitted", {
        providerType: "keyboard",
        attemptId: attemptId,
        scanId: scanId,
        source: "keyboard",
        value: trimmed,
        rawValue: value,
        normalizedValue: trimmed,
        symbology: undefined,
        bufferStartAt: eventBufferStartAt || undefined,
        bufferEndAt: ts,
        inputDurationMs: eventBufferStartAt ? ts - eventBufferStartAt : undefined,
        characterEventCount: eventCharacterCount,
        inputEventCount: eventInputCount,
        flushReason: mapFlushReason(meta && meta.reason),
        terminator: meta && meta.terminator ? meta.terminator : "None",
      });
      if (onScan) {
        onScan({
          value: trimmed,
          symbology: undefined,
          raw: meta || null,
          ts: ts,
          source: "keyboard",
          attemptId: attemptId,
          scanId: scanId,
        });
      }
      bufferStartAt = 0;
      characterEventCount = 0;
      inputEventCount = 0;
      if (!(meta && meta.keepAttemptOpen)) {
        resetAttempt();
      }
    }

    function clearBuffer() {
      buffer = "";
      bufferStartAt = 0;
      characterEventCount = 0;
      inputEventCount = 0;
      pendingObservedTerminator = "";
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
      var attemptId = ensureAttemptId();
      var scanId = nextScanId();
      var terminator =
        pendingObservedTerminator ||
        (reason === "enter" ? "Enter" : reason === "tab" ? "Tab" : "None");
      notify("flush-requested", {
        providerType: "keyboard",
        attemptId: attemptId,
        scanId: scanId,
        reason: mapFlushReason(reason),
        terminator: terminator,
        buffer: value,
      });
      var meta = {
        reason: reason,
        flushReason: mapFlushReason(reason),
        terminator: terminator,
        attemptId: attemptId,
        scanId: scanId,
        bufferStartAt: bufferStartAt,
        characterEventCount: characterEventCount,
        inputEventCount: inputEventCount,
        keepAttemptOpen: true,
      };
      clearBuffer();
      emit(value, meta);
      notify("flush-completed", {
        providerType: "keyboard",
        attemptId: attemptId,
        scanId: scanId,
        reason: mapFlushReason(reason),
        terminator: terminator,
        value: value,
      });
      resetAttempt();
    }

    function scheduleFlush(delay, reason) {
      if (bufferTimer) {
        clearTimeout(bufferTimer);
      }
      notify("flush-requested", {
        providerType: "keyboard",
        attemptId: buffer ? ensureAttemptId() : activeAttemptId || undefined,
        reason: mapFlushReason(reason),
        delayMs: delay,
        buffer: buffer,
      });
      bufferTimer = window.setTimeout(function () {
        flushBuffer(reason);
      }, delay);
    }

    function clearPendingTargetRead(target) {
      var pending = pendingTargetRead;
      if (!pending) {
        return false;
      }
      if (target && pending.target !== target) {
        return false;
      }
      pending.cancelled = true;
      if (pending.timerId) {
        clearTimeout(pending.timerId);
      }
      if (pendingTargetRead === pending) {
        pendingTargetRead = null;
      }
      return true;
    }

    function scheduleTargetRead(target, reason) {
      clearPendingTargetRead();
      var pending = {
        target: target,
        reason: reason,
        timerId: null,
        cancelled: false,
      };
      pending.timerId = window.setTimeout(function () {
        if (pendingTargetRead !== pending || pending.cancelled) {
          return;
        }
        pendingTargetRead = null;
        pending.cancelled = true;
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
        ensureAttemptId();
        bufferStartAt = bufferStartAt || nowTs();
        inputEventCount += 1;
        notify("buffer-replace", {
          providerType: "keyboard",
          attemptId: activeAttemptId,
          reason: reason,
          value: value,
        });
        flushBuffer(reason);
      }, inputDelayMs);
      pendingTargetRead = pending;
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
      ensureAttemptId();
      bufferStartAt = bufferStartAt || nowTs();
      inputEventCount += 1;
      notify("buffer-replace", {
        providerType: "keyboard",
        attemptId: activeAttemptId,
        reason: "input",
        value: value,
      });
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
          ensureAttemptId();
          bufferStartAt = bufferStartAt || nowTs();
          notify("buffer-replace", {
            providerType: "keyboard",
            attemptId: activeAttemptId,
            reason: "enter",
            value: buffer,
          });
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
      if (buffer && event.key === "Tab") {
        pendingObservedTerminator = "Tab";
      }
      if (hasDiagnosticObserver()) {
        var printable =
          event.key &&
          event.key.length === 1 &&
          !event.altKey &&
          !event.ctrlKey &&
          !event.metaKey;
        var rawAttemptId =
          buffer || printable || event.key === "Enter" || event.key === "Tab" ? ensureAttemptId() : "";
        notifyRaw("raw-keydown", event, {
          attemptId: rawAttemptId || undefined,
          buffer: buffer,
          terminator: normalizeTerminator(event.key),
          tabReceived: event.key === "Tab",
          pendingObservedTerminator: pendingObservedTerminator || undefined,
        });
      }
      if (scanSink && document.activeElement === scanSink) {
        return;
      }
      if (isEditableTarget(event.target)) {
        return;
      }
      if (event.key === "Enter") {
        if (isScanTarget) {
          unlockScanTarget(event.target);
          var targetValue = event.target && event.target.value ? event.target.value : "";
          if (targetValue) {
            clearPendingTargetRead(event.target);
            ensureAttemptId();
            bufferStartAt = bufferStartAt || nowTs();
            inputEventCount += 1;
            notify("buffer-replace", {
              providerType: "keyboard",
              attemptId: activeAttemptId,
              reason: "enter",
              value: targetValue,
            });
            emit(targetValue, {
              reason: "enter",
              flushReason: "enter",
              terminator: "Enter",
              attemptId: activeAttemptId,
              scanId: nextScanId(),
            });
            event.preventDefault();
          }
          return;
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
          return;
        }
        if (!bufferStartAt) {
          bufferStartAt = nowTs();
        }
        ensureAttemptId();
        buffer += event.key;
        characterEventCount += 1;
        notify("buffer-append", {
          providerType: "keyboard",
          attemptId: activeAttemptId,
          key: event.key,
          buffer: buffer,
        });
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
      if (isScanAllowedTarget(target)) {
        return;
      }
    }

    function start() {
      stop();
      notify("provider-started", {
        providerType: "keyboard",
        inputDelayMs: inputDelayMs,
        keyDelayMs: keyDelayMs,
        dedupeMs: config.dedupeMs || DEFAULT_DEDUP_MS,
      });
      if (scanSink) {
        sinkInputHandler = handleSinkInput;
        sinkKeydownHandler = handleSinkKeydown;
        scanSink.addEventListener("input", sinkInputHandler);
        scanSink.addEventListener("keydown", sinkKeydownHandler);
      }
      docKeyupHandler = function (event) {
        notifyRaw("raw-keyup", event, { attemptId: activeAttemptId || undefined, buffer: buffer });
      };
      docBeforeInputHandler = function (event) {
        notifyRaw("raw-beforeinput", event, { attemptId: activeAttemptId || undefined, buffer: buffer });
      };
      docRawInputHandler = function (event) {
        notifyRaw("raw-input", event, { attemptId: activeAttemptId || undefined, buffer: buffer });
      };
      docPasteHandler = function (event) {
        notifyRaw("raw-paste", event, { attemptId: activeAttemptId || undefined, buffer: buffer });
      };
      docCompositionStartHandler = function (event) {
        notifyRaw("raw-compositionstart", event, { attemptId: activeAttemptId || undefined, buffer: buffer });
      };
      docCompositionUpdateHandler = function (event) {
        notifyRaw("raw-compositionupdate", event, { attemptId: activeAttemptId || undefined, buffer: buffer });
      };
      docCompositionEndHandler = function (event) {
        notifyRaw("raw-compositionend", event, { attemptId: activeAttemptId || undefined, buffer: buffer });
      };
      docFocusHandler = function (event) {
        notifyRaw("focus", event, { attemptId: activeAttemptId || undefined, buffer: buffer });
      };
      docBlurHandler = function (event) {
        notifyRaw("blur", event, { attemptId: activeAttemptId || undefined, buffer: buffer });
      };
      docVisibilityHandler = function () {
        notify("visibilitychange", {
          attemptId: activeAttemptId || undefined,
          visibilityState:
            typeof document !== "undefined" && document.visibilityState
              ? document.visibilityState
              : "",
          documentHasFocus:
            typeof document !== "undefined" && typeof document.hasFocus === "function"
              ? document.hasFocus()
              : undefined,
          buffer: buffer,
        });
      };
      document.addEventListener("keyup", docKeyupHandler, true);
      document.addEventListener("beforeinput", docBeforeInputHandler, true);
      document.addEventListener("input", docRawInputHandler, true);
      document.addEventListener("paste", docPasteHandler, true);
      document.addEventListener("compositionstart", docCompositionStartHandler, true);
      document.addEventListener("compositionupdate", docCompositionUpdateHandler, true);
      document.addEventListener("compositionend", docCompositionEndHandler, true);
      document.addEventListener("focusin", docFocusHandler, true);
      document.addEventListener("focusout", docBlurHandler, true);
      document.addEventListener("visibilitychange", docVisibilityHandler, true);
      docKeydownHandler = handleDocKeydown;
      document.addEventListener("keydown", docKeydownHandler, true);
      docInputHandler = handleDocInput;
      document.addEventListener("input", docInputHandler, true);
    }

    function stop() {
      notify("provider-stopped", {
        providerType: "keyboard",
      });
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
      if (docKeyupHandler) {
        document.removeEventListener("keyup", docKeyupHandler, true);
        docKeyupHandler = null;
      }
      if (docBeforeInputHandler) {
        document.removeEventListener("beforeinput", docBeforeInputHandler, true);
        docBeforeInputHandler = null;
      }
      if (docRawInputHandler) {
        document.removeEventListener("input", docRawInputHandler, true);
        docRawInputHandler = null;
      }
      if (docPasteHandler) {
        document.removeEventListener("paste", docPasteHandler, true);
        docPasteHandler = null;
      }
      if (docCompositionStartHandler) {
        document.removeEventListener("compositionstart", docCompositionStartHandler, true);
        docCompositionStartHandler = null;
      }
      if (docCompositionUpdateHandler) {
        document.removeEventListener("compositionupdate", docCompositionUpdateHandler, true);
        docCompositionUpdateHandler = null;
      }
      if (docCompositionEndHandler) {
        document.removeEventListener("compositionend", docCompositionEndHandler, true);
        docCompositionEndHandler = null;
      }
      if (docFocusHandler) {
        document.removeEventListener("focusin", docFocusHandler, true);
        docFocusHandler = null;
      }
      if (docBlurHandler) {
        document.removeEventListener("focusout", docBlurHandler, true);
        docBlurHandler = null;
      }
      if (docVisibilityHandler) {
        document.removeEventListener("visibilitychange", docVisibilityHandler, true);
        docVisibilityHandler = null;
      }
      if (docInputHandler) {
        document.removeEventListener("input", docInputHandler, true);
        docInputHandler = null;
      }
      clearPendingTargetRead();
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
        notify("scanner-error", {
          providerType: "keyboard",
          message: error && error.message ? error.message : String(error || "error"),
        });
        if (onError) {
          onError(error);
        }
      },
    };
  }

  function createAndroidIntentScanner(options) {
    var config = options || {};
    var canScan = typeof config.canScan === "function" ? config.canScan : function () { return true; };
    var getDiagnosticObserver =
      typeof config.getDiagnosticObserver === "function"
        ? config.getDiagnosticObserver
        : function () {
            return null;
          };
    var onScan = null;
    var onError = null;
    var dedupe = createDedupe(config.dedupeMs);
    var subscription = null;
    var attemptSeq = 0;
    var scanSeq = 0;
    var lastAcceptedScanByValue = {};

    function hasDiagnosticObserver() {
      return typeof getDiagnosticObserver() === "function";
    }

    function notify(type, detail) {
      var observer = getDiagnosticObserver();
      if (typeof observer !== "function") {
        return;
      }
      notifyDiagnostic(observer, type, detail);
    }

    function nextAttemptId() {
      attemptSeq += 1;
      return "intent-" + attemptSeq;
    }

    function nextScanId(attemptId) {
      scanSeq += 1;
      return attemptId + "-scan-" + scanSeq;
    }

    function handlePayload(payload) {
      var attemptId = nextAttemptId();
      var scanId = nextScanId(attemptId);
      if (hasDiagnosticObserver()) {
        notify("raw-input", {
          providerType: "intent",
          attemptId: attemptId,
          scanId: scanId,
          source: "intent",
          rawPayload: clonePlain(payload),
        });
      }
      if (!canScan()) {
        return;
      }
      var scan = normalizeScanPayload(payload);
      if (!scan.value) {
        return;
      }
      var ts = scan.ts || nowTs();
      if (!dedupe(scan.value, ts)) {
        var duplicateOf = lastAcceptedScanByValue[scan.value] || {};
        notify("dedupe-rejected", {
          providerType: "intent",
          attemptId: attemptId,
          scanId: scanId,
          duplicateOfScanId: duplicateOf.scanId || undefined,
          duplicateOfAttemptId: duplicateOf.attemptId || undefined,
          dedupeAccepted: false,
          source: "intent",
          value: scan.value,
          duplicateAt: ts,
        });
        return;
      }
      notify("dedupe-accepted", {
        providerType: "intent",
        attemptId: attemptId,
        scanId: scanId,
        dedupeAccepted: true,
        source: "intent",
        value: scan.value,
      });
      lastAcceptedScanByValue[scan.value] = {
        attemptId: attemptId,
        scanId: scanId,
        acceptedAt: ts,
      };
      scan.ts = ts;
      scan.source = "intent";
      scan.attemptId = attemptId;
      scan.scanId = scanId;
      notify("scan-emitted", {
        providerType: "intent",
        attemptId: attemptId,
        scanId: scanId,
        source: "intent",
        value: scan.value,
        rawValue: scan.value,
        normalizedValue: scan.value,
        symbology: scan.symbology,
        bufferStartAt: ts,
        bufferEndAt: ts,
        inputDurationMs: 0,
        characterEventCount: 0,
        inputEventCount: 1,
        flushReason: "intent",
        terminator: "None",
      });
      if (onScan) {
        onScan(scan);
      }
    }

    function start() {
      stop();
      notify("provider-started", {
        providerType: "intent",
        dedupeMs: config.dedupeMs || DEFAULT_DEDUP_MS,
      });
      if (!isBridgeAvailable()) {
        notify("scanner-error", {
          providerType: "intent",
          message: "INTENT_BRIDGE_UNAVAILABLE",
        });
        if (onError) {
          onError(new Error("INTENT_BRIDGE_UNAVAILABLE"));
        }
        return;
      }
      try {
        subscription = window.FlowStockAndroidBridge.subscribeScans(handlePayload);
      } catch (error) {
        notify("scanner-error", {
          providerType: "intent",
          message: error && error.message ? error.message : String(error || "error"),
        });
        if (onError) {
          onError(error);
        }
      }
    }

    function stop() {
      notify("provider-stopped", {
        providerType: "intent",
      });
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
          notify("scanner-error", {
            providerType: "intent",
            message: error && error.message ? error.message : String(error || "error"),
          });
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
    var diagnosticObserver = null;

    function getDiagnosticObserver() {
      return diagnosticObserver;
    }

    function notify(type, detail) {
      notifyDiagnostic(diagnosticObserver, type, detail);
    }

    var keyboardScanner = createKeyboardWedgeScanner({
      scanSink: scanSink,
      canScan: canScan,
      dedupeMs: config.dedupeMs,
      inputDelayMs: config.inputDelayMs,
      keyDelayMs: config.keyDelayMs,
      getDiagnosticObserver: getDiagnosticObserver,
    });
    var intentScanner = createAndroidIntentScanner({
      canScan: canScan,
      dedupeMs: config.dedupeMs,
      getDiagnosticObserver: getDiagnosticObserver,
    });

    function attachProvider(nextProvider) {
      if (!nextProvider) {
        return;
      }
      provider = nextProvider;
      provider.onScan(function (scan) {
        notify("manager-received", {
          providerType: providerType || (nextProvider && nextProvider.type) || "",
          attemptId: scan && scan.attemptId ? scan.attemptId : undefined,
          scanId: scan && scan.scanId ? scan.scanId : undefined,
          source: scan && scan.source ? scan.source : "",
          value: scan && scan.value ? scan.value : "",
          symbology: scan && scan.symbology ? scan.symbology : undefined,
        });
        if (scanHandler) {
          notify("handler-dispatched", {
            providerType: providerType || (nextProvider && nextProvider.type) || "",
            attemptId: scan && scan.attemptId ? scan.attemptId : undefined,
            scanId: scan && scan.scanId ? scan.scanId : undefined,
            source: scan && scan.source ? scan.source : "",
            value: scan && scan.value ? scan.value : "",
          });
          scanHandler(scan);
        } else {
          notify("handler-missing", {
            providerType: providerType || (nextProvider && nextProvider.type) || "",
            attemptId: scan && scan.attemptId ? scan.attemptId : undefined,
            scanId: scan && scan.scanId ? scan.scanId : undefined,
            source: scan && scan.source ? scan.source : "",
            value: scan && scan.value ? scan.value : "",
          });
        }
      });
      provider.onError(function (error) {
        notify("scanner-error", {
          providerType: providerType || (nextProvider && nextProvider.type) || "",
          message: error && error.message ? error.message : String(error || "error"),
        });
        if (errorHandler) {
          errorHandler(error);
        }
      });
    }

    function start() {
      stop();
      providerType = selectProvider(mode);
      notify("provider-selected", {
        mode: mode,
        providerType: providerType,
      });
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
      setDiagnosticObserver: function (observer) {
        diagnosticObserver = typeof observer === "function" ? observer : null;
      },
      clearDiagnosticObserver: function () {
        diagnosticObserver = null;
      },
      getSettings: function () {
        return {
          mode: mode,
          providerType: providerType || selectProvider(mode),
          dedupeMs: config.dedupeMs || DEFAULT_DEDUP_MS,
          inputDelayMs: config.inputDelayMs || DEFAULT_INPUT_DELAY_MS,
          keyDelayMs: config.keyDelayMs || DEFAULT_KEY_DELAY_MS,
          androidBridgeAvailable: !!isBridgeAvailable(),
        };
      },
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
