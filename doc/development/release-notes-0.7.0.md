MD2Word 0.7.0 brings a Windows 11 Fluent desktop interface and an About page that checks the latest official GitHub Release. Updates remain manual: review the release notes, download the appropriate package, then close MD2Word before installing or replacing a Portable directory.

The Word finalization step now centers figure captions beneath their images while retaining the template's resolved caption style. A synthetic Markdown → Electron → C# Worker → Word conversion and Word-exported PDF were reviewed locally.

Windows x64 downloads include a Portable ZIP and Setup installer. Setup retains user-managed templates across reinstall and upgrade; a local 0.6.1 → 0.7.0 upgrade was verified with unchanged template-file hashes and default selection. Microsoft Word desktop and Pandoc are required for conversion. Packages are unsigned. GitHub Actions compiles and tests without Word COM; the real Word and installer lifecycle checks were completed separately on a Windows host.
