using Md2Word.Worker.Protocol;
using Md2Word.Worker.Services;

namespace Md2Word.Worker.Tests;

public sealed class PandocMetadataReaderTests
{
    [Fact]
    public async Task InvokesPandocForRuntimeSyntheticMarkdown()
    {
        using var workspace = new SyntheticWorkspace();
        var source = workspace.WriteText("runtime-metadata.md", """
            ---
            title: Runtime synthetic title
            heading_numbering: true
            heading_numbering_start_base: 2
            word_repeat_table_headers: false
            word_heading_numbering:
              level1:
                number_style: upper_roman
                format: "%1."
            manul_version_tables:
              - bookmark: MANUAL_TABLE_VERSION_HISTORY
                column_keys: [version, description]
                rows:
                  - version: "1.0"
                    description: |-
                      First line
                      Second line
            ---

            Synthetic body.
            """);

        var metadata = await new PandocMetadataReader().ReadAsync(
            FindRequiredPandoc(),
            source,
            CancellationToken.None);

        Assert.Equal("Runtime synthetic title", metadata.Title);
        Assert.True(metadata.HeadingNumbering);
        Assert.Equal(2, metadata.HeadingNumberingStartBase);
        Assert.False(metadata.WordRepeatTableHeaders);
        Assert.Equal("upper_roman", metadata.EffectiveWordHeadingNumbering[1].NumberStyle);
        var table = Assert.Single(metadata.VersionTables);
        Assert.Equal("First line\nSecond line", Assert.Single(table.Rows)["description"]);
    }

    [Fact]
    public void ParsesExplicitCoverAndHistoricalVersionTableMetadata()
    {
        var metadata = PandocMetadataReader.ParseJson("""
            {
              "pandoc-api-version": [1, 23, 1],
              "meta": {
                "title": {
                  "t": "MetaInlines",
                  "c": [
                    { "t": "Str", "c": "Synthetic" },
                    { "t": "Space" },
                    { "t": "Strong", "c": [{ "t": "Str", "c": "Manual" }] }
                  ]
                },
                "subtitle": { "t": "MetaString", "c": "P0 metadata" },
                "manul_version_tables": {
                  "t": "MetaList",
                  "c": [{
                    "t": "MetaMap",
                    "c": {
                      "bookmark": { "t": "MetaString", "c": "MANUAL_TABLE_VERSION_HISTORY" },
                      "column_keys": {
                        "t": "MetaList",
                        "c": [
                          { "t": "MetaString", "c": "version" },
                          { "t": "MetaString", "c": "description" }
                        ]
                      },
                      "rows": {
                        "t": "MetaList",
                        "c": [{
                          "t": "MetaMap",
                          "c": {
                            "version": { "t": "MetaString", "c": "1.0" },
                            "description": {
                              "t": "MetaInlines",
                              "c": [
                                { "t": "Str", "c": "First" },
                                { "t": "LineBreak" },
                                { "t": "Str", "c": "Second" }
                              ]
                            }
                          }
                        }]
                      }
                    }
                  }]
                }
              },
              "blocks": []
            }
            """);

        Assert.Equal("Synthetic Manual", metadata.Title);
        Assert.Equal("P0 metadata", metadata.Subtitle);
        var table = Assert.Single(metadata.VersionTables);
        Assert.Equal("MANUAL_TABLE_VERSION_HISTORY", table.Bookmark);
        Assert.Equal(["version", "description"], table.ColumnKeys);
        var row = Assert.Single(table.Rows);
        Assert.Equal("1.0", row["version"]);
        Assert.Equal("First\nSecond", row["description"]);
    }

    [Fact]
    public void MissingAndEmptyMetadataDoesNotRequestTemplateCapabilities()
    {
        var missing = PandocMetadataReader.ParseJson("""{"meta": {}, "blocks": []}""");
        var empty = PandocMetadataReader.ParseJson("""
            {
              "meta": {
                "title": { "t": "MetaString", "c": "  " },
                "subtitle": { "t": "MetaInlines", "c": [] },
                "manul_version_tables": { "t": "MetaList", "c": [] }
              },
              "blocks": []
            }
            """);

        Assert.False(missing.HasExplicitValues);
        Assert.False(empty.HasExplicitValues);
        Assert.True(missing.FigureCaptions);
        Assert.True(empty.FigureCaptions);
        Assert.True(missing.HeadingNumbering);
        Assert.Equal(1, missing.HeadingNumberingStartBase);
        Assert.True(missing.WordRepeatTableHeaders);
        Assert.Equal("decimal", missing.EffectiveWordHeadingNumbering[1].NumberStyle);
        Assert.Equal("%1.%2.%3.%4", missing.EffectiveWordHeadingNumbering[4].Format);
    }

    [Fact]
    public void ReadsFigureCaptionSwitchAndRejectsNonBooleanValues()
    {
        var disabled = PandocMetadataReader.ParseJson("""
            {
              "meta": {
                "figure_captions": { "t": "MetaBool", "c": false }
              },
              "blocks": []
            }
            """);

        Assert.False(disabled.FigureCaptions);

        var exception = Assert.Throws<WorkerCommandException>(() => PandocMetadataReader.ParseJson("""
            {
              "meta": {
                "figure_captions": { "t": "MetaString", "c": "false" }
              },
              "blocks": []
            }
            """));
        Assert.Equal("FRONT_MATTER_INVALID", exception.Code);
        Assert.Equal("metadata", exception.Stage);
    }

    [Fact]
    public void ReadsHeadingNumberingAndRepeatHeaderContract()
    {
        var metadata = PandocMetadataReader.ParseJson("""
            {
              "meta": {
                "heading_numbering": { "t": "MetaBool", "c": true },
                "heading_numbering_start_base": {
                  "t": "MetaInlines",
                  "c": [{ "t": "Str", "c": "2" }]
                },
                "word_repeat_table_headers": { "t": "MetaBool", "c": false },
                "word_heading_numbering": {
                  "t": "MetaMap",
                  "c": {
                    "level1": {
                      "t": "MetaMap",
                      "c": {
                        "number_style": { "t": "MetaString", "c": "upper_roman" },
                        "format": { "t": "MetaString", "c": "%1." }
                      }
                    },
                    "level2": {
                      "t": "MetaMap",
                      "c": {
                        "number_style": { "t": "MetaString", "c": "lower_letter" },
                        "format": { "t": "MetaString", "c": "%1-%2" }
                      }
                    }
                  }
                }
              },
              "blocks": []
            }
            """);

        Assert.True(metadata.HeadingNumbering);
        Assert.Equal(2, metadata.HeadingNumberingStartBase);
        Assert.False(metadata.WordRepeatTableHeaders);
        Assert.Equal("upper_roman", metadata.EffectiveWordHeadingNumbering[1].NumberStyle);
        Assert.Equal("%1.", metadata.EffectiveWordHeadingNumbering[1].Format);
        Assert.Equal("lower_letter", metadata.EffectiveWordHeadingNumbering[2].NumberStyle);
        Assert.Equal("%1-%2", metadata.EffectiveWordHeadingNumbering[2].Format);
        Assert.Equal("decimal", metadata.EffectiveWordHeadingNumbering[3].NumberStyle);
        Assert.Equal("%1.%2.%3", metadata.EffectiveWordHeadingNumbering[3].Format);
    }

    [Theory]
    [InlineData("heading_numbering", "{ \"t\": \"MetaString\", \"c\": \"false\" }")]
    [InlineData("word_repeat_table_headers", "{ \"t\": \"MetaString\", \"c\": \"false\" }")]
    [InlineData("heading_numbering_start_base", "{ \"t\": \"MetaString\", \"c\": \"0\" }")]
    public void RejectsInvalidHeadingAndTableSwitches(string key, string valueJson)
    {
        var exception = Assert.Throws<WorkerCommandException>(() => PandocMetadataReader.ParseJson(
            $$"""
              {
                "meta": {
                  "{{key}}": {{valueJson}}
                },
                "blocks": []
              }
              """));

        Assert.Equal("FRONT_MATTER_INVALID", exception.Code);
        Assert.Equal("metadata", exception.Stage);
    }

    [Fact]
    public void RejectsUnsupportedHeadingNumberStyleAndEmptyFormat()
    {
        var unsupported = Assert.Throws<WorkerCommandException>(() => PandocMetadataReader.ParseJson("""
            {
              "meta": {
                "word_heading_numbering": {
                  "t": "MetaMap",
                  "c": {
                    "level1": {
                      "t": "MetaMap",
                      "c": {
                        "number_style": { "t": "MetaString", "c": "hexadecimal" }
                      }
                    }
                  }
                }
              },
              "blocks": []
            }
            """));
        Assert.Equal("FRONT_MATTER_INVALID", unsupported.Code);

        var emptyFormat = Assert.Throws<WorkerCommandException>(() => PandocMetadataReader.ParseJson("""
            {
              "meta": {
                "word_heading_numbering": {
                  "t": "MetaMap",
                  "c": {
                    "level1": {
                      "t": "MetaMap",
                      "c": {
                        "format": { "t": "MetaString", "c": "  " }
                      }
                    }
                  }
                }
              },
              "blocks": []
            }
            """));
        Assert.Equal("FRONT_MATTER_INVALID", emptyFormat.Code);
    }

    [Fact]
    public void RejectsNonListHistoricalVersionTableSchemaWithStableCode()
    {
        var exception = Assert.Throws<WorkerCommandException>(() => PandocMetadataReader.ParseJson("""
            {
              "meta": {
                "manul_version_tables": {
                  "t": "MetaMap",
                  "c": {}
                }
              },
              "blocks": []
            }
            """));

        Assert.Equal("FRONT_MATTER_INVALID", exception.Code);
        Assert.Equal("metadata", exception.Stage);
    }

    [Fact]
    public void RejectsRowKeysNotDeclaredByColumnKeys()
    {
        var exception = Assert.Throws<WorkerCommandException>(() => PandocMetadataReader.ParseJson("""
            {
              "meta": {
                "manul_version_tables": {
                  "t": "MetaList",
                  "c": [{
                    "t": "MetaMap",
                    "c": {
                      "bookmark": { "t": "MetaString", "c": "MANUAL_TABLE_VERSION_HISTORY" },
                      "column_keys": {
                        "t": "MetaList",
                        "c": [{ "t": "MetaString", "c": "version" }]
                      },
                      "rows": {
                        "t": "MetaList",
                        "c": [{
                          "t": "MetaMap",
                          "c": {
                            "version": { "t": "MetaString", "c": "1.0" },
                            "undeclared": { "t": "MetaString", "c": "ignored is unsafe" }
                          }
                        }]
                      }
                    }
                  }]
                }
              },
              "blocks": []
            }
            """));

        Assert.Equal("FRONT_MATTER_INVALID", exception.Code);
    }

    private static string FindRequiredPandoc()
    {
        var configured = Environment.GetEnvironmentVariable("MD2WORD_PANDOC_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var executableNames = OperatingSystem.IsWindows()
            ? new[] { "pandoc.exe", "pandoc" }
            : new[] { "pandoc" };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var executableName in executableNames)
            {
                var candidate = Path.Combine(directory.Trim('"'), executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException("Pandoc is required to verify front-matter metadata extraction.");
    }
}
