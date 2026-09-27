WpfStudio uses Samsung netcoredbg 3.2.0-1092, licensed under MIT. The pinned Windows x64 release is installed by `Get-Debugger.ps1` after SHA-256 verification and copied into build output. The installation script retrieves its license alongside the complete distribution. WpfStudio does not use or redistribute vsdbg.

- Source and license: https://github.com/Samsung/netcoredbg/tree/3.2.0-1092
- Release: https://github.com/Samsung/netcoredbg/releases/tag/3.2.0-1092
- SHA-256 of `netcoredbg-win64.zip`: `3c410a45fa502415203a94fcb88654af65bf8e3dac158a5527a722e7a6b9274a`

The archive includes Microsoft's open-source Roslyn assemblies and the .NET debug shim. The adapter also uses nlohmann/json and linenoise-ng. Their licenses are included separately by the installer. Runtime compatibility must be verified for supported runtime/architecture combinations; the runtime test project exercises real Windows x64 WPF dispatcher fixtures on .NET 8, 9 and 10. Windows x86/ARM64, .NET Framework, native mixed debugging and hot reload are not covered.
