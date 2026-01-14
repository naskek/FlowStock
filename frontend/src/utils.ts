export const PRODUCTION_ZONE_CODE = "PROD-01";

export const errDetail = (err: any) => err?.response?.data?.detail || err?.message || "Ошибка";

type DocUiMeta = {
  label: string;
  listTitle: string;
  newLabel: string;
  finishLabel: string;
  hint?: string;
};

const DOC_UI: Record<string, DocUiMeta> = {
  inbound: {
    label: "Приёмка",
    listTitle: "Документы приёмки",
    newLabel: "Новая приёмка",
    finishLabel: "Провести приёмку",
  },
  outbound: {
    label: "Отгрузка",
    listTitle: "Документы отгрузки",
    newLabel: "Новая отгрузка",
    finishLabel: "Провести отгрузку",
  },
  production_issue: {
    label: "Сырьё → производство",
    listTitle: "Документы: Сырьё → производство",
    newLabel: "Новое списание: Сырьё → производство",
    finishLabel: "Провести: Сырьё → производство",
    hint: "Сырьё → производство — списание сырья и материалов в производство",
  },
  production_receipt: {
    label: "Производство → склад",
    listTitle: "Документы: Производство → склад",
    newLabel: "Новый выпуск: Производство → склад",
    finishLabel: "Провести: Производство → склад",
    hint: "Производство → склад — постановка готовой продукции на склад",
  },
  inventory: {
    label: "Инвентаризация",
    listTitle: "Документы инвентаризации",
    newLabel: "Новая инвентаризация",
    finishLabel: "Провести инвентаризацию",
  },
  move: {
    label: "Перемещение",
    listTitle: "Документы перемещений",
    newLabel: "Новое перемещение",
    finishLabel: "Провести перемещение",
  },
};

const fallbackUi: DocUiMeta = {
  label: "Документ",
  listTitle: "Документы",
  newLabel: "Новый документ",
  finishLabel: "Провести документ",
};

const uiMeta = (t: string): DocUiMeta => DOC_UI[t] || fallbackUi;

export const docTypeLabel = (t: string) => uiMeta(t).label;
export const docListTitle = (t: string) => uiMeta(t).listTitle;
export const docNewLabel = (t: string) => uiMeta(t).newLabel;
export const docFinishLabel = (t: string) => uiMeta(t).finishLabel;
export const docHint = (t: string) => uiMeta(t).hint || "";

export const statusLabel = (s: string) =>
  s === "draft"
    ? "Черновик"
    : s === "in_progress"
    ? "В процессе"
    : s === "done"
    ? "Завершён"
    : s === "canceled"
    ? "Удалён"
    : s;

export const statusClass = (s: string) =>
  s === "draft"
    ? "status-draft"
    : s === "in_progress"
    ? "status-progress"
    : s === "done"
    ? "status-done"
    : s === "canceled"
    ? "status-canceled"
    : "";

const docPrefix = (t?: string) =>
  t === "inbound"
    ? "ПР"
    : t === "outbound"
    ? "ОТ"
    : t === "production_issue"
    ? "СПР"
    : t === "production_receipt"
    ? "ПСК"
    : t === "inventory"
    ? "ИНВ"
    : t?.toUpperCase?.() || "DOC";

export const formatDocNumber = (doc: { id?: number | string; type?: string; created_at?: string }) => {
  const prefix = docPrefix(doc?.type);
  const date = doc?.created_at ? new Date(doc.created_at) : new Date();
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const num = String(doc?.id ?? "").padStart(6, "0");
  return `${prefix}-${year}${month}-${num}`;
};
