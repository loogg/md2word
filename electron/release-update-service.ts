import { AppError } from "./errors";
import type { UpdateCheckResult } from "./contracts";

export const REPOSITORY_URL = "https://github.com/loogg/md2word";
export const RELEASES_URL = `${REPOSITORY_URL}/releases`;
const LATEST_RELEASE_API = "https://api.github.com/repos/loogg/md2word/releases/latest";
const versionPattern = /^v?(\d+)\.(\d+)\.(\d+)$/;

export function compareVersions(left: string, right: string): number {
  const a = versionPattern.exec(left);
  const b = versionPattern.exec(right);
  if (!a || !b) throw new AppError("UPDATE_VERSION_INVALID", "版本号格式无效，无法比较更新。");
  for (let index = 1; index <= 3; index += 1) {
    const difference = Number(a[index]) - Number(b[index]);
    if (difference !== 0) return Math.sign(difference);
  }
  return 0;
}

interface GitHubRelease {
  tag_name?: unknown;
  html_url?: unknown;
  body?: unknown;
  published_at?: unknown;
  draft?: unknown;
  prerelease?: unknown;
}

export class ReleaseUpdateService {
  constructor(
    private readonly currentVersion: string,
    private readonly request: typeof fetch = fetch,
  ) {}

  async check(): Promise<UpdateCheckResult> {
    let response: Response;
    try {
      response = await this.request(LATEST_RELEASE_API, {
        headers: { Accept: "application/vnd.github+json", "User-Agent": "MD2Word-Desktop" },
        signal: AbortSignal.timeout(8_000),
      });
    } catch {
      throw new AppError("UPDATE_CHECK_FAILED", "无法连接 GitHub 检查更新，请稍后重试。", true);
    }
    if (response.status === 403 || response.status === 429) {
      return this.checkLatestPage();
    }
    if (!response.ok) {
      throw new AppError("UPDATE_CHECK_FAILED", "GitHub 更新服务暂时不可用，请稍后重试。", true);
    }
    let release: GitHubRelease;
    try {
      release = await response.json() as GitHubRelease;
    } catch {
      throw new AppError("UPDATE_RESPONSE_INVALID", "GitHub 返回的版本信息无效。", true);
    }
    const tag = typeof release.tag_name === "string" ? release.tag_name : "";
    const expectedUrl = versionPattern.test(tag)
      ? `${RELEASES_URL}/tag/${tag}`
      : undefined;
    if (!expectedUrl || release.html_url !== expectedUrl || release.draft || release.prerelease) {
      throw new AppError("UPDATE_RESPONSE_INVALID", "GitHub 返回的正式版本信息无效。", true);
    }
    const latestVersion = tag.replace(/^v/, "");
    const releaseNotes = typeof release.body === "string"
      ? release.body.replace(/<[^>]*>/g, "").trim().slice(0, 2_000)
      : "";
    return {
      status: compareVersions(latestVersion, this.currentVersion) > 0 ? "available" : "up-to-date",
      currentVersion: this.currentVersion,
      latestVersion,
      releaseNotes,
      publishedAt: typeof release.published_at === "string" ? release.published_at : undefined,
      checkedAt: new Date().toISOString(),
    };
  }

  private async checkLatestPage(): Promise<UpdateCheckResult> {
    let response: Response;
    try {
      response = await this.request(`${RELEASES_URL}/latest`, {
        method: "HEAD",
        redirect: "manual",
        signal: AbortSignal.timeout(15_000),
      });
    } catch {
      throw new AppError("UPDATE_CHECK_FAILED", "无法连接 GitHub 检查更新，请稍后重试。", true);
    }
    const location = response.headers.get("location") ?? "";
    const match = /^https:\/\/github\.com\/loogg\/md2word\/releases\/tag\/(v?\d+\.\d+\.\d+)$/.exec(location);
    if (![301, 302, 303, 307, 308].includes(response.status) || !match) {
      throw new AppError("UPDATE_RESPONSE_INVALID", "GitHub 返回的正式版本信息无效。", true);
    }
    const latestVersion = match[1].replace(/^v/, "");
    return {
      status: compareVersions(latestVersion, this.currentVersion) > 0 ? "available" : "up-to-date",
      currentVersion: this.currentVersion,
      latestVersion,
      releaseNotes: "",
      checkedAt: new Date().toISOString(),
    };
  }
}
