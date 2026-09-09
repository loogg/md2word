# Public template source catalog

For a step-by-step Word bookmark, style, and CSS walkthrough, see the
[template authoring guide](../docs/MD2Word-模板制作指南.md). Copy both DOCX and CSS
before editing; the reference profile uses its own custom CSS mapping.

This directory mirrors the runtime `templates` catalog. Every direct child is
a self-contained public template package with this structure:

```text
<package-name>/
  index.json
  <template-id>/
    template.docx
    style.css
```

`npm run package:win` validates every package index and every indexed DOCX/CSS
pair with the published Worker, then copies only those indexed files into the
public release. It does not generate or rewrite package metadata.

To add another public package, copy a complete package into this directory and
use globally unique template IDs. Only synthetic, non-business, publicly
distributable assets may be stored here. Private templates, customer
documents, conversion outputs, and business content must remain outside the
repository; copy a private package beside the packaged executable as
`templates/<package-name>/` after packaging instead.
