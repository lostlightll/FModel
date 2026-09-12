# FModel CLI

A local, headless entry point into this checkout's CUE4Parse game adapters.
No WPF application, MCP server, network service, or API key is required.
Requires the .NET 10 SDK to build and the .NET 10 runtime to run.

## Build and run

From the repository root:

```powershell
dotnet build FModel.Cli/FModel.Cli.csproj -c Release
dotnet FModel.Cli/bin/Release/net10.0/FModel.Cli.dll --help
dotnet FModel.Cli/bin/Release/net10.0/FModel.Cli.dll mount --profile .local/nzm.json
dotnet FModel.Cli/bin/Release/net10.0/FModel.Cli.dll search --profile .local/nzm.json --query Weapon --extension uasset --limit 20
```

`nzm.example.json` documents the profile fields. Relative paths are resolved from
the profile directory. `Directory` is scanned recursively, including patch folders.
Read commands ignore `OutputDirectory` (it may be empty or absent). Only exports
validate an output root and require it not to overlap the input directory.
`Game` defaults to `GAME_AssaultFireFuture`; do not replace it with generic UE4.24.

Provide the key in the environment variable named by `AesKeyEnvironmentVariable`
(default: `FMODEL_AES_KEY`), or in an `AesKey` property in a private local profile.
The environment takes precedence. Never commit keys. The repository ignores
`.local/`, but this is not encryption or an access-control boundary.
An optional `Mappings` property accepts an existing `.usmap` path. Like the
directory fields, relative paths are resolved from the profile directory.

## Standalone Windows package

```powershell
./FModel.Cli/Publish.ps1 -PrivateProfile .local/nzm.json
```

Creates a timestamped, self-contained Windows x64 package and ZIP under
`artifacts/`. The package includes `nzm.cmd`, usage instructions, licenses, and
your private profile with output set to `exports/` beside the profile. No .NET
installation is needed on the target machine. Run `./nzm.cmd search --query Weapon`
from the extracted directory. The ZIP contains the configured key: do not publish
it without removing private settings. Optional mappings remain external files.

## Commands

| Command | Options | Result |
| --- | --- | --- |
| `mount` | `--profile` | Registered/mounted container and file counts, missing key GUIDs |
| `search` | `--profile`, optional `--query`, `--extension`, `--offset`, `--limit` | Case-insensitive path substring search, sorted by path, maximum 200 results |
| `containers` | `--profile`, optional `--query`, `--offset`, `--limit` | Mounted containers with exact disk path, size, entry count and ReadOrder |
| `list` | `--profile`, `--container`, optional `--query`, `--offset`, `--limit` | Only selected container's own entries: virtual path, uncompressed size, source |
| `diff` | `--profile`, `--container`, `--asset`, optional `--against`, `--max-differences`, `--max-depth`, `--max-nodes` | Bounded field differences against a previous or explicitly selected container |
| `lua-functions` | `--profile`, `--container`, `--asset`, optional `--dialect`, `--query`, `--offset`, `--limit` | Bounded Lua function inventory |
| `lua-disasm` | Same source options, `--function`, optional `--offset`, `--limit` | Function-level bytecode disassembly, never execution |
| `lua-diff` | Same source options, optional `--against`, `--function`, `--max-changes`, `--max-work`, pagination | Lua/slua function and instruction differences |
| `inspect` | `--profile`, `--asset`, optional `--output-directory` | Parse all package exports into `json/<asset>.json`; return path and byte count |
| `extract` | `--profile`, `--asset`, optional `--output-directory` | Decrypt/decompress original package plus associated payloads into `raw/` |

`--asset` takes the exact virtual path returned by `search`, not a disk path.
There is deliberately no wildcard extraction or automatic full-game export.
See [the Lua JSON contract](Lua/README.md) for dialect validation, stable identities,
root-only scope, normalization, resource limits and incomplete-result semantics.
Use `inspect` for Unreal packages, not arbitrary loose files or localization files.
Raw extraction is not conversion into PNG, WAV, FBX, or a reimportable project.

```powershell
dotnet FModel.Cli/bin/Release/net10.0/FModel.Cli.dll inspect --profile .local/nzm.json --asset NZM/Content/AIBehavior/BaseAIBluprints/DataConfig/DataAsset/AITarget/DA_AITarget_WeaponFriend.uasset
dotnet FModel.Cli/bin/Release/net10.0/FModel.Cli.dll extract --profile .local/nzm.json --asset NZM/Content/AIBehavior/BaseAIBluprints/DataConfig/DataAsset/AITarget/DA_AITarget_WeaponFriend.uasset
```

Each command emits one JSON response to stdout; help is plain text. Library
diagnostics are suppressed because third-party messages can contain secrets.
Errors expose safe CLI messages or exception type names only. Invoke the built DLL, not `dotnet run`, when parsing
stdout, because build output is not part of the JSON contract.

- Exit `0`: operation succeeded and all registered containers mounted.
- Exit `1`: argument, configuration, parsing, or filesystem error.
- Exit `2`: no mounted containers or an incomplete registered-container mount.
  Search/export results may still be available; check `mountComplete`.

Output paths cannot traverse outside the configured root, contain symlinks or
junctions, or overwrite existing files. A failed extraction rolls back files
created by that invocation. Forced process termination can still leave partial
output. To repeat an export, use `--output-directory` (relative to the current
working directory) without changing or copying the profile.

## In-place container analysis

`mount`, `search`, `containers`, `list` and `diff` do not create export directories,
write profiles, copy/link/rename containers, or export package data. A process
mounts indices once; the two diff views borrow those readers and read only the
requested asset and needed dependencies. `list` never uses merged provider entries.

`--container` and `--against` accept exact filenames or absolute container paths.
Duplicate names fail with `AmbiguousContainer` and `candidates`; use an absolute path.
`--asset` for diff requires an exact virtual filename including its extension.
Automatic previous-version selection chooses the highest strictly lower CUE4Parse
`ReadOrder` containing that path, not a guessed filename version. Equal-priority
predecessors fail with `AmbiguousPreviousVersion`; choose one with `--against`.
Missing previous versions fail with `NoPreviousVersion`.

Each structured pak view pins its selected container, limits dependency lookup
to containers with `ReadOrder <= selected.ReadOrder`, and rejects ambiguous
dependency sources. Every main package and its `.uexp`, `.ubulk`, `.uptnl` payloads
come from the same owner, with no missing-sidecar fallback. Unavailable container
indices prevent automatic selection and structured comparisons. IoStore structured
version views are not supported; their requested raw content can still be compared.

Successful results use `{ok,mountComplete,result}`. A diff result contains:

- `asset`, `before` (old container), `after` (target container), `kind`.
- `binary`: requested-file `beforeSize`/`afterSize`, same-owner package totals,
  aggregate `contentChanged`, and only the `changedFiles` (including sidecars).
- `diff`: `differences` with JSON Pointer `path`, `kind`, `before`, `after`;
  `truncated`, `reasons`, `visitedNodes`, `differenceCountLowerBound`.
- `structuredError` and `limits`. `kind` is `structured`, `binary` (including Lua),
  or `binary-fallback` if parsing is unavailable. Binary results have `diff: null`;
  they never automatically decompile content.

Only changed leaves are emitted, not entire tables or inserted objects. An empty
`differences` array means equal only when `truncated` is false and `kind` is
`structured`. `differenceCountLowerBound` is exact only after complete traversal.

| Parameter | Default | Allowed range |
| --- | --- | --- |
| `offset` | 0 | 0–2147483647 |
| `limit` | 50 | 1–200 |
| `max-differences` | 100 | 1–1000 |
| `max-depth` | 32 | 1–64 |
| `max-nodes` | 100000 | 1–2000000 |

Node budget counts traversal and duplicate property scans. Reaching a traversal
limit reports `truncated: true` and a reason. Scalar previews are capped at 2048
characters. Additional hard limits: 64 MiB per file, 128 packages and 256 MiB read
budget per version view, and 32 Mi characters per in-memory asset JSON. These
resource-limit failures return a safe error rather than claiming equality.

```powershell
FModel.Cli.exe list --profile private.json --container P_1.0.50.485.0_R_4519726_P.pak --limit 20
FModel.Cli.exe diff --profile private.json --container P_1.0.50.485.0_R_4519726_P.pak --asset NZM/Content/Attributes/AutoGenerate/numerical_modifier_config.uasset --max-differences 20
```

## Scope and limitations

- Each process remounts the game; there is no persistent session or job queue.
  A future MCP transport can reuse `GameSession` without involving the GUI.
- Mount counts cover containers registered by CUE4Parse. Unsupported containers
  rejected during discovery are not included in these counts. Launcher Chromium
  `.pak` files are not Unreal containers.
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
./FModel.Cli.Tests/Smoke.ps1 -Profile .local/nzm.json
```

The smoke test creates a temporary profile and output under the system temp
directory; it never overwrites existing exports or modifies game files.
