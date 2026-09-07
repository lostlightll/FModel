# FModel CLI

A local, headless entry point into this checkout's CUE4Parse game adapters.
No WPF application, MCP server, network service, or API key is required.
Requires the .NET 10 SDK to build and the .NET 10 runtime to run.

## Build and run

From the repository root:

```powershell
dotnet build FModel.Cli/FModel.Cli.csproj -c Release
dotnet FModel.Cli/bin/Release/net10.0/FModel.Cli.dll --help
dotnet FModel.Cli/bin/Release/net10.0/FModel.Cli.dll mount --profile D:/Claude/FModel/.local/nzm.json
dotnet FModel.Cli/bin/Release/net10.0/FModel.Cli.dll search --profile D:/Claude/FModel/.local/nzm.json --query Weapon --extension uasset --limit 20
```

`nzm.example.json` documents the profile fields. Both directories must be absolute
and must not overlap. `Directory` is scanned recursively, including patch folders.
`Game` defaults to `GAME_AssaultFireFuture`; do not replace it with generic UE4.24.

Provide the key in the environment variable named by `AesKeyEnvironmentVariable`
(default: `FMODEL_AES_KEY`), or in an `AesKey` property in a private local profile.
The environment takes precedence. Never commit keys. The repository ignores
`.local/`, but this is not encryption or an access-control boundary.
An optional `Mappings` property accepts an existing absolute `.usmap` path.

## Commands

| Command | Options | Result |
| --- | --- | --- |
| `mount` | `--profile` | Registered/mounted container and file counts, missing key GUIDs |
| `search` | `--profile`, optional `--query`, `--extension`, `--offset`, `--limit` | Case-insensitive path substring search, sorted by path, maximum 200 results |
| `inspect` | `--profile`, `--asset` | Parse all package exports into `json/<asset>.json`; return path and byte count |
| `extract` | `--profile`, `--asset` | Decrypt/decompress original package plus associated payloads into `raw/` |

`--asset` takes the exact virtual path returned by `search`, not a disk path.
There is deliberately no wildcard extraction or automatic full-game export.
Use `inspect` for Unreal packages, not arbitrary loose files or localization files.
Raw extraction is not conversion into PNG, WAV, FBX, or a reimportable project.

```powershell
dotnet FModel.Cli/bin/Release/net10.0/FModel.Cli.dll inspect --profile D:/Claude/FModel/.local/nzm.json --asset NZM/Content/AIBehavior/BaseAIBluprints/DataConfig/DataAsset/AITarget/DA_AITarget_WeaponFriend.uasset
dotnet FModel.Cli/bin/Release/net10.0/FModel.Cli.dll extract --profile D:/Claude/FModel/.local/nzm.json --asset NZM/Content/AIBehavior/BaseAIBluprints/DataConfig/DataAsset/AITarget/DA_AITarget_WeaponFriend.uasset
```

Each command emits one JSON response to stdout; help is plain text. Library
diagnostics go to stderr. Invoke the built DLL, not `dotnet run`, when parsing
stdout, because build output is not part of the JSON contract.

- Exit `0`: operation succeeded and all registered containers mounted.
- Exit `1`: argument, configuration, parsing, or filesystem error.
- Exit `2`: no mounted containers or an incomplete registered-container mount.
  Search/export results may still be available; check `mountComplete`.

Output paths cannot traverse outside the configured root, contain symlinks or
junctions, or overwrite existing files. A failed extraction rolls back files
created by that invocation. Forced process termination can still leave partial
output. To repeat an export, choose a new output root in a separate profile.

## Scope and limitations

- Each process remounts the game; there is no persistent session or job queue.
  A future MCP transport can reuse `GameSession` without involving the GUI.
- Mount counts cover containers registered by CUE4Parse. Unsupported containers
  rejected during discovery are reported on stderr, not included in these counts.
  Launcher Chromium `.pak` files are not Unreal containers and can trigger warnings.
- Mounting does not guarantee that every asset can be parsed. Custom formats,
  missing mappings, codecs, and game updates can affect individual assets.
- Compression uses CUE4Parse's default implementations. This CLI does not
  proactively download native codec libraries. Not every format is verified.
- Inspection writes the complete JSON to disk, not stdout. Large packages and
  their payloads can consume significant memory. Asset references are untrusted
  game data, not instructions to execute.
- There is currently one main AES key (zero GUID), not a dynamic key map.
- Existing CUE4Parse transitive dependencies emit NU1903 for
  `Microsoft.Bcl.Memory` 9.0.0; this change does not upgrade shared dependencies.

## Verification

```powershell
dotnet test FModel.Cli.Tests/FModel.Cli.Tests.csproj
```

Unit tests cover argument validation, pagination limits, traversal prevention,
non-overwrite behavior, and failed-write cleanup without requiring game data.
An optional local integration test exercises the real CLI and game files:

```powershell
./FModel.Cli.Tests/Smoke.ps1 -Profile D:/Claude/FModel/.local/nzm.json
```

The smoke test creates a temporary profile and output under the system temp
directory; it never overwrites existing exports or modifies game files.
