using System.Text.Json;
using Md2Word.Worker.Protocol;

namespace Md2Word.Worker.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task DescribeCapabilitiesReturnsTheBundledVersionedCatalog()
    {
        var input = JsonSerializer.Serialize(new
        {
            protocolVersion = "1.0",
            requestId = "req-capabilities",
            command = "describe-capabilities",
        });
        var output = new StringWriter();

        var exitCode = await WorkerProtocolHost.RunAsync(
            new StringReader(input + Environment.NewLine),
            output,
            new StringWriter());

        Assert.Equal(0, exitCode);
        using var frame = JsonDocument.Parse(output.ToString());
        Assert.Equal("result", frame.RootElement.GetProperty("type").GetString());
        var result = frame.RootElement.GetProperty("result");
        Assert.Equal("1.1", result.GetProperty("schemaVersion").GetString());
        Assert.Equal("0.6.1", result.GetProperty("productVersion").GetString());
        Assert.Equal("1.0", result.GetProperty("protocolVersion").GetString());
        Assert.Contains(
            result.GetProperty("frontMatter").EnumerateArray(),
            item => item.GetProperty("key").GetString() == "word_heading_numbering");
        Assert.Contains(
            result.GetProperty("categories").EnumerateArray()
                .SelectMany(category => category.GetProperty("items").EnumerateArray()),
            item => item.GetProperty("id").GetString() == "CAP-TEMPLATE-CSS-MAPPING");
    }

    [Theory]
    [InlineData("not-json", "INVALID_JSON")]
    [InlineData("{\"protocolVersion\":\"2.0\",\"requestId\":\"req\",\"command\":\"diagnose\"}", "PROTOCOL_VERSION_UNSUPPORTED")]
    [InlineData("{\"protocolVersion\":\"1.0\",\"requestId\":\"req\",\"command\":\"unknown\"}", "COMMAND_UNKNOWN")]
    [InlineData("{\"protocolVersion\":\"1.0\",\"requestId\":\"req\",\"command\":\"cancel\",\"jobId\":\"job\"}", "CANCEL_WITHOUT_CONVERSION")]
    public async Task InvalidInitialFramesProduceOneProtocolErrorAndNonzeroExit(string inputFrame, string expectedCode)
    {
        var output = new StringWriter();
        var diagnostics = new StringWriter();

        var exitCode = await WorkerProtocolHost.RunAsync(new StringReader(inputFrame + Environment.NewLine), output, diagnostics);

        Assert.NotEqual(0, exitCode);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        using var json = JsonDocument.Parse(lines[0]);
        Assert.Equal("1.0", json.RootElement.GetProperty("protocolVersion").GetString());
        Assert.Equal("error", json.RootElement.GetProperty("type").GetString());
        Assert.Equal(expectedCode, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("not-json", output.ToString());
        Assert.NotEmpty(diagnostics.ToString());
    }

    [Fact]
    public async Task ValidateTemplateReturnsBusinessResultEvenWhenInvalid()
    {
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate([( "Body", "正文", null )], includeBodyStart: false);
        var css = workspace.WriteText("style.css", "p.manual-body-paragraph { mso-style-name: \"正文\"; }");
        var input = JsonSerializer.Serialize(new
        {
            protocolVersion = "1.0",
            requestId = "req-validate",
            command = "validate-template",
            docxPath = template,
            cssPath = css,
        });
        var output = new StringWriter();

        var exitCode = await WorkerProtocolHost.RunAsync(new StringReader(input + Environment.NewLine), output, new StringWriter());

        Assert.Equal(0, exitCode);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("result", result.RootElement.GetProperty("type").GetString());
        Assert.Equal("invalid", result.RootElement.GetProperty("result").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ValidateTemplateSerializesTheSharedStyleMappingContract()
    {
        using var workspace = new SyntheticWorkspace();
        var template = workspace.CreateTemplate(
        [
            ("BodyStyle", "正文", null),
            ("OrderedStyle", "示例 有序列项", null),
            ("UnorderedStyle", "示例 正文", null),
            ("Heading2Style", "标题 2", null),
            ("CodeStyle", "CodeBlock", null),
        ]);
        var css = workspace.WriteText("style.css", """
            p.manual-body-paragraph { mso-style-name: "正文"; }
            li.manual-body-ordered-item { mso-style-name: "示例 有序列项"; }
            li.manual-body-unordered-item { mso-style-name: "示例 正文"; }
            h2 { mso-style-name: "标题 2"; }
            p.manual-code-block-paragraph { mso-style-name: "CodeBlock"; }
            """);
        var input = JsonSerializer.Serialize(new
        {
            protocolVersion = "1.0",
            requestId = "req-style-contract",
            command = "validate-template",
            docxPath = template,
            cssPath = css,
        });
        var output = new StringWriter();

        var exitCode = await WorkerProtocolHost.RunAsync(new StringReader(input + Environment.NewLine), output, new StringWriter());

        Assert.Equal(0, exitCode);
        using var frame = JsonDocument.Parse(output.ToString());
        var result = frame.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("styleMap", out _));
        var mappings = result.GetProperty("styleMappings").EnumerateArray().ToArray();

        var ordered = mappings.Single(mapping => mapping.GetProperty("role").GetString() == "ordered-list");
        Assert.Equal("li.manual-body-ordered-item", ordered.GetProperty("cssSelector").GetString());
        Assert.Equal("示例 有序列项", ordered.GetProperty("requestedStyleName").GetString());
        Assert.Equal("OrderedStyle", ordered.GetProperty("resolvedStyleId").GetString());
        Assert.Equal("resolved", ordered.GetProperty("status").GetString());

        var heading = mappings.Single(mapping => mapping.GetProperty("role").GetString() == "heading"
            && mapping.GetProperty("headingLevel").GetInt32() == 2);
        Assert.Equal("标题 2", heading.GetProperty("resolvedStyleName").GetString());
        Assert.DoesNotContain(mappings, mapping => mapping.GetProperty("role").GetString() is "h1" or "h2");

        var unconfiguredHeading = mappings.Single(mapping => mapping.GetProperty("role").GetString() == "heading"
            && mapping.GetProperty("headingLevel").GetInt32() == 1);
        Assert.Equal(string.Empty, unconfiguredHeading.GetProperty("requestedStyleName").GetString());
        Assert.Equal("Heading1", unconfiguredHeading.GetProperty("resolvedStyleId").GetString());
        Assert.Equal("word-fallback", unconfiguredHeading.GetProperty("status").GetString());

        var table = mappings.Single(mapping => mapping.GetProperty("role").GetString() == "table");
        Assert.Equal("Normal", table.GetProperty("resolvedStyleId").GetString());
        Assert.Equal("word-fallback", table.GetProperty("status").GetString());
    }
}
