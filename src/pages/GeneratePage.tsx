import {
  AlertCircle,
  ArrowRight,
  CheckCircle2,
  CircleStop,
  Clock3,
  FileCheck2,
  FolderOpen,
  Info,
  LoaderCircle,
  Play,
  RotateCcw,
  Save,
  ScrollText,
  Sparkles,
  TerminalSquare,
  TriangleAlert,
} from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { FileDropzone } from "../components/FileDropzone";
import { Modal } from "../components/Modal";
import { StatusBadge } from "../components/StatusBadge";
import { TemplateSelector } from "../components/TemplateSelector";
import type { ConversionTaskState } from "../lib/conversionState";
import { errorMessage } from "../lib/errorMessage";
import { isActiveConversionStatus } from "../lib/conversionState";
import type {
  EnvironmentStatus,
  PickedFile,
  RuntimeCapabilities,
  SelectedMarkdown,
  TemplateProfile,
  WordStyleMapping,
} from "../types";

interface GeneratePageProps {
  templates: TemplateProfile[];
  selectedTemplateId: string | null;
  onTemplateChange: (id: string) => void;
  environment: EnvironmentStatus;
  capabilities: RuntimeCapabilities;
  markdown: SelectedMarkdown | null;
  task: ConversionTaskState;
  onMarkdownChange: (file: SelectedMarkdown | null) => void;
  onPickMarkdown: () => Promise<PickedFile | null>;
  onRegisterMarkdown: (file: File) => Promise<PickedFile>;
  onRequestOutput: (failureDemo: boolean) => Promise<void>;
  onCancelOutput: () => void;
  onConfirmDemoOutput: (directoryLabel: string, fileName: string) => Promise<void>;
  onCancelConversion: () => Promise<void>;
  onOpenOutput: () => Promise<void>;
  onRevealOutput: () => Promise<void>;
  onNavigateTemplates: () => void;
}

const phaseLabels = {
  queued: "等待执行",
  preparing: "环境与模板检查",
  metadata: "读取文档元数据",
  pandoc: "解析 Markdown",
  mermaid: "资源处理",
  "word-import": "导入 Word 内容",
  "template-assembly": "Word 模板装配",
  "word-finalize": "Word 文档收口",
  "openxml-finalize": "Open XML 后处理",
  cleanup: "保存与清理",
} as const;

const phaseOrder = Object.keys(phaseLabels) as Array<keyof typeof phaseLabels>;

const timeLabel = (timestamp: string) =>
  new Date(timestamp).toLocaleTimeString("zh-CN", { hour12: false, hour: "2-digit", minute: "2-digit", second: "2-digit" });

const normalizedDocxName = (fileName: string) => {
  const trimmed = fileName.trim().replace(/[. ]+$/, "");
  if (!trimmed) return "document.docx";
  return /\.docx$/i.test(trimmed) ? trimmed : `${trimmed.replace(/\.[^.]+$/, "")}.docx`;
};

const mappingLabel = (mapping: WordStyleMapping | undefined) => {
  if (!mapping) return "未报告";
  if (mapping.status !== "resolved" && mapping.status !== "word-fallback") return mapping.message || "未解析";
  const styleName = mapping.resolvedStyleName || mapping.requestedStyleName || "已解析";
  return mapping.status === "word-fallback" ? `${styleName}（Word 原生）` : styleName;
};

export function GeneratePage({
  templates,
  selectedTemplateId,
  onTemplateChange,
  environment,
  capabilities,
  markdown,
  task,
  onMarkdownChange,
  onPickMarkdown,
  onRegisterMarkdown,
  onRequestOutput,
  onCancelOutput,
  onConfirmDemoOutput,
  onCancelConversion,
  onOpenOutput,
  onRevealOutput,
  onNavigateTemplates,
}: GeneratePageProps) {
  const [outputDirectory, setOutputDirectory] = useState("演示文档目录");
  const [outputFileName, setOutputFileName] = useState("document.docx");
  const [toast, setToast] = useState<string | null>(null);
  const [logsCollapsed, setLogsCollapsed] = useState(false);
  const [hiddenEventCount, setHiddenEventCount] = useState(0);

  const selectedTemplate = useMemo(
    () => templates.find((template) => template.id === selectedTemplateId) ?? null,
    [selectedTemplateId, templates],
  );
  const requiredEnvironmentReady = environment.items.filter((item) => item.required).every((item) => item.status === "ready");
  const selectedTemplateUsable = Boolean(selectedTemplate && selectedTemplate.validation.status !== "invalid");
  const active = isActiveConversionStatus(task.status);
  const displayedEvents = task.events.slice(Math.min(hiddenEventCount, task.events.length));
  const phaseIndex = task.stage ? Math.max(0, phaseOrder.indexOf(task.stage)) : 0;
  const stageProgress = task.status === "succeeded" ? 100 : task.status === "idle" || task.status === "choosing-output" ? 0 : ((phaseIndex + 1) / phaseOrder.length) * 100;
  const orderedListMapping = selectedTemplate?.validation.styleMappings.find((mapping) => mapping.role === "ordered-list");
  const unorderedListMapping = selectedTemplate?.validation.styleMappings.find((mapping) => mapping.role === "unordered-list");

  useEffect(() => {
    if (task.events.length === 0) setHiddenEventCount(0);
  }, [task.events.length]);

  useEffect(() => {
    if (!markdown) return;
    const stem = markdown.fileName.replace(/\.(md|markdown)$/i, "");
    setOutputFileName(`${stem}.docx`);
  }, [markdown]);

  const handleTemplateChange = (id: string) => {
    if (id !== selectedTemplateId) onTemplateChange(id);
  };

  const openOutput = async (kind: "open" | "reveal") => {
    try {
      await (kind === "open" ? onOpenOutput() : onRevealOutput());
      if (capabilities.shell === "mock") setToast(kind === "open" ? "演示：未打开真实 Word 文件" : "演示：未打开真实文件夹");
    } catch (reason) {
      setToast(errorMessage(reason, "操作失败，请重试。"));
    }
  };

  return (
    <div className="page-enter space-y-6">
      <section className="flex items-end justify-between gap-8">
        <div>
          <div className="mb-2 flex items-center gap-2 text-xs font-bold uppercase tracking-[0.16em] text-blue-600">
            <Sparkles className="h-4 w-4" /> 单文档工作流
          </div>
          <h1 className="text-[28px] font-bold tracking-tight text-slate-950">把 Markdown 交给正确的 Word 模板</h1>
          <p className="mt-2 max-w-3xl text-sm leading-6 text-slate-500">模板与 CSS 作为一个配置管理；文件路径只由桌面 Main 与 Worker 解析。</p>
        </div>
        <div className={`flex items-center gap-3 rounded-xl border px-4 py-3 ${capabilities.backend === "electron" ? "border-emerald-100 bg-emerald-50" : "border-blue-100 bg-blue-50"}`}>
          <div className={`flex h-9 w-9 items-center justify-center rounded-lg text-white ${capabilities.backend === "electron" ? "bg-emerald-600" : "bg-blue-600"}`}><Info className="h-4.5 w-4.5" /></div>
          <div>
            <p className="text-xs font-bold text-slate-900">{capabilities.backend === "electron" ? "桌面正式接入" : "当前为交互原型"}</p>
            <p className="mt-0.5 text-[11px] text-slate-600">{capabilities.conversion === "worker" ? "任务由独立 C# Worker 执行" : "演示转换，不会写入真实文件"}</p>
          </div>
        </div>
      </section>

      {!requiredEnvironmentReady ? (
        <div className="flex items-center justify-between rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-800">
          <span className="flex items-center gap-2 font-semibold"><AlertCircle className="h-4 w-4" /> 必需运行环境不完整，已禁止启动转换。</span>
          <span className="text-xs">Windows、Word、Pandoc 与 Worker 均须就绪</span>
        </div>
      ) : null}

      <div className="grid grid-cols-[minmax(0,1.65fr)_minmax(330px,0.75fr)] gap-6">
        <section className="panel overflow-hidden">
          <div className="flex items-center justify-between border-b border-slate-100 px-6 py-5">
            <div>
              <div className="flex items-center gap-2"><span className="flex h-6 w-6 items-center justify-center rounded-full bg-blue-600 text-xs font-bold text-white">1</span><h2 className="text-base font-bold text-slate-900">选择模板</h2></div>
              <p className="ml-8 mt-1 text-xs text-slate-500">生成时 DOCX 与 CSS 始终作为一个整体使用</p>
            </div>
            <button type="button" onClick={onNavigateTemplates} disabled={active} className="text-xs font-bold text-blue-600 hover:text-blue-800 disabled:text-slate-400">管理模板 <ArrowRight className="ml-1 inline h-3.5 w-3.5" /></button>
          </div>
          <TemplateSelector templates={templates} selectedTemplateId={selectedTemplateId} disabled={active} onSelect={handleTemplateChange} onManage={onNavigateTemplates} />

          <div className="border-t border-slate-100 px-6 py-5">
            <div className="mb-4 flex items-center gap-2"><span className="flex h-6 w-6 items-center justify-center rounded-full bg-blue-600 text-xs font-bold text-white">2</span><div><h2 className="text-base font-bold text-slate-900">选择 Markdown</h2><p className="mt-1 text-xs text-slate-500">Renderer 只保存 opaque handle 与必要的文件元信息</p></div></div>
            <FileDropzone value={markdown} capabilities={capabilities} disabled={active} onPick={onPickMarkdown} onRegister={onRegisterMarkdown} onChange={onMarkdownChange} />
          </div>

          <div className="flex items-center justify-between border-t border-slate-100 bg-slate-50/60 px-6 py-4">
            <div className="flex items-center gap-2 text-xs text-slate-500"><Save className="h-4 w-4 text-slate-400" /> 每次生成均重新选择 `.docx` 输出</div>
            <div className="flex gap-2">
              {capabilities.conversion === "mock" ? <button type="button" onClick={() => void onRequestOutput(true)} disabled={!markdown || !selectedTemplateUsable || active} className="rounded-lg border border-slate-300 bg-white px-3.5 py-2 text-xs font-bold text-slate-600 transition hover:bg-slate-100 disabled:cursor-not-allowed disabled:opacity-40">演示错误</button> : null}
              <button type="button" onClick={() => void onRequestOutput(false)} disabled={!markdown || !selectedTemplateUsable || active || !requiredEnvironmentReady} className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-5 py-2 text-sm font-bold text-white shadow-sm transition hover:bg-blue-700 disabled:cursor-not-allowed disabled:bg-slate-300"><Play className="h-4 w-4 fill-current" /> 生成 Word</button>
            </div>
          </div>
        </section>

        <aside className="space-y-5">
          <section className="panel p-5">
            <div className="flex items-center justify-between"><h2 className="text-sm font-bold text-slate-900">当前配置</h2>{selectedTemplate ? <StatusBadge state={selectedTemplate.validation.status} /> : null}</div>
            {selectedTemplate ? (
              <div className="mt-4 space-y-3">
                <div className="rounded-xl bg-slate-50 p-3.5">
                  <p className="text-[10px] font-bold uppercase tracking-wider text-slate-400">模板配置</p>
                  <p className="mt-1.5 text-sm font-bold text-slate-900">{selectedTemplate.name}</p>
                  <div className="mt-3 space-y-2 text-[11px] text-slate-600">
                    <div className="flex justify-between gap-4"><span>Word 模板</span><span className="truncate font-medium">{selectedTemplate.templateFileName}</span></div>
                    <div className="flex justify-between gap-4"><span>样式来源</span><span className="truncate font-medium">{selectedTemplate.css.fileName}</span></div>
                    <div className="flex justify-between gap-4"><span>有序列表 `1.`</span><span className="truncate font-medium" title={mappingLabel(orderedListMapping)}>{mappingLabel(orderedListMapping)}</span></div>
                    <div className="flex justify-between gap-4"><span>无序列表 `-`</span><span className="truncate font-medium" title={mappingLabel(unorderedListMapping)}>{mappingLabel(unorderedListMapping)}</span></div>
                    <div className="flex justify-between gap-4"><span>最近校验</span><span className="font-medium">{new Date(selectedTemplate.validation.checkedAt).toLocaleDateString("zh-CN")}</span></div>
                  </div>
                </div>
                {selectedTemplate.validation.status !== "valid" ? <div className={`rounded-xl border p-3 text-[11px] leading-5 ${selectedTemplate.validation.status === "invalid" ? "border-red-200 bg-red-50 text-red-700" : "border-amber-200 bg-amber-50 text-amber-700"}`}><p className="font-bold">{selectedTemplate.validation.summary}</p>{selectedTemplate.validation.issues[0] ? <p className="mt-1">{selectedTemplate.validation.issues[0].message}</p> : null}</div> : null}
              </div>
            ) : <p className="mt-4 rounded-xl bg-slate-50 p-4 text-xs text-slate-500">请先选择或添加模板。</p>}
          </section>

          <section className="panel overflow-hidden">
            <div className="flex items-center justify-between border-b border-slate-100 px-5 py-4"><div className="flex items-center gap-2"><TerminalSquare className="h-4 w-4 text-slate-500" /><h2 className="text-sm font-bold text-slate-900">任务状态</h2></div>{task.status === "queued" || task.status === "running" || task.status === "canceling" ? <span className="flex items-center gap-1.5 text-[11px] font-bold text-blue-600"><LoaderCircle className="h-3.5 w-3.5 animate-spin" /> {task.status === "queued" ? "排队中" : task.status === "canceling" ? "安全取消中" : "处理中"}</span> : null}</div>
            <div className="p-5">
              {task.status === "idle" && !task.result ? <div className="flex min-h-40 flex-col items-center justify-center text-center"><div className="rounded-full bg-slate-100 p-3 text-slate-400"><Clock3 className="h-5 w-5" /></div><p className="mt-3 text-xs font-bold text-slate-600">等待生成任务</p><p className="mt-1 max-w-56 text-[11px] leading-5 text-slate-400">选择模板和 Markdown 后，任务阶段与日志会显示在这里。</p></div> : null}
              {task.status !== "idle" && task.status !== "choosing-output" ? <div className="mb-4"><div className="mb-2 flex items-center justify-between text-[11px]"><span className="font-semibold text-slate-600">{task.stage ? phaseLabels[task.stage] : "准备任务"}</span><span className="font-bold text-blue-700">阶段 {Math.min(phaseIndex + 1, phaseOrder.length)} / {phaseOrder.length}</span></div><div className="h-2 overflow-hidden rounded-full bg-slate-100"><div className={`h-full rounded-full transition-all duration-500 ${task.status === "failed" ? "bg-red-500" : "bg-blue-600"}`} style={{ width: `${stageProgress}%` }} /></div></div> : null}
              {task.status === "queued" || task.status === "running" ? <button type="button" onClick={() => void onCancelConversion()} className="mt-4 flex w-full items-center justify-center gap-2 rounded-lg border border-red-200 bg-red-50 py-2 text-xs font-bold text-red-700 hover:bg-red-100"><CircleStop className="h-4 w-4" /> 取消生成</button> : null}
              {task.status === "canceling" ? <button type="button" disabled className="mt-4 flex w-full items-center justify-center gap-2 rounded-lg border border-slate-200 bg-slate-50 py-2 text-xs font-bold text-slate-500"><LoaderCircle className="h-4 w-4 animate-spin" /> 正在安全取消…</button> : null}
              {task.status === "succeeded" && task.result ? <div className="mt-4 rounded-xl border border-emerald-200 bg-emerald-50 p-3.5"><div className="flex items-center gap-2 text-xs font-bold text-emerald-800"><FileCheck2 className="h-4 w-4" /> {capabilities.conversion === "mock" ? "演示生成完成" : "生成完成"}</div><p className="mt-2 break-all text-[10px] leading-4 text-emerald-700">{task.result.outputDisplayPath || task.result.outputFileName}</p>{task.result.warnings.length ? <div aria-label="转换警告" className="mt-3 rounded-lg border border-amber-200 bg-amber-50 p-2.5 text-amber-800"><div className="flex items-center gap-1.5 text-[10px] font-bold"><TriangleAlert className="h-3.5 w-3.5" /> 已生成，但有 {task.result.warnings.length} 条警告</div><ul className="mt-1.5 space-y-1 text-[10px] leading-4">{task.result.warnings.map((warning, index) => <li key={`${index}-${warning}`}>• {warning}</li>)}</ul></div> : null}<div className="mt-3 flex gap-2"><button type="button" onClick={() => void openOutput("open")} className="flex-1 rounded-md bg-emerald-700 py-1.5 text-[10px] font-bold text-white">{capabilities.shell === "mock" ? "模拟打开" : "打开文件"}</button><button type="button" onClick={() => void openOutput("reveal")} className="flex-1 rounded-md border border-emerald-300 bg-white py-1.5 text-[10px] font-bold text-emerald-700">{capabilities.shell === "mock" ? "模拟定位" : "在文件夹中显示"}</button></div></div> : null}
              {task.status === "failed" ? <div className="mt-4 rounded-xl border border-red-200 bg-red-50 p-3.5"><div className="flex items-center gap-2 text-xs font-bold text-red-800"><TriangleAlert className="h-4 w-4" /> {task.error?.code ?? "CONVERSION_FAILED"}</div><p className="mt-2 text-[11px] leading-5 text-red-700">{task.error?.message ?? "转换失败，请查看诊断日志。"}</p><button type="button" onClick={() => void onRequestOutput(false)} className="mt-3 flex w-full items-center justify-center gap-2 rounded-md bg-red-700 py-1.5 text-[10px] font-bold text-white"><RotateCcw className="h-3.5 w-3.5" /> 重新选择输出位置</button></div> : null}
              {task.status === "canceled" ? <div className="mt-4 rounded-xl border border-slate-200 bg-slate-50 p-3 text-[11px] text-slate-600">转换已安全取消，未保留部分输出。</div> : null}
            </div>
          </section>
        </aside>
      </div>

      {task.events.length ? <section className="panel overflow-hidden"><div className="flex items-center justify-between border-b border-slate-100 px-5 py-3"><div className="flex items-center gap-2"><ScrollText className="h-4 w-4 text-slate-500" /><h2 className="text-sm font-bold text-slate-900">任务日志</h2></div><div className="flex gap-3"><button type="button" onClick={() => setHiddenEventCount(task.events.length)} className="text-[11px] font-bold text-slate-500 hover:text-slate-800">清空显示</button><button type="button" onClick={() => setLogsCollapsed((value) => !value)} className="text-[11px] font-bold text-blue-600">{logsCollapsed ? "展开" : "折叠"}</button></div></div>{!logsCollapsed ? <div className="max-h-48 overflow-y-auto px-5 py-3 app-scrollbar" aria-live="polite">{displayedEvents.length ? <div className="space-y-2">{displayedEvents.map((event, index) => <div key={`${event.jobId}-${event.timestamp}-${index}`} className="grid grid-cols-[74px_58px_1fr] gap-3 text-[11px]"><span className="font-mono text-slate-400">{timeLabel(event.timestamp)}</span><span className={`font-bold ${event.level === "error" ? "text-red-600" : event.level === "warning" ? "text-amber-600" : "text-blue-600"}`}>{event.level ?? "info"}</span><span className="text-slate-600">{event.message || (event.stage ? phaseLabels[event.stage] : event.kind)}</span></div>)}</div> : <p className="text-center text-xs text-slate-400">日志显示已清空；任务仍会继续记录诊断信息。</p>}</div> : null}</section> : null}

      <Modal open={task.status === "choosing-output" && capabilities.fileDialogs === "mock"} title="另存为 Word 文档" description="浏览器原型使用此窗口模拟 Electron 原生系统对话框。" onClose={onCancelOutput} footer={<><button type="button" onClick={onCancelOutput} className="rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-600 hover:bg-slate-50">取消</button><button type="button" onClick={() => void onConfirmDemoOutput(outputDirectory, normalizedDocxName(outputFileName))} disabled={!outputDirectory.trim() || !outputFileName.trim()} className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-5 py-2 text-sm font-bold text-white hover:bg-blue-700 disabled:opacity-40"><Save className="h-4 w-4" /> 保存并生成</button></>}>
        <div className="space-y-4"><div className="rounded-xl border border-blue-100 bg-blue-50 p-3 text-xs leading-5 text-blue-800">演示窗口只创建 opaque output handle，不会创建文件或保存真实路径。</div><label><span className="field-label">保存目录</span><div className="flex gap-2"><input aria-label="保存目录" className="field-control" value={outputDirectory} onChange={(event) => setOutputDirectory(event.target.value)} /><button type="button" className="shrink-0 rounded-lg border border-slate-300 px-3 text-slate-500" aria-label="浏览目录（演示）"><FolderOpen className="h-4 w-4" /></button></div></label><label><span className="field-label">文件名</span><input aria-label="文件名" className="field-control" value={outputFileName} onChange={(event) => setOutputFileName(event.target.value)} onBlur={() => setOutputFileName((value) => normalizedDocxName(value))} /></label><div className="flex items-start gap-2 rounded-lg bg-slate-50 p-3 text-[11px] leading-5 text-slate-500"><ScrollText className="mt-0.5 h-4 w-4 shrink-0" />输出扩展名固定为 `.docx`；正式版覆盖确认由 Windows 系统对话框负责。</div></div>
      </Modal>

      {toast ? <button type="button" onClick={() => setToast(null)} className="fixed bottom-6 right-6 z-40 flex items-center gap-2 rounded-xl bg-slate-900 px-4 py-3 text-xs font-semibold text-white shadow-2xl"><CheckCircle2 className="h-4 w-4 text-emerald-400" /> {toast}</button> : null}
    </div>
  );
}
