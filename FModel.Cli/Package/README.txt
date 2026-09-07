FModel CLI - Windows x64 standalone

No .NET installation is required. Extract the ZIP before running commands.
Open PowerShell in this directory. This is a command-line tool, not a GUI.

  .\nzm.cmd mount
  .\nzm.cmd search --query Weapon --extension uasset --limit 20
  .\nzm.cmd inspect --asset "NZM/Content/path/from/search.uasset"
  .\nzm.cmd extract --asset "NZM/Content/path/from/search.uasset"

inspect: write package properties as JSON.
extract: write original decrypted package plus sidecar files.
Results go to exports/ next to nzm.local.json. Existing files are not overwritten.
Paths returned by search are virtual game paths, not Windows filesystem paths.

Configuration: nzm.local.json. Directory is your installed game directory.
Relative paths are resolved from the configuration file's directory.
The environment variable FMODEL_AES_KEY overrides a key in the configuration.

PRIVATE PACKAGE: nzm.local.json may contain your AES key. Do not publish or share
this archive without removing the key and checking other private settings.

JSON results: stdout. Library diagnostics: stderr.
Exit codes: 0 success; 1 failure; 2 incomplete registered-container mount.
Launcher Chromium .pak files can produce non-Unreal-format warnings.
Patch selection uses CUE4Parse ReadOrder, not filesystem modification time.
Search may contain duplicate paths. Counts include entries across containers.
This release does not include PNG/FBX/WAV conversion, MCP, or a Codex skill.

Third-party notices and project licenses are included alongside this file.
