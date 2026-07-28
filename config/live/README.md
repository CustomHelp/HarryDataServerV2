# `config/live` — Snapshot der Live-Konfiguration

Dies ist ein **versionierter Abzug der real laufenden Konfiguration** aus `F:\002_Configs`
(Stand 2026-07-28). Zweck: die Anlagenkonfiguration ist damit im Repo nachvollziehbar und
wiederherstellbar — bisher existierte sie nur auf dem Server-Laufwerk.

```
config/live/
  Harry.ini            <- Abzug von F:\002_Configs\Harry.ini
  Templates/*.json     <- Abzug von F:\002_Configs\Templates\*.json (28 Dateien)
```

## Wichtig: das hier wird NICHT gelesen

Zur Laufzeit sucht der Server seine `Harry.ini` in dieser Reihenfolge
(`Services/IniConfigService`, CLAUDE.md §10):

1. Umgebungsvariable `HARRY_CONFIG_DIR`
2. **`F:\002_Configs`** ← das ist der Produktionsfall
3. neben der ausführbaren Datei
4. Legacy `D:\HarryDataServer`

Dieser Ordner ist also reine **Dokumentation/Backup**, keine aktive Konfiguration. Wer hier etwas
ändert, ändert nichts an der Anlage — und umgekehrt gilt: **Änderungen an `F:\002_Configs` müssen
hier von Hand nachgezogen werden**, sonst läuft der Snapshot aus dem Ruder. Vor einem Deploy oder
nach einer Konfigurationsänderung:

```
robocopy F:\002_Configs config\live\ Harry.ini /NJH /NJS
robocopy F:\002_Configs\Templates config\live\Templates *.json /NJH /NJS
```

## Abgrenzung zu `HarryDataServer/Resources/Templates`

Das sind **zwei verschiedene Dinge** und sie werden absichtlich nicht synchronisiert:

| Ort | Rolle |
|-----|-------|
| `HarryDataServer/Resources/Templates/*.json` | Wird von der `.csproj` als `Content` **neben die EXE kopiert** und dient dem JSON-Loader nur als Rückfallebene. Für die M1X-Kameras stehen dort noch **Stubs** (`"STUB - Please fill telegram_place …"`). Kundeneigene Dateien — nicht überschreiben. |
| `config/live/Templates/*.json` | Die **echten, gefüllten** Kameraprogramm-Definitionen, die auf der Anlage tatsächlich verwendet werden (z. B. `Result_M10_ST030_KF1.json`: 165 statt 26 Zeilen). |

> Enthalten ist unter anderem die Korrektur vom 2026-07-21, mit der `telegram_place` bei **72**
> statt 71 beginnt (Token 71 ist `Total_Result`, CLAUDE.md §9). Der Live-Ordner hält daneben ein
> `_backup_offbyone_20260721\` mit dem alten Stand — das ist **nicht** Teil des Snapshots.

## Nicht enthalten

`F:\002_Configs\MSA_References\` (45 Dateien: `DEMO_*.json`-Vorlagen und die von HarryLimitSample
eingelernten LimitSample-Referenzen). Diese ändern sich im Betrieb durch Einlernen und würden den
Snapshot dauernd verändern. Falls sie mit ins Repo sollen, bitte bewusst entscheiden.

## Zugangsdaten: eine einzige maskierte Zeile

**`config/live/Harry.ini` ist bis auf genau eine Zeile identisch mit der Live-INI.** Maskiert ist
ausschließlich das DB-Passwort:

```ini
[MySQL]
Password=<siehe F:\002_Configs\Harry.ini>     ; statt des echten Werts
```

**Das echte Passwort liegt nur lokal in `F:\002_Configs\Harry.ini`** auf dem Server und kommt
bewusst nicht ins Repo. Alles andere — Pfade, `[Retention]`-Werte, `[SPS]`-Ports, alle 14
Kamerablöcke — ist unverändert übernommen.

Wer den Snapshot zum Wiederherstellen benutzt, muss diese eine Zeile also von Hand mit dem echten
Wert füllen (bzw. den nach CLAUDE.md §8 vom Kunden geänderten Wert eintragen).

> **Regel für neue Dateien:** keine Klartext-Passwörter in neu ins Repo aufgenommenen Dateien
> (CLAUDE.md, Standing Rule). Die zwei **Altfundstellen** `HarryDataServer/Harry.ini` (Zeile 16) und
> `tools/customer/Harry.customer.ini` sind noch nicht bereinigt und als offener Punkt notiert.
