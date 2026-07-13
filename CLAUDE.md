# AGENTS.md

This file provides guidance to Codex (ChatGPT) and other coding agents working in this repository.

## What this is

Kiriha is a Windows desktop file manager (Avalonia UI, .NET 10, native AOT) that intentionally combines two UI vocabularies: **Chrome's tab strip / omnibox / bookmarks bar** on top, and **Windows Explorer's** everything else (breadcrumb address bar, command bar, shell context menus, quick access, clipboard/DnD semantics). When implementing a feature, the default is "match what Explorer does" unless the feature is inherently tab/browser-shaped (in which case "match what Chrome does").

There is only one project: `src/Kiriha/Kiriha.csproj` (no test project, no CI config).

## Build & run

```bash
dotnet build src/Kiriha/Kiriha.csproj      # or: dotnet build Kiriha.slnx
dotnet run --project src/Kiriha/Kiriha.csproj
```

Native AOT publish:

```bash
dotnet publish src/Kiriha/Kiriha.csproj -c Release
# output: src/Kiriha/bin/Release/win-x64/publish/Kiriha.exe
```

From Git Bash, AOT's link step needs vswhere on PATH or it fails:
```bash
export PATH="$PATH:/c/Program Files (x86)/Microsoft Visual Studio/Installer"
```

`TreatWarningsAsErrors` is on (`Directory.Build.props`). `IL3053` is suppressed in the csproj — it's a known false-positive from Velopack's COM interop that ILC misreads as invalid IL; don't "fix" it, it's already handled.

There are no automated tests. Verification is: build clean (0 warnings), run the Debug exe, and screenshot/exercise the actual UI for anything touching XAML or interaction — a green build does not mean a feature works.

## Architecture

**MVVM, one window, tab-scoped state.** `MainWindowViewModel` owns the tab collection, sidebar, bookmarks bar, and window-level settings. Each open tab is an independent `TabViewModel` with its own path, navigation history (`_back`/`_forward` stacks), sort/view state, search, and clipboard cut/copy. Folder-change watching is **not** per-tab: `TabViewModel` holds only an `IDisposable` subscription handle and delegates to `DirectoryObservationService` (Services/DirectoryObservationService.cs), which keys one shared `FileSystemWatcher` per path across all tabs (reference-counted, with a debounce) so opening the same folder in multiple tabs doesn't spawn duplicate watchers. The **Settings tab** (`chrome://settings`-style) is just a `TabViewModel` constructed with `isSettingsTab: true` — `IsSettingsTab`/`IsNormalTab` gate which half of `MainWindow.axaml`'s per-tab `DataTemplate` renders. There is no separate settings window.

**Shared state that must stay shared across tabs** lives in a few singleton-ish objects passed into every `TabViewModel`, not duplicated per-tab:
- `ShellOptions` (Services/ShellOptions.cs) — show-hidden / show-extensions / show-checkboxes, with a `Changed` event every tab subscribes to so toggling one option refreshes all open tabs.
- `ClipboardFileService` — process-wide cut/copy state (`CutStateChanged` event) so every tab's file list can show the Explorer-style "cut item is translucent" effect, not just the tab that did the cutting.
- `AppSettings` (Services/SettingsService.cs) — the JSON-serialized (source-generated, AOT-safe) blob persisted to `%LocalAppData%\Kiriha\settings.json`: pinned tabs, bookmarks tree, window bounds, view-mode default, theme, startup path, etc. `MainWindowViewModel` holds the one `AppSettings` instance and writes through `SettingsService.Save` on every relevant mutation — there's no debounce, so don't be surprised by frequent small writes.

**Windows Explorer parity is implemented via direct Win32/Shell COM interop**, not by shelling out or reimplementing Explorer's behavior in managed code. Read the existing service before adding a new interop point — the pattern is `internal static partial class` + `[LibraryImport]` (source-generated P/Invoke, AOT-compatible) or source-generated COM (`[GeneratedComInterface]`) rather than classic `[ComImport]`/`DllImport`:
- `ShellContextMenuService` — real `IContextMenu`/`IShellFolder` so right-click gives the actual OS verbs (copy, delete, properties, "Open with", extension-installed entries, etc.), not a reimplementation. Note the deliberate scope decision recorded in comments: Windows 11's redesigned context menu is Explorer-internal with no public API, so this shows the classic/full menu instead — that's expected, not a bug to "fix" toward the new visual style.
- `ClipboardFileService` — writes real `CF_HDROP` + `Preferred DropEffect` clipboard formats so Ctrl+X/C/V round-trips with actual Explorer windows.
- `FileOperationService` — wraps `SHFileOperation`/`IFileOperation`-equivalent so delete goes to the Recycle Bin with the real system progress UI instead of `File.Delete`.
- `QuickAccessService` — reads the real Quick Access shell folder via COM so the sidebar matches the user's actual pinned folders, not a hardcoded guess.
- `ShellNewService` — reads the `HKCR\...\ShellNew` registry entries so "New > ..." matches what Explorer would offer on this machine.

**File list rendering is three parallel `ListBox`es, not one.** `MainWindow.axaml` has separate `ListBox`es for Details / List / Icons view, all bound to the same `TabViewModel.Entries`, toggled via `IsVisible` bindings on `IsDetailsView`/`IsListView`/`IsIconsView`. When adding a column or per-row behavior, it typically needs to be wired into all three templates that apply to it (details view has the column grid; icons/list are simpler). `TabViewModel.IconSize` is a free (Finder-style) continuous zoom value — `ViewMode`'s named presets (ExtraLarge/Large/Medium) just set `IconSize` to a fixed number; don't reintroduce a fixed enum-keyed size map.

**A large amount of interaction logic is necessarily in `Views/MainWindow.axaml.cs` code-behind**, not pure XAML bindings — context menus that are built dynamically (shell menus, "New" submenu from `ShellNewService`, bookmarks bar nesting), drag-and-drop (both from-OS and to-OS), multi-key keyboard shortcuts, and Win32 handle lookups (`TryGetPlatformHandle()`) can't be expressed declaratively. This file is ~1800 lines; grep for the specific `_Click`/`_PointerReleased`/`_KeyDown` handler name before assuming logic doesn't exist yet.

**Theme and accent color are not static resources** — `App.axaml` defines Light/Dark `ThemeDictionaries` for the Chrome-ish chrome (tab strip bg, omnibox bg, etc.), and `ThemeService` pushes the live Windows accent color into Avalonia's `SystemAccentColor*` resources on startup and on every `PlatformSettings.ColorValuesChanged`. Don't hardcode colors in new views — use the `DynamicResource` keys already defined in `App.axaml` (`TabStripBg`, `ContentBg`, `TextPrimary`, etc.) so both themes and live accent-color changes keep working.

**Auto-update is Velopack**, wired the same way as the sibling project `Lhamiel` (`../Lhamiel` — check there first if extending update behavior, it's the reference implementation this was copied from). `Program.cs` runs `VelopackApp.Build().Run()` before the Avalonia app starts; `UpdateService.Check4Update` does the actual check/download/apply dialog. `VelopackUpdateDialog`/`Velopack` are explicitly root-assemblies in the csproj (`TrimmerRootAssembly`) because their view models are only reached via compiled XAML bindings and would otherwise be trimmed by AOT.

**File-type icons are raster PNGs, not SVGs — this was a deliberate reversal, not the original design.** `Assets/MaterialIcons/` bundles [material-icon-theme](https://github.com/material-extensions/vscode-material-icon-theme) (MIT; see `NOTICE.md` there) as 1,250 pre-rasterized 256×256 PNGs plus a `manifest.json` (extension/filename/folder-name → icon key, derived from the npm package's `dist/material-icons.json`). `MaterialIconService.ResolveIconKey` does the name→key lookup (exact filename → lowercase filename → extension → default), `GetImage` loads+caches the PNG as an `IImage` via the exact same `AssetLoader` + `Bitmap` path already used for real image thumbnails. **Do not swap this to runtime SVG rendering** (e.g. `Avalonia.Svg.Skia`): that was tried first and empirically produced a fully black, never-composited window on this Avalonia 12.1.0 project — the package's latest build (11.3.0) only targets Avalonia 11.3.0, and no managed exception surfaces (process stays "Responding", CPU idle, nothing in the event log), so it silently breaks rendering rather than failing loudly. If material-icon-theme's upstream icon set ever needs refreshing, the reproducible pipeline is: pull the npm tarball → `dist/material-icons.json` for the manifest + `icons/*.svg` → rasterize with `resvg` (not cairosvg/svglib — Windows-native, no dependency hell) → drop the PNGs + manifest back into `Assets/MaterialIcons/`. The icon set is opt-in per `ShellOptions.UseMaterialIcons` (persisted as `AppSettings.UseMaterialIcons`, default off) — emoji (`FileSystemEntry.Icon`) remains the default for existing users.

## Conventions specific to this repo

- All user-facing strings and code comments are Japanese (see any existing file). Keep new UI text and comments in Japanese to match.
- No dependency injection container — everything is constructed directly and wired via constructor params / shared instances passed down from `MainWindowViewModel`.
- Avalonia 12's `WindowDecorations="BorderOnly"` is used deliberately so the custom-drawn caption buttons in the tab strip don't double up with OS-drawn ones — if captions ever look doubled, this is the property to check, not a bug in the custom buttons.
- `ApplicationIcon` (exe icon) and the `AvaloniaResource` `icon\app_icon.png` (window/taskbar icon) are two independent things that both need updating if the icon changes.
