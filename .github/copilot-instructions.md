# CssTools – Copilot Instructions

## Projektübersicht

**CssTools** ist ein Visual Studio 2022/2026 VSIX-Extension-Projekt (.NET Framework 4.7.2), das Entwicklern beim Arbeiten mit CSS Custom Properties (CSS-Variablen, `--name: value`) hilft. Das Projekt befindet sich unter `CssTools\CssTools.csproj`.

## Kernfunktionalität

### 1. Hover-Tooltip (QuickInfo)
Wenn der Benutzer in einem Editor-Buffer über eine CSS-Variable hovert – egal ob in `.css`, `.cs`, `.razor`/`.blazor` oder `.html` – erscheint ein Tooltip mit:
- **Variablenname** als Header (Keyword-Style)
- Darunter je eine **Zeile pro Definition**, gruppiert nach **Projektname** (alphabetisch), innerhalb der Gruppe alphabetisch nach Dateiname
- Jede Zeile zeigt Wert + Dateiname:Zeilennummer als **anklickbaren Link** → öffnet die Datei und springt zur genauen Zeile (`NavigationHelper`)

### 2. CSS Variable Store
`CssVariableStore` ist ein thread-sicherer Singleton. Er speichert alle Vorkommen jeder `--variable` mit:
- `FilePath` (absoluter Pfad)
- `ProjectName` (Name des `.csproj` ohne Extension)
- `LineNumber` (1-basiert)
- `Value` (Rohwert)

Zwei interne Dictionaries: `_fileDefinitions` und `_fileNameIndex` (beide keyed by Dateipfad).

### 3. Startup-Scan
Das Package (`CssToolsPackage`) lädt bei Lösungsöffnung automatisch (`[ProvideAutoLoad]` mit `SolutionExistsAndFullyLoaded`). Es iteriert alle Projekte der Solution via `IVsSolution` + `IVsHierarchy` und scannt jede `.css`-Datei in den Store.

### 4. FileSystemWatcher
Pro Projektverzeichnis läuft ein `FileSystemWatcher` auf `*.css` (rekursiv) – Änderungen, Neuanlagen, Umbenennungen und Löschungen aktualisieren den Store sofort, auch ohne die Datei im Editor zu öffnen.

### 5. Konfiguration (`.csstools.json`)
Im **Solution-Root** (neben der `.sln`) kann eine `.csstools.json` liegen:
```json
{
  "exclude": [ "**/themes/**", "**/PlaywrightTests/**" ],
  "include": [ "**/themes/light.css" ]
}
```
- `exclude`: Glob-Patterns – matching Dateien werden nicht gescannt
- `include`: überschreibt `exclude` (include gewinnt immer)
- Wird beim Start geladen; ein `FileSystemWatcher` auf den Solution-Root erkennt Änderungen und ruft `Reload()` → `PurgeExcluded()` auf (räumt den Store sofort auf)
- Wichtig: `IsInitialized`-Flag verhindert, dass der MEF-Provider Puffer scannt bevor die Config geladen ist

### 6. Menübefehl „Show CSS Variables"
Unter **Tools → Show CSS Variables** werden alle bekannten Variablen in ein dediziertes Output-Window-Pane geschrieben, gruppiert nach Variablenname → Projektname → Datei:Zeile.

## Dateistruktur

| Datei | Aufgabe |
|---|---|
| `CssToolsPackage.cs` | Package-Entry-Point, Startup-Scan, FileSystemWatcher, Command-Init |
| `CssVariableStore.cs` | Thread-sicherer Singleton-Store, Modell `CssVariableDefinition` |
| `CssToolsConfig.cs` | Config-Singleton, JSON-Parser (`JavaScriptSerializer`), Glob→Regex-Matcher |
| `CssVariableQuickInfoSource.cs` | Hover-Logik, baut ContainerElement mit Projekt-Gruppen |
| `CssVariableQuickInfoSourceProvider.cs` | MEF-Export für alle relevanten Content-Types |
| `NavigationHelper.cs` | Öffnet Datei + springt zu Zeilennummer via `IVsUIShellOpenDocument` |
| `ShowVariablesCommand.cs` | Menübefehl + Output-Window-Pane |
| `CssTools.vsct` | VSCT-Kommandotabelle für den Tools-Menüeintrag |

## Wichtige technische Details

- **MEF Content-Types**: `CSS`, `text/x-css`, `CSharp`, `Razor`, `RazorCSharp`, `RazorCoreCSharp`, `HTML`, `htmlx`, `HTMLX`
- **Projektname-Erhaltung**: Der MEF-Provider kennt keinen Projektnamen. Er liest ihn via `CssVariableStore.GetProjectName(filePath)` aus dem Store (vom Startup-Scan bereits befüllt) und überschreibt ihn nie mit `""`.
- **Glob-Matching**: Case-insensitive, `**` = beliebige Ordnertiefe, `*` = beliebige Zeichen ohne `/`
- **Assembly-Referenzen**: `System.ComponentModel.Composition` (MEF), `System.Web.Extensions` (JavaScriptSerializer)
- **Zeilennummer-Ermittlung**: Binärsuche auf einer vorgefertigten Line-Start-Tabelle aus dem Dateiinhalt
- **Config-Watcher** liegt im Solution-Root, **nicht** in Projektordnern

## Bekannte Eigenheiten

- Der MEF-Provider läuft früher als das Package. Vor `IsInitialized == true` werden Puffer-Scans übersprungen.
- `PurgeExcluded()` muss nach jedem `Reload()` aufgerufen werden, sonst bleiben bereits gescannte excluded Dateien im Store.
- `IVsProject.GetMkDocument` gibt für einige Hierarchy-Items keinen Pfad zurück (z.B. Solution-Folder-Nodes) – das wird per `== 0` Check und `File.Exists` abgefangen.
