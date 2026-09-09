import path from "node:path";

export interface TemplateLibraryPathOptions {
  isPackaged: boolean;
  userDataPath: string;
  executablePath: string;
  portableExecutableDirectory?: string;
}

function absoluteDirectory(candidate: string | undefined): string | undefined {
  const value = candidate?.trim();
  return value && path.isAbsolute(value) ? path.resolve(value) : undefined;
}

/**
 * Development keeps mutable data out of the repository. Packaged portable builds
 * deliberately keep the template library beside the user-visible executable.
 */
export function resolveTemplateLibraryRoot(options: TemplateLibraryPathOptions): string {
  if (!options.isPackaged) return path.join(path.resolve(options.userDataPath), "templates");

  const executableDirectory =
    absoluteDirectory(options.portableExecutableDirectory)
    ?? path.dirname(path.resolve(options.executablePath));
  return path.join(executableDirectory, "templates");
}
