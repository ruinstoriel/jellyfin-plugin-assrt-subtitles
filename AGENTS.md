# AGENTS.md

Jellyfin plugin that fetches subtitles from assrt.net. Two projects: the plugin itself (`src/Jellyfin.Plugin.AssrtSubtitles`, `net9.0`) and xunit tests (`Jellyfin.Plugin.AssrtSubtitles.Tests`, `net10.0`). `global.json` pins the .NET 10 SDK (10.0.300, `latestMajor`).

## Commands

```bash
dotnet build Jellyfin.Plugin.AssrtSubtitles.sln -c Release
dotnet test  Jellyfin.Plugin.AssrtSubtitles.Tests/Jellyfin.Plugin.AssrtSubtitles.Tests.csproj -c Release
dotnet publish src/Jellyfin.Plugin.AssrtSubtitles/Jellyfin.Plugin.AssrtSubtitles.csproj -c Release -o ./publish
```

- Main project has `ImplicitUsings` disabled and XML doc comments on public members; tests have implicit usings on. Match this per-project.
- CI does NOT run tests (restore/build/publish only) and installs the .NET 9 SDK. Tests need the .NET 10 SDK locally.
- No linter/analyzer gate; the build has 2 pre-existing CS8600 warnings in `AssrtSubtitleProvider.cs` — don't chase them.

## Release workflow (tag-driven, do not hand-edit manifest.json)

Releases happen by pushing a tag matching the plugin version (e.g. `0.1.14.14`). CI (`.github/workflows/ci.yml`) then:
1. Zips `Jellyfin.Plugin.AssrtSubtitles.dll` + `.deps.json` + the third-party runtime deps `SharpCompress.dll` and `ZstdSharp.dll` into `assrt-subtitles-plugin.zip` (the config page is an embedded resource in the DLL).
2. Computes the zip MD5, builds a new manifest version entry from the **tag name** (version), **last commit message** (changelog), and prepends it to root `manifest.json`.
3. Deploys that manifest to `gh-pages`, uploads the artifact, and creates a GitHub Release.

So: bump `<Version>` in the `.csproj` before tagging; never edit the `versions` array in `manifest.json` — CI generates it. `manifest.json` only needs its static fields (guid, name, description).

## Release steps (follow exactly)

1. Pick the next version: take the highest existing tag (`git tag --sort=-v:refname | Select-Object -First 1`) and bump the last segment (e.g. `0.1.14.14` → `0.1.14.15`). Never reuse an existing tag name.
2. Set `<Version>` in `src/Jellyfin.Plugin.AssrtSubtitles/Jellyfin.Plugin.AssrtSubtitles.csproj` to that version.
3. Build (`dotnet build Jellyfin.Plugin.AssrtSubtitles.sln -c Release`) to confirm it compiles.
4. `git add -A && git commit -m "<中文主题>" -m "<中文要点列表>"` — see "Commit messages" below. A git hook may auto-update `manifest.json`; commit whatever it produces.
5. `git push origin master`, then `git tag <version>` and `git push origin <version>`. CI handles the rest (zip + manifest + Release).

## Commit messages

Two-part format: a short subject line, then a bulleted body. Never pack the whole change into one long line.
The entire message becomes the release changelog (CI reads the last commit message), so the body matters.

```powershell
git commit -m "搜索结果排序改为 JEV 模型优先" -m "- 新增 TypeSafeApiKey / TypeSafeApiUrl 配置项与配置页输入框`n- 新增 TypeSafeClient：Bearer 鉴权、429/529 指数退避重试`n- 新增 7 个 JevRanker 单元测试"
```

- Subject (~20-30 chars): what changed, no trailing period.
- Body: one `-` bullet per logical change, one sentence each. Pass it as a second `-m`; use PowerShell backtick-`` `n `` for line breaks.

## Architecture

- `Plugin.cs` — entry point; `BasePlugin<PluginConfiguration>` + `IHasWebPages`; embeds `Configuration/configPage.html`.
- `PluginServiceRegistrator.cs` — registers a named `HttpClient` ("AssrtSubtitles") with a proper User-Agent, `AssrtApiClient`, and `AssrtSubtitleProvider` as `ISubtitleProvider`.
- `AssrtApiClient.cs` — `api.assrt.net/v1` search/detail/download.
- `TypeSafeClient.cs` — POSTs a `choice` question to the TypeSafe evaluation endpoint (https://docs.typesafe.ai/api, `jev-latest`) to rank search results. Configured via `TypeSafeApiKey`/`TypeSafeApiUrl`; on 429/529 it retries with exponential backoff and returns null (caller falls back to upload-date ordering).
- `Models/JevRanker.cs` — pure helpers for the JEV prompt (`BuildState`/`BuildInstructions`) and for ordering candidates by probability; unit tested in `JevRankerTests.cs`.
- `AssrtSubtitleProvider.cs` — search → JEV ranking (upload-year proximity is only the fallback) → map results; `GetSubtitles` → pick file, extract archives via SharpCompress; `_queryCache` stores search requests for 10 min to improve archive-entry selection. Note: `BuildQuery` for episodes uses only `SeriesName` (no season/episode in the query) and appends `" <ProductionYear>"` for accuracy when the year isn't already part of the title.
- `Models/AssrtFilelistConverter.cs` — custom converter handling assrt's inconsistent `filelist` JSON: array, single object, `""`, or `{}`. Add test coverage here if touching it.
- `Models/MediaMatcher.cs` — Dice + Levenshtein similarity with SxxExx/season/episode extraction and veto logic.

## Gotchas

- `PluginConfiguration.cs` ships a prefilled dev API token and defaults `PreferredLanguages` to `["zho"]`. Treat tokens as secrets: never log the token; `GetApiToken()` returns null on blank, which silently skips search.
- Code comments are predominantly Chinese — keep that convention.
- Config page lives as an embedded resource; after changing it you must rebuild (no separate static file is shipped).
- Plugin assemblies load in a per-plugin `AssemblyLoadContext` backed by `AssemblyDependencyResolver` on the plugin `.deps.json`; the resolver only looks inside the plugin folder, so **every non-Jellyfin NuGet dependency must be copied into the zip** (currently `SharpCompress.dll` + `ZstdSharp.dll`). Only ship deps the server doesn't already provide — never the `MediaBrowser.*` / `Jellyfin.*` / `Microsoft.*` shared assemblies.
- Only the two tests in `AssrtFilelistConverterTests.cs` are meaningful; `UnitTest1.cs` is an empty placeholder.
