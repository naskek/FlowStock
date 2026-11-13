"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

function deferred() {
  let resolve, reject;
  const promise = new Promise((ok, fail) => { resolve = ok; reject = fail; });
  return { promise, resolve, reject };
}
const settle = () => new Promise(resolve => setImmediate(resolve));

// Small DOM fixture: exercise real modal render/change/refresh handlers.
class Element {
  constructor() { this.children = {}; this.handlers = {}; this.isConnected = true; }
  set innerHTML(html) {
    this.html = html;
    this.children = {};
    for (const match of html.matchAll(/id="([^"]+)"/g)) this.children[match[1]] = new Element();
  }
  get innerHTML() { return this.html || ""; }
  querySelector(selector) {
    const id = selector.slice(1);
    if (this.children[id]) return this.children[id];
    for (const child of Object.values(this.children)) {
      const result = child.querySelector(selector);
      if (result) return result;
    }
    return null;
  }
  querySelectorAll() { return []; }
  addEventListener(event, handler) { this.handlers[event] = handler; }
}

function fixture() {
  let admin = true, confirmResult = true, confirmations = 0, invalidations = 0;
  let reads = null, write = deferred(), calls = [];
  const source = { id: 42, order_ref: "SO-42", order_type: "CUSTOMER", order_status: "IN_PROGRESS", allow_partial_outbound: false };
  let modal;
  const body = {
    appendChild(element) { modal = element; element.parentNode = body; },
    removeChild(element) { element.isConnected = false; element.parentNode = null; },
  };
  const context = { window: { confirm() { confirmations++; return confirmResult; } }, document: { body, createElement: () => new Element() } };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(path.join(__dirname, "pc-order-modal.js"), "utf8"), context);
  const api = context.window.FlowStockPcOrderModal;
  api.init({
    fetchJson(url, options) {
      if (options) { calls.push({ url, options }); return write.promise; }
      if (url.endsWith("/lines")) return Promise.resolve([]);
      return reads ? reads.promise : Promise.resolve({ ...source });
    },
    hasCapability: capability => admin && capability === "ManagePartialOutbound",
    escapeHtml: String, formatDate: () => "—", formatQuantity: String,
    isInternalOrder: order => order.order_type === "INTERNAL", isShippedOrder: () => false,
    getShipmentReadiness: () => null, renderReadinessBadge: () => "", applyOrderReadinessFromLines: () => {},
    translatePalletStatus: String, getOrderLineHighlightState: () => ({}), renderLinePalletFillingBadge: () => "",
    getOrderTypeLabel: String, bindModalDismiss: () => () => {},
    refreshSession: () => { admin = false; return Promise.resolve(); },
    onSessionInvalid: () => { invalidations++; },
  });
  return {
    api, source, calls,
    open() { api.openOrderModal({ ...source }); },
    checkbox: () => modal.querySelector("#partialOutboundPermission"),
    status: () => modal.querySelector("#partialOutboundPermissionStatus").textContent,
    change(value) { const box = this.checkbox(); box.checked = value; box.handlers.change(); },
    setAdmin(value) { admin = value; }, setConfirm(value) { confirmResult = value; },
    setReads(value) { reads = value; }, setWrite(value) { write = value; },
    get confirmations() { return confirmations; }, get invalidations() { return invalidations; },
  };
}

async function run() {
  const f = fixture();
  for (const status of ["IN_PROGRESS", "ACCEPTED", "DRAFT", "SHIPPED", "CANCELLED", "MERGED"]) {
    const state = f.api.permissionState({ ...f.source, order_status: status, allow_partial_outbound: true });
    assert.equal(state.visible, true);
    assert.equal(state.checked, ["IN_PROGRESS", "ACCEPTED"].includes(status));
  }
  f.setAdmin(false);
  assert.equal(f.api.permissionState(f.source).visible, false);
  f.open(); await settle(); assert.equal(f.checkbox(), null);
  f.setAdmin(true);
  for (const order of [{ ...f.source, order_type: "INTERNAL" }, { ...f.source, is_pending_confirmation: true }, { ...f.source, id: 0 }]) {
    assert.equal(f.api.permissionState(order).visible, false);
  }

  f.open(); await settle();
  assert.equal(f.checkbox().disabled, false);
  f.setConfirm(false); f.change(true);
  assert.equal(f.checkbox().checked, false);
  assert.equal(f.calls.length, 0);
  f.setConfirm(true); f.change(true);
  assert.equal(f.checkbox().disabled, true);
  f.change(true); assert.equal(f.calls.length, 1);
  assert.equal(f.calls[0].url, "/api/orders/42/partial-outbound-permission");
  assert.equal(f.calls[0].options.method, "PUT");
  assert.deepEqual(JSON.parse(f.calls[0].options.body), { allow_partial_outbound: true });

  // A new modal has an independent controller and request state.
  const g = fixture(); g.open(); await settle();
  const first = deferred(); g.setWrite(first);
  const stale = deferred(); g.setReads(stale);
  const poll = g.api.getOpenOrderModalController().refresh();
  g.change(true);
  first.resolve({ ok: true, order_id: 42, status: "ACCEPTED", allow_partial_outbound: true });
  await settle(); assert.equal(g.checkbox().checked, true);
  stale.resolve({ ...g.source, allow_partial_outbound: false }); await poll;
  assert.equal(g.checkbox().checked, true, "late pre-command poll must not restore stale permission");
  assert.equal(g.api.getOpenOrderModalController().order.order_status, "ACCEPTED");
  g.setReads(null);
  const second = deferred(); g.setWrite(second);
  const beforeDisable = g.confirmations;
  g.change(false); assert.equal(g.confirmations, beforeDisable);
  second.resolve({ ok: true, order_id: 42, status: "ACCEPTED", allow_partial_outbound: true });
  await settle(); assert.equal(g.checkbox().checked, true, "must apply server value");

  const failed = deferred(); g.setWrite(failed); g.change(false);
  failed.reject(new Error("offline")); await settle();
  assert.equal(g.checkbox().checked, true); assert.equal(g.checkbox().disabled, true);
  assert.match(g.status(), /фактическое значение/);
  await g.api.getOpenOrderModalController().refresh();
  assert.equal(g.checkbox().disabled, false); assert.equal(g.checkbox().checked, false);

  const forbidden = deferred(); g.setWrite(forbidden); g.change(true);
  forbidden.reject(Object.assign(new Error("ADMIN_REQUIRED"), { error: "ADMIN_REQUIRED", status: 403 }));
  await settle(); assert.equal(g.checkbox(), null);
  const h = fixture(); h.open(); await settle();
  const unauthorized = deferred(); h.setWrite(unauthorized); h.change(true);
  unauthorized.reject(Object.assign(new Error("INVALID_SESSION"), { status: 401 }));
  await settle(); assert.equal(h.invalidations, 1); assert.equal(h.api.hasOpenOrderModal(), false);
  console.log("PC order partial-outbound permission tests passed.");
}
run().catch(error => { console.error(error); process.exitCode = 1; });
