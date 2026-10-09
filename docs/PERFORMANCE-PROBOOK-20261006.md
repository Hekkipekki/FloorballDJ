# ProBook — manuell mätning av P4.12, 6 oktober 2026

Den inskickade mätningen är giltig för analys. Den kör **0.40.0-rc.1 / phase4-persistent-deck-v1**, standardvägen **legacy**, och avslutas med exitkod 0. Sammanfattningen anger 24 784 skrivna händelser, noll tappade/kasserade och `CaptureClosed`. Den omfattar cirka 117 sekunders loggning, **16 lyckade låtstarter**, fyra lyckade sparningar och en öppning av slumpinställningarna som avbryts. Inga uppspelnings-/sparfel registreras.

Källa: `20261006-000908-manual-profile-legacy-30ca923a-20261006T001219Z-1-001.zip`, SHA-256 `65F92D76C526A0F137BF13798952C0B7C332A82F4C4601269CEE7F875F9D5855`. Kopia och omräknad rapport finns under `artifacts/performance/probook-20261006-p412/20261006-000908-manual-profile-legacy-30ca923a/`. Råarkivet ändras inte och dess innehåll behandlas som mätdata.

## Testförutsättningar

HP ProBook 440 G7, i5-10210U (4/8), cirka 16 GB RAM och 1920 × 1080 enligt inventeringen. Profilen har 15 deck, 554 ljudjinglar och 698 platser. Loggen anger energiplansnamnet ”Hög prestanda”. Körningen registrerar MP3 vid både 44,1 och 48 kHz samt två WAV-starter, alla stereo/primär/Solo.

Användaren uppger separat: **molnsynkad mapp med offlineåtkomst, batteridrift**. Upplevelsen var relativt snabb och möjligen lite snabbare, men ingen säker före/efter-bedömning. Originalets `powerSource` är `unknown`; användaruppgiften ersätter inte råfältet. Offlineåtkomst innebär inte att vi har mätt vad synktjänstens filsystemfilter gör vid metadatakontroller. Vi fastställer ingen specifik moln-/drivrutinsorsak från denna logg.

## Den största tydliga snabbtangentskostnaden

Samtliga 16 starter kommer från slumpval. `MainWindow.xaml.cs` hämtar indexerade medlemmar och anropar sedan `JingleShortcutIndex.ExistingAudio` för både pool och följdljud. Den senare metoden går igenom samtliga potentiella medlemmar med färsk `File.Exists`. Detta arbete körs synkront i tangentbordshanteraren på UI-tråd 2.

| Gruppstorlek | Starter | Filkontroll, intervall | Från hanterad tangent till första providerbuffert |
| --- | --- | --- | --- |
| 248 medlemmar | 5 | 223,67–464,16 ms | 358,79–600,35 ms |
| 1–28 medlemmar | 11 | 1,97–40,86 ms | 60,15–119,90 ms |

Den stora gruppens fem kontroller tog 281,32, 243,23, 228,80, 223,67 och 464,16 ms. Alla 248 medlemmar var tillgängliga i varje sådan observation. Uppslagsindexet tar däremot bara omkring 0,066 ms i median över samtliga tryck. Filkontrollspannet är därför ett konkret foregroundarbete att minska; snabbare indexering ensam löser inte dessa stora grupptryck.

Det är olika låtar/klipp, inte ett kontrollerat experiment mellan gruppstorlekar. Även output-/filöppning bidrar: outputinitialiseringens median är 21,88 ms och max 126,01 ms. Hela hanterare-till-första-buffert-medianen är 97,34 ms, max 600,35 ms. Första signal över den post-fader-tröskeln har median 254,69 ms och påverkas av bland annat fade/ljudinnehåll. Dessa buffertmått är inte fysisk tangent-till-högtalare-latens och inkluderar inte föregående OS-/WPF-inputkö.

## Slumpinställningar och gränssnitt

Den enda öppningen når förberedelseskärm/FirstContentRendered efter 156,18 ms och Ready efter **1 006,13 ms**. Bakgrundsförberedelsen tar 553,85 ms och gör 492 färska, unika filprober. Draft-/bindningsstegen tar 7,78/24,81 ms; dessa och Ready är överlappande mått och ska inte summeras. Modal livstid 19,74 sekunder omfattar användarens tid i fönstret, inte öppningsfördröjning. Gruppen realiseras en gång; inget faktiskt gruppbyte kvalificerar kortåteranvändningen som införs i P4.13.

Periodisk dispatcherköfördröjning når 2 570,87 ms. Det bevisar att en lång UI-väntan förekommer i körningen, men loggen innehåller inga metodstackar som förklarar varje sådan topp. Vi tillskriver inte hela toppen en viss kodrad eller fönsteröppning.

## Slutsats och nästa prioritet

Testet gjordes rätt och ger användbar verklig hårdvaruevidens. Reportens varning om noll upprepade starter betyder att ingen samma-jingle-repetition registrerats; den gör inte den här manuella körningen ogiltig. Sexton unika slumpstarter räcker för att identifiera kostnader, inte för stabila svanspercentiler eller hela 1.0-releasegrinden.

Efter den separata P4.13-leveransen prioriteras **slumpvalets fulla synkrona filkontroll**, inom det redan planerade snabbtangents-/ljudarbetet. Undersök validering av kandidater vid behov med bevarad färsk missing-file-hantering, ospelad-prioritet, variation, fallback, upprepad tangent/fade och följdljud. Inför ingen gammal tillgänglighetscache som ändrar dessa regler. En alternativ bakgrundsberedning behöver tydligt ägarskap, avbrytning och skydd mot ändrad profil/aktiv grupp.

Någon säker före/efter-vinst gentemot det tidigare ProBook-arkivet fastställs inte: låtar/grupper, energiläge och förutsättningar är inte matchade. Nästa jämförelse bör använda samma stora och lilla grupp, samma filer/utgång, strömläge och testsekvens. Aktuell mätning är P4.12; den kvalificerar inte P4.13:s kod, fysisk ljudstart, alla enheter eller långtidsbruk.


Uppföljning 2026-10-06: [P1.7](PERFORMANCE-PHASE1-RANDOM-VALIDATION.md) är nu infört och kontrollerat för att minska den identifierade primärscanningen. Den ursprungliga mätningen är fortsatt P4.12 och kvalificerar inte den nya kodens faktiska laptopvinst.
