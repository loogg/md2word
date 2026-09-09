import { AlertTriangle, CheckCircle2, CircleDashed, XCircle } from "lucide-react";
import type { EnvironmentItemStatus, ValidationStatus } from "../types";

type BadgeState = ValidationStatus | EnvironmentItemStatus;

const styles: Record<BadgeState, string> = {
  valid: "border-emerald-200 bg-emerald-50 text-emerald-700",
  ready: "border-emerald-200 bg-emerald-50 text-emerald-700",
  warning: "border-amber-200 bg-amber-50 text-amber-700",
  invalid: "border-red-200 bg-red-50 text-red-700",
  blocked: "border-red-200 bg-red-50 text-red-700",
  error: "border-red-200 bg-red-50 text-red-700",
  "optional-missing": "border-amber-200 bg-amber-50 text-amber-700",
};

const labels: Record<BadgeState, string> = {
  valid: "校验通过",
  ready: "已就绪",
  warning: "需要注意",
  invalid: "校验失败",
  blocked: "已阻止",
  error: "检查失败",
  "optional-missing": "可选项缺失",
};

function BadgeIcon({ state }: { state: BadgeState }) {
  if (state === "valid" || state === "ready") return <CheckCircle2 className="h-3.5 w-3.5" />;
  if (state === "invalid" || state === "blocked" || state === "error") return <XCircle className="h-3.5 w-3.5" />;
  if (state === "warning" || state === "optional-missing") return <AlertTriangle className="h-3.5 w-3.5" />;
  return <CircleDashed className="h-3.5 w-3.5" />;
}

export function StatusBadge({ state, label }: { state: BadgeState; label?: string }) {
  return (
    <span
      className={`inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[12px] font-semibold ${styles[state]}`}
    >
      <BadgeIcon state={state} />
      {label ?? labels[state]}
    </span>
  );
}
