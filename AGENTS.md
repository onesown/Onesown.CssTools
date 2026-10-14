# CssTools – Anweisungen für KI-Agenten

Visual-Studio-Erweiterung (VSIX, .NET Framework 4.7.2, in-proc): IntelliSense, Hover und Navigation für CSS-Variablen und CSS-Klassen. Fachliche Details und Code-Regeln stehen in `.github/copilot-instructions.md` – zuerst lesen.

## Verteilung

Die Erweiterung wird über **GitHub-Releases** verteilt. Das Onesown Desktop Dashboard liest das neueste Release,
zeigt bei allen Entwicklern „Update verfügbar“ und installiert die `.vsix` über den VSIXInstaller.

- `.github/workflows/build.yml` baut jeden Push und Pull Request (kein Release).
- `.github/workflows/release.yml` baut bei einem Tag `v*` und hängt genau **eine** `.vsix` an das Release
  (`CssTools-X.Y.Z.vsix`). Die Version kommt aus dem Tag; der Workflow schreibt sie selbst in die Versionsdateien.

## Release – nur auf ausdrückliche Anweisung des Benutzers

1. Version anheben – höher als das letzte Release, sonst zeigt das Dashboard kein Update:
   - `CssTools/source.extension.vsixmanifest` (`Identity/@Version`)
2. Committen und pushen.
3. Tag mit derselben Version pushen: `v1.2.3` (zwei bis vier Stellen), `git tag vX.Y.Z` und `git push origin vX.Y.Z`.
4. Unter „Actions“ prüfen, dass der Lauf „Release“ grün ist und die `.vsix` am Release hängt (`gh run list`, `gh release view`).

Versionsdateien und Tag immer gleich halten. Keine Releases von Hand hochladen, keinen Tag wiederverwenden.

## Regeln

- Commit, Push und Tag nur auf ausdrückliche Anweisung des Benutzers.
- `InstallationTarget` im Manifest umfasst Community, Professional und Enterprise; nicht wieder einschränken.
- Kein `DeployExtension` im Release-Build; lokal installiert wird über das Dashboard oder den VSIXInstaller.
