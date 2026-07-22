# HarryPareto — Live Top-20 Fehlergründe

Standalone-Companion-Tool (WPF, .NET 8), das live ein **Pareto der Produktions-Fehlergründe**
zeigt. Nur-Lesen (`GetData`). Keine neuen NuGet-Pakete — Balken in purem WPF (`ProgressBar`),
DPAPI-Passwortschutz per crypt32-P/Invoke.

## Kennzahl

Pro Merkmal die **Anzahl betroffener Teile** = `COUNT(DISTINCT serial)` bei `result_status = 0`
im Zeitfenster, dazu die **Gesamt-Vorkommen** (`COUNT(*)`) als Zweitzahl.

- **Status 2** ("nicht bewertet") zählt **nie** mit.
- Nur **Produktion** (`run_type = 0`) — MSA/Diagnose sind ausgeschlossen (eigene Tabellen).
- Es werden **beide** Messtabellen per `UNION ALL` einbezogen: `measurements_serial`
  (`serial_number`) **und** `measurements_serial_trimmer` (`serial_trimmer`) — sonst fehlten
  M20/M21.
- **Kein Join auf `dmcserial`** — auch noch nicht abgeschlossene Teile zählen mit.

## Verbindung

Beim ersten Start fragt der Dialog die **IP** ab (Port/DB/User/Passwort vorbelegt:
`3306` / `camera_data` / `GetData` / `1234Get`). Gespeichert wird nach
`%APPDATA%\HarryPareto\settings.json`; das **Passwort ist DPAPI-verschlüsselt** (CurrentUser).
Beim nächsten Start Auto-Connect mit Timeout; schlägt er fehl, erscheint der Dialog erneut
(kein Crash).

## Index-Empfehlung (NICHT automatisch angelegt — bitte prüfen/freigeben)

Die Aggregations- und KPI-Queries filtern auf `run_type`, `result_status` und `measured_at`
und gruppieren über `definition_id`. `measurements_serial(_trimmer)` sind bereits **nach Tag
partitioniert** (`TO_DAYS(measured_at)`), das Zeitfenster nutzt also Partition-Pruning. Innerhalb
der Partitionen hilft zusätzlich ein zusammengesetzter Index. Vorschlag (erst nach Freigabe von
Philipp anlegen):

```sql
-- Beschleunigt die WHERE run_type/result_status + Range-Scan der Pareto-/KPI-Query:
ALTER TABLE measurements_serial
  ADD INDEX idx_pareto (run_type, result_status, measured_at, definition_id);

ALTER TABLE measurements_serial_trimmer
  ADD INDEX idx_pareto (run_type, result_status, measured_at, definition_id);
```

> Hinweis: Zusätzliche Indizes kosten Schreib-Performance. Auf der Live-Linie zuerst mit
> `EXPLAIN` auf einem repräsentativen Zeitfenster prüfen, ob der Index tatsächlich genutzt wird,
> bevor er dauerhaft angelegt wird.

## Remote-Betrieb auf einem anderen PC (nur Doku)

HarryPareto kann auf einem beliebigen Netz-PC laufen. Voraussetzungen auf dem MySQL-Server:

1. **bind-address öffnen** — in `my.ini` unter `[mysqld]` `bind-address = 0.0.0.0`
   (oder die konkrete Server-IP `172.29.1.5`), damit der Server Netzwerkverbindungen annimmt.
   Danach MySQL neu starten. Firewall: Port 3306 für das Linien-Subnetz freigeben.
2. **Read-Only-Benutzer netzwerkweit** — `GetData` muss vom Remote-Host erreichbar sein:

   ```sql
   -- GetData für Netzwerkzugriff (nur SELECT), Passwort nach Inbetriebnahme ändern:
   CREATE USER IF NOT EXISTS 'GetData'@'%' IDENTIFIED BY '1234Get';
   GRANT SELECT ON camera_data.* TO 'GetData'@'%';
   FLUSH PRIVILEGES;
   ```

3. Im HarryPareto-Verbindungsdialog die **Server-IP** eintragen (statt `localhost`).

Der schreibende `SettData`-Account bleibt unverändert auf `localhost` beschränkt (CLAUDE.md §8).
