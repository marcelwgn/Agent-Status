# Agent Status - Taskbar Tray

> Initial idea by [@niels9001](https://github.com/niels9001).

A lightweight Windows app that surfaces the status of your AI coding agents (GitHub Copilot CLI, Claude Code, and more) directly in the taskbar tray, so you can see at a glance when an agent is working, waiting on input, or done.

> **Note:** Parts of this project were "vibe coded" with the help of AI coding agents. Expect the occasional rough edge — bug reports and PRs are welcome.

## Features

- Live status for active agent sessions in the system tray
- Support for GitHub Copilot CLI and Claude Code sessions
- Optional taskbar band integration
- Command Palette extension for [Microsoft Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview) (`AgentStatus.CmdPal`)

## Requirements

- Windows 10 (build 19041) or later, x64 or ARM64
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (for building from source)
- Windows App SDK runtime (installed automatically with the MSIX package)

## Privacy

Agent Status reads agent session metadata from local files only (e.g.
GitHub Copilot CLI session logs and Claude Code session files in your user profile).
It does not make network calls and does not transmit any data off your machine.

## Building from source

```powershell
dotnet build AgentStatus.slnx -p:Platform=x64
dotnet test AgentStatus.Core.Tests/AgentStatus.Core.Tests.csproj -p:Platform=x64
```

## Packaging and installing locally

The `scripts/` folder contains helper scripts for producing local MSIX
packages signed with a **30-day self-signed certificate** (intended for
personal/dev use, not for distribution to others):

- `scripts/publish.ps1` – builds and signs MSIX packages and exports
  the matching `.cer` to `publish/`.
- `scripts/install.ps1` – installs the exported certificate into the
  local machine `Trusted People` store so the MSIX can be sideloaded.

For Microsoft Store submission, `scripts/create-winui-msixbundle.ps1`
and `scripts/create-cmdpal-msixbundle.ps1` produce unsigned bundles
(the Store re-signs them).

## Contributing

Issues and pull requests are welcome. Please run the build and tests
above before opening a PR.

## License

MIT — see [LICENSE](LICENSE).
