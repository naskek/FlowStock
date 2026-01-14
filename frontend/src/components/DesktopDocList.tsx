import React, { useEffect, useMemo, useState } from "react";
import { Link, useLocation, useNavigate } from "react-router-dom";
import api, { User } from "../api";
import { LocationItem } from "../types";
import {
  errDetail,
  formatDocNumber,
  statusClass,
  statusLabel,
  docTypeLabel,
  PRODUCTION_ZONE_CODE,
  docListTitle,
  docNewLabel,
  docHint,
} from "../utils";

type SortOption = "date_desc" | "date_asc" | "id_desc" | "id_asc";

export const DesktopDocList = ({
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
  const [docs, setDocs] = useState<any[]>([]);
  const [statusFilter, setStatusFilter] = useState<string>("");
  const [warehouseFilter, setWarehouseFilter] = useState<string>("");
  const [sortOption, setSortOption] = useState<SortOption>("date_desc");
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const location = useLocation();
  const [userNames, setUserNames] = useState<Record<number, string>>({});

  useEffect(() => {
    if (!user || user.role === "viewer") {
      navigate("/desktop");
    }
  }, [user, navigate]);

  const load = async () => {
    try {
      const resp = await api.get("/docs", { params: { type } });
      const list = Array.isArray(resp.data) ? resp.data.filter((d) => d.type === type) : [];
      setDocs(list);
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  useEffect(() => {
    if (!user || user.role === "viewer") return;
    load();
    if (user.role === "admin") {
      api
        .get("/users")
        .then((r) => {
          const map: Record<number, string> = {};
          r.data.forEach((u: User) => (map[u.id] = u.login));
          setUserNames(map);
        })
        .catch(() => {});
    }
  }, [user]);

  useEffect(() => {
    if (!user || user.role === "viewer") return;
    load();
  }, [location.key]);

  useEffect(() => {
    if (docs.length && (!selectedId || !docs.find((d) => d.id === selectedId))) {
      setSelectedId(docs[0].id);
    }
  }, [docs, selectedId]);

  const filtered = useMemo(() => {
    const copy = [...docs];
    const filteredDocs = copy
      .filter((d) => (statusFilter ? d.status === statusFilter : true))
      .filter((d) => {
        if (!warehouseFilter) return true;
        const code = d.meta?.cell_code || "";
        return code.toLowerCase().includes(warehouseFilter.toLowerCase());
      })
      .sort((a, b) => {
        const field = sortOption.startsWith("date") ? "date" : "id";
        const dir = sortOption.endsWith("desc") ? -1 : 1;
        const aVal = field === "date" ? new Date(a.created_at).getTime() || 0 : Number(a.id) || 0;
        const bVal = field === "date" ? new Date(b.created_at).getTime() || 0 : Number(b.id) || 0;
        return (aVal - bVal) * dir;
      });
    return filteredDocs;
  }, [docs, statusFilter, warehouseFilter, sortOption]);

  const formatDate = (value: string) => {
    if (!value) return "—";
    const d = new Date(value);
    if (Number.isNaN(d.getTime())) return "—";
    return d.toLocaleString("ru-RU", { dateStyle: "short", timeStyle: "short" });
  };

  const defaultCell = useMemo(() => {
    const first = locations.find((l) => l.cell_code !== PRODUCTION_ZONE_CODE);
    return first?.cell_code || "";
  }, [locations]);

  const createDoc = async () => {
    try {
      const meta = warehouseFilter || defaultCell ? { cell_code: warehouseFilter || defaultCell } : undefined;
      const resp = await api.post("/docs", { type, meta });
      showToast("success", `Создан документ #${resp.data.id}`);
      await load();
      setSelectedId(resp.data.id);
      if (type === "production_issue") {
        navigate(`/desktop/production/issue/${resp.data.id}`);
      } else if (type === "production_receipt") {
        navigate(`/desktop/production/receipt/${resp.data.id}`);
      } else {
        navigate(`/desktop/docs/${type}/${resp.data.id}`);
      }
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const editDoc = () => {
    if (!selectedId) return;
    navigate(`/desktop/docs/${type}/${selectedId}`);
  };

  const deleteDoc = async () => {
    if (!selectedId) return;
    try {
      await api.post(`/docs/${selectedId}/cancel`);
      showToast("success", "Документ удалён");
      setSelectedId(null);
      load();
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const downloadPdf = async (id: number) => {
    try {
      const resp = await api.get(`/docs/${id}/pdf`, { responseType: "blob" });
      const url = window.URL.createObjectURL(new Blob([resp.data], { type: "application/pdf" }));
      const link = document.createElement("a");
      link.href = url;
      link.download = `doc_${id}.pdf`;
      link.click();
      window.URL.revokeObjectURL(url);
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const title = docListTitle(type);
  const kicker = docTypeLabel(type);
  const newLabel = docNewLabel(type);
  const hint = docHint(type);

  return (
    <div className="desktop-stack">
      <div className="desktop-card">
        <div className="desktop-kicker">{kicker}</div>
        <h2 style={{ margin: "6px 0" }}>{title}</h2>
        <p className="desktop-subtext">
          {hint || "Выберите документ или создайте новый."}
        </p>
        <div className="row desktop-row-compact" style={{ marginTop: 8 }}>
          <button className="btn compact action-small" onClick={createDoc}>
            {newLabel}
          </button>
          <button className="btn compact secondary action-small" onClick={editDoc} disabled={!selectedId}>
            Редактировать
          </button>
          <button className="btn compact danger action-small" onClick={deleteDoc} disabled={!selectedId}>
            Удалить
          </button>
        </div>
        <div className="row desktop-row-compact" style={{ marginTop: 12, marginBottom: 8 }}>
          <select value={sortOption} onChange={(e) => setSortOption(e.target.value as SortOption)}>
            <option value="date_desc">По дате ↓</option>
            <option value="date_asc">По дате ↑</option>
            <option value="id_desc">По № ↓</option>
            <option value="id_asc">По № ↑</option>
          </select>
          <select value={warehouseFilter} onChange={(e) => setWarehouseFilter(e.target.value)}>
            <option value="">Все склады</option>
            {locations.map((l) => (
              <option key={l.id} value={l.cell_code}>
                {l.cell_code} · {l.zone || l.warehouse}
              </option>
            ))}
          </select>
          <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)}>
            <option value="">Все статусы</option>
            <option value="draft">Черновик</option>
            <option value="in_progress">В процессе</option>
            <option value="done">Завершён</option>
            <option value="canceled">Удалён</option>
          </select>
        </div>
        <div className="desktop-table-wrap">
          <table className="table desktop-table actions-right">
            <thead>
              <tr>
                <th>№</th>
                <th>Дата</th>
                <th>Склад</th>
                <th>Статус</th>
                <th>Создал</th>
                <th>Действия</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((d) => (
                <tr
                  key={d.id}
                  className={selectedId === d.id ? "desktop-row-selected" : ""}
                  style={{ cursor: "pointer" }}
                  onClick={() => setSelectedId(d.id)}
                  onDoubleClick={() => navigate(`/desktop/docs/${type}/${d.id}`)}
                >
                  <td>{formatDocNumber(d)}</td>
                  <td>{formatDate(d.created_at)}</td>
                  <td>{d.meta?.cell_code || "—"}</td>
                  <td>
                    <span className={`pill desktop-pill status-pill ${statusClass(d.status)}`}>{statusLabel(d.status)}</span>
                  </td>
                  <td>{userNames[d.created_by] || `Пользователь ${d.created_by}`}</td>
                  <td className="actions-cell">
                    <div className="row" style={{ gap: 6, flexWrap: "wrap", justifyContent: "flex-end" }}>
                      <button
                        className="btn secondary compact action-small"
                        onClick={() => navigate(`/desktop/docs/${type}/${d.id}`)}
                      >
                        Открыть
                      </button>
                      <button className="btn secondary compact action-small" onClick={() => downloadPdf(d.id)}>
                        PDF
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
              {!filtered.length && (
                <tr>
                  <td colSpan={6} style={{ textAlign: "center", opacity: 0.7 }}>
                    Документов нет
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
};
