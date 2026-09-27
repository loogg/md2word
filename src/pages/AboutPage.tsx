import { ArrowDownToLine, ArrowUpRight, CheckCircle2, CircleHelp, Info, LoaderCircle, RefreshCw } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import packageInfo from "../../package.json";
import { errorMessage } from "../lib/errorMessage";
import type { AppAdapter, UpdateCheckResult } from "../types";

interface AboutPageProps {
  adapter: AppAdapter;
}

export function AboutPage({ adapter }: AboutPageProps) {
  const headingRef = useRef<HTMLHeadingElement>(null);
  const [checking, setChecking] = useState(false);
  const [result, setResult] = useState<UpdateCheckResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [linkError, setLinkError] = useState<string | null>(null);
  const isDemo = adapter.runtimeCapabilities.backend === "browser-mock";

  useEffect(() => { headingRef.current?.focus(); }, []);

  const check = async () => {
    setChecking(true);
    setError(null);
    setResult(null);
    try {
      setResult(await adapter.updates.check());
    } catch (reason) {
      setError(errorMessage(reason, "检查更新失败，请稍后重试。"));
    } finally {
      setChecking(false);
    }
  };

  const open = async (action: () => Promise<void>) => {
    setLinkError(null);
    try {
      await action();
    } catch (reason) {
      setLinkError(errorMessage(reason, "无法打开浏览器，请重试。"));
    }
  };

  return (
    <div className="page-enter max-w-[1000px] space-y-6">
      <section className="space-y-1">
        <h1 ref={headingRef} tabIndex={-1} className="text-[28px] font-semibold tracking-tight text-slate-950">关于</h1>
        <p className="text-sm text-slate-500">查看软件版本与 GitHub 更新</p>
      </section>

      <section className="panel overflow-hidden p-6" aria-label="软件版本与更新">
        <div className="flex flex-wrap items-start justify-between gap-5">
          <div className="flex items-start gap-4">
            <div className="flex h-11 w-11 shrink-0 items-center justify-center rounded-[10px] bg-blue-50 text-blue-700"><Info className="h-6 w-6" /></div>
            <div>
              <h2 className="text-xl font-semibold text-slate-950">MD2Word 文档生成器</h2>
              <p className="mt-1 text-xs text-slate-500">Windows 桌面版 · Markdown 转 Word</p>
            </div>
          </div>
          <div className="flex flex-wrap gap-2">
            <button type="button" onClick={() => void open(() => adapter.updates.openRepository())} className="fluent-button"><ArrowUpRight className="h-4 w-4" /> GitHub</button>
            <button type="button" onClick={() => void open(() => adapter.updates.openReleases())} className="fluent-button">更新日志</button>
            <button type="button" onClick={() => void check()} disabled={checking} className="fluent-button fluent-button-primary">
              {checking ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
              {checking ? "正在检查" : "检查更新"}
            </button>
          </div>
        </div>

        <div className="mt-5 flex flex-wrap items-center gap-3 text-xs">
          <span className="inline-flex items-center gap-2 rounded-full border border-slate-200 bg-slate-50 px-3 py-1.5 text-slate-600">当前版本 <strong className="font-semibold text-slate-900">v{packageInfo.version}</strong></span>
          <span className="text-slate-500">运行形式：{isDemo ? "浏览器交互预览（模拟更新检查）" : "Windows 桌面应用"}</span>
        </div>

        <div className="mt-5 rounded-[10px] bg-[#e8f1fb] px-4 py-3 text-sm text-[#174b78]" role="status" aria-live="polite">
          {checking ? (isDemo ? "演示：正在检查版本…" : "正在从 GitHub Releases 获取最新正式版本…") : error ? error : result?.status === "available"
            ? `${isDemo ? "演示：" : ""}发现新版本 v${result.latestVersion}。查看更新日志后，可打开 GitHub Release 下载并手动安装。`
            : result?.status === "up-to-date" ? <span className="inline-flex items-center gap-2"><CheckCircle2 className="h-4 w-4" /> 已是最新正式版本（v{result.latestVersion}）。</span>
              : `点击“检查更新”${isDemo ? "演示" : "从 GitHub"}获取最新正式版本。`}
        </div>

        {result?.status === "available" ? (
          <div className="mt-4 flex flex-wrap items-center justify-between gap-3">
            <p className="text-xs text-slate-500">{isDemo ? "演示结果，不代表 GitHub 当前真实版本。" : "下载前请在 Release 页面确认版本与适用的安装包。"}</p>
            <button type="button" onClick={() => void open(() => adapter.updates.openLatestRelease())} className="fluent-button fluent-button-primary"><ArrowDownToLine className="h-4 w-4" /> 前往下载新版</button>
          </div>
        ) : null}
        {result?.releaseNotes ? <details className="mt-4 rounded-[10px] border border-slate-200 bg-slate-50 px-4 py-3"><summary className="cursor-pointer text-sm font-medium text-slate-700">版本说明摘要</summary><p className="mt-3 max-h-48 overflow-y-auto whitespace-pre-wrap break-words text-xs leading-5 text-slate-600">{result.releaseNotes}</p></details> : null}
        {linkError ? <p role="alert" className="mt-3 text-xs text-red-700">{linkError}</p> : null}
        <p className="mt-5 text-xs text-slate-500">更新来源：loogg/md2word · GitHub Releases</p>
      </section>

      <section className="panel flex items-start gap-3 p-5">
        <CircleHelp className="mt-0.5 h-5 w-5 shrink-0 text-slate-500" />
        <div><h2 className="text-sm font-semibold text-slate-900">升级方式</h2><p className="mt-1 text-xs leading-6 text-slate-600">Setup 安装版下载新版安装包并覆盖安装；便携版下载新版压缩包后解压到新目录。升级前先退出 MD2Word，并保留自己的模板库。此页面只检查版本，不会自动下载、安装或修改用户文档。</p></div>
      </section>
    </div>
  );
}
