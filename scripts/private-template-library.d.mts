export interface PrivateTemplateLibrary {
  root: string;
  indexPath: string;
  profiles: Array<{ id: string }>;
}

export interface TemplateCatalog {
  root: string;
  bundles: Array<{ name: string; root: string; profiles: Array<{ id: string }> }>;
}

export function validateTemplateLibrary(rootPath: string): Promise<PrivateTemplateLibrary>;
export function validateTemplateCatalog(rootPath: string): Promise<TemplateCatalog>;
