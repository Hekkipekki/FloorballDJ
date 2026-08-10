# FloorballDJ

FloorballDJ är en modern jingle cart för sportevenemang. Första versionen är en inbyggd Windows-app byggd med C# 14, .NET 10 LTS, WPF Fluent och NAudio/WASAPI.

## Starta

```powershell
dotnet run --project src/FloorballDJ/FloorballDJ.csproj
```

## Det som fungerar i grunden

- dynamiskt antal decks, rader och kolumner utan att jinglar raderas när layouten krymper
- val av Windows-utgång och programvolym i dB
- samtidiga jinglar med Mix, Solo och Duck
- stopp, paus/fortsätt, fade ut, speltid, återstående tid och tvåkanalig nivåmätare
- ljudinläsning, kopiera/klistra in, ta bort från knapp och sessionsräkning
- egenskapsvy med vågform, klickbara start/slutmarkörer, preview och borttagning av tyst start/slut
- färg-, effekt-, loop-, fade- och snabbtangentsfält i det öppna projektformatet
- autosparning, manuell backup och import av Snap Jingle Player XML

Projekt sparas som indenterad UTF-8 JSON med ändelsen `.floorballdj.json`. Det gör filerna lätta att versionshantera, dela och redigera manuellt.

## Arkitektur

- `Models` innehåller det stabila och delbara projektformatet.
- `Services/AudioEngine.cs` isolerar uppspelningen från gränssnittet.
- `Services/ProjectService.cs` hanterar sparning och backup.
- `Views` innehåller huvudfönster, inställningar och ljudegenskaper.
- `Controls/WaveformControl.cs` ritar vågformen med WPF:s GPU-accelererade renderingsyta.

## Planerade produktionssteg

1. DSP för pitch, tempo och rate samt sample-exakt loop.
2. Automatisk omlänkning av flyttade ljudfiler och mappbevakning.
3. Global snabbtangentmotor och MIDI/styrpanel.
4. LUFS-integrerad loudnessanalys, true peak och normaliseringsförslag.
5. Ångra/gör om, radfärger, drag-and-drop och full sessionlogg.
6. Automatiska tester, crash recovery, MSIX/installer och signerade releaser.

## Bygg installerare

När Inno Setup 7 finns installerat kan en komplett Windows-installerare byggas med:

```powershell
.\scripts\Build-Installer.ps1 -Version 0.40.0-beta
```

Se `DISTRIBUTION.md` för GitHub Releases, kontrollsummor och kodsignering.
