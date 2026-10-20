"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const context = {
  window: {},
  setTimeout,
  clearTimeout,
  Promise
};
vm.createContext(context);
vm.runInContext(
  fs.readFileSync(path.join(__dirname, "pc-catalog.js"), "utf8"),
  context,
  { filename: "pc-catalog.js" }
);
const createController = context.window.FlowStockPcCatalog.testHooks.createItemAutosaveController;
assert.equal(typeof createController, "function");

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise(function (ok, fail) { resolve = ok; reject = fail; });
  return { promise, resolve, reject };
}

async function settle() {
  await new Promise(function (resolve) { setImmediate(resolve); });
}

async function testSerialWritesAndCloseFlush() {
  const form = { name: "Original", gtin: "123" };
  const requests = [];
  const pending = [];
  const statuses = [];
  let activeWrites = 0;
  let maxActive = 0;
  let closed = 0;
  let savedEvents = 0;
  const timers = new Map();
  let nextTimerId = 0;
  const controller = createController({
    read: function () { return Object.assign({}, form); },
    save: function (payload) {
      const current = deferred();
      requests.push(payload);
      pending.push(current);
      activeWrites++;
      maxActive = Math.max(maxActive, activeWrites);
      return current.promise.then(function () { activeWrites--; });
    },
    onStatus: function (status) { statuses.push(status); },
    onSaved: function () { savedEvents++; },
    onClose: function () { closed++; },
    setTimeout: function (callback) {
      const id = ++nextTimerId;
      timers.set(id, callback);
      return id;
    },
    clearTimeout: function (id) { timers.delete(id); }
  });

  form.name = "First";
  controller.changed(700);
  form.name = "Second";
  controller.changed(700);
  assert.equal(timers.size, 1, "rapid typing replaces earlier debounce");
  assert.equal(requests.length, 0, "no POST before debounce");
  for (const callback of timers.values()) callback();
  timers.clear();
  await settle();
  assert.equal(requests.length, 1);
  assert.equal(requests[0].name, "Second");

  form.name = "Final";
  controller.changed(700);
  const closing = controller.requestClose();
  assert.equal(closed, 0, "close waits for in-flight write");
  assert.equal(requests.length, 1, "only one in-flight write");

  pending[0].resolve();
  await settle();
  assert.equal(requests.length, 2, "latest edits saved after first request resolves");
  assert.equal(requests[1].name, "Final");
  assert.equal(closed, 0);

  pending[1].resolve();
  assert.equal(await closing, true);
  assert.equal(closed, 1, "close only after final write");
  assert.equal(maxActive, 1, "full-payload POST requests must never overlap");
  assert.equal(savedEvents, 2);
  assert.equal(statuses.at(-1), "saved");
  assert.equal(controller.isDirty(), false);
  controller.dispose();
}

async function testNoOpAndRetryAfterError() {
  const form = { name: "Initial" };
  const payloads = [];
  const statuses = [];
  let fail = true;
  const controller = createController({
    read: function () { return Object.assign({}, form); },
    save: function (payload) {
      payloads.push(payload);
      return fail ? Promise.reject(new Error("offline")) : Promise.resolve();
    },
    onStatus: function (state) { statuses.push(state); },
    onClose: function () {}
  });
  form.name = "Changed";
  controller.changed(700);
  assert.equal(await controller.flush(), false);
  assert.equal(controller.isDirty(), true, "failed request must retain unsaved changes");
  assert.equal(statuses.at(-1), "error");
  await settle();
  assert.equal(payloads.length, 1, "no automatic uncontrolled retry");
  fail = false;
  assert.equal(await controller.flush(), true);
  assert.equal(controller.isDirty(), false);
  assert.equal(payloads.length, 2);
  form.name = "Changed";
  controller.changed(700);
  assert.equal(await controller.flush(), true);
  assert.equal(payloads.length, 2, "same payload must not be resent");
  controller.dispose();
}

async function testInvalidCloseAndDispose() {
  let closed = 0;
  let requests = 0;
  let formIsValid = false;
  const timers = new Map();
  let nextId = 0;
  const states = [];
  const controller = createController({
    read: function () { return formIsValid ? { name: "Valid" } : null; },
    save: function () { requests++; return Promise.resolve(); },
    onStatus: function (state) { states.push(state); },
    onClose: function () { closed++; },
    setTimeout: function (callback) { const id = ++nextId; timers.set(id, callback); return id; },
    clearTimeout: function (id) { timers.delete(id); }
  });
  controller.changed(700);
  assert.equal(await controller.requestClose(), false);
  assert.equal(closed, 0, "invalid pending edits block closing");
  assert.equal(requests, 0, "invalid form never sent to API");
  assert.equal(states.at(-1), "invalid");

  formIsValid = true;
  controller.changed(700);
  assert.equal(await controller.requestClose(), true);
  assert.equal(requests, 1);
  assert.equal(closed, 1);
  controller.dispose();

  const idle = createController({
    read: function () { return { name: "Initial" }; },
    save: function () { requests++; return Promise.resolve(); },
    onClose: function () {}
  });
  assert.equal(await idle.requestClose(), true, "untouched card closes immediately");
  idle.dispose();
}


async function testContinuedTypingCancelsPendingClose() {
  const form = { name: "Initial" };
  const writes = [];
  let closes = 0;
  const controller = createController({
    read: function () { return Object.assign({}, form); },
    save: function () {
      const next = deferred();
      writes.push(next);
      return next.promise;
    },
    onClose: function () { closes++; }
  });
  form.name = "First";
  controller.changed(700);
  const attemptedClose = controller.requestClose();
  await settle();
  assert.equal(writes.length, 1);

  form.name = "Second";
  controller.changed(700);
  writes[0].resolve();
  await settle();
  assert.equal(writes.length, 2);
  writes[1].resolve();
  assert.equal(await attemptedClose, true);
  assert.equal(closes, 0, "new editing cancels earlier close intent");
  assert.equal(controller.isDirty(), false);
  assert.equal(await controller.requestClose(), true);
  assert.equal(closes, 1);
  controller.dispose();
}

async function main() {
  await testSerialWritesAndCloseFlush();
  await testContinuedTypingCancelsPendingClose();
  await testNoOpAndRetryAfterError();
  await testInvalidCloseAndDispose();
  console.log("pc-catalog autosave state tests passed");
}

main().catch(function (error) {
  console.error(error);
  process.exitCode = 1;
});
