# Rule format

Rules live in `rules/*.json` (embedded into the exe at build time) and are validated by `rules/schema.json`.

```json
{
  "$schema": "./schema.json",
  "rules": [
    {
      "id": "build.node",
      "category": "build",
      "risk": "regenerable",
      "match": { "marker": ["PackageJson"], "targets": ["node_modules", ".next"] },
      "title": { "en": "Node dependencies / build cache: {name}", "tr": "Node bağımlılıkları / build önbelleği: {name}" },
      "whatBreaks": { "en": "The project won't run until dependencies are installed again.", "tr": "…" },
      "howToRestore": { "en": "npm install", "tr": "npm install" }
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `id` | Stable, dotted, lowercase (`cache.npm`). Finding ids are derived from it and the path, so notes survive rescans. |
| `category` | `cache`, `build`, `installer`, `archive`, `duplicate`, `old-version`, `partial`, `agent-temp`, `game`, `system`… |
| `risk` | `safe` (nothing breaks), `regenerable` (comes back at a cost), `review` (look first), `personal` (info), `protected` (never suggested) |
| `action` | `quarantine` (default), `command` (shown; a system action may run it), `uninstall`, `launcher`, `info` |
| `match` | Exactly one of the forms below |
| `minBytes` | Smaller matches are ignored (default 10 MB) |
| `keepRecentHours` | Entries changed within this many hours stay in place when quarantining (Temp: 24) |
| `maturity` | `pending` rules run only with `--include-pending` |
| `title`, `reason`, `whatBreaks`, `howToRestore` | `{ "en": …, "tr": … }`; `{name}`, `{parent}`, `{count}`… are filled in |

## Match forms

- **`path`**: known-folder paths. `%UserProfile%`, `%LocalAppData%`, `%AppData%`, `%ProgramData%`, `%Temp%`,
  `%Downloads%`, `%Desktop%`, `%Documents%`, `%SystemDrive%`, `%ProgramFiles%`, `%ProgramFilesX86%`, `%Windows%`…
  `*` matches one folder level, `**` any depth. `excludeNames` lists folder names to skip.
- **`marker` + `targets`**: a project marker (`PackageJson`, `CargoToml`, `CsProj`, `UnityAssets`…) and the build
  folders next to it. Unity is recognised by `Assets` + `ProjectSettings`.
- **`dirName`**: a folder with this name anywhere (optionally `under` some paths).
- **`detector`**: a coded detector in `src/Ferahla.Windows/Detectors` for what JSON can't express (installers vs
  installed apps, zip twins, duplicates, old SDK/editor/CLI versions, repository clones, VM disks…).

## What the engine adds on top

Protected locations are removed from every finding (Windows, pagefile, Program Files for quarantine). Two rules
matching the same folder keep the first. A finding entirely inside another one is dropped; a partly nested item is
counted once. Growth since the previous scan of the same drive is computed per finding.
