# Agent Status - Taskbar Tray

> Initial idea by [@niels9001](https://github.com/niels9001).

A lightweightWindows app that surfaces the status of your AI coding agents (GitHub Copilot CLI, Claude Code, and more) directly in the taskbar tray, so you can see at a glance when an agent is working, waiting on input, or done.

## Features

- Live status for active agent sessions in the system tray
- Support for GitHub Copilot CLI and Claude Code sessions
- Optional taskbar band integration
- Command Palette extension (`AgentStatus.CmdPalExtension`)

## Building

```powershell
dotnet build AgentStatus.slnx -p:Platform=x64
dotnet test AgentStatus.Core.Tests/AgentStatus.Core.Tests.csproj -p:Platform=x64
```

## License

See [LICENSE](LICENSE).
