param(
    [string]$OutputDirectory = "output/comprehensive-acceptance"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "output"))
$targetRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
$allowedPrefix = $allowedRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) `
    + [System.IO.Path]::DirectorySeparatorChar

if (-not ($targetRoot + [System.IO.Path]::DirectorySeparatorChar).StartsWith(
    $allowedPrefix,
    [System.StringComparison]::OrdinalIgnoreCase
)) {
    throw "Synthetic acceptance fixtures must stay under the workspace output directory."
}

if (Test-Path -LiteralPath $targetRoot) {
    $resolvedTarget = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $targetRoot).Path)
    if (-not ($resolvedTarget + [System.IO.Path]::DirectorySeparatorChar).StartsWith(
        $allowedPrefix,
        [System.StringComparison]::OrdinalIgnoreCase
    )) {
        throw "Refusing to replace a fixture directory outside the allowed output root."
    }
    Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
}

$utf8 = [System.Text.UTF8Encoding]::new($false)

function Write-Utf8([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText($Path, $Content, $utf8)
}

function New-DocxArchive([string]$PackageRoot, [string]$DestinationPath) {
    $archiveStream = [System.IO.File]::Open(
        $DestinationPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::Write
    )
    $archive = [System.IO.Compression.ZipArchive]::new(
        $archiveStream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $false
    )
    try {
        Get-ChildItem -LiteralPath $PackageRoot -Recurse -File | ForEach-Object {
            $relativePath = $_.FullName.Substring($PackageRoot.Length + 1).Replace('\', '/')
            $entry = $archive.CreateEntry(
                $relativePath,
                [System.IO.Compression.CompressionLevel]::Optimal
            )
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
}

function New-SyntheticPng(
    [string]$Path,
    [int]$Width,
    [int]$Height,
    [string]$Label,
    [string]$AccentHex
) {
    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $background = [System.Drawing.Color]::FromArgb(255, 248, 250, 252)
    $accent = [System.Drawing.ColorTranslator]::FromHtml($AccentHex)
    $ink = [System.Drawing.Color]::FromArgb(255, 31, 41, 55)
    $borderPen = [System.Drawing.Pen]::new($accent, [Math]::Max(4, [int]($Height / 45)))
    $accentBrush = [System.Drawing.SolidBrush]::new($accent)
    $inkBrush = [System.Drawing.SolidBrush]::new($ink)
    $fontSize = [Math]::Max(12, [Math]::Min(42, [int]($Height / 7)))
    $font = $null
    try {
        $graphics.Clear($background)
        $inset = [Math]::Max(18, [int]($Height / 12))
        $graphics.DrawRectangle(
            $borderPen,
            $inset,
            $inset,
            $Width - (2 * $inset),
            $Height - (2 * $inset)
        )
        $barWidth = [Math]::Max(24, [int]($Width / 30))
        $graphics.FillRectangle(
            $accentBrush,
            $inset * 2,
            $inset * 2,
            $barWidth,
            $Height - (4 * $inset)
        )
        $textX = $inset * 3 + $barWidth
        $maxTextWidth = $Width - $textX - ($inset * 2)
        do {
            if ($null -ne $font) {
                $font.Dispose()
            }
            $font = [System.Drawing.Font]::new(
                "Arial",
                $fontSize,
                [System.Drawing.FontStyle]::Bold
            )
            $measured = $graphics.MeasureString($Label, $font)
            if ($measured.Width -le $maxTextWidth -or $fontSize -le 12) {
                break
            }
            $fontSize -= 2
        } while ($true)
        $graphics.DrawString(
            $Label,
            $font,
            $inkBrush,
            $textX,
            [int](($Height - $measured.Height) / 2)
        )
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        if ($null -ne $font) {
            $font.Dispose()
        }
        $inkBrush.Dispose()
        $accentBrush.Dispose()
        $borderPen.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$packageRoot = Join-Path $targetRoot "docx-package"
New-Item -ItemType Directory -Path (Join-Path $packageRoot "_rels") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $packageRoot "word/_rels") -Force | Out-Null

Write-Utf8 (Join-Path $packageRoot "[Content_Types].xml") @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Default Extension="png" ContentType="image/png"/>
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
  <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
  <Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/>
  <Override PartName="/word/settings.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml"/>
  <Override PartName="/word/header1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml"/>
  <Override PartName="/word/footer1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml"/>
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
  <Relationship Id="rIdNumbering" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering" Target="numbering.xml"/>
  <Relationship Id="rIdSettings" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings" Target="settings.xml"/>
  <Relationship Id="rIdHeader1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/header" Target="header1.xml"/>
  <Relationship Id="rIdFooter1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer" Target="footer1.xml"/>
</Relationships>
'@

Write-Utf8 (Join-Path $packageRoot "word/settings.xml") @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:settings xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:zoom w:percent="100"/>
  <w:updateFields w:val="true"/>
  <w:compat/>
</w:settings>
'@

Write-Utf8 (Join-Path $packageRoot "word/header1.xml") @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:hdr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:p>
    <w:pPr><w:pStyle w:val="AcceptanceFixed"/></w:pPr>
    <w:r><w:t>合成复杂验收模板｜仅限本地测试</w:t></w:r>
  </w:p>
</w:hdr>
'@

Write-Utf8 (Join-Path $packageRoot "word/footer1.xml") @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:ftr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:p>
    <w:pPr><w:pStyle w:val="AcceptanceFixed"/><w:jc w:val="center"/></w:pPr>
    <w:r><w:t>合成验收 · 第 </w:t></w:r>
    <w:fldSimple w:instr="PAGE"><w:r><w:t>1</w:t></w:r></w:fldSimple>
    <w:r><w:t> 页</w:t></w:r>
  </w:p>
</w:ftr>
'@

Write-Utf8 (Join-Path $packageRoot "word/styles.xml") @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:docDefaults>
    <w:rPrDefault>
      <w:rPr>
        <w:rFonts w:ascii="Arial" w:hAnsi="Arial" w:eastAsia="Microsoft YaHei"/>
        <w:sz w:val="21"/><w:szCs w:val="21"/>
        <w:lang w:val="en-US" w:eastAsia="zh-CN"/>
      </w:rPr>
    </w:rPrDefault>
    <w:pPrDefault>
      <w:pPr><w:spacing w:before="0" w:after="120" w:line="276" w:lineRule="auto"/></w:pPr>
    </w:pPrDefault>
  </w:docDefaults>

  <w:style w:type="paragraph" w:default="1" w:styleId="Normal">
    <w:name w:val="Normal"/><w:qFormat/>
    <w:pPr><w:spacing w:before="0" w:after="120" w:line="276" w:lineRule="auto"/></w:pPr>
    <w:rPr><w:rFonts w:ascii="Arial" w:hAnsi="Arial" w:eastAsia="Microsoft YaHei"/><w:sz w:val="21"/><w:szCs w:val="21"/></w:rPr>
  </w:style>
  <w:style w:type="paragraph" w:styleId="AcceptanceBody">
    <w:name w:val="合成验收正文"/><w:aliases w:val="验收正文"/><w:basedOn w:val="Normal"/><w:qFormat/>
    <w:pPr><w:spacing w:before="0" w:after="120" w:line="276" w:lineRule="auto"/><w:widowControl/></w:pPr>
    <w:rPr><w:rFonts w:ascii="Arial" w:hAnsi="Arial" w:eastAsia="Microsoft YaHei"/><w:sz w:val="21"/><w:szCs w:val="21"/></w:rPr>
  </w:style>
  <w:style w:type="paragraph" w:styleId="AcceptanceOrdered">
    <w:name w:val="合成验收有序列表"/><w:basedOn w:val="AcceptanceBody"/><w:qFormat/>
    <w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="42"/></w:numPr><w:spacing w:before="0" w:after="60" w:line="276" w:lineRule="auto"/></w:pPr>
  </w:style>
  <w:style w:type="paragraph" w:styleId="AcceptanceUnordered">
    <w:name w:val="合成验收无序列表"/><w:basedOn w:val="AcceptanceBody"/><w:qFormat/>
    <w:pPr><w:spacing w:before="0" w:after="60" w:line="276" w:lineRule="auto"/></w:pPr>
  </w:style>

  <w:style w:type="paragraph" w:styleId="AcceptanceHeading1">
    <w:name w:val="合成验收标题1"/><w:basedOn w:val="AcceptanceBody"/><w:next w:val="AcceptanceBody"/><w:qFormat/>
    <w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="240" w:after="120" w:line="288" w:lineRule="auto"/><w:outlineLvl w:val="0"/></w:pPr>
    <w:rPr><w:b/><w:color w:val="1F4E79"/><w:sz w:val="32"/><w:szCs w:val="32"/></w:rPr>
  </w:style>
  <w:style w:type="paragraph" w:styleId="AcceptanceHeading2">
    <w:name w:val="合成验收标题2"/><w:basedOn w:val="AcceptanceBody"/><w:next w:val="AcceptanceBody"/><w:qFormat/>
    <w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="200" w:after="80" w:line="276" w:lineRule="auto"/><w:outlineLvl w:val="1"/></w:pPr>
    <w:rPr><w:b/><w:color w:val="2F5597"/><w:sz w:val="28"/><w:szCs w:val="28"/></w:rPr>
  </w:style>
  <w:style w:type="paragraph" w:styleId="AcceptanceHeading3">
    <w:name w:val="合成验收标题3"/><w:basedOn w:val="AcceptanceBody"/><w:next w:val="AcceptanceBody"/><w:qFormat/>
    <w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="160" w:after="60" w:line="276" w:lineRule="auto"/><w:outlineLvl w:val="2"/></w:pPr>
    <w:rPr><w:b/><w:color w:val="365F91"/><w:sz w:val="24"/><w:szCs w:val="24"/></w:rPr>
  </w:style>
  <w:style w:type="paragraph" w:styleId="AcceptanceHeading4">
    <w:name w:val="合成验收标题4"/><w:basedOn w:val="AcceptanceBody"/><w:next w:val="AcceptanceBody"/><w:qFormat/>
    <w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="120" w:after="40" w:line="276" w:lineRule="auto"/><w:outlineLvl w:val="3"/></w:pPr>
    <w:rPr><w:b/><w:color w:val="404040"/><w:sz w:val="22"/><w:szCs w:val="22"/></w:rPr>
  </w:style>
  <w:style w:type="paragraph" w:styleId="AcceptanceHeading5">
    <w:name w:val="合成验收标题5"/><w:basedOn w:val="AcceptanceBody"/><w:next w:val="AcceptanceBody"/><w:qFormat/>
    <w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="80" w:after="40" w:line="276" w:lineRule="auto"/><w:outlineLvl w:val="4"/></w:pPr>
    <w:rPr><w:b/><w:color w:val="595959"/><w:sz w:val="21"/><w:szCs w:val="21"/></w:rPr>
  </w:style>
  <w:style w:type="paragraph" w:styleId="AcceptanceHeading6">
    <w:name w:val="合成验收标题6"/><w:basedOn w:val="AcceptanceBody"/><w:next w:val="AcceptanceBody"/><w:qFormat/>
    <w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="80" w:after="40" w:line="276" w:lineRule="auto"/><w:outlineLvl w:val="5"/></w:pPr>
    <w:rPr><w:b/><w:i/><w:color w:val="666666"/><w:sz w:val="21"/><w:szCs w:val="21"/></w:rPr>
  </w:style>

  <w:style w:type="paragraph" w:styleId="AcceptanceCaption">
    <w:name w:val="合成验收图题"/><w:basedOn w:val="AcceptanceBody"/><w:qFormat/>
    <w:pPr><w:keepLines/><w:spacing w:before="60" w:after="180" w:line="240" w:lineRule="auto"/><w:jc w:val="center"/></w:pPr>
    <w:rPr><w:color w:val="595959"/><w:sz w:val="19"/><w:szCs w:val="19"/></w:rPr>
  </w:style>
  <w:style w:type="paragraph" w:styleId="AcceptanceTableCaption">
    <w:name w:val="合成验收表题"/><w:basedOn w:val="AcceptanceBody"/><w:qFormat/>
    <w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="120" w:after="60" w:line="240" w:lineRule="auto"/><w:jc w:val="center"/></w:pPr>
    <w:rPr><w:b/><w:color w:val="404040"/><w:sz w:val="19"/><w:szCs w:val="19"/></w:rPr>
  </w:style>
  <w:style w:type="paragraph" w:styleId="AcceptanceCode">
    <w:name w:val="合成验收代码"/><w:aliases w:val="CodeBlock"/><w:basedOn w:val="AcceptanceBody"/><w:qFormat/>
    <w:pPr><w:keepLines/><w:spacing w:before="60" w:after="120" w:line="240" w:lineRule="auto"/><w:ind w:left="240" w:right="240"/><w:shd w:val="clear" w:fill="F2F4F7"/></w:pPr>
    <w:rPr><w:rFonts w:ascii="Consolas" w:hAnsi="Consolas" w:eastAsia="DengXian"/><w:sz w:val="18"/><w:szCs w:val="18"/></w:rPr>
  </w:style>
  <w:style w:type="paragraph" w:styleId="AcceptanceTableBody">
    <w:name w:val="合成验收表格正文"/><w:basedOn w:val="AcceptanceBody"/><w:qFormat/>
    <w:pPr><w:spacing w:before="0" w:after="0" w:line="240" w:lineRule="auto"/></w:pPr>
    <w:rPr><w:sz w:val="19"/><w:szCs w:val="19"/></w:rPr>
  </w:style>
  <w:style w:type="paragraph" w:styleId="AcceptanceAdmonition">
    <w:name w:val="合成验收引用"/><w:basedOn w:val="AcceptanceBody"/><w:qFormat/>
    <w:pPr><w:spacing w:before="0" w:after="120" w:line="276" w:lineRule="auto"/></w:pPr>
  </w:style>
  <w:style w:type="paragraph" w:styleId="AcceptanceFigureImage">
    <w:name w:val="合成验收图片段"/><w:basedOn w:val="AcceptanceBody"/><w:qFormat/>
    <w:pPr><w:keepNext/><w:spacing w:before="120" w:after="60" w:line="240" w:lineRule="auto"/><w:jc w:val="center"/></w:pPr>
  </w:style>
  <w:style w:type="paragraph" w:styleId="AcceptanceCoverTitle">
    <w:name w:val="合成验收封面标题"/><w:basedOn w:val="Normal"/>
    <w:pPr><w:spacing w:before="0" w:after="120" w:line="288" w:lineRule="auto"/><w:jc w:val="center"/></w:pPr>
    <w:rPr><w:b/><w:color w:val="1F4E79"/><w:sz w:val="48"/><w:szCs w:val="48"/></w:rPr>
  </w:style>
  <w:style w:type="paragraph" w:styleId="AcceptanceCoverSubtitle">
    <w:name w:val="合成验收封面副标题"/><w:basedOn w:val="Normal"/>
    <w:pPr><w:spacing w:before="0" w:after="240" w:line="276" w:lineRule="auto"/><w:jc w:val="center"/></w:pPr>
    <w:rPr><w:color w:val="595959"/><w:sz w:val="26"/><w:szCs w:val="26"/></w:rPr>
  </w:style>
  <w:style w:type="paragraph" w:styleId="AcceptanceFixed">
    <w:name w:val="合成验收固定区"/><w:basedOn w:val="Normal"/>
    <w:pPr><w:spacing w:before="0" w:after="60" w:line="240" w:lineRule="auto"/></w:pPr>
    <w:rPr><w:color w:val="7F7F7F"/><w:sz w:val="18"/><w:szCs w:val="18"/></w:rPr>
  </w:style>
</w:styles>
'@

Write-Utf8 (Join-Path $packageRoot "word/numbering.xml") @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:abstractNum w:abstractNumId="42">
    <w:nsid w:val="A11CE042"/><w:multiLevelType w:val="multilevel"/><w:tmpl w:val="A11CE042"/>
    <w:lvl w:ilvl="0">
      <w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1）"/><w:lvlJc w:val="left"/>
      <w:pPr><w:tabs><w:tab w:val="num" w:pos="720"/></w:tabs><w:ind w:left="720" w:hanging="360"/></w:pPr>
      <w:rPr><w:rFonts w:ascii="Arial" w:hAnsi="Arial" w:eastAsia="Microsoft YaHei"/></w:rPr>
    </w:lvl>
    <w:lvl w:ilvl="1">
      <w:start w:val="1"/><w:numFmt w:val="lowerLetter"/><w:lvlText w:val="%1.%2)"/><w:lvlJc w:val="left"/>
      <w:pPr><w:tabs><w:tab w:val="num" w:pos="1080"/></w:tabs><w:ind w:left="1080" w:hanging="360"/></w:pPr>
    </w:lvl>
    <w:lvl w:ilvl="2">
      <w:start w:val="1"/><w:numFmt w:val="lowerRoman"/><w:lvlText w:val="%1.%2.%3)"/><w:lvlJc w:val="left"/>
      <w:pPr><w:tabs><w:tab w:val="num" w:pos="1440"/></w:tabs><w:ind w:left="1440" w:hanging="360"/></w:pPr>
    </w:lvl>
  </w:abstractNum>
  <w:num w:numId="42"><w:abstractNumId w:val="42"/></w:num>
</w:numbering>
'@

Write-Utf8 (Join-Path $packageRoot "word/document.xml") @'
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
            xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <w:body>
    <w:p>
      <w:pPr><w:pStyle w:val="AcceptanceCoverTitle"/></w:pPr>
      <w:bookmarkStart w:id="10" w:name="MANUAL_COVER_TITLE"/>
      <w:r><w:t>复杂合成验收标题</w:t></w:r>
      <w:bookmarkEnd w:id="10"/>
    </w:p>
    <w:p>
      <w:pPr><w:pStyle w:val="AcceptanceCoverSubtitle"/></w:pPr>
      <w:bookmarkStart w:id="11" w:name="MANUAL_COVER_SUBTITLE"/>
      <w:r><w:t>只含公开合成内容 · 非业务文档</w:t></w:r>
      <w:bookmarkEnd w:id="11"/>
    </w:p>
    <w:p><w:pPr><w:pStyle w:val="AcceptanceFixed"/></w:pPr><w:r><w:t>[FIXED-BEFORE] 模板固定区：转换后必须保持原样。</w:t></w:r></w:p>

    <w:bookmarkStart w:id="30" w:name="MANUAL_TABLE_VERSION_HISTORY"/>
    <w:tbl>
      <w:tblPr>
        <w:tblW w:w="9026" w:type="dxa"/>
        <w:tblBorders>
          <w:top w:val="single" w:sz="4" w:color="808080"/>
          <w:left w:val="single" w:sz="4" w:color="808080"/>
          <w:bottom w:val="single" w:sz="4" w:color="808080"/>
          <w:right w:val="single" w:sz="4" w:color="808080"/>
          <w:insideH w:val="single" w:sz="4" w:color="D9D9D9"/>
          <w:insideV w:val="single" w:sz="4" w:color="D9D9D9"/>
        </w:tblBorders>
      </w:tblPr>
      <w:tblGrid><w:gridCol w:w="1500"/><w:gridCol w:w="1800"/><w:gridCol w:w="5726"/></w:tblGrid>
      <w:tr>
        <w:trPr><w:tblHeader/></w:trPr>
        <w:tc><w:tcPr><w:tcW w:w="1500" w:type="dxa"/></w:tcPr><w:p><w:pPr><w:pStyle w:val="AcceptanceTableBody"/></w:pPr><w:r><w:t>版本</w:t></w:r></w:p></w:tc>
        <w:tc><w:tcPr><w:tcW w:w="1800" w:type="dxa"/></w:tcPr><w:p><w:pPr><w:pStyle w:val="AcceptanceTableBody"/></w:pPr><w:r><w:t>日期</w:t></w:r></w:p></w:tc>
        <w:tc><w:tcPr><w:tcW w:w="5726" w:type="dxa"/></w:tcPr><w:p><w:pPr><w:pStyle w:val="AcceptanceTableBody"/></w:pPr><w:r><w:t>说明</w:t></w:r></w:p></w:tc>
      </w:tr>
      <w:tr>
        <w:tc><w:p><w:pPr><w:pStyle w:val="AcceptanceTableBody"/></w:pPr><w:r><w:t>template</w:t></w:r></w:p></w:tc>
        <w:tc><w:p><w:pPr><w:pStyle w:val="AcceptanceTableBody"/></w:pPr><w:r><w:t>2026-07-20</w:t></w:r></w:p></w:tc>
        <w:tc><w:p><w:pPr><w:pStyle w:val="AcceptanceTableBody"/></w:pPr><w:r><w:t>待 Front Matter 更新</w:t></w:r></w:p></w:tc>
      </w:tr>
    </w:tbl>
    <w:bookmarkEnd w:id="30"/>

    <w:p><w:pPr><w:pStyle w:val="AcceptanceHeading2"/></w:pPr><w:r><w:t>[CTRL-H2] 模板原生间距控制区</w:t></w:r></w:p>
    <w:p><w:pPr><w:pStyle w:val="AcceptanceBody"/></w:pPr><w:r><w:t>[CTRL-BODY-1] 合成正文基准行一</w:t></w:r></w:p>
    <w:p><w:pPr><w:pStyle w:val="AcceptanceBody"/></w:pPr><w:r><w:t>[CTRL-BODY-2] 合成正文基准行二</w:t></w:r></w:p>
    <w:p><w:pPr><w:pStyle w:val="AcceptanceCode"/></w:pPr><w:r><w:t>[CTRL-CODE] template_native_spacing_control();</w:t></w:r></w:p>
    <w:p><w:pPr><w:pStyle w:val="AcceptanceFixed"/></w:pPr><w:r><w:br w:type="page"/></w:r></w:p>

    <w:p><w:bookmarkStart w:id="1" w:name="MANUAL_BODY_START"/><w:bookmarkEnd w:id="1"/></w:p>
    <w:p><w:pPr><w:pStyle w:val="AcceptanceBody"/></w:pPr><w:r><w:t>synthetic placeholder must be removed</w:t></w:r></w:p>
    <w:p><w:bookmarkStart w:id="2" w:name="MANUAL_BODY_END"/><w:bookmarkEnd w:id="2"/></w:p>

    <w:p><w:pPr><w:pStyle w:val="AcceptanceFixed"/></w:pPr><w:r><w:t>[FIXED-AFTER] 模板固定尾部：不得被正文样式污染。</w:t></w:r></w:p>
    <w:tbl>
      <w:tblPr><w:tblW w:w="4200" w:type="dxa"/></w:tblPr>
      <w:tblGrid><w:gridCol w:w="2100"/><w:gridCol w:w="2100"/></w:tblGrid>
      <w:tr>
        <w:tc><w:p><w:pPr><w:pStyle w:val="AcceptanceFixed"/></w:pPr><w:r><w:t>固定键</w:t></w:r></w:p></w:tc>
        <w:tc><w:p><w:pPr><w:pStyle w:val="AcceptanceFixed"/></w:pPr><w:r><w:t>固定值</w:t></w:r></w:p></w:tc>
      </w:tr>
    </w:tbl>
    <w:sectPr>
      <w:headerReference w:type="default" r:id="rIdHeader1"/>
      <w:footerReference w:type="default" r:id="rIdFooter1"/>
      <w:pgSz w:w="11906" w:h="16838"/>
      <w:pgMar w:top="1417" w:right="1440" w:bottom="1417" w:left="1440" w:header="708" w:footer="708" w:gutter="0"/>
      <w:cols w:space="425"/>
    </w:sectPr>
  </w:body>
</w:document>
'@

$templatePath = Join-Path $targetRoot "synthetic-comprehensive-template.docx"
New-DocxArchive $packageRoot $templatePath
Remove-Item -LiteralPath $packageRoot -Recurse -Force

$cssPath = Join-Path $targetRoot "synthetic-comprehensive-style.css"
Write-Utf8 $cssPath @'
/* Synthetic comprehensive acceptance mapping. No business styles or content. */
p.manual-body-paragraph { mso-style-name: "合成验收正文"; }
li.manual-body-ordered-item,
p.manual-body-ordered-item-paragraph { mso-style-name: "合成验收有序列表"; }
li.manual-body-unordered-item,
p.manual-body-unordered-item-paragraph { mso-style-name: "合成验收无序列表"; }
h1 { mso-style-name: "合成验收标题1"; }
h2 { mso-style-name: "合成验收标题2"; }
h3 { mso-style-name: "合成验收标题3"; }
h4 { mso-style-name: "合成验收标题4"; }
h5 { mso-style-name: "合成验收标题5"; }
h6 { mso-style-name: "合成验收标题6"; }
p.manual-figure-caption,
figcaption { mso-style-name: "合成验收图题"; }
p.manual-table-caption,
table > caption { mso-style-name: "合成验收表题"; }
p.manual-code-block-paragraph { mso-style-name: "合成验收代码"; }
code.manual-inline-code { mso-style-name: "合成验收正文"; }
p.manual-table-paragraph { mso-style-name: "合成验收表格正文"; }
p.manual-admonition-paragraph { mso-style-name: "合成验收引用"; }
p.manual-figure-image-paragraph { mso-style-name: "合成验收图片段"; }
'@

$assetsRoot = Join-Path $targetRoot "assets"
New-Item -ItemType Directory -Path $assetsRoot -Force | Out-Null
$wideImagePath = Join-Path $assetsRoot "合成超宽图.png"
$smallImagePath = Join-Path $assetsRoot "synthetic-small.png"
New-SyntheticPng $wideImagePath 1800 420 "SYNTHETIC WIDE IMAGE 1800x420" "#2F5597"
New-SyntheticPng $smallImagePath 360 200 "SYNTHETIC SMALL IMAGE" "#B45309"

$markdown = [System.Text.StringBuilder]::new()
[void]$markdown.AppendLine(@'
---
title: 复杂合成链路验收
subtitle: 只含公开合成内容
heading_base_level: 1
heading_numbering: true
heading_numbering_start_base: 1
word_repeat_table_headers: true
figure_captions: true
word_heading_numbering:
  level1:
    number_style: decimal
    format: "%1."
  level2:
    number_style: decimal
    format: "%1.%2"
  level3:
    number_style: lower_letter
    format: "%1.%2.%3"
  level4:
    number_style: lower_roman
    format: "%1.%2.%3.%4"
manul_version_tables:
  - bookmark: MANUAL_TABLE_VERSION_HISTORY
    column_keys: [version, date, description]
    rows:
      - version: "0.1"
        date: "2026-07-20"
        description: "复杂合成验收初始记录"
      - version: "0.2"
        date: "2026-07-20"
        description: "第二行纯合成说明"
---

# [DOM-H1] 综合间距与结构验收

[DOM-BODY-1] 合成正文基准行一

[DOM-BODY-2] 合成正文基准行二

[DOM-RICH] 这一段同时包含**粗体**、*斜体*、`inline_code()`、实体符号 & 与 < 的转义结果，以及[跳转到内部目标](#内部链接目标)。

[DOM-LINK-2] 第二个链接仍然[跳转到同一内部目标](#内部链接目标)，用于验证共享 ASCII 安全书签。

## [DOM-H2] 标题层级

### [DOM-H3] 三级标题

#### [DOM-H4] 四级标题

##### [DOM-H5] 五级标题

###### [DOM-H6] 六级标题

[DOM-HEADING-BODY] 六级标题后的正文用于检查模板标题 keep 与段距。

## [DOM-LINK-TARGET] 内部链接目标 {#内部链接目标}

[DOM-LINK-BODY] 内部链接目标正文只出现一次。

## [DOM-LIST] 原生列表矩阵

- [UL-DASH] 连字符一级项目
  - [UL-DASH-L2] 二级无序项目
    1. [UL-MIXED-L3] 三级有序项目
* [UL-STAR] 星号一级项目
+ [UL-PLUS] 加号一级项目

3. [OL-START-3] 非一开始的有序项目
4. [OL-START-4] 第二个有序项目
   - [OL-UL-L2] 有序中的二级无序
     1. [OL-UL-OL-L3] 三级有序
5. [OL-START-5] 回到一级

1. [LOOSE-1] 宽松列表第一项

   [LOOSE-CONT] 同一条目的第二段，不应产生伪编号。

2. [LOOSE-2] 宽松列表第二项，包含代码：

   ```text
   [LIST-CODE] nested_code();
   ```

[LIST-SEPARATOR] 普通正文分隔两个列表实例。

1. [RESTART-1] 分离后重新从一开始
2. [RESTART-2] 分离列表第二项

## [DOM-CODE] 代码空白语义

'@)
[void]$markdown.AppendLine('```text')
[void]$markdown.AppendLine('[CODE-L1] first  line has two spaces')
[void]$markdown.AppendLine('')
[void]$markdown.AppendLine('    [CODE-L3] four-space indent')
[void]$markdown.AppendLine("`t[CODE-L4] tab-indented line")
[void]$markdown.AppendLine('[CODE-L5] final line')
[void]$markdown.AppendLine('```')
[void]$markdown.AppendLine('')
[void]$markdown.AppendLine(@'
## [DOM-QUOTE] 引用与 admonition

> [QUOTE-GENERIC] 普通引用段使用灰色基础配色。

> NOTE: [QUOTE-NOTE] English note label.
>
> [QUOTE-NOTE-2] 同一 NOTE 的第二段保持相同语义。
>
> - [QUOTE-NOTE-LIST] 引用内列表保持列表角色。

> 注意：[QUOTE-CAUTION] 中文注意标签。

> WARNING: [QUOTE-WARNING] English warning label.

> 危险：[QUOTE-DANGER] 中文危险标签。
>
> ```text
> [QUOTE-CODE] quoted_code();
> ```

## [DOM-TABLE] 表格矩阵

Table: [TABLE-CAPTION-1] 普通两列表

| 键 | 说明 |
|---|---|
| [T2-K1] Alpha | 两列表第一行用于检查 24% / 76% |
| [T2-K2] Beta | 单元格包含 `table_inline_code()` |

### [DOM-ADJACENT-TABLES] 三张相邻独立表

| A | B |
|---|---|
| [ADJ-1A] | 第一张表 |

| A | B |
|---|---|
| [ADJ-2A] | 第二张表 |

| A | B |
|---|---|
| [ADJ-3A] | 第三张表 |

### [DOM-MERGED-TABLE] 规则 HTML 合并表

<table>
  <caption>[TABLE-CAPTION-MERGED] 规则纵横合并表</caption>
  <thead>
    <tr><th>类别</th><th>项目</th><th>结果</th></tr>
  </thead>
  <tbody>
    <tr><td rowspan="2">[MERGE-V] A</td><td>[MERGE-R1] 项目一</td><td>通过</td></tr>
    <tr><td colspan="2">[MERGE-H] 横向合并内容</td></tr>
  </tbody>
</table>

### [DOM-LONG-TABLE] 跨页长表

| 序号 | 合成标识 | 说明 |
|---:|---|---|
'@)
1..56 | ForEach-Object {
    [void]$markdown.AppendLine(
        "| $_ | [LONG-$('{0:D2}' -f $_)] | 第 $('{0:D2}' -f $_) 行纯合成长表内容，用于重复表头与分页检查。 |"
    )
}
[void]$markdown.AppendLine('')
[void]$markdown.AppendLine(@'
## [DOM-IMAGE] 本地图片、非 ASCII 路径与图题

![SYNTHETIC-WIDE-CAPTION 合成超宽图](assets/合成超宽图.png "synthetic wide image")

![SYNTHETIC-SMALL-CAPTION 合成小图](assets/synthetic-small.png "synthetic small image")

## [DOM-MERMAID] Mermaid 成功、兼容与降级

```mermaid
sequenceDiagram
  participant A as Device
  participant B as Service
  A->>B: Synthetic request
  B-->>A: Synthetic response
```
<!-- caption: MERMAID-CAPTION-OK 合成设备时序 -->

```mermaid
flowchart LR
  IN[Input] --> DOM[DOM normalize]
  DOM --> WORD[Word]
```
<!-- cation: MERMAID-CATION-COMPAT 合成兼容拼写流程 -->

```mermaid
this is intentionally invalid mermaid syntax {{{
```
<!-- caption: MERMAID-INVALID-CAPTION 不应成为孤立图题 -->

## [DOM-END] 结束与固定区边界

[DOM-END-BODY] 正文最后一个合成段落；其后应直接进入模板固定尾部，不应泄漏占位文字。
'@)

$markdownPath = Join-Path $targetRoot "synthetic-comprehensive.md"
Write-Utf8 $markdownPath $markdown.ToString()

$requiredFailurePath = Join-Path $targetRoot "synthetic-mermaid-required-failure.md"
Write-Utf8 $requiredFailurePath @'
---
title: 合成 Mermaid required 失败验收
---

# Required 失败不发布成品

```mermaid
this is intentionally invalid mermaid syntax {{{
```
<!-- caption: required 模式失败图 -->
'@

$expectationsPath = Join-Path $targetRoot "synthetic-expectations.json"
$expectations = [ordered]@{
    schemaVersion = 1
    synthetic = $true
    purpose = "MD2Word comprehensive acceptance; contains no business data"
    expectedBookmarks = @(
        "MANUAL_COVER_TITLE",
        "MANUAL_COVER_SUBTITLE",
        "MANUAL_TABLE_VERSION_HISTORY",
        "MANUAL_BODY_START",
        "MANUAL_BODY_END"
    )
    requiredVisibleExactlyOnce = @(
        "[FIXED-BEFORE]",
        "[CTRL-BODY-1]",
        "[CTRL-BODY-2]",
        "[FIXED-AFTER]",
        "[DOM-BODY-1]",
        "[DOM-BODY-2]",
        "[DOM-LINK-TARGET]",
        "[DOM-END-BODY]",
        "[MERGE-V]",
        "[MERGE-H]",
        "[LONG-56]",
        "SYNTHETIC-WIDE-CAPTION",
        "MERMAID-CAPTION-OK"
    )
    forbiddenVisibleFragments = @(
        "synthetic placeholder must be removed",
        "__MD2WORD_ROLE_",
        "md2word-role-marker",
        "<html",
        "<body",
        "<table",
        "<tr",
        "<td",
        "class=",
        "style=",
        "<!DOCTYPE"
    )
    forbiddenReferencedStyleFragments = @(
        "HTML Code",
        "HTML 代码",
        "manual-",
        "md2word-"
    )
    styles = [ordered]@{
        body = [ordered]@{
            id = "AcceptanceBody"
            name = "合成验收正文"
            beforePt = 0
            afterPt = 6
            lineMultiple = 1.15
        }
        ordered = [ordered]@{
            id = "AcceptanceOrdered"
            name = "合成验收有序列表"
            beforePt = 0
            afterPt = 3
            lineMultiple = 1.15
        }
        unordered = [ordered]@{
            id = "AcceptanceUnordered"
            name = "合成验收无序列表"
            beforePt = 0
            afterPt = 3
            lineMultiple = 1.15
        }
        code = [ordered]@{
            id = "AcceptanceCode"
            name = "合成验收代码"
            beforePt = 3
            afterPt = 6
            lineMultiple = 1.0
        }
        table = [ordered]@{
            id = "AcceptanceTableBody"
            name = "合成验收表格正文"
            beforePt = 0
            afterPt = 0
            lineMultiple = 1.0
        }
        admonition = [ordered]@{
            id = "AcceptanceAdmonition"
            name = "合成验收引用"
            beforePt = 0
            afterPt = 6
            lineMultiple = 1.15
        }
    }
    spacingControlPairs = @(
        [ordered]@{
            name = "body"
            control = @("[CTRL-BODY-1]", "[CTRL-BODY-2]")
            imported = @("[DOM-BODY-1]", "[DOM-BODY-2]")
            maxDeltaDifferencePt = 2
        }
    )
    expectedMinimums = [ordered]@{
        pages = 6
        tables = 7
        images = 4
        paragraphs = 100
        longTableRows = 57
    }
    expectedAdjacentTableSeparators = 2
    expectedMermaidAutoWarning = "MERMAID_"
    expectedCodeLines = @(
        "[CODE-L1] first  line has two spaces",
        "",
        "    [CODE-L3] four-space indent",
        "    [CODE-L4] tab-indented line",
        "[CODE-L5] final line"
    )
}
Write-Utf8 $expectationsPath ($expectations | ConvertTo-Json -Depth 8)

[pscustomobject]@{
    scenario = "Comprehensive"
    template = $templatePath
    css = $cssPath
    markdown = $markdownPath
    requiredFailureMarkdown = $requiredFailurePath
    expectations = $expectationsPath
    assets = @($wideImagePath, $smallImagePath)
} | ConvertTo-Json -Compress
