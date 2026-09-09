import { FileText, LoaderCircle, UploadCloud, X } from "lucide-react";
import { useRef, useState, type DragEvent } from "react";
import { errorMessage } from "../lib/errorMessage";
import type { PickedFile, RuntimeCapabilities, SelectedMarkdown } from "../types";

interface FileDropzoneProps {
  value: SelectedMarkdown | null;
  capabilities: RuntimeCapabilities;
  disabled?: boolean;
  onPick: () => Promise<PickedFile | null>;
  onRegister: (file: File) => Promise<PickedFile>;
  onChange: (file: SelectedMarkdown | null) => void;
}

const formatBytes = (bytes: number) => {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
};

const isMarkdown = (fileName: string) => /\.(md|markdown)$/i.test(fileName);

export function FileDropzone({
  value,
  capabilities,
  disabled = false,
  onPick,
  onRegister,
  onChange,
}: FileDropzoneProps) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [dragging, setDragging] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const acceptFile = async (file: File | undefined) => {
    if (!file || disabled) return;
    if (!isMarkdown(file.name)) {
      setError("仅支持 .md 或 .markdown 文件");
      return;
    }
    setBusy(true);
    setError(null);
    try {
      onChange(await onRegister(file));
    } catch (reason) {
      setError(errorMessage(reason, "无法登记此 Markdown 文件，请重试。"));
    } finally {
      setBusy(false);
    }
  };

  const pickFile = async () => {
    if (disabled || busy) return;
    if (capabilities.fileDialogs === "mock") {
      inputRef.current?.click();
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const picked = await onPick();
      if (picked) onChange(picked);
    } catch (reason) {
      setError(errorMessage(reason, "无法选择 Markdown 文件，请重试。"));
    } finally {
      setBusy(false);
    }
  };

  const handleDrop = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    setDragging(false);
    if (event.dataTransfer.files.length !== 1) {
      setError("一次只能拖入一个 Markdown 文件");
      return;
    }
    void acceptFile(event.dataTransfer.files[0]);
  };

  if (value) {
    return (
      <div className="rounded-xl border border-blue-200 bg-blue-50/60 p-4">
        <div className="flex items-start gap-3">
          <div className="rounded-xl bg-white p-2.5 text-blue-600 shadow-sm">
            <FileText className="h-6 w-6" />
          </div>
          <div className="min-w-0 flex-1">
            <p className="truncate text-sm font-bold text-slate-900">{value.fileName}</p>
            <p className="mt-1 text-xs text-slate-500">{formatBytes(value.size)} · 应用将按源目录解析相对图片</p>
            <p className="mt-2 truncate rounded-md bg-white/80 px-2 py-1.5 text-[11px] text-slate-500">
              {capabilities.backend === "electron" ? "已由桌面应用安全登记；Renderer 不持有真实路径" : `本地演示文件 / ${value.fileName}`}
            </p>
          </div>
          <button
            type="button"
            aria-label="移除 Markdown"
            disabled={disabled}
            onClick={() => onChange(null)}
            className="rounded-lg p-1.5 text-slate-400 hover:bg-white hover:text-slate-700 disabled:cursor-not-allowed disabled:opacity-50"
          >
            <X className="h-4 w-4" />
          </button>
        </div>
      </div>
    );
  }

  return (
    <>
      {capabilities.fileDialogs === "mock" ? (
        <input
          aria-label="选择 Markdown 文件"
          ref={inputRef}
          type="file"
          accept=".md,.markdown,text/markdown"
          className="sr-only"
          disabled={disabled || busy}
          onChange={(event) => {
            void acceptFile(event.target.files?.[0]);
            event.target.value = "";
          }}
        />
      ) : null}
      <div
        role="button"
        tabIndex={disabled ? -1 : 0}
        aria-disabled={disabled || busy}
        onClick={() => void pickFile()}
        onKeyDown={(event) => {
          if (event.key === "Enter" || event.key === " ") void pickFile();
        }}
        onDragEnter={(event) => {
          event.preventDefault();
          if (!disabled) setDragging(true);
        }}
        onDragOver={(event) => event.preventDefault()}
        onDragLeave={() => setDragging(false)}
        onDrop={handleDrop}
        className={`group flex min-h-44 flex-col items-center justify-center rounded-xl border-2 border-dashed px-6 text-center transition ${
          disabled ? "cursor-not-allowed border-slate-200 bg-slate-100 text-slate-400" : "cursor-pointer"
        } ${dragging ? "border-blue-500 bg-blue-50" : "border-slate-300 bg-slate-50/70 hover:border-blue-400 hover:bg-blue-50/40"}`}
      >
        <div className="mb-3 rounded-2xl border border-slate-200 bg-white p-3 text-slate-500 shadow-sm transition group-hover:-translate-y-0.5 group-hover:text-blue-600">
          {busy ? <LoaderCircle className="h-7 w-7 animate-spin" /> : <UploadCloud className="h-7 w-7" />}
        </div>
        <p className="text-sm font-bold text-slate-800">{busy ? "正在安全登记文件…" : "拖入 Markdown，或点击选择文件"}</p>
        <p className="mt-1.5 text-xs text-slate-500">支持 .md / .markdown 单文件 · 真实路径不会暴露给页面</p>
      </div>
      {error ? <p role="alert" className="mt-2 text-xs font-medium text-red-600">{error}</p> : null}
    </>
  );
}
