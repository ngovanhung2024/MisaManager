# Native dependency inventory

Resolved package lock files are authoritative for versions and hashes. This
inventory was refreshed on 2026-09-26 after retirement of Python/model artifacts.

| Component | Version | License and notice source |
|---|---|---|
| DocumentFormat.OpenXml / Framework | 3.5.1 | MIT; OpenXML-LICENSE.txt from upstream commit 3139fdfd27414548a41555f7848d5728f6e71a42 |
| FlaUI.Core / UIA3 | 5.0.0 | MIT; FlaUI-LICENSE.txt |
| Microsoft.Windows.CsWin32 | 0.3.335 | MIT; package NOTICE.txt and generated bindings |
| Interop.UIAutomationClient | 10.19041.0 | UIAutomationInterop-LICENSE.txt from the resolved NuGet package |
| Microsoft/System transitive packages | packages.lock.json | native-packages/<package>/<version>/ contains package LICENSE and available THIRD-PARTY-NOTICES |
| .NET self-contained runtime | 10.0.12 | DotNetRuntime-LICENSE.txt and DotNetRuntime-NOTICES.txt from Microsoft.NETCore.App.Runtime.win-x64 |
| Windows Desktop runtime | 10.0.12 | WindowsDesktopRuntime-LICENSE.txt from the resolved runtime package |
| WiX SDK / Heat / UI extension | 4.0.6 | MS-RL; WiX-LICENSE.txt from upstream commit 73c897383236ddbbbc6ba257634013c1269ceec2 |

WiX is a build dependency; the UI extension contributes to the MSI. No WiX source
is modified. Its source is https://github.com/wixtoolset/wix at the commit above.
Open XML source is https://github.com/dotnet/Open-XML-SDK at the recorded commit.
No Python/OCR/model package is part of the native release. The release pipeline
copies this directory into the MSI notices payload and hashes the shipped files.
