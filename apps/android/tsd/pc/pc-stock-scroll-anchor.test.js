const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "pc-stock-scroll-anchor.js"), "utf8");

function createHarness() {
  const listeners = {};
  const animationFrames = [];
  const scrollCalls = [];
  let replacementRow = null;
  let wireCalls = 0;

  const tableWrap = {
    addEventListener(type, handler, capture) {
      listeners[type] = { handler, capture };
    },
    contains() {
      return true;
    },
    querySelector() {
      return replacementRow;
    },
  };

  const window = {
    FlowStockPcStock: {
      wireStock() {
        wireCalls += 1;
        return "wired";
      },
    },
    requestAnimationFrame(callback) {
      animationFrames.push(callback);
      return animationFrames.length;
    },
    setTimeout(callback) {
      callback();
      return 1;
    },
    scrollBy(x, y) {
      scrollCalls.push([x, y]);
    },
  };
  const document = {
    getElementById(id) {
      return id === "stockTableWrap" ? tableWrap : null;
    },
  };

  vm.runInNewContext(source, {
    window,
    document,
    Number,
    Math,
    isFinite,
  });

  return {
    window,
    tableWrap,
    listeners,
    animationFrames,
    scrollCalls,
    get wireCalls() {
      return wireCalls;
    },
    setReplacementRow(row) {
      replacementRow = row;
    },
  };
}

function createRow(itemId, expanded, top) {
  return {
    getAttribute(name) {
      if (name === "data-stock-toggle-item") return String(itemId);
      if (name === "aria-expanded") return expanded ? "true" : "false";
      return null;
    },
    getBoundingClientRect() {
      return { top };
    },
  };
}

{
  const harness = createHarness();
  const result = harness.window.FlowStockPcStock.wireStock();
  assert.equal(result, "wired");
  assert.equal(harness.wireCalls, 1);
  assert.equal(harness.listeners.click.capture, true);
  assert.equal(harness.listeners.keydown.capture, true);
}

{
  const harness = createHarness();
  harness.window.FlowStockPcStock.wireStock();
  const rowBeforeCollapse = createRow(42, true, 210);
  harness.listeners.click.handler({
    type: "click",
    target: {
      closest() {
        return rowBeforeCollapse;
      },
    },
  });

  assert.equal(harness.animationFrames.length, 1);
  harness.setReplacementRow(createRow(42, false, 470));
  harness.animationFrames.shift()();
  assert.deepEqual(harness.scrollCalls, [[0, 260]]);
}

{
  const harness = createHarness();
  harness.window.FlowStockPcStock.wireStock();
  const collapsedRow = createRow(7, false, 300);
  harness.listeners.click.handler({
    type: "click",
    target: {
      closest() {
        return collapsedRow;
      },
    },
  });

  assert.equal(harness.animationFrames.length, 0, "expansion must not alter scroll position");
  assert.deepEqual(harness.scrollCalls, []);
}

{
  const harness = createHarness();
  harness.window.FlowStockPcStock.wireStock();
  const rowBeforeCollapse = createRow(9, true, 180);
  harness.listeners.keydown.handler({
    type: "keydown",
    key: "Enter",
    target: {
      closest() {
        return rowBeforeCollapse;
      },
    },
  });

  assert.equal(harness.animationFrames.length, 1);
  harness.setReplacementRow(createRow(9, false, 180.2));
  harness.animationFrames.shift()();
  assert.deepEqual(harness.scrollCalls, [], "sub-pixel movement must not cause scroll jitter");
}

{
  const harness = createHarness();
  harness.window.FlowStockPcStock.wireStock();
  const rowBeforeCollapse = createRow(11, true, 200);
  harness.listeners.click.handler({
    type: "click",
    target: {
      closest() {
        return rowBeforeCollapse;
      },
    },
  });

  harness.setReplacementRow(createRow(11, true, 500));
  harness.animationFrames.shift()();
  assert.deepEqual(harness.scrollCalls, [], "stale collapse callback must not move a re-expanded row");
}

console.log("pc-stock-scroll-anchor tests passed");
