export type PageId = "generate" | "templates" | "capabilities" | "environment";

export type ValidationStatus = "valid" | "warning" | "invalid";
export type IssueSeverity = "info" | "warning" | "error";
export type StyleMode = "builtin" | "custom";

export type WordStyleRole =
  | "body"
  | "ordered-list"
  | "unordered-list"
  | "heading"
  | "caption"
  | "table-caption"
  | "code-block"
  | "inline-code"
  | "table"
  | "admonition"
  | "figure-image";

export type WordStyleMappingStatus = "resolved" | "word-fallback" | "missing" | "ambiguous" | "not-configured";

/** A CSS-to-Word mapping resolved by the template validator. */
export interface WordStyleMapping {
  role: WordStyleRole;
  /** Present only for heading mappings. */
  headingLevel?: 1 | 2 | 3 | 4 | 5 | 6;
  cssSelector: string;
  requestedStyleName: string;
  resolvedStyleId?: string;
  resolvedStyleName?: string;
  status: WordStyleMappingStatus;
  message?: string;
}

export interface TemplateValidationIssue {
  code: string;
  severity: IssueSeverity;
  target: "docx" | "css" | "bookmark" | "style" | "security";
  message: string;
  capabilityId?: string;
}

export interface TemplateCapabilities {
  bodyRange: boolean;
  coverTitle: boolean;
  coverSubtitle: boolean;
  versionTables: string[];
  codeBlockStyle: boolean;
}

export interface TemplateValidationReport {
  status: ValidationStatus;
  checkedAt: string;
  contentFingerprint: string;
  summary: string;
  issues: TemplateValidationIssue[];
  capabilities: TemplateCapabilities;
  styleMappings: WordStyleMapping[];
}

export interface TemplateProfile {
  id: string;
  name: string;
  description: string;
  templateFileName: string;
  css: { mode: StyleMode; fileName: string };
  isDefault: boolean;
  mermaidDefaults: MermaidOptions;
  validation: TemplateValidationReport;
  createdAt: string;
  updatedAt: string;
}

export interface MermaidOptions {
  mode: "auto" | "off" | "required";
  format: "png" | "svg";
}

/** Renderer-safe file metadata. `handle` is opaque and is resolved only by Main. */
export interface PickedFile {
  handle: string;
  fileName: string;
  size: number;
  lastModified: number;
}

/** Renderer-safe output selection. `displayPath` must already be redacted by Main. */
export interface PickedOutput {
  handle: string;
  fileName: string;
  displayPath: string;
}

export type SelectedMarkdown = PickedFile;

export interface TemplateDraft {
  name: string;
  description: string;
  templateFileName: string;
  templateFileHandle: string | null;
  styleMode: StyleMode;
  styleCssFileName: string;
  styleCssHandle: string | null;
  mermaidMode: MermaidOptions["mode"];
  mermaidFormat: MermaidOptions["format"];
}

export interface AddTemplateInput {
  name: string;
  description: string;
  templateFile: PickedFile;
  css: { mode: StyleMode; file?: PickedFile };
  mermaidDefaults: MermaidOptions;
}

export interface UpdateTemplateInput {
  name: string;
  description: string;
  /** Omitted to retain the currently managed DOCX. */
  templateFile?: PickedFile;
  /** A custom file may be omitted only when retaining the current custom CSS. */
  css: { mode: StyleMode; file?: PickedFile };
  mermaidDefaults: MermaidOptions;
}

export interface ValidateTemplateInput extends UpdateTemplateInput {
  /** Omitted for a template that has not been imported yet. */
  templateId?: string;
}

/** Main/Worker-only path-bearing request. Renderer uses StartConversionInput. */
export interface ConversionRequest {
  jobId: string;
  templateId: string;
  sourcePath: string;
  outputPath: string;
  templateSnapshot: {
    docxPath: string;
    cssPath: string;
    validationFingerprint: string;
  };
  tools: { pandocPath: string; npxPath?: string; mermaidBrowserPath?: string };
  options: {
    tocDepth: number;
    mermaidMode: MermaidOptions["mode"];
    mermaidFormat: MermaidOptions["format"];
  };
}

/** Renderer-to-Main conversion request containing no filesystem paths. */
export interface StartConversionInput {
  templateId: string;
  sourceHandle: string;
  outputHandle: string;
  options: {
    tocDepth: number;
    mermaidMode: MermaidOptions["mode"];
    mermaidFormat: MermaidOptions["format"];
  };
}

export type ConversionStage =
  | "queued"
  | "preparing"
  | "metadata"
  | "pandoc"
  | "mermaid"
  | "word-import"
  | "template-assembly"
  | "word-finalize"
  | "openxml-finalize"
  | "cleanup";

export interface ConversionError {
  code: string;
  message: string;
  stage?: ConversionStage;
  retryable: boolean;
}

/** Renderer-safe result. Main retains the real output path and exposes shell actions by jobId. */
export interface ConversionResult {
  jobId: string;
  status: "succeeded" | "failed" | "canceled";
  outputFileName?: string;
  outputDisplayPath?: string;
  diagnosticAvailable?: boolean;
  durationMs: number;
  warnings: string[];
}

export interface ConversionEvent {
  jobId: string;
  timestamp: string;
  kind: "queued" | "started" | "stage" | "log" | "canceling" | "completed" | "failed" | "canceled";
  stage?: ConversionStage;
  level?: "info" | "warning" | "error";
  /** Optional coarse progress supplied by the worker; UI must not imply document-level precision. */
  progress?: number;
  message?: string;
  result?: ConversionResult;
  error?: ConversionError;
}

export type EnvironmentItemStatus = "ready" | "optional-missing" | "blocked" | "error";

export interface EnvironmentItem {
  id: "windows" | "word" | "pandoc" | "worker" | "mermaid";
  name: string;
  version: string;
  detail: string;
  status: EnvironmentItemStatus;
  required: boolean;
}

export interface EnvironmentStatus {
  checkedAt: string;
  overall: "ready" | "degraded" | "blocked";
  items: EnvironmentItem[];
}

export type CapabilityStatus = "supported" | "conditional" | "limited" | "unsupported";

export interface CapabilityFeature {
  id: string;
  title: string;
  status: CapabilityStatus;
  summary: string;
  details: string[];
  syntax: string[];
  relatedMetadata: string[];
}

export interface CapabilityCategory {
  id: string;
  title: string;
  description: string;
  items: CapabilityFeature[];
}

export interface FrontMatterCapability {
  id: string;
  key: string;
  type: string;
  defaultValue: string;
  status: CapabilityStatus;
  description: string;
  example: string;
  allowedValues: string[];
  requires: string[];
}

export interface TemplateBookmarkCapability {
  name: string;
  description: string;
}

export interface TemplateCssRoleCapability {
  role: WordStyleRole;
  label: string;
  selectors: string[];
  fallback: string;
}

export interface CapabilityLimitation {
  id: string;
  title: string;
  status: Extract<CapabilityStatus, "limited" | "unsupported">;
  summary: string;
  details: string[];
}

export interface CapabilityManifest {
  schemaVersion: string;
  productVersion: string;
  protocolVersion: string;
  locale: "zh-CN";
  title: string;
  summary: string;
  categories: CapabilityCategory[];
  frontMatter: FrontMatterCapability[];
  templateContract: {
    requiredBookmarks: TemplateBookmarkCapability[];
    optionalBookmarks: TemplateBookmarkCapability[];
    cssRoles: TemplateCssRoleCapability[];
    validationNotes: string[];
  };
  limitations: CapabilityLimitation[];
  tooling: {
    platform: string;
    required: string[];
    optional: string[];
    pinned: Record<string, string>;
  };
  implemented: string[];
  pendingLegacyParity: string[];
}

export type RuntimeBackend = "browser-mock" | "electron";

export interface RuntimeCapabilities {
  backend: RuntimeBackend;
  fileDialogs: "mock" | "native";
  templateStorage: "local-storage" | "main";
  templateValidation: "mock" | "worker";
  conversion: "mock" | "worker";
  environment: "mock" | "worker";
  shell: "mock" | "native";
}

/** Exact narrow API exposed by Electron preload as `window.md2word`. */
export interface Md2WordApi {
  runtimeCapabilities: RuntimeCapabilities;
  templates: {
    list(): Promise<TemplateProfile[]>;
    add(input: AddTemplateInput): Promise<TemplateProfile>;
    update(id: string, input: UpdateTemplateInput): Promise<TemplateProfile>;
    remove(id: string): Promise<TemplateProfile[]>;
    setDefault(id: string): Promise<TemplateProfile[]>;
    validate(id: string): Promise<TemplateProfile>;
    validateDraft(input: ValidateTemplateInput): Promise<TemplateValidationReport>;
  };
  files: {
    pickMarkdown(): Promise<PickedFile | null>;
    /** Preload resolves an Electron drag File with webUtils.getPathForFile, then registers it in Main. */
    registerMarkdown(file: File): Promise<PickedFile>;
    pickTemplateDocx(): Promise<PickedFile | null>;
    pickCss(): Promise<PickedFile | null>;
    pickOutput(suggestedName: string): Promise<PickedOutput | null>;
  };
  conversions: {
    start(input: StartConversionInput): Promise<{ jobId: string }>;
    cancel(jobId: string): Promise<void>;
    onEvent(listener: (event: ConversionEvent) => void): () => void;
  };
  environment: {
    check(): Promise<EnvironmentStatus>;
  };
  capabilities: {
    describe(): Promise<CapabilityManifest>;
  };
  shell: {
    openOutput(jobId: string): Promise<void>;
    revealOutput(jobId: string): Promise<void>;
    openTemplateLibrary(): Promise<void>;
  };
}

export interface AppAdapter extends Md2WordApi {
  /** Optional synchronous seed used only to avoid a blank first paint in the browser mock. */
  initialState?: {
    templates: TemplateProfile[];
    environment: EnvironmentStatus;
    capabilityManifest: CapabilityManifest;
  };
  /** Explicitly mock-only controls; absent from the Electron adapter. */
  demo?: {
    registerFile(kind: "markdown" | "template-docx" | "css", file: File): Promise<PickedFile>;
    registerNamedFile(kind: "template-docx" | "css", fileName: string): PickedFile;
    createOutput(directoryLabel: string, fileName: string): Promise<PickedOutput>;
    setNextConversionFailure(shouldFail: boolean): void;
    setWordAvailable(available: boolean): Promise<EnvironmentStatus>;
    reset(): Promise<{ templates: TemplateProfile[]; environment: EnvironmentStatus }>;
  };
}

declare global {
  interface Window {
    md2word?: Md2WordApi;
  }
}
