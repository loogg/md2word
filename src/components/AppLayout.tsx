import { BookOpenCheck, BookOpenText, ChevronRight, FileOutput, Info, Layers3, Settings2 } from "lucide-react";
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
  about: { section: "系统", title: "关于" },
};

function NavButton({ item, active, onClick }: { item: typeof navItems[number]; active: boolean; onClick: () => void }) {
  const Icon = item.icon;
  return (
    <button type="button" aria-label={`${item.label} ${item.description}`} aria-current={active ? "page" : undefined} onClick={onClick}
      className={`fluent-nav-item group flex w-full items-center gap-3 rounded-lg px-3 py-2.5 text-left ${active ? "fluent-nav-active" : ""}`}>
      <Icon className="h-[18px] w-[18px] shrink-0" />
      <span className="min-w-0 flex-1"><span className="block text-[13px] font-semibold">{item.label}</span><span className="mt-0.5 block truncate text-[10px] text-slate-500">{item.description}</span></span>
    </button>
  );
}

export function AppLayout({ page, onPageChange, wordEnvironmentReady, capabilities, children }: AppLayoutProps) {
  const meta = pageMeta[page];
  const desktop = capabilities.backend === "electron";
  return (
    <div className="flex h-screen min-h-[720px] bg-[#f5f5f7] text-slate-800">
      <aside className="flex w-[238px] shrink-0 flex-col border-r border-[#e0e3e8] bg-[#f9fafb]">
        <div className="px-5 pb-5 pt-6">
          <div className="flex items-center gap-3">
            <div className="flex h-9 w-9 items-center justify-center rounded-[9px] bg-[#0067c0] text-white"><BookOpenText className="h-5 w-5" /></div>
            <div><p className="text-[14px] font-semibold tracking-tight text-slate-950">MD2Word</p><p className="text-[11px] text-slate-500">文档生成器 · {desktop ? "桌面版" : "原型"}</p></div>
          </div>
        </div>
        <nav className="flex-1 px-3" aria-label="主导航">
          <p className="mb-2 px-3 text-[11px] font-medium text-slate-500">工作空间</p>
          <div className="space-y-1">{navItems.map((item) => <NavButton key={item.id} item={item} active={page === item.id} onClick={() => onPageChange(item.id)} />)}</div>
        </nav>
        <div className="px-3 pb-3">
          <div className="my-2 border-t border-[#e2e5ea]" />
          <NavButton item={{ id: "about", label: "关于", description: "版本与软件更新", icon: Info }} active={page === "about"} onClick={() => onPageChange("about")} />
          <p className="mt-3 px-3 text-[10px] leading-4 text-slate-500">{desktop ? "文件路径仅由桌面 Main 与 Worker 处理。" : "交互原型：文件、环境与转换均为模拟。"}</p>
        </div>
      </aside>

      <section className="flex min-w-0 flex-1 flex-col">
        <header className="flex h-[62px] shrink-0 items-center justify-between border-b border-[#e5e7eb] bg-[#fbfbfc] px-7">
          <div className="flex items-center gap-2 text-xs"><span className="text-slate-500">{meta.section}</span><ChevronRight className="h-3.5 w-3.5 text-slate-400" /><span className="font-semibold text-slate-800">{meta.title}</span></div>
          <div className="flex items-center gap-2.5 text-[11px]">
            <span className={`inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 ${wordEnvironmentReady ? "bg-emerald-50 text-emerald-800" : "bg-rose-50 text-rose-800"}`}>
              <span className={`h-1.5 w-1.5 rounded-full ${wordEnvironmentReady ? "bg-emerald-600" : "bg-rose-600"}`} />
              {wordEnvironmentReady ? "Windows / Word 已检测" : "Word 环境缺失"}
            </span>
            <span className="rounded-full bg-[#e8f1fb] px-2.5 py-1 font-medium text-[#005a9e]">{desktop ? "桌面运行" : "交互原型"}</span>
          </div>
        </header>
        <main className="min-h-0 flex-1 overflow-y-auto px-7 py-6 app-scrollbar"><div className="mx-auto max-w-[1500px]">{children}</div></main>
      </section>
    </div>
  );
}
