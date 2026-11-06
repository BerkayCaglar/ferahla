# Third-party notices

## kondo

The project-marker → build-folder table in `rules/build.json` is based on the project types supported by
[kondo](https://github.com/tbillington/kondo) (MIT License, Copyright (c) 2020 Trent Billington).
Ferahla's own additions: Unity projects are recognised by `Assets/` + `ProjectSettings/`, Unity `Build/Builds`
folders are a separate review-level rule, and Unreal `Saved/` is left alone.

## Fluent UI Web Components

`spikes/09-fluent-offline/vendor/` contains `@fluentui/web-components` and a theme generated from `@fluentui/tokens`
(MIT License, Copyright (c) Microsoft Corporation). See `spikes/09-fluent-offline/vendor/NOTICE.md`.

## Libraries compiled into ferahla.exe

| Package | License |
|---|---|
| .NET runtime, ASP.NET Core, Microsoft.Extensions.* | MIT, Copyright (c) .NET Foundation and Contributors |
| [System.CommandLine](https://github.com/dotnet/command-line-api) | MIT, Copyright (c) .NET Foundation and Contributors |
| [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) (C# MCP SDK) | Apache License 2.0, © Model Context Protocol a Series of LF Projects, LLC |

The Apache License 2.0 text: https://www.apache.org/licenses/LICENSE-2.0. The SDK is used unmodified.

## Ideas (no code or data copied)

- [npkill](https://github.com/voidcosmos/npkill) profiles: "what breaks / how to restore" per target.
- [BleachBit CleanerML](https://github.com/bleachbit/cleanerml): per-option warnings, pending/release maturity, schema validation.
  BleachBit is GPL; none of its rules are copied.
- [Czkawka](https://github.com/qarmin/czkawka): duplicate detection by size → partial hash → full hash.
