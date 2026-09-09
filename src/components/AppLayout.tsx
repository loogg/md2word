import {
  BookOpenText,
  BookOpenCheck,
  ChevronRight,
  FileOutput,
  Layers3,
  Settings2,
  ShieldCheck,
  Sparkles,
} from "lucide-react";
import type { ReactNode } from "react";
import type { PageId, RuntimeCapabilities } from "../types";

interface AppLayoutProps {
  page: PageId;
  onPageChange: (page: PageId) => void;
  wordEnvironmentReady: boolean;
  capabilities: RuntimeCapabilities;
  children: ReactNode;
}

const navItems: Array<{ id: PageId; label: string; description: string; icon: typeof FileOutput }> = [
  { id: "generate", label: "生成 Word", description: "选择模板并转换", icon: FileOutput },
  { id: "templates", label: "模板管理", description: "管理 DOCX 与 CSS", icon: Layers3 },
  { id: "capabilities", label: "能力说明", description: "语法、元数据与边界", icon: BookOpenCheck },
  { id: "environment", label: "环境与设置", description: "依赖检查与偏好", icon: Settings2 },
];

const pageMeta: Record<PageId, { section: string; title: string }> = {
  generate: { section: "工作台", title: "生成 Word" },
  templates: { section: "资源管理", title: "模板管理" },
  capabilities: { section: "帮助", title: "能力说明" },
  environment: { section: "系统", title: "环境与设置" },
};

export function AppLayout({ page, onPageChange, wordEnvironmentReady, capabilities, children }: AppLayoutProps) {
  const meta = pageMeta[page];
  return (
    <div className="flex h-screen min-h-[720px] bg-[#eef2f7] text-slate-800">
      <aside className="flex w-[254px] shrink-0 flex-col bg-ink-950 text-white shadow-xl">
        <div className="border-b border-white/8 px-6 py-6">
          <div className="flex items-center gap-3">
            <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-blue-600 shadow-lg shadow-blue-950/30">
              <BookOpenText className="h-5 w-5" />
            </div>
            <div>
              <p className="text-[15px] font-bold tracking-wide">MD2Word</p>
              <p className="mt-0.5 text-[11px] text-slate-400">文档生成器 · {capabilities.backend === "electron" ? "桌面版" : "原型"}</p>
            </div>
          </div>
        </div>

        <nav className="flex-1 px-3 py-5" aria-label="主导航">
          <p className="mb-2 px-3 text-[10px] font-bold uppercase tracking-[0.18em] text-slate-500">工作空间</p>
          <div className="space-y-1.5">
            {navItems.map((item) => {
              const Icon = item.icon;
              const active = page === item.id;
              return (
                <button
                  key={item.id}
                  type="button"
                  onClick={() => onPageChange(item.id)}
                  className={`group flex w-full items-center gap-3 rounded-xl px-3 py-3 text-left transition ${
                    active ? "bg-blue-600 text-white shadow-lg shadow-blue-950/25" : "text-slate-300 hover:bg-white/6 hover:text-white"
                  }`}
                >
                  <Icon className={`h-[18px] w-[18px] shrink-0 ${active ? "text-white" : "text-slate-400 group-hover:text-blue-300"}`} />
                  <span className="min-w-0 flex-1">
                    <span className="block text-[13px] font-semibold">{item.label}</span>
                    <span className={`mt-0.5 block truncate text-[10px] ${active ? "text-blue-100" : "text-slate-500"}`}>
                      {item.description}
                    </span>
                  </span>
                  {active ? <ChevronRight className="h-4 w-4 text-blue-100" /> : null}
                </button>
              );
            })}
          </div>
        </nav>

        <div className="mx-3 mb-3 rounded-xl border border-white/8 bg-white/4 p-3.5">
          <div className="flex items-center gap-2 text-[11px] font-semibold text-emerald-300">
            <ShieldCheck className="h-4 w-4" />
            {capabilities.backend === "electron" ? "Renderer 安全隔离" : "原型环境安全隔离"}
          </div>
          <p className="mt-2 text-[10px] leading-4 text-slate-500">{capabilities.backend === "electron" ? "文件路径只保留在 Main 与 Worker。" : "当前不访问真实模板、Word 或本地转换引擎。"}</p>
        </div>
        <div className="flex items-center justify-between border-t border-white/8 px-5 py-3 text-[10px] text-slate-500">
          <span>{capabilities.backend === "electron" ? "desktop" : "prototype"}</span>
          <span className="inline-flex items-center gap-1 text-blue-300"><Sparkles className="h-3 w-3" /> {capabilities.backend === "electron" ? "APP" : "UI"}</span>
        </div>
      </aside>

      <section className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-[70px] shrink-0 items-center justify-between border-b border-slate-200 bg-white/95 px-8 backdrop-blur">
          <div className="flex items-center gap-2 text-sm">
            <span className="text-slate-400">{meta.section}</span>
            <ChevronRight className="h-4 w-4 text-slate-300" />
            <span className="font-semibold text-slate-700">{meta.title}</span>
          </div>
          <div className="flex items-center gap-3">
            <span
              className={`inline-flex items-center gap-2 rounded-full border px-3 py-1.5 text-xs font-semibold ${
                wordEnvironmentReady
                  ? "border-emerald-200 bg-emerald-50 text-emerald-700"
                  : "border-rose-200 bg-rose-50 text-rose-700"
              }`}
            >
              <span
                className={`h-2 w-2 rounded-full ${
                  wordEnvironmentReady
                    ? "bg-emerald-500 shadow-[0_0_0_3px_rgba(16,185,129,0.13)]"
                    : "bg-rose-500 shadow-[0_0_0_3px_rgba(244,63,94,0.13)]"
                }`}
              />
              {wordEnvironmentReady ? "Windows / Word 已检测" : "Word 环境缺失"}
            </span>
            <span className="rounded-full border border-blue-200 bg-blue-50 px-3 py-1.5 text-xs font-semibold text-blue-700">{capabilities.backend === "electron" ? "桌面运行" : "交互原型"}</span>
          </div>
        </header>
        <main className="min-h-0 flex-1 overflow-y-auto px-8 py-7 app-scrollbar">
          <div className="mx-auto max-w-[1500px]">{children}</div>
        </main>
      </section>
    </div>
  );
}
