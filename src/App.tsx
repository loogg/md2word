import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from "react";
import { AppLayout } from "./components/AppLayout";
import { createAppAdapter } from "./lib/appAdapter";
import { conversionTaskReducer, initialConversionTaskState, isActiveConversionStatus } from "./lib/conversionState";
import { EnvironmentPage } from "./pages/EnvironmentPage";
import { CapabilitiesPage } from "./pages/CapabilitiesPage";
import { GeneratePage } from "./pages/GeneratePage";
import { TemplatesPage } from "./pages/TemplatesPage";
import type { AppAdapter, CapabilityManifest, ConversionError, EnvironmentStatus, PageId, PickedOutput, SelectedMarkdown, TemplateProfile } from "./types";

const preferredTemplateId = (templates: TemplateProfile[], currentId?: string | null) => {
  if (currentId && templates.some((template) => template.id === currentId)) return currentId;
  return templates.find((template) => template.isDefault && template.validation.status !== "invalid")?.id
    ?? templates.find((template) => template.validation.status !== "invalid")?.id
    ?? null;
};

const errorFrom = (reason: unknown, fallbackCode: string): ConversionError => {
  const structured = reason && typeof reason === "object" ? reason as Partial<ConversionError> : undefined;
  return {
    code: typeof structured?.code === "string" ? structured.code : fallbackCode,
    message: reason instanceof Error
      ? reason.message
      : typeof structured?.message === "string" ? structured.message : "操作失败，请重试。",
    stage: structured?.stage,
    retryable: typeof structured?.retryable === "boolean" ? structured.retryable : true,
  };
};

export default function App() {
  const adapterRef = useRef<AppAdapter | null>(null);
  if (!adapterRef.current) adapterRef.current = createAppAdapter();
  const adapter = adapterRef.current;
  const seed = adapter.initialState;

  const [page, setPage] = useState<PageId>("generate");
  const [templates, setTemplates] = useState<TemplateProfile[]>(seed?.templates ?? []);
  const [selectedTemplateId, setSelectedTemplateId] = useState<string | null>(() => preferredTemplateId(seed?.templates ?? []));
  const [environment, setEnvironment] = useState<EnvironmentStatus>(seed?.environment ?? { checkedAt: new Date(0).toISOString(), overall: "blocked", items: [] });
  const [capabilityManifest, setCapabilityManifest] = useState<CapabilityManifest | null>(seed?.capabilityManifest ?? null);
  const [capabilityLoading, setCapabilityLoading] = useState(!seed);
  const [capabilityError, setCapabilityError] = useState<string | null>(null);
  const [capabilityFocusId, setCapabilityFocusId] = useState<string | null>(null);
  const [markdown, setMarkdown] = useState<SelectedMarkdown | null>(null);
  const [task, dispatchTask] = useReducer(conversionTaskReducer, initialConversionTaskState);
  const pendingCancelRef = useRef(false);

  useEffect(() => {
    if (adapter.initialState) return undefined;
    let active = true;
    void Promise.all([adapter.templates.list(), adapter.environment.check()]).then(([nextTemplates, nextEnvironment]) => {
      if (!active) return;
      setTemplates(nextTemplates);
      setSelectedTemplateId((current) => preferredTemplateId(nextTemplates, current));
      setEnvironment(nextEnvironment);
    }).catch(() => {
      // Page-level retry actions surface operational failures; the seed remains usable for the browser mock.
    });
    return () => { active = false; };
  }, [adapter]);

  const loadCapabilities = useCallback(async () => {
    setCapabilityLoading(true);
    setCapabilityError(null);
    try {
      setCapabilityManifest(await adapter.capabilities.describe());
    } catch (reason) {
      setCapabilityError(errorFrom(reason, "CAPABILITY_CATALOG_LOAD_FAILED").message);
    } finally {
      setCapabilityLoading(false);
    }
  }, [adapter]);

  useEffect(() => {
    if (adapter.initialState) return;
    void loadCapabilities();
  }, [adapter, loadCapabilities]);

  useEffect(() => adapter.conversions.onEvent((event) => dispatchTask({ type: "event-received", event })), [adapter]);

  useEffect(() => {
    setSelectedTemplateId((current) => preferredTemplateId(templates, current));
  }, [templates]);

  const selectedTemplate = useMemo(
    () => templates.find((template) => template.id === selectedTemplateId) ?? null,
    [selectedTemplateId, templates],
  );
  const requiredEnvironmentReady = useMemo(
    () => environment.items.length > 0 && environment.items.filter((item) => item.required).every((item) => item.status === "ready"),
    [environment],
  );
  const wordEnvironmentReady = useMemo(
    () => environment.items.filter((item) => item.id === "windows" || item.id === "word").every((item) => item.status === "ready"),
    [environment],
  );

  const startWithOutput = async (output: PickedOutput, demoFailure: boolean) => {
    if (!markdown || !selectedTemplate || selectedTemplate.validation.status === "invalid") {
      dispatchTask({ type: "cancel-output" });
      return;
    }
    if (!requiredEnvironmentReady) {
      dispatchTask({ type: "start-failed", error: { code: "ENVIRONMENT_BLOCKED", message: "Windows、Word、Pandoc 或 C# Worker 尚未就绪。", retryable: true } });
      return;
    }
    adapter.demo?.setNextConversionFailure(demoFailure);
    dispatchTask({ type: "start-requested", output });
    try {
      const accepted = await adapter.conversions.start({
        templateId: selectedTemplate.id,
        sourceHandle: markdown.handle,
        outputHandle: output.handle,
        options: {
          tocDepth: 3,
          mermaidMode: selectedTemplate.mermaidDefaults.mode,
          mermaidFormat: selectedTemplate.mermaidDefaults.format,
        },
      });
      dispatchTask({ type: "job-accepted", jobId: accepted.jobId });
      if (pendingCancelRef.current) {
        pendingCancelRef.current = false;
        await adapter.conversions.cancel(accepted.jobId);
      }
    } catch (reason) {
      pendingCancelRef.current = false;
      dispatchTask({ type: "start-failed", error: errorFrom(reason, "CONVERSION_START_FAILED") });
    }
  };

  const requestOutput = async (demoFailure: boolean) => {
    if (!markdown || !selectedTemplate || isActiveConversionStatus(task.status)) return;
    dispatchTask({ type: "begin-output", demoFailure });
    if (adapter.runtimeCapabilities.fileDialogs === "mock") return;
    try {
      const stem = markdown.fileName.replace(/\.(md|markdown)$/i, "");
      const output = await adapter.files.pickOutput(`${stem}.docx`);
      if (!output) {
        dispatchTask({ type: "cancel-output" });
        return;
      }
      await startWithOutput(output, false);
    } catch (reason) {
      dispatchTask({ type: "start-failed", error: errorFrom(reason, "OUTPUT_SELECTION_FAILED") });
    }
  };

  const confirmDemoOutput = async (directoryLabel: string, fileName: string) => {
    if (!adapter.demo) return;
    try {
      const output = await adapter.demo.createOutput(directoryLabel, fileName);
      await startWithOutput(output, task.demoFailure);
    } catch (reason) {
      dispatchTask({ type: "start-failed", error: errorFrom(reason, "OUTPUT_SELECTION_FAILED") });
    }
  };

  const cancelConversion = async () => {
    if (task.status !== "queued" && task.status !== "running") return;
    dispatchTask({ type: "cancel-requested" });
    if (!task.jobId) {
      pendingCancelRef.current = true;
      return;
    }
    try {
      await adapter.conversions.cancel(task.jobId);
    } catch (reason) {
      dispatchTask({ type: "start-failed", error: errorFrom(reason, "CANCEL_REQUEST_FAILED") });
    }
  };

  const changeMarkdown = (file: SelectedMarkdown | null) => {
    if (isActiveConversionStatus(task.status)) return;
    pendingCancelRef.current = false;
    setMarkdown(file);
    dispatchTask({ type: "reset" });
  };

  const changeTemplate = (id: string) => {
    if (isActiveConversionStatus(task.status)) return;
    pendingCancelRef.current = false;
    setSelectedTemplateId(id);
    dispatchTask({ type: "reset" });
  };

  const changePage = (nextPage: PageId) => {
    if (nextPage !== "capabilities") setCapabilityFocusId(null);
    setPage(nextPage);
  };

  const openCapability = (id: string) => {
    setCapabilityFocusId(id);
    setPage("capabilities");
  };

  return (
    <AppLayout page={page} onPageChange={changePage} wordEnvironmentReady={wordEnvironmentReady} capabilities={adapter.runtimeCapabilities}>
      {page === "generate" ? (
        <GeneratePage
          templates={templates}
          selectedTemplateId={selectedTemplateId}
          onTemplateChange={changeTemplate}
          environment={environment}
          capabilities={adapter.runtimeCapabilities}
          markdown={markdown}
          task={task}
          onMarkdownChange={changeMarkdown}
          onPickMarkdown={() => adapter.files.pickMarkdown()}
          onRegisterMarkdown={(file) => adapter.files.registerMarkdown(file)}
          onRequestOutput={requestOutput}
          onCancelOutput={() => dispatchTask({ type: "cancel-output" })}
          onConfirmDemoOutput={confirmDemoOutput}
          onCancelConversion={cancelConversion}
          onOpenOutput={() => task.jobId ? adapter.shell.openOutput(task.jobId) : Promise.reject(new Error("任务结果已失效。"))}
          onRevealOutput={() => task.jobId ? adapter.shell.revealOutput(task.jobId) : Promise.reject(new Error("任务结果已失效。"))}
          onNavigateTemplates={() => changePage("templates")}
        />
      ) : null}
      {page === "templates" ? (
        <TemplatesPage templates={templates} adapter={adapter} onTemplatesChange={setTemplates} onDefaultSelected={setSelectedTemplateId} onOpenCapability={openCapability} />
      ) : null}
      {page === "capabilities" ? (
        <CapabilitiesPage
          manifest={capabilityManifest}
          loading={capabilityLoading}
          error={capabilityError}
          focusId={capabilityFocusId}
          onRetry={() => void loadCapabilities()}
        />
      ) : null}
      {page === "environment" ? (
        <EnvironmentPage
          environment={environment}
          adapter={adapter}
          onEnvironmentChange={setEnvironment}
          onReset={({ templates: restoredTemplates, environment: restoredEnvironment }) => {
            setTemplates(restoredTemplates);
            setEnvironment(restoredEnvironment);
            setSelectedTemplateId(preferredTemplateId(restoredTemplates));
            setMarkdown(null);
            pendingCancelRef.current = false;
            dispatchTask({ type: "reset" });
          }}
        />
      ) : null}
    </AppLayout>
  );
}
