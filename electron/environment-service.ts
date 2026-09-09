import type { EnvironmentStatus } from "./contracts";

export interface EnvironmentProbeResult {
  available: boolean;
  version?: string;
  detail: string;
}

export type EnvironmentProbe = () => Promise<EnvironmentProbeResult>;

export interface EnvironmentServiceOptions {
  platform?: NodeJS.Platform;
  now?: () => Date;
  timeoutMs?: number;
  probes: {
    word: EnvironmentProbe;
    pandoc: EnvironmentProbe;
    worker: EnvironmentProbe;
    mermaid: EnvironmentProbe;
  };
}

type ItemId = EnvironmentStatus["items"][number]["id"];

function redactDetail(detail: string): string {
  return detail
    .replace(/[A-Za-z]:\\(?:[^\\\s]+\\)*[^\s]*/g, "[本机路径]")
    .replace(/\\\\[^\\\s]+\\[^\s]+/g, "[网络路径]")
    .slice(0, 300);
}

export class EnvironmentService {
  readonly #platform: NodeJS.Platform;
  readonly #now: () => Date;
  readonly #timeoutMs: number;
  readonly #probes: EnvironmentServiceOptions["probes"];

  constructor(options: EnvironmentServiceOptions) {
    this.#platform = options.platform ?? process.platform;
    this.#now = options.now ?? (() => new Date());
    this.#timeoutMs = options.timeoutMs ?? 8_000;
    this.#probes = options.probes;
  }

  async check(): Promise<EnvironmentStatus> {
    const windowsReady = this.#platform === "win32";
    const items: EnvironmentStatus["items"] = [
      {
        id: "windows",
        name: "Windows",
        status: windowsReady ? "ready" : "blocked",
        required: true,
        version: windowsReady ? "Windows" : "不支持",
        detail: windowsReady ? "当前系统可运行桌面版。" : "MD2Word 正式版只支持 Windows。",
      },
    ];

    const specifications: Array<{
      id: Exclude<ItemId, "windows">;
      name: string;
      required: boolean;
      probe: EnvironmentProbe;
    }> = [
      { id: "word", name: "Microsoft Word", required: true, probe: this.#probes.word },
      { id: "pandoc", name: "Pandoc", required: true, probe: this.#probes.pandoc },
      { id: "worker", name: "C# Word Worker", required: true, probe: this.#probes.worker },
      { id: "mermaid", name: "Mermaid CLI", required: false, probe: this.#probes.mermaid },
    ];

    const probed = await Promise.all(
      specifications.map(async (specification) => {
        try {
          const result = await this.#withTimeout(specification.probe, specification.name);
          return {
            id: specification.id,
            name: specification.name,
            status: result.available ? "ready" : specification.required ? "blocked" : "optional-missing",
            required: specification.required,
            version: result.version ?? (result.available ? "已检测" : "不可用"),
            detail: redactDetail(result.detail),
          } satisfies EnvironmentStatus["items"][number];
        } catch (error) {
          return {
            id: specification.id,
            name: specification.name,
            status: specification.required ? "error" : "optional-missing",
            required: specification.required,
            version: "检查失败",
            detail: error instanceof Error && error.name === "TimeoutError" ? `${specification.name} 检查超时。` : `${specification.name} 检查失败。`,
          } satisfies EnvironmentStatus["items"][number];
        }
      }),
    );
    items.push(...probed);

    const blocked = items.some((item) => item.required && item.status !== "ready");
    const degraded = items.some((item) => !item.required && item.status !== "ready");
    return {
      checkedAt: this.#now().toISOString(),
      overall: blocked ? "blocked" : degraded ? "degraded" : "ready",
      items,
    };
  }

  async #withTimeout(probe: EnvironmentProbe, name: string): Promise<EnvironmentProbeResult> {
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      return await Promise.race([
        probe(),
        new Promise<never>((_, reject) => {
          timer = setTimeout(() => {
            const error = new Error(`${name} probe timed out`);
            error.name = "TimeoutError";
            reject(error);
          }, this.#timeoutMs);
        }),
      ]);
    } finally {
      if (timer) clearTimeout(timer);
    }
  }
}
