import React from "react";

export type Toast = { type: "success" | "error"; text: string } | null;

export const ToastView = ({ toast, clear }: { toast: Toast; clear: () => void }) =>
  toast ? (
    <div className={`toast ${toast.type}`} onClick={clear}>
      {toast.text}
    </div>
  ) : null;
