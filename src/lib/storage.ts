import { DEMO_TEMPLATES } from "../data/demoData";
import type { TemplateProfile } from "../types";

const STORAGE_KEY = "md2word.prototype.templates.v1";

const cloneDemoTemplates = () => structuredClone(DEMO_TEMPLATES);

export function loadTemplates(): TemplateProfile[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return cloneDemoTemplates();
    const parsed = JSON.parse(raw) as TemplateProfile[];
    return Array.isArray(parsed) ? parsed : cloneDemoTemplates();
  } catch {
    return cloneDemoTemplates();
  }
}

export function saveTemplates(templates: TemplateProfile[]): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(templates));
}

export function resetTemplates(): TemplateProfile[] {
  localStorage.removeItem(STORAGE_KEY);
  return cloneDemoTemplates();
}
