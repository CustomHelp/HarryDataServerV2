# SPS-/PLC-Schnittstellen — HarryDataServer V2

> **Zweck:** Spezifikation für den SPS-Programmierer, damit die 7 TCP-Clients der SPS
> sauber aufgebaut werden können.
> Quelle: tatsächlicher Server-Code (`TcpSpsServer.cs`, `SpsChannel.cs`,
> `SpsPartExitData.cs`, `SystemHealthService.cs`) + `Harry.ini`.
> Stand: 2026-07-02.

---

## 1. Rollen & Grundregeln (gelten für ALLE 7 Schnittstellen)

| Punkt | Festlegung |
|-------|------------|
| **Rollen** | **HarryDataServer = TCP-Server** (lauscht). **SPS = TCP-Client** (verbindet sich). Das gilt für alle 7 Kanäle. |
| **IP des Servers** | `172.29.1.5` (aus `Harry.ini`, Subnetz `255.255.0.0`) |
| **Ports** | 7 verschiedene Ports auf **derselben IP** (siehe Tabelle Abschnitt 2) |
| **Feldtrenner** | **Semikolon `;`** |
| **Dezimaltrenner** | **Punkt `.`** (z. B. Feuchte `43.7`) |
| **Telegramm-Ende (SPS → Server)** | **`\r`** (Carriage Return, 0x0D). Der Server akzeptiert beim Empfang zusätzlich `\n` als Trenner — aber bitte **`\r` senden** (das ist die Konvention). |
| **Telegramm-Ende (Server → SPS)** | **`\r`** (Carriage Return) — einheitlich auf **allen 7 Kanälen**. |
| **Zeichensatz** | **Latin-1 / ISO-8859-1** (1 Byte pro Zeichen). Keine Umlaute/Sonderzeichen in Nutzdaten nötig. |
| **Empfangspuffer** | 8192 Byte |
| **Verbindungen** | Jeder Kanal akzeptiert mehrere Verbindungen und blockiert die anderen nicht. Empfehlung: **pro Kanal eine dauerhaft offene Verbindung** halten (nicht pro Telegramm neu verbinden). |
| **Prinzip** | Jeder Kanal ist **Request/Response**: SPS sendet 1 Telegramm → Server antwortet mit genau 1 Telegramm. |

> **Wichtig:** Die Nutzdaten dürfen **kein `;` und kein `\r`/`\n`** enthalten, da das die Feld-
> bzw. Telegrammgrenze ist. Fehlertexte des Servers werden serverseitig bereinigt
> (`;` → `,`, Zeilenumbrüche → Leerzeichen).

---

## 2. Übersicht der 7 Kanäle

| Kanal | Zweck | Default-Port (`Harry.ini`) | INI-Schlüssel |
|-------|-------|----------------------------|---------------|
| 1 | **KeepAlive / Status** | 6000 | `PortKeepAlive` |
| 2 | **Part Exit** (St160 Verpackung) | 6001 | `PortPartExit` |
| 3 | **MSA-Trigger M10** | 6002 | `PortMSA_M10` |
| 4 | **MSA-Trigger M11** | 6003 | `PortMSA_M11` |
| 5 | **MSA-Trigger M20** | 6004 | `PortMSA_M20` |
| 6 | **MSA-Trigger M21** | 6005 | `PortMSA_M21` |
| 7 | **MSA-Trigger M50** | 6006 | `PortMSA_M50` |

> **Die 5 „gleichen" Schnittstellen** sind die **Kanäle 3–7 (MSA-Trigger)**. Sie haben ein
> **identisches Protokoll** — nur der Port (= Modul) unterscheidet sich. Der Programmierer
> kann also einen Client-Baustein bauen und 5-mal parametrieren.
>
> Alle Ports sind in `Harry.ini` frei konfigurierbar; oben stehen die Standardwerte.

---

## 3. Kanal 1 — KeepAlive / Status

**Zweck:** Lebenszeichen + Gesamtstatus des Systems (Kamera-Verbindungen + Fehlerzustand).

### SPS → Server (Request)
Ein beliebiges Telegramm (z. B. ein Zähler oder ein fester String). Der Inhalt wird
1:1 zurückgespiegelt.

```
<beliebiger Text>\r
```

### Server → SPS (Response)
Der Server **spiegelt das empfangene Telegramm** und hängt an:
1. den **Kamerastatus** — je Kamera eine `1` (verbunden) oder `0` (offline), in **INI-Reihenfolge**, mit `;` getrennt (Anzahl dynamisch, aktuell 14 Kameras),
2. ein **Signalwort** zum Systemzustand,
3. **nur im Fehlerfall** einen Klartext-Fehlertext (Englisch).

**Systemgesund (OK):**
```
<mirror>;1;1;0;1;1;1;0;1;1;1;1;1;1;1;OK\r
```

**Warnung:**
```
<mirror>;1;1;0;1;1;1;0;1;1;1;1;1;1;1;WARNING;<Fehlertext>\r
```

**Fehler:**
```
<mirror>;1;1;0;1;1;1;0;1;1;1;1;1;1;1;ERROR;<Fehlertext>\r
```

| Signalwort | Bedeutung |
|-----------|-----------|
| `OK` | Alles in Ordnung |
| `WARNING` | Warnung aktiv (z. B. einzelne Zeile abgewiesen) — Betrieb läuft weiter |
| `ERROR` | schwerer Fehler (z. B. DB nicht erreichbar, Pipeline steht) |

> **Hinweis:** Die `1/0`-Kameraliste zeigt offline-Kameras an, **ohne** das Signalwort zu
> ändern. Das Signalwort spiegelt DB-/Pipeline-Fehler, nicht einzelne Kamera-Ausfälle.
> Die Anzahl der `1/0`-Felder ist gleich der Anzahl konfigurierter Kameras (nicht fest 14).

---

## 4. Kanal 2 — Part Exit (St160 Verpackung)

**Zweck:** Meldet ein fertiges Teil am Verpackungsauslauf. Löst serverseitig CSV-Export,
Collage-Erzeugung und ggf. MSA-Auswertung aus.

### SPS → Server (Request) — **12 Felder, `;`-getrennt, in dieser Reihenfolge**

```
DMC;SZID;VirtualSerial;OrderName;Mode;M1X_Module;M1X_Nest;M3X_Module;M3X_Nest;M50_Nest;Humidity;ResultStatus\r
```

| # | Feld | Typ | Beispiel | Bemerkung |
|---|------|-----|----------|-----------|
| 0 | `DMC` | String | `A1B2C3...` | DataMatrix-Code auf dem Teil |
| 1 | `SZID` | String | `1026062308300012...` | Rahmen-Seriennummer (32 Zeichen) |
| 2 | `VirtualSerial` | String | `...` | Trimmer-Seriennummer (32 Zeichen) |
| 3 | `OrderName` | String | `Auftrag_4711` | aktueller Auftragsname |
| 4 | `Mode` | String | `Normal` | `Normal` / `MSA1` / `MSA3` / `LimitSample` |
| 5 | `M1X_Module` | Int | `10` | welches M1x-Modul (10 oder 11) |
| 6 | `M1X_Nest` | Int | `2` | Nestnummer in M1x |
| 7 | `M3X_Module` | String | `M32` | welches M3x-Blade-Modul |
| 8 | `M3X_Nest` | String | `1` | Nestnummer in M3x |
| 9 | `M50_Nest` | String | `4` | Nestnummer in M50 |
| 10 | `Humidity` | Float | `43.7` | Feuchtewert aus M1x (Punkt als Dezimaltrenner) |
| 11 | `ResultStatus` | String | `OK` | **`OK`** / **`NG`** / **`DE`** (deleted) |

> Es müssen **mindestens 12 Felder** vorhanden sein. Leere Felder sind erlaubt (z. B.
> `VirtualSerial` leer → einfach zwei `;;` hintereinander). Zahlenfelder, die nicht geparst
> werden können, werden serverseitig als „leer" behandelt.

### Server → SPS (Response) — **ACK**

Der Server fährt zuerst die komplette Verarbeitung (DB, CSV, Collage/MSA) und antwortet
**danach** mit einer Bestätigung:

```
<SZID auf 32 Zeichen mit '0' rechts aufgefüllt>;true\r     (Verarbeitung erfolgreich)
<SZID auf 32 Zeichen mit '0' rechts aufgefüllt>;false\r    (Fehler bei Verarbeitung)
```

Beispiel:
```
10260623083000120000000000000000;true\r
```

- **`true`** = Teil vollständig verarbeitet.
- **`false`** = Verarbeitung fehlgeschlagen **oder** Telegramm ungültig (weniger als 12 Felder).
  Bei ungültigem Telegramm sind die ersten 32 Zeichen `0` (`0000...0000;false\r`).
- Abschluss mit **`\r`** — wie alle anderen Kanäle (bis 2026-07-02 war es hier `\r\n` aus V1; auf einheitliches `\r` umgestellt).

> **Timing:** Die SPS bekommt die ACK erst, **wenn die serverseitige Verarbeitung fertig ist**.
> Bitte im SPS-Client ein ausreichendes Antwort-Timeout vorsehen (kein festes „sofort").

---

## 5. Kanäle 3–7 — MSA-Trigger (die 5 identischen Schnittstellen)

**Zweck:** Nach Abschluss eines MSA-/LimitSample-Laufs fragt die SPS pro Modul das Ergebnis ab.
Protokoll auf allen 5 Kanälen **identisch** — nur der Port bestimmt das Modul (siehe Abschnitt 2).

### SPS → Server (Request)

```
Request;<BaseID>\r
```

- Das erste Feld muss **exakt `Request`** sein (Groß-/Kleinschreibung egal).
- `<BaseID>` = die **blanke 14-stellige BaseID ohne Schleifenzähler**.

**BaseID-Format (14 Zeichen):** `MMYYMMDDHHmmSS`

| Teil | Stellen | Beispiel |
|------|---------|----------|
| Modul | 2 | `10` |
| Jahr | 2 | `26` |
| Monat | 2 | `06` |
| Tag | 2 | `23` |
| Stunde | 2 | `08` |
| Minute | 2 | `30` |
| Sekunde | 2 | `00` |

Beispiel-Request:
```
Request;10260623083000\r
```

### Server → SPS (Response)

| Antwort | Bedeutung |
|---------|-----------|
| `Wait` | Auswertung läuft noch / MSA-Engine noch nicht bereit → **erneut abfragen** |
| `OK` | MSA bestanden |
| `NG` | MSA nicht bestanden |
| `Error;<Beschreibung>` | Fehler aufgetreten (Klartext, Englisch) |

Alle Antworten enden mit **`\r`**. Beispiele:
```
Wait\r
OK\r
NG\r
Error;expected 'Request;<BaseID>'\r
```

> **Empfohlener SPS-Ablauf:** `Request;<BaseID>` senden → bei `Wait` nach kurzer Pause
> erneut fragen (Polling) → bis `OK` / `NG` / `Error` kommt.
>
> **Falsches Format** (nicht `Request;<BaseID>`) → Antwort `Error;expected 'Request;<BaseID>'`.

---

## 6. Fehler- & Sonderfälle (Zusammenfassung)

| Situation | Server-Antwort |
|-----------|----------------|
| Kanal 1, System gesund | `<mirror>;<cams>;OK\r` |
| Kanal 1, Warnung | `<mirror>;<cams>;WARNING;<text>\r` |
| Kanal 1, Fehler | `<mirror>;<cams>;ERROR;<text>\r` |
| Kanal 2, Teil verarbeitet | `<SZID32>;true\r` |
| Kanal 2, Verarbeitung fehlgeschlagen | `<SZID32>;false\r` |
| Kanal 2, ungültiges Telegramm (<12 Felder) | `00000000000000000000000000000000;false\r` |
| Kanäle 3–7, läuft noch | `Wait\r` |
| Kanäle 3–7, bestanden / durchgefallen | `OK\r` / `NG\r` |
| Kanäle 3–7, falsches Format | `Error;expected 'Request;<BaseID>'\r` |
| Kanäle 3–7, interner Fehler | `Error;<Beschreibung>\r` |

---

## 7. Checkliste für den SPS-Programmierer

- [ ] **TCP-Client** (die SPS verbindet sich; der Server verbindet **nicht** aktiv).
- [ ] Server-IP `172.29.1.5`, 7 Ports `6000`–`6006` (bzw. wie in `Harry.ini` konfiguriert).
- [ ] Pro Kanal **eine dauerhafte Verbindung** halten, nicht pro Telegramm neu aufbauen.
- [ ] Sende-Telegramm mit **`\r`** abschließen.
- [ ] Felder mit **`;`** trennen, Dezimalzahlen mit **`.`**.
- [ ] Nutzdaten enthalten **kein `;`** und **kein `\r`/`\n`**.
- [ ] Antworten zeilenweise lesen — **alle 7 Kanäle** enden einheitlich mit `\r`.
- [ ] Kanal 2: **Antwort-Timeout großzügig** wählen (Verarbeitung erfolgt vor der ACK).
- [ ] Kanäle 3–7: **`Wait` → Polling** bis `OK`/`NG`/`Error`.
- [ ] Kanäle 3–7 mit **einem gemeinsamen Baustein** umsetzen (5× parametriert nach Port/Modul).

---

## 8. Offene Punkte / vor dem Termin klären

> Diese Punkte betreffen mögliche **Protokoll-Erweiterungen** (aus dem SOW / Addendum),
> die noch nicht implementiert sind — beim Termin ansprechen:

1. **M1X-LimitSample Batch-Bestätigung (Addendum §2.2):** Der M1X-LimitSample-Lauf endet
   **nicht** automatisch nach fester Teilezahl. Nach jeweils **4 Teilen** soll der Bediener
   bestätigen, ob weitere Teile kommen. Erst nach „keine weiteren Teile" ist der Lauf fertig.
   → **Welcher Kanal / welches Signal / welches Format** soll das übertragen? (evtl. neuer
   Befehlstyp auf dem MSA-Kanal).
2. **Shift-Counter Reset (SOW §4.3):** Geplant ist ein PLC-Signal `RESET_SHIFT_COUNTER` zum
   Schichtwechsel. → **Auf welchem Kanal, welches Telegrammformat?**
3. **Failure-Warnings X-in-Folge (SOW §4.4):** Warnung, wenn X Fehler derselben Prüfgruppe
   in Folge auftreten. Übertragung geplant im KeepAlive-Response (Kanal 1). → Schwellwerte
   und genaue Signalisierung abstimmen.
4. **Feld-Details Part Exit:** Wertebereiche/Formate von `M3X_Module`, `M3X_Nest`, `M50_Nest`
   final bestätigen (aktuell als String verarbeitet).
