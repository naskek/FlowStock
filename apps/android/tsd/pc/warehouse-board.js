(function () {
  "use strict";

  var deps = null;
  var selectedBundleId = null;
  var statusFilter = "ALL";
  var cachedBundles = [];

  var STATUS_FILTERS = [
    { value: "ALL", label: "Все" },
    { value: "DRAFT", label: "Черновик" },
    { value: "SUBMITTED", label: "На подтверждении" },
    { value: "APPROVED", label: "Подтверждено" },
    { value: "IN_EXECUTION", label: "В работе" },
    { value: "EXECUTED", label: "Исполнено ТСД" },
    { value: "COMPLETED", label: "Проведено" },
    { value: "REJECTED", label: "Отклонено" },
    { value: "CANCELLED", label: "Отменено" },
    { value: "FAILED", label: "Ошибка" },
  ];

  var STATUS_LABELS = {
    DRAFT: "Черновик",
    SUBMITTED: "На подтверждении",
    APPROVED: "Подтверждено",
    IN_EXECUTION: "В работе",
    EXECUTED: "Исполнено ТСД",
    COMPLETED: "Проведено",
    REJECTED: "Отклонено",
    CANCELLED: "Отменено",
    FAILED: "Ошибка",
  };

  var ACTION_LABELS = {
    MOVE_HU: "Перемещение HU",
    ADOPT_PALLET_PLAN: "Перенос плана паллет",
  };

  function init(shared) {
    deps = shared;
  }

  function esc(value) {
    return deps ? deps.escapeHtml(value) : String(value || "");
  }

  function fmtDt(value) {
    return deps && deps.formatDateTime ? deps.formatDateTime(value) : String(value || "");
  }

  function accountActor() {
    if (!deps || !deps.getAccount) {
      return "WEB_PLANNER";
    }
    var account = deps.getAccount();
    return (account && account.login) || (account && account.device_id) || "WEB_PLANNER";
  }

  function formatPlannerError(payload) {
    var parts = [];
    if (payload && Array.isArray(payload.errors)) {
      payload.errors.forEach(function (entry) {
        if (entry && entry.message) {
          parts.push(String(entry.message));
        } else if (entry && entry.code) {
          parts.push(String(entry.code));
        }
      });
    }
    if (payload && Array.isArray(payload.warnings) && payload.warnings.length) {
      payload.warnings.forEach(function (entry) {
        if (entry && entry.message) {
          parts.push("⚠ " + entry.message);
        }
      });
    }
    if (payload && payload.message) {
      parts.push(String(payload.message));
    }
    if (payload && payload.error) {
      parts.push(String(payload.error));
    }
    return parts.length ? parts.join(" ") : "Ошибка сервера";
  }

  function plannerRequest(url, options) {
    if (!deps || !deps.fetchJson) {
      return Promise.reject(new Error("NO_API"));
    }
    var opts = options || {};
    return fetch(url, {
      method: opts.method || "GET",
      headers: opts.headers,
      body: opts.body,
      cache: "no-store",
      signal: opts.signal,
    })
      .then(function (response) {
        return response
          .json()
          .catch(function () {
            return null;
          })
          .then(function (payload) {
            if (!response.ok) {
              throw new Error(formatPlannerError(payload));
            }
            return payload;
          });
      });
  }

  function plannerFetch(url, options) {
    var opts = options || {};
    var headers = new Headers(opts.headers || {});
    if (!headers.has("Content-Type") && opts.body) {
      headers.set("Content-Type", "application/json");
    }
    if (deps && deps.createRequestHeaders) {
      headers = deps.createRequestHeaders(headers, url);
    }
    return plannerRequest(url, {
      method: opts.method,
      headers: headers,
      body: opts.body,
    });
  }

  function statusLabel(status) {
    var key = String(status || "").trim().toUpperCase();
    return STATUS_LABELS[key] || status || "—";
  }

  function actionLabel(actionType) {
    var key = String(actionType || "").trim().toUpperCase();
    return ACTION_LABELS[key] || actionType || "—";
  }

  function listBundles() {
    var query = statusFilter && statusFilter !== "ALL" ? "?status=" + encodeURIComponent(statusFilter) : "";
    return plannerFetch("/api/planner/bundles" + query).then(function (payload) {
      cachedBundles = payload && Array.isArray(payload.bundles) ? payload.bundles : [];
      return cachedBundles;
    });
  }

  function getBundle(id) {
    return plannerFetch("/api/planner/bundles/" + encodeURIComponent(String(id)));
  }

  function createBundle(comment) {
    return plannerFetch("/api/planner/bundles", {
      method: "POST",
      body: JSON.stringify({
        source: "WEB_PLANNER",
        created_by: accountActor(),
        comment: comment || null,
      }),
    });
  }

  function addLine(bundleId, line) {
    return plannerFetch("/api/planner/bundles/" + encodeURIComponent(String(bundleId)) + "/lines", {
      method: "POST",
      body: JSON.stringify(line),
    });
  }

  function submitBundle(bundleId) {
    return plannerFetch("/api/planner/bundles/" + encodeURIComponent(String(bundleId)) + "/submit", {
      method: "POST",
      body: JSON.stringify({ actor: accountActor() }),
    });
  }

  function cancelBundle(bundleId) {
    return plannerFetch("/api/planner/bundles/" + encodeURIComponent(String(bundleId)) + "/cancel", {
      method: "POST",
    });
  }

  function previewLines(lines) {
    return plannerFetch("/api/planner/bundles/preview", {
      method: "POST",
      body: JSON.stringify({ lines: lines }),
    });
  }

  function renderWarehouseBoard() {
    return deps.renderPageShell(
      '<section class="pc-card wb-card">' +
        '<div class="pc-toolbar wb-toolbar">' +
        '  <div class="form-field">' +
        '    <label for="wbStatusFilter">Статус</label>' +
        '    <select id="wbStatusFilter" class="pc-input">' +
        STATUS_FILTERS.map(function (item) {
          return (
            '<option value="' +
            esc(item.value) +
            '"' +
            (item.value === statusFilter ? " selected" : "") +
            ">" +
            esc(item.label) +
            "</option>"
          );
        }).join("") +
        "    </select>" +
        "  </div>" +
        '  <div class="pc-toolbar-actions">' +
        '    <button type="button" class="btn btn-outline" id="wbRefreshBtn">Обновить</button>' +
        '    <button type="button" class="btn btn-outline" id="wbCreateMoveBtn">Создать MOVE_HU</button>' +
        '    <button type="button" class="btn btn-outline" id="wbCreateAdoptBtn">Создать ADOPT</button>' +
        "  </div>" +
        "</div>" +
        '<div id="wbNotice" class="pc-status wb-notice" hidden></div>' +
        '<div class="wb-table-wrap">' +
        '  <table class="pc-table wb-table">' +
        "    <thead><tr>" +
        "      <th>№</th><th>Статус</th><th>Источник</th><th>Создано</th><th>Кем</th>" +
        "      <th>Комментарий</th><th>Строк</th><th>Заданий</th><th></th>" +
        "    </tr></thead>" +
        '    <tbody id="wbBundlesBody"><tr><td colspan="9">Загрузка...</td></tr></tbody>' +
        "  </table>" +
        "</div>" +
        '<div id="wbDetails" class="wb-details">' +
        '  <div class="wb-details-placeholder">Выберите пакет в таблице или создайте новый.</div>' +
        "</div>" +
        "</section>"
    );
  }

  function setNotice(message, isError) {
    var node = document.getElementById("wbNotice");
    if (!node) {
      return;
    }
    if (!message) {
      node.hidden = true;
      node.textContent = "";
      return;
    }
    node.hidden = false;
    node.textContent = message;
    node.classList.toggle("is-error", !!isError);
  }

  function renderBundlesTable(rows) {
    var body = document.getElementById("wbBundlesBody");
    if (!body) {
      return;
    }
    if (!rows || !rows.length) {
      body.innerHTML = '<tr><td colspan="9" class="pc-muted">Пакетов нет.</td></tr>';
      return;
    }
    body.innerHTML = rows
      .map(function (row) {
        var id = row.id;
        var isSelected = selectedBundleId === id;
        return (
          "<tr" +
          (isSelected ? ' class="is-selected"' : "") +
          ">" +
          "<td>" +
          esc(row.bundle_ref || "#" + id) +
          "</td>" +
          "<td>" +
          esc(statusLabel(row.status)) +
          "</td>" +
          "<td>" +
          esc(row.source || "—") +
          "</td>" +
          "<td>" +
          esc(fmtDt(row.created_at)) +
          "</td>" +
          "<td>" +
          esc(row.created_by || "—") +
          "</td>" +
          "<td>" +
          esc(row.comment || "—") +
          "</td>" +
          "<td>" +
          esc(row.line_count != null ? row.line_count : "—") +
          "</td>" +
          "<td>" +
          esc(row.task_count != null ? row.task_count : "—") +
          "</td>" +
          '<td><button type="button" class="btn btn-outline btn-sm" data-open-bundle="' +
          esc(id) +
          '">Открыть</button></td>' +
          "</tr>"
        );
      })
      .join("");
  }

  function renderLinesTable(lines) {
    if (!lines || !lines.length) {
      return '<div class="pc-muted">Строк действий нет.</div>';
    }
    var head =
      "<tr><th>#</th><th>Тип</th><th>Статус</th><th>HU</th><th>Откуда</th><th>Куда</th><th>Qty</th><th>Ошибка</th></tr>";
    var body = lines
      .map(function (line) {
        return (
          "<tr>" +
          "<td>" +
          esc(line.line_no) +
          "</td>" +
          "<td>" +
          esc(actionLabel(line.action_type)) +
          "</td>" +
          "<td>" +
          esc(line.status) +
          "</td>" +
          "<td>" +
          esc(line.hu_code || "—") +
          "</td>" +
          "<td>" +
          esc(line.from_location_id || "—") +
          "</td>" +
          "<td>" +
          esc(line.to_location_id || "—") +
          "</td>" +
          "<td>" +
          esc(line.qty != null ? line.qty : "—") +
          "</td>" +
          "<td>" +
          esc(line.error_message || line.error_code || "—") +
          "</td>" +
          "</tr>"
        );
      })
      .join("");
    return '<table class="pc-table wb-subtable"><thead>' + head + "</thead><tbody>" + body + "</tbody></table>";
  }

  function renderTasksBlock(tasks) {
    if (!tasks || !tasks.length) {
      return '<div class="pc-muted">TSD-заданий нет (server-only действия выполняются без TSD).</div>';
    }
    return tasks
      .map(function (entry, index) {
        var task = entry.task || {};
        var lines = entry.lines || [];
        var events = entry.events || [];
        var linesHtml = lines.length
          ? '<table class="pc-table wb-subtable"><thead><tr><th>#</th><th>HU</th><th>Статус</th><th>Откуда</th><th>Куда</th><th>Скан HU</th><th>Скан место</th></tr></thead><tbody>' +
            lines
              .map(function (line) {
                return (
                  "<tr><td>" +
                  esc(line.line_no) +
                  "</td><td>" +
                  esc(line.expected_hu_code) +
                  "</td><td>" +
                  esc(line.status) +
                  "</td><td>" +
                  esc(line.from_location_id) +
                  "</td><td>" +
                  esc(line.to_location_id) +
                  "</td><td>" +
                  esc(line.scanned_hu_code || "—") +
                  "</td><td>" +
                  esc(line.scanned_location_id || "—") +
                  "</td></tr>"
                );
              })
              .join("") +
            "</tbody></table>"
          : '<div class="pc-muted">Строк задания нет.</div>';
        var eventsHtml = events.length
          ? '<ul class="wb-events">' +
            events
              .map(function (ev) {
                return (
                  "<li><strong>" +
                  esc(ev.event_type) +
                  "</strong> " +
                  esc(fmtDt(ev.event_at)) +
                  (ev.hu_code ? " HU " + esc(ev.hu_code) : "") +
                  (ev.message ? " — " + esc(ev.message) : "") +
                  "</li>"
                );
              })
              .join("") +
            "</ul>"
          : '<div class="pc-muted">Событий пока нет.</div>';
        return (
          '<div class="wb-task-block">' +
          "<h4>Задание " +
          esc(task.task_ref || "#" + task.id) +
          " · " +
          esc(statusLabel(task.status)) +
          "</h4>" +
          linesHtml +
          "<h5>События / сканы</h5>" +
          eventsHtml +
          "</div>"
        );
      })
      .join("");
  }

  function renderBundleDetails(payload) {
    var root = document.getElementById("wbDetails");
    if (!root) {
      return;
    }
    if (!payload || !payload.bundle) {
      root.innerHTML = '<div class="wb-details-placeholder">Пакет не найден.</div>';
      return;
    }
    var bundle = payload.bundle;
    var status = String(bundle.status || "").toUpperCase();
    var canSubmit = status === "DRAFT";
    var canCancel = status === "DRAFT" || status === "SUBMITTED";
    var actions =
      '<div class="wb-detail-actions">' +
      '<button type="button" class="btn btn-outline" id="wbDetailRefreshBtn">Обновить</button>' +
      (canSubmit
        ? '<button type="button" class="btn primary-btn" id="wbSubmitBtn">Отправить на подтверждение</button>'
        : "") +
      (canCancel ? '<button type="button" class="btn btn-outline" id="wbCancelBtn">Отменить</button>' : "") +
      '<button type="button" class="btn btn-outline" id="wbPreviewBtn">Проверить (preview)</button>' +
      "</div>";

    root.innerHTML =
      '<div class="wb-details-card">' +
      "<h3>Пакет " +
      esc(bundle.bundle_ref) +
      "</h3>" +
      '<div class="wb-detail-meta">' +
      "<div><strong>Статус:</strong> " +
      esc(statusLabel(bundle.status)) +
      "</div>" +
      "<div><strong>Источник:</strong> " +
      esc(bundle.source) +
      "</div>" +
      "<div><strong>Создан:</strong> " +
      esc(fmtDt(bundle.created_at)) +
      " " +
      esc(bundle.created_by || "") +
      "</div>" +
      (bundle.comment ? "<div><strong>Комментарий:</strong> " + esc(bundle.comment) + "</div>" : "") +
      (bundle.error_message
        ? '<div class="wb-error"><strong>Ошибка:</strong> ' + esc(bundle.error_message) + "</div>"
        : "") +
      "</div>" +
      actions +
      "<h4>Строки действий</h4>" +
      renderLinesTable(payload.lines || []) +
      "<h4>TSD-задания</h4>" +
      renderTasksBlock(payload.tasks || []) +
      '<div id="wbPreviewResult" class="wb-preview-result" hidden></div>' +
      "</div>";
  }

  function openBundle(id) {
    selectedBundleId = id;
    renderBundlesTable(cachedBundles);
    var details = document.getElementById("wbDetails");
    if (details) {
      details.innerHTML = '<div class="pc-status">Загрузка деталей...</div>';
    }
    return getBundle(id)
      .then(function (payload) {
        renderBundleDetails(payload);
        wireDetailActions(payload);
      })
      .catch(function (error) {
        setNotice(String(error.message || error), true);
        renderBundleDetails(null);
      });
  }

  function wireDetailActions(payload) {
    var refreshBtn = document.getElementById("wbDetailRefreshBtn");
    var submitBtn = document.getElementById("wbSubmitBtn");
    var cancelBtn = document.getElementById("wbCancelBtn");
    var previewBtn = document.getElementById("wbPreviewBtn");
    if (refreshBtn) {
      refreshBtn.addEventListener("click", function () {
        if (selectedBundleId) {
          openBundle(selectedBundleId);
        }
      });
    }
    if (submitBtn && payload && payload.bundle) {
      submitBtn.addEventListener("click", function () {
        submitBtn.disabled = true;
        submitBundle(payload.bundle.id)
          .then(function (result) {
            setNotice(
              "Пакет " + (result.bundle_ref || payload.bundle.bundle_ref) + " отправлен: " + statusLabel(result.status),
              false
            );
            return refreshAll().then(function () {
              openBundle(payload.bundle.id);
            });
          })
          .catch(function (error) {
            setNotice(String(error.message || error), true);
          })
          .finally(function () {
            submitBtn.disabled = false;
          });
      });
    }
    if (cancelBtn && payload && payload.bundle) {
      cancelBtn.addEventListener("click", function () {
        if (!window.confirm("Отменить пакет " + payload.bundle.bundle_ref + "?")) {
          return;
        }
        cancelBtn.disabled = true;
        cancelBundle(payload.bundle.id)
          .then(function () {
            setNotice("Пакет отменён.", false);
            return refreshAll().then(function () {
              openBundle(payload.bundle.id);
            });
          })
          .catch(function (error) {
            setNotice(String(error.message || error), true);
          })
          .finally(function () {
            cancelBtn.disabled = false;
          });
      });
    }
    if (previewBtn && payload && payload.lines && payload.lines.length) {
      previewBtn.addEventListener("click", function () {
        var lines = payload.lines.map(function (line) {
          return {
            action_type: line.action_type,
            payload_json: line.payload_json || "{}",
            source_order_id: line.source_order_id,
            target_order_id: line.target_order_id,
            item_id: line.item_id,
            hu_code: line.hu_code,
            from_location_id: line.from_location_id,
            to_location_id: line.to_location_id,
            qty: line.qty,
          };
        });
        previewBtn.disabled = true;
        previewLines(lines)
          .then(function (result) {
            var box = document.getElementById("wbPreviewResult");
            if (!box) {
              return;
            }
            box.hidden = false;
            var errors = (result && result.errors) || [];
            var warnings = (result && result.warnings) || [];
            if (result && result.valid && !errors.length) {
              box.textContent = "Проверка пройдена.";
              box.className = "wb-preview-result is-ok";
              return;
            }
            var text = errors
              .map(function (e) {
                return e.message || e.code;
              })
              .concat(
                warnings.map(function (w) {
                  return "⚠ " + (w.message || w.code);
                })
              )
              .join(" ");
            box.textContent = text || "Есть замечания по пакету.";
            box.className = "wb-preview-result is-error";
          })
          .catch(function (error) {
            setNotice(String(error.message || error), true);
          })
          .finally(function () {
            previewBtn.disabled = false;
          });
      });
    }
  }

  function refreshAll() {
    return listBundles().then(function (rows) {
      renderBundlesTable(rows);
    });
  }

  function openModal(title, bodyHtml, footerHtml) {
    var modal = document.createElement("div");
    modal.className = "pc-modal";
    modal.innerHTML =
      '<div class="pc-modal-card wb-modal-card">' +
      '<div class="pc-modal-header"><div class="pc-modal-title">' +
      esc(title) +
      "</div></div>" +
      bodyHtml +
      '<div class="pc-modal-footer">' +
      (footerHtml || "") +
      "</div></div>";
    document.body.appendChild(modal);
    modal.addEventListener("click", function (event) {
      if (event.target === modal) {
        modal.remove();
      }
    });
    return modal;
  }

  function runMoveFlow() {
    var modal = openModal(
      "Создать MOVE_HU",
      '<div class="wb-form">' +
        '<label>HU <input id="wbMoveHu" class="pc-input" type="text" /></label>' +
        '<label>Item id <input id="wbMoveItemId" class="pc-input" type="number" /></label>' +
        '<label>From location id <input id="wbMoveFromLoc" class="pc-input" type="number" /></label>' +
        '<label>To location id <input id="wbMoveToLoc" class="pc-input" type="number" /></label>' +
        '<label>Количество <input id="wbMoveQty" class="pc-input" type="number" step="0.001" value="1" /></label>' +
        '<label>Комментарий <input id="wbMoveComment" class="pc-input" type="text" /></label>' +
        '<div id="wbMoveError" class="wb-error" hidden></div>' +
        "</div>",
      '<button type="button" class="btn btn-outline" data-close-modal>Закрыть</button>' +
        '<button type="button" class="btn primary-btn" id="wbMoveSubmit">Создать и отправить</button>'
    );
    modal.querySelector("[data-close-modal]").addEventListener("click", function () {
      modal.remove();
    });
    modal.querySelector("#wbMoveSubmit").addEventListener("click", function () {
      var hu = String(modal.querySelector("#wbMoveHu").value || "").trim();
      var itemId = Number(modal.querySelector("#wbMoveItemId").value);
      var fromLoc = Number(modal.querySelector("#wbMoveFromLoc").value);
      var toLoc = Number(modal.querySelector("#wbMoveToLoc").value);
      var qty = Number(modal.querySelector("#wbMoveQty").value) || 1;
      var comment = String(modal.querySelector("#wbMoveComment").value || "").trim();
      var err = modal.querySelector("#wbMoveError");
      if (!hu || !itemId || !toLoc) {
        err.hidden = false;
        err.textContent = "Заполните HU, item id и место назначения.";
        return;
      }
      var payload = {
        hu_code: hu,
        item_id: itemId,
        qty: qty,
        from_location_id: fromLoc || null,
        to_location_id: toLoc,
      };
      var line = {
        action_type: "MOVE_HU",
        hu_code: hu,
        item_id: itemId,
        qty: qty,
        from_location_id: fromLoc || null,
        to_location_id: toLoc,
        payload_json: JSON.stringify(payload),
      };
      var btn = modal.querySelector("#wbMoveSubmit");
      btn.disabled = true;
      err.hidden = true;
      createBundle(comment)
        .then(function (created) {
          return addLine(created.bundle_id, line).then(function () {
            return submitBundle(created.bundle_id).then(function (submitted) {
              return { bundleId: created.bundle_id, result: submitted };
            });
          });
        })
        .then(function (ctx) {
          modal.remove();
          setNotice(
            "Создан пакет " + (ctx.result.bundle_ref || "") + " · " + statusLabel(ctx.result.status),
            false
          );
          selectedBundleId = ctx.bundleId;
          return refreshAll().then(function () {
            openBundle(ctx.bundleId);
          });
        })
        .catch(function (error) {
          err.hidden = false;
          err.textContent = String(error.message || error);
        })
        .finally(function () {
          btn.disabled = false;
        });
    });
  }

  function runAdoptFlow() {
    var modal = openModal(
      "Создать ADOPT_PALLET_PLAN",
      '<div class="wb-form">' +
        '<label>Source INTERNAL order id <input id="wbAdoptSource" class="pc-input" type="number" /></label>' +
        '<label>Target CUSTOMER order id <input id="wbAdoptTarget" class="pc-input" type="number" /></label>' +
        '<label>Комментарий <input id="wbAdoptComment" class="pc-input" type="text" /></label>' +
        '<div id="wbAdoptError" class="wb-error" hidden></div>' +
        "</div>",
      '<button type="button" class="btn btn-outline" data-close-modal>Закрыть</button>' +
        '<button type="button" class="btn primary-btn" id="wbAdoptSubmit">Создать и отправить</button>'
    );
    modal.querySelector("[data-close-modal]").addEventListener("click", function () {
      modal.remove();
    });
    modal.querySelector("#wbAdoptSubmit").addEventListener("click", function () {
      var sourceId = Number(modal.querySelector("#wbAdoptSource").value);
      var targetId = Number(modal.querySelector("#wbAdoptTarget").value);
      var comment = String(modal.querySelector("#wbAdoptComment").value || "").trim();
      var err = modal.querySelector("#wbAdoptError");
      if (!sourceId || !targetId) {
        err.hidden = false;
        err.textContent = "Укажите id заказов.";
        return;
      }
      var payload = {
        source_internal_order_id: sourceId,
        target_customer_order_id: targetId,
      };
      var line = {
        action_type: "ADOPT_PALLET_PLAN",
        source_order_id: sourceId,
        target_order_id: targetId,
        payload_json: JSON.stringify(payload),
      };
      var btn = modal.querySelector("#wbAdoptSubmit");
      btn.disabled = true;
      err.hidden = true;
      createBundle(comment)
        .then(function (created) {
          return addLine(created.bundle_id, line).then(function () {
            return submitBundle(created.bundle_id).then(function (submitted) {
              return { bundleId: created.bundle_id, result: submitted };
            });
          });
        })
        .then(function (ctx) {
          modal.remove();
          setNotice(
            "Создан пакет " + (ctx.result.bundle_ref || "") + " · " + statusLabel(ctx.result.status),
            false
          );
          selectedBundleId = ctx.bundleId;
          return refreshAll().then(function () {
            openBundle(ctx.bundleId);
          });
        })
        .catch(function (error) {
          err.hidden = false;
          err.textContent = String(error.message || error);
        })
        .finally(function () {
          btn.disabled = false;
        });
    });
  }

  function wireWarehouseBoard() {
    var filter = document.getElementById("wbStatusFilter");
    var refreshBtn = document.getElementById("wbRefreshBtn");
    var moveBtn = document.getElementById("wbCreateMoveBtn");
    var adoptBtn = document.getElementById("wbCreateAdoptBtn");
    var body = document.getElementById("wbBundlesBody");

    if (filter) {
      filter.addEventListener("change", function () {
        statusFilter = filter.value || "ALL";
        refreshAll().catch(function (error) {
          setNotice(String(error.message || error), true);
        });
      });
    }
    if (refreshBtn) {
      refreshBtn.addEventListener("click", function () {
        refreshAll().catch(function (error) {
          setNotice(String(error.message || error), true);
        });
      });
    }
    if (moveBtn) {
      moveBtn.addEventListener("click", runMoveFlow);
    }
    if (adoptBtn) {
      adoptBtn.addEventListener("click", runAdoptFlow);
    }
    if (body) {
      body.addEventListener("click", function (event) {
        var target = event.target;
        if (!target || !target.getAttribute) {
          return;
        }
        var idText = target.getAttribute("data-open-bundle");
        if (!idText) {
          return;
        }
        openBundle(Number(idText));
      });
    }

    refreshAll().catch(function (error) {
      setNotice(String(error.message || error), true);
    });
  }

  window.FlowStockWarehouseBoard = {
    init: init,
    render: renderWarehouseBoard,
    wire: wireWarehouseBoard,
    statusLabel: statusLabel,
    STATUS_LABELS: STATUS_LABELS,
  };
})();
