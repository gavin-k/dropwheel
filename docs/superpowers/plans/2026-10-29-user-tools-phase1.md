# User Tools Phase ① Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tools an AI agent writes into `%LOCALAPPDATA%\DropWheel\tools\<slug>\` appear on the Tools / Actions wheel, run through the job queue with undo, and can be grouped into a second level that opens as an outer ring.

**Architecture:** Core gains a `Tools/` namespace (manifest, reader, catalog, script runner behind `IProcessRunner`) and a `layout.json` (`WheelLayout` / `LayoutStore`) that decides which slot each operation occupies; `WheelBuilder` reads the layout and emits slots with children. The App wires a `SidecarProcessRunner`, a hot-reloading `UserToolCatalog`, a trust gate in settings, an outer ring in `WheelWindow` / `WheelController`, a Settings section and `--validate-tool` / `--list-tools`. A skill folder (`skills/dropwheel-tool`) plus a JSON Schema is the public contract agents read.

**Tech Stack:** .NET 10, C# 14, xunit + FluentAssertions, System.Text.Json source generation, WinUI 3 (App only), CsWin32.

**Spec:** `/Users/gavinlee/.claude/plans/soft-questing-lobster.md` (approved plan; Phase ① Steps 0–9 and the decisions table).

## Global Constraints

- `DropWheel.Core` references no Windows API; `./build.sh` on macOS restores, builds and tests Core + Engines after every task and must stay green.
- `EngineToolsTests` asserts registration order per kind; with no layout the wheel keeps registration order.
- Every new `L.T("…")` gets its English in that project's `Strings.en.json` (`CatalogTests`). User-tool labels are data and never pass through `L.T`.
- Runners are stateless; `Check` is cheap and synchronous (`JobQueue.Retry` reuses the instance, the wheel calls `Check` per sector on open).
- External scripts never report output paths: DropWheel hands out a temp path and `OutputFiles` moves it into place so undo / rollback / conflict policy apply.
- Tool ids match `^user\.[a-z0-9-]+$` and are never a format id.
- Commit after each task on branch `feature/user-tools-phase1`, messages end with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.

## Review Focus

1. A manifest whose `args` names `{output}` while `output.mode` is `none`: reader reports `output` problem, tool is not loaded (Task 1 test).
2. A script that exits 0 but never writes `{output}`: `ScriptTool` fails with a readable error, no `.dropwheel-tmp` left behind (Task 3 test).
3. A full Tools wheel for images when a new tool installs: the target slot folds into a group instead of the tool vanishing (Task 5 test).
4. A layout naming an id that no longer exists (tool deleted): slot is skipped, wheel still builds (Task 5 test).
5. A script that hangs: timeout kills it and the job fails with the timeout message (Task 3 test via fake runner `TimedOut`).

---

### Task 0: Raw output extension

**Files:**
- Modify: `src/DropWheel.Core/Conversion/IConverter.cs` (`ConversionRequest`)
- Modify: `src/DropWheel.Core/Jobs/OutputPlanner.cs:PathFor`
- Test: `tests/DropWheel.Core.Tests/OutputPlannerTests.cs`, `tests/DropWheel.Core.Tests/OutputFilesTests.cs`

**Interfaces:**
- Produces: `ConversionRequest.OutputExtension : string?` (init); when set, `OutputPlanner.PathFor` uses it as the extension and `{fmt}` token without consulting `FormatRegistry`.

- [ ] Write `OutputPlannerTests.A_raw_output_extension_bypasses_the_format_registry`: request `TargetFormatId = "user.x"`, `OutputExtension = "bak"` → path `Dir/a.bak`.
- [ ] Write `OutputFilesTests.Write_with_a_raw_extension_lands_next_to_the_source`.
- [ ] Run, expect fail (`ArgumentException` unknown format).
- [x] Implement; run `./build.sh`; commit `feat(jobs): plan outputs by a raw extension`.

### Task 1a: Manifest model and reader

The catalog (Task 1b) needs `CommandResolver` (Task 2) and `ScriptTool` (Task 3), so the order is 0 → 1a → 2 → 3 → 1b.

**Files:**
- Create: `src/DropWheel.Core/Tools/LocalizedText.cs`, `ToolManifest.cs`, `ToolProblem.cs`, `ToolManifestReader.cs`, `ToolsJson.cs`
- Test: `tests/DropWheel.Core.Tests/Tools/ToolManifestReaderTests.cs`

**Interfaces:**
- `record LocalizedText(string Zh, string? En = null) { string Pick(); }`
- `record ToolManifest(int Schema, string Id, LocalizedText Name, LocalizedText Hint, string? Icon, WheelMode Wheel, IReadOnlyList<MediaKind>? AppliesTo, string Kind, ScriptSpec Script, PlacementHint? Placement, string Undo)`
- `record ScriptSpec(string Command, IReadOnlyList<string> Args, bool PerFile, OutputSpec Output, int TimeoutSeconds)`; `record OutputSpec(string Mode, string Extension, string Suffix)`; `record PlacementHint(LocalizedText? Group, int? Index)`
- `record ToolProblem(string Code, string Message, bool IsWarning = false)`; `record ToolReadResult(string Folder, ToolManifest? Manifest, IReadOnlyList<ToolProblem> Problems)`
- `static ToolManifestReader.Read(string folder) → ToolReadResult`; `static string CommandHash(ScriptSpec)`
- `UserToolCatalog(string toolsDir, IProcessRunner runner, CommandResolver resolver, TimeProvider? time = null)`: `CatalogSnapshot Current`, `event Action<CatalogSnapshot>? Changed`, `CatalogSnapshot Reload()`, `void Watch()`, `Dispose()`; `record CatalogSnapshot(IReadOnlyList<ScriptTool> Tools, IReadOnlyList<ToolReadResult> Problems)`.

- [x] Tests: minimal manifest reads; bare-string name is Zh-only and `Pick()` falls back to Zh; id without the `user.` prefix is rejected (`id`; the prefix alone keeps every tool id off the format ids, so there is no separate `id-is-format` code); unknown placeholder (`args`); `{output}` with `mode:none` (`output`); every problem is listed, not just the first; bad JSON lists one `json` problem.
- [x] Implement; `./build.sh`; commit `feat(tools): read tool manifests`.

### Task 1b: Catalog (after Task 3)

**Files:**
- Create: `src/DropWheel.Core/Tools/UserToolCatalog.cs`
- Test: `tests/DropWheel.Core.Tests/Tools/UserToolCatalogTests.cs`

- [x] Tests: `Reload` swaps snapshot and raises `Changed`; `.problems.json` / dotfiles are not tool folders; a folder with problems gets `.problems.json`, a fixed one loses it; duplicate ids are an `id` problem; a command not on PATH is a warning that still loads the tool.
- [x] Implement (`FileSystemWatcher` + 300 ms debounce ignoring `.problems.json` and `*.dropwheel-tmp`); `./build.sh`; commit `feat(tools): watch the tools folder`.

### Task 2: Process runner

**Files:**
- Create: `src/DropWheel.Core/Tools/IProcessRunner.cs`, `CommandResolver.cs`
- Create: `src/DropWheel.Engines/Tools/SidecarProcessRunner.cs`, `KillOnCloseJob.cs`
- Modify: `src/DropWheel.Engines/Media/SidecarProcess.cs` (environment, `onStarted`)
- Test: `tests/DropWheel.Core.Tests/Tools/CommandResolverTests.cs`

**Interfaces:**
- `record ProcessStart(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string,string> Environment, TimeSpan Timeout)`; `record ProcessOutcome(int ExitCode, string StderrTail, bool TimedOut)`
- `interface IProcessRunner { Task<ProcessOutcome> RunAsync(ProcessStart start, Action<string>? onStdoutLine, CancellationToken ct); }`
- `CommandResolver(Func<string,bool> fileExists, string? path, string? pathExt)`: `string? Resolve(string command, string toolDir)`; `pwsh` falls back to `powershell`.

- [x] Tests for absolute / relative-to-toolDir / PATH × PATHEXT / pwsh fallback.
- [x] Implement; `./build.sh`; commit `feat(tools): run external commands behind IProcessRunner`.

### Task 3: ScriptTool

**Files:**
- Create: `src/DropWheel.Core/Tools/ScriptTool.cs`, `ScriptOutput.cs`
- Modify: `IWheelOperation.cs` (`string? TrustKey => null`), `Availability.cs` (`UntrustedTool`)
- Test: `tests/DropWheel.Core.Tests/Tools/ScriptToolTests.cs`, `ScriptOutputTests.cs`, `JobQueueTests`

- [x] Tests: output moved into place; progress lines drive progress; non-zero exit → error with stderr tail; `skip` → no output, Skipped; `mode:none` → Done with no outputs; timeout → error; missing output → error and no temp left; cancellation leaves nothing.
- [x] Implement; `./build.sh`; commit `feat(tools): run script tools through the job queue`.

### Task 4: Trust gate and live operations

**Files:**
- Create: `src/DropWheel.Core/Settings/UserToolSettings.cs`, `src/DropWheel.Core/Wheel/LiveOperations.cs`
- Modify: `AppSettings.cs`, `SettingsStore.cs` (Repair), `WheelModel.cs:BuildOperations`, `App.xaml.cs`
- Test: `WheelBuilderTests` (new file), `SettingsStoreTests`

- [ ] Tests: untrusted script tool greyed with `UntrustedTool`; changed hash greys again; `Tools` section round trips and repairs.
- [ ] Implement; `./build.sh`; commit `feat(tools): gate script tools behind a trust switch`.

### Task 5: Layout file and grouped slots

**Files:**
- Create: `src/DropWheel.Core/Wheel/WheelLayout.cs`, `LayoutStore.cs`
- Modify: `WheelModel.cs` (`WheelSlot.Children`, `WheelModel.SlotAt`, `WheelBuilder` layout parameter), `SettingsJson`
- Modify (App): `WheelWindow.Slot`, `WheelController.Rebuild` / `OnDropped`
- Test: `tests/DropWheel.Core.Tests/Wheel/LayoutStoreTests.cs`, `WheelBuilderTests`

- [ ] Tests: no layout keeps registration order; first install seeds built-ins; place by index; unknown id ignored; placement applied once; full wheel folds the target slot into a group; group full → unplaced; round trip.
- [ ] Implement; `./build.sh`; commit `feat(wheel): lay out tools and actions from layout.json with groups`.

### Task 6: Outer ring

**Files:**
- Modify: `WheelGeometry.cs`, `SectorGate.cs`; Create `WheelHit.cs`
- Modify (Shell): `DropTargetHost.cs` (`WheelPlacement.Ring`, `WheelHit` events)
- Modify (App): `WheelController.cs` (dwell 200 ms, ring close 150 ms), `WheelWindow.cs` (`SetRing`, `SizeFor`)
- Test: `WheelGeometryTests`, `SectorGateTests`

- [ ] Tests: children centred on parent clockwise; child centres hit themselves; ring band is no hit without a ring; ring hit reported only while open.
- [ ] Implement; `./build.sh`; commit `feat(wheel): open a group as an outer ring`.

### Task 7: Settings › 我的工具 — `SettingsWindow` section, strings in `Strings.en.json`; commit `feat(settings): list user tools with a trust switch`.

### Task 8: `--validate-tool` / `--list-tools` — `Program.cs`, `DISABLE_XAML_GENERATED_MAIN`, `AttachConsole`; commit `feat(app): validate and list tools from the command line`.

### Task 9: Skill, schema, docs, packaging — `skills/dropwheel-tool/SKILL.md` + examples, `docs/tool-manifest.schema.json`, `docs/adr/0005-user-tools-manifest-and-trust.md`, docs updates, csproj Content, `FileSystemWriteVirtualization` disabled; commit `docs(tools): ship the dropwheel-tool skill and manifest schema`.
