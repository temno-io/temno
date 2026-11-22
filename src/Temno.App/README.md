# Temno.App (WinUI 3)

The Windows desktop app: Temno's windows, panels and controls. Editing logic lives in `Temno.Core`, media in
`Temno.Media`, the MCP server in `Temno.Mcp` and the generation clients in `Temno.Generation`. This project draws
them.

## Build and run

Needs Windows x64 and the **.NET 10 SDK**, 10.0.400 or later (`winget install Microsoft.DotNet.SDK.10`). Open a
new terminal after installing, so `dotnet` is on PATH.

From the repository root:

```powershell
.\scripts\run-app.ps1
```

That builds x64 Debug and starts the app. `-Configuration Release` builds Release, and `-NoBuild` starts what is
already built.

To build by hand:

```powershell
dotnet build src\Temno.App\Temno.App.csproj -c Debug -p:Platform=x64
```

The app runs unpackaged from the build folder, not from the project folder:

```text
src\Temno.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Temno.App.exe
```

## Layout

| Path | Role |
|------|------|
| `App.xaml.cs`, `MainWindow.xaml` | Start-up, the dark window chrome and the root frame |
| `Theme/AppTheme.cs`, `Theme/AppTheme.xaml` | Design tokens (colours, spacing, radii, window sizes), in C# and as XAML resources |
| `Views/HomePage.xaml` | Recent projects, and new, open and reveal |
| `Views/Editor/EditorPage.xaml` and its `EditorPage.*.cs` parts | The editor: panels, menus, keyboard, import, captions and the export queue |
| `Controls/TimelineControl.xaml` | Tracks and clips: move, trim, blade and zoom |
| `Controls/PreviewHostControl.xaml` | The preview monitor, its transport and the caption overlay |
| `Controls/InspectorControl.xaml` | Clip properties |
| `Controls/AgentPanelControl.xaml` | The chat panel |
| `Controls/SettingsControl.xaml`, `Controls/AccountControl.xaml` | Settings and the account panel |
| `Controls/Shared/` | The shared editor controls: `EditorRow`, `UnitNumberField`, `ColorWell`, `ToggleColorRow`, `SectionHeader` and others. Build new panels from these |
| `Services/` | App services: the open project session, dialogs, import, sign-in, updates and the crash log |
| `Views/DevControlGallery.cs` | A gallery of the shared controls, in Debug builds only: start with `TEMNO_DEV_GALLERY=1` |

## Project references

`Temno.Core`, `Temno.Media`, `Temno.Mcp` and `Temno.Generation`.
