import { Check, ChevronDown, FileOutput, Search, Star } from "lucide-react";
import { useMemo, useRef, useState } from "react";
import type { KeyboardEvent as ReactKeyboardEvent } from "react";
import type { TemplateProfile } from "../types";
import { Modal } from "./Modal";
import { StatusBadge } from "./StatusBadge";

interface TemplateSelectorProps {
  templates: TemplateProfile[];
  selectedTemplateId: string | null;
  disabled?: boolean;
  onSelect: (id: string) => void;
  onManage: () => void;
}

const normalize = (value: string) => value.trim().toLocaleLowerCase("zh-CN");

export function TemplateSelector({
  templates,
  selectedTemplateId,
  disabled = false,
  onSelect,
  onManage,
}: TemplateSelectorProps) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [activeIndex, setActiveIndex] = useState(0);
  const searchInputRef = useRef<HTMLInputElement>(null);
  const optionRefs = useRef<Array<HTMLButtonElement | null>>([]);

  const selectedTemplate = useMemo(
    () => templates.find((template) => template.id === selectedTemplateId) ?? null,
    [selectedTemplateId, templates],
  );

  const filteredTemplates = useMemo(() => {
    const normalizedQuery = normalize(query);
    return templates
      .filter((template) => {
        if (!normalizedQuery) return true;
        return normalize(
          `${template.name} ${template.description} ${template.templateFileName} ${template.css.fileName}`,
        ).includes(normalizedQuery);
      })
      .map((template, index) => ({ template, index }))
      .sort((left, right) => {
        const rank = (template: TemplateProfile) => {
          if (template.id === selectedTemplateId) return 0;
          if (template.isDefault) return 1;
          return 2;
        };
        return rank(left.template) - rank(right.template) || left.index - right.index;
      })
      .map(({ template }) => template);
  }, [query, selectedTemplateId, templates]);

  const openSelector = () => {
    setQuery("");
    setActiveIndex(0);
    setOpen(true);
  };

  const closeSelector = () => {
    setOpen(false);
    setQuery("");
    setActiveIndex(0);
  };

  const focusOption = (index: number) => {
    if (!filteredTemplates.length) return;
    const nextIndex = Math.max(0, Math.min(index, filteredTemplates.length - 1));
    setActiveIndex(nextIndex);
    window.requestAnimationFrame(() => optionRefs.current[nextIndex]?.focus());
  };

  const handleOptionKeyDown = (event: ReactKeyboardEvent<HTMLButtonElement>, index: number) => {
    if (event.key === "ArrowDown") {
      event.preventDefault();
      focusOption(index === filteredTemplates.length - 1 ? 0 : index + 1);
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      focusOption(index === 0 ? filteredTemplates.length - 1 : index - 1);
    } else if (event.key === "Home") {
      event.preventDefault();
      focusOption(0);
    } else if (event.key === "End") {
      event.preventDefault();
      focusOption(filteredTemplates.length - 1);
    }
  };

  const chooseTemplate = (id: string) => {
    onSelect(id);
    closeSelector();
  };

  if (!selectedTemplate) {
    return (
      <div className="p-6">
        <button
          type="button"
          onClick={onManage}
          className="w-full rounded-xl border-2 border-dashed border-slate-300 p-6 text-center text-sm font-semibold text-slate-500 transition hover:border-blue-400 hover:text-blue-700"
        >
          还没有可用模板，前往模板管理添加
        </button>
      </div>
    );
  }

  return (
    <>
      <div className="p-6">
        <div className="flex min-h-[104px] items-center gap-4 rounded-xl border border-slate-200 bg-slate-50/75 p-4">
          <div className="flex h-11 w-11 shrink-0 items-center justify-center rounded-xl bg-blue-600 text-white shadow-sm shadow-blue-200">
            <FileOutput className="h-5 w-5" />
          </div>
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-2">
              <p className="truncate text-sm font-bold text-slate-900">{selectedTemplate.name}</p>
              {selectedTemplate.isDefault ? (
                <span className="inline-flex items-center gap-1 rounded bg-slate-900 px-1.5 py-0.5 text-[9px] font-bold text-white">
                  <Star className="h-2.5 w-2.5 fill-current" /> 默认
                </span>
              ) : null}
              <StatusBadge state={selectedTemplate.validation.status} />
            </div>
            <p className="mt-1 truncate text-[11px] text-slate-500">{selectedTemplate.description}</p>
            <div className="mt-2 flex min-w-0 items-center gap-2 text-[10px] text-slate-500">
              <span className="max-w-[55%] truncate rounded bg-white px-2 py-1 ring-1 ring-slate-200" title={selectedTemplate.templateFileName}>
                {selectedTemplate.templateFileName}
              </span>
              <span className="truncate rounded bg-white px-2 py-1 ring-1 ring-slate-200" title={selectedTemplate.css.fileName}>
                {selectedTemplate.css.mode === "custom" ? "自定义 CSS" : "内置默认 CSS"}
              </span>
            </div>
          </div>
          <button
            type="button"
            aria-haspopup="dialog"
            aria-expanded={open}
            aria-label={`更换模板，当前为${selectedTemplate.name}`}
            disabled={disabled}
            onClick={openSelector}
            className="inline-flex shrink-0 items-center gap-2 rounded-lg border border-blue-200 bg-white px-4 py-2.5 text-xs font-bold text-blue-700 shadow-sm transition hover:border-blue-300 hover:bg-blue-50 disabled:cursor-not-allowed disabled:border-slate-200 disabled:text-slate-400 disabled:shadow-none"
          >
            更换模板 <ChevronDown className="h-3.5 w-3.5" />
          </button>
        </div>
      </div>

      <Modal
        open={open}
        title="更换模板"
        description="搜索并选择一个模板配置；DOCX 与 CSS 会始终一起使用。"
        onClose={closeSelector}
        widthClass="max-w-2xl"
      >
        <div className="space-y-4">
          <div className="relative">
            <Search className="pointer-events-none absolute left-3.5 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
            <input
              ref={searchInputRef}
              data-autofocus="true"
              aria-label="搜索可用模板"
              value={query}
              onChange={(event) => {
                setQuery(event.target.value);
                setActiveIndex(0);
              }}
              onKeyDown={(event) => {
                if (event.key === "ArrowDown" && filteredTemplates.length) {
                  event.preventDefault();
                  focusOption(activeIndex);
                }
              }}
              className="field-control pl-10"
              placeholder="搜索名称、用途、DOCX 或 CSS"
            />
          </div>

          <div className="flex items-center justify-between text-[11px] text-slate-500">
            <span aria-live="polite">找到 {filteredTemplates.length} 个模板</span>
            <span>使用方向键浏览，Enter 选择，Esc 关闭</span>
          </div>

          {filteredTemplates.length ? (
            <div role="listbox" aria-label="模板列表" className="max-h-[360px] space-y-2 overflow-y-auto pr-1 app-scrollbar">
              {filteredTemplates.map((template, index) => {
                const selected = template.id === selectedTemplateId;
                return (
                  <button
                    key={template.id}
                    ref={(node) => { optionRefs.current[index] = node; }}
                    type="button"
                    role="option"
                    aria-selected={selected}
                    tabIndex={index === activeIndex ? 0 : -1}
                    onFocus={() => setActiveIndex(index)}
                    onKeyDown={(event) => handleOptionKeyDown(event, index)}
                    onClick={() => chooseTemplate(template.id)}
                    className={`flex w-full items-center gap-3 rounded-xl border p-3 text-left transition ${
                      selected
                        ? "border-blue-400 bg-blue-50 shadow-[0_0_0_2px_rgba(37,99,235,0.08)]"
                        : "border-slate-200 bg-white hover:border-blue-300 hover:bg-slate-50"
                    }`}
                  >
                    <span className={`flex h-9 w-9 shrink-0 items-center justify-center rounded-lg ${selected ? "bg-blue-600 text-white" : "bg-slate-100 text-slate-500"}`}>
                      {selected ? <Check className="h-4 w-4" /> : <FileOutput className="h-4 w-4" />}
                    </span>
                    <span className="min-w-0 flex-1">
                      <span className="flex items-center gap-2">
                        <span className="truncate text-sm font-bold text-slate-900">{template.name}</span>
                        {template.isDefault ? <span className="shrink-0 rounded bg-slate-900 px-1.5 py-0.5 text-[9px] font-bold text-white">默认</span> : null}
                      </span>
                      <span className="mt-1 flex min-w-0 items-center gap-2 text-[10px] text-slate-500">
                        <span className="max-w-[58%] truncate" title={template.templateFileName}>{template.templateFileName}</span>
                        <span className="text-slate-300">·</span>
                        <span className="truncate" title={template.css.fileName}>{template.css.mode === "custom" ? "自定义 CSS" : "内置默认 CSS"}</span>
                      </span>
                    </span>
                    <StatusBadge state={template.validation.status} />
                  </button>
                );
              })}
            </div>
          ) : (
            <div className="rounded-xl border border-dashed border-slate-300 bg-slate-50 px-5 py-8 text-center">
              <p className="text-sm font-bold text-slate-700">没有匹配的模板</p>
              <p className="mt-1 text-xs text-slate-500">换一个名称、DOCX 或 CSS 关键词试试。</p>
              <button
                type="button"
                onClick={() => {
                  setQuery("");
                  setActiveIndex(0);
                  searchInputRef.current?.focus();
                }}
                className="mt-4 text-xs font-bold text-blue-600 hover:text-blue-800"
              >
                清除搜索
              </button>
            </div>
          )}

          <div className="flex items-center justify-between rounded-xl border border-slate-200 bg-slate-50 px-4 py-3">
            <p className="text-[11px] text-slate-500">添加、编辑和校验模板请前往模板管理。</p>
            <button
              type="button"
              onClick={() => {
                setOpen(false);
                onManage();
              }}
              className="text-xs font-bold text-blue-600 hover:text-blue-800"
            >
              管理模板
            </button>
          </div>
        </div>
      </Modal>
    </>
  );
}
