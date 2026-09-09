import {
  AlertTriangle,
  AppWindow,
  ChevronRight,
  Database,
  FileOutput,
  FolderOpen,
  HardDrive,
  MonitorCog,
  RefreshCw,
  RotateCcw,
  Settings2,
  ShieldCheck,
  TerminalSquare,
  Workflow,
} from "lucide-react";
import { useState } from "react";
import { Modal } from "../components/Modal";
import { StatusBadge } from "../components/StatusBadge";
import { errorMessage } from "../lib/errorMessage";
import type { AppAdapter, EnvironmentStatus, TemplateProfile } from "../types";

interface EnvironmentPageProps {
  environment: EnvironmentStatus;
  adapter: AppAdapter;
  onEnvironmentChange: (status: EnvironmentStatus) => void;
  onReset: (state: { templates: TemplateProfile[]; environment: EnvironmentStatus }) => void;
}

const itemIcons = {
  windows: AppWindow,
  word: FileOutput,
  pandoc: TerminalSquare,
  worker: Workflow,
  mermaid: MonitorCog,
};

export function EnvironmentPage({ environment, adapter, onEnvironmentChange, onReset }: EnvironmentPageProps) {
  const [refreshing, setRefreshing] = useState(false);
  const [resetOpen, setResetOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refresh = async () => {
    setRefreshing(true);
    setError(null);
    try {
      onEnvironmentChange(await adapter.environment.check());
    } catch (reason) {
      setError(errorMessage(reason, "环境检查失败，请重试。"));
    } finally {
      setRefreshing(false);
    }
  };

  const word = environment.items.find((item) => item.id === "word");
  const toggleWord = async () => {
    if (!adapter.demo) return;
    setError(null);
    try {
      onEnvironmentChange(await adapter.demo.setWordAvailable(word?.status === "blocked"));
    } catch (reason) {
      setError(errorMessage(reason, "演示状态切换失败。"));
    }
  };

  const reset = async () => {
    if (!adapter.demo) return;
    try {
      onReset(await adapter.demo.reset());
      setResetOpen(false);
    } catch (reason) {
      setError(errorMessage(reason, "重置演示数据失败。"));
    }
  };

  return (
    <div className="page-enter space-y-6">
      <section className="flex items-end justify-between gap-8">
        <div><div className="mb-2 flex items-center gap-2 text-xs font-bold uppercase tracking-[0.16em] text-blue-600"><Settings2 className="h-4 w-4" /> 系统诊断</div><h1 className="text-[28px] font-bold tracking-tight text-slate-950">在启动 Word 前先把环境说清楚</h1><p className="mt-2 text-sm leading-6 text-slate-500">{adapter.runtimeCapabilities.environment === "worker" ? "检查由独立 Worker 执行，不阻塞 Renderer。" : "当前结果为浏览器演示，不代表本机真实环境。"}</p></div>
        <button type="button" onClick={() => void refresh()} disabled={refreshing} className="inline-flex items-center gap-2 rounded-lg border border-slate-300 bg-white px-4 py-2.5 text-sm font-bold text-slate-700"><RefreshCw className={`h-4 w-4 ${refreshing ? "animate-spin" : ""}`} /> 重新检查</button>
      </section>

      {error ? <div role="alert" className="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div> : null}

      <section className="grid grid-cols-5 gap-3">
        {environment.items.map((item) => {
          const Icon = itemIcons[item.id];
          return <article key={item.id} className={`panel min-w-0 p-4 ${item.status === "blocked" ? "border-red-200" : ""}`}><div className="flex items-start justify-between gap-2"><div className={`rounded-xl p-2.5 ${item.status === "ready" ? "bg-emerald-50 text-emerald-600" : item.status === "blocked" || item.status === "error" ? "bg-red-50 text-red-600" : "bg-amber-50 text-amber-600"}`}><Icon className="h-5 w-5" /></div><span className="rounded bg-slate-100 px-1.5 py-0.5 text-[9px] font-bold text-slate-500">{item.required ? "必需" : "可选"}</span></div><h2 className="mt-3 truncate text-sm font-bold text-slate-900">{item.name}</h2><p className="mt-1 truncate text-[10px] font-medium text-slate-500">{item.version}</p><div className="mt-3"><StatusBadge state={item.status} label={adapter.runtimeCapabilities.environment === "mock" && item.status === "ready" ? "模拟就绪" : undefined} /></div></article>;
        })}
      </section>

      <div className="grid grid-cols-[minmax(0,1.4fr)_minmax(340px,0.8fr)] gap-6">
        <section className="panel overflow-hidden"><div className="flex items-center justify-between border-b border-slate-100 px-5 py-4"><div><h2 className="text-sm font-bold text-slate-900">环境检查详情</h2><p className="mt-1 text-[11px] text-slate-500">上次检查：{new Date(environment.checkedAt).toLocaleString("zh-CN", { hour12: false })}</p></div>{adapter.demo ? <button type="button" onClick={() => void toggleWord()} className={`rounded-lg px-3 py-2 text-xs font-bold ${word?.status === "blocked" ? "bg-emerald-50 text-emerald-700" : "bg-amber-50 text-amber-700"}`}>{word?.status === "blocked" ? "恢复 Word 就绪" : "模拟 Word 缺失"}</button> : null}</div><div className="divide-y divide-slate-100">{environment.items.map((item) => { const Icon = itemIcons[item.id]; return <div key={item.id} className="flex items-center gap-4 px-5 py-4"><div className="rounded-lg bg-slate-100 p-2 text-slate-500"><Icon className="h-4 w-4" /></div><div className="min-w-0 flex-1"><div className="flex items-center gap-2"><p className="text-xs font-bold text-slate-800">{item.name}</p><span className="text-[10px] text-slate-400">{item.version}</span></div><p className="mt-1 text-[11px] leading-5 text-slate-500">{item.detail}</p></div><StatusBadge state={item.status} label={adapter.runtimeCapabilities.environment === "mock" && item.status === "ready" ? "模拟就绪" : undefined} /></div>; })}</div></section>

        <aside className="space-y-5"><section className="panel p-5"><div className="flex items-center gap-2"><Workflow className="h-4 w-4 text-blue-600" /><h2 className="text-sm font-bold text-slate-900">正式架构</h2></div><div className="mt-4 space-y-2">{["React Renderer", "Electron Main · Node.js", "C# Word Worker", "Microsoft Word COM"].map((label, index, values) => <div key={label}><div className="flex items-center gap-3 rounded-lg border border-slate-200 bg-slate-50 px-3 py-2.5"><span className="flex h-6 w-6 items-center justify-center rounded-md bg-slate-900 text-[10px] font-bold text-white">{index + 1}</span><span className="text-xs font-bold text-slate-700">{label}</span></div>{index < values.length - 1 ? <ChevronRight className="mx-auto my-1 h-3.5 w-3.5 rotate-90 text-slate-300" /> : null}</div>)}</div></section><section className="panel p-5"><div className="flex items-center gap-2"><ShieldCheck className="h-4 w-4 text-emerald-600" /><h2 className="text-sm font-bold text-slate-900">安全边界</h2></div><ul className="mt-3 space-y-2 text-[11px] leading-5 text-slate-500"><li>• Renderer 不持有真实文件路径</li><li>• preload 仅暴露类型化 API</li><li>• Main 再次校验全部入参</li><li>• Worker 独立进程、Word 全局串行</li></ul></section></aside>
      </div>

      <section className="grid grid-cols-3 gap-4"><div className="panel p-4"><div className="flex items-center gap-2 text-xs font-bold"><FileOutput className="h-4 w-4 text-blue-600" /> 输出行为</div><p className="mt-2 text-[11px] leading-5 text-slate-500">每次生成均选择输出，Renderer 只接收输出 handle 与脱敏显示信息。</p></div><div className="panel p-4"><div className="flex items-center justify-between"><div className="flex items-center gap-2 text-xs font-bold"><Database className="h-4 w-4 text-violet-600" /> 模板库</div>{adapter.runtimeCapabilities.shell === "native" ? <button type="button" onClick={() => void adapter.shell.openTemplateLibrary()} aria-label="打开模板库目录" className="text-blue-600"><FolderOpen className="h-4 w-4" /></button> : null}</div><p className="mt-2 text-[11px] leading-5 text-slate-500">{adapter.runtimeCapabilities.templateStorage === "main" ? "Portable 发布版位于 MD2Word.exe 同级 templates；当前目录由 Main 原子维护。" : "浏览器 localStorage 演示数据。"}</p></div><div className="panel p-4"><div className="flex items-center justify-between"><div className="flex items-center gap-2 text-xs font-bold"><HardDrive className="h-4 w-4 text-amber-600" /> {adapter.demo ? "演示数据" : "运行数据"}</div>{adapter.demo ? <button type="button" onClick={() => setResetOpen(true)} className="inline-flex items-center gap-1 text-[10px] font-bold text-blue-600"><RotateCcw className="h-3 w-3" /> 重置</button> : null}</div><p className="mt-2 text-[11px] leading-5 text-slate-500">{adapter.demo ? "恢复两个初始模拟模板与环境状态。" : "真实模板与任务数据由桌面 Main 管理。"}</p></div></section>

      <Modal open={resetOpen} title="重置原型演示数据" description="仅清理本应用浏览器 mock 存储。" onClose={() => setResetOpen(false)} footer={<><button type="button" onClick={() => setResetOpen(false)} className="rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold">取消</button><button type="button" onClick={() => void reset()} className="rounded-lg bg-blue-600 px-5 py-2 text-sm font-bold text-white">确认重置</button></>}><div className="flex items-start gap-3 rounded-xl border border-amber-200 bg-amber-50 p-4"><AlertTriangle className="h-5 w-5 text-amber-600" /><div><p className="text-sm font-bold text-amber-900">自定义演示配置将被移除</p><p className="mt-1 text-xs text-amber-700">不会读写任何真实模板、Markdown 或输出文件。</p></div></div></Modal>
    </div>
  );
}
