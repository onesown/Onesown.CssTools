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

## Marketplace – nur auf ausdrücklichen Auftrag („Veröffentliche Version X im Marketplace“)

Die Erweiterung steht öffentlich im Visual Studio Marketplace als `onesown.CssTools11`
(https://marketplace.visualstudio.com/items?itemName=onesown.CssTools11). Publish-Manifest: `vs-publish.json`.
Ausführliche Anleitung: Wissensdatenbank onesown, Artikel 4435 „Visual-Studio-Erweiterungen per LLM im Marketplace
veröffentlichen“ (https://kb.onesown.at/Artikel/Details?Id=4435).

1. Erst nach grünem GitHub-Release: dieselbe `.vsix` laden (`gh release download vX.Y.Z -p "*.vsix"`) und die Version
   im enthaltenen `extension.vsixmanifest` prüfen.
2. Öffentliche Version lesen (ohne Anmeldung): `POST https://marketplace.visualstudio.com/_apis/public/gallery/extensionquery`
   mit `filterType 7` = `onesown.CssTools11`. Ist die Version schon veröffentlicht, nicht erneut hochladen.
3. `VsixPublisher.exe` über vswhere finden: `<installationPath>\VSSDK\VisualStudioIntegration\Tools\Bin\VsixPublisher.exe`.
4. `VsixPublisher.exe publish -payload <vsix> -publishManifest vs-publish.json` – ohne Token-Parameter; die Anmeldung
   hat der Benutzer lokal erledigt. Bei `VsixPub0007` (nicht angemeldet) den Benutzer bitten, sich laut Artikel 4435
   lokal anzumelden. Den Token nie im Chat anfordern oder auslesen.
5. Danach Validierung der neuen Version und den öffentlichen Download prüfen (direkt nach dem Upload kann der Download
   noch kurz mit HTTP 500 antworten – warten, nicht erneut hochladen).

- `identity.internalName` (`CssTools11`) und `publisher` (`onesown`) im Publish-Manifest müssen zum bestehenden Eintrag passen.
- Der interne Name ist `CssTools11` (nicht `CssTools`) – nicht ändern, sonst entsteht ein zweiter Eintrag.
