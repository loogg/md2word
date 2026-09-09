import {
  AlertTriangle,
  BookOpenCheck,
  Braces,
  CheckCircle2,
  FileCode2,
  LayoutTemplate,
  RefreshCw,
  Search,
  ShieldAlert,
  SlidersHorizontal,
} from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import type {
  CapabilityFeature,
  CapabilityManifest,
  CapabilityStatus,
  FrontMatterCapability,
} from "../types";

type CapabilityTab = "features" | "metadata" | "template" | "limits";

interface CapabilitiesPageProps {
  manifest: CapabilityManifest | null;
  loading: boolean;
  error: string | null;
  focusId: string | null;
  onRetry: () => void;
}

const statusMeta: Record<CapabilityStatus, { label: string; className: string }> = {
  supported: { label: "支持", className: "border-emerald-200 bg-emerald-50 text-emerald-700" },
  conditional: { label: "条件支持", className: "border-blue-200 bg-blue-50 text-blue-700" },
  limited: { label: "有限支持", className: "border-amber-200 bg-amber-50 text-amber-700" },
  unsupported: { label: "未承诺", className: "border-slate-300 bg-slate-100 text-slate-600" },
};

const tabs: Array<{ id: CapabilityTab; label: string; description: string; icon: typeof Braces }> = [
  { id: "features", label: "语法与转换", description: "Markdown、Word 收口", icon: Braces },
  { id: "metadata", label: "Front Matter", description: "键、默认值与示例", icon: SlidersHorizontal },
  { id: "template", label: "模板契约", description: "书签与 CSS 角色", icon: LayoutTemplate },
  { id: "limits", label: "边界与环境", description: "限制与运行依赖", icon: ShieldAlert },
];

function StatusPill({ status }: { status: CapabilityStatus }) {
  const meta = statusMeta[status];
  return <span className={`shrink-0 rounded-full border px-2.5 py-1 text-[10px] font-bold ${meta.className}`}>{meta.label}</span>;
}

const normalize = (value: string) => value.trim().toLocaleLowerCase("zh-CN");

function includesQuery(values: Array<string | undefined>, query: string) {
  if (!query) return true;
  return values.some((value) => normalize(value ?? "").includes(query));
}

function FeatureCard({
  feature,
  register,
  highlighted,
}: {
  feature: CapabilityFeature;
  register: (id: string, node: HTMLElement | null) => void;
  highlighted: boolean;
}) {
  return (
    <article
      ref={(node) => register(feature.id, node)}
      data-capability-id={feature.id}
      tabIndex={-1}
      className={`rounded-xl border bg-white p-5 outline-none transition ${
        highlighted ? "border-blue-400 ring-4 ring-blue-100" : "border-slate-200 hover:border-blue-200"
      }`}
    >
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0">
          <p className="font-mono text-[10px] font-semibold text-slate-400">{feature.id}</p>
          <h3 className="mt-1 text-[15px] font-bold text-slate-900">{feature.title}</h3>
        </div>
        <StatusPill status={feature.status} />
      </div>
      <p className="mt-3 text-xs leading-5 text-slate-600">{feature.summary}</p>
      {feature.syntax.length ? (
        <div className="mt-3 rounded-lg bg-slate-950 px-3 py-2.5 font-mono text-[10px] leading-4 text-slate-200">
          {feature.syntax.map((line, index) => <div key={`${line}-${index}`}>{line}</div>)}
        </div>
      ) : null}
      {feature.details.length ? (
        <ul className="mt-3 space-y-1.5 text-[11px] leading-4 text-slate-500">
          {feature.details.map((detail) => <li key={detail} className="flex gap-2"><span className="text-blue-500">•</span><span>{detail}</span></li>)}
        </ul>
      ) : null}
      {feature.relatedMetadata.length ? (
        <div className="mt-3 flex flex-wrap gap-1.5">
          {feature.relatedMetadata.map((key) => <code key={key} className="rounded bg-violet-50 px-2 py-1 text-[9px] font-semibold text-violet-700">{key}</code>)}
        </div>
      ) : null}
    </article>
  );
}

function MetadataCard({
  item,
  register,
  highlighted,
}: {
  item: FrontMatterCapability;
  register: (id: string, node: HTMLElement | null) => void;
  highlighted: boolean;
}) {
  return (
    <article
      ref={(node) => register(item.id, node)}
      data-capability-id={item.id}
      tabIndex={-1}
      className={`rounded-xl border bg-white p-5 outline-none ${highlighted ? "border-blue-400 ring-4 ring-blue-100" : "border-slate-200"}`}
    >
      <div className="flex items-start justify-between gap-4">
        <div>
          <code className="text-sm font-bold text-blue-700">{item.key}</code>
          <p className="mt-1 font-mono text-[9px] text-slate-400">{item.id}</p>
        </div>
        <StatusPill status={item.status} />
      </div>
      <p className="mt-3 text-xs leading-5 text-slate-600">{item.description}</p>
      <dl className="mt-4 grid grid-cols-[74px_1fr] gap-x-3 gap-y-2 border-t border-slate-100 pt-3 text-[10px]">
        <dt className="font-bold text-slate-400">类型</dt><dd className="font-mono text-slate-700">{item.type}</dd>
        <dt className="font-bold text-slate-400">默认行为</dt><dd className="text-slate-700">{item.defaultValue}</dd>
        {item.allowedValues.length ? <><dt className="font-bold text-slate-400">可选值</dt><dd className="font-mono text-slate-700">{item.allowedValues.join(" / ")}</dd></> : null}
        {item.requires.length ? <><dt className="font-bold text-slate-400">模板要求</dt><dd className="font-mono text-slate-700">{item.requires.join("、")}</dd></> : null}
      </dl>
      <pre className="mt-4 overflow-x-auto whitespace-pre-wrap rounded-lg bg-slate-950 p-3 text-[10px] leading-4 text-slate-200">{item.example}</pre>
    </article>
  );
}

export function CapabilitiesPage({ manifest, loading, error, focusId, onRetry }: CapabilitiesPageProps) {
  const headingRef = useRef<HTMLHeadingElement>(null);
  const nodesRef = useRef(new Map<string, HTMLElement>());
  const [tab, setTab] = useState<CapabilityTab>("features");
  const [query, setQuery] = useState("");
  const [highlightedId, setHighlightedId] = useState<string | null>(focusId);

  const registerNode = (id: string, node: HTMLElement | null) => {
    if (node) nodesRef.current.set(id, node);
    else nodesRef.current.delete(id);
  };

  useEffect(() => { headingRef.current?.focus(); }, []);

  useEffect(() => {
    if (!focusId || !manifest) return;
    const metadataMatch = manifest.frontMatter.some((item) => item.id === focusId);
    const limitMatch = manifest.limitations.some((item) => item.id === focusId);
    setTab(metadataMatch ? "metadata" : limitMatch ? "limits" : "features");
    setQuery("");
    setHighlightedId(focusId);
  }, [focusId, manifest]);

  useEffect(() => {
    if (!highlightedId) return;
    const frame = requestAnimationFrame(() => {
      const node = nodesRef.current.get(highlightedId);
      node?.scrollIntoView({ behavior: "smooth", block: "center" });
      node?.focus({ preventScroll: true });
    });
    return () => cancelAnimationFrame(frame);
  }, [highlightedId, tab, manifest]);

  const normalizedQuery = normalize(query);
  const featureGroups = useMemo(() => manifest?.categories.map((category) => ({
    ...category,
    items: category.items.filter((item) => includesQuery(
      [item.id, item.title, item.summary, ...item.details, ...item.syntax, ...item.relatedMetadata],
      normalizedQuery,
    )),
  })).filter((category) => category.items.length) ?? [], [manifest, normalizedQuery]);
  const metadata = useMemo(() => manifest?.frontMatter.filter((item) => includesQuery(
    [item.id, item.key, item.type, item.description, item.defaultValue, item.example, ...item.allowedValues, ...item.requires],
    normalizedQuery,
  )) ?? [], [manifest, normalizedQuery]);
  const featureCount = manifest?.categories.reduce((total, category) => total + category.items.length, 0) ?? 0;
  const supportedCount = manifest?.categories.reduce(
    (total, category) => total + category.items.filter((item) => item.status === "supported").length,
    0,
  ) ?? 0;

  if (!manifest) {
    return (
      <div className="page-enter">
        <section className="panel flex min-h-80 flex-col items-center justify-center p-8 text-center">
          {loading ? <RefreshCw className="h-7 w-7 animate-spin text-blue-600" /> : <AlertTriangle className="h-7 w-7 text-rose-600" />}
          <h1 ref={headingRef} tabIndex={-1} className="mt-4 text-xl font-bold text-slate-900">{loading ? "正在读取能力目录" : "能力目录暂时不可用"}</h1>
          <p className="mt-2 max-w-xl text-sm text-slate-500">{error ?? "桌面版会从当前打包的 Worker 读取能力清单。"}</p>
          {!loading ? <button type="button" onClick={onRetry} className="mt-5 rounded-lg bg-blue-600 px-4 py-2 text-sm font-bold text-white">重新读取</button> : null}
        </section>
      </div>
    );
  }

  return (
    <div className="page-enter space-y-6">
      <section className="flex items-end justify-between gap-8">
        <div>
          <div className="mb-2 flex items-center gap-2 text-xs font-bold uppercase tracking-[0.16em] text-blue-600"><BookOpenCheck className="h-4 w-4" /> 随版本发布的能力目录</div>
          <h1 ref={headingRef} tabIndex={-1} className="text-[28px] font-bold tracking-tight text-slate-950">{manifest.title}</h1>
          <p className="mt-2 max-w-4xl text-sm leading-6 text-slate-500">{manifest.summary}</p>
        </div>
        <div className="shrink-0 rounded-xl border border-blue-100 bg-blue-50 px-4 py-3 text-right">
          <p className="text-[10px] font-bold uppercase tracking-wider text-blue-500">当前安装版本</p>
          <p className="mt-1 text-lg font-bold text-blue-900">v{manifest.productVersion}</p>
          <p className="text-[9px] text-blue-600">清单 {manifest.schemaVersion} · 协议 {manifest.protocolVersion}</p>
        </div>
      </section>

      <section className="grid grid-cols-4 gap-4">
        <div className="panel p-4"><p className="text-2xl font-bold text-slate-900">{featureCount}</p><p className="mt-1 text-xs text-slate-500">转换能力条目</p></div>
        <div className="panel p-4"><p className="text-2xl font-bold text-emerald-700">{supportedCount}</p><p className="mt-1 text-xs text-slate-500">已实现能力</p></div>
        <div className="panel p-4"><p className="text-2xl font-bold text-violet-700">{manifest.frontMatter.length}</p><p className="mt-1 text-xs text-slate-500">Front Matter 键</p></div>
        <div className="panel p-4"><p className="text-2xl font-bold text-amber-700">{manifest.limitations.length}</p><p className="mt-1 text-xs text-slate-500">已声明边界</p></div>
      </section>

      <section className="panel overflow-hidden">
        <div className="flex items-center justify-between gap-6 border-b border-slate-100 px-5 py-4">
          <div className="flex gap-1" role="tablist" aria-label="能力说明类别">
            {tabs.map((item) => {
              const Icon = item.icon;
              const active = item.id === tab;
              return <button key={item.id} type="button" role="tab" aria-selected={active} onClick={() => { setTab(item.id); setHighlightedId(null); }} className={`flex items-center gap-2 rounded-lg px-3 py-2 text-left ${active ? "bg-blue-600 text-white" : "text-slate-500 hover:bg-slate-100"}`}><Icon className="h-4 w-4" /><span><span className="block text-xs font-bold">{item.label}</span><span className={`block text-[9px] ${active ? "text-blue-100" : "text-slate-400"}`}>{item.description}</span></span></button>;
            })}
          </div>
          {tab !== "template" && tab !== "limits" ? (
            <label className="relative w-72">
              <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
              <input aria-label="搜索能力说明" value={query} onChange={(event) => { setQuery(event.target.value); setHighlightedId(null); }} className="w-full rounded-lg border border-slate-200 bg-slate-50 py-2 pl-9 pr-3 text-xs" placeholder="搜索名称、ID、语法或元数据" />
            </label>
          ) : null}
        </div>

        <div className="bg-slate-50/60 p-5">
          {tab === "features" ? (
            featureGroups.length ? <div className="space-y-7">{featureGroups.map((category) => <section key={category.id}><div className="mb-3"><h2 className="text-sm font-bold text-slate-900">{category.title}</h2><p className="mt-1 text-[11px] text-slate-500">{category.description}</p></div><div className="grid grid-cols-2 gap-4">{category.items.map((feature) => <FeatureCard key={feature.id} feature={feature} register={registerNode} highlighted={highlightedId === feature.id} />)}</div></section>)}</div> : <p className="py-16 text-center text-sm text-slate-500">没有匹配的能力条目。</p>
          ) : null}

          {tab === "metadata" ? (
            metadata.length ? <div className="grid grid-cols-2 gap-4">{metadata.map((item) => <MetadataCard key={item.id} item={item} register={registerNode} highlighted={highlightedId === item.id} />)}</div> : <p className="py-16 text-center text-sm text-slate-500">没有匹配的 Front Matter 键。</p>
          ) : null}

          {tab === "template" ? (
            <div className="space-y-5">
              <div className="grid grid-cols-2 gap-4">
                <section className="rounded-xl border border-slate-200 bg-white p-5"><h2 className="text-sm font-bold text-slate-900">必需书签</h2><div className="mt-3 space-y-2">{manifest.templateContract.requiredBookmarks.map((bookmark) => <div key={bookmark.name} className="rounded-lg bg-rose-50 p-3"><code className="text-xs font-bold text-rose-700">{bookmark.name}</code><p className="mt-1 text-[10px] leading-4 text-rose-900/70">{bookmark.description}</p></div>)}</div></section>
                <section className="rounded-xl border border-slate-200 bg-white p-5"><h2 className="text-sm font-bold text-slate-900">按需书签</h2><div className="mt-3 space-y-2">{manifest.templateContract.optionalBookmarks.map((bookmark) => <div key={bookmark.name} className="rounded-lg bg-blue-50 p-3"><code className="text-xs font-bold text-blue-700">{bookmark.name}</code><p className="mt-1 text-[10px] leading-4 text-blue-900/70">{bookmark.description}</p></div>)}</div></section>
              </div>
              <section className="rounded-xl border border-slate-200 bg-white p-5"><div className="flex items-center gap-2"><FileCode2 className="h-4 w-4 text-violet-600" /><h2 className="text-sm font-bold text-slate-900">CSS 语义角色</h2></div><div className="mt-4 grid grid-cols-2 gap-3">{manifest.templateContract.cssRoles.map((role) => <div key={role.role} className="rounded-lg border border-slate-100 bg-slate-50 p-3"><div className="flex justify-between gap-3"><span className="text-xs font-bold text-slate-800">{role.label}</span><code className="text-[9px] text-violet-700">{role.role}</code></div><p className="mt-2 font-mono text-[9px] leading-4 text-slate-500">{role.selectors.join(" · ")}</p><p className="mt-1 text-[9px] text-slate-400">回退：{role.fallback}</p></div>)}</div></section>
              <section className="rounded-xl border border-slate-200 bg-white p-5"><h2 className="text-sm font-bold text-slate-900">校验约定</h2><ul className="mt-3 space-y-2 text-[11px] leading-5 text-slate-600">{manifest.templateContract.validationNotes.map((note) => <li key={note} className="flex gap-2"><CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0 text-emerald-600" /><span>{note}</span></li>)}</ul></section>
            </div>
          ) : null}

          {tab === "limits" ? (
            <div className="space-y-5">
              <div className="grid grid-cols-2 gap-4">{manifest.limitations.map((item) => <article key={item.id} ref={(node) => registerNode(item.id, node)} data-capability-id={item.id} tabIndex={-1} className={`rounded-xl border bg-white p-5 outline-none ${highlightedId === item.id ? "border-blue-400 ring-4 ring-blue-100" : "border-slate-200"}`}><div className="flex items-start justify-between gap-3"><div><p className="font-mono text-[9px] text-slate-400">{item.id}</p><h2 className="mt-1 text-sm font-bold text-slate-900">{item.title}</h2></div><StatusPill status={item.status} /></div><p className="mt-3 text-xs leading-5 text-slate-600">{item.summary}</p>{item.details.map((detail) => <p key={detail} className="mt-2 text-[10px] leading-4 text-slate-500">{detail}</p>)}</article>)}</div>
              <section className="rounded-xl border border-slate-200 bg-white p-5"><h2 className="text-sm font-bold text-slate-900">运行环境与固定工具</h2><p className="mt-1 text-xs text-slate-500">{manifest.tooling.platform}</p><div className="mt-4 grid grid-cols-3 gap-4 text-[10px]"><div><p className="font-bold text-slate-700">必需</p><ul className="mt-2 space-y-1 text-slate-500">{manifest.tooling.required.map((item) => <li key={item}>• {item}</li>)}</ul></div><div><p className="font-bold text-slate-700">可选</p><ul className="mt-2 space-y-1 text-slate-500">{manifest.tooling.optional.map((item) => <li key={item}>• {item}</li>)}</ul></div><div><p className="font-bold text-slate-700">固定版本</p><ul className="mt-2 space-y-1 font-mono text-slate-500">{Object.entries(manifest.tooling.pinned).map(([name, value]) => <li key={name}>{name}: {value}</li>)}</ul></div></div></section>
            </div>
          ) : null}
        </div>
      </section>
    </div>
  );
}
