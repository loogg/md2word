using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class DiagnosticsServiceTests
{
    [Theory]
    [InlineData(true, true, "ready")]
    [InlineData(true, false, "optional-missing")]
    [InlineData(false, true, "optional-missing")]
    [InlineData(false, false, "optional-missing")]
    public void MermaidIsReadyOnlyWhenNpxAndSupportedBrowserAreAvailable(
        bool npxAvailable,
        bool browserAvailable,
        string expectedStatus)
    {
        var service = CreateService(npxAvailable, browserAvailable);

        var status = service.Diagnose();

        var mermaid = Assert.Single(status.Items, item => item.Id == "mermaid");
        Assert.Equal(expectedStatus, mermaid.Status);
        Assert.False(mermaid.Required);
        Assert.Equal(npxAvailable ? "npx 10.9.2" : "unavailable", mermaid.Version);
    }

    [Fact]
    public void MermaidDiagnosticNeverIncludesTheDetectedBrowserPath()
    {
        const string privateBrowserPath = @"C:\Users\PrivateAccount\AppData\Local\Microsoft\Edge\Application\msedge.exe";
        var service = new DiagnosticsService(
            (executable, _) => executable == "npx"
                ? new ToolProbeResult(true, "10.9.2")
                : new ToolProbeResult(true, "test-version"),
            () => new MermaidBrowserProbeResult(true, "Microsoft Edge", privateBrowserPath));

        var status = service.Diagnose();

        var mermaid = Assert.Single(status.Items, item => item.Id == "mermaid");
        Assert.Equal("ready", mermaid.Status);
        Assert.Contains("Microsoft Edge", mermaid.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(privateBrowserPath, mermaid.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PrivateAccount", mermaid.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingBrowserDetailUsesOnlyGenericProductNames()
    {
        const string privateAttemptedPath = @"C:\Users\PrivateAccount\AppData\Local\Google\Chrome\Application\chrome.exe";
        var service = new DiagnosticsService(
            (_, _) => new ToolProbeResult(true, "10.9.2"),
            () => new MermaidBrowserProbeResult(false, "Google Chrome", privateAttemptedPath));

        var status = service.Diagnose();

        var mermaid = Assert.Single(status.Items, item => item.Id == "mermaid");
        Assert.Equal("optional-missing", mermaid.Status);
        Assert.Contains("Microsoft Edge or Google Chrome", mermaid.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(privateAttemptedPath, mermaid.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PrivateAccount", mermaid.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static DiagnosticsService CreateService(bool npxAvailable, bool browserAvailable) => new(
        (executable, _) => executable == "npx"
            ? new ToolProbeResult(npxAvailable, npxAvailable ? "10.9.2" : "unavailable")
            : new ToolProbeResult(true, "test-version"),
        () => browserAvailable
            ? new MermaidBrowserProbeResult(true, "Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")
            : MermaidBrowserProbeResult.Unavailable);
}
