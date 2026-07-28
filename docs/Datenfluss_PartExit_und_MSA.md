# Datenfluss: Part-Exit (ST160) und MSA

**Stand:** 2026-07-28 (Soll-Konzept umgesetzt) · **Grundlage:** Quellcode `HarryDataServerV2`
(Branch `main`), Live-Konfiguration `F:\002_Configs\Harry.ini`, Live-Logs `F:\004_Logs`, Live-Bestand
auf `Z:\`, `X:\`, `Y:\`.

Jede Aussage ist mit *(Datei.Methode)* belegt. Werte in **fett** stammen aus der laufenden `Harry.ini`.
Dieses Dokument beschreibt den **Soll-Zustand nach der Umstellung vom 28.07.2026** (Ordner-Rollen von
Philipp festgelegt). Passagen, die einen bewusst geänderten Zustand markieren, sind mit
**[NEU 28.07.]** gekennzeichnet.

---

## 0. Verbindliche Ordner-Rollen (Soll-Konzept, Philipp 28.07.2026)

| Ordner | Rolle |
|---|---|
| **01_Low_Resolution_Individual\Input** | Kamera legt Low-Res je Teil ab. **Der einzige Ordner, in dem Part-Exit-Bildaktionen suchen und löschen** (OK/NG/DE/Unknown). |
| **02_Low_Resolution_Collage\Input** | Ziel der Collagen (wenn `Collage_Generate=true`). |
| **03_High_Resolution_NG** | Kamera legt NG-Vollbilder ab. **Das Programm fasst 03 NIE an** — nur die Retention (30 Tage, Tagesordner). |
| **04_High_Resolution_Diagnostic** | Dritte Schwelle bei OK-Teilen (zusätzliches Vollbild). **Das Programm fasst 04 NIE an** — nur die Retention. |
| **05_High_Resolution_GoldenSample\Input** | Erste Schwelle (GoldenSample/MSA). **Transit-Puffer:** ein MSA-Abschluss **verschiebt** die Laufbilder nach `X:`; alles, was übrig bleibt (Abbrüche **und** die M1X-Produktions-`.png` — bewusst so entschieden), löscht die Retention nach **3 Tagen**. |
| **06_Backup** | Ziel der OK-Bilder, **nur wenn keine Collage erzeugt wird**; Retention 30 Tage. |

> **NAS-Sortierung ist AUS [NEU 28.07.]:** in 01–05 entstehen keine neuen `JJJJ\MM\TT`-Tagesordner
> mehr; vorhandene sind Altbestand. Alle Suchen laufen weiterhin **rekursiv ab der Ordnerwurzel**
> (`ImageFileName.SortedRoot`) und tolerieren die Alt-Tagesordner.

## 0b. Live-Konfiguration (die Werte, die das Verhalten steuern)

| Schlüssel | Live-Wert | Wirkung |
|---|---|---|
| `[Collage] Collage_Generate` | **false** | **Der einzige Schalter für das OK-Verhalten [NEU 28.07.]:** `false` → OK-Bilder werden nach `Z:\06_Backup\JJJJ\MM\TT` **verschoben**; `true` → Collage bauen, Originale **ersatzlos löschen**. Aktuell wird keine Collage erzeugt (`CollageService.StartAsync`). |
| `[NAS] DeletePictures` | **entfernt [NEU 28.07.]** | Abgekündigt und **ignoriert**. Ist der Key noch gesetzt, folgt beim Start eine WARNUNG (`IniConfigManager.ParseRetention`); das Verhalten folgt allein `Collage_Generate`. |
| `[NAS] BackupFolder` | **Z:\06_Backup** | Ziel des OK-Verschiebens, Unterbaum `JJJJ\MM\TT` (`ImageHandler.MoveToBackup`) |
| `[NAS] LowResIndividualPath` | **Z:\01_Low_Resolution_Individual\Input** | **Die einzige Suchwurzel aller Part-Exit-Bildaktionen [NEU 28.07.]** |
| `[NAS] HighResNGPath` | **Z:\03_High_Resolution_NG\Input** | nur Retention (`Images_NG`) — **keine** Part-Exit-Aktion mehr |
| `[NAS] HighResDiagnosticPath` | **Z:\04_High_Resolution_Diagnostic\Input** | nur Retention — **keine** Part-Exit-Aktion mehr |
| `[NAS] HighResGoldenSamplePath` | **Z:\05_High_Resolution_GoldenSample\Input** | MSA-Transit-Puffer; Quelle des **Verschiebens** (`MsaRunImages.Move`) |
| `[Collage] Collage_SingleImages` | **Z:\01_…\Input** | Bildquelle aller Part-Exit-Aktionen (Vorrang vor `LowResIndividualPath`) |
| `[Collage] Collage_ResultImages` | **Z:\02_Low_Resolution_Collage\Input** | Collage-Ziel (aktuell ungenutzt, weil `Collage_Generate=false`) |
| `[CSV] CSV_BasePath` | **Y:\02_CSV_Merge** | Produktions-CSV |
| `[CSV] CSVMSA_Save` | **entfernt [NEU 28.07.]** | Abgekündigt und ignoriert (MSA-Summary-CSV entfällt) → WARNUNG, falls gesetzt |
| `[CSV] CSV_MSAPath` | **Y:\01_CSV_Evaluation** | **Es wird nichts mehr dorthin geschrieben [NEU 28.07.]** — der Wert dient nur noch als Retention-Wurzel für den Altbestand (`CSV_Evaluation`) |
| `[CSV] CSV_DiagnosticPath` | **Y:\03_CSV_ExtraResults** | Diagnose-CSV |
| `[CSV] DataSetsPerFile` | **10000** | Zeilen je CSV-Datei |
| `[MSA] ReportPath` | **X:\MSA_Reports** | **Der einzige datei-basierte MSA-Ablageort:** PDF/RAW/IMG je Lauf |
| `[MSA] ReportFallbackPath` | **F:\003_Deploy\MSA_Reports_Fallback** | wenn `X:` nicht schreibbar |
| `[MSA] ReferencePath` | **F:\002_Configs\MSA_References** | Referenzdateien (nur Eingang) |
| `[General] SerialNumberLength` | **19** | Rahmenserie |
| `[General] TrimmerSerialNumberLength` | **13** | Trimmerserie |
| `[Retention]` | NG/Diag/Collage/Backup **30**, **GoldenSample 3 [NEU 28.07.]**, InputLeftovers **3**, Database_Production **35**, **Database_MSA=0**, **Reports_MSA=0**, CSV_Evaluation **365**, CSV_Merge **365**, CSV_ExtraResults **90** | 0 = **NIE** löschen |

---

# Teil 1 — Was passiert beim Abschlusssignal (Part-Exit ST160)?

## 1.1 Ablauf Schritt für Schritt

1. **Telegramm-Empfang.** Die SPS schickt auf Kanal 2 (**Port 6001**, `[SPS] PortPartExit`) ein
   `;`-getrenntes Telegramm mit 15 Feldern; Frame-Ende ist `\r`
   (`TcpSpsServer.HandleConnectionAsync`). Ist ein Orchestrator registriert — im Betrieb immer, er
   trägt sich beim Start ein (`PartExitOrchestrator.StartAsync`: `_sps.PartExitHandler = HandleAsync`) —
   wird die Antwort **zurückgehalten, bis die komplette Verarbeitung durch ist**
   (`TcpSpsServer.HandleConnectionAsync`, Zweig `channel == SpsChannel.PartExit`).

2. **Parsing.** `SpsPartExitData.TryParse` teilt an `;`. **Weniger als 15 Felder → `null`**
   → WARNUNG *„SPS PartExit: malformed telegram"* und Antwort `<32×'0'>;false`
   (`TcpSpsServer.HandlePartExitAckAsync`). Sonst werden alle 15 Felder übernommen.

3. **Normalisierung der Serien.** Im selben Schritt:
   `Szid = SerialNumberHelper.Normalize(Feld 1)` → auf **19** gekürzt, aber **nur wenn der Rest
   hinter Stelle 19 ausschließlich `0` ist**; `VirtualSerial = SerialNumberHelper.NormalizeTrimmer(Feld 2)`
   → analog auf **13**. Der **DMC (Feld 0) wird NICHT normalisiert** (breiteres Feld)
   (`SpsPartExitData.TryParse`, `SerialNumberHelper.NormalizeTo`). Leeres Feld → leerer String.

4. **Protokollierung.** Eine INFO-Zeile mit DMC, SZID, Auftrag, Modus, Ergebnis **und dem
   Roh-Telegramm** (`TcpSpsServer.HandlePartExitAckAsync`).

5. **Weiche 1 — MSA-Testteil.** Ist `Mode` ∈ {`MSA1`,`MSA3`,`LimitSample`} (`SpsPartExitData.IsMsa`),
   endet die Verarbeitung sofort mit ACK `true`: **keine DB-Zeile, keine CSV-Zeile, keine Bildaktion**
   (`PartExitOrchestrator.HandleAsync`, erster Block).

6. **Weiche 2 — DE (Feld 14 = `DE`).** `PartResult.Deleted` → `RunImagesAsync("DE", …, Delete)`:
   Suchschlüssel sind **SZID (19) und/oder Trimmerserie (13)**, gesucht wird **ausschließlich** in
   `Z:\01_Low_Resolution_Individual` **[NEU 28.07. — 03 und 04 sind aus den Suchwurzeln
   entfernt]**, rekursiv via `ImageFileName.SortedRoot` (also `\Input` **und** die Alt-Tagesordner).
   Treffer = Schlüssel ist **Präfix von Serial1**; **MSA-Bilder und unlesbare Namen werden
   übersprungen** (`ImageHandler.Apply`). Danach `return` — **kein `dmcserial`-Insert, keine
   CSV-Zeile** (`PartExitOrchestrator.HandleAsync`, DE-Block).

7. **Weiche 3 — Unknown (Feld 14 weder OK/NG/DE) [NEU 28.07.].** Vor allem Weiteren wird **immer
   eine WARNUNG** mit dem **Rohwert von Feld 14** und dem **Roh-Telegramm** geschrieben
   (`PartExitOrchestrator.HandleAsync`; Rohwert aus `SpsPartExitData.ResultRaw`). Danach läuft das
   Teil wie NG — **aber ohne CSV-Zeile** (siehe Schritt 9).

8. **DB-Schreibvorgang (nur OK/NG/Unknown).** `SaveDmcAsync` schreibt **eine Zeile in `dmcserial`**
   per `INSERT … ON DUPLICATE KEY UPDATE` (Schlüssel `uk_serial` auf `serial_number`).
   `result_status` = **1 (OK) / 0 (NG) / 0 (Unknown)** (`SpsPartExitData.ResultStatusCode`).
   Leere Felder → `NULL`. Serien > 22 Zeichen werden mit WARNUNG gekürzt (`CapSerial`).
   **Ist die SZID leer, wird gar nichts geschrieben [NEU 28.07.]** — stattdessen eine WARNUNG mit
   dem Roh-Telegramm, weil eine Zeile ohne Serie über `uk_serial` mit allen anderen serienlosen
   Teilen in einer einzigen Zeile kollidieren würde (Befund B1). Ist die DB nicht bereit → `false`,
   Health-Meldung, **ACK false**.

9. **Parallelblock.** Anschließend laufen *gleichzeitig* (`Task.WhenAll`):
   **a) CSV** (`CsvExportService.WritePartAsync`) — **nur bei OK und NG; Unknown ist bewusst
   ausgenommen [NEU 28.07.]**, damit in der Produktions-CSV nur Teile mit verstandenem Ergebnis
   stehen;
   **b) Collage** — nur bei OK **und** `Collage_Generate=true` (**aktuell false → entfällt**);
   **c) Bildbehandlung** — **jetzt bei OK, NG und Unknown [NEU 28.07.]**
   (`PartExitOrchestrator.RunImagesAsync`).

10. **DB-Lookups für die CSV.** Beim Zeilenbau werden je Teil zwei Abfragen gefahren
   (`CsvExportService.BuildRowAsync` → `FillMeasurementsAsync`):
   `measurements_serial WHERE serial_number = <SZID>` und — **nur wenn die Trimmerserie nicht leer
   ist** — `measurements_serial_trimmer WHERE serial_trimmer = <Trimmerserie>`.
   Liefert der exakte Treffer 0 Zeilen → WARNUNG *„no rows in … trying prefix fallback"* und ein
   zweiter Versuch mit `LIKE '<serial>%'`; greift der, folgt eine zweite WARNUNG.
   `dmcserial` wird für die CSV **nicht** gelesen — alle Kopffelder kommen direkt aus dem Telegramm.

11. **CSV-Zeile.** 16 Metaspalten + dynamische Messspalten (aus `measurement_definitions`,
    zwei Kopfzeilen: Controller / Variablenname) (`CsvExportService.MetaHeaders`, `FullHeaderRows`).
    Datei: **`Y:\02_CSV_Merge\JJJJ\MM\TT\<TTMMJJ_HHMMSS>_<OrderName>.csv`**
    (`CsvFileWriter.Open`, `dateSubfolders: true`; Label = Auftragsname, sonst `NoOrder`).
    Rotation bei **Auftragswechsel** (`WritePartAsync`) und nach **10000** Zeilen.

12. **Bildbehandlung — eine gemeinsame Maschinerie für alle Zustände [NEU 28.07.].**
    `ImageHandler.ApplyAsync(context, serials, lowResPath, action, backupFolder)` ist die **einzige**
    Implementierung für OK/NG/DE/Unknown (vorher zwei getrennte Wege mit unterschiedlicher Wurzel,
    Extension-Filter und Match-Semantik — Befund B10). Sie sucht **rekursiv ab
    `SortedRoot(Z:\01…\Input)`**, also `\Input` **plus** Alt-Tagesordner, **ohne
    Extension-Filter** (`*.bmp` und `*.png` gleichbehandelt), matcht **feldgenau als Präfix von
    Serial1** und überspringt **MSA-Bilder** (`Serial2 ≠ Nullen`) sowie **unlesbare Namen**.
    Aktion je Zustand:
    * **NG / Unknown / DE → `Delete`** (ersatzlos; der NG-Nachweis ist das Vollbild in 03)
    * **OK mit `Collage_Generate=true` → `Delete`** (die Collage ist der Nachweis)
    * **OK mit `Collage_Generate=false` (Live) → `MoveToBackup`**: Kopie nach
      **`Z:\06_Backup\JJJJ\MM\TT\<Originalname>`**, **Größenvergleich**, dann Löschen des Originals
      (`ImageHandler.MoveToBackup`).
    Jede Löschung/Verschiebung protokolliert **Anzahl + Schlüssel** (INFO); 0 Treffer → WARNUNG mit
    Roh- und Normalschlüssel, Ordner, Anzahl geprüfter Dateien und Anzahl übersprungener MSA-Bilder.

13. **Antwort an die SPS.** `<SZID auf 32 mit '0' aufgefüllt>;true|false\r`; `false`, sobald
    `dmcserial`, CSV, Collage oder Bildbehandlung fehlschlug (`HandleAsync` → `success`;
    `TcpSpsServer.HandlePartExitAckAsync`). Dauert alles zusammen > **450 ms**, folgt eine WARNUNG
    (`PartExitOrchestrator.BudgetMs`).
    **[NEU 28.07.]** Der Orchestrator gibt jetzt `PartExitOutcome(Success, DurationMs)` zurück. Das
    **Wire-Telegramm bleibt byte-identisch**; für die UI-Liste „Last responses" der Part-Exit-Karte
    wird ein **separater Anzeigetext** mit der Dauer gebildet, z. B.
    `2807…778;true (87 ms)` (`TcpSpsServer.HandlePartExitAckAsync` liefert `(Wire, Display)`;
    nur `Display` geht an `ChannelActivity`). Die Dauer ist **dieselbe Messung**, die auch
    `LastTiming` und die 450-ms-Prüfung speist — sie wird nicht neu gemessen.

## 1.2 Flussdiagramm Part-Exit

```
        SPS ST160, Port 6001
                 |
      15 Felder, ';'-getrennt, CR
                 v
   +-------------------------------+
   | SpsPartExitData.TryParse      |
   | Szid  -> Normalize(19)        |
   | VSer  -> NormalizeTrimmer(13) |
   | DMC   -> unveraendert         |
   +-------------------------------+
        | < 15 Felder
        +-------------------> WARN "malformed"  -> ACK <32x'0'>;false   [ENDE]
        |
        v
   Mode = MSA1/MSA3/LimitSample ? --ja--> nichts tun -> ACK ...;true     [ENDE]
        | nein
        v
   Feld 14 = DE ? --ja--> ImageHandler.ApplyAsync("DE", ..., Delete)
        |                  Suche NUR in Z:\01 (rekursiv, inkl. Alt-Tagesordner)
        |                  Treffer: Serial1-Praefix = SZID(19) | Trimmer(13)
        |                  MSA-Bilder + unlesbare Namen: uebersprungen
        |                  -> File.Delete  (KEIN dmcserial, KEINE CSV)
        |                  -> INFO "n low-res image(s) deleted" | WARN wenn 0
        |                                                                [ENDE]
        | nein
        v
   Feld 14 unbekannt ? --ja--> WARN mit Rohwert Feld 14 + Roh-Telegramm
        |                      (danach weiter wie NG, aber OHNE CSV)
        v
   SZID leer ? --ja--> WARN "no dmcserial row" (keine ''-Zeile)   [DB uebersprungen]
        | nein
        v
   SaveDmcAsync -> INSERT ... ON DUPLICATE KEY UPDATE dmcserial
                   result_status = 1 (OK) | 0 (NG) | 0 (Unknown)
        |
        v
   +---------------- Task.WhenAll ----------------------------+
   |                       |                                  |
   v                       v                                  v
 CSV                 Collage (nur OK &&              Bilder (OK / NG / Unknown)
 nur OK + NG         Collage_Generate=true)          ImageHandler.ApplyAsync
 NICHT Unknown       LIVE: false -> entfaellt          Quelle: NUR Z:\01 (rekursiv)
   |                       |                           Treffer: Serial1-Praefix
   | Lookup measurements_serial (SZID)                 MSA + unlesbar: nie
   | Lookup ..._trimmer (nur wenn Trimmer != leer)     kein *.bmp-Filter
   | exakt -> sonst WARN + LIKE 'serial%'                     |
   v                                                          +-- NG/Unknown/DE -> Delete
 Y:\02_CSV_Merge\JJJJ\MM\TT\                                  +-- OK & Collage an -> Delete
 <TTMMJJ_HHMMSS>_<Auftrag>.csv                                +-- OK & Collage aus -> MOVE
                                                                  Z:\01 -> Z:\06_Backup\JJJJ\MM\TT
                                                                  (Kopie + Groessenpruefung + Delete)
   +----------------------------------------------------------+
                 |
                 v
   Wire  : ACK <SZID auf 32>;true|false + CR     <- unveraendert an die SPS
   Anzeige: <dasselbe>;true (87 ms)              <- nur UI "Last responses"
                                                   (> 450 ms -> WARN)
```

## 1.3 Vollständige Statusliste (aus dem Code, nichts angenommen)

Der Part-Exit-Pfad unterscheidet **sieben** Zustände. Fünf davon sind eigene Code-Zweige, zwei sind
Datenvarianten, die das Ergebnis spürbar verändern:

| # | Status | Erkennungsmerkmal im Code |
|---|--------|---------------------------|
| 1 | **OK / Gutteil** | Feld 14 = `OK` → `PartResult.Ok` (`SpsPartExitData.ParseResult`) |
| 2 | **NG** | Feld 14 = `NG` → `PartResult.Ng` |
| 3 | **DE (Ausschuss)** | Feld 14 = `DE` → `PartResult.Deleted`, eigener Zweig in `HandleAsync` |
| 4 | **Unknown** | Feld 14 ist **weder OK/NG/DE** (z. B. leer, Tippfehler) → `PartResult.Unknown`. **[NEU 28.07.] Eigener Zweig:** immer eine WARNUNG mit Rohwert + Roh-Telegramm, `dmcserial` + Bildlöschung wie NG, **aber keine CSV-Zeile** |
| 5 | **MSA-Testteil** | Feld 4 `Mode` ∈ {MSA1, MSA3, LimitSample} → `IsMsa`, Abbruch **vor** allem anderen |
| 6 | **Malformed** | < 15 Felder oder leer → `TryParse` = `null` (`TcpSpsServer.HandlePartExitAckAsync`) |
| 7 | **Blockauswurf / leere Serien-Felder** | Kein eigener Status: ein OK/NG/Unknown-Teil, bei dem **DMC (Feld 0) und/oder VirtualSerial (Feld 2) leer** sind. Live am 28.07.: **167 von 1064** Part-Exits mit leerem DMC (Log `Part Exit: DMC= …`) |

> **Zusatzweg:** `ohne jede Serie` — sind SZID *und* Trimmerserie leer, wird kein Bild angefasst
> (WARNUNG *„… part exit with neither a frame nor a trimmer serial; no image touched."*,
> `PartExitOrchestrator.RunImagesAsync`) und bei OK/NG/Unknown zusätzlich **keine `dmcserial`-Zeile**
> geschrieben (WARNUNG, `SaveDmcAsync`). ACK bleibt `true` — die SPS kann daran nichts ändern.
>
> **Nebenpfad ohne Orchestrator:** Ist kein Orchestrator registriert, beantwortet
> `TcpSpsServer.HandlePartExit` das Telegramm nur mit `"OK"` und feuert das Event `PartExitReceived` —
> **ohne DB, CSV oder Bilder**. Im Betrieb tritt das nicht auf (der Orchestrator registriert sich in
> `StartAsync`), es ist aber der Zustand zwischen Programmstart und Orchestrator-Start.

## 1.4 Statustabelle: DB / CSV / Bilder / Sonstiges

Legende Bildordner: **01** = `Z:\01_Low_Resolution_Individual`, **03** = `Z:\03_High_Resolution_NG`,
**05** = `Z:\05_High_Resolution_GoldenSample`, **Collage** = `Z:\02_Low_Resolution_Collage`,
**06** = `Z:\06_Backup`.

| Status | DB | CSV | Bilder je Ordner | Sonstiges |
|---|---|---|---|---|
| **1 OK** | 1 Zeile `dmcserial` (`result_status=1`), Insert-or-Update auf `serial_number`. `measurements_serial(_trimmer)` werden nur **gelesen**, nie geschrieben (`SaveDmcAsync`, `FillMeasurementsAsync`) | **Ja.** Alle 16 Metafelder aus dem Telegramm; Messspalten aus beiden Messtabellen. DMC/Trimmer leer ⇔ Telegrammfeld leer | **01:** Treffer (Serial1-Präfix SZID 19 / Trimmer 13). **`Collage_Generate=false` (Live) → VERSCHIEBEN:** `Z:\01…\<Datei>` → **`Z:\06_Backup\JJJJ\MM\TT\<Datei>`** (Kopie + Größenvergleich + Original löschen). **`Collage_Generate=true` → ersatzlos LÖSCHEN**, kein Backup. **03/04/05:** **nichts** (werden nie angefasst). **Collage:** nur bei `true` → `Z:\02…\Input\<SZID>_Collage.jpg`. **06:** Ziel des Verschiebens (nur bei `false`) | INFO `OK: n low-res image(s) moved to backup … → <Ziel>` bzw. `… deleted`; INFO `Collage written…` (nur wenn aktiv); WARN bei 0 Treffern mit Roh-/Normalschlüssel + geprüfter Dateizahl + MSA-Skips; WARN > 450 ms |
| **2 NG** | 1 Zeile `dmcserial` (`result_status=0`) | **Ja**, identisch zu OK, Spalte `Result` = `Ng` | **01: [NEU 28.07.] ersatzlos LÖSCHEN** (kein Backup — der NG-Nachweis ist das Vollbild in 03). **03:** die Kamera legt das Vollbild dort ab; **der Server fasst 03 nie an**. **04/05/Collage/06:** nichts | INFO `NG: n low-res image(s) deleted for [<Schlüssel>]`; 0 Treffer → WARN (roh + normalisiert). Die NG↔Low-Res-Retention-Verknüpfung bleibt als **Fallback** (siehe 1.6) |
| **3 DE** | **Keine** DB-Änderung — weder `dmcserial` noch Messzeilen | **Nein** | **01: [NEU 28.07.] nur noch hier** — alle Dateien mit passendem Serial1-Präfix ersatzlos gelöscht (rekursiv inkl. Alt-Tagesordner). **03/04: [NEU 28.07.] aus den Suchwurzeln entfernt** → werden nicht mehr angefasst. **05/Collage/06:** nichts | INFO `DE: n low-res image(s) deleted for [<Schlüssel>]` bzw. WARN mit Roh-/Normalschlüssel, geprüfter Dateizahl, MSA-Skips |
| **4 Unknown** | 1 Zeile `dmcserial` (`result_status=0`, wie NG) | **[NEU 28.07.] NEIN** — die Produktions-CSV bleibt frei von Teilen mit unverstandenem Ergebnis | wie NG: **01** ersatzlos löschen; 03/04/05 nichts | **[NEU 28.07.]** Immer WARN `Part exit with unknown result '<Rohwert>' … NO CSV row. Raw telegram: '<Roh>'` (behebt B2) |
| **5 MSA-Testteil** | **Keine** — Produktionstabellen werden nie berührt | **Nein** | **keine Aktion in irgendeinem Ordner** | Nur Zähler `TotalProcessed`; MSA-Daten laufen ausschließlich über den Kamerapfad (Teil 2) |
| **6 Malformed** | Keine | Nein | Keine | WARN `SPS PartExit: malformed telegram '<Roh>'`, ACK `<32×'0'>;false` |
| **7 Blockauswurf / leere Felder** | **`dmc`/`serial_trimmer` = NULL**. **[NEU 28.07.] Ist die SZID leer, wird KEINE Zeile geschrieben** (WARNUNG mit Roh-Telegramm) — damit kann keine `serial_number=''`-Sammelzeile mehr entstehen (behebt B1) | **Ja** (bei OK/NG). DMC- und Trimmer-Spalte **leer**; der Trimmer-Lookup **entfällt komplett** → alle M2X-Messspalten bleiben leer | Bildsuche mit leerer Schlüsselliste → **[NEU 28.07.]** WARN `… no usable serial (raw [])`, keine Datei angefasst | WARN `Part exit without a frame serial … no dmcserial row written`; ggf. WARN `no rows in measurements_serial …` |

## 1.5 Timing: synchron vs. zeitversetzt

**Synchron am Telegramm** (die SPS wartet auf das ACK):

| Schritt | Beleg |
|---|---|
| Parsing + Normalisierung | `SpsPartExitData.TryParse` |
| `dmcserial`-Insert | `PartExitOrchestrator.SaveDmcAsync` |
| CSV-Zeile inkl. beider Mess-Lookups | `CsvExportService.WritePartAsync` |
| Collage (wenn aktiv) | `CollageService.ComposeForPartAsync` — läuft auf einem Worker-Thread, wird aber **im ACK abgewartet** (`Task.WhenAll`) |
| Bildaktion **aller** Zustände (OK/NG/DE/Unknown) | `ImageHandler.ApplyAsync` über `PartExitOrchestrator.RunImagesAsync`; bei OK mit Collage wird zusätzlich auf die Collage gewartet, damit diese die Bilder vorher lesen kann |

→ **Es gibt keine asynchrone Nachverarbeitung des einzelnen Teils.** Budget 450 ms, Überschreitung = WARNUNG.

> **⚠ Messwert zum Budget (28.07., Live):** eine rekursive Aufzählung von
> `Z:\01_Low_Resolution_Individual` dauert **560 ms bei 6347 Dateien** über die Freigabe — allein
> mehr als das 450-ms-Budget. Heute liefen bereits **2126 von 8674** Part-Exits über Budget (bis
> 1198 ms), obwohl nur OK-Teile (5283) eine Bildaktion hatten. Da NG (3357) und Unknown jetzt
> ebenfalls suchen, ist mit etwa einer **Verdopplung der Budget-Warnungen** zu rechnen. Das ist
> bewusst **nicht** stillschweigend umgebaut worden — Optionen siehe „Auffälligkeiten" B13.

**Zeitversetzt** (`RetentionService`): Start **1 Minute nach Programmstart**, danach **alle 24 h**
(`RetentionService.StartAsync`/`RunAsync`, `Interval = 24 h`, `StartupDelay = 1 min`).
Zusätzlich zeitversetzt: die MSA-Auswertung am Laufende (Teil 2) und die NAS-eigene Einsortierung der
Bilder aus `…\Input` in `JJJJ\MM\TT` (macht das NAS, nicht der Server).

## 1.6 Sweep-Tabelle: was räumt wann wo ab

| Sweep | Live-Wert | Wo | Was genau | Schutzregeln |
|---|---|---|---|---|
| `Images_NG` | **30 Tage** | `Z:\03_High_Resolution_NG\JJJJ\MM\TT` | löscht **ganze Tagesordner** (Datum aus dem **Ordnernamen**) und dazu die verknüpften Low-Res-Bilder in `Z:\01` | Verknüpfung über die **ersten 12 Zeichen von Serial1** auf beiden Seiten; **MSA-Bilder und unlesbare Namen werden nie gelöscht** (`RetentionService.DeleteLinkedLowRes`, `SerialPrefix`). **[NEU 28.07.] Bleibt bewusst als FALLBACK**, obwohl NG jetzt schon am Part-Exit löscht — ein verpasster Part-Exit (Serverneustart, verlorenes Telegramm) oder ein nach dem Part-Exit geschriebenes Bild hinterlässt sonst Waisen, die der Leftover-Sweep in `01` gerade **nicht** aufnimmt (er schont NG-geflaggte Dateien und geht nicht in die Alt-Tagesordner) |
| `Images_Diagnostic` | **30 Tage** | `Z:\04_…\JJJJ\MM\TT` | ganze Tagesordner | — |
| `Images_GoldenSample` | **3 Tage [NEU 28.07., war 30]** | `Z:\05_…\JJJJ\MM\TT` | ganze Tagesordner | Passend zur Transit-Rolle von 05: abgeschlossene Läufe sind verschoben, hier bleibt nichts Aufbewahrungswürdiges (behebt B4) |
| `Images_Collage` | **30 Tage** | `Z:\02_…\JJJJ\MM\TT` | ganze Tagesordner | — |
| `Images_Backup` | **30 Tage** | `Z:\06_Backup\JJJJ\MM\TT` | ganze Tagesordner | — |
| `Images_InputLeftovers` | **3 Tage** | **oberste Ebene** jedes `…\Input` (01, 02, 03, 04, 05) | Einzeldateien älter als 3 Tage = Reste fehlgeschlagener Läufe → löschen + WARNUNG | **In 01–04:** MSA-Bild → `KeepMsa` (nie löschen, WARNUNG); NG-Bild in `01` → `KeepNg`. **[NEU 28.07.] In 05 (Transit-Puffer): MSA-Bilder werden gelöscht** (`isMsaTransitFolder: true`) — abgeschlossene Läufe sind verschoben, was liegt, sind Abbruch-Reste oder M1X-Produktionsbilder. **Überall:** unlesbarer Name → `KeepUnknown`, nie löschen (`RetentionService.ClassifyLeftover`) |
| `Reports_MSA` | **0 = NIE** | `X:\MSA_Reports\JJJJ-MM-TT` | *deaktiviert* | INFO `Retention: Reports/MSA – disabled (0 = never).` |
| `CSV_Merge` | **365 Tage** | `Y:\02_CSV_Merge\JJJJ\MM\TT` | ganze Tagesordner | — |
| `CSV_Evaluation` | **365 Tage** | `Y:\01_CSV_Evaluation\JJJJ\MM\TT` | ganze Tagesordner | **[NEU 28.07.]** Enthält nur noch **Altbestand** — es werden keine MSA-CSVs mehr geschrieben (behebt B5) |
| `CSV_ExtraResults` | **90 Tage** | `Y:\03_CSV_ExtraResults\JJJJ\MM\TT` | ganze Tagesordner | — |
| `Database_Production` | **35 Tage** | `measurements_serial`, `measurements_serial_trimmer` | **DROP PARTITION** (nie DELETE) | Stammdaten (`settings`, `*_definitions`, `cameras`) werden nie angefasst |
| `Database_Production` | **35 Tage** | `dmcserial` | gebündeltes `DELETE … LIMIT 10000` mit Pausen | — |
| `Database_MSA` | **0 = NIE** | `msa_measurements`, `msa_results` | *deaktiviert* | INFO `disabled (0 = never)` |

> **Live-Nachweis:** in **keinem** Log (`F:\004_Logs\*.log`) existiert eine `leftover`-Zeile — der
> 3-Tage-Sweep hat **noch nie** eine Datei gelöscht. Der letzte vollständige Retention-Lauf
> (2026-07-28 11:49) meldet für alle Bild- und CSV-Ziele „0, nothing to do".

## 1.7 Ordnerbaum

NAS-Sortierung ist AUS: neue Tagesordner entstehen nicht mehr, vorhandene sind Altbestand.

```
Z:\   (Bilder-Share des NAS; die Ordner liegen DIREKT unter Z:, nicht unter Z:\Images)
 ├─ 01_Low_Resolution_Individual\   Einzelbilder je Teil (M50 + M2X). DER EINZIGE Ordner, in dem
 │   ├─ Input\                      Part-Exit-Bildaktionen suchen/loeschen (OK/NG/DE/Unknown)
 │   └─ JJJJ\MM\TT\                 Altbestand - wird rekursiv mitgesucht
 ├─ 02_Low_Resolution_Collage\      Ziel der Collagen (aktuell leer: Collage_Generate=false)
 │   ├─ Input\  └─ JJJJ\MM\TT\
 ├─ 03_High_Resolution_NG\          NG-Vollbilder. Kamera schreibt, PROGRAMM FASST NIE AN;
 │   ├─ Input\  └─ JJJJ\MM\TT\      nur Retention Images_NG (30 Tage, Tagesordner)
 ├─ 04_High_Resolution_Diagnostic\  Dritte Schwelle bei OK-Teilen (Zusatz-Vollbild).
 │   ├─ Input\  └─ JJJJ\MM\TT\      PROGRAMM FASST NIE AN; nur Retention (30 Tage)
 ├─ 05_High_Resolution_GoldenSample\ Erste Schwelle (GoldenSample/MSA). TRANSIT-PUFFER:
 │   └─ Input\                       MSA-Abschluss VERSCHIEBT die Laufbilder nach X:;
 │                                   Reste (Abbrueche + M1X-Produktions-.png) -> 3 Tage
 └─ 06_Backup\                      Ziel der OK-Bilder, NUR wenn keine Collage erzeugt wird:
     └─ JJJJ\MM\TT\                 Kopie + Groessenpruefung, danach Loeschen des Originals

X:\   (MSA-Ablage - der EINZIGE datei-basierte MSA-Ort)
 └─ MSA_Reports\
     ├─ JJJJ-MM-TT\<Modul>\<BaseID>\PDF\   die PDF-Reports (bei MSA1/LimitSample je Teil)
     │                            \RAW\   Minitab-Rohexport (*_RawData.csv)
     │                            \IMG\   die Laufbilder, VERSCHOBEN aus Z:\05
     └─ <Modul>\JJJJ-MM-TT\               ALTES flaches Layout vor 2026-07-21 (bleibt liegen)

Y:\   (CSV-Ablage)
 ├─ 01_CSV_Evaluation\JJJJ\MM\TT\<BaseID>\   NUR ALTBESTAND - hier wird nichts mehr geschrieben
 ├─ 02_CSV_Merge\JJJJ\MM\TT\                 Produktions-CSV (eine Zeile je fertigem Teil)
 └─ 03_CSV_ExtraResults\JJJJ\MM\TT\          Diagnose-CSV (aktuell leer)
```

---

# Teil 2 — Wo landen MSA-Daten, und was passiert mit MSA-Bildern?

## 2.1 Alle Ablageorte für MSA-Daten

| Artefakt | Ort / Muster (Live) | Wann | Beleg |
|---|---|---|---|
| **Messwerte** | Tabelle **`msa_measurements`** — Schlüssel `dmc`, `base_id`, `loop_number`, `controller_name`, `definition_id` | **während des Laufs**, je Kameratelegramm; eigene Queue, Flush im Takt `[SQLSettings] SaveIntervalSeconds=1` | `MsaService.OnResultsReceived` (Enqueue), `INSERT INTO msa_measurements` (Z. 293) |
| **Ausgewertete Ergebnisse** | Tabelle **`msa_results`** — Cg/Cgk/%P/T, `passed`, `evaluated`, `reason`, `matched_reference` | **bei Laufende** | `MsaService.StoreResultsAsync` (Z. 1156 ff., `INSERT INTO msa_results` Z. 1173) |
| **PDF-Reports** | `X:\MSA_Reports\JJJJ-MM-TT\<Modul>\<BaseID>\PDF\`<br>`<Modul>_<Typ>_<BaseID>[_<DMC>]_<TTMMJJ_HHMMSS>_AllResults.pdf` und `…_FailuresOnly.pdf`<br>*(MSA1/LimitSample: ein Paar **je Teil/DMC**; MSA3: ein Paar je Lauf)* | bei Laufende | `PdfReportService.ResolvePaths`, `MsaService.GeneratePerPartPdfs` / `GeneratePdf` |
| **Minitab-Rohexport** | `…\<BaseID>\RAW\<Modul>_<Typ>_<TTMMJJ_HHMMSS>_RawData.csv`; Spalten `Controller;BaseID;Loop;DMC;Measurement;Value;Status;Timestamp`, UTF-8 mit BOM | bei Laufende | `MsaService.ExportRawDataAsync` |
| **Laufbilder** | `…\<BaseID>\IMG\<Originaldateiname>` — **[NEU 28.07.] verschoben, nicht kopiert** | bei Laufende | `MsaService.MoveRunImages` → `MsaRunImages.Move` |
| ~~Summary-CSV~~ | ~~`Y:\01_CSV_Evaluation\…`~~ | **[NEU 28.07.] entfällt ersatzlos** | Export entfernt (`MsaService`, Kommentar an der alten Stelle); `[CSV] CSVMSA_Save` abgekündigt. Der **Minitab-RAW-Export** ist die maschinenlesbare Form, die Zahlen stehen zusätzlich in `msa_results`. Altbestand auf `Y:\01` bleibt unangetastet |
| **Referenzdateien (nur Eingang)** | `F:\002_Configs\MSA_References\<Modul>\MSA1\*.json`, `…\<Modul>\LimitSamples\<DMC>.json`, Alt: `MSA_<Modul>.json` | wird **gelesen**, vom Server nie gelöscht | `MsaReferenceLoader`, `[MSA] ReferencePath` |
| **Ausweichpfad** | `F:\003_Deploy\MSA_Reports_Fallback\…` (gleiches Layout) | nur wenn `X:` nicht beschreibbar → WARNUNG | `MsaResultLayout.EnsureWritableReportDir` |

> **Live-Beispiel** (`X:\MSA_Reports\2026-07-24\M20\20260724070500\`): Unterordner `PDF\` und `RAW\`;
> PDF-Name `M20_MSA1_20260724070500_00000000000000000000000000000001_240726_070500_AllResults.pdf`.
> Das Alt-Layout `X:\MSA_Reports\M50\2026-07-21\…` existiert daneben weiter.

## 2.2 MSA-Bilder — kompletter Weg

**Kamera → NAS.** Die Kamera schreibt MSA-Bilder in denselben Aufbau wie Produktionsbilder, nur mit
anderem Feldinhalt: **Serial1 = BaseID + Loop + Auffüllung**, **Serial2 = der auf das Teil gelaserte
DMC** (`ImageFileName`, CLAUDE.md §11). Live belegt an 128 echten MSA-Bildern des Laufs
`50260723165426`:

```
5026072316542600100000-21072615261304011996000000035951-0-M50_ST040_KF1-1-&Cam1Img.png
└──── Serial1 22 ─────┘ └──────── Serial2 32 = DMC ────┘ │ └ Controller ┘ │ └ Bildvariable
   BaseID 50260723165426                                 │                └ Kameranummer
   + Loop 001 + Auffuellung                              └ Gesamtergebnis
```

Diese Bilder lagen in **`Z:\05_High_Resolution_GoldenSample\Input`** — das ist der einzige Ordner,
den der Server als MSA-Bildquelle liest (`[NAS] HighResGoldenSamplePath`).

**Server → Report [NEU 28.07.: VERSCHIEBEN statt kopieren].** Nur **bei Laufende** (nicht
währenddessen) läuft `MsaService.MoveRunImages` → `MsaRunImages.Move`: es wird über
`Z:\05_High_Resolution_GoldenSample` **rekursiv** (inkl. Alt-Tagesordner, via
`ImageFileName.SortedRoot`) gesucht, Treffer = **Serial1 beginnt mit der 14-stelligen BaseID**
(`ImageFileName.MatchesBaseId`), und jede Treffer-Datei wird nach `…\<BaseID>\IMG\`
**verschoben**. Da die Laufwerksgrenze `Z:` → `X:` überschritten wird, ist das
**Kopieren → Größenvergleich → Original löschen**. INFO-Zeile: *„n found, n moved"*.

**Fehlerfall.** Schlägt ein einzelnes Verschieben fehl, folgt eine **WARNUNG** und das **Original
bleibt liegen** (die 3-Tage-Retention räumt es später); der Lauf wird davon **nie** fehlerhaft.
Fehlende/unpassende Bilder sind ebenfalls kein Laufaufmerker.

**Originale.** Nach einem erfolgreichen Lauf liegt in `Z:\05` **nichts mehr** von diesem Lauf — das
ist der Zweck der Transit-Rolle. Vorher wurde kopiert, wodurch die Originale dauerhaft im
Transit-Ordner blieben. Live-Beleg des alten Verhaltens: `2026-07-23 16:56:06 MSA images for BaseID
50260723165426: 128 found, 128 copied into X:\MSA_Reports\2026-07-23\M50\50260723165426\IMG.`

```
   Kamera (MSA-Modus)
        |  Serial1 = BaseID+Loop, Serial2 = DMC
        v
  Z:\05_High_Resolution_GoldenSample\Input\        <- waehrend des Laufs, Bild fuer Bild
        |
        |   (parallel, voellig unabhaengig davon:)
        |   Kameratelegramm -> MsaService.OnResultsReceived -> Queue
        |        -> INSERT msa_measurements            (waehrend des Laufs, 1 s Takt)
        |
        v  SPS sendet "Request;<BaseID>" auf Kanal 3-7  => LAUFENDE
  MsaService.EvaluateAsync
        |
        +-- StoreResultsAsync ......... INSERT msa_results
        +-- GeneratePdf(s) ............ X:\MSA_Reports\JJJJ-MM-TT\<Modul>\<BaseID>\PDF\*.pdf
        +-- ExportRawDataAsync ........ ...\<BaseID>\RAW\*_RawData.csv   (Minitab, ';'-getrennt)
        +-- MoveRunImages ............. VERSCHIEBEN von Z:\05 nach ...\<BaseID>\IMG\
        |                               Treffer: Serial1 beginnt mit BaseID(14)
        |                               Z: -> X: = Kopie + Groessenpruefung + Original loeschen
        |                               Fehler -> WARN, Original bleibt (Retention raeumt spaeter)
        +-- PushMsaResultAsync ........ OK / NG / Error;<Grund> an die SPS

  (KEINE Summary-CSV mehr auf Y:\01 - entfaellt seit 28.07.; Zahlen stehen in msa_results + RAW)
  Nach einem erfolgreichen Lauf ist Z:\05 fuer diesen Lauf LEER (Transit-Puffer).
```

## 2.3 Löschsicherheit — Nachweis je Sweep

Der Nachweis unterscheidet jetzt zwei Zonen: **01–04 = geschützt**, **05 = Transit-Puffer, bewusst
nicht geschützt** (abgeschlossene Läufe sind ja verschoben).

| Vorgang | Fasst MSA-Bilder/-Daten an? | Beleg |
|---|---|---|
| **DE-Löschung** (Part-Exit) | **Nein.** Jede Datei wird geparst; `parsed.IsMsa` (Serial2 ≠ lauter Nullen) → `continue`, Zähler `MsaSkipped`. Zusätzlich wird **nur `Z:\01`** durchsucht | `ImageHandler.Apply` |
| **NG-/Unknown-Löschung** (Part-Exit) | **Nein.** Dieselbe Maschinerie, dieselbe `IsMsa`-Prüfung, dieselbe Wurzel `Z:\01` | `ImageHandler.Apply` |
| **OK-Verschieben/Löschen** (Part-Exit) | **Nein.** Gleiche `IsMsa`-Prüfung → ein MSA-Bild wird auch **nicht** in den Backup-Baum kopiert | `ImageHandler.Apply` |
| **Collage-Quellsuche** | **Nein.** `if (parsed is null \|\| parsed.IsMsa) continue;` (zudem live deaktiviert) | `CollageComposer.FindCandidateFiles` |
| **InputLeftovers in 01–04** | **Nein.** `ClassifyLeftover` liefert `KeepMsa`; zusätzlich WARNUNG „*… MSA image(s) … were KEPT*". Unlesbare Namen → `KeepUnknown`, ebenfalls behalten | `RetentionService.ClassifyLeftover(…, isMsaTransitFolder: false)` |
| **InputLeftovers in 05** | **[NEU 28.07.] Ja — gewollt.** `isMsaTransitFolder: true` hebt die MSA-Ausnahme genau dort auf: nach **3 Tagen** werden dort auch MSA-Bilder gelöscht, weil abgeschlossene Läufe verschoben sind und nur Abbruch-Reste bzw. M1X-Produktionsbilder zurückbleiben. **Unlesbare Namen bleiben auch hier verschont** | `RetentionService.CleanupInputLeftovers("Input/GoldenSample", …, isMsaTransitFolder: true)` |
| **NG → Low-Res-Verknüpfung** | **Nein.** MSA-Bilder liefern keinen Löschschlüssel und werden auch nicht gelöscht | `RetentionService.SerialPrefix`, `DeleteLinkedLowRes` |
| **`Database_MSA` = 0** | **Nein** — `msa_measurements`/`msa_results` werden übersprungen, INFO `disabled (0 = never)` | `RetentionService.BatchDeleteByAgeAsync` (`retentionDays <= 0`) |
| **`Reports_MSA` = 0** | **Nein** — `X:\MSA_Reports` (PDF/RAW/**IMG**) wird nicht angefasst. Damit sind die **verschobenen** Laufbilder dauerhaft geschützt | `RetentionService.CleanupDatedTopFolders` |
| **`Images_GoldenSample` = 3** | **Ja — gewollt**, gleiche Begründung wie InputLeftovers/05: löscht Alt-Tagesordner unter `Z:\05`. Heute existieren dort keine Tagesordner (NAS-Sortierung aus) | `RetentionService.CleanupSortedDayFolders` |
| **`CSV_Evaluation` = 365** | **Betrifft keine neuen MSA-Daten mehr** — es wird nichts mehr nach `Y:\01` geschrieben; die Regel altert nur den Bestand aus (behebt B5) | `RunRetentionAsync` |

**Fazit:** Die QS-relevanten MSA-Daten sind vollständig geschützt — **`msa_measurements`,
`msa_results` (`Database_MSA=0`) und der komplette Report-Baum `X:\MSA_Reports` inkl. der
verschobenen Laufbilder (`Reports_MSA=0`) werden nie automatisch gelöscht.** Der einzige Ort, an dem
MSA-Bilder überhaupt gelöscht werden, ist der **Transit-Puffer `Z:\05` nach 3 Tagen** — und dort
liegen nach einem erfolgreichen Lauf keine Laufbilder mehr. **Restrisiko (bewusst akzeptiert, von
Philipp entschieden):** bricht ein Lauf ab, ohne dass eine Auswertung stattfindet, werden dessen
Bilder nach 3 Tagen gelöscht.

## 2.4 Rolle von `05_High_Resolution_GoldenSample`

Im MSA-Fluss ist dieser Ordner der **einzige Eingang für Laufbilder** und laut Soll-Konzept ein
reiner **Transit-Puffer**: `MsaRunImages.Move` liest ausschließlich `[NAS] HighResGoldenSamplePath`
und **verschiebt** die Laufbilder heraus. Funktioniert nachweislich (128/128 am 23.07. — damals noch
kopiert).

**Bekannter offener Punkt (Entscheidung von Philipp, bewusst so belassen):** Im selben Ordner liegen
heute **990 M1X-Produktionsbilder** (`M10/M11_ST060_KF1`, Kameranummer 4, `*.png`, Serial2 = lauter
Nullen → also *keine* MSA-Bilder). Praktische Folgen:

* Die **DE-Löschung findet sie nicht** — sie durchsucht laut Soll-Konzept **nur `Z:\01`**. Die Bilder
  verschrotteter Teile bleiben in `Z:\05` liegen …
* … und werden dort vom **3-Tage-InputLeftovers-Sweep** entfernt (er stuft sie korrekt als
  Produktionsbilder ein). Das ist der bewusst gewählte Weg: *„auch die M1X-Produktions-.png löscht die
  Retention nach 3 Tagen"*.
* Die **OK-Bildaufräumung findet sie ebenfalls nicht** — sie sucht nur in
  `Collage_SingleImages` = `Z:\01…\Input`. M1X-OK-Bilder werden also **nicht** ins Backup verschoben.
* Für MSA sind sie harmlos: der Move matcht auf die BaseID, Produktionsserien treffen nicht.

Ob die M1X-Kameras dorthin schreiben sollen oder nach `Z:\01`/`Z:\03`, bleibt eine Kamera-seitige
Frage — hier bewusst nur beschrieben.

---

## 3. Befunde — Stand nach der Umstellung

### 3a. Behoben am 28.07.2026

**B1 — Leere SZID kollidierte in `dmcserial`** → **behoben.** `SaveDmcAsync` bricht jetzt vor dem
Insert ab, wenn die SZID leer ist, und meldet das als WARNUNG mit dem Roh-Telegramm. Begründung für
„Insert überspringen" statt `NULL`: eine Zeile ohne Serie ist wertlos (kein Mess-Lookup, keine
Bildverknüpfung, keine Rückverfolgbarkeit), und `NULL` würde über `UNIQUE`-Semantik unbegrenzt viele
solcher wertlosen Zeilen zulassen.

**B2 — `PartResult.Unknown` war stumm** → **behoben.** Jede Unknown-Verarbeitung schreibt eine
WARNUNG mit dem **Rohwert von Feld 14** und dem **Roh-Telegramm**; zusätzlich wird das Teil aus der
Produktions-CSV herausgehalten, damit dort nur Teile mit verstandenem Ergebnis stehen.

**B4 — `Images_GoldenSample = 30` neben `Reports_MSA = 0`** → **behoben** durch die neue Rolle von
`Z:\05`: Laufbilder werden am Laufende nach `X:` **verschoben** und sind dort über `Reports_MSA = 0`
dauerhaft geschützt; der Transit-Puffer selbst steht jetzt auf **3 Tagen**.

**B5 — MSA-Summary-CSVs unter `CSV_Evaluation = 365`** → **behoben.** Der Export entfällt; nach
`Y:\01` wird nichts mehr geschrieben. Der Altbestand bleibt liegen und altert über die bestehende
365-Tage-Regel aus.

**B6 — Toter Pfad über `[MSA] ResultPath`** → **behoben.** `MsaResultLayout.RunRoot/PdfDir/CsvDir/ImgDir`
und die CSV-Helfer (`CsvRunRoot`, `EnsureWritableCsvDir`) sind entfernt; es existiert nur noch das
aktive `ReportRunRoot`-Layout.

**B8 — CSV-Dateinamens-Konvention in CLAUDE.md §13 veraltet** → **behoben** (Doku an den Code
angeglichen: `<TTMMJJ_HHMMSS>_<OrderName>.csv`).

**B10 — Zwei unterschiedliche Bildsuch-Wege** → **behoben.** OK/NG/DE/Unknown nutzen jetzt eine
gemeinsame Maschinerie (`ImageHandler.Apply`): gleiche Wurzel, gleiche Rekursion, **kein**
Extension-Filter mehr, gleiche feldgenaue Match-Semantik.

### 3b. Weiterhin offen (nur gemeldet, nichts geändert)

**B3 — Zwei verschiedene „nicht gefunden"-Wege bei der CSV.** Fehlen Messzeilen, wird gewarnt und
ein `LIKE '<serial>%'`-Fallback versucht. Greift der, ist die Zeile inhaltlich richtig, aber es steht
eine zweite WARNUNG im Log; greift er nicht, bleibt die Zeile mit leeren Messspalten stehen — die CSV
sieht dann aus wie ein Teil ohne Messungen (`FillMeasurementsAsync`).

**B13 — ⚠ Das 450-ms-Budget wird durch die neue NG-Löschung deutlich häufiger gerissen.**
Gemessen am 28.07.: eine rekursive Aufzählung von `Z:\01_Low_Resolution_Individual` kostet
**560 ms bei 6347 Dateien**; bereits heute liefen **2126 von 8674** Part-Exits über Budget (bis
1198 ms), obwohl nur die 5283 OK-Teile eine Bildaktion hatten. Mit NG (3357) und Unknown im
Bildpfad ist etwa eine **Verdopplung** zu erwarten. Bewusst **nicht** stillschweigend umgebaut.
Mögliche Auswege (zu entscheiden):
* **a)** Gezielte Suche statt Vollscan: `Directory.EnumerateFiles(root, "<Schlüssel>*")` filtert
  serverseitig und wäre um Größenordnungen schneller. **Vorbehalt:** greift nicht für die
  Legacy-Unterstrich-Namen (`…12_0032044…`) — die liegen heute allerdings nur in `Z:\03`, das der
  Part-Exit nicht mehr durchsucht. Ein Hybrid (Muster zuerst, Vollscan als Rückfall bei 0 Treffern)
  wäre sicher, aber aufwendiger.
* **b)** Dateiliste über ein kurzes Zeitfenster cachen, sodass eine Aufzählung mehrere Teile bedient.
* **c)** Die Bildaktion aus dem ACK-Fenster herausnehmen (ändert die ACK-Semantik: die SPS erfährt
  dann nicht mehr, ob die Bildaktion geklappt hat).
* **d)** Budget auf einen realistischen Wert anheben — löst das Grundproblem nicht, beendet aber die
  Warnungsflut.

**B7 — Zwei Report-Layouts nebeneinander.** `X:\MSA_Reports\M50\2026-07-21\…` (alt, flach) und
`X:\MSA_Reports\2026-07-24\M20\<BaseID>\…` (neu). Bewusst keine Migration, aber für QS verwirrend.
Im alten Layout liegt zusätzlich noch eine `*_RawData.csv` direkt im Modulordner.

**B9 — Controller `M50_ST110_KF4` existiert in den Bilddaten, aber nicht in der `Harry.ini`.**
Er taucht sowohl in NG-Bildern als auch in den MSA-Laufbildern auf; die INI kennt für ST110 nur
`KF1` und `KF3`. Für diese Kamera gibt es folglich keine Telegramm-/Messwertanbindung.

**B11 — Fake-DMCs im MSA-Betrieb.** Die per-Teil-PDFs des Laufs `20260724070500` heißen
`…_00000000000000000000000000000001_…` — Serial2 enthält dort nur einen Zähler statt eines echten DMC
(passt zu CLAUDE.md §7 „gefälschte DMCs bei MSA1-Referenzteilen"). Für die MSA-Erkennung reicht das
(ein Zeichen ≠ `0`), aber ein Lauf mit Serial2 = **lauter** Nullen wäre nicht als MSA erkennbar — und
läge damit im Transit-Puffer als „Produktionsbild" im Zugriff der 3-Tage-Retention.

**B12 — M2X-MSA-Läufe kopieren 0 Bilder.** Drei M20-Läufe am 24.07. meldeten `0 found, 0 copied`,
während der M50-Lauf am 23.07. 128 Bilder fand. Entweder schreiben die M2X-Kameras ihre MSA-Bilder
nicht nach `Z:\05`, oder es wurden keine erzeugt. **Mit der Move-Semantik wird das relevanter:**
findet ein Lauf keine Bilder, bleibt sein `IMG\`-Ordner leer und es gibt keinen Bildnachweis.

**B14 — Die OK-Bildaktion greift nicht für M1X.** Die Suchwurzel ist `Z:\01`, M1X schreibt aber nach
`Z:\05` (siehe 2.4). M1X-OK-Bilder landen daher **nie** im Backup-Baum, sondern werden im
Transit-Puffer nach 3 Tagen gelöscht. Das ist die dokumentierte Konsequenz der bewussten
Entscheidung — falls M1X-OK-Bilder gesichert werden sollen, muss die Kamera nach `Z:\01` schreiben.

**B7 — Zwei Report-Layouts nebeneinander.** `X:\MSA_Reports\M50\2026-07-21\…` (alt, flach) und
`X:\MSA_Reports\2026-07-24\M20\<BaseID>\…` (neu). Bewusst keine Migration, aber für QS verwirrend.

**B8 — Dateinamens-Konvention der Produktions-CSV weicht von CLAUDE.md §13 ab.** Dort steht
`YYYY-MM-DD-HH-mm-OrderName.csv`, der Code schreibt `<TTMMJJ_HHMMSS>_<OrderName>.csv`
(`CsvFileWriter.Open` + `FileNaming.Stamp`, gemäß SOW §5.1.2). Die Doku in §13 ist veraltet, nicht der Code.

**B9 — Controller `M50_ST110_KF4` existiert in den Bilddaten, aber nicht in der `Harry.ini`.**
Er taucht sowohl in NG-Bildern als auch in den MSA-Laufbildern auf; die INI kennt für ST110 nur
`KF1` und `KF3`. Für diese Kamera gibt es folglich keine Telegramm-/Messwertanbindung.

**B10 — Die OK-Bildsuche nutzt `…\Input` ohne `SortedRoot`, die DE-Suche mit.** Damit findet die
OK-Aufräumung Bilder **nicht**, die das NAS bereits nach `JJJJ\MM\TT` einsortiert hat, die DE-Löschung
dagegen schon (`PartExitOrchestrator.HandleAsync` vs. `ImageHandler.DeleteBySerials`). Zusätzlich
filtert die OK-Suche auf `*.bmp`, die DE-Suche auf `*` — M1X-`*.png` wären für die OK-Aufräumung
also doppelt unsichtbar.

**B11 — Fake-DMCs im MSA-Betrieb.** Die per-Teil-PDFs des Laufs `20260724070500` heißen
`…_00000000000000000000000000000001_…` — Serial2 enthält dort nur einen Zähler statt eines echten DMC
(passt zu CLAUDE.md §7 „gefälschte DMCs bei MSA1-Referenzteilen"). Für die MSA-Erkennung reicht das
(ein Zeichen ≠ `0`), aber ein Lauf mit Serial2 = **lauter** Nullen wäre nicht als MSA erkennbar.

**B12 — M2X-MSA-Läufe kopieren 0 Bilder.** Drei M20-Läufe am 24.07. melden `0 found, 0 copied`,
während der M50-Lauf am 23.07. 128 Bilder fand. Entweder schreiben die M2X-Kameras ihre MSA-Bilder
nicht nach `Z:\05`, oder es wurden keine erzeugt.

---

## 4. Noch unbelegt (nur zur Laufzeit klärbar)

1. **Füllt die Kamera Serial2 im MSA-Betrieb immer mit dem DMC?** Für **M50** ja (128 Live-Dateien
   mit 32-stelligem DMC, s. 2.2). Für **M1X und M2X** liegt **kein einziges MSA-Bild** vor
   (B12) → dort ist die MSA-Erkennung `IsMsa` noch nicht praktisch nachgewiesen. Das ist jetzt
   wichtiger als vorher: in `Z:\05` entscheidet `IsMsa` nichts mehr über das Löschen (Transit), aber
   in `Z:\01` schützt es MSA-Bilder vor der Part-Exit-Löschung.
2. **Wer hat die MSA-Originale vom 23.07. aus `Z:\05` entfernt?** Der Server nachweislich nicht:
   damals wurde nur kopiert, und in **keinem** Log steht eine `leftover`-Löschzeile. Auffällig ist,
   dass der gesamte NAS-Bestand (01, 03, 05) erst ab **27.07. ~16:00** beginnt — das deutet auf eine
   manuelle oder NAS-seitige Bereinigung hin, ist aber nicht belegt.
3. **Greift der 3-Tage-InputLeftovers-Sweep überhaupt jemals?** Bis heute nie ausgelöst (kein
   `leftover`-Eintrag in irgendeinem Log); der erste scharfe Lauf steht bevor (ältester Bestand
   27.07. 16:00). Damit ist auch die neue 05-Regel noch nicht live erprobt.
4. **Wie viel Zeit kostet die NG-Bildlöschung wirklich im ACK-Fenster?** Der Enumerations-Messwert
   (560 ms/6347 Dateien) ist gemessen, der Effekt auf die Budget-Warnungen aber eine Prognose —
   nach dem ersten Produktionstag anhand von `grep -c "Part exit took"` gegenprüfen (B13).
5. **Wie oft tritt `PartResult.Unknown` real auf?** Bisher nicht rekonstruierbar; die neue WARNUNG
   macht es ab sofort sichtbar.
6. **Verhält sich das Verschieben über die Laufwerksgrenze `Z:` → `X:` unter Last robust?** Der
   Kopie-plus-Größenvergleich ist getestet, aber noch nie mit einem echten Lauf über die beiden
   Netzfreigaben gelaufen. Beim nächsten MSA-Lauf prüfen: `IMG\` vollständig **und** `Z:\05` für
   diesen Lauf leer.
7. **Ob M1X-OK-Bilder gesichert werden sollen** (B14) — hängt an der Kamera-Zielordner-Frage.
