# Third-party components

WpfStudio combines the following packages. Their licenses remain with their respective authors. The publish script includes license files from the resolved NuGet packages in `licenses/` and writes the complete direct/transitive package inventory to `THIRD-PARTY-PACKAGES.json`.

| Component | Version | Project / license information |
| --- | --- | --- |
| AvalonEdit | 6.3.1.120 | [ICSharpCode/AvalonEdit](https://github.com/icsharpcode/AvalonEdit), MIT |
| AvalonDock and docking support packages | 5.0.0 | [Dirkster99/AvalonDock](https://github.com/Dirkster99/AvalonDock), Microsoft Public License (Ms-PL), as included in the package LICENSE files |
| CommunityToolkit.Mvvm | 8.4.2 | [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet), MIT |
| Microsoft.CodeAnalysis / Roslyn | 5.9.0 | [dotnet/roslyn](https://github.com/dotnet/roslyn), MIT |
| Microsoft.Build.Locator | 1.11.2 | [microsoft/MSBuildLocator](https://github.com/microsoft/MSBuildLocator), MIT |
| StreamJsonRpc | 2.25.29 | [microsoft/vs-streamjsonrpc](https://github.com/microsoft/vs-streamjsonrpc), MIT |
| Microsoft.Extensions libraries | 10.0.12 and transitive versions | [dotnet/runtime](https://github.com/dotnet/runtime), MIT |
| Microsoft.Data.SqlClient | 7.1.0 | [dotnet/SqlClient](https://github.com/dotnet/SqlClient), MIT |
| Microsoft.Web.WebView2 SDK | 1.0.4191.47 | [Microsoft WebView2](https://developer.microsoft.com/microsoft-edge/webview2/), Microsoft software license terms included in the SDK package |
| netcoredbg | 3.2.0-1092 | [Samsung/netcoredbg](https://github.com/Samsung/netcoredbg), MIT; see [debugger notices](tools/DEBUGGER-NOTICES.md) |
| xterm.js | 6.0.0 | [xtermjs/xterm.js](https://github.com/xtermjs/xterm.js), MIT |
| xterm fit / search addons | 0.11.0 / 0.16.0 | [terminal notices](src/WpfStudio.Runtime/Assets/Terminal/THIRD-PARTY-NOTICES.md), MIT |

The terminal assets carry their original license files next to the bundled JavaScript. The downloaded debugger distribution carries its own license and notices for bundled Roslyn and .NET components. The Microsoft Edge WebView2 runtime is installed separately and is not redistributed in the portable package. The .NET SDK and SQL Server are also separate installations.

Test dependencies such as xUnit, Microsoft.NET.Test.Sdk, and coverlet are development-only and are not included in the published app. This document records component provenance; authoritative terms are the original package license files and metadata shipped with the package.
