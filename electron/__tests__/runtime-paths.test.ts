import path from "node:path";
import { describe, expect, it } from "vitest";
import { resolveTemplateLibraryRoot } from "../runtime-paths";

describe("resolveTemplateLibraryRoot", () => {
  it("keeps Setup templates in userData even if a portable environment variable is inherited", () => {
    expect(resolveTemplateLibraryRoot({
      isPackaged: true,
      isInstalled: true,
      userDataPath: "C:\\AppData\\MD2Word",
      executablePath: "C:\\Programs\\MD2Word\\MD2Word.exe",
      portableExecutableDirectory: "D:\\Other Portable App",
    })).toBe(path.normalize("C:\\AppData\\MD2Word\\templates"));
  });
  it("keeps development templates under userData", () => {
    expect(resolveTemplateLibraryRoot({
      isPackaged: false,
      userDataPath: "C:\\Users\\synthetic\\AppData\\Roaming\\md2word",
      executablePath: "D:\\workspace\\node_modules\\electron\\electron.exe",
      portableExecutableDirectory: "E:\\ignored-portable-root",
    })).toBe(path.normalize("C:\\Users\\synthetic\\AppData\\Roaming\\md2word\\templates"));
  });

  it("uses the original portable executable directory for a single-file build", () => {
    expect(resolveTemplateLibraryRoot({
      isPackaged: true,
      userDataPath: "C:\\ignored-user-data",
      executablePath: "C:\\Temp\\portable-extract\\MD2Word.exe",
      portableExecutableDirectory: "D:\\MD2Word Portable",
    })).toBe(path.normalize("D:\\MD2Word Portable\\templates"));
  });

  it("uses the executable directory for an unpacked or ZIP build", () => {
    expect(resolveTemplateLibraryRoot({
      isPackaged: true,
      userDataPath: "C:\\ignored-user-data",
      executablePath: "D:\\MD2Word\\MD2Word.exe",
    })).toBe(path.normalize("D:\\MD2Word\\templates"));
  });

  it("ignores a relative portable directory", () => {
    expect(resolveTemplateLibraryRoot({
      isPackaged: true,
      userDataPath: "C:\\ignored-user-data",
      executablePath: "D:\\MD2Word\\MD2Word.exe",
      portableExecutableDirectory: ".\\spoofed",
    })).toBe(path.normalize("D:\\MD2Word\\templates"));
  });
});
