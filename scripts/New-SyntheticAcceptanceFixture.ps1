param(
    [string]$OutputDirectory = "output/acceptance-fixture",
    [ValidateSet("Basic", "Comprehensive")]
    [string]$Scenario = "Basic"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "output"))
$targetRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
$allowedPrefix = $allowedRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not ($targetRoot + [System.IO.Path]::DirectorySeparatorChar).StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Synthetic acceptance fixtures must stay under the workspace output directory."
}

if (Test-Path -LiteralPath $targetRoot) {
    $resolvedTarget = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $targetRoot).Path)
    if (-not ($resolvedTarget + [System.IO.Path]::DirectorySeparatorChar).StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace a fixture directory outside the allowed output root."
    }
    Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
}

if ($Scenario -eq "Comprehensive") {
    & (Join-Path $PSScriptRoot "New-ComprehensiveSyntheticAcceptanceFixture.ps1") `
        -OutputDirectory $OutputDirectory
    if (-not $?) {
        throw "The comprehensive synthetic fixture generator failed."
    }
    exit 0
}

$packageRoot = Join-Path $targetRoot "docx-package"
New-Item -ItemType Directory -Path (Join-Path $packageRoot "_rels") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageRoot "word/_rels") -Force | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Write-Utf8([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText($Path, $Content, $utf8)
}

Write-Utf8 (Join-Path $packageRoot "[Content_Types].xml") @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
  <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
</Types>
'@

Write-Utf8 (Join-Path $packageRoot "_rels/.rels") @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>
'@

Write-Utf8 (Join-Path $packageRoot "word/_rels/document.xml.rels") @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rIdStyles" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
</Relationships>
'@

Write-Utf8 (Join-Path $packageRoot "word/styles.xml") @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:docDefaults><w:rPrDefault/><w:pPrDefault/></w:docDefaults>
  <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
  <w:style w:type="paragraph" w:styleId="BodyStyle"><w:name w:val="示例 正文"/><w:aliases w:val="正文"/></w:style>
  <w:style w:type="paragraph" w:styleId="OrderedStyle"><w:name w:val="示例 有序列项"/></w:style>
  <w:style w:type="paragraph" w:styleId="Heading1Style"><w:name w:val="示例 标题1"/></w:style>
  <w:style w:type="paragraph" w:styleId="Heading2Style"><w:name w:val="示例 标题2"/></w:style>
  <w:style w:type="paragraph" w:styleId="Heading3"><w:name w:val="heading 3"/></w:style>
  <w:style w:type="paragraph" w:styleId="Heading4"><w:name w:val="heading 4"/></w:style>
  <w:style w:type="paragraph" w:styleId="Heading5"><w:name w:val="heading 5"/></w:style>
  <w:style w:type="paragraph" w:styleId="Heading6"><w:name w:val="heading 6"/></w:style>
  <w:style w:type="paragraph" w:styleId="CaptionStyle"><w:name w:val="示例 图示"/></w:style>
  <w:style w:type="paragraph" w:styleId="CodeStyle"><w:name w:val="示例 代码"/><w:aliases w:val="CodeBlock"/></w:style>
</w:styles>
'@

Write-Utf8 (Join-Path $packageRoot "word/document.xml") @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
    <w:p><w:bookmarkStart w:id="10" w:name="MANUAL_COVER_TITLE"/><w:r><w:t>合成验收标题</w:t></w:r><w:bookmarkEnd w:id="10"/></w:p>
    <w:p><w:bookmarkStart w:id="11" w:name="MANUAL_COVER_SUBTITLE"/><w:r><w:t>合成验收副标题</w:t></w:r><w:bookmarkEnd w:id="11"/></w:p>
    <w:p><w:bookmarkStart w:id="1" w:name="MANUAL_BODY_START"/><w:bookmarkEnd w:id="1"/></w:p>
    <w:p><w:pPr><w:pStyle w:val="BodyStyle"/></w:pPr><w:r><w:t>synthetic placeholder</w:t></w:r></w:p>
    <w:p><w:bookmarkStart w:id="2" w:name="MANUAL_BODY_END"/><w:bookmarkEnd w:id="2"/></w:p>
    <w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440"/></w:sectPr>
  </w:body>
</w:document>
'@

$templatePath = Join-Path $targetRoot "synthetic-template.docx"
$archiveStream = [System.IO.File]::Open($templatePath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write)
$archive = [System.IO.Compression.ZipArchive]::new(
    $archiveStream,
    [System.IO.Compression.ZipArchiveMode]::Create,
    $false
)
try {
    Get-ChildItem -LiteralPath $packageRoot -Recurse -File | ForEach-Object {
        $relativePath = $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
        $entry = $archive.CreateEntry($relativePath, [System.IO.Compression.CompressionLevel]::Optimal)
        $entryStream = $entry.Open()
        $sourceStream = [System.IO.File]::OpenRead($_.FullName)
        try {
            $sourceStream.CopyTo($entryStream)
        }
        finally {
            $sourceStream.Dispose()
            $entryStream.Dispose()
        }
    }
}
finally {
    $archive.Dispose()
    $archiveStream.Dispose()
}
Remove-Item -LiteralPath $packageRoot -Recurse -Force

$cssPath = Join-Path $targetRoot "synthetic-style.css"
Write-Utf8 $cssPath @'
p.manual-body-paragraph, p.manual-table-paragraph { mso-style-name: "示例 正文"; }
li.manual-body-ordered-item, p.manual-body-ordered-item-paragraph { mso-style-name: "示例 有序列项"; }
li.manual-body-unordered-item, p.manual-body-unordered-item-paragraph { mso-style-name: "示例 正文"; }
h1 { mso-style-name: "示例 标题1"; }
h2 { mso-style-name: "示例 标题2"; }
p.manual-figure-caption { mso-style-name: "示例 图示"; }
p.manual-table-caption, table > caption { mso-style-name: "示例 图示"; }
p.manual-code-block-paragraph { mso-style-name: "示例 代码"; }
code.manual-inline-code { mso-style-name: "示例 正文"; }
p.manual-admonition-paragraph,
p.manual-figure-image-paragraph { mso-style-name: "示例 正文"; }
'@

$markdownPath = Join-Path $targetRoot "synthetic-lists.md"
Write-Utf8 $markdownPath @'
---
title: 合成列表验收
subtitle: 不含业务数据
---

# 列表结构

- 连字符项目
* 星号项目
+ 加号项目

3. 非一开始
4. 第二项
   - 二级无序
     1. 三级有序
     2. 三级有序第二项
   - 二级无序第二项
5. 第三项

1. 宽松列表第一项

   同一条目的第二段。

2. 宽松列表第二项，包含代码：

   ```text
   synthetic code
   ```

正文分隔。

1. 分离后重新从一开始
2. 分离列表第二项

```text
top-level synthetic code
```

```mermaid
sequenceDiagram
  participant A as Device
  participant B as Network
  A->>B: Discover
  B-->>A: Configure
```
<!-- caption: 设备发现与网络配置系统架构时序 -->
'@

[pscustomobject]@{
    template = $templatePath
    css = $cssPath
    markdown = $markdownPath
} | ConvertTo-Json -Compress
