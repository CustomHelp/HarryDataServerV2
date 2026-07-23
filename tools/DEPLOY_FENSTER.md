# DEPLOY_FENSTER — 10-Minuten-Stopp-Fenster (HarryDataServer V2)

Umstellung der Produktion vom Repo-`bin\Release` auf den festen Deploy-Pfad
**`F:\003_Deploy\HarryDataServer\App\`**. Alles unter „Phase 1“ ist bei laufender Anlage
vorbereitet; dieses Fenster macht nur noch das Nötigste. **Gesamt ≈ 8–10 min.**

> `D:` ist das DVD-Laufwerk — niemals dorthin schreiben. Nie eine zweite Server-Instanz
> gegen die laufende Anlage starten (Ports 6000–6006, Kameras, DB).

## Vorbedingungen (in Phase 1 bereits erledigt)
- Ordner `F:\003_Deploy\HarryDataServer\App\` + `App_prev\`, `F:\004_Logs\`, `F:\003_Deploy\MSA_Reports_Fallback\` angelegt.
- Live-`F:\002_Configs\Harry.ini` auf `F:` umgestellt (LogFilePath=`F:\004_Logs\`, Collage_IniPath, neuer `[MSA] ReportFallbackPath=F:\003_Deploy\MSA_Reports_Fallback`). **Wirkt erst beim Neustart** (Schritt 5).
- `tools\deploy.cmd` steht bereit; Companion-Suche im Server findet `App\<Tool>\` (+ Repo-Fallback für Dev).
- Release im Repo gebaut — **AUSSER** den EXEs, die von der laufenden Anlage gesperrt waren (siehe unten „Nachbauen“).

---

## Schritte im Fenster

### 1. Anlage/Programme beenden — ~1 min
- Server `HarryDataServer` schließen (Fenster zu, bzw. Task beenden).
- Alle Companions schließen, falls offen: HarryAnalysis, HarryGraph, HarryCounter, HarryLimitSample, HarryCollageCreator, HarryPareto.
- Prüfen, dass nichts mehr läuft:
  `tasklist | findstr /I "HarryDataServer HarryAnalysis HarryGraph HarryCounter HarryLimitSample HarryCollageCreator HarryPareto"`
  → Ausgabe muss leer sein. (deploy.cmd bricht sonst in Schritt 3 selbst ab.)

### 2. Nur falls nötig: gesperrte Release-Builds nachziehen — ~1–2 min
Erst **jetzt** (nach Schritt 1) sind die vorher gesperrten EXEs frei.
```
dotnet build F:\001_CHP\Programme\HarryDataServerV2\HarryDataServer.sln -c Release
```
> In Phase 1 gesperrt und daher hier nachzubauen: **siehe die von Claude gemeldete Liste**
> (mind. `HarryDataServer.exe`, da die Anlage aus `bin\Release` lief; ggf. laufende Companions).
> Wenn Phase-1-Release komplett durchlief, ist dieser Schritt übersprungbar.

### 3. Deploy ausführen — ~1 min
```
F:\001_CHP\Programme\HarryDataServerV2\tools\deploy.cmd
```
Macht: Prozess-Check → `App` → `App_prev` sichern → je Projekt `robocopy` aus `bin\Release\net8.0-windows\` → `App\version.txt` (Datum + git-Hash). Bricht bei laufendem Prozess / fehlendem Build sauber ab (Exit-Code ≠ 0).

### 4. Desktop-Verknüpfung umbiegen — ~1 min
Verknüpfung auf **`F:\003_Deploy\HarryDataServer\App\HarryDataServer\HarryDataServer.exe`** anlegen/ändern
(Arbeitsverzeichnis = dieser Ordner). Alte Verknüpfung auf `bin\Release` entfernen, damit niemand die falsche startet.

### 5. Start aus `App\` + Funktionskontrolle — ~3–4 min
Server aus der neuen Verknüpfung starten und prüfen:
- [ ] Statusleiste zeigt die geladene **`F:\002_Configs\Harry.ini`**.
- [ ] Alle 14 Kameras **connected** (Cameras-Tab, grüne LEDs).
- [ ] PLC-**KeepAlive** läuft (PLC-Tab, Kanal 1), Ports 6000–6006 gebunden.
- [ ] **Logs** laufen jetzt in **`F:\004_Logs\`** (neue `HarryDataServer-<Datum>.log`), nicht mehr im exe-Ordner.
- [ ] Tools-Tab: alle 6 Companions werden gefunden (starten je einen kurz, Fenster geht auf) — sie liegen unter `App\<Tool>\`.
- [ ] **Ein Teil durchlaufen lassen**: Part-Exit kommt an, CSV/DB-Eintrag entsteht, keine Fehler im Log.

### 6. Rollback-Plan (falls etwas klemmt) — ~1–2 min
- Neuen Server beenden.
- `App_prev` zurück über `App` kopieren:
  `robocopy F:\003_Deploy\HarryDataServer\App_prev F:\003_Deploy\HarryDataServer\App /MIR`
  und aus `App\HarryDataServer\HarryDataServer.exe` starten, **oder**
- Übergangsweise die alte EXE direkt aus dem Repo starten:
  `F:\001_CHP\Programme\HarryDataServerV2\HarryDataServer\bin\Release\net8.0-windows\HarryDataServer.exe`
  (läuft mit derselben `F:\002_Configs\Harry.ini`).
- Danach in Ruhe (außerhalb des Fensters) die Ursache analysieren.

---

## Nach dem Fenster
- Prüfen, dass in `F:\004_Logs\` frische Logs entstehen und `bin\Release\...\Logs` nicht mehr wächst.
- MSA-Fallback nur relevant, wenn `X:\MSA_Reports` mal weg ist → dann landet es in `F:\003_Deploy\MSA_Reports_Fallback`.
- `App_prev` als Rückfallebene bis zum nächsten erfolgreichen Deploy stehen lassen.
