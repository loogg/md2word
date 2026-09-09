import type {
  CapabilityManifest,
  ConversionEvent,
  ConversionRequest,
  ConversionResult,
  ConversionStage,
  EnvironmentStatus,
  TemplateValidationReport,
} from "../src/types";

// Renderer-facing types are defined once in src/types.ts and re-exported here for Main/preload.
export type {
  AddTemplateInput,
  CapabilityCategory,
  CapabilityFeature,
  CapabilityLimitation,
  CapabilityManifest,
  CapabilityStatus,
  ConversionError,
  ConversionEvent,
  ConversionRequest,
  ConversionResult,
  ConversionStage,
  EnvironmentItem,
  EnvironmentItemStatus,
  EnvironmentStatus,
  FrontMatterCapability,
  IssueSeverity,
  Md2WordApi,
  MermaidOptions,
  PickedFile,
  PickedOutput,
  RuntimeCapabilities,
  StartConversionInput,
  StyleMode,
  TemplateCapabilities,
  TemplateBookmarkCapability,
  TemplateCssRoleCapability,
  TemplateProfile,
  TemplateValidationIssue,
  TemplateValidationReport,
  UpdateTemplateInput,
  ValidateTemplateInput,
  ValidationStatus,
  WordStyleMapping,
  WordStyleMappingStatus,
  WordStyleRole,
} from "../src/types";

export type FileHandleKind = "markdown" | "template-docx" | "css" | "output-docx";

/** Worker-only result. Real paths never cross the Main -> Renderer boundary. */
export interface WorkerConversionResult extends Omit<ConversionResult, "outputFileName" | "outputDisplayPath" | "diagnosticAvailable"> {
  outputPath?: string;
  diagnosticPath?: string;
}

export interface WorkerConversionEvent extends Omit<ConversionEvent, "result"> {
  result?: WorkerConversionResult;
}

export type WorkerRequestFrame =
  | { protocolVersion: "1.0"; requestId: string; command: "diagnose" }
  | { protocolVersion: "1.0"; requestId: string; command: "describe-capabilities" }
  | {
      protocolVersion: "1.0";
      requestId: string;
      command: "validate-template";
      docxPath: string;
      cssPath: string;
    }
  | {
      protocolVersion: "1.0";
      requestId: string;
      command: "convert";
      request: ConversionRequest;
    };

export interface WorkerCancelFrame {
  protocolVersion: "1.0";
  requestId: string;
  command: "cancel";
  jobId: string;
}

export interface WorkerError {
  code: string;
  message: string;
  stage?: ConversionStage;
  retryable?: boolean;
}

export type WorkerOutputFrame<TResult = CapabilityManifest | EnvironmentStatus | TemplateValidationReport | WorkerConversionResult> =
  | { protocolVersion: "1.0"; requestId: string; type: "event"; event: WorkerConversionEvent }
  | { protocolVersion: "1.0"; requestId: string; type: "result"; result: TResult }
  | { protocolVersion: "1.0"; requestId: string; type: "error"; error: WorkerError };
