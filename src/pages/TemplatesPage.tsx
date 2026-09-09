import {
  AlertTriangle,
  CheckCircle2,
  ChevronRight,
  FileCode2,
  FilePlus2,
  FileText,
  LoaderCircle,
  Pencil,
  Plus,
  RefreshCw,
  Search,
  ShieldCheck,
  Star,
  Trash2,
  Upload,
} from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { Modal } from "../components/Modal";
import { StatusBadge } from "../components/StatusBadge";
import { errorMessage } from "../lib/errorMessage";
import type {
  AddTemplateInput,
  AppAdapter,
  PickedFile,
  TemplateDraft,
  TemplateProfile,
  TemplateValidationIssue,
  TemplateValidationReport,
  UpdateTemplateInput,
  ValidateTemplateInput,
  WordStyleMapping,
  WordStyleRole,
} from "../types";

interface TemplatesPageProps {
  templates: TemplateProfile[];
  adapter: AppAdapter;
  onTemplatesChange: (templates: TemplateProfile[]) => void;
  onDefaultSelected: (id: string) => void;
  onOpenCapability?: (id: string) => void;
}

const emptyDraft: TemplateDraft = {
  name: "",
  description: "",
  templateFileName: "",
  templateFileHandle: null,
  styleMode: "builtin",
  styleCssFileName: "",
  styleCssHandle: null,
  mermaidMode: "auto",
  mermaidFormat: "png",
};

const pickedFromDraft = (handle: string | null, fileName: string): PickedFile | undefined =>
  handle ? { handle, fileName, size: 0, lastModified: 0 } : undefined;

const inputFromDraft = (draft: TemplateDraft, templateId?: string): ValidateTemplateInput => ({
  templateId,
  name: draft.name.trim(),
  description: draft.description.trim(),
  templateFile: pickedFromDraft(draft.templateFileHandle, draft.templateFileName),
  css: {
    mode: draft.styleMode,
    file: draft.styleMode === "custom" ? pickedFromDraft(draft.styleCssHandle, draft.styleCssFileName) : undefined,
  },
  mermaidDefaults: { mode: draft.mermaidMode, format: draft.mermaidFormat },
});

const mappingNames: Record<WordStyleRole, string> = {
  body: "正文",
  "ordered-list": "有序列表 `1.`",
  "unordered-list": "无序列表 `-`",
  heading: "标题",
  caption: "图注",
  "table-caption": "表题",
  "code-block": "代码块",
  "inline-code": "行内代码",
  table: "表格正文",
  admonition: "提示/引用",
  "figure-image": "图片段落",
};

function StyleMappingList({ mappings }: { mappings: WordStyleMapping[] }) {
  return (
    <div className="mt-3 grid grid-cols-2 gap-2">
      {mappings.map((mapping, index) => (
        <div key={`${mapping.role}-${mapping.headingLevel ?? 0}-${index}`} className="rounded-lg border border-white/70 bg-white/70 px-3 py-2 text-[10px]">
          <div className="flex items-center justify-between gap-2">
            <span className="font-bold text-slate-600">{mappingNames[mapping.role]}{mapping.headingLevel ? ` H${mapping.headingLevel}` : ""}</span>
            <span className={`font-bold ${mapping.status === "resolved" ? "text-emerald-700" : mapping.status === "word-fallback" ? "text-blue-700" : mapping.status === "missing" ? "text-red-700" : "text-amber-700"}`}>{mapping.status === "resolved" ? "CSS 已解析" : mapping.status === "word-fallback" ? "Word 原生" : mapping.status === "missing" ? "缺失" : mapping.status === "ambiguous" ? "有歧义" : "未配置"}</span>
          </div>
          <p className="mt-1 truncate text-slate-500" title={mapping.resolvedStyleName || mapping.requestedStyleName || mapping.message}>{mapping.resolvedStyleName || mapping.requestedStyleName || mapping.message || "—"}</p>
        </div>
      ))}
    </div>
  );
}

function ValidationIssueList({
  issues,
  onOpenCapability,
}: {
  issues: TemplateValidationIssue[];
  onOpenCapability?: (id: string) => void;
}) {
  if (!issues.length) return null;
  return (
    <ul className="mt-3 space-y-2 text-[11px] text-slate-600">
      {issues.map((issue) => (
        <li key={`${issue.code}-${issue.message}`} className="rounded-lg border border-white/70 bg-white/60 px-3 py-2">
          <div className="flex items-start justify-between gap-3">
            <span><span className="mr-1.5 font-mono text-[9px] text-slate-400">{issue.code}</span>{issue.message}</span>
            {issue.capabilityId && onOpenCapability ? (
              <button
                type="button"
                onClick={() => onOpenCapability(issue.capabilityId!)}
                className="shrink-0 font-bold text-blue-700 hover:text-blue-900"
              >
                查看对应能力
              </button>
            ) : null}
          </div>
        </li>
      ))}
    </ul>
  );
}

function TemplateEditor({
  open,
  current,
  templates,
  adapter,
  onClose,
  onSaved,
  onOpenCapability,
}: {
  open: boolean;
  current: TemplateProfile | null;
  templates: TemplateProfile[];
  adapter: AppAdapter;
  onClose: () => void;
  onSaved: (profile: TemplateProfile) => void;
  onOpenCapability?: (id: string) => void;
}) {
  const initialDraft: TemplateDraft = current
    ? {
        name: current.name,
        description: current.description,
        templateFileName: current.templateFileName,
        templateFileHandle: null,
        styleMode: current.css.mode,
        styleCssFileName: current.css.mode === "custom" ? current.css.fileName : "",
        styleCssHandle: null,
        mermaidMode: current.mermaidDefaults.mode,
        mermaidFormat: current.mermaidDefaults.format,
      }
    : emptyDraft;
  const [draft, setDraft] = useState<TemplateDraft>(initialDraft);
  const [report, setReport] = useState<TemplateValidationReport | null>(current?.validation ?? null);
  const [formError, setFormError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [warningConfirmed, setWarningConfirmed] = useState(false);

  const updateDraft = (patch: Partial<TemplateDraft>) => {
    setDraft((value) => ({ ...value, ...patch }));
    setReport(null);
    setFormError(null);
    setWarningConfirmed(false);
  };

  const selectFile = async (kind: "template-docx" | "css") => {
    setBusy(true);
    setFormError(null);
    try {
      const picked = await (kind === "template-docx" ? adapter.files.pickTemplateDocx() : adapter.files.pickCss());
      if (!picked) return;
      updateDraft(kind === "template-docx"
        ? { templateFileName: picked.fileName, templateFileHandle: picked.handle }
        : { styleCssFileName: picked.fileName, styleCssHandle: picked.handle });
    } catch (reason) {
      setFormError(errorMessage(reason, "文件选择失败，请重试。"));
    } finally {
      setBusy(false);
    }
  };

  const registerDemoFile = async (kind: "template-docx" | "css", file: File | undefined) => {
    if (!file || !adapter.demo) return;
    setBusy(true);
    try {
      const picked = await adapter.demo.registerFile(kind, file);
      updateDraft(kind === "template-docx"
        ? { templateFileName: picked.fileName, templateFileHandle: picked.handle }
        : { styleCssFileName: picked.fileName, styleCssHandle: picked.handle });
    } finally {
      setBusy(false);
    }
  };

  const validate = async () => {
    if (!draft.name.trim()) {
      setFormError("请填写模板名称。");
      return null;
    }
    if (!current && !draft.templateFileHandle) {
      setFormError("请选择 DOCX 模板文件。");
      return null;
    }
    if (draft.styleMode === "custom" && !draft.styleCssHandle && current?.css.mode !== "custom") {
      setFormError("自定义模式必须选择 CSS 文件。");
      return null;
    }
    const duplicate = templates.some((template) => template.id !== current?.id && template.name.trim().toLocaleLowerCase("zh-CN") === draft.name.trim().toLocaleLowerCase("zh-CN"));
    if (duplicate) {
      setFormError("已有同名模板，请更换名称。");
      return null;
    }
    setBusy(true);
    try {
      const nextReport = await adapter.templates.validateDraft(inputFromDraft(draft, current?.id));
      setReport(nextReport);
      setFormError(null);
      return nextReport;
    } catch (reason) {
      setFormError(errorMessage(reason, "模板校验失败，请重试。"));
      return null;
    } finally {
      setBusy(false);
    }
  };

  const save = async () => {
    let nextReport = report ?? await validate();
    if (!nextReport || nextReport.status === "invalid") return;
    if (nextReport.status === "warning" && !warningConfirmed) {
      setWarningConfirmed(true);
      setFormError("模板存在警告；请检查后再次点击“确认警告并保存”。");
      return;
    }
    if (nextReport.status === "warning") {
      // The report loaded with an existing profile has no Main-side warning approval. Revalidate
      // after explicit confirmation so save consumes a fresh approval for this exact file pair.
      nextReport = await validate();
      if (!nextReport || nextReport.status === "invalid") return;
    }
    const validationInput = inputFromDraft(draft, current?.id);
    setBusy(true);
    setFormError(null);
    try {
      let profile: TemplateProfile;
      if (current) {
        const input: UpdateTemplateInput = validationInput;
        profile = await adapter.templates.update(current.id, input);
      } else {
        if (!validationInput.templateFile) throw new Error("请选择 DOCX 模板文件。");
        const input: AddTemplateInput = { ...validationInput, templateFile: validationInput.templateFile };
        profile = await adapter.templates.add(input);
      }
      onSaved(profile);
      onClose();
    } catch (reason) {
      setFormError(errorMessage(reason, "模板保存失败，请重试。"));
    } finally {
      setBusy(false);
    }
  };

  const setDemoName = (kind: "template-docx" | "css", fileName: string) => {
    const picked = adapter.demo?.registerNamedFile(kind, fileName);
    updateDraft(kind === "template-docx"
      ? { templateFileName: fileName, templateFileHandle: picked?.handle ?? null }
      : { styleCssFileName: fileName, styleCssHandle: picked?.handle ?? null });
  };

  return (
    <Modal
      key={current?.id ?? "new-template"}
      open={open}
      title={current ? "编辑模板配置" : "添加模板配置"}
      description={adapter.runtimeCapabilities.backend === "electron" ? "DOCX 与 CSS 将由 Main 导入、隔离保存并交给 Worker 校验。" : "浏览器演示只保存文件名与 mock handle，不读取真实文档内容。"}
      onClose={onClose}
      widthClass="max-w-2xl"
      footer={<><button type="button" onClick={onClose} disabled={busy} className="rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold text-slate-600">取消</button><button type="button" onClick={() => void validate()} disabled={busy} className="rounded-lg border border-blue-200 bg-blue-50 px-4 py-2 text-sm font-bold text-blue-700">校验配置</button><button type="button" onClick={() => void save()} disabled={busy} className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-5 py-2 text-sm font-bold text-white disabled:bg-slate-300">{busy ? <LoaderCircle className="h-4 w-4 animate-spin" /> : null}{report?.status === "warning" && warningConfirmed ? "确认警告并保存" : "保存模板"}</button></>}
    >
      <div className="space-y-5">
        <div className="grid grid-cols-2 gap-4"><label><span className="field-label">模板名称 *</span><input aria-label="模板名称" className="field-control" value={draft.name} onChange={(event) => updateDraft({ name: event.target.value })} /></label><label><span className="field-label">模板用途</span><input aria-label="模板用途" className="field-control" value={draft.description} onChange={(event) => updateDraft({ description: event.target.value })} /></label></div>

        <div><span className="field-label">Word 模板 *</span>{adapter.runtimeCapabilities.fileDialogs === "native" ? <button type="button" onClick={() => void selectFile("template-docx")} className="flex w-full items-center gap-3 rounded-xl border border-dashed border-slate-300 bg-slate-50 p-4 text-left"><div className="rounded-lg bg-white p-2.5 text-blue-600"><Upload className="h-5 w-5" /></div><div className="min-w-0 flex-1"><p className="truncate text-sm font-bold text-slate-800">{draft.templateFileName || "选择 .docx 模板文件"}</p><p className="mt-1 text-xs text-slate-500">由 Windows 原生对话框选择，路径不会交给 Renderer</p></div></button> : <label className="flex cursor-pointer items-center gap-3 rounded-xl border border-dashed border-slate-300 bg-slate-50 p-4"><input aria-label="Word 模板文件" className="sr-only" type="file" accept=".docx" onChange={(event) => void registerDemoFile("template-docx", event.target.files?.[0])} /><div className="rounded-lg bg-white p-2.5 text-blue-600"><Upload className="h-5 w-5" /></div><div className="min-w-0 flex-1"><p className="truncate text-sm font-bold text-slate-800">{draft.templateFileName || "选择 .docx 模板文件"}</p><p className="mt-1 text-xs text-slate-500">演示：只登记文件元信息</p></div></label>}{adapter.demo ? <input aria-label="模板文件名模拟输入" className="field-control mt-2 font-mono text-xs" placeholder="如：无书签旧版模板.docx" value={draft.templateFileName} onChange={(event) => setDemoName("template-docx", event.target.value)} /> : null}</div>

        <div><span className="field-label">样式来源 *</span><div className="grid grid-cols-2 gap-3"><button type="button" onClick={() => updateDraft({ styleMode: "builtin", styleCssFileName: "", styleCssHandle: null })} className={`rounded-xl border p-4 text-left ${draft.styleMode === "builtin" ? "border-blue-500 bg-blue-50" : "border-slate-200"}`}><div className="flex items-center gap-2 text-sm font-bold"><FileText className="h-4 w-4 text-blue-600" /> 使用内置默认 CSS</div><p className="mt-1.5 text-[11px] text-slate-500">随模板快照一同保存。</p></button><button type="button" onClick={() => updateDraft({ styleMode: "custom" })} className={`rounded-xl border p-4 text-left ${draft.styleMode === "custom" ? "border-blue-500 bg-blue-50" : "border-slate-200"}`}><div className="flex items-center gap-2 text-sm font-bold"><FileCode2 className="h-4 w-4 text-violet-600" /> 选择自定义 CSS</div><p className="mt-1.5 text-[11px] text-slate-500">逐项解析 mso-style-name。</p></button></div>{draft.styleMode === "custom" ? <div className="mt-3">{adapter.runtimeCapabilities.fileDialogs === "native" ? <button type="button" onClick={() => void selectFile("css")} className="field-control text-left text-xs font-semibold">{draft.styleCssFileName || "选择 .css 样式文件"}</button> : <label className="flex cursor-pointer items-center justify-between rounded-lg border border-slate-200 px-3 py-3"><input aria-label="CSS 样式文件" className="sr-only" type="file" accept=".css" onChange={(event) => void registerDemoFile("css", event.target.files?.[0])} /><span className="truncate text-xs font-semibold">{draft.styleCssFileName || "选择 .css 样式文件"}</span><span className="text-xs font-bold text-blue-600">浏览</span></label>}{adapter.demo ? <input aria-label="CSS 文件名模拟输入" className="field-control mt-2 font-mono text-xs" value={draft.styleCssFileName} onChange={(event) => setDemoName("css", event.target.value)} /> : null}</div> : null}</div>

        <div className="grid grid-cols-2 gap-4"><label><span className="field-label">Mermaid 默认模式</span><select aria-label="Mermaid 默认模式" className="field-control" value={draft.mermaidMode} onChange={(event) => updateDraft({ mermaidMode: event.target.value as TemplateDraft["mermaidMode"] })}><option value="auto">auto · 自动兼容</option><option value="off">off · 不渲染</option><option value="required">required · 缺失即失败</option></select></label><label><span className="field-label">Mermaid 格式</span><select aria-label="Mermaid 格式" className="field-control" value={draft.mermaidFormat} onChange={(event) => updateDraft({ mermaidFormat: event.target.value as TemplateDraft["mermaidFormat"] })}><option value="png">PNG</option><option value="svg">SVG</option></select></label></div>

        {formError ? <div role="alert" className="flex items-center gap-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2.5 text-xs font-semibold text-red-700"><AlertTriangle className="h-4 w-4" />{formError}</div> : null}
        {report ? <div className={`rounded-xl border p-4 ${report.status === "invalid" ? "border-red-200 bg-red-50" : report.status === "warning" ? "border-amber-200 bg-amber-50" : "border-emerald-200 bg-emerald-50"}`}><div className="flex items-center justify-between gap-3"><div className="flex items-center gap-2 text-sm font-bold text-slate-800">{report.status === "valid" ? <CheckCircle2 className="h-5 w-5 text-emerald-600" /> : <AlertTriangle className="h-5 w-5 text-amber-600" />}{report.summary}</div><StatusBadge state={report.status} /></div><ValidationIssueList issues={report.issues} onOpenCapability={onOpenCapability ? (id) => { onClose(); onOpenCapability(id); } : undefined} /><StyleMappingList mappings={report.styleMappings} /></div> : null}
      </div>
    </Modal>
  );
}

export function TemplatesPage({ templates, adapter, onTemplatesChange, onDefaultSelected, onOpenCapability }: TemplatesPageProps) {
  const pageHeadingRef = useRef<HTMLHeadingElement>(null);
  const [query, setQuery] = useState("");
  const [editorOpen, setEditorOpen] = useState(false);
  const [editing, setEditing] = useState<TemplateProfile | null>(null);
  const [deleting, setDeleting] = useState<TemplateProfile | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [pageError, setPageError] = useState<string | null>(null);
  const filtered = useMemo(() => { const normalized = query.trim().toLocaleLowerCase("zh-CN"); return normalized ? templates.filter((template) => `${template.name} ${template.description} ${template.templateFileName} ${template.css.fileName}`.toLocaleLowerCase("zh-CN").includes(normalized)) : templates; }, [query, templates]);

  useEffect(() => { pageHeadingRef.current?.focus(); }, []);

  const replaceProfile = (profile: TemplateProfile) => onTemplatesChange(templates.some((item) => item.id === profile.id) ? templates.map((item) => item.id === profile.id ? profile : item) : [...templates, profile]);
  const execute = async (id: string, action: () => Promise<void>) => { setBusyId(id); setPageError(null); try { await action(); } catch (reason) { setPageError(errorMessage(reason, "模板操作失败，请重试。")); } finally { setBusyId(null); } };

  return (
    <div className="page-enter space-y-6">
      <section className="flex items-end justify-between gap-8"><div><div className="mb-2 flex items-center gap-2 text-xs font-bold uppercase tracking-[0.16em] text-blue-600"><ShieldCheck className="h-4 w-4" /> 模板配置中心</div><h1 ref={pageHeadingRef} tabIndex={-1} className="text-[28px] font-bold tracking-tight text-slate-950">让模板和 CSS 永远保持正确配对</h1><p className="mt-2 text-sm leading-6 text-slate-500">校验会明确报告正文、有序列表、无序列表、标题、图注与代码块落到的 Word 样式。</p></div><button type="button" onClick={() => { setEditing(null); setEditorOpen(true); }} className="inline-flex items-center gap-2 rounded-lg bg-blue-600 px-5 py-2.5 text-sm font-bold text-white"><Plus className="h-4 w-4" /> 添加模板</button></section>
      {pageError ? <div role="alert" className="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{pageError}</div> : null}
      <div className="grid grid-cols-3 gap-4"><div className="panel flex items-center gap-4 p-4"><FilePlus2 className="h-5 w-5 text-blue-600" /><div><p className="text-2xl font-bold">{templates.length}</p><p className="text-xs text-slate-500">模板配置</p></div></div><div className="panel flex items-center gap-4 p-4"><CheckCircle2 className="h-5 w-5 text-emerald-600" /><div><p className="text-2xl font-bold">{templates.filter((item) => item.validation.status === "valid").length}</p><p className="text-xs text-slate-500">校验通过</p></div></div><div className="panel flex items-center gap-4 p-4"><FileCode2 className="h-5 w-5 text-violet-600" /><div><p className="text-2xl font-bold">{templates.filter((item) => item.css.mode === "custom").length}</p><p className="text-xs text-slate-500">自定义 CSS</p></div></div></div>
      <section className="panel overflow-hidden"><div className="flex items-center justify-between border-b border-slate-100 px-5 py-4"><div className="relative w-80"><Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" /><input aria-label="搜索模板" value={query} onChange={(event) => setQuery(event.target.value)} className="w-full rounded-lg border border-slate-200 bg-slate-50 py-2 pl-9 pr-3 text-xs" placeholder="搜索名称、用途、DOCX 或 CSS" /></div><p className="text-xs text-slate-400">{adapter.runtimeCapabilities.templateStorage === "main" ? "模板副本由桌面 Main 隔离管理" : "演示配置保存在浏览器本地"}</p></div>
        {filtered.length ? <div className="divide-y divide-slate-100">{filtered.map((template) => {
          const ordered = template.validation.styleMappings.find((mapping) => mapping.role === "ordered-list");
          const unordered = template.validation.styleMappings.find((mapping) => mapping.role === "unordered-list");
          return <article key={template.id} className="grid grid-cols-[minmax(220px,1.2fr)_minmax(250px,1fr)_minmax(230px,0.9fr)_140px] items-center gap-4 px-5 py-5 hover:bg-slate-50/70"><div className="flex min-w-0 items-start gap-3.5"><div className="rounded-xl bg-blue-50 p-3 text-blue-600"><FileText className="h-5 w-5" /></div><div className="min-w-0"><div className="flex items-center gap-2"><h2 className="truncate text-sm font-bold">{template.name}</h2>{template.isDefault ? <span className="rounded bg-blue-600 px-1.5 py-0.5 text-[9px] font-bold text-white">默认</span> : null}</div><p className="mt-1 line-clamp-2 text-[11px] text-slate-500">{template.description || "未填写用途说明"}</p></div></div><div className="min-w-0 space-y-1.5 text-[10px]"><p className="truncate"><span className="mr-2 inline-block w-9 text-slate-400">DOCX</span>{template.templateFileName}</p><p className="truncate"><span className="mr-2 inline-block w-9 text-slate-400">CSS</span>{template.css.fileName}</p><p className="truncate"><span className="mr-2 inline-block w-9 text-slate-400">`1.`</span>{ordered?.resolvedStyleName || ordered?.requestedStyleName || "未报告"}</p><p className="truncate"><span className="mr-2 inline-block w-9 text-slate-400">`-`</span>{unordered?.resolvedStyleName || unordered?.requestedStyleName || "未报告"}</p></div><div><StatusBadge state={template.validation.status} /><p className="mt-2 line-clamp-2 text-[10px] text-slate-500">{template.validation.summary}</p></div><div className="flex w-[140px] shrink-0 items-center justify-end gap-1">{!template.isDefault ? <button type="button" disabled={template.validation.status === "invalid" || busyId === template.id} title={template.validation.status === "invalid" ? "校验失败的模板不能设为默认" : "设为默认"} aria-label={`将${template.name}设为默认`} onClick={() => void execute(template.id, async () => { const next = await adapter.templates.setDefault(template.id); onTemplatesChange(next); onDefaultSelected(template.id); })} className="rounded-lg p-2 text-slate-400 disabled:opacity-30"><Star className="h-4 w-4" /></button> : <span aria-hidden="true" className="h-8 w-8 shrink-0" />}<button type="button" disabled={busyId === template.id} title="重新校验" aria-label={`重新校验${template.name}`} onClick={() => void execute(template.id, async () => replaceProfile(await adapter.templates.validate(template.id)))} className="rounded-lg p-2 text-slate-400">{busyId === template.id ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}</button><button type="button" aria-label={`编辑${template.name}`} onClick={() => { setEditing(template); setEditorOpen(true); }} className="rounded-lg p-2 text-slate-400"><Pencil className="h-4 w-4" /></button><button type="button" aria-label={`删除${template.name}`} onClick={() => setDeleting(template)} className="rounded-lg p-2 text-slate-400 hover:text-red-600"><Trash2 className="h-4 w-4" /></button></div></article>;
        })}</div> : <div className="flex min-h-64 flex-col items-center justify-center p-8 text-center"><FilePlus2 className="h-6 w-6 text-slate-400" /><p className="mt-4 text-sm font-bold text-slate-700">{templates.length ? "没有匹配的模板" : "模板库还是空的"}</p>{!templates.length ? <button type="button" onClick={() => setEditorOpen(true)} className="mt-4 text-xs font-bold text-blue-600">添加第一个模板 <ChevronRight className="inline h-3.5 w-3.5" /></button> : null}</div>}
      </section>
      {editorOpen ? <TemplateEditor open current={editing} templates={templates} adapter={adapter} onClose={() => setEditorOpen(false)} onSaved={replaceProfile} onOpenCapability={onOpenCapability} /> : null}
      <Modal open={Boolean(deleting)} title="删除模板配置" description={adapter.runtimeCapabilities.templateStorage === "main" ? "只删除应用管理副本，不删除用户原文件。" : "只删除浏览器中的演示配置。"} onClose={() => setDeleting(null)} footer={<><button type="button" onClick={() => setDeleting(null)} className="rounded-lg border border-slate-300 bg-white px-4 py-2 text-sm font-semibold">取消</button><button type="button" onClick={() => { const target = deleting; if (!target) return; setDeleting(null); void execute(target.id, async () => onTemplatesChange(await adapter.templates.remove(target.id))); }} className="rounded-lg bg-red-600 px-5 py-2 text-sm font-bold text-white">确认删除</button></>}><div className="flex items-start gap-3 rounded-xl border border-red-100 bg-red-50 p-4"><AlertTriangle className="h-5 w-5 text-red-600" /><div><p className="text-sm font-bold text-red-900">{deleting?.name}</p><p className="mt-1 text-xs text-red-700">删除默认模板后只会自动选择第一个校验可用的模板。</p></div></div></Modal>
    </div>
  );
}
