import React, { useEffect, useMemo, useRef, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import api, { User } from "../api";
import { LocationItem } from "../types";
import {
  errDetail,
  formatDocNumber,
  statusClass,
  statusLabel,
  PRODUCTION_ZONE_CODE,
  docTypeLabel,
  docFinishLabel,
  docHint,
} from "../utils";

export const DesktopDocDetail = ({
  type,
  user,
  showToast,
  locations,
}: {
  type: "inbound" | "outbound" | "production_issue" | "production_receipt";
  user: User | null;
  showToast: (t: "success" | "error", m: string) => void;
  locations: LocationItem[];
}) => {
  const navigate = useNavigate();
  const { docId } = useParams();
  const [status, setStatus] = useState<string>("draft");
  const [createdAt, setCreatedAt] = useState<string>("");
  const [lines, setLines] = useState<any[]>([]);
  const [activeZone, setActiveZone] = useState<string>("");
  const [scanValue, setScanValue] = useState("");
  const [qty, setQty] = useState(1);
  const [counterpartyId, setCounterpartyId] = useState<number | null>(null);
  const [counterpartyName, setCounterpartyName] = useState<string>("");
  const [contacts, setContacts] = useState<any[]>([]);
  const [contactsFilter, setContactsFilter] = useState<string>("");
  const [loading, setLoading] = useState(false);
  const [palletsMode, setPalletsMode] = useState(false);
  const [palletCount, setPalletCount] = useState(1);
  const [pallets, setPallets] = useState<{ sscc: string; hu_id?: number }[]>([]);
  const [selectedSscc, setSelectedSscc] = useState<string>("");
  const [packType, setPackType] = useState<string>("unit");
  const [manualProductMode, setManualProductMode] = useState(false);
  const [productQuery, setProductQuery] = useState("");
  const [productOptions, setProductOptions] = useState<any[]>([]);
  const [selectedProductId, setSelectedProductId] = useState<number | null>(null);
  const [displayNumber, setDisplayNumber] = useState("");
  const [productNames, setProductNames] = useState<Record<number, string>>({});
  const scanInput = useRef<HTMLInputElement>(null);
  const docTypeTitle = docTypeLabel(type);
  const docSubtitle = docHint(type);

  useEffect(() => {
    if (!user || user.role === "viewer") {
      navigate("/desktop");
    }
  }, [user, navigate]);

  useEffect(() => {
    scanInput.current?.focus();
  }, [docId]);

  const locationMap = useMemo(() => {
    const map: Record<number, LocationItem> = {};
    locations.forEach((l) => (map[l.id] = l));
    return map;
  }, [locations]);

  const fetchDoc = async () => {
    if (!docId) {
      navigate(`/desktop/docs/${type}`);
      return;
    }
    setLoading(true);
    try {
      const resp = await api.get(`/docs/${docId}`);
      const data = resp.data;
      if (data.type !== type) {
        showToast("error", `Это не документ типа ${docTypeTitle}`);
        navigate(`/desktop/docs/${type}`);
        return;
      }
      setStatus(data.status);
      setCreatedAt(data.created_at || "");
      setLines(data.lines || []);
      setCounterpartyId(data.counterparty_id ?? null);
      setCounterpartyName(data.counterparty?.name || "");
      setPalletsMode(false);
      setPallets([]);
      setSelectedSscc("");
      setManualProductMode(false);
      setSelectedProductId(null);
      setProductQuery("");
      setProductOptions([]);
      // сохраним названия, если бэк вернул
      if (Array.isArray(data.lines)) {
        const names: Record<number, string> = {};
        data.lines.forEach((ln: any) => {
          if (ln.product_id && ln.product_name) {
            names[ln.product_id] = ln.product_name;
          }
        });
        setProductNames((prev) => ({ ...prev, ...names }));
      }
      if (data.meta?.cell_code) setActiveZone(data.meta.cell_code);
      setDisplayNumber(formatDocNumber({ id: data.id ?? docId, type, created_at: data.created_at }));
      // если не хватает имён, попробуем догрузить списком
      const missingIds =
        data.lines
          ?.map((ln: any) => ln.product_id)
          ?.filter((id: number) => id && !productNames[id]) || [];
      if (missingIds.length) {
        const respNames = await api.get("/products", { params: { ids: missingIds.join(",") } }).catch(() => null);
        if (respNames?.data && Array.isArray(respNames.data)) {
          const names: Record<number, string> = {};
          respNames.data.forEach((p: any) => {
            if (p.id && p.name) names[p.id] = p.name;
          });
          setProductNames((prev) => ({ ...prev, ...names }));
        }
      }
    } catch (err: any) {
      showToast("error", errDetail(err));
      navigate(`/desktop/docs/${type}`);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    if (!user || user.role === "viewer") {
      navigate("/desktop");
      return;
    }
    api
      .get("/contacts")
      .then((r) => Array.isArray(r.data) && setContacts(r.data))
      .catch(() => {});
    fetchDoc();
  }, [docId, user]);

  useEffect(() => {
    if (!activeZone && locations.length) {
      const first = locations.find((l) => l.cell_code !== PRODUCTION_ZONE_CODE);
      if (first) setActiveZone(first.cell_code);
    }
  }, [locations, activeZone]);

  const onScan = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!scanValue && !manualProductMode) return;
    if (!docId) return;
    if (!activeZone) {
      showToast("error", "Выберите склад");
      return;
    }
    if (!palletsMode) {
      if (manualProductMode) {
        if (!selectedProductId) {
          showToast("error", "Выберите товар");
          return;
        }
        try {
          const resp = await api.post(`/docs/${docId}/lines`, {
            product_id: selectedProductId,
            qty_delta: qty || 1,
            cell_code: activeZone,
          });
          setLines(resp.data.lines || []);
          setScanValue("");
          showToast("success", "Строка добавлена");
          scanInput.current?.focus();
        } catch (err: any) {
          showToast("error", errDetail(err));
        }
        return;
      }
      try {
        const resp = await api.post(`/docs/${docId}/scan`, {
          barcode: scanValue,
          cell_code: activeZone,
          qty: qty || 1,
        });
        setLines(resp.data.lines || []);
        setScanValue("");
        showToast("success", "Строка добавлена");
        scanInput.current?.focus();
      } catch (err: any) {
        showToast("error", errDetail(err));
      }
      return;
    }

    // pallets mode
    if (!selectedSscc) {
      showToast("error", "Выберите паллету");
      return;
    }
    if (manualProductMode && !selectedProductId) {
      showToast("error", "Выберите товар");
      return;
    }
    try {
      const resp = await api.post(`/docs/${docId}/pallets/${selectedSscc}/manual`, {
        barcode: manualProductMode ? undefined : scanValue,
        product_id: manualProductMode ? selectedProductId : undefined,
        pack_type: packType || "unit",
        pack_count: qty || 1,
      });
      setLines(resp.data.lines || []);
      setScanValue("");
      showToast("success", "Добавлено на паллету");
      scanInput.current?.focus();
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const saveDoc = async () => {
    if (!docId) return;
    try {
      const resp = await api.post(`/docs/${docId}/start`);
      setStatus(resp.data.status);
      showToast("success", "Документ сохранён");
      navigate(`/desktop/docs/${type}`);
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const finishDoc = async () => {
    if (!docId) return;
    try {
      const resp = await api.post(`/docs/${docId}/finish`);
      setStatus(resp.data.status);
      showToast("success", "Документ проведён");
      navigate(`/desktop/docs/${type}`);
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const downloadPdf = async () => {
    if (!docId) return;
    try {
      const resp = await api.get(`/docs/${docId}/pdf`, { responseType: "blob" });
      const url = window.URL.createObjectURL(new Blob([resp.data], { type: "application/pdf" }));
      const link = document.createElement("a");
      link.href = url;
      link.download = `doc_${docId}.pdf`;
      link.click();
      window.URL.revokeObjectURL(url);
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const closeCard = () => navigate(`/desktop/docs/${type}`);

  const updateCounterparty = async (value: number | null) => {
    if (!docId) return;
    try {
      const resp = await api.put(`/docs/${docId}`, { counterparty_id: value });
      setCounterpartyId(resp.data.counterparty_id ?? null);
      setCounterpartyName(resp.data.counterparty?.name || "");
      showToast("success", "Контрагент обновлён");
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const filteredContacts = contacts.filter((c: any) =>
    (c.name || "").toLowerCase().includes(contactsFilter.toLowerCase().trim())
  );

  useEffect(() => {
    const fetchProducts = async () => {
      if (!manualProductMode) {
        setProductOptions([]);
        return;
      }
      try {
        const hasQuery = productQuery.trim().length >= 2;
        const resp = hasQuery
          ? await api.get("/products/search", { params: { q: productQuery.trim() } })
          : await api.get("/products", { params: { limit: 20 } });
        if (Array.isArray(resp.data)) setProductOptions(resp.data.slice(0, 20));
      } catch {
        // ignore
      }
    };
    fetchProducts();
  }, [productQuery, manualProductMode]);

  const createPallets = async () => {
    if (!docId) return;
    if (!activeZone) {
      showToast("error", "Выберите склад");
      return;
    }
    const count = Math.max(1, Number(palletCount) || 1);
    try {
      const resp = await api.post(`/docs/${docId}/pallets/auto`, {
        cell_code: activeZone,
        count,
      });
      const ssccList = resp.data?.sscc || [];
      if (Array.isArray(ssccList) && ssccList.length) {
        const newPallets = ssccList.map((s: any) => ({ sscc: s }));
        setPallets((prev) => [...prev, ...newPallets]);
        setSelectedSscc(ssccList[0]);
        showToast("success", `Создано паллет: ${ssccList.length}`);
      } else {
        showToast("error", "Не удалось создать паллеты");
      }
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  return (
    <div className="desktop-stack" style={{ maxWidth: 900, margin: "0 auto" }}>
      <div className="desktop-card highlight">
        <div className="desktop-kicker">{docTypeTitle}</div>
        <h2 style={{ margin: "4px 0" }}>
          {docTypeTitle} №{docId || displayNumber || "-"}
        </h2>
        <p className="desktop-subtext" style={{ marginTop: 4 }}>
          {docSubtitle && <>{docSubtitle} · </>}
          Статус: <span className={`pill desktop-pill status-pill ${statusClass(status)}`}>{statusLabel(status)}</span> · Дата:{" "}
          {createdAt ? new Date(createdAt).toLocaleString("ru-RU", { dateStyle: "short", timeStyle: "short" }) : "-"}
        </p>
      </div>

      <div className="desktop-card">
        <div className="desktop-form-grid" style={{ gap: 12 }}>
          <div>
            <label>Склад</label>
            <select value={activeZone} onChange={(e) => setActiveZone(e.target.value)}>
              <option value="">Выберите склад</option>
              {locations.map((loc) => (
                <option key={loc.id} value={loc.cell_code}>
                  {loc.cell_code} · {loc.zone || loc.warehouse}
                </option>
              ))}
            </select>
          </div>
          <div>
            <label>Дата</label>
            <input value={createdAt ? new Date(createdAt).toLocaleString("ru-RU") : ""} readOnly />
          </div>
          <div>
            <label>Статус</label>
            <input value={status} readOnly />
          </div>
          <div>
            <label>Контрагент</label>
            {status === "draft" ? (
              <div className="row" style={{ gap: 8 }}>
                <select
                  value={counterpartyId ?? ""}
                  onChange={(e) => updateCounterparty(e.target.value ? Number(e.target.value) : null)}
                  style={{ minWidth: 220 }}
                >
                  <option value="">Не выбран</option>
                  {filteredContacts.map((c: any) => (
                    <option key={c.id} value={c.id}>
                      {c.name} ({c.type})
                    </option>
                  ))}
                </select>
                <input
                  placeholder="Поиск"
                  value={contactsFilter}
                  onChange={(e) => setContactsFilter(e.target.value)}
                  style={{ minWidth: 140 }}
                />
              </div>
            ) : (
              <input value={counterpartyName || "—"} readOnly />
            )}
          </div>
        </div>
      </div>

      <div className="desktop-card">
        <form className="desktop-form" onSubmit={onScan}>
          <div className="desktop-form-row">
            <div className="desktop-form-grid">
              <label>Скан / штрихкод</label>
              <input
                ref={scanInput}
                value={scanValue}
                onChange={(e) => setScanValue(e.target.value)}
                placeholder="Штрихкод товара"
                disabled={manualProductMode}
              />
            </div>
            <div className="desktop-form-grid small">
              <label>Кол-во</label>
              <input type="number" min={1} value={qty} onChange={(e) => setQty(Number(e.target.value))} />
            </div>
            {(type === "inbound" || type === "outbound") && status !== "done" && status !== "canceled" && (
              <div className="desktop-form-grid">
                <label>Паллеты?</label>
                <div className="row" style={{ gap: 8 }}>
                  <label className="row" style={{ gap: 4 }}>
                    <input type="checkbox" checked={palletsMode} onChange={(e) => setPalletsMode(e.target.checked)} />
                    Да
                  </label>
                </div>
              </div>
            )}
            <div className="desktop-form-grid">
              <label>Ручной выбор товара</label>
              <div className="row" style={{ gap: 8, flexWrap: "wrap" }}>
                <label className="row" style={{ gap: 4 }}>
                  <input
                    type="checkbox"
                    checked={manualProductMode}
                    onChange={(e) => {
                      const val = e.target.checked;
                      setManualProductMode(val);
                      if (!val) {
                        setSelectedProductId(null);
                        setProductQuery("");
                      }
                    }}
                  />
                  Да
                </label>
                {manualProductMode && (
                  <>
                    <input
                      placeholder="Поиск товара"
                      value={productQuery}
                      onChange={(e) => setProductQuery(e.target.value)}
                      style={{ minWidth: 180 }}
                    />
                    <select
                      value={selectedProductId ?? ""}
                      onChange={(e) => setSelectedProductId(e.target.value ? Number(e.target.value) : null)}
                    >
                      <option value="">Не выбран</option>
                      {productOptions.map((p) => (
                        <option key={p.id} value={p.id}>
                          {p.sku} · {p.name}
                        </option>
                      ))}
                    </select>
                  </>
                )}
              </div>
            </div>
            <button className="btn compact" type="submit" style={{ alignSelf: "flex-end" }} disabled={loading}>
              Добавить
            </button>
          </div>
        </form>
      </div>

      {(type === "inbound" || type === "outbound") && status !== "done" && status !== "canceled" && palletsMode && (
        <div className="desktop-card">
          <h3>Паллеты</h3>
          <div className="desktop-form-row" style={{ gap: 8, flexWrap: "wrap" }}>
            <div className="desktop-form-grid small">
              <label>Кол-во паллет</label>
              <input
                type="number"
                min={1}
                value={palletCount}
                onChange={(e) => setPalletCount(Number(e.target.value))}
              />
            </div>
            <button className="btn compact" type="button" onClick={createPallets}>
              Создать паллеты
            </button>
            <div className="desktop-form-grid">
              <label>Активная паллета</label>
              <select value={selectedSscc} onChange={(e) => setSelectedSscc(e.target.value)}>
                <option value="">Не выбрана</option>
                {pallets.map((p) => (
                  <option key={p.sscc} value={p.sscc}>
                    {p.sscc}
                  </option>
                ))}
              </select>
            </div>
            <div className="desktop-form-grid">
              <label>Тип упаковки</label>
              <input value={packType} onChange={(e) => setPackType(e.target.value)} />
            </div>
          </div>
          <div className="desktop-table-wrap" style={{ marginTop: 8 }}>
            <table className="table desktop-table">
              <thead>
                <tr>
                  <th>SSCC</th>
                </tr>
              </thead>
              <tbody>
                {pallets.map((p) => (
                  <tr key={p.sscc}>
                    <td>{p.sscc}</td>
                  </tr>
                ))}
                {!pallets.length && (
                  <tr>
                    <td style={{ textAlign: "center", opacity: 0.7 }}>Паллет ещё нет</td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}

      <div className="desktop-card">
        <h3>Товары</h3>
        <div className="desktop-table-wrap">
          <table className="table desktop-table">
            <thead>
              <tr>
                <th>Товар</th>
                <th>Склад</th>
                <th>Кол-во</th>
              </tr>
            </thead>
            <tbody>
              {lines.map((l) => (
                <tr key={l.id}>
                  <td>{productNames[l.product_id] || l.product_name || l.product_id}</td>
                  <td>{locationMap[l.location_id]?.cell_code || l.location_id}</td>
                  <td>{l.qty_fact}</td>
                </tr>
              ))}
              {!lines.length && (
                <tr>
                  <td colSpan={3} style={{ textAlign: "center", opacity: 0.7 }}>
                    Строк ещё нет
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>

      <div className="desktop-card" style={{ display: "flex", gap: 10, justifyContent: "flex-end", flexWrap: "wrap" }}>
        <button className="btn secondary compact" onClick={closeCard}>
          Закрыть
        </button>
        <button className="btn secondary compact" onClick={downloadPdf} disabled={loading}>
          PDF
        </button>
        <button className="btn compact" onClick={saveDoc} disabled={loading}>
          Сохранить
        </button>
        <button className="btn danger compact" onClick={finishDoc} disabled={loading}>
          {docFinishLabel(type)}
        </button>
      </div>
    </div>
  );
};
