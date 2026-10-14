# CssTools

Visual-Studio-Erweiterung (VS 2022 ab 17.14 und VS 2026; Community, Professional, Enterprise) für CSS Custom Properties (`--variablen`) und CSS-Klassen in `.css`, `.razor`, `.html` und `.cs`: Hover-Tooltips mit allen Definitionen der Solution, anklickbare Sprünge zur Definition und IntelliSense nach `--`.

- Beschreibung für Benutzer: [`CssTools/overview.md`](CssTools/overview.md) (auch die Marketplace-Seite)
- Aufbau und Regeln für Agenten: [`.github/copilot-instructions.md`](.github/copilot-instructions.md)

## Installation

**Empfohlen: über das Onesown Desktop Dashboard.** Es zeigt die Erweiterung in der Update-Zentrale, installiert sie aus dem neuesten GitHub-Release und meldet neue Versionen.

Von Hand: `CssTools-X.Y.Z.vsix` aus den [Releases](https://github.com/onesown/Onesown.CssTools/releases) laden, Visual Studio schließen und die Datei doppelklicken.

## Release

1. Version in `CssTools/source.extension.vsixmanifest` (`Identity/@Version`) anheben – höher als das letzte Release, sonst zeigt das Onesown Desktop Dashboard kein Update.
2. Committen und pushen.
3. Tag mit derselben Version pushen: `git tag vX.Y.Z` und `git push origin vX.Y.Z`.
4. Unter „Actions“ prüfen, dass der Lauf „Release“ grün ist und am Release `CssTools-X.Y.Z.vsix` hängt.

Der Workflow `.github/workflows/release.yml` schreibt die Version aus dem Tag ins Manifest, baut im Release-Modus und erstellt das GitHub-Release. Maßgeblich ist also der Tag; Manifest und Tag trotzdem gleich halten, damit lokale Builds dieselbe Version tragen. Das Dashboard zeigt und installiert die neue Version bei allen Entwicklern. Jeder Push und Pull Request wird zusätzlich von `build.yml` gebaut – ein Release entsteht nur durch einen Tag.
