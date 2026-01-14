import React, { useEffect, useMemo, useRef, useState } from "react";
import { Routes, Route, useNavigate, Link, useLocation, Navigate, Outlet, useParams, useSearchParams } from "react-router-dom";
import api, { User } from "./api";
import { addToQueue, getQueue, syncQueue } from "./offlineQueue";
import { playError, playSuccess } from "./sound";
import { useAuth } from "./hooks/useAuth";
import { useLocations } from "./hooks/useLocations";
import { ToastView, Toast } from "./components/Toast";
import { LocationItem } from "./types";
import {
  errDetail,
  PRODUCTION_ZONE_CODE,
  statusLabel,
  statusClass,
  formatDocNumber,
  docTypeLabel,
  docNewLabel,
  docFinishLabel,
  docHint,
} from "./utils";
import { DesktopDocList } from "./components/DesktopDocList";
import { DesktopDocDetail } from "./components/DesktopDocDetail";

function App() {
  const { user, login, logout, authLoading } = useAuth();
  const [toast, setToast] = useState<Toast>(null);
  const [offline, setOffline] = useState(!navigator.onLine);
  const [queueCount, setQueueCount] = useState(0);
  const [syncProgress, setSyncProgress] = useState(0);
  const [installEvent, setInstallEvent] = useState<any>(null);
  const location = useLocation();
  const navigate = useNavigate();
  const isDesktop = location.pathname.startsWith("/desktop");
  const { locations, reloadLocations } = useLocations(!authLoading && !!user);
  const [activeCompanyName, setActiveCompanyName] = useState<string>("");
  const [activeCompanyLogo, setActiveCompanyLogo] = useState<string>("");
  const isMobileUA = /Android|webOS|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini/i.test(
    navigator.userAgent || ""
  );

  const refreshActiveCompany = async () => {
    try {
      const resp = await api.get("/company");
      const data = resp.data || {};
      setActiveCompanyName(data.name || "");
      setActiveCompanyLogo(data.logo || "");
    } catch {
      // ignore
    }
  };

  useEffect(() => {
    if (isMobileUA && location.pathname.startsWith("/desktop")) {
      navigate("/", { replace: true });
    } else if (!isMobileUA && (location.pathname === "/" || location.pathname === "/login")) {
      navigate("/desktop", { replace: true });
    }
  }, [isMobileUA, location.pathname, navigate]);

  useEffect(() => {
    const update = () => setOffline(!navigator.onLine);
    window.addEventListener("online", update);
    window.addEventListener("offline", update);
    return () => {
      window.removeEventListener("online", update);
      window.removeEventListener("offline", update);
    };
  }, []);

  useEffect(() => {
    const handler = (e: any) => {
      e.preventDefault();
      setInstallEvent(e);
    };
    window.addEventListener("beforeinstallprompt", handler);
    return () => window.removeEventListener("beforeinstallprompt", handler);
  }, []);

  useEffect(() => {
    if (isDesktop) return;
    getQueue().then((q) => setQueueCount(q.length));
  }, [isDesktop]);

  useEffect(() => {
    if (isDesktop) return;
    if (!offline) {
      syncQueue(setSyncProgress).then((q) => setQueueCount(q.length));
    }
  }, [offline, isDesktop]);

  useEffect(() => {
    if (!authLoading) {
      refreshActiveCompany();
    }
  }, [authLoading, user, location.pathname, navigate]);

  useEffect(() => {
    document.body.classList.toggle("desktop-mode", isDesktop);
    return () => document.body.classList.remove("desktop-mode");
  }, [isDesktop]);

  const showToast = (type: "success" | "error", text: string) => {
    setToast({ type, text });
    if (!isDesktop) {
      if (type === "success") playSuccess();
      else playError();
    }
    setTimeout(() => setToast(null), 2500);
  };

  return (
    <div className={isDesktop ? "desktop-layout" : "layout"}>
      <ToastView toast={toast} clear={() => setToast(null)} />
      {!isDesktop && offline && (
        <div className="offline">
          Offline · очередь: {queueCount}
          {syncProgress > 0 && syncProgress < 1 ? ` · sync ${(syncProgress * 100).toFixed(0)}%` : ""}
        </div>
      )}
      <Routes>
        <Route path="/login" element={<LoginPage onLogin={login} toast={showToast} />} />
        <Route path="/desktop/login" element={<DesktopLogin onLogin={login} toast={showToast} />} />
        <Route
          path="/desktop"
          element={
            <DesktopLayout
              user={user}
              logout={logout}
              locations={locations}
              companyName={activeCompanyName}
              companyLogo={activeCompanyLogo}
            />
          }
        >
          <Route index element={<DesktopHome />} />
          <Route
            path="docs/inbound"
            element={<DesktopDocList type="inbound" user={user} showToast={showToast} locations={locations} />}
          />
          <Route
            path="docs/inbound/:docId"
            element={<DesktopDocDetail type="inbound" user={user} showToast={showToast} locations={locations} />}
          />
          <Route
            path="docs/outbound"
            element={<DesktopDocList type="outbound" user={user} showToast={showToast} locations={locations} />}
          />
          <Route
            path="docs/outbound/:docId"
            element={<DesktopDocDetail type="outbound" user={user} showToast={showToast} locations={locations} />}
          />
          <Route
            path="production/issue/:docId"
            element={<DesktopDocDetail type="production_issue" user={user} showToast={showToast} locations={locations} />}
          />
          <Route
            path="production/receipt/:docId"
            element={<DesktopDocDetail type="production_receipt" user={user} showToast={showToast} locations={locations} />}
          />
          <Route path="inventory" element={<DesktopInventory user={user} showToast={showToast} locations={locations} />} />
          <Route path="stock" element={<DesktopStock locations={locations} />} />
          <Route path="pallets" element={<DesktopPallets showToast={showToast} />} />
          <Route path="audit" element={<DesktopAudit user={user} />} />
          <Route path="products" element={<DesktopProducts user={user} showToast={showToast} />} />
          <Route
            path="admin"
            element={
              <DesktopAdmin
                user={user}
                showToast={showToast}
                reloadLocations={reloadLocations}
                locations={locations}
                setActiveCompanyName={setActiveCompanyName}
                setActiveCompanyLogo={setActiveCompanyLogo}
                refreshActiveCompany={refreshActiveCompany}
              />
            }
          />
          <Route
            path="production/issue"
            element={<DesktopProduction user={user} showToast={showToast} locations={locations} mode="issue" />}
          />
          <Route
            path="production/receipt"
            element={<DesktopProduction user={user} showToast={showToast} locations={locations} mode="receipt" />}
          />
          <Route path="*" element={<Navigate to="/desktop" replace />} />
        </Route>
        <Route
          path="/"
          element={
            <Menu
              user={user}
              logout={logout}
              onNeedLogin={() => showToast("error", "Нужно войти")}
              installEvent={installEvent}
              clearInstall={() => setInstallEvent(null)}
              showToast={showToast}
              locations={locations}
            />
          }
        />
        <Route
          path="/docs/:type"
          element={
            <DocPage
              user={user}
              showToast={showToast}
              offline={offline}
              setQueueCount={setQueueCount}
              locations={locations}
            />
          }
        />
        <Route path="/inventory" element={<InventoryPage user={user} showToast={showToast} locations={locations} />} />
        <Route path="/stock" element={<StockPage locations={locations} />} />
        <Route path="/audit" element={<AuditPage user={user} />} />
        <Route path="/production" element={<ProductionMenu user={user} />} />
        <Route
          path="/production/issue"
          element={<ProductionPage user={user} showToast={showToast} mode="issue" locations={locations} />}
        />
        <Route
          path="/production/receipt"
          element={<ProductionPage user={user} showToast={showToast} mode="receipt" locations={locations} />}
        />
        <Route path="*" element={<Navigate to={isDesktop ? "/desktop" : "/"} replace />} />
      </Routes>
    </div>
  );
}

const LoginPage = ({
  onLogin,
  toast,
}: {
  onLogin: (l: string, p: string, redirectTo?: string) => Promise<void>;
  toast: any;
}) => {
  const [login, setLogin] = useState("");
  const [password, setPassword] = useState("");
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    try {
      await onLogin(login, password);
    } catch (err: any) {
      toast("error", err?.response?.data?.detail || "Ошибка входа");
    }
  };
  return (
    <div className="card">
      <h2>Вход</h2>
      <form onSubmit={submit}>
        <label>Логин</label>
        <input value={login} onChange={(e) => setLogin(e.target.value)} autoFocus />
        <label>Пароль (для admin можно оставить пустым)</label>
        <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} />
        <button className="btn" type="submit">
          Войти
        </button>
      </form>
    </div>
  );
};

const Menu = ({
  user,
  logout,
  onNeedLogin,
  installEvent,
  clearInstall,
  showToast,
  locations,
}: {
  user: User | null;
  logout: () => void;
  onNeedLogin: () => void;
  installEvent: any;
  clearInstall: () => void;
  showToast: (t: "success" | "error", m: string) => void;
  locations: LocationItem[];
}) => {
  if (!user) {
    onNeedLogin();
    return (
      <div className="card">
        <p>Необходим вход</p>
        <Link className="btn" to="/login">
          На экран входа
        </Link>
      </div>
    );
  }
  return (
    <div className="card">
      <div className="row" style={{ justifyContent: "space-between" }}>
        <div>
          Привет, {user.login} <span className="pill">{user.role}</span>
        </div>
        <button className="btn secondary" onClick={logout} style={{ maxWidth: 160 }}>
          Выйти
        </button>
      </div>
      {installEvent && (
        <div className="row" style={{ marginTop: 8 }}>
          <button
            className="btn secondary"
            onClick={async () => {
              installEvent.prompt();
              const choice = await installEvent.userChoice;
              if (choice.outcome === "accepted") {
                showToast("success", "PWA установлено");
              }
              clearInstall();
            }}
          >
            Установить на устройство
          </button>
        </div>
      )}
      <div className="grid tsd-menu" style={{ marginTop: 16 }}>
        <Link className="btn" to="/docs/inbound">
          Приёмка
        </Link>
        <Link className="btn" to="/docs/outbound">
          Отгрузка
        </Link>
        <Link className="btn" to="/inventory">
          Инвентаризация
        </Link>
        <Link className="btn" to="/stock">
          Остатки
        </Link>
        <Link className="btn" to="/audit">
          Аудит
        </Link>
        <Link className="btn secondary" to="/production">
          Производство
        </Link>
      </div>
    </div>
  );
};

const DocPage = ({
  user,
  showToast,
  offline,
  setQueueCount,
  locations,
}: {
  user: User | null;
  showToast: (t: "success" | "error", m: string) => void;
  offline: boolean;
  setQueueCount: (n: number) => void;
  locations: LocationItem[];
}) => {
  const navigate = useNavigate();
  const { type: typeParam } = useParams();
  const type = typeParam === "outbound" ? "outbound" : "inbound";
  const [docId, setDocId] = useState<number | null>(null);
  const [status, setStatus] = useState<string>("draft");
  const [lines, setLines] = useState<any[]>([]);
  const [mode, setMode] = useState<"scan" | "cell">("scan");
  const [activeZone, setActiveZone] = useState<string>("");
  const [scanValue, setScanValue] = useState("");
  const scanInput = useRef<HTMLInputElement>(null);
  const locationMap = useMemo(() => {
    const map: Record<number, LocationItem> = {};
    locations.forEach((l) => (map[l.id] = l));
    return map;
  }, [locations]);

  useEffect(() => {
    scanInput.current?.focus();
  }, [mode, docId]);

  useEffect(() => {
    if (!activeZone && locations.length) {
      const first = locations.find((l) => l.cell_code !== PRODUCTION_ZONE_CODE) || locations[0];
      setActiveZone(first.cell_code);
    }
  }, [locations, activeZone]);

  if (!user || user.role === "viewer") {
    navigate("/login");
    return null;
  }

  const createDoc = async () => {
    try {
      const resp = await api.post("/docs", { type });
      const id = resp.data.id;
      setDocId(id);
      setStatus(resp.data.status);
      showToast("success", `Создан документ #${id}`);
      const start = await api.post(`/docs/${id}/start`);
      setStatus(start.data.status);
      showToast("success", "Документ в работе");
    } catch (err: any) {
      console.error("createDoc error", err?.response?.data || err);
      const code = err?.response?.status ? `${err.response.status}: ` : "";
      showToast("error", `${code}${errDetail(err)}`);
    }
  };

  const fetchDoc = async (id: number) => {
    try {
      const resp = await api.get(`/docs/${id}`);
      setLines(resp.data.lines || []);
      setStatus(resp.data.status);
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const onScan = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!scanValue) return;
    if (mode === "cell") {
      setActiveZone(scanValue);
      setScanValue("");
      setMode("scan");
      showToast("success", `Зона: ${scanValue}`);
      return;
    }
    if (!docId) {
      showToast("error", "Создайте документ");
      return;
    }
    if (!activeZone) {
      showToast("error", "Выберите зону");
      return;
    }
    const payload = { barcode: scanValue, cell_code: activeZone, qty: 1 };
    setScanValue("");
    if (offline) {
      await addToQueue({
        id: Date.now().toString(),
        type: "scan",
        payload,
        url: `/docs/${docId}/scan`,
        status: "pending",
      });
      const q = await getQueue();
      setQueueCount(q.length);
      showToast("success", "В очередь");
      return;
    }
    try {
      const resp = await api.post(`/docs/${docId}/scan`, payload);
      setLines(resp.data.lines || []);
      showToast("success", "Скан ок");
    } catch (err: any) {
      showToast("error", err?.response?.data?.detail || "Ошибка");
    }
  };

  const finishDoc = async () => {
    if (!docId) return;
    try {
      const resp = await api.post(`/docs/${docId}/finish`);
      setStatus(resp.data.status);
      showToast("success", docFinishLabel(type));
    } catch (e: any) {
      showToast("error", errDetail(e));
    }
  };

  return (
    <div className="card">
      <Link to="/" className="pill">
        {"<"} Назад
      </Link>
      <h2>
        {docTypeLabel(type)} №{docId ?? "-"}
      </h2>
      <div className="row" style={{ gap: 8, flexWrap: "wrap" }}>
        <button className="btn" onClick={createDoc}>
          {docNewLabel(type)}
        </button>
        <button
          className="btn secondary"
          onClick={() => {
            if (!activeZone) {
              showToast("error", "Выберите зону для теста");
              return;
            }
            createDoc();
          }}
        >
          Быстрый тест (создать документ)
        </button>
        <button className="btn secondary" onClick={() => setMode(mode === "scan" ? "cell" : "scan")}>
          Режим: {mode === "scan" ? "Товар" : "Зона"}
        </button>
        <button className="btn secondary" onClick={() => docId && fetchDoc(docId)}>
          Обновить
        </button>
        <button className="btn danger" onClick={finishDoc} disabled={!docId}>
          {docFinishLabel(type)}
        </button>
      </div>
      <p>
        Документ: {docId ?? "-"} · статус:{" "}
        <span className={`pill status-pill ${statusClass(status)}`}>{statusLabel(status)}</span> · активная зона:{" "}
        {activeZone || "не задана"}
      </p>
      <div className="row" style={{ gap: 8, flexWrap: "wrap" }}>
        <select value={activeZone} onChange={(e) => setActiveZone(e.target.value)}>
          <option value="">Выберите зону</option>
          {locations.map((loc) => (
            <option key={loc.id} value={loc.cell_code}>
              {loc.cell_code} · {loc.zone || loc.warehouse}
            </option>
          ))}
        </select>
      </div>
      <form onSubmit={onScan}>
        <label>Скан</label>
        <input
          ref={scanInput}
          value={scanValue}
          onChange={(e) => setScanValue(e.target.value)}
          placeholder="Штрихкод или зона"
        />
      </form>
      <table className="table">
        <thead>
          <tr>
            <th>Товар</th>
            <th>Зона</th>
            <th>Кол-во</th>
          </tr>
        </thead>
        <tbody>
          {lines.map((l) => (
            <tr key={l.id}>
              <td>{l.product_id}</td>
              <td>{locationMap[l.location_id]?.cell_code || l.location_id}</td>
              <td>{l.qty_fact}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
};

const InventoryPage = ({ user, showToast, locations }: { user: User | null; showToast: any; locations: LocationItem[] }) => {
  const [taskId, setTaskId] = useState<number | null>(null);
  const [zone, setZone] = useState("");
  const [barcode, setBarcode] = useState("");
  const [qty, setQty] = useState(1);

  useEffect(() => {
    if (!zone && locations.length) {
      const first = locations.find((l) => l.cell_code !== "PROD-01");
      if (first) setZone(first.cell_code);
    }
  }, [locations, zone]);

  if (!user || user.role === "viewer") {
    return (
      <div className="card">
        <p>Недостаточно прав</p>
      </div>
    );
  }

  const createTask = async () => {
    if (!zone) {
      showToast("error", "Выберите зону");
      return;
    }
    const resp = await api.post("/inventory/tasks", { scope: { zone } });
    setTaskId(resp.data.id);
    showToast("success", "Задача создана");
  };

  const scan = async () => {
    if (!taskId) return;
    try {
      await api.post(`/inventory/tasks/${taskId}/scan`, { cell_code: zone, barcode, qty });
      setBarcode("");
      showToast("success", "Скан учтён");
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const finish = async () => {
    if (!taskId) return;
    try {
      await api.post(`/inventory/tasks/${taskId}/finish`);
      showToast("success", "Инвентаризация завершена");
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  return (
    <div className="card">
      <Link to="/" className="pill">
        {"<"} Назад
      </Link>
      <h2>Инвентаризация</h2>
      <div className="row" style={{ gap: 8 }}>
        <select value={zone} onChange={(e) => setZone(e.target.value)}>
          <option value="">Зона хранения</option>
          {locations.map((loc) => (
            <option key={loc.id} value={loc.cell_code}>
              {loc.cell_code} · {loc.zone || loc.warehouse}
            </option>
          ))}
        </select>
        <button className="btn" style={{ maxWidth: 200 }} onClick={createTask}>
          Создать задачу
        </button>
      </div>
      <div style={{ marginTop: 12 }}>
        <input placeholder="Штрихкод" value={barcode} onChange={(e) => setBarcode(e.target.value)} />
        <input type="number" value={qty} onChange={(e) => setQty(Number(e.target.value))} />
        <button className="btn" onClick={scan} disabled={!taskId}>
          Сканировать
        </button>
      </div>
      <button className="btn danger" onClick={finish} disabled={!taskId}>
        Завершить и провести
      </button>
    </div>
  );
};

const StockPage = ({ locations }: { locations: LocationItem[] }) => {
  const [rows, setRows] = useState<any[]>([]);
  const locationMap = useMemo(() => {
    const map: Record<number, LocationItem> = {};
    locations.forEach((l) => (map[l.id] = l));
    return map;
  }, [locations]);
  useEffect(() => {
    api.get("/stock").then((r) => setRows(r.data));
  }, []);
  return (
    <div className="card">
      <Link to="/" className="pill">
        {"<"} Назад
      </Link>
      <h2>Остатки</h2>
      <table className="table">
        <thead>
          <tr>
            <th>Продукт</th>
            <th>Зона</th>
            <th>Кол-во</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.id}>
              <td>{r.product_id}</td>
              <td>{locationMap[r.location_id]?.cell_code || r.location_id}</td>
              <td>{r.qty}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
};

const ProductionMenu = ({ user }: { user: User | null }) => {
  if (!user || user.role === "viewer") {
    return (
      <div className="card">
        <p>Недостаточно прав</p>
      </div>
    );
  }
  return (
    <div className="card">
      <Link to="/" className="pill">
        {"<"} Назад
      </Link>
      <h2>Производственные операции</h2>
      <p className="desktop-subtext">
        {docHint("production_issue")}
      </p>
      <p className="desktop-subtext" style={{ marginTop: -8 }}>
        {docHint("production_receipt")}
      </p>
      <div className="grid" style={{ marginTop: 12 }}>
        <Link className="btn" to="/production/issue">
          {docTypeLabel("production_issue")}
        </Link>
        <Link className="btn secondary" to="/production/receipt">
          {docTypeLabel("production_receipt")}
        </Link>
      </div>
    </div>
  );
};

const ProductionPage = ({
  user,
  showToast,
  mode,
  locations,
}: {
  user: User | null;
  showToast: (t: "success" | "error", m: string) => void;
  mode: "issue" | "receipt";
  locations: LocationItem[];
}) => {
  const docType = mode === "issue" ? "production_issue" : "production_receipt";
  return <DesktopDocList type={docType} user={user} showToast={showToast} locations={locations} />;
};

const AuditPage = ({ user }: { user: User | null }) => {
  const [rows, setRows] = useState<any[]>([]);
  useEffect(() => {
    if (!user) return;
    api.get("/audit").then((r) => setRows(r.data));
  }, [user]);
  if (!user) return null;
  return (
    <div className="card">
      <Link to="/" className="pill">
        {"<"} Назад
      </Link>
      <h2>Аудит</h2>
      <table className="table">
        <thead>
          <tr>
            <th>Действие</th>
            <th>Пользователь</th>
            <th>Сущность</th>
            <th>Время</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.id}>
              <td>{r.action}</td>
              <td>{r.user_id}</td>
              <td>
                {r.entity_type} #{r.entity_id}
              </td>
              <td>{new Date(r.created_at).toLocaleString()}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
};

const DesktopLogin = ({
  onLogin,
  toast,
}: {
  onLogin: (l: string, p: string, redirectTo?: string) => Promise<void>;
  toast: any;
}) => {
  const [login, setLogin] = useState("admin");
  const [password, setPassword] = useState("");

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    try {
      await onLogin(login, password, "/desktop");
    } catch (err: any) {
      toast("error", err?.response?.data?.detail || "Ошибка входа");
    }
  };

  return (
    <div className="desktop-auth">
      <div className="desktop-card wide">
        <div>
          <div className="desktop-kicker">WMS Desktop</div>
          <h2>Вход в рабочее место</h2>
          <p className="desktop-subtext">Полноэкранный интерфейс для работы мышью и клавиатурой.</p>
        </div>
        <form className="desktop-form" onSubmit={submit}>
          <label>Логин</label>
          <input value={login} onChange={(e) => setLogin(e.target.value)} autoFocus />
          <label>Пароль (для admin можно оставить пустым)</label>
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} />
          <div className="row" style={{ gap: 10, marginTop: 8 }}>
            <button className="btn" type="submit">
              Войти
            </button>
            <Link className="btn secondary compact" to="/login">
              Версия для ТСД
            </Link>
          </div>
        </form>
      </div>
    </div>
  );
};

const DesktopLayout = ({
  user,
  logout,
  locations: _locations,
  companyName,
  companyLogo,
}: {
  user: User | null;
  logout: () => void;
  locations: LocationItem[];
  companyName?: string;
  companyLogo?: string;
}) => {
  const location = useLocation();

  if (!user) {
    return <Navigate to="/desktop/login" replace />;
  }

  const ops = [
    { to: "/desktop/docs/inbound", label: docTypeLabel("inbound") },
    { to: "/desktop/docs/outbound", label: docTypeLabel("outbound") },
    { to: "/desktop/production/issue", label: docTypeLabel("production_issue") },
    { to: "/desktop/production/receipt", label: docTypeLabel("production_receipt") },
    { to: "/desktop/stock", label: "Остатки" },
    { to: "/desktop/pallets", label: "Паллеты" },
  ];
  const control = [{ to: "/desktop/inventory", label: "Инвентаризация" }];
  const catalog = [{ to: "/desktop/products", label: "Товары" }];
  const admin =
    user.role === "admin"
      ? [
          { to: "/desktop/admin?section=users", label: "Создание пользователей" },
          { to: "/desktop/admin?section=locations", label: "Склады и зоны" },
          { to: "/desktop/admin?section=contacts", label: "Контрагенты" },
          { to: "/desktop/admin?section=db", label: "Работа с БД" },
          { to: "/desktop/admin?section=company", label: "Компания" },
          { to: "/desktop/admin?section=audit", label: "Журнал" },
        ]
      : [];
  const isActive = (path: string) => {
    const [pathname, search] = path.split("?");
    if (location.pathname !== pathname && !location.pathname.startsWith(`${pathname}/`)) {
      return false;
    }
    if (!search) return true;
    const expected = new URLSearchParams(search);
    const current = new URLSearchParams(location.search);
    for (const [key, value] of expected.entries()) {
      if (current.get(key) !== value) return false;
    }
    return true;
  };

  return (
    <div className="desktop-shell idoklad-shell">
      <aside className="idoklad-sidebar">
        <Link className="idoklad-brand" to="/desktop">
          <div className={`idoklad-logo${companyLogo ? " has-logo" : ""}`}>
            {companyLogo ? <img src={companyLogo} alt="Логотип" /> : "TSD"}
          </div>
          <div>
            <div className="idoklad-brand-title">Warehouse</div>
            {companyName && <div className="idoklad-brand-sub">{companyName}</div>}
          </div>
        </Link>
        <nav className="idoklad-nav">
          <div className="idoklad-nav-group">
            <div className="idoklad-nav-title">Операции</div>
            {ops.map((i) => (
              <Link key={i.to} to={i.to} className={`idoklad-nav-item ${isActive(i.to) ? "active" : ""}`}>
                {i.label}
              </Link>
            ))}
          </div>
          <div className="idoklad-nav-group">
            <div className="idoklad-nav-title">Контроль</div>
            {control.map((i) => (
              <Link key={i.to} to={i.to} className={`idoklad-nav-item ${isActive(i.to) ? "active" : ""}`}>
                {i.label}
              </Link>
            ))}
          </div>
          <div className="idoklad-nav-group">
            <div className="idoklad-nav-title">Каталог</div>
            {catalog.map((i) => (
              <Link key={i.to} to={i.to} className={`idoklad-nav-item ${isActive(i.to) ? "active" : ""}`}>
                {i.label}
              </Link>
            ))}
          </div>
          {admin.length > 0 && (
            <div className="idoklad-nav-group">
              <div className="idoklad-nav-title">Админ</div>
              {admin.map((i) => (
                <Link key={i.to} to={i.to} className={`idoklad-nav-item ${isActive(i.to) ? "active" : ""}`}>
                  {i.label}
                </Link>
              ))}
            </div>
          )}
        </nav>
      </aside>
      <div className="idoklad-main">
        <div className="idoklad-topbar">
          <div className="idoklad-top-left" />
          <div style={{ marginLeft: "auto" }}>
            <DesktopUserMenu user={user} logout={logout} companyName={companyName} />
          </div>
        </div>
        <div className="desktop-content">
          <Outlet />
        </div>
      </div>
    </div>
  );
};

const DesktopDropdown = ({
  label,
  items,
  isActive,
}: {
  label: string;
  items: { to: string; label: string }[];
  isActive: (p: string) => boolean;
}) => {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, []);

  return (
    <div
      className="desktop-dropdown"
      ref={ref}
      onMouseEnter={() => setOpen(true)}
      onMouseLeave={() => setOpen(false)}
    >
      <button className="desktop-nav-trigger" type="button" onClick={() => setOpen((o) => !o)}>
        {label} ▼
      </button>
      {open && (
        <div className="desktop-dropdown-menu">
          {items.map((item) => (
            <Link key={item.to} to={item.to} className={`desktop-dropdown-item ${isActive(item.to) ? "active" : ""}`}>
              {item.label}
            </Link>
          ))}
        </div>
      )}
    </div>
  );
};

const DesktopUserMenu = ({ user, logout, companyName }: { user: User; logout: () => void; companyName?: string }) => {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener("mousedown", handler);
    return () => document.removeEventListener("mousedown", handler);
  }, []);

  return (
    <div
      className="desktop-user-menu"
      ref={ref}
      onMouseEnter={() => setOpen(true)}
      onMouseLeave={() => setOpen(false)}
    >
      <button className="desktop-user-trigger" onClick={() => setOpen((o) => !o)} type="button">
        <span className="desktop-user-login">{user.login}</span>
        <span className="pill desktop-pill">{user.role}</span>
      </button>
      {open && (
        <div className="desktop-dropdown-menu right">
          <button className="desktop-dropdown-item btn-link" onClick={logout} type="button">
            Выйти
          </button>
        </div>
      )}
    </div>
  );
};

const DesktopHome = () => <Navigate to="/desktop/docs/inbound" replace />;

const DesktopDocs = ({
  user,
  showToast,
  locations,
}: {
  user: User | null;
  showToast: (t: "success" | "error", m: string) => void;
  locations: LocationItem[];
}) => {
  const navigate = useNavigate();
  const docType = "outbound";
  const docLabel = docTypeLabel(docType);
  const newLabel = docNewLabel(docType);
  const finishLabel = docFinishLabel(docType);
  const [docId, setDocId] = useState<number | null>(null);
  const [status, setStatus] = useState<string>("draft");
  const [lines, setLines] = useState<any[]>([]);
  const [activeZone, setActiveZone] = useState("");
  const [scanValue, setScanValue] = useState("");
  const [qty, setQty] = useState(1);
  const locationMap = useMemo(() => {
    const map: Record<number, LocationItem> = {};
    locations.forEach((l) => (map[l.id] = l));
    return map;
  }, [locations]);

  useEffect(() => {
    if (!activeZone && locations.length) {
      const first = locations.find((l) => l.cell_code !== PRODUCTION_ZONE_CODE);
      if (first) setActiveZone(first.cell_code);
    }
  }, [locations, activeZone]);

  useEffect(() => {
    if (!user || user.role === "viewer") navigate("/desktop");
  }, [user]);

  useEffect(() => {
    let cancelled = false;
    const init = async () => {
      if (!user || user.role === "viewer") return;
      setDocId(null);
      setLines([]);
      setStatus("draft");
      try {
        const list = await api.get("/docs", { params: { type: docType } });
        const docs: any[] = Array.isArray(list.data) ? list.data : [];
        const existing =
          docs.find((d) => d.status === "in_progress") || docs.find((d) => d.status === "draft");
        if (existing) {
          if (cancelled) return;
          setDocId(existing.id);
          setStatus(existing.status);
          const detail = await api.get(`/docs/${existing.id}`);
          if (cancelled) return;
          setLines(detail.data.lines || []);
          setStatus(detail.data.status || existing.status);
          if (detail.data.status === "draft") {
            const started = await api.post(`/docs/${existing.id}/start`);
            if (!cancelled) setStatus(started.data.status);
          }
          return;
        }
        if (!cancelled) {
          await createDoc(true);
        }
      } catch (err: any) {
        if (!cancelled) showToast("error", errDetail(err));
      }
    };
    init();
    return () => {
      cancelled = true;
    };
  }, [docType, user]);

  const createDoc = async (silent?: boolean) => {
    try {
      const resp = await api.post("/docs", { type: docType });
      setDocId(resp.data.id);
      setLines([]);
      setStatus(resp.data.status);
      const started = await api.post(`/docs/${resp.data.id}/start`);
      setStatus(started.data.status);
      if (!silent) {
        showToast("success", `Документ #${resp.data.id} создан`);
        showToast("success", "Документ в работе");
      }
    } catch (err: any) {
      if (!silent) showToast("error", errDetail(err));
    }
  };

  const onScan = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!scanValue) return;
    if (!docId) {
      showToast("error", "Создайте или откройте документ");
      return;
    }
    if (!activeZone) {
      showToast("error", "Выберите склад");
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
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const finishDoc = async () => {
    if (!docId) return;
    try {
      const resp = await api.post(`/docs/${docId}/finish`);
      setStatus(resp.data.status);
      const detail = await api.get(`/docs/${docId}`);
      setLines(detail.data.lines || []);
      showToast("success", finishLabel);
    } catch (e: any) {
      showToast("error", e?.response?.data?.detail || "Ошибка");
    }
  };

  return (
    <div className="desktop-stack" style={{ maxWidth: 900, margin: "0 auto" }}>
      <div className="desktop-card">
        <div className="desktop-kicker">{docLabel}</div>
        <h3>
          {docLabel} №{docId ?? "-"}
        </h3>
        <p className="desktop-subtext">
          Статус:{" "}
          <span className={`pill desktop-pill status-pill ${statusClass(status)}`}>{statusLabel(status)}</span> · Склад:{" "}
          {activeZone
            ? `${activeZone} · ${
                locations.find((l) => l.cell_code === activeZone)?.zone ||
                locations.find((l) => l.cell_code === activeZone)?.warehouse ||
                ""
              }`
            : "не выбран"}
        </p>
        <div className="row" style={{ gap: 10, flexWrap: "wrap" }}>
          <button className="btn compact action-small" onClick={() => createDoc()}>
            {newLabel}
          </button>
          <button className="btn secondary compact" onClick={() => setActiveZone("")}>
            Сбросить склад
          </button>
        </div>
        <div className="desktop-form-grid" style={{ marginTop: 8 }}>
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
      </div>

      <div className="desktop-card">
        <form className="desktop-form" onSubmit={onScan}>
          <div className="desktop-form-row">
            <div className="desktop-form-grid">
              <label>Скан / штрихкод</label>
              <input value={scanValue} onChange={(e) => setScanValue(e.target.value)} placeholder="Штрихкод товара" />
            </div>
            <div className="desktop-form-grid small">
              <label>Кол-во</label>
              <input type="number" min={1} value={qty} onChange={(e) => setQty(Number(e.target.value))} />
            </div>
            <button className="btn compact" type="submit" style={{ alignSelf: "flex-end" }}>
              Добавить
            </button>
          </div>
        </form>
      </div>

      <div className="desktop-card">
        <h3>Строки документа</h3>
        <div className="desktop-table-wrap">
          <table className="table desktop-table">
            <thead>
              <tr>
                <th>ID строки</th>
                <th>Товар</th>
                <th>Склад</th>
                <th>Кол-во</th>
              </tr>
            </thead>
            <tbody>
              {lines.map((l) => (
                <tr key={l.id}>
                  <td>{l.id}</td>
                  <td>{l.product_id}</td>
                  <td>{locationMap[l.location_id]?.cell_code || l.location_id}</td>
                  <td>{l.qty_fact}</td>
                </tr>
              ))}
              {!lines.length && (
                <tr>
                  <td colSpan={4} style={{ textAlign: "center", opacity: 0.7 }}>
                    Строк еще нет
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
        <div className="desktop-card" style={{ textAlign: "right" }}>
          <button className="btn danger compact" onClick={finishDoc} disabled={!docId} style={{ maxWidth: 280 }}>
            {finishLabel}
          </button>
        </div>
    </div>
  );
};

const DesktopInventory = ({
  user,
  showToast,
  locations,
}: {
  user: User | null;
  showToast: (t: "success" | "error", m: string) => void;
  locations: LocationItem[];
}) => {
  const [taskId, setTaskId] = useState<number | null>(null);
  const [zone, setZone] = useState("");
  const [barcode, setBarcode] = useState("");
  const [qty, setQty] = useState(1);

  useEffect(() => {
    if (!zone && locations.length) {
      const first = locations.find((l) => l.cell_code !== PRODUCTION_ZONE_CODE);
      if (first) setZone(first.cell_code);
    }
  }, [locations, zone]);

  if (!user || user.role === "viewer") return <p>Недостаточно прав</p>;

  const createTask = async () => {
    if (!zone) {
      showToast("error", "Выберите зону");
      return;
    }
    try {
      const resp = await api.post("/inventory/tasks", { scope: { zone } });
      setTaskId(resp.data.id);
      showToast("success", "Задача создана");
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const scan = async () => {
    if (!taskId) return;
    try {
      await api.post(`/inventory/tasks/${taskId}/scan`, { cell_code: zone, barcode, qty });
      setBarcode("");
      showToast("success", "Скан учтен");
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const finish = async () => {
    if (!taskId) return;
    try {
      await api.post(`/inventory/tasks/${taskId}/finish`);
      showToast("success", "Инвентаризация завершена");
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  return (
    <div className="desktop-stack">
      <div className="desktop-card">
        <div className="desktop-kicker">Инвентаризация</div>
        <h3>Задача #{taskId ?? "-"}</h3>
        <div className="desktop-form-row">
          <div className="desktop-form-grid">
            <label>Зона</label>
            <select value={zone} onChange={(e) => setZone(e.target.value)}>
              <option value="">Зона хранения</option>
              {locations.map((loc) => (
                <option key={loc.id} value={loc.cell_code}>
                  {loc.cell_code} · {loc.zone || loc.warehouse}
                </option>
              ))}
            </select>
          </div>
          <button className="btn compact" style={{ alignSelf: "flex-end" }} onClick={createTask}>
            Создать задачу
          </button>
        </div>
      </div>
      <div className="desktop-card">
        <div className="desktop-form-row">
          <div className="desktop-form-grid">
            <label>Штрихкод</label>
            <input value={barcode} onChange={(e) => setBarcode(e.target.value)} placeholder="Введите или сканируйте" />
          </div>
          <div className="desktop-form-grid small">
            <label>Кол-во</label>
            <input type="number" min={1} value={qty} onChange={(e) => setQty(Number(e.target.value))} />
          </div>
          <button className="btn compact" onClick={scan} disabled={!taskId} style={{ alignSelf: "flex-end" }}>
            Сканировать
          </button>
        </div>
        <button className="btn danger compact" onClick={finish} disabled={!taskId} style={{ marginTop: 12 }}>
          Завершить и провести
        </button>
      </div>
    </div>
  );
};

const DesktopStock = ({ locations }: { locations: LocationItem[] }) => {
  const [rows, setRows] = useState<any[]>([]);
  const [filter, setFilter] = useState("");
  const [productFilter, setProductFilter] = useState<string>("");
  const [products, setProducts] = useState<any[]>([]);
  const locationMap = useMemo(() => {
    const map: Record<number, LocationItem> = {};
    locations.forEach((l) => (map[l.id] = l));
    return map;
  }, [locations]);

  useEffect(() => {
    api.get("/stock").then((r) => setRows(r.data));
  }, []);
  useEffect(() => {
    api
      .get("/products")
      .then((r) => setProducts(r.data))
      .catch(() => setProducts([]));
  }, []);

  const filtered = rows.filter(
    (r) =>
      !filter ||
      `${r.product_id}`.toLowerCase().includes(filter.toLowerCase()) ||
      `${locationMap[r.location_id]?.cell_code || r.location_id}`.toLowerCase().includes(filter.toLowerCase()) ||
      `${locationMap[r.location_id]?.zone || ""}`.toLowerCase().includes(filter.toLowerCase())
  ).filter(
    (r) => !productFilter || String(r.product_id) === productFilter
  );

  return (
    <div className="desktop-stack">
      <div className="desktop-card highlight">
        <div className="desktop-kicker">Остатки</div>
        <h2 style={{ margin: "6px 0" }}>Поиск по складу</h2>
        <p className="desktop-subtext">Смотрите фактические остатки по зонам и товарам.</p>
        <div className="desktop-form-grid" style={{ marginTop: 8, gap: 10 }}>
          <div className="desktop-form-grid">
            <label>Фильтр</label>
            <input
              placeholder="Товар или зона"
              value={filter}
              onChange={(e) => setFilter(e.target.value)}
            />
          </div>
          <div className="desktop-form-grid">
            <label>Товар</label>
            <select value={productFilter} onChange={(e) => setProductFilter(e.target.value)}>
              <option value="">Все товары</option>
              {products.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.sku} · {p.name}
                </option>
              ))}
            </select>
          </div>
          <div className="desktop-form-grid">
            <label>Зона</label>
            <select onChange={(e) => setFilter(e.target.value)} value={filter}>
              <option value="">Все зоны</option>
              {locations.map((l) => (
                <option key={l.id} value={l.cell_code}>
                  {l.cell_code} · {l.zone || l.warehouse}
                </option>
              ))}
            </select>
          </div>
        </div>
      </div>
      <div className="desktop-card">
        <div className="desktop-table-wrap">
          <table className="table desktop-table">
            <thead>
              <tr>
                <th>Продукт</th>
                <th>Зона</th>
                <th>Кол-во</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((r) => (
                <tr key={r.id}>
                  <td>{r.product_id}</td>
                  <td>{locationMap[r.location_id]?.cell_code || r.location_id}</td>
                  <td>{r.qty}</td>
                </tr>
              ))}
              {!filtered.length && (
                <tr>
                  <td colSpan={3} style={{ textAlign: "center", opacity: 0.7 }}>
                    Ничего не найдено
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

const DesktopProduction = ({
  user,
  showToast,
  locations,
  mode,
}: {
  user: User | null;
  showToast: (t: "success" | "error", m: string) => void;
  locations: LocationItem[];
  mode: "issue" | "receipt";
}) => {
  const docType = mode === "issue" ? "production_issue" : "production_receipt";
  return <DesktopDocList type={docType} user={user} showToast={showToast} locations={locations} />;
};

const DesktopAudit = ({ user }: { user: User | null }) => {
  const [rows, setRows] = useState<any[]>([]);
  useEffect(() => {
    if (!user) return;
    api.get("/audit").then((r) => setRows(r.data));
  }, [user]);
  if (!user) return null;
  return (
    <div className="desktop-card">
      <div className="desktop-kicker">Аудит</div>
      <h3>Действия пользователей</h3>
      <div className="desktop-table-wrap">
        <table className="table desktop-table">
          <thead>
            <tr>
              <th>Действие</th>
              <th>Пользователь</th>
              <th>Сущность</th>
              <th>Время</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.id}>
                <td>{r.action}</td>
                <td>{r.user_id}</td>
                <td>
                  {r.entity_type} #{r.entity_id}
                </td>
                <td>{new Date(r.created_at).toLocaleString()}</td>
              </tr>
            ))}
            {!rows.length && (
              <tr>
                <td colSpan={4} style={{ textAlign: "center", opacity: 0.7 }}>
                  Записей нет
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
};

const DesktopPallets = ({
  showToast,
}: {
  showToast: (t: "success" | "error", m: string) => void;
}) => {
  const [pallets, setPallets] = useState<any[]>([]);
  const [statusFilter, setStatusFilter] = useState("");
  const [cellFilter, setCellFilter] = useState("");
  const [searchSSCC, setSearchSSCC] = useState("");
  const [selectedHU, setSelectedHU] = useState<any | null>(null);
  const [loading, setLoading] = useState(false);
  const [createAutoCell, setCreateAutoCell] = useState("");
  const [putawayCell, setPutawayCell] = useState("");
  const [fillForm, setFillForm] = useState({
    docId: "",
    sscc: "",
    productId: "",
    packType: "",
    packCount: "",
    barcode: "",
    batch: "",
    expiry: "",
  });
  const [consumeForm, setConsumeForm] = useState({
    docId: "",
    sscc: "",
    productId: "",
    qty: "",
    batch: "",
  });
  const [availableProducts, setAvailableProducts] = useState<{ id: number; name?: string }[]>([]);
  const [availableFillDocs, setAvailableFillDocs] = useState<any[]>([]);
  const [availableIssueDocs, setAvailableIssueDocs] = useState<any[]>([]);

  const loadPallets = async () => {
    try {
      setLoading(true);
      const resp = await api.get("/handling-units", {
        params: {
          status: statusFilter || undefined,
          cell_code: cellFilter || undefined,
        },
      });
      setPallets(Array.isArray(resp.data) ? resp.data : []);
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
    setLoading(false);
  };

  const loadDocOptions = async () => {
    try {
      const resp = await api.get("/docs");
      const docs = Array.isArray(resp.data) ? resp.data : [];
      setAvailableFillDocs(
        docs.filter(
          (d) =>
            ["inbound", "production_receipt"].includes(d.type) &&
            d.status !== "done" &&
            d.status !== "canceled"
        )
      );
      setAvailableIssueDocs(
        docs.filter(
          (d) =>
            d.type === "production_issue" &&
            d.status !== "done" &&
            d.status !== "canceled"
        )
      );
      // сброс списка товаров при смене документов
      setAvailableProducts([]);
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const loadDocProducts = async (docId?: string) => {
    if (!docId) {
      setAvailableProducts([]);
      setFillForm((f) => ({ ...f, productId: "" }));
      return;
    }
    try {
      const resp = await api.get(`/docs/${docId}`);
      const lines = Array.isArray(resp.data?.lines) ? resp.data.lines : [];
      const uniq: Record<number, any> = {};
      lines.forEach((l: any) => {
        if (l.product_id && !uniq[l.product_id]) {
          uniq[l.product_id] = { id: l.product_id, name: l.product_name || "" };
        }
      });
      const list = Object.values(uniq) as { id: number; name?: string }[];
      setAvailableProducts(list);
      if (list.length) {
        setFillForm((f) => ({ ...f, productId: f.productId || String(list[0].id) }));
      }
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const viewHU = async (scan?: string) => {
    const sscc = (scan || searchSSCC).trim();
    if (!sscc) {
      showToast("error", "Укажите SSCC");
      return;
    }
    try {
      const resp = await api.get(`/handling-units/${encodeURIComponent(sscc)}`);
      const hu = resp.data;
      setSelectedHU(hu);
      setSearchSSCC(hu?.sscc || sscc);
      setFillForm((f) => ({ ...f, sscc: hu?.sscc || sscc }));
      setConsumeForm((f) => ({
        ...f,
        sscc: hu?.sscc || sscc,
        productId: hu?.contents?.[0]?.product_id ? String(hu.contents[0].product_id) : f.productId,
      }));
      setPutawayCell(hu?.location?.cell_code || hu?.location_id || "");
    } catch (err: any) {
      showToast("error", errDetail(err));
      setSelectedHU(null);
    }
  };

  const deleteHU = async (sscc: string) => {
    if (!sscc) return;
    if (!confirm(`Удалить паллету ${sscc}?`)) return;
    try {
      await api.delete(`/handling-units/${encodeURIComponent(sscc)}`);
      showToast("success", "Паллета удалена");
      setSelectedHU((prev: any) => (prev?.sscc === sscc ? null : prev));
      await loadPallets();
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const putawayHU = async () => {
    if (!selectedHU?.sscc) {
      showToast("error", "Откройте паллету");
      return;
    }
    try {
      await api.post(`/handling-units/${encodeURIComponent(selectedHU.sscc)}/putaway`, {
        cell_code: putawayCell || undefined,
      });
      showToast("success", "Статус: на складе");
      await viewHU(selectedHU.sscc);
      await loadPallets();
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const createAutoHU = async () => {
    try {
      const resp = await api.post("/handling-units/auto", { cell_code: createAutoCell || undefined });
      const sscc = resp.data?.sscc || resp.data?.sscc18 || "";
      showToast("success", sscc ? `Паллета создана: ${sscc}` : "Паллета создана");
      await loadPallets();
      if (sscc) await viewHU(sscc);
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const fillPallet = async () => {
    const { docId, sscc, productId, packType, packCount, barcode, batch, expiry } = fillForm;
    if (!docId || !sscc || !packType || !packCount) {
      showToast("error", "Документ, SSCC, тип и количество обязательны");
      return;
    }
    const payload: any = {
      pack_type: packType,
      pack_count: Number(packCount),
    };
    if (productId) payload.product_id = Number(productId);
    if (barcode) payload.barcode = barcode;
    if (batch) payload.batch = batch;
    if (expiry) payload.expiry_date = expiry;
    try {
      await api.post(`/docs/${docId}/pallets/${encodeURIComponent(sscc)}/manual`, payload);
      showToast("success", "Паллета наполнена");
      setFillForm((f) => ({ ...f, packCount: "", barcode: "", batch: "", expiry: "" }));
      await viewHU(sscc);
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const consumePallet = async () => {
    const { docId, sscc, productId, qty, batch } = consumeForm;
    if (!sscc || !productId || !qty) {
      showToast("error", "SSCC, товар и количество обязательны");
      return;
    }
    const payload: any = {
      product_id: Number(productId),
      qty: Number(qty),
    };
    if (docId) payload.doc_id = Number(docId);
    if (batch) payload.batch = batch;
    try {
      await api.post(`/handling-units/${encodeURIComponent(sscc)}/consume`, payload);
      showToast("success", "Списано с паллеты");
      setConsumeForm((f) => ({ ...f, qty: "" }));
      await viewHU(sscc);
      await loadPallets();
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  useEffect(() => {
    loadPallets();
    loadDocOptions();
  }, []);

  return (
    <div className="desktop-card">
      <div className="desktop-kicker">Паллеты / SSCC</div>
      <div className="desktop-split" style={{ gap: 16, alignItems: "flex-start" }}>
        <div className="desktop-form" style={{ gap: 10, flex: 1 }}>
          <h3>Создание паллеты</h3>
          <div className="desktop-form-row" style={{ gap: 8, flexWrap: "wrap" }}>
            <input
              placeholder="Ячейка (опц.)"
              value={createAutoCell}
              onChange={(e) => setCreateAutoCell(e.target.value)}
              style={{ minWidth: 160 }}
            />
            <button className="btn compact" type="button" onClick={createAutoHU}>
              Создать паллету SSCC
            </button>
          </div>

          <h3 style={{ marginTop: 12 }}>Наполнение паллеты</h3>
          <div className="desktop-form-row" style={{ gap: 8, flexWrap: "wrap" }}>
            <select
              value={fillForm.docId}
              onChange={(e) => {
                const val = e.target.value;
                setFillForm((f) => ({ ...f, docId: val }));
                loadDocProducts(val);
              }}
              style={{ minWidth: 220 }}
            >
              <option value="">Документ</option>
              {availableFillDocs.map((d) => (
                <option key={d.id} value={d.id}>
                  #{d.id} · {docTypeLabel(d.type)} · {formatDocNumber({ id: d.id, type: d.type, created_at: d.created_at })}
                </option>
              ))}
            </select>
            <input
              placeholder="SSCC"
              value={fillForm.sscc}
              onChange={(e) => setFillForm((f) => ({ ...f, sscc: e.target.value }))}
              style={{ width: 160 }}
            />
            <select
              value={fillForm.productId}
              onChange={(e) => setFillForm((f) => ({ ...f, productId: e.target.value }))}
              style={{ minWidth: 180 }}
            >
              <option value="">Товар из документа</option>
              {availableProducts.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.id}{p.name ? ` · ${p.name}` : ""}
                </option>
              ))}
            </select>
            <input
              placeholder="Тип упаковки (BOX15...)"
              value={fillForm.packType}
              onChange={(e) => setFillForm((f) => ({ ...f, packType: e.target.value }))}
              style={{ minWidth: 160 }}
            />
            <input
              placeholder="Кол-во упаковок"
              type="number"
              min={0}
              step="0.001"
              value={fillForm.packCount}
              onChange={(e) => setFillForm((f) => ({ ...f, packCount: e.target.value }))}
              style={{ width: 150 }}
            />
            <input
              placeholder="Штрихкод (опц.)"
              value={fillForm.barcode}
              onChange={(e) => setFillForm((f) => ({ ...f, barcode: e.target.value }))}
              style={{ minWidth: 160 }}
            />
            <input
              placeholder="Партия (опц.)"
              value={fillForm.batch}
              onChange={(e) => setFillForm((f) => ({ ...f, batch: e.target.value }))}
              style={{ minWidth: 120 }}
            />
            <input
              type="date"
              value={fillForm.expiry}
              onChange={(e) => setFillForm((f) => ({ ...f, expiry: e.target.value }))}
            />
            <button className="btn compact action-small" type="button" onClick={fillPallet}>
              Добавить в паллету
            </button>
          </div>

          <h3 style={{ marginTop: 12 }}>Списание с паллеты</h3>
          <div className="desktop-form-row" style={{ gap: 8, flexWrap: "wrap" }}>
            <select
              value={consumeForm.docId}
              onChange={(e) => setConsumeForm((f) => ({ ...f, docId: e.target.value }))}
              style={{ minWidth: 220 }}
            >
              <option value="">Документ (issue)</option>
              {availableIssueDocs.map((d) => (
                <option key={d.id} value={d.id}>
                  #{d.id} · {docTypeLabel(d.type)} · {formatDocNumber({ id: d.id, type: d.type, created_at: d.created_at })}
                </option>
              ))}
            </select>
            <input
              placeholder="SSCC"
              value={consumeForm.sscc}
              onChange={(e) => setConsumeForm((f) => ({ ...f, sscc: e.target.value }))}
              style={{ width: 160 }}
            />
            <select
              value={consumeForm.productId}
              onChange={(e) => setConsumeForm((f) => ({ ...f, productId: e.target.value }))}
              style={{ minWidth: 180 }}
            >
              <option value="">Товар из паллеты</option>
              {(selectedHU?.contents || []).map((c: any, idx: number) => (
                <option key={c.id || idx} value={c.product_id}>
                  {c.product_id}
                </option>
              ))}
            </select>
            <input
              placeholder="Товар ID (если нет в списке)"
              type="number"
              value={consumeForm.productId}
              onChange={(e) => setConsumeForm((f) => ({ ...f, productId: e.target.value }))}
              style={{ width: 180 }}
            />
            <input
              placeholder="Кол-во"
              type="number"
              min={0}
              step="0.001"
              value={consumeForm.qty}
              onChange={(e) => setConsumeForm((f) => ({ ...f, qty: e.target.value }))}
              style={{ width: 120 }}
            />
            <input
              placeholder="Партия (опц.)"
              value={consumeForm.batch}
              onChange={(e) => setConsumeForm((f) => ({ ...f, batch: e.target.value }))}
              style={{ minWidth: 120 }}
            />
            <button className="btn danger compact action-small" type="button" onClick={consumePallet}>
              Списать
            </button>
          </div>
        </div>

        <div className="desktop-form" style={{ gap: 8, flex: 1 }}>
          <h3>Поиск / детали</h3>
          <div className="desktop-form-row" style={{ gap: 8, flexWrap: "wrap" }}>
            <input
              placeholder="SSCC"
              value={searchSSCC}
              onChange={(e) => setSearchSSCC(e.target.value)}
              style={{ minWidth: 180 }}
            />
            <button className="btn compact secondary" type="button" onClick={() => viewHU()}>
              Открыть
            </button>
          </div>
          {selectedHU && (
            <div className="desktop-card" style={{ background: "#f8fafc" }}>
              <div className="desktop-kicker">Паллета</div>
              <div style={{ fontSize: 15, fontWeight: 600 }}>{selectedHU.sscc}</div>
              <div style={{ display: "flex", gap: 12, flexWrap: "wrap", marginTop: 6 }}>
                <span className={`badge ${selectedHU.status || ""}`}>Статус: {selectedHU.status || "-"}</span>
                <span>Локация: {selectedHU.location?.cell_code || selectedHU.location_id || "-"}</span>
                {selectedHU.source_doc_id && <span>Источник док: {selectedHU.source_doc_id}</span>}
                {selectedHU.reserved_doc_id && <span>Резерв док: {selectedHU.reserved_doc_id}</span>}
              </div>
              <div className="row" style={{ gap: 8, marginTop: 6, flexWrap: "wrap" }}>
                <button className="btn secondary compact action-small" type="button" onClick={() => viewHU(selectedHU.sscc)}>
                  Обновить
                </button>
                <button className="btn danger compact action-small" type="button" onClick={() => deleteHU(selectedHU.sscc)}>
                  Удалить паллету
                </button>
              </div>
              <div className="desktop-form-row" style={{ gap: 8, flexWrap: "wrap", marginTop: 6 }}>
                <input
                  placeholder="Ячейка для размещения"
                  value={putawayCell}
                  onChange={(e) => setPutawayCell(e.target.value)}
                  style={{ minWidth: 180 }}
                />
                <button className="btn compact action-small" type="button" onClick={putawayHU}>
                  Поставить на склад
                </button>
              </div>
              <div style={{ marginTop: 8 }}>
                <div className="desktop-kicker">Содержимое</div>
                <div className="desktop-table-wrap">
                  <table className="table desktop-table">
                    <thead>
                      <tr>
                        <th>Товар</th>
                        <th>Qty</th>
                        <th>Партия</th>
                        <th>Годен до</th>
                      </tr>
                    </thead>
                    <tbody>
                      {(selectedHU.contents || []).map((c: any, idx: number) => (
                        <tr key={c.id || idx}>
                          <td>{c.product_id}</td>
                          <td>{c.qty}</td>
                          <td>{c.batch || "-"}</td>
                          <td>{c.expiry_date || "-"}</td>
                        </tr>
                      ))}
                      {(!selectedHU.contents || !selectedHU.contents.length) && (
                        <tr>
                          <td colSpan={4} style={{ textAlign: "center", opacity: 0.7 }}>
                            Пусто
                          </td>
                        </tr>
                      )}
                    </tbody>
                  </table>
                </div>
              </div>
            </div>
          )}
        </div>
      </div>

      <div className="desktop-card" style={{ marginTop: 16 }}>
        <div className="desktop-kicker">Список паллет</div>
        <div className="desktop-form-row" style={{ gap: 8, flexWrap: "wrap", marginBottom: 8 }}>
          <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)}>
            <option value="">Все статусы</option>
            <option value="created">Создана</option>
            <option value="putaway">На складе</option>
            <option value="reserved">Зарезервирована</option>
            <option value="shipped">Отгружена</option>
            <option value="quarantine">Карантин</option>
          </select>
          <input
            placeholder="Ячейка"
            value={cellFilter}
            onChange={(e) => setCellFilter(e.target.value)}
            style={{ minWidth: 140 }}
          />
          <button className="btn compact secondary" type="button" onClick={loadPallets} disabled={loading}>
            Обновить
          </button>
        </div>
        <div className="desktop-table-wrap">
          <table className="table desktop-table actions-right">
            <thead>
              <tr>
                <th>SSCC</th>
                <th>Статус</th>
                <th>Ячейка</th>
                <th>Документ (источник)</th>
                <th>Резерв</th>
                <th>Действия</th>
              </tr>
            </thead>
            <tbody>
              {pallets.map((p) => (
                <tr key={p.id || p.sscc}>
                  <td>{p.sscc}</td>
                  <td>{p.status}</td>
                  <td>{p.location?.cell_code || p.location_id || "-"}</td>
                  <td>{p.source_doc_id || "-"}</td>
                  <td>{p.reserved_doc_id || "-"}</td>
                  <td className="actions-cell">
                    <button className="btn secondary compact action-small" type="button" onClick={() => viewHU(p.sscc)}>
                      Открыть
                    </button>
                    <button className="btn danger compact action-small" type="button" onClick={() => deleteHU(p.sscc)}>
                      Удалить
                    </button>
                  </td>
                </tr>
              ))}
              {!pallets.length && (
                <tr>
                  <td colSpan={6} style={{ textAlign: "center", opacity: 0.7 }}>
                    Паллет нет
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

const DesktopProducts = ({ user, showToast }: { user: User | null; showToast: (t: "success" | "error", m: string) => void }) => {
  const normalizeProductPayload = (p: any) => ({
    sku: (p.sku || "").trim(),
    name: (p.name || "").trim(),
    barcode_ean: (p.barcode_ean || "").trim(),
    unit: (p.unit || "pcs").trim() || "pcs",
    pack_qty: p.pack_qty === undefined || p.pack_qty === null ? 1 : Number(p.pack_qty) || 1,
    brand: p.brand ? String(p.brand).trim() : null,
    is_active: p.is_active ?? true,
  });
  const [products, setProducts] = useState<any[]>([]);
  const [query, setQuery] = useState("");
  const [loading, setLoading] = useState(false);
  const [showCreate, setShowCreate] = useState(false);
  const [form, setForm] = useState({
    gtin: "",
    sku: "",
    name: "",
    packType: "unit",
    unit: "шт",
    brand: "",
    packSize: 1,
    group: "",
  });
  const [editId, setEditId] = useState<number | null>(null);
  const [editForm, setEditForm] = useState({
    sku: "",
    name: "",
    barcode: "",
    unit: "шт",
    pack_qty: 1,
    brand: "",
    is_active: true,
  });
  const [createPackings, setCreatePackings] = useState<{ pack_type: string; qty_per_pack: string }[]>([]);
  const [editPackings, setEditPackings] = useState<any[]>([]);
  const [pendingRequests, setPendingRequests] = useState<any[]>([]);

  const loadPending = () => {
    try {
      const raw = localStorage.getItem("product_edit_requests");
      if (raw) setPendingRequests(JSON.parse(raw));
      else setPendingRequests([]);
    } catch {
      setPendingRequests([]);
    }
  };

  const loadProductPackings = async (productId: number) => {
    try {
      const resp = await api.get("/packing-profiles", { params: { product_id: productId } });
      setEditPackings(Array.isArray(resp.data) ? resp.data : []);
    } catch (err: any) {
      showToast("error", `Упаковки не загрузились: ${errDetail(err)}`);
      setEditPackings([]);
    }
  };

  const saveEditPackings = async (productId: number) => {
    for (const p of editPackings) {
      if (!p.pack_type || !p.qty_per_pack) continue;
      const payload = {
        product_id: productId,
        pack_type: p.pack_type,
        qty_per_pack: Number(p.qty_per_pack),
        is_active: p.is_active ?? true,
      };
      if (p.id) {
        await api.put(`/packing-profiles/${p.id}`, payload);
      } else {
        await api.post("/packing-profiles", payload);
      }
    }
  };

  const deleteEditPacking = async (p: any, idx: number) => {
    if (p.id) {
      if (!confirm("Удалить профиль упаковки?")) return;
      try {
        await api.delete(`/packing-profiles/${p.id}`);
        showToast("success", "Профиль удалён");
        setEditPackings((prev) => prev.filter((_, i) => i !== idx));
      } catch (err: any) {
        showToast("error", errDetail(err));
      }
    } else {
      setEditPackings((prev) => prev.filter((_, i) => i !== idx));
    }
  };

  useEffect(() => {
    load();
    loadPending();
  }, []); // initial load

  const load = async (search?: string) => {
    try {
      setLoading(true);
      const resp = await api.get("/products", { params: search ? { q: search } : {} });
      setProducts(resp.data);
    } catch (err: any) {
      console.error("load products", err?.response?.data || err);
      showToast("error", `Ошибка: ${err?.response?.status || ""} ${errDetail(err)}`);
    }
    setLoading(false);
  };

  const filtered = useMemo(() => {
    const source = Array.isArray(products) ? products : [];
    const q = query.trim().toLowerCase();
    const filteredList = q
      ? source.filter(
          (p: any) =>
            (p.sku || "").toLowerCase().includes(q) ||
            (p.name || "").toLowerCase().includes(q) ||
            (p.barcode_ean || "").toLowerCase().includes(q)
        )
      : source;
    return filteredList.slice(0, 200);
  }, [products, query]);

  if (showCreate) {
    return (
      <div className="desktop-stack">
        <div className="desktop-card highlight">
          <div className="row" style={{ justifyContent: "space-between", alignItems: "center" }}>
            <div>
              <div className="desktop-kicker">Новая карточка товара</div>
              <h3 style={{ margin: "6px 0" }}>Создание товара</h3>
            </div>
            <button className="btn secondary compact action-small" onClick={() => setShowCreate(false)}>
              Назад к каталогу
            </button>
          </div>
          <div className="desktop-form-row" style={{ gap: 12, flexWrap: "wrap", marginTop: 10 }}>
            <input
              placeholder="GTIN* / Штрихкод (скан)"
              value={form.gtin}
              onChange={(e) => setForm((f) => ({ ...f, gtin: e.target.value }))}
              style={{ minWidth: 200 }}
              autoFocus
            />
            <input
              placeholder="SKU (опционально)"
              value={form.sku}
              onChange={(e) => setForm((f) => ({ ...f, sku: e.target.value }))}
              style={{ minWidth: 180 }}
            />
            <input
              placeholder="Наименование*"
              value={form.name}
              onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
              style={{ minWidth: 260 }}
            />
            <select
              value={form.packType}
              onChange={(e) => {
                const nextType = e.target.value;
                const nextPack = nextType === "unit" ? 1 : form.packSize || 1;
                setForm((f) => ({ ...f, packType: nextType, packSize: nextPack }));
              }}
              style={{ minWidth: 180 }}
            >
              <option value="unit">Единица товара</option>
              <option value="box">Упаковка</option>
              <option value="pallet">Паллет</option>
            </select>
            <input
              placeholder="Ед. изм. (шт, л)"
              value={form.unit}
              onChange={(e) => setForm((f) => ({ ...f, unit: e.target.value }))}
              style={{ minWidth: 120 }}
            />
            <input
              placeholder="Бренд"
              value={form.brand}
              onChange={(e) => setForm((f) => ({ ...f, brand: e.target.value }))}
              style={{ minWidth: 160 }}
            />
            <div className="row" style={{ gap: 8, alignItems: "center" }}>
              <input
                placeholder="Группа товаров (опц.)"
                value={form.group}
                onChange={(e) => setForm((f) => ({ ...f, group: e.target.value }))}
                style={{ minWidth: 200 }}
              />
              <button
                className="btn secondary compact action-small"
                type="button"
                onClick={() => showToast("error", "Создание групп товаров ещё не реализовано")}
              >
                Создать группу товаров
              </button>
            </div>
          </div>
          {form.packType !== "unit" && (
            <div className="desktop-form-row" style={{ gap: 10, flexWrap: "wrap", marginTop: 8 }}>
              <input
                type="number"
                min={1}
                placeholder={form.packType === "box" ? "Штук в упаковке" : "Упаковок на паллете"}
                value={form.packSize}
                onChange={(e) => setForm((f) => ({ ...f, packSize: Number(e.target.value) || 1 }))}
                style={{ minWidth: 200 }}
              />
            </div>
          )}
          <div className="desktop-card" style={{ marginTop: 12 }}>
            <div className="desktop-kicker">Упаковки для SSCC</div>
            <div className="desktop-form-row" style={{ gap: 8, flexWrap: "wrap" }}>
              <input
                placeholder="pack_type (например BOX15)"
                value={createPackings[0]?.pack_type ?? ""}
                onChange={(e) => {
                  const next = [...createPackings];
                  if (!next.length) next.push({ pack_type: "", qty_per_pack: "" });
                  next[0].pack_type = e.target.value;
                  setCreatePackings(next);
                }}
                style={{ minWidth: 160 }}
              />
              <input
                type="number"
                min={0}
                step="0.001"
                placeholder="qty_per_pack"
                value={createPackings[0]?.qty_per_pack ?? ""}
                onChange={(e) => {
                  const next = [...createPackings];
                  if (!next.length) next.push({ pack_type: "", qty_per_pack: "" });
                  next[0].qty_per_pack = e.target.value;
                  setCreatePackings(next);
                }}
                style={{ minWidth: 140 }}
              />
              <button
                className="btn secondary compact action-small"
                type="button"
                onClick={() => setCreatePackings((prev) => [...prev, { pack_type: "", qty_per_pack: "" }])}
              >
                Добавить ещё упаковку
              </button>
            </div>
            {createPackings.length > 1 && (
              <div className="desktop-table-wrap" style={{ marginTop: 8 }}>
                <table className="table desktop-table actions-right">
                  <thead>
                    <tr>
                      <th>Тип</th>
                      <th>Кол-во</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {createPackings.map((p, idx) => (
                      <tr key={idx}>
                        <td>{p.pack_type || "-"}</td>
                        <td>{p.qty_per_pack || "-"}</td>
                        <td className="actions-cell">
                          <button
                            className="btn danger compact action-small"
                            type="button"
                            onClick={() => setCreatePackings((prev) => prev.filter((_, i) => i !== idx))}
                          >
                            Удалить
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
          <div className="row" style={{ gap: 10, marginTop: 12, flexWrap: "wrap" }}>
            <button
              className="btn compact action-small"
              onClick={async () => {
                if (!form.gtin.trim() || !form.name.trim()) {
                  showToast("error", "GTIN и Наименование обязательны");
                  return;
                }
                const packQty =
                  form.packType === "unit"
                    ? 1
                    : form.packType === "box"
                    ? form.packSize || 1
                    : form.packSize || 1; // упаковок на паллету
                const payload = normalizeProductPayload({
                  sku: form.sku || form.gtin,
                  name: form.name,
                  barcode_ean: form.gtin,
                  unit: form.unit || "шт",
                  pack_qty: packQty,
                  brand: form.brand || null,
                  is_active: true,
                });
                try {
                  const created = await api.post("/products", payload);
                  const newId = created.data?.id;
                  if (newId && createPackings.length) {
                    for (const p of createPackings) {
                      if (!p.pack_type || !p.qty_per_pack) continue;
                      await api.post("/packing-profiles", {
                        product_id: newId,
                        pack_type: p.pack_type,
                        qty_per_pack: Number(p.qty_per_pack),
                        is_active: true,
                      });
                    }
                  }
                  showToast("success", "Товар создан");
                  setForm({
                    gtin: "",
                    sku: "",
                    name: "",
                    packType: "unit",
                    unit: "шт",
                    brand: "",
                    packSize: 1,
                    group: "",
                  });
                  setCreatePackings([]);
                  setShowCreate(false);
                  load(query);
                } catch (err: any) {
                  console.error("create product", err?.response?.data || err);
                  showToast("error", `Ошибка: ${err?.response?.status || ""} ${errDetail(err)}`);
                }
              }}
            >
              Добавить
            </button>
            <button className="btn secondary compact action-small" onClick={() => setShowCreate(false)}>
              Отмена
            </button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="desktop-stack">
      <div className="desktop-card">
        <div className="desktop-kicker">Товары</div>
        <h2 style={{ margin: "4px 0" }}>Каталог</h2>
        <div className="desktop-form-row" style={{ marginTop: 8 }}>
          <div className="desktop-form-grid">
            <label>Поиск</label>
            <input
              placeholder="Поиск по SKU/названию/штрихкоду"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
            />
          </div>
          <button className="btn secondary compact action-small" onClick={() => load(query)} disabled={loading}>
            Обновить
          </button>
        </div>
      </div>
      <div className="desktop-card">
        <div className="row" style={{ justifyContent: "space-between", alignItems: "center" }}>
          <div className="desktop-kicker">Карточка товара</div>
          <button className="btn compact action-small" onClick={() => setShowCreate(true)}>
            Создать карточку товара
          </button>
        </div>
      </div>
      {editId !== null && (
        <div className="desktop-card highlight">
          <div className="desktop-kicker">Редактирование товара</div>
          <div className="desktop-form-row" style={{ marginTop: 6, gap: 10, flexWrap: "wrap" }}>
            <input value={editForm.sku} readOnly style={{ minWidth: 140, background: "#f1f5f9" }} />
            <input
              placeholder="Наименование"
              value={editForm.name}
              onChange={(e) => setEditForm((f) => ({ ...f, name: e.target.value }))}
              style={{ minWidth: 220 }}
            />
            <input
              placeholder="Штрихкод"
              value={editForm.barcode}
              onChange={(e) => setEditForm((f) => ({ ...f, barcode: e.target.value }))}
              style={{ minWidth: 160 }}
            />
            <input
              placeholder="Ед. изм."
              value={editForm.unit}
              onChange={(e) => setEditForm((f) => ({ ...f, unit: e.target.value }))}
              style={{ width: 90 }}
            />
          <input
            type="number"
            min={1}
            placeholder="Упак."
            value={editForm.pack_qty}
            onChange={(e) => setEditForm((f) => ({ ...f, pack_qty: Number(e.target.value) || 1 }))}
            style={{ width: 90 }}
          />
            <input
              placeholder="Бренд"
              value={editForm.brand}
              onChange={(e) => setEditForm((f) => ({ ...f, brand: e.target.value }))}
              style={{ minWidth: 140 }}
            />
          </div>
          <div className="desktop-card" style={{ marginTop: 10 }}>
            <div className="desktop-kicker">Упаковки (SSCC)</div>
            <p className="desktop-subtext">
              Укажите тип упаковки (BOX15, TRAY6, BAG10KG и т.п.) и количество единиц в одной упаковке. Активные упаковки
              используются при наполнении паллет (pack_count × qty_per_pack).
            </p>
            <div className="desktop-form-row" style={{ gap: 8, flexWrap: "wrap" }}>
              <button
                className="btn secondary compact action-small"
                type="button"
                onClick={() =>
                  setEditPackings((prev) => [...prev, { id: null, pack_type: "BOX", qty_per_pack: "1", is_active: true }])
                }
              >
                Добавить упаковку
              </button>
              <button className="btn secondary compact action-small" type="button" onClick={() => editId && loadProductPackings(editId)}>
                Обновить упаковки
              </button>
            </div>
            <div className="desktop-table-wrap" style={{ marginTop: 8 }}>
              <table className="table desktop-table actions-right">
                <thead>
                  <tr>
                    <th>ID</th>
                    <th>Тип</th>
                    <th>Кол-во</th>
                    <th>Активен</th>
                    <th>Действия</th>
                  </tr>
                </thead>
                <tbody>
                  {editPackings.map((p, idx) => (
                    <tr key={p.id ?? `new-${idx}`}>
                      <td>{p.id ?? "-"}</td>
                      <td>
                        <div className="desktop-form-grid small">
                          <label>Тип</label>
                          <input
                            value={p.pack_type ?? ""}
                            placeholder="BOX15 / TRAY6 / BAG10KG"
                            onChange={(e) =>
                              setEditPackings((prev) =>
                                prev.map((row, i) => (i === idx ? { ...row, pack_type: e.target.value } : row))
                              )
                            }
                            style={{ minWidth: 140 }}
                          />
                        </div>
                      </td>
                      <td>
                        <div className="desktop-form-grid small">
                          <label>Кол-во в упаковке</label>
                          <input
                            type="number"
                            min={0}
                            step="0.001"
                            value={p.qty_per_pack ?? ""}
                            placeholder="Напр. 15"
                            onChange={(e) =>
                              setEditPackings((prev) =>
                                prev.map((row, i) => (i === idx ? { ...row, qty_per_pack: e.target.value } : row))
                              )
                            }
                            style={{ width: 140 }}
                          />
                        </div>
                      </td>
                      <td>
                        <div className="desktop-form-grid small" style={{ alignItems: "center" }}>
                          <label>Активен</label>
                          <input
                            type="checkbox"
                            checked={p.is_active ?? true}
                            onChange={(e) =>
                              setEditPackings((prev) =>
                                prev.map((row, i) => (i === idx ? { ...row, is_active: e.target.checked } : row))
                              )
                            }
                          />
                        </div>
                      </td>
                      <td className="actions-cell">
                        <button className="btn danger compact action-small" type="button" onClick={() => deleteEditPacking(p, idx)}>
                          Удалить
                        </button>
                      </td>
                    </tr>
                  ))}
                  {!editPackings.length && (
                    <tr>
                      <td colSpan={5} style={{ textAlign: "center", opacity: 0.7 }}>
                        Упаковок нет
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </div>
          <div className="row" style={{ gap: 10, flexWrap: "wrap", marginTop: 8 }}>
            <button
              className="btn compact action-small"
              onClick={async () => {
                if (!editForm.name.trim()) {
                  showToast("error", "Наименование обязательно");
                  return;
                }
                if (!editId) return;
                const payload = normalizeProductPayload({
                  sku: editForm.sku,
                  name: editForm.name,
                  barcode_ean: editForm.barcode,
                  unit: editForm.unit,
                  pack_qty: editForm.pack_qty,
                  brand: editForm.brand,
                  is_active: editForm.is_active,
                });
                // Admin: применяем сразу
                if (user?.role === "admin") {
                  try {
                    await api.put(`/products/${editId}`, payload);
                    await saveEditPackings(editId);
                    showToast("success", "Товар обновлён");
                    setEditId(null);
                    await load(query);
                  } catch (err: any) {
                    console.error("update product", err?.response?.data || err);
                    showToast("error", `Ошибка: ${err?.response?.status || ""} ${errDetail(err)}`);
                  }
                  return;
                }
                // Non-admin: отправляем в условную модерацию (локально)
                const request = {
                  id: Date.now(),
                  product_id: editId,
                  sku: editForm.sku,
                  payload: payload,
                  requested_by: user?.login || "user",
                  requested_at: new Date().toISOString(),
                };
                const next = [...pendingRequests, request];
                setPendingRequests(next);
                localStorage.setItem("product_edit_requests", JSON.stringify(next));
                showToast("success", "Изменение отправлено на модерацию");
                setEditId(null);
              }}
            >
              {user?.role === "admin" ? "Сохранить" : "Отправить на модерацию"}
            </button>
            <button
              className="btn secondary compact action-small"
              onClick={() => {
                setEditId(null);
                setEditPackings([]);
              }}
            >
              Отмена
            </button>
          </div>
        </div>
      )}
      {user?.role === "admin" && (
        <div className="desktop-card">
          <div className="desktop-kicker">
            Запросы на изменение {pendingRequests.length ? `(${pendingRequests.length})` : ""}
          </div>
          <div className="desktop-table-wrap">
            <table className="table desktop-table">
              <thead>
                <tr>
                  <th>SKU</th>
                  <th>Поля</th>
                  <th>Отправил</th>
                  <th>Когда</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {pendingRequests.map((r) => (
                  <tr key={r.id}>
                    <td>{r.sku}</td>
                    <td>
                      {Object.entries(r.payload || {})
                        .map(([k, v]) => `${k}: ${v ?? "-"}`)
                        .join("; ")}
                    </td>
                    <td>{r.requested_by}</td>
                    <td>{new Date(r.requested_at).toLocaleString()}</td>
                    <td className="actions-cell">
                      <button
                        className="btn compact action-small"
                        onClick={async () => {
                          if (!user) {
                            showToast("error", "Нужно войти как админ");
                            return;
                          }
                          const payload = normalizeProductPayload({
                            sku: r.payload?.sku || r.sku,
                            name: r.payload?.name || "",
                            barcode_ean: r.payload?.barcode_ean || "",
                            unit: r.payload?.unit || "pcs",
                            pack_qty: r.payload?.pack_qty ?? 1,
                            brand: r.payload?.brand,
                            is_active: r.payload?.is_active ?? true,
                          });
                          try {
                            await api.put(`/products/${r.product_id}`, payload);
                            const next = pendingRequests.filter((x) => x.id !== r.id);
                            setPendingRequests(next);
                            localStorage.setItem("product_edit_requests", JSON.stringify(next));
                            showToast("success", "Изменение применено");
                            await load(query);
                          } catch (err: any) {
                            console.error("apply pending product", err?.response?.data || err);
                            showToast("error", `Ошибка: ${err?.response?.status || ""} ${errDetail(err)}`);
                          }
                        }}
                      >
                        Применить
                      </button>
                      <button
                        className="btn secondary compact action-small"
                        onClick={() => {
                          const next = pendingRequests.filter((x) => x.id !== r.id);
                          setPendingRequests(next);
                          localStorage.setItem("product_edit_requests", JSON.stringify(next));
                          showToast("success", "Запрос отклонён");
                        }}
                      >
                        Отклонить
                      </button>
                    </td>
                  </tr>
                ))}
                {!pendingRequests.length && (
                  <tr>
                    <td colSpan={5} style={{ textAlign: "center", opacity: 0.7 }}>
                      Запросов нет
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}
      <div className="desktop-card">
        <div className="desktop-table-wrap">
          <table className="table desktop-table">
            <thead>
              <tr>
                <th>SKU</th>
                <th>Наименование</th>
                <th>Штрихкод</th>
                <th>Ед.</th>
                <th>Упак.</th>
                <th>Бренд</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {filtered.map((p: any) => (
                <tr key={p.id}>
                  <td>{p.sku}</td>
                  <td>{p.name}</td>
                  <td>{p.barcode_ean}</td>
                  <td>{p.unit}</td>
                  <td>{p.pack_qty ?? "-"}</td>
                  <td>{p.brand || "-"}</td>
                  <td className="actions-cell">
                    <button
                      className="btn secondary compact action-small"
                      onClick={() => {
                        setEditId(p.id);
                        setEditForm({
                          sku: p.sku || "",
                          name: p.name || "",
                          barcode: p.barcode_ean || "",
                          unit: p.unit || "pcs",
                          pack_qty: p.pack_qty ?? 1,
                          brand: p.brand || "",
                          is_active: p.is_active ?? true,
                        });
                        loadProductPackings(p.id);
                        window.scrollTo({ top: 0, behavior: "smooth" });
                      }}
                    >
                      Редактировать
                    </button>
                    {user?.role === "admin" && (
                      <button
                        className="btn danger compact action-small"
                        onClick={async () => {
                          const ok = window.confirm(`Удалить товар SKU=${p.sku}?`);
                          if (!ok) return;
                          try {
                            await api.delete(`/products/${p.id}`);
                            showToast("success", "Товар удалён");
                            await load(query);
                          } catch (err: any) {
                            console.error("delete product", err?.response?.data || err);
                            const status = err?.response?.status;
                            const detail = errDetail(err);
                            if (status === 400 || status === 409 || status === 500 || /constraint|integrity/i.test(detail)) {
                              showToast("error", "Нельзя удалить: товар использован в документах/остатках");
                            } else {
                              showToast("error", `Ошибка: ${status || ""} ${detail}`);
                            }
                          }
                        }}
                      >
                        Удалить
                      </button>
                    )}
                  </td>
                </tr>
              ))}
              {!filtered.length && (
                <tr>
                  <td colSpan={7} style={{ textAlign: "center", opacity: 0.7 }}>
                    Нет товаров
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

const DesktopAdmin = ({
  user,
  showToast,
  reloadLocations,
  locations,
  setActiveCompanyName,
  setActiveCompanyLogo,
  refreshActiveCompany,
}: {
  user: User | null;
  showToast: (t: "success" | "error", m: string) => void;
  reloadLocations: () => void;
  locations: LocationItem[];
  setActiveCompanyName: (name: string) => void;
  setActiveCompanyLogo: (logo: string) => void;
  refreshActiveCompany: () => Promise<void>;
}) => {
  const navigate = useNavigate();
  type AdminSection = "users" | "locations" | "contacts" | "db" | "company" | "audit";
  const [searchParams, setSearchParams] = useSearchParams();
  const getSection = (value: string | null): AdminSection => {
    switch (value) {
      case "locations":
        return "locations";
      case "db":
        return "db";
      case "company":
        return "company";
      case "contacts":
        return "contacts";
      case "audit":
        return "audit";
      case "users":
      default:
        return "users";
    }
  };
  const [section, setSection] = useState<AdminSection>(() => getSection(searchParams.get("section")));
  const [users, setUsers] = useState<User[]>([]);
  const [newLogin, setNewLogin] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [newRole, setNewRole] = useState<"admin" | "worker" | "viewer">("worker");
  const [filter, setFilter] = useState("");
  const [roleFilter, setRoleFilter] = useState<"all" | "admin" | "worker" | "viewer">("all");
  const [locationsList, setLocationsList] = useState<LocationItem[]>([]);
  const [locForm, setLocForm] = useState<{ id: number | null; warehouse: string; zone: string; cell_code: string }>({
    id: null,
    warehouse: "",
    zone: "",
    cell_code: "",
  });
  const [docRows, setDocRows] = useState<any[]>([]);
  const [docTypeFilter, setDocTypeFilter] = useState<string>("");
  const [docStatusFilter, setDocStatusFilter] = useState<string>("");
  const [dateFrom, setDateFrom] = useState<string>("");
  const [dateTo, setDateTo] = useState<string>("");
  const [lastDeletedUser, setLastDeletedUser] = useState<User | null>(null);
  const [lastDeletedLocation, setLastDeletedLocation] = useState<LocationItem | null>(null);
  const [lastCanceledDoc, setLastCanceledDoc] = useState<number | null>(null);
  const [usersMap, setUsersMap] = useState<Record<number, string>>({});
  const [pendingStatuses, setPendingStatuses] = useState<Record<number, string | undefined>>({});
  const [auditRows, setAuditRows] = useState<any[]>([]);
  const [contacts, setContacts] = useState<any[]>([]);
  const [contactForm, setContactForm] = useState({
    id: null as number | null,
    name: "",
    type: "both" as "supplier" | "customer" | "both",
    phone: "",
    email: "",
    note: "",
    is_active: true,
  });
  const [contactFilter, setContactFilter] = useState("");
  const contactTypes = [
    { value: "supplier", label: "Поставщик" },
    { value: "customer", label: "Клиент" },
    { value: "both", label: "Поставщик/Клиент" },
  ];
  const [companyForm, setCompanyForm] = useState({
    id: null as number | null,
    name: "",
    inn: "",
    kpp: "",
    address: "",
    phone: "",
    email: "",
    logo: "",
  });
  const [companyList, setCompanyList] = useState<any[]>([]);
  const statusOptions = [
    { value: "draft", label: "Черновик" },
    { value: "in_progress", label: "В процессе" },
    { value: "done", label: "Завершён" },
    { value: "canceled", label: "Удалён" },
  ];
  const rawSection = searchParams.get("section");
  const sectionFromUrl = getSection(rawSection);

  useEffect(() => {
    if (!rawSection || rawSection !== sectionFromUrl) {
      const params = new URLSearchParams(searchParams);
      params.set("section", sectionFromUrl);
      setSearchParams(params, { replace: true });
    }
    if (section !== sectionFromUrl) {
      setSection(sectionFromUrl);
    }
  }, [rawSection, sectionFromUrl, searchParams, section, setSearchParams]);

  useEffect(() => {
    if (!user || user.role !== "admin") {
      navigate("/desktop");
      return;
    }
    load();
    loadLocationsList();
    api
      .get("/users")
      .then((r) => {
        const map: Record<number, string> = {};
        r.data.forEach((u: User) => (map[u.id] = u.login));
        setUsersMap(map);
      })
      .catch(() => {});
    api
      .get("/audit")
      .then((r) => setAuditRows(Array.isArray(r.data) ? r.data : []))
      .catch(() => {});
    loadCompanyList();
    loadContacts();
  }, [user]);

  const load = async () => {
    const resp = await api.get("/users");
    setUsers(resp.data);
  };

  const loadLocationsList = async () => {
    try {
      const resp = await api.get("/locations");
      setLocationsList(resp.data);
      reloadLocations();
    } catch (err) {
      showToast("error", errDetail(err));
    }
  };

  useEffect(() => {
    setLocationsList(locations);
  }, [locations]);

  const createUser = async () => {
    if (!newLogin || !newPassword) return;
    await api.post("/users", { login: newLogin, password: newPassword, role: newRole, is_active: true });
    setNewLogin("");
    setNewPassword("");
    showToast("success", "Пользователь создан");
    load();
  };

  const toggleActive = async (u: User) => {
    await api.put(`/users/${u.id}`, { is_active: !u.is_active });
    showToast("success", "Статус обновлен");
    load();
  };

  const changePassword = async (u: User) => {
    const pwd = prompt(`Новый пароль для ${u.login}:`);
    if (!pwd) return;
    await api.post(`/users/${u.id}/password`, { password: pwd });
    showToast("success", "Пароль обновлен");
  };

  const loadContacts = async () => {
    try {
      const resp = await api.get("/contacts", { params: { include_inactive: true } });
      setContacts(Array.isArray(resp.data) ? resp.data : []);
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const saveContact = async () => {
    if (!contactForm.name.trim()) {
      showToast("error", "Имя обязательно");
      return;
    }
    try {
      const payload = { ...contactForm };
      let resp;
      if (payload.id) {
        resp = await api.put(`/contacts/${payload.id}`, payload);
      } else {
        resp = await api.post("/contacts", payload);
      }
      setContactForm({ ...contactForm, ...resp.data, id: resp.data.id ?? payload.id ?? null });
      showToast("success", payload.id ? "Контрагент обновлён" : "Контрагент создан");
      await loadContacts();
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const editContact = (c: any) => {
    setContactForm({
      id: c.id,
      name: c.name || "",
      type: c.type || "both",
      phone: c.phone || "",
      email: c.email || "",
      note: c.note || "",
      is_active: !!c.is_active,
    });
  };

  const newContact = () => {
    setContactForm({ id: null, name: "", type: "both", phone: "", email: "", note: "", is_active: true });
  };

  const toggleContactActive = async (c: any) => {
    try {
      await api.put(`/contacts/${c.id}`, { is_active: !c.is_active });
      showToast("success", "Статус обновлён");
      await loadContacts();
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const filteredContacts = contacts.filter((c) =>
    (c.name || "").toLowerCase().includes(contactFilter.toLowerCase().trim())
  );

  const deleteUser = async (u: User) => {
    if (!confirm(`Удалить пользователя ${u.login}? Он будет деактивирован.`)) return;
    if (!confirm("Точно удалить?")) return;
    try {
      await api.put(`/users/${u.id}`, { is_active: false });
      showToast("success", "Пользователь деактивирован");
      setLastDeletedUser(u);
      load();
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const undoDeleteUser = async () => {
    if (!lastDeletedUser) return;
    try {
      await api.put(`/users/${lastDeletedUser.id}`, { is_active: true });
      showToast("success", `Пользователь ${lastDeletedUser.login} восстановлен`);
      setLastDeletedUser(null);
      load();
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const saveLocation = async () => {
    if (!locForm.warehouse || !locForm.cell_code) {
      showToast("error", "Склад и код зоны обязательны");
      return;
    }
    try {
      if (locForm.id) {
        await api.put(`/locations/${locForm.id}`, {
          warehouse: locForm.warehouse,
          zone: locForm.zone || null,
          cell_code: locForm.cell_code,
        });
        showToast("success", "Склад обновлён");
      } else {
        await api.post("/locations", {
          warehouse: locForm.warehouse,
          zone: locForm.zone || null,
          cell_code: locForm.cell_code,
        });
        showToast("success", "Склад создан");
      }
      setLocForm({ id: null, warehouse: "", zone: "", cell_code: "" });
      loadLocationsList();
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const editLocation = (loc: LocationItem) => {
    setLocForm({ id: loc.id, warehouse: loc.warehouse, zone: loc.zone || "", cell_code: loc.cell_code });
  };

  const deleteLocation = async (loc: LocationItem) => {
    if (!confirm(`Удалить склад ${loc.cell_code}?`)) return;
    if (!confirm("Точно удалить?")) return;
    try {
      await api.delete(`/locations/${loc.id}`);
      showToast("success", "Удалено");
      if (locForm.id === loc.id) setLocForm({ id: null, warehouse: "", zone: "", cell_code: "" });
      setLastDeletedLocation(loc);
      loadLocationsList();
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const undoDeleteLocation = async () => {
    if (!lastDeletedLocation) return;
    try {
      await api.post("/locations", {
        warehouse: lastDeletedLocation.warehouse,
        zone: lastDeletedLocation.zone || null,
        cell_code: lastDeletedLocation.cell_code,
      });
      showToast("success", "Удалённый склад восстановлен");
      setLastDeletedLocation(null);
      loadLocationsList();
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const loadDocs = async () => {
    const params: any = {};
    if (docTypeFilter) params.type = docTypeFilter;
    if (docStatusFilter) params.status = docStatusFilter;
    const parseDate = (value: string, endOfDay = false) => {
      const d = new Date(value);
      if (Number.isNaN(d.getTime())) return null;
      if (endOfDay) d.setHours(23, 59, 59, 999);
      const pad = (n: number) => n.toString().padStart(2, "0");
      return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(
        d.getMinutes()
      )}:${pad(d.getSeconds())}`;
    };
    const fromISO = dateFrom ? parseDate(dateFrom, false) : null;
    const toISO = dateTo ? parseDate(dateTo, true) : null;
    if (fromISO) params.date_from = fromISO;
    if (toISO) params.date_to = toISO;
    try {
      const resp = await api.get("/docs", { params });
      setDocRows(Array.isArray(resp.data) ? resp.data : []);
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const cancelFromList = async (id: number) => {
    if (!confirm(`Удалить документ #${id}?`)) return;
    if (!confirm("Точно удалить документ?")) return;
    try {
      await api.post(`/docs/${id}/cancel`);
      showToast("success", "Документ удалён");
      setLastCanceledDoc(id);
      loadDocs();
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const cancelAllListed = async () => {
    if (!docRows.length) return;
    const pass = prompt(
      "Для подтверждения удаления всех найденных документов введите пароль (можно оставить пустым):",
      ""
    );
    if (pass === null) return;
    if (!confirm("Точно удалить все найденные документы?")) return;
    try {
      for (const d of docRows) {
        await api.post(`/docs/${d.id}/cancel`);
      }
      if (docRows.length === 1) setLastCanceledDoc(docRows[0].id);
      showToast("success", "Документы удалены");
      loadDocs();
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const undoCancelDoc = async () => {
    if (!lastCanceledDoc) return;
    try {
      await api.post(`/docs/${lastCanceledDoc}/start`);
      showToast("success", "Документ возвращён в работу");
      setLastCanceledDoc(null);
      loadDocs();
    } catch (err: any) {
      showToast("error", `Не удалось восстановить: ${errDetail(err)}`);
    }
  };

  const setDocStatus = async (id: number, status: string) => {
    try {
      await api.post(`/docs/${id}/status`, { status });
      showToast("success", "Статус обновлён");
      setPendingStatuses((s) => ({ ...s, [id]: undefined }));
      setDocRows((rows) => rows.map((r) => (r.id === id ? { ...r, status } : r)));
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

  const emptyCompany = {
    id: null as number | null,
    name: "",
    inn: "",
    kpp: "",
    address: "",
    phone: "",
    email: "",
    logo: "",
  };

  const pickCompany = (data: any) => ({
    id: data?.id ?? null,
    name: data?.name ?? "",
    inn: data?.inn ?? "",
    kpp: data?.kpp ?? "",
    address: data?.address ?? "",
    phone: data?.phone ?? "",
    email: data?.email ?? "",
    logo: data?.logo ?? "",
  });

  const pickCompanyForForm = (list: any[], preferredId?: number | null) => {
    if (!list.length) return null;
    if (typeof preferredId === "number") {
      const selected = list.find((i) => i.id === preferredId);
      if (selected) return selected;
    }
    return list.find((i) => i.is_active) || list[0];
  };

  const loadCompanyList = async (preferredId?: number | null) => {
    try {
      const resp = await api.get("/company/all");
      const list = Array.isArray(resp.data) ? [...resp.data].sort((a, b) => b.id - a.id) : [];
      setCompanyList(list);
      const selected = pickCompanyForForm(list, preferredId ?? companyForm.id);
      if (selected) {
        const picked = pickCompany(selected);
        setCompanyForm({ ...emptyCompany, ...picked, id: picked.id ?? null });
        setActiveCompanyName(picked.name || "");
        setActiveCompanyLogo(picked.logo || "");
      } else {
        setCompanyForm(emptyCompany);
        setActiveCompanyName(list[0]?.name || "");
        setActiveCompanyLogo(list[0]?.logo || "");
      }
    } catch (err: any) {
      // fallback to a single latest entry if list недоступен
      try {
        const single = await api.get("/company");
        const data = single.data || {};
        const picked = pickCompany(data);
        setCompanyForm({ ...emptyCompany, ...picked, id: picked.id ?? null });
        setCompanyList(data ? [{ ...picked, id: picked.id ?? 1, is_active: true }] : []);
        setActiveCompanyName(picked.name || "");
        setActiveCompanyLogo(picked.logo || "");
      } catch {
        showToast("error", errDetail(err));
      }
    }
  };

  const selectCompanyItem = (item: any) => {
    const picked = pickCompany(item);
    setCompanyForm({ ...emptyCompany, ...picked, id: picked.id ?? null });
    setActiveCompanyName(picked.name || "");
    setActiveCompanyLogo(picked.logo || "");
  };

  const saveCompany = async () => {
    try {
      const payload = { ...companyForm };
      const phoneDigits = payload.phone.replace(/\D/g, "");
      // если не ввели номер полностью — очищаем
      if (phoneDigits.length <= 1) {
        payload.phone = "";
      }
      // валидации
      if (!payload.name.trim() || !payload.inn.trim() || !payload.kpp.trim() || !payload.address.trim()) {
        showToast("error", "Наименование, ИНН, КПП и Адрес обязательны");
        return;
      }
      if (!/^\d+$/.test(payload.inn)) {
        showToast("error", "ИНН должен содержать только цифры");
        return;
      }
      if (!/^\d+$/.test(payload.kpp)) {
        showToast("error", "КПП должен содержать только цифры");
        return;
      }
      if (payload.phone && !/^\+7-\d{3}-\d{3}-\d{2}-\d{2}$/.test(payload.phone)) {
        showToast("error", "Телефон в формате +7-000-000-00-00");
        return;
      }
      if (payload.email && !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(payload.email)) {
        showToast("error", "Неверный формат email");
        return;
      }

      let resp;
      if (payload.id) {
        resp = await api.put(`/company/${payload.id}`, payload);
      } else {
        resp = await api.post("/company", payload);
      }
      const saved = pickCompany(resp.data);
      setCompanyForm({ ...emptyCompany, ...saved, id: saved.id ?? null });
      showToast("success", "Реквизиты сохранены");
      await loadCompanyList(saved.id ?? companyForm.id);
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const deleteCompanyEntry = async (id: number) => {
    if (!confirm("Удалить набор реквизитов?")) return;
    try {
      await api.delete(`/company/${id}`);
      showToast("success", "Набор удалён");
      await loadCompanyList();
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const activateCompany = async (id: number) => {
    try {
      const resp = await api.put(`/company/${id}/activate`);
      const picked = pickCompany(resp.data);
      setActiveCompanyName(picked.name || "");
      setActiveCompanyLogo(picked.logo || "");
      setCompanyForm({ ...emptyCompany, ...picked, id: resp.data.id ?? picked.id ?? null });
      await loadCompanyList(resp.data.id ?? picked.id ?? null);
      showToast("success", "Компания выбрана для документов");
    } catch (err: any) {
      showToast("error", errDetail(err));
    }
  };

  const companyValidation = React.useMemo(() => {
    const phoneRegex = /^\+7-\d{3}-\d{3}-\d{2}-\d{2}$/;
    const emailRegex = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;
    return {
      name: companyForm.name.trim().length > 0,
      inn: /^\d+$/.test(companyForm.inn) && companyForm.inn.trim().length > 0,
      kpp: /^\d+$/.test(companyForm.kpp) && companyForm.kpp.trim().length > 0,
      address: companyForm.address.trim().length > 0,
      phone: !companyForm.phone || companyForm.phone.length <= 4 || phoneRegex.test(companyForm.phone),
      email: !companyForm.email || emailRegex.test(companyForm.email),
    };
  }, [companyForm]);

  const fieldStyle = (ok: boolean) =>
    ok
      ? { flex: 1 }
      : {
          flex: 1,
          border: "1px solid #dc2626",
          boxShadow: "0 0 0 1px rgba(220,38,38,0.2)",
        };

  const handleLogoChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    const input = event.target;
    const file = input.files?.[0];
    if (!file) return;
    const allowedTypes = ["image/png", "image/jpeg"];
    if (!allowedTypes.includes(file.type)) {
      showToast("error", "Логотип должен быть PNG или JPG");
      input.value = "";
      return;
    }
    if (file.size > 1024 * 1024) {
      showToast("error", "Логотип больше 1 МБ");
      input.value = "";
      return;
    }
    const reader = new FileReader();
    reader.onload = () => {
      if (typeof reader.result === "string") {
        setCompanyForm((prev) => ({ ...prev, logo: reader.result as string }));
      }
      input.value = "";
    };
    reader.onerror = () => showToast("error", "Ошибка чтения логотипа");
    reader.readAsDataURL(file);
  };

  if (!user || user.role !== "admin") return null;

  const setSectionAndSync = (next: AdminSection) => {
    setSection(next);
    const params = new URLSearchParams(searchParams);
    params.set("section", next);
    setSearchParams(params);
  };

  const filtered = users.filter((u) => {
    const matchesLogin = !filter || u.login.toLowerCase().includes(filter.toLowerCase());
    const matchesRole = roleFilter === "all" || u.role === roleFilter;
    return matchesLogin && matchesRole;
  });

  return (
    <div className="desktop-stack">
      {section === "users" && (
        <>
          <div className="desktop-card">
            <div className="desktop-kicker">Создание пользователя</div>
            <div className="desktop-form-row">
              <input placeholder="Логин" value={newLogin} onChange={(e) => setNewLogin(e.target.value)} />
              <input placeholder="Пароль" value={newPassword} onChange={(e) => setNewPassword(e.target.value)} />
              <select value={newRole} onChange={(e) => setNewRole(e.target.value as any)}>
                <option value="admin">admin</option>
                <option value="worker">worker</option>
                <option value="viewer">viewer</option>
              </select>
              <button className="btn compact" onClick={createUser}>
                Создать
              </button>
            </div>
          </div>
          <div className="desktop-card">
            <div className="desktop-split">
              <div>
                <div className="desktop-kicker">Пользователи</div>
                <h3>Управление доступами</h3>
              </div>
              <div className="row" style={{ gap: 8, flexWrap: "wrap" }}>
                <input
                  placeholder="Поиск по логину"
                  value={filter}
                  onChange={(e) => setFilter(e.target.value)}
                  style={{ minWidth: 180 }}
                />
                <select value={roleFilter} onChange={(e) => setRoleFilter(e.target.value as any)}>
                  <option value="all">все роли</option>
                  <option value="admin">admin</option>
                  <option value="worker">worker</option>
                  <option value="viewer">viewer</option>
                </select>
                <button className="btn secondary compact" onClick={load}>
                  Обновить список
                </button>
                {lastDeletedUser && (
                  <button className="btn secondary compact" onClick={undoDeleteUser}>
                    Восстановить пользователя {lastDeletedUser.login}
                  </button>
                )}
              </div>
            </div>
            <div className="desktop-table-wrap">
          <table className="table desktop-table actions-right">
            <thead>
              <tr>
                <th>Логин</th>
                <th>Роль</th>
                <th>Статус</th>
                <th>Действия</th>
                  </tr>
                </thead>
                <tbody>
                  {filtered.map((u) => (
                    <tr key={u.id}>
                      <td>{u.login}</td>
                      <td>{u.role}</td>
                      <td>{u.is_active ? "активен" : "заблокирован"}</td>
                      <td className="row" style={{ gap: 6, flexWrap: "wrap" }}>
                        <button className="btn secondary compact" onClick={() => toggleActive(u)}>
                          {u.is_active ? "Блокировать" : "Активировать"}
                        </button>
                        <button className="btn secondary compact" onClick={() => changePassword(u)}>
                          Сменить пароль
                        </button>
                        <button className="btn danger compact" onClick={() => deleteUser(u)}>
                          Удалить
                        </button>
                      </td>
                    </tr>
                  ))}
                  {!filtered.length && (
                    <tr>
                      <td colSpan={4} style={{ textAlign: "center", opacity: 0.7 }}>
                        Пользователи не найдены
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          </div>
        </>
      )}

      {section === "locations" && (
        <div className="desktop-card">
          <div className="desktop-split">
            <div>
              <div className="desktop-kicker">Склады и зоны</div>
              <h3>Управление зонами хранения</h3>
            </div>
            <div className="row" style={{ gap: 6, flexWrap: "wrap" }}>
              <input
                placeholder="Склад (warehouse)"
                value={locForm.warehouse}
                onChange={(e) => setLocForm((f) => ({ ...f, warehouse: e.target.value }))}
              />
              <input
                placeholder="Зона (опционально)"
                value={locForm.zone}
                onChange={(e) => setLocForm((f) => ({ ...f, zone: e.target.value }))}
              />
              <input
                placeholder="Код зоны (cell_code)"
                value={locForm.cell_code}
                onChange={(e) => setLocForm((f) => ({ ...f, cell_code: e.target.value }))}
              />
              <button className="btn compact" onClick={saveLocation}>
                {locForm.id ? "Сохранить изменения" : "Добавить склад"}
              </button>
              <button
                className="btn secondary compact"
                onClick={() => setLocForm({ id: null, warehouse: "", zone: "", cell_code: "" })}
              >
                Очистить
              </button>
              {lastDeletedLocation && (
                <button className="btn secondary compact" onClick={undoDeleteLocation}>
                  Восстановить склад {lastDeletedLocation.cell_code}
                </button>
              )}
            </div>
          </div>
          <div className="desktop-table-wrap">
          <table className="table desktop-table actions-right">
            <thead>
              <tr>
                <th>ID</th>
                <th>Склад</th>
                <th>Зона</th>
                  <th>Код</th>
                  <th>Действия</th>
                </tr>
              </thead>
              <tbody>
                {locationsList.map((loc) => (
                  <tr key={loc.id}>
                    <td>{loc.id}</td>
                    <td>{loc.warehouse}</td>
                    <td>{loc.zone || "—"}</td>
                    <td>{loc.cell_code}</td>
                    <td className="row" style={{ gap: 6, flexWrap: "wrap" }}>
                      <button className="btn secondary compact" onClick={() => editLocation(loc)}>
                        Редактировать
                      </button>
                      <button className="btn danger compact" onClick={() => deleteLocation(loc)}>
                        Удалить
                      </button>
                    </td>
                  </tr>
                ))}
                {!locationsList.length && (
                  <tr>
                    <td colSpan={5} style={{ textAlign: "center", opacity: 0.7 }}>
                      Складов нет
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {section === "contacts" && (
        <div className="desktop-card">
          <div className="desktop-kicker">Контрагенты</div>
          <div className="desktop-form-grid" style={{ marginBottom: 12 }}>
            <input
              placeholder="Название"
              value={contactForm.name}
              onChange={(e) => setContactForm({ ...contactForm, name: e.target.value })}
            />
            <select
              value={contactForm.type}
              onChange={(e) => setContactForm({ ...contactForm, type: e.target.value as any })}
            >
              {contactTypes.map((t) => (
                <option key={t.value} value={t.value}>
                  {t.label}
                </option>
              ))}
            </select>
            <input
              placeholder="Телефон (опционально)"
              value={contactForm.phone}
              onChange={(e) => setContactForm({ ...contactForm, phone: e.target.value })}
            />
            <input
              placeholder="Email (опционально)"
              value={contactForm.email}
              onChange={(e) => setContactForm({ ...contactForm, email: e.target.value })}
            />
            <textarea
              placeholder="Заметка"
              value={contactForm.note}
              onChange={(e) => setContactForm({ ...contactForm, note: e.target.value })}
              rows={2}
            />
            <label className="row" style={{ gap: 8 }}>
              <input
                type="checkbox"
                checked={contactForm.is_active}
                onChange={(e) => setContactForm({ ...contactForm, is_active: e.target.checked })}
              />
              Активен
            </label>
            <div className="row" style={{ gap: 8, justifyContent: "flex-end" }}>
              <button className="btn compact" onClick={saveContact}>
                {contactForm.id ? "Сохранить" : "Создать"}
              </button>
              <button className="btn secondary compact" onClick={newContact}>
                Новый
              </button>
            </div>
          </div>

          <div className="row" style={{ marginBottom: 8, gap: 8, flexWrap: "wrap" }}>
            <input
              placeholder="Фильтр по названию"
              value={contactFilter}
              onChange={(e) => setContactFilter(e.target.value)}
            />
            <button className="btn secondary compact" onClick={loadContacts}>
              Обновить
            </button>
          </div>

          <div className="desktop-table-wrap">
            <table className="table desktop-table actions-right">
              <thead>
                <tr>
                  <th>ID</th>
                  <th>Название</th>
                  <th>Тип</th>
                  <th>Телефон</th>
                  <th>Email</th>
                  <th>Активен</th>
                  <th>Действия</th>
                </tr>
              </thead>
              <tbody>
                {filteredContacts.map((c) => (
                  <tr key={c.id}>
                    <td>{c.id}</td>
                    <td>{c.name}</td>
                    <td>{contactTypes.find((t) => t.value === c.type)?.label || c.type}</td>
                    <td>{c.phone || "-"}</td>
                    <td>{c.email || "-"}</td>
                    <td>{c.is_active ? "Да" : "Нет"}</td>
                    <td className="row" style={{ gap: 6, flexWrap: "wrap" }}>
                      <button className="btn secondary compact" onClick={() => editContact(c)}>
                        Редактировать
                      </button>
                      <button className="btn compact" onClick={() => toggleContactActive(c)}>
                        {c.is_active ? "Отключить" : "Включить"}
                      </button>
                    </td>
                  </tr>
                ))}
                {!filteredContacts.length && (
                  <tr>
                    <td colSpan={7} style={{ textAlign: "center", opacity: 0.7 }}>
                      Контрагенты не найдены
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {section === "db" && (
        <div className="desktop-card">
          <div className="desktop-kicker">Работа с БД</div>
          <h3>Документы (поиск и удаление)</h3>
          <div className="desktop-form-row">
            <select value={docTypeFilter} onChange={(e) => setDocTypeFilter(e.target.value)}>
              <option value="">Все типы</option>
              <option value="inbound">Приёмка</option>
              <option value="outbound">Отгрузка</option>
            </select>
            <select value={docStatusFilter} onChange={(e) => setDocStatusFilter(e.target.value)}>
              <option value="">Все статусы</option>
              <option value="draft">Черновик</option>
              <option value="in_progress">В процессе</option>
              <option value="done">Завершён</option>
              <option value="canceled">Удалён</option>
            </select>
            <input type="date" value={dateFrom} onChange={(e) => setDateFrom(e.target.value)} />
            <input type="date" value={dateTo} onChange={(e) => setDateTo(e.target.value)} />
            <button className="btn compact" onClick={loadDocs}>
              Показать
            </button>
            <button className="btn danger compact" onClick={cancelAllListed} disabled={!docRows.length}>
              Удалить все найденные
            </button>
            {lastCanceledDoc && (
              <button className="btn secondary compact" onClick={undoCancelDoc}>
                Попробовать вернуть #{lastCanceledDoc}
              </button>
            )}
          </div>
          <div className="desktop-table-wrap" style={{ marginTop: 10 }}>
          <table className="table desktop-table actions-right">
            <thead>
              <tr>
                <th>№</th>
                <th>Тип</th>
                <th>Дата</th>
                <th>Статус</th>
                <th>Действия</th>
                </tr>
              </thead>
              <tbody>
                {docRows.map((d) => (
                  <tr key={d.id}>
                    <td>{formatDocNumber(d)}</td>
                    <td>{docTypeLabel(d.type)}</td>
                    <td>{d.created_at ? new Date(d.created_at).toLocaleString("ru-RU") : "—"}</td>
                    <td>
                      <div className="row" style={{ gap: 6, alignItems: "center", flexWrap: "wrap" }}>
                        <select
                          value={pendingStatuses[d.id] ?? d.status}
                          onChange={(e) =>
                            setPendingStatuses((prev) => ({
                              ...prev,
                              [d.id]: e.target.value,
                            }))
                          }
                          style={{ minWidth: 150 }}
                        >
                          {statusOptions.map((s) => (
                            <option key={s.value} value={s.value}>
                              {s.label}
                            </option>
                          ))}
                        </select>
                        {pendingStatuses[d.id] && pendingStatuses[d.id] !== d.status && (
                          <div className="row" style={{ gap: 6 }}>
                            <button
                              className="btn compact"
                              style={{ background: "#1f9d55" }}
                              onClick={() => setDocStatus(d.id, pendingStatuses[d.id] as string)}
                            >
                              Сохранить
                            </button>
                            <button
                              className="btn danger compact"
                              onClick={() => setPendingStatuses((prev) => ({ ...prev, [d.id]: undefined }))}
                            >
                              Сбросить
                            </button>
                          </div>
                        )}
                      </div>
                    </td>
                    <td className="actions-cell">
                      <div className="row" style={{ gap: 6, flexWrap: "wrap", justifyContent: "flex-end" }}>
                        <button className="btn secondary compact" onClick={() => navigate(`/desktop/docs/${d.type}/${d.id}`)}>
                          Открыть
                        </button>
                        <button className="btn secondary compact" onClick={() => downloadPdf(d.id)}>
                          PDF
                        </button>
                        <button className="btn danger compact" onClick={() => cancelFromList(d.id)}>
                          Удалить
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
                {!docRows.length && (
                  <tr>
                    <td colSpan={5} style={{ textAlign: "center", opacity: 0.7 }}>
                      Документы не найдены
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
          <div className="desktop-card" style={{ marginTop: 12 }}>
            <div className="desktop-kicker">Жёсткое обнуление</div>
            <p className="desktop-subtext">Удалить всю БД (тестовый режим). Требуется подтверждение.</p>
            <button
              className="btn danger compact"
              onClick={() => {
                const pwd = prompt("ВНИМАНИЕ: обнуление БД. Введите пароль (можно оставить пустым для теста):", "");
                if (pwd === null) return;
                showToast("error", "Полное обнуление требует отдельного backend-эндоинта."); // placeholder
              }}
            >
              Удалить всю БД
            </button>
          </div>
        </div>
      )}

      {section === "company" && (
        <div className="desktop-card">
          <div className="desktop-kicker">Компания</div>
          <h3>Реквизиты и шапка документов</h3>
          <div style={{ display: "flex", gap: 16, alignItems: "flex-start", flexWrap: "wrap" }}>
            <div
              className="desktop-form-grid"
              style={{ display: "flex", flexDirection: "column", gap: 8, maxWidth: 620, flex: 1, minWidth: 340 }}
            >
              <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
                <label style={{ width: 180, margin: 0 }}>Наименование</label>
                <input
                  style={fieldStyle(companyValidation.name)}
                  value={companyForm.name}
                  onChange={(e) => setCompanyForm({ ...companyForm, name: e.target.value })}
                  placeholder="Обязательное поле"
                />
              </div>
              <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
                <label style={{ width: 180, margin: 0 }}>Логотип</label>
                <input type="file" accept="image/png,image/jpeg" onChange={handleLogoChange} />
                <div style={{ fontSize: 12, opacity: 0.7 }}>PNG/JPG, до 1 МБ</div>
                {companyForm.logo && (
                  <button
                    className="btn secondary compact"
                    type="button"
                    onClick={() => setCompanyForm((prev) => ({ ...prev, logo: "" }))}
                  >
                    Удалить
                  </button>
                )}
              </div>
              {companyForm.logo && (
                <div style={{ marginLeft: 190, display: "flex", alignItems: "center", gap: 10 }}>
                  <img
                    src={companyForm.logo}
                    alt="Логотип"
                    style={{
                      width: 64,
                      height: 64,
                      objectFit: "contain",
                      borderRadius: 10,
                      border: "1px solid #e2e8f0",
                      background: "#fff",
                    }}
                  />
                </div>
              )}
              <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
                <label style={{ width: 180, margin: 0 }}>ИНН</label>
                <input
                  style={fieldStyle(companyValidation.inn)}
                  value={companyForm.inn}
                  onChange={(e) => setCompanyForm({ ...companyForm, inn: e.target.value.replace(/\D/g, "").slice(0, 12) })}
                  placeholder="Только цифры"
                />
              </div>
              <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
                <label style={{ width: 180, margin: 0 }}>КПП</label>
                <input
                  style={fieldStyle(companyValidation.kpp)}
                  value={companyForm.kpp}
                  onChange={(e) => setCompanyForm({ ...companyForm, kpp: e.target.value.replace(/\D/g, "").slice(0, 9) })}
                  placeholder="Только цифры"
                />
              </div>
              <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
                <label style={{ width: 180, margin: 0 }}>Адрес</label>
                <textarea
                  style={{ ...fieldStyle(companyValidation.address), minHeight: 90, resize: "vertical" }}
                  rows={4}
                  value={companyForm.address}
                  onChange={(e) => setCompanyForm({ ...companyForm, address: e.target.value })}
                />
              </div>
              <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
                <label style={{ width: 180, margin: 0 }}>Телефон</label>
                <input
                  style={fieldStyle(companyValidation.phone)}
                  value={companyForm.phone}
                  onChange={(e) => {
                    const raw = e.target.value.replace(/\D/g, "");
                    const digits = (raw.startsWith("7") ? raw : `7${raw.replace(/^7/, "")}`).slice(0, 11);
                    const a = digits.slice(1, 4);
                    const b = digits.slice(4, 7);
                    const c = digits.slice(7, 9);
                    const d = digits.slice(9, 11);
                    let formatted = "+7";
                    if (a) formatted += `-${a}`;
                    if (b) formatted += `-${b}`;
                    if (c) formatted += `-${c}`;
                    if (d) formatted += `-${d}`;
                    setCompanyForm((prev) => ({ ...prev, phone: formatted }));
                  }}
                  placeholder="+7-000-000-00-00"
                />
              </div>
              <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
                <label style={{ width: 180, margin: 0 }}>Email</label>
                <input
                  style={fieldStyle(companyValidation.email)}
                  value={companyForm.email}
                  onChange={(e) => setCompanyForm({ ...companyForm, email: e.target.value })}
                  placeholder="example@domain.ru"
                />
              </div>
              <div className="row" style={{ justifyContent: "flex-end", marginTop: 10, gap: 10 }}>
                <button className="btn compact" style={{ background: "#1f9d55" }} onClick={saveCompany}>
                  {companyForm.id ? "Обновить" : "Сохранить"} реквизиты
                </button>
                <button className="btn secondary compact" onClick={() => setCompanyForm(emptyCompany)}>
                  Новый набор
                </button>
              </div>
            </div>

            <div style={{ flex: 1, minWidth: 280, display: "flex", flexDirection: "column", gap: 10 }}>
              <div className="desktop-kicker" style={{ marginBottom: 4 }}>
                Сохранённые наборы
              </div>
              {companyList.length === 0 && <div style={{ opacity: 0.6 }}>Ничего не сохранено.</div>}
              {companyList.map((item) => (
                <div
                  key={item.id}
                  onClick={() => selectCompanyItem(item)}
                  style={{
                    position: "relative",
                    border: item.is_active ? "2px solid #1f9d55" : "1px solid #d9e2ec",
                    borderRadius: 8,
                    padding: 10,
                    display: "flex",
                    flexDirection: "column",
                    gap: 6,
                    background: item.is_active ? "#f0f4ff" : "#f8fafc",
                    cursor: "pointer",
                  }}
                >
                  {item.is_active && (
                    <div
                      style={{
                        position: "absolute",
                        top: 6,
                        right: 6,
                        width: 14,
                        height: 14,
                        borderRadius: "50%",
                        background: "#1f9d55",
                        border: "2px solid #e6ffed",
                      }}
                      title="Текущий набор"
                    />
                  )}
                  <div style={{ fontWeight: 600 }}>{item.name || "Без названия"}</div>
                  <div style={{ fontSize: 13, opacity: 0.8 }}>
                    ИНН: {item.inn || "—"} · КПП: {item.kpp || "—"}
                  </div>
                  {item.address && (
                    <div style={{ fontSize: 13, opacity: 0.8, lineHeight: 1.4 }}>{item.address}</div>
                  )}
                  <div className="row" style={{ gap: 8, justifyContent: "flex-end" }}>
                    <button
                      className="btn compact secondary"
                      onClick={() => selectCompanyItem(item)}
                    >
                      Редактировать
                    </button>
                    {!item.is_active && (
                      <button className="btn compact" style={{ background: "#1f9d55" }} onClick={() => activateCompany(item.id)}>
                        Сделать текущей
                      </button>
                    )}
                    <button className="btn danger compact" onClick={() => deleteCompanyEntry(item.id)}>
                      Удалить
                    </button>
                  </div>
                </div>
              ))}
            </div>
          </div>
        </div>
      )}

      {section === "audit" && (
        <div className="desktop-card">
          <div className="desktop-kicker">Журнал</div>
          <table className="table desktop-table">
            <thead>
              <tr>
                <th>Действие</th>
                <th>Пользователь</th>
                <th>Сущность</th>
                <th>Время</th>
              </tr>
            </thead>
            <tbody>
              {auditRows
                .sort((a, b) => new Date(b.created_at).getTime() - new Date(a.created_at).getTime())
                .map((r) => (
                  <tr key={r.id}>
                    <td>{r.action}</td>
                    <td>{usersMap[r.user_id] || r.user_id || "—"}</td>
                    <td>
                      {r.entity_type} #{r.entity_id}
                    </td>
                    <td>{new Date(r.created_at).toLocaleString("ru-RU")}</td>
                  </tr>
                ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
};

export default App;


