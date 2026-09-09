interface PublicTemplateValidation {
  status: string;
  checkedAt: string;
  contentFingerprint: string;
  issues: Array<{ severity?: string }>;
  styleMappings: unknown[];
  [key: string]: unknown;
}

export function stagePublicTemplateCatalog(options: {
  projectRoot: string;
  stageRoot: string;
  sourceRoot?: string;
  workerPath?: string;
  validateTemplate?: (
    docxPath: string,
    cssPath: string,
    requestId: string,
  ) => Promise<PublicTemplateValidation>;
}): Promise<void>;
