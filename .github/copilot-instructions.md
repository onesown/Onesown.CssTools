# CssTools – Copilot Instructions

## Projektübersicht

**CssTools** ist ein Visual Studio 2022/2026 VSIX-Extension-Projekt (.NET Framework 4.7.2), das Entwicklern beim Arbeiten mit CSS Custom Properties (CSS-Variablen, `--name: value`) und CSS-Klassennamen hilft. Das Projekt befindet sich unter `CssTools\CssTools.csproj`.

## Kernfunktionalität

### 1. Hover-Tooltip für CSS-Variablen (QuickInfo)
Wenn der Benutzer in einem Editor-Buffer über eine CSS-Variable hovert – egal ob in `.css`, `.cs`, `.razor`/`.blazor` oder `.html` – erscheint ein Tooltip mit:
- **Variablenname** als Header (Keyword-Style)
- Darunter je eine **Zeile pro Definition**, gruppiert nach **Projektname** (alphabetisch), innerhalb der Gruppe alphabetisch nach Dateiname
- Jede Zeile zeigt Wert + Dateiname:Zeilennummer als **anklickbaren Link** → öffnet die Datei und springt zur genauen Zeile (`NavigationHelper`)

### 2. Hover-Tooltip für CSS-Klassen (QuickInfo)
Wenn der Benutzer über einen CSS-Klassennamen hovert – in `.css`, `.html`, `.razor`/`.blazor` oder `.cs` – erscheint ein Tooltip mit:
- **Klassenname** als Header (`.className`, Keyword-Style)
- Darunter je eine **Zeile pro Definition**, gruppiert nach **Projektname**, innerhalb der Gruppe sortiert nach **Zeilennummer**
- Jede Zeile zeigt Dateiname:Zeilennummer als **anklickbaren Link**

**Erkennung des Cursor-Kontexts** (`CssClassQuickInfoSource`):
- **CSS-Buffer**: Token direkt unter dem Cursor (jedes `[\w-]+`-Token)
- **HTML/Razor-Buffer**: Razor-aware Scanner der `class="..."` Attribute parst:
  - Statische Segmente zwischen `@`-Ausdrücken → direkte Token-Suche
  - `@(...)` Razor-Ausdrücke → String-Literale darin (`"float-label"`) werden ebenfalls ausgewertet (`FindClassInRazorExpression`). Wichtig: `SkipBalancedParens` mit `SkipStringLiteral` verarbeitet korrekt verschachtelte Anführungszeichen wie `@(cond ? "a" : "b")`.
  - Einfache `@Identifier`-Ausdrücke werden übersprungen
  - Fallback `FindClassNameInStringLiteral` für weitere String-Literale auf der Zeile

**CSS-Klassen-Scan** (`CssClassStore`, Regex `(?<![:\w])\.(?!\d)([\w-]+)`):
- Erkennt alle Klassenselektoren inklusive Nachfahren-Kombinatoren (`.a .b { }`), Ketten (`.a.b`), Pseudo-Klassen etc.
- Dedupliziert gleiche Klasse auf gleicher Zeile (Chained Selektoren)

### 3. CSS-Variablen-Autocomplete (IntelliSense)
Sobald der User `--` tippt – in `.css`, `.cs`, `.razor`/`.blazor` oder `.html` – erscheint eine Completion-Liste mit allen gescannten CSS-Variablen:
- **Trigger**: exakt `--` als Prefix (ein einzelnes `-` öffnet die Liste **nicht**)
- **Exklusiv**: `ExclusivelyProvidesItems` + `DoDismiss()` in `InitializeCompletion` schließt den nativen CSS-Language-Service-Completer **synchron auf dem UI-Thread**, bevor die async-Session startet
- **Insert-Text**: Auswahl eines Items fügt `var(--varname)` ein (nicht nur den Namen)
- **Suffix**: erster bekannter Wert der Variable wird neben dem Namen angezeigt
- **Description-Tooltip**: gleicher Aufbau wie QuickInfo – Projektname als Gruppe, darunter Wert + Datei:Zeile
- **Implementierung**: `CssVariableCompletionSource` (`IAsyncCompletionSource`) + `CssVariableCompletionSourceProvider` (`IAsyncCompletionSourceProvider`, MEF, alle Content-Types)
- **Wichtig**: `ICompletionBroker` wird per MEF in den Provider importiert und an die Source weitergegeben; `DoDismiss()` muss in `InitializeCompletion` (UI-Thread) aufgerufen werden – **nicht** in `GetCompletionContextAsync` (Background-Thread), sonst wird die eigene Session abgebrochen

### 4. CSS Variable Store
`CssVariableStore` ist ein thread-sicherer Singleton. Er speichert alle Vorkommen jeder `--variable` mit:
- `FilePath` (absoluter Pfad)
- `ProjectName` (Name des `.csproj` ohne Extension)
- `LineNumber` (1-basiert)
- `Value` (Rohwert)

Zwei interne Dictionaries: `_fileDefinitions` und `_fileNameIndex` (beide keyed by Dateipfad).

### 5. CSS Class Store
`CssClassStore` ist ein thread-sicherer Singleton analog zu `CssVariableStore`. Er speichert alle Klassen-Selektoren aus `.css`-Dateien als `CssClassDefinition` (FilePath, ProjectName, LineNumber). Kein Value-Feld (bei Klassen nicht sinnvoll). Sortierung in `GetDefinitions`: Projekt → Zeilennummer → Dateiname.

### 6. Startup-Scan
Das Package (`CssToolsPackage`) lädt bei Lösungsöffnung automatisch (`[ProvideAutoLoad]` mit `SolutionExistsAndFullyLoaded`). Es iteriert alle Projekte der Solution via `IVsSolution` + `IVsHierarchy` und scannt jede `.css`-Datei in **beide** Stores (`CssVariableStore` und `CssClassStore`).

### 7. FileSystemWatcher
Pro Projektverzeichnis läuft ein `FileSystemWatcher` auf `*.css` (rekursiv) – Änderungen, Neuanlagen, Umbenennungen und Löschungen aktualisieren **beide** Stores sofort.

### 8. Konfiguration (`.csstools.json`)
Im **Solution-Root** (neben der `.sln`) kann eine `.csstools.json` liegen:
```json
{
  "exclude": [ "**/themes/**", "**/PlaywrightTests/**" ],
  "include": [ "**/themes/light.css" ]
}
```
- `exclude`: Glob-Patterns – matching Dateien werden nicht gescannt
- `include`: überschreibt `exclude` (include gewinnt immer)
- Wird beim Start geladen; ein `FileSystemWatcher` auf den Solution-Root erkennt Änderungen und ruft `Reload()` → `PurgeExcluded()` auf beiden Stores auf
- Wichtig: `IsInitialized`-Flag verhindert, dass der MEF-Provider Puffer scannt bevor die Config geladen ist

### 9. Menübefehl „Show CSS Variables"
Unter **Tools → Show CSS Variables** werden alle bekannten Variablen in ein dediziertes Output-Window-Pane geschrieben, gruppiert nach Variablenname → Projektname → Datei:Zeile.

## Dateistruktur

| Datei | Aufgabe |
|---|---|
| `CssToolsPackage.cs` | Package-Entry-Point, Startup-Scan (beide Stores), FileSystemWatcher, Command-Init |
| `CssVariableStore.cs` | Thread-sicherer Singleton-Store, Modell `CssVariableDefinition` |
| `CssClassStore.cs` | Thread-sicherer Singleton-Store, Modell `CssClassDefinition` |
| `CssToolsConfig.cs` | Config-Singleton, JSON-Parser (`JavaScriptSerializer`), Glob→Regex-Matcher |
| `CssVariableQuickInfoSource.cs` | Hover-Logik für CSS-Variablen, baut ContainerElement mit Projekt-Gruppen |
| `CssVariableQuickInfoSourceProvider.cs` | MEF-Export für CSS-Variablen, alle relevanten Content-Types |
| `CssVariableCompletionSource.cs` | `IAsyncCompletionSource`: Trigger-Erkennung, Item-Liste, Description-Tooltip, Legacy-Session-Dismiss |
| `CssVariableCompletionSourceProvider.cs` | MEF-Export `IAsyncCompletionSourceProvider`, importiert `ICompletionBroker` |
| `CssClassQuickInfoSource.cs` |
| `CssClassQuickInfoSourceProvider.cs` | MEF-Export für CSS-Klassen, alle relevanten Content-Types; scannt nur CSS-Buffer |
| `NavigationHelper.cs` | Öffnet Datei + springt zu Zeilennummer via `IVsUIShellOpenDocument` |
| `ShowVariablesCommand.cs` | Menübefehl + Output-Window-Pane |
| `CssTools.vsct` | VSCT-Kommandotabelle für den Tools-Menüeintrag |

## Wichtige technische Details

- **MEF Content-Types**: `CSS`, `text/x-css`, `CSharp`, `Razor`, `RazorCSharp`, `RazorCoreCSharp`, `HTML`, `htmlx`, `HTMLX`
- **Projektname-Erhaltung**: Der MEF-Provider kennt keinen Projektnamen. Er liest ihn via `GetProjectName(filePath)` aus dem jeweiligen Store und überschreibt ihn nie mit `""`.
- **Glob-Matching**: Case-insensitive, `**` = beliebige Ordnertiefe, `*` = beliebige Zeichen ohne `/`
- **Assembly-Referenzen**: `System.ComponentModel.Composition` (MEF), `System.Web.Extensions` (JavaScriptSerializer), `PresentationFramework` + `PresentationCore` (WPF, für `IWpfTextView.VisualElement`)
- **Zeilennummer-Ermittlung**: Binärsuche auf einer vorgefertigten Line-Start-Tabelle aus dem Dateiinhalt
- **Config-Watcher** liegt im Solution-Root, **nicht** in Projektordnern
- **CSS-Klassen-Buffer-Scan**: `CssClassQuickInfoSourceProvider` scannt nur CSS-Buffer (Klassen sind dort definiert); HTML/Razor-Buffer werden nicht gescannt, liefern aber Tooltips via Store-Lookup

## Bekannte Eigenheiten

- Der MEF-Provider läuft früher als das Package. Vor `IsInitialized == true` werden Puffer-Scans übersprungen.
- `PurgeExcluded()` muss nach jedem `Reload()` auf **beiden** Stores aufgerufen werden.
- `IVsProject.GetMkDocument` gibt für einige Hierarchy-Items keinen Pfad zurück (z.B. Solution-Folder-Nodes) – das wird per `== 0` Check und `File.Exists` abgefangen.
- Razor `@(cond ? "a" : "b")` innerhalb von `class="..."`: Der Scanner muss balancierten Klammern mit `SkipBalancedParens` + `SkipStringLiteral` folgen, sonst werden innere Anführungszeichen als Attribut-Ende fehlgedeutet.


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
