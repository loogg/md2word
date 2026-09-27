import { AlertTriangle, AppWindow, Database, FileOutput, FolderOpen, MonitorCog, RefreshCw, RotateCcw, TerminalSquare, Workflow } from "lucide-react";
import { useEffect, useRef, useState } from "react";
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

const itemIcons = { windows: AppWindow, word: FileOutput, pandoc: TerminalSquare, worker: Workflow, mermaid: MonitorCog };

export function EnvironmentPage({ environment, adapter, onEnvironmentChange, onReset }: EnvironmentPageProps) {
  const headingRef = useRef<HTMLHeadingElement>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [resetOpen, setResetOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [openingLibrary, setOpeningLibrary] = useState(false);
  const isDemo = adapter.runtimeCapabilities.environment === "mock";
  const word = environment.items.find((item) => item.id === "word");

  useEffect(() => { headingRef.current?.focus(); }, []);

  const refresh = async () => {
    setRefreshing(true);
    setError(null);
    try { onEnvironmentChange(await adapter.environment.check()); }
    catch (reason) { setError(errorMessage(reason, "环境检查失败，请重试。")); }
    finally { setRefreshing(false); }
  };

  const toggleWord = async () => {
    if (!adapter.demo) return;
    setError(null);
    try { onEnvironmentChange(await adapter.demo.setWordAvailable(word?.status === "blocked")); }
    catch (reason) { setError(errorMessage(reason, "演示状态切换失败。")); }
  };

  const reset = async () => {
    if (!adapter.demo) return;
    setError(null);
    try { onReset(await adapter.demo.reset()); setResetOpen(false); }
    catch (reason) { setError(errorMessage(reason, "重置演示数据失败。")); }
  };

  const openLibrary = async () => {
    setOpeningLibrary(true);
    setError(null);
    try { await adapter.shell.openTemplateLibrary(); }
    catch (reason) { setError(errorMessage(reason, "无法打开模板库，请重试。")); }
    finally { setOpeningLibrary(false); }
  };

  return (
    <div className="page-enter max-w-[1100px] space-y-6">
      <section className="flex items-end justify-between gap-6">
        <div><h1 ref={headingRef} tabIndex={-1} className="text-[28px] font-semibold tracking-tight text-slate-950">环境与设置</h1><p className="mt-1 text-sm text-slate-500">检查生成 Word 所需的本机组件与模板库位置。</p></div>
        <button type="button" onClick={() => void refresh()} disabled={refreshing} className="fluent-button"><RefreshCw className={`h-4 w-4 ${refreshing ? "animate-spin" : ""}`} /> {refreshing ? "检查中" : "重新检查"}</button>
      </section>

      {error ? <div role="alert" className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{error}</div> : null}

      <section className="panel overflow-hidden" aria-label="运行依赖检查">
        <div className="flex flex-wrap items-center justify-between gap-2 border-b border-slate-100 px-5 py-4"><h2 className="text-sm font-semibold text-slate-900">运行依赖</h2><p className="text-xs text-slate-500">上次检查：{new Date(environment.checkedAt).toLocaleString("zh-CN", { hour12: false })}{isDemo ? " · 模拟结果" : ""}</p></div>
        <div className="divide-y divide-slate-100">
          {environment.items.map((item) => {
            const Icon = itemIcons[item.id];
            return <div key={item.id} className="flex min-h-[72px] items-center gap-4 px-5 py-3">
              <div className="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-slate-100 text-slate-600"><Icon className="h-4.5 w-4.5" /></div>
              <div className="min-w-0 flex-1"><div className="flex flex-wrap items-baseline gap-x-2"><h3 className="text-sm font-semibold text-slate-800">{item.name}</h3><span className="text-[11px] text-slate-500">{item.version}</span></div><p className="mt-0.5 text-[11px] leading-5 text-slate-500">{item.detail}</p></div>
              <span className="hidden shrink-0 text-[11px] text-slate-500 sm:inline">{item.required ? "必需" : "可选"}</span>
              <StatusBadge state={item.status} label={isDemo && item.status === "ready" ? "模拟就绪" : undefined} />
            </div>;
          })}
        </div>
        <div className="border-t border-slate-100 bg-slate-50 px-5 py-3 text-xs text-slate-500">Mermaid 为可选组件；是否需要渲染由模板的 Mermaid 模式决定。</div>
      </section>

      <section className="grid grid-cols-2 gap-4">
        <div className="panel p-5"><div className="flex items-center justify-between gap-3"><h2 className="flex items-center gap-2 text-sm font-semibold text-slate-900"><Database className="h-4 w-4 text-blue-600" /> 模板库</h2>{adapter.runtimeCapabilities.shell === "native" ? <button type="button" onClick={() => void openLibrary()} disabled={openingLibrary} className="fluent-button" aria-label="打开模板库目录"><FolderOpen className="h-4 w-4" /> 打开模板库</button> : null}</div><p className="mt-2 text-xs leading-6 text-slate-600">{adapter.runtimeCapabilities.templateStorage === "main" ? "便携版使用 EXE 同级 templates；安装版使用用户数据目录，由 Main 原子维护。" : "浏览器本地存储中的演示模板，不读取本机真实模板。"}</p></div>
        <div className="panel p-5"><h2 className="text-sm font-semibold text-slate-900">输出位置</h2><p className="mt-2 text-xs leading-6 text-slate-600">每次生成均在“另存为”中选择 DOCX 保存位置。{isDemo ? "浏览器预览仅演示该流程。" : "真实文件路径仅在桌面进程中使用。"}</p></div>
      </section>

      {adapter.demo ? <section className="panel flex flex-wrap items-center justify-between gap-4 p-5"><div><h2 className="text-sm font-semibold text-slate-900">演示状态</h2><p className="mt-1 text-xs text-slate-500">只影响浏览器模拟数据，便于检查错误和空态。</p></div><div className="flex gap-2"><button type="button" onClick={() => void toggleWord()} className="fluent-button">{word?.status === "blocked" ? "恢复 Word 就绪" : "模拟 Word 缺失"}</button><button type="button" onClick={() => setResetOpen(true)} className="fluent-button"><RotateCcw className="h-4 w-4" /> 重置演示数据</button></div></section> : null}

      <Modal open={resetOpen} title="重置原型演示数据" description="仅清理本应用浏览器 mock 存储。" onClose={() => setResetOpen(false)} footer={<><button type="button" onClick={() => setResetOpen(false)} className="fluent-button">取消</button><button type="button" onClick={() => void reset()} className="fluent-button fluent-button-primary">确认重置</button></>}><div className="flex items-start gap-3 rounded-lg border border-amber-200 bg-amber-50 p-4"><AlertTriangle className="h-5 w-5 text-amber-600" /><div><p className="text-sm font-semibold text-amber-900">自定义演示配置将被移除</p><p className="mt-1 text-xs text-amber-700">不会读写任何真实模板、Markdown 或输出文件。</p></div></div></Modal>
    </div>
  );
}
