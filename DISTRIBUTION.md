# Distribution av FloorballDJ

## Bygg en lokal Windows-installerare

Kör från projektroten:

```powershell
.\scripts\Build-Installer.ps1 -Version 0.40.0-beta
```

Färdig installationsfil och SHA256-kontrollsumma skapas i:

```text
artifacts\installer\0.40.0-beta\FloorballDJ-Setup.exe
artifacts\installer\0.40.0-beta\FloorballDJ-Setup.exe.sha256
```

Installationen använder normalt `Program Files` och visar Windows UAC så att tidigare installationer kan uppgraderas på samma plats. Installationsguiden kan även växlas till installation endast för aktuell användare, som då använder användarens lokala programmapp. Profiler, autosparningar, loggar och användarens ljudfiler tas inte bort vid uppgradering eller avinstallation.

## GitHub Releases

Arbetsflödet `.github/workflows/release.yml` kan startas manuellt med ett versionsnummer eller automatiskt genom att pusha en tagg som `v0.40.0-beta`. Det skapar en GitHub Release med:

- `FloorballDJ-Setup.exe`
- `FloorballDJ-Setup.exe.sha256`

Om källkoden ska vara privat rekommenderas ett separat offentligt repository, exempelvis `Hekkipekki/FloorballDJ-Releases`, som endast innehåller releasefiler. Skapa då:

- Repository variable `RELEASE_REPOSITORY` med värdet `Hekkipekki/FloorballDJ-Releases`.
- Repository secret `RELEASE_REPO_TOKEN` med en finbegränsad GitHub-token som endast får skriva Contents i release-repositoryt.

Utan dessa två inställningar publiceras releasen i samma repository som källkoden.

En stabil nedladdningsadress kan därefter användas på webbplatsen:

```text
https://github.com/Hekkipekki/FloorballDJ-Releases/releases/latest/download/FloorballDJ-Setup.exe
```

## Kodsignering

Köp ett betrott Authenticode-certifikat från en erkänd utfärdare. Lägg aldrig certifikatet eller lösenordet i källkoden.

Skapa följande GitHub Actions-secrets:

- `WINDOWS_SIGNING_CERTIFICATE_BASE64`: PFX-filen base64-kodad.
- `WINDOWS_SIGNING_CERTIFICATE_PASSWORD`: PFX-filens lösenord.

Om dessa secrets finns signeras `FloorballDJ.exe`, installationsfilen och avinstalleraren automatiskt med SHA-256 och en RFC 3161-tidsstämpel. Utan secrets skapas en fungerande men osignerad beta-installerare.

För lokal signering:

```powershell
$env:FLOORBALLDJ_SIGN_PFX = "C:\secure\FloorballDJ.pfx"
$env:FLOORBALLDJ_SIGN_PASSWORD = "lösenord"
.\scripts\Build-Installer.ps1 -Version 0.40.0 -Sign
```

Inno Setup 7 har särskilda kommersiella licensvillkor. Kontrollera och köp den licens som krävs innan FloorballDJ säljs kommersiellt.
