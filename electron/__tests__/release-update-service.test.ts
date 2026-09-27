import { describe, expect, it, vi } from "vitest";
import { compareVersions, ReleaseUpdateService } from "../release-update-service";

const release = (tag: string) => ({
  tag_name: tag,
  html_url: `https://github.com/loogg/md2word/releases/tag/${tag}`,
  body: "新增版本检查与界面优化。",
  published_at: "2026-09-27T00:00:00Z",
  draft: false,
  prerelease: false,
});

const requestFor = (body: unknown, status = 200) => vi.fn(async () => ({
  ok: status === 200,
  status,
  json: async () => body,
})) as unknown as typeof fetch;

describe("GitHub Release update checks", () => {
  it("compares numeric semantic versions, not strings", () => {
    expect(compareVersions("0.10.0", "0.9.9")).toBe(1);
    expect(compareVersions("v0.7.0", "0.7.0")).toBe(0);
    expect(compareVersions("0.6.9", "0.7.0")).toBe(-1);
    expect(() => compareVersions("main", "0.7.0")).toThrow(/版本号格式/);
  });

  it("reports an available release and trims untrusted notes", async () => {
    const request = requestFor({ ...release("v0.8.0"), body: "<b>新版</b>" });
    const result = await new ReleaseUpdateService("0.7.0", request).check();
    expect(result).toMatchObject({ status: "available", latestVersion: "0.8.0", releaseNotes: "新版" });
    expect(request).toHaveBeenCalledWith(
      "https://api.github.com/repos/loogg/md2word/releases/latest",
      expect.objectContaining({ headers: expect.objectContaining({ Accept: "application/vnd.github+json" }) }),
    );
  });

  it("does not offer downgrades or mismatched release links", async () => {
    await expect(new ReleaseUpdateService("0.7.0", requestFor(release("v0.6.1"))).check())
      .resolves.toMatchObject({ status: "up-to-date" });
    await expect(new ReleaseUpdateService("0.7.0", requestFor({ ...release("v0.8.0"), html_url: "https://other.example/" })).check())
      .rejects.toMatchObject({ code: "UPDATE_RESPONSE_INVALID" });
  });

  it("reports network and server failures without changing files", async () => {
    await expect(new ReleaseUpdateService("0.7.0", requestFor({}, 503)).check())
      .rejects.toMatchObject({ code: "UPDATE_CHECK_FAILED" });
    const failingRequest = vi.fn(async () => { throw new Error("offline"); }) as unknown as typeof fetch;
    await expect(new ReleaseUpdateService("0.7.0", failingRequest).check())
      .rejects.toMatchObject({ code: "UPDATE_CHECK_FAILED" });
  });

  it("falls back to the fixed latest-release redirect when anonymous API requests are rate limited", async () => {
    const request = vi.fn()
      .mockResolvedValueOnce({ ok: false, status: 403 })
      .mockResolvedValueOnce({ status: 302, headers: new Headers({ location: "https://github.com/loogg/md2word/releases/tag/v0.8.0" }) }) as unknown as typeof fetch;
    await expect(new ReleaseUpdateService("0.7.0", request).check()).resolves.toMatchObject({
      status: "available",
      latestVersion: "0.8.0",
      releaseNotes: "",
    });
    expect(request).toHaveBeenLastCalledWith(
      "https://github.com/loogg/md2word/releases/latest",
      expect.objectContaining({ method: "HEAD", redirect: "manual" }),
    );
  });

  it("rejects a rate-limit fallback redirected outside the trusted release tags", async () => {
    const request = vi.fn()
      .mockResolvedValueOnce({ ok: false, status: 429 })
      .mockResolvedValueOnce({ status: 302, headers: new Headers({ location: "https://other.example/releases/tag/v0.8.0" }) }) as unknown as typeof fetch;
    await expect(new ReleaseUpdateService("0.7.0", request).check())
      .rejects.toMatchObject({ code: "UPDATE_RESPONSE_INVALID" });
  });
});
