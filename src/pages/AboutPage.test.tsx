import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { vi } from "vitest";
import { AboutPage } from "./AboutPage";
import { createBrowserMockAdapter } from "../lib/mockAdapter";
import type { UpdateCheckResult } from "../types";

describe("About and updates", () => {
  it("shows clearly marked demo update results and opens the release action", async () => {
    const adapter = createBrowserMockAdapter();
    adapter.updates.openLatestRelease = vi.fn(async () => {});
    render(<AboutPage adapter={adapter} />);
    expect(screen.getByRole("heading", { name: "关于" })).toHaveFocus();
    expect(screen.getByText(/浏览器交互预览（模拟更新检查）/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "检查更新" }));
    await waitFor(() => expect(screen.getByText(/演示：发现新版本/)).toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "前往下载新版" }));
    expect(adapter.updates.openLatestRelease).toHaveBeenCalledOnce();
  });

  it("shows a retryable error and recovers on another check", async () => {
    const adapter = createBrowserMockAdapter();
    adapter.updates.check = vi.fn()
      .mockRejectedValueOnce(new Error("网络失败"))
      .mockResolvedValueOnce({ status: "up-to-date", currentVersion: "0.7.0", latestVersion: "0.7.0", releaseNotes: "", checkedAt: new Date().toISOString() });
    render(<AboutPage adapter={adapter} />);
    fireEvent.click(screen.getByRole("button", { name: "检查更新" }));
    await waitFor(() => expect(screen.getByText("网络失败")).toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "检查更新" }));
    await waitFor(() => expect(screen.getByText(/已是最新正式版本/)).toBeInTheDocument());
  });

  it("marks the browser loading state as a demo and disables duplicate checks", async () => {
    const adapter = createBrowserMockAdapter();
    let finishCheck: (value: UpdateCheckResult) => void = () => {};
    adapter.updates.check = () => new Promise((resolve) => { finishCheck = resolve; });
    render(<AboutPage adapter={adapter} />);
    fireEvent.click(screen.getByRole("button", { name: "检查更新" }));
    expect(screen.getByRole("button", { name: "正在检查" })).toBeDisabled();
    expect(screen.getByText("演示：正在检查版本…")).toBeInTheDocument();
    finishCheck({ status: "up-to-date", currentVersion: "0.7.0", latestVersion: "0.7.0", releaseNotes: "", checkedAt: new Date().toISOString() });
    await waitFor(() => expect(screen.getByText(/已是最新正式版本/)).toBeInTheDocument());
  });
});
