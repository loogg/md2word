import type { IpcMainInvokeEvent, WebContents } from "electron";
import type { CapabilityManifest, EnvironmentStatus, TemplateProfile, TemplateValidationReport } from "./contracts";
import type { ConversionCoordinator } from "./conversion-coordinator";
import type { EnvironmentService } from "./environment-service";
import { AppError, toPublicError } from "./errors";
import type { FileDialogService } from "./file-dialog-service";
import type { HandleRegistry, JobResultRegistry } from "./handle-registry";
import { IPC_CHANNELS } from "./ipc-channels";
import type { TemplateStore } from "./template-store";
import type { TemplateDraftValidationService } from "./template-validation-service";
import {
  assertId,
  parseAddTemplateInput,
  parseStartConversionInput,
  parseUpdateTemplateInput,
  parseValidateTemplateInput,
} from "./validation";

export interface IpcMainPort {
  handle(channel: string, listener: (event: IpcMainInvokeEvent, ...args: unknown[]) => unknown): void;
  removeHandler(channel: string): void;
}

export interface NativeShellPort {
  openPath(absolutePath: string): Promise<string>;
  showItemInFolder(absolutePath: string): void;
}

export interface RegisterIpcOptions {
  ipcMain: IpcMainPort;
  getTrustedWebContents: () => WebContents | undefined;
  handles: HandleRegistry;
  results: JobResultRegistry;
  dialogs: FileDialogService;
  templates: TemplateStore;
  draftValidation: TemplateDraftValidationService;
  conversions: ConversionCoordinator;
  environment: EnvironmentService;
  describeCapabilities: () => Promise<CapabilityManifest>;
  shell: NativeShellPort;
}

type Handler = (ownerId: number, ...args: unknown[]) => unknown;

export function registerIpcHandlers(options: RegisterIpcOptions): () => void {
  const registered: string[] = [];
  const register = (channel: string, handler: Handler) => {
    options.ipcMain.handle(channel, async (event, ...args) => {
      try {
        const trusted = options.getTrustedWebContents();
        if (!trusted || event.sender !== trusted || event.senderFrame !== trusted.mainFrame) {
          throw new AppError("IPC_FORBIDDEN", "拒绝来自非受信页面的请求。");
        }
        return { ok: true as const, value: await handler(event.sender.id, ...args) };
      } catch (error) {
        return { ok: false as const, error: toPublicError(error) };
      }
    });
    registered.push(channel);
  };

  register(IPC_CHANNELS.templatesList, () => options.templates.list());
  register(IPC_CHANNELS.templatesAdd, async (ownerId, value) => {
    const input = parseAddTemplateInput(value);
    const approvedWarningFingerprint = options.draftValidation.consumeWarningApproval(ownerId, input);
    return options.templates.add({
      name: input.name,
      description: input.description,
      templateSourcePath: options.handles.resolve(input.templateFile.handle, "template-docx", ownerId),
      css:
        input.css.mode === "builtin"
          ? { mode: "builtin" }
          : { mode: "custom", sourcePath: options.handles.resolve(input.css.file!.handle, "css", ownerId) },
      mermaidDefaults: input.mermaidDefaults,
      approvedWarningFingerprint,
    });
  });
  register(IPC_CHANNELS.templatesUpdate, async (ownerId, idValue, value) => {
    const id = assertId(idValue, "模板 ID");
    const input = parseUpdateTemplateInput(value);
    const approvedWarningFingerprint = options.draftValidation.consumeWarningApproval(ownerId, { ...input, templateId: id });
    return options.templates.update({
      id,
      name: input.name,
      description: input.description,
      templateSourcePath: input.templateFile
        ? options.handles.resolve(input.templateFile.handle, "template-docx", ownerId)
        : undefined,
      css:
        input.css.mode === "builtin"
          ? { mode: "builtin" }
          : {
              mode: "custom",
              sourcePath: input.css.file ? options.handles.resolve(input.css.file.handle, "css", ownerId) : undefined,
            },
      mermaidDefaults: input.mermaidDefaults,
      approvedWarningFingerprint,
    });
  });
  register(IPC_CHANNELS.templatesRemove, async (_ownerId, idValue): Promise<TemplateProfile[]> => {
    await options.templates.remove(assertId(idValue, "模板 ID"));
    return options.templates.list();
  });
  register(IPC_CHANNELS.templatesSetDefault, (_ownerId, idValue) =>
    options.templates.setDefault(assertId(idValue, "模板 ID")),
  );
  register(IPC_CHANNELS.templatesValidate, (_ownerId, idValue): Promise<TemplateProfile> =>
    options.templates.validate(assertId(idValue, "模板 ID")),
  );
  register(IPC_CHANNELS.templatesValidateDraft, (ownerId, value): Promise<TemplateValidationReport> =>
    options.draftValidation.validate(ownerId, parseValidateTemplateInput(value)),
  );

  register(IPC_CHANNELS.filesPickMarkdown, (ownerId) => options.dialogs.pickMarkdown(ownerId));
  register(IPC_CHANNELS.filesRegisterMarkdownPath, (ownerId, value) => {
    if (typeof value !== "string" || value.length > 32_768) throw new AppError("INVALID_FILE_SELECTION", "拖入文件无效。");
    return options.dialogs.registerMarkdown(ownerId, value);
  });
  register(IPC_CHANNELS.filesPickTemplateDocx, (ownerId) => options.dialogs.pickTemplateDocx(ownerId));
  register(IPC_CHANNELS.filesPickCss, (ownerId) => options.dialogs.pickCss(ownerId));
  register(IPC_CHANNELS.filesPickOutput, (ownerId, suggestedName) => options.dialogs.pickOutput(ownerId, suggestedName));

  register(IPC_CHANNELS.conversionsStart, (ownerId, value) =>
    options.conversions.start(ownerId, parseStartConversionInput(value)),
  );
  register(IPC_CHANNELS.conversionsCancel, (ownerId, value) => {
    options.conversions.cancel(ownerId, assertId(value, "任务 ID"));
  });
  register(IPC_CHANNELS.environmentCheck, (): Promise<EnvironmentStatus> => options.environment.check());
  register(IPC_CHANNELS.capabilitiesDescribe, (): Promise<CapabilityManifest> => options.describeCapabilities());

  register(IPC_CHANNELS.shellOpenOutput, async (ownerId, value) => {
    const outputPath = options.results.resolve(assertId(value, "任务 ID"), ownerId);
    const error = await options.shell.openPath(outputPath);
    if (error) throw new AppError("OPEN_OUTPUT_FAILED", "无法打开输出文件。", true);
  });
  register(IPC_CHANNELS.shellRevealOutput, (ownerId, value) => {
    options.shell.showItemInFolder(options.results.resolve(assertId(value, "任务 ID"), ownerId));
  });
  register(IPC_CHANNELS.shellOpenTemplateLibrary, async () => {
    const error = await options.shell.openPath(options.templates.rootPath);
    if (error) throw new AppError("OPEN_TEMPLATE_LIBRARY_FAILED", "无法打开模板库目录。", true);
  });

  return () => {
    for (const channel of registered) options.ipcMain.removeHandler(channel);
  };
}
