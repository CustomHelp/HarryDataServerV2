namespace HarryShared.Help;

/// <summary>
/// The bilingual help content for each Harry app. One factory method per app returns a
/// <see cref="HelpContent"/> that the shared <c>HelpWindow</c> renders. The app passes its own
/// version string. Add a method here as each app is wired up.
/// </summary>
public static class SuiteHelp
{
    public static HelpContent LimitSample(string version) => new(
        AppName: "HarryLimitSample — Reference Editor",
        Version: version,
        DescriptionEn:
            "HarryLimitSample teaches limit-sample reference parts for the MSA evaluation. You scan a " +
            "part, mark each measurement as Should Pass, Should Fail or Ignore, and save one reference " +
            "file per part. During a LimitSample run the server then checks that every prepared error " +
            "(Should Fail) is rejected and every good feature (Should Pass) is accepted.",
        DescriptionDe:
            "HarryLimitSample lernt Grenzmuster-Teile für die MSA-Auswertung ein. Du scannst ein Teil, " +
            "markierst jede Messung als Should Pass, Should Fail oder Ignore und speicherst eine " +
            "Referenzdatei pro Teil. Bei einem LimitSample-Lauf prüft der Server dann, dass jeder " +
            "vorbereitete Fehler (Should Fail) abgewiesen und jedes Gut-Merkmal (Should Pass) angenommen wird.",
        Sections: new List<HelpSection>
        {
            new("Teach a part", "Teil einlernen", new List<HelpStep>
            {
                new("Scan the part's DMC in the Scan / Serial box (or type it) and press Enter — the part's measurements load into the table.",
                    "DMC des Teils ins Feld Scan / Serial scannen (oder eingeben) und Enter drücken — die Messungen des Teils werden in die Tabelle geladen."),
                new("Alternatively pick a Module and click 'Load existing' to start from an already saved reference.",
                    "Alternativ ein Modul wählen und 'Load existing' klicken, um von einer bereits gespeicherten Referenz auszugehen."),
                new("In the Expectation column, set each measurement to Should Pass, Should Fail or Ignore.",
                    "In der Spalte Expectation jede Messung auf Should Pass, Should Fail oder Ignore stellen."),
                new("Click 'Save reference' (or press Ctrl+S). One file per part is written to <ReferencePath>\\<Module>\\LimitSamples\\<DMC>.json.",
                    "'Save reference' klicken (oder Strg+S). Es wird eine Datei pro Teil geschrieben: <ReferencePath>\\<Modul>\\LimitSamples\\<DMC>.json."),
            }),
            new("Edit or delete taught parts", "Gelernte Teile bearbeiten oder löschen", new List<HelpStep>
            {
                new("The right-hand list shows the taught parts of the selected module and its identical (baugleich) mirror: M10↔M11 and M20↔M21.",
                    "Die rechte Liste zeigt die gelernten Teile des gewählten Moduls und seines baugleichen Spiegels: M10↔M11 und M20↔M21."),
                new("Open re-loads a part for editing — the full measurement set is shown (including Ignore), so you can change any mark, e.g. Ignore → Should Pass.",
                    "Open lädt ein Teil zum Bearbeiten neu — es werden alle Messungen angezeigt (auch Ignore), du kannst also jede Markierung ändern, z. B. Ignore → Should Pass."),
                new("Delete removes that part's reference file (after a confirmation).",
                    "Delete löscht die Referenzdatei dieses Teils (nach Rückfrage)."),
            }),
            new("Notes", "Hinweise", new List<HelpStep>
            {
                new("A reference needs at least one Should Fail (prepared error); otherwise the run is reported as INVALID.",
                    "Eine Referenz braucht mindestens ein Should Fail (vorbereiteter Fehler), sonst wird der Lauf als INVALID gemeldet."),
                new("If the camera did not judge the part (only status 2), teaching is refused — use a limit sample the camera actually detects as NOK.",
                    "Hat die Kamera das Teil nicht bewertet (nur Status 2), wird das Einlernen abgelehnt — ein Grenzmuster verwenden, das die Kamera als NOK erkennt."),
                new("The reference folder comes from Harry.ini, [MSA] ReferencePath — it is shown in the status bar at the bottom.",
                    "Der Referenzordner kommt aus Harry.ini, [MSA] ReferencePath — er wird unten in der Statusleiste angezeigt."),
            }),
        },
        Shortcuts: new List<HelpShortcut>
        {
            new("Enter", "Search for the entered part", "Nach dem eingegebenen Teil suchen"),
            new("Ctrl+S", "Save the reference", "Referenz speichern"),
            new("F1", "Open this help", "Diese Hilfe öffnen"),
        });

    public static HelpContent Analysis(string version) => new(
        AppName: "HarryAnalysis — Part Inspector",
        Version: version,
        DescriptionEn:
            "HarryAnalysis looks up a finished part by its DMC, SZID or virtual (trimmer) serial and shows " +
            "all of its measurements with the limits and results. It is read-only. Parts without a part-exit " +
            "record are still found — the measurements are resolved directly from the camera tables.",
        DescriptionDe:
            "HarryAnalysis sucht ein fertiges Teil über DMC, SZID oder virtuelle (Trimmer-)Seriennummer und " +
            "zeigt alle Messungen mit Grenzen und Ergebnissen. Nur-Lesen. Auch Teile ohne Part-Exit-Datensatz " +
            "werden gefunden — die Messungen kommen dann direkt aus den Kamera-Tabellen.",
        Sections: new List<HelpSection>
        {
            new("Look up a part", "Teil suchen", new List<HelpStep>
            {
                new("Type or scan a DMC, SZID or virtual serial into the Scan / Serial box and press Enter (or click Search).",
                    "DMC, SZID oder virtuelle Seriennummer ins Feld Scan / Serial eingeben oder scannen und Enter drücken (oder Search klicken)."),
                new("The part header (result, order, humidity …) and its measurements appear; each row shows the value, Min/Max limits and the result.",
                    "Der Teil-Kopf (Ergebnis, Auftrag, Feuchte …) und die Messungen erscheinen; jede Zeile zeigt Wert, Min/Max-Grenzen und Ergebnis."),
                new("Recent look-ups are kept in the Scan history (last 20).",
                    "Die letzten Abfragen bleiben in der Scan-History (letzte 20)."),
            }),
            new("Export", "Exportieren", new List<HelpStep>
            {
                new("Export CSV exports the currently shown part's measurements.",
                    "Export CSV exportiert die Messungen des aktuell gezeigten Teils."),
                new("Export All writes every part in the history into one CSV. Right-click a history row to remove it; Clear All empties the history.",
                    "Export All schreibt alle Teile der History in eine CSV. Rechtsklick auf eine History-Zeile entfernt sie; Clear All leert die History."),
            }),
        },
        Shortcuts: new List<HelpShortcut>
        {
            new("Enter", "Search for the entered part", "Nach dem eingegebenen Teil suchen"),
            new("F1", "Open this help", "Diese Hilfe öffnen"),
        });

    public static HelpContent Graph(string version) => new(
        AppName: "HarryGraph — Measurement Trend",
        Version: version,
        DescriptionEn:
            "HarryGraph plots measurement values over time. Add up to six graphs, pick one or more measurements " +
            "per graph, and view either a fixed date/time range or a live view of the last N points per series.",
        DescriptionDe:
            "HarryGraph stellt Messwerte über die Zeit dar. Bis zu sechs Graphen hinzufügen, je Graph eine oder " +
            "mehrere Messungen wählen, und entweder einen festen Datums-/Zeitbereich oder eine Live-Ansicht der " +
            "letzten N Punkte pro Serie anzeigen.",
        Sections: new List<HelpSection>
        {
            new("Build a graph", "Graph aufbauen", new List<HelpStep>
            {
                new("Use + / – to add or remove graphs (max 6).",
                    "Mit + / – Graphen hinzufügen oder entfernen (max. 6)."),
                new("In each graph, pick one or more measurements from the list. Each measurement appears once (by its display name).",
                    "In jedem Graph eine oder mehrere Messungen aus der Liste wählen. Jede Messung erscheint einmal (über den Anzeigenamen)."),
            }),
            new("Choose the time range", "Zeitbereich wählen", new List<HelpStep>
            {
                new("Set From / To (date + HH:mm:ss) for a fixed range, or set 'Live last N' for the most recent N points per series.",
                    "From / To (Datum + HH:mm:ss) für einen festen Bereich setzen, oder 'Live last N' für die letzten N Punkte pro Serie."),
                new("Refresh all reloads every graph. You can zoom/pan inside a graph and save or load a graph configuration as JSON.",
                    "Refresh all lädt alle Graphen neu. In einem Graph kann gezoomt/verschoben werden; eine Graph-Konfiguration lässt sich als JSON speichern/laden."),
            }),
        },
        Shortcuts: new List<HelpShortcut>
        {
            new("F1", "Open this help", "Diese Hilfe öffnen"),
        });

    public static HelpContent Counter(string version) => new(
        AppName: "HarryCounter — NG Error Counter",
        Version: version,
        DescriptionEn:
            "HarryCounter counts NG (failed) parts, grouped by up to two dimensions such as error category, " +
            "nest or module. It offers a live view over the last N finished parts or a fixed date/time range.",
        DescriptionDe:
            "HarryCounter zählt NG- (Fehler-)Teile, gruppiert nach bis zu zwei Dimensionen wie Fehlergruppe, " +
            "Nest oder Modul. Wahlweise Live-Ansicht über die letzten N fertigen Teile oder fester Datums-/Zeitbereich.",
        Sections: new List<HelpSection>
        {
            new("Group and count", "Gruppieren und zählen", new List<HelpStep>
            {
                new("Choose 'Group by' level 1 and optionally level 2, e.g. Feature group → Nest.",
                    "'Group by' Ebene 1 und optional Ebene 2 wählen, z. B. Fehlergruppe → Nest."),
                new("The tree shows the counts; your expand/collapse and selection are kept across refreshes. 'Reset Tree' returns to the default view.",
                    "Der Baum zeigt die Zählungen; Auf-/Zuklappen und Auswahl bleiben über Refreshes erhalten. 'Reset Tree' setzt auf die Standardansicht zurück."),
            }),
            new("Live vs. range", "Live vs. Bereich", new List<HelpStep>
            {
                new("Live aggregates the last N finished parts (editable N). A fixed range uses the From/To date + time.",
                    "Live aggregiert die letzten N fertigen Teile (N editierbar). Ein fester Bereich nutzt From/To Datum + Zeit."),
            }),
        },
        Shortcuts: new List<HelpShortcut>
        {
            new("F1", "Open this help", "Diese Hilfe öffnen"),
        });

    public static HelpContent Collage(string version) => new(
        AppName: "HarryCollageCreator — Collage.ini Editor",
        Version: version,
        DescriptionEn:
            "HarryCollageCreator is the visual editor for Collage.ini. Place, zoom, crop and mirror the camera " +
            "images on a canvas and save the layout the server uses to build collages.",
        DescriptionDe:
            "HarryCollageCreator ist der visuelle Editor für Collage.ini. Kamerabilder auf einer Fläche " +
            "platzieren, zoomen, zuschneiden und spiegeln und das Layout speichern, das der Server zum Bauen " +
            "der Collagen verwendet.",
        Sections: new List<HelpSection>
        {
            new("Edit a layout", "Layout bearbeiten", new List<HelpStep>
            {
                new("New / Open… / Save… manage the Collage.ini file.",
                    "New / Open… / Save… verwalten die Collage.ini-Datei."),
                new("Add images… to place image slots; then drag, zoom, crop and mirror each slot on the canvas.",
                    "Mit Add images… Bild-Slots hinzufügen; dann jeden Slot auf der Fläche verschieben, zoomen, zuschneiden und spiegeln."),
                new("Export preview… renders a preview image of the current layout.",
                    "Export preview… erzeugt ein Vorschaubild des aktuellen Layouts."),
            }),
        },
        Shortcuts: new List<HelpShortcut>
        {
            new("F1", "Open this help", "Diese Hilfe öffnen"),
        });

    public static HelpContent Pareto(string version) => new(
        AppName: "HarryPareto — Live Top-20 Defect Reasons",
        Version: version,
        DescriptionEn:
            "HarryPareto shows a live Pareto of the production defect reasons. The metric per feature is " +
            "the number of AFFECTED PARTS (distinct serials with result_status = 0) in the time window, " +
            "with the total occurrences as a second figure. It is read-only, combines the frame and " +
            "trimmer measurement tables (so M20/M21 are included) and does not join dmcserial, so parts " +
            "that have not finished still count. Status 2 (not evaluated) never counts.",
        DescriptionDe:
            "HarryPareto zeigt live ein Pareto der Produktions-Fehlergründe. Kennzahl je Merkmal ist die " +
            "Anzahl BETROFFENER TEILE (eindeutige Seriennummern mit result_status = 0) im Zeitfenster, " +
            "dazu die Gesamt-Vorkommen als Zweitzahl. Nur lesend, kombiniert Rahmen- und Trimmer-Tabelle " +
            "(damit M20/M21 enthalten sind) und ohne Join auf dmcserial, sodass auch nicht abgeschlossene " +
            "Teile mitzählen. Status 2 (nicht bewertet) zählt nie mit.",
        Sections: new List<HelpSection>
        {
            new("Connect", "Verbinden", new List<HelpStep>
            {
                new("On first start the connection dialog asks for IP (port/database/user/password are pre-filled). Passwords are stored DPAPI-encrypted in %APPDATA%\\HarryPareto\\settings.json.",
                    "Beim ersten Start fragt der Verbindungsdialog die IP ab (Port/Datenbank/Benutzer/Passwort sind vorbelegt). Passwörter werden DPAPI-verschlüsselt in %APPDATA%\\HarryPareto\\settings.json gespeichert."),
                new("On the next start it auto-connects with the saved settings; if that fails the dialog reappears — the app never crashes. Use 'Verbindung…' to change the connection.",
                    "Beim nächsten Start verbindet es automatisch mit den gespeicherten Daten; schlägt das fehl, erscheint der Dialog erneut — die App stürzt nie ab. Über 'Verbindung…' die Verbindung ändern."),
                new("Remote operation on another PC needs the MySQL server's bind-address opened and the read-only user (GetData) reachable over the network.",
                    "Für den Betrieb auf einem anderen PC muss die bind-address des MySQL-Servers geöffnet und der Nur-Lese-Benutzer (GetData) über das Netzwerk erreichbar sein."),
            }),
            new("Read the view", "Ansicht lesen", new List<HelpStep>
            {
                new("The KPI head shows inspected parts, bad parts, the rate %, the time window, the last update and the connection.",
                    "Der KPI-Kopf zeigt geprüfte Teile, Schlechtteile, Quote %, Zeitfenster, letzte Aktualisierung und Verbindung."),
                new("The Top-20 bars are largest first; each shows the affected-part count, its % and the occurrences, a trend arrow versus the previous window, and is coloured by module_ref (see the legend).",
                    "Die Top-20-Balken stehen größter zuerst; jeder zeigt die Anzahl betroffener Teile, deren % und die Vorkommen, einen Trend-Pfeil gegenüber dem Vorfenster, und ist nach module_ref eingefärbt (siehe Legende)."),
                new("The module chart shows the share per module_ref — click a bar to filter the Top-20 to that module; clear it with the ✕ chip.",
                    "Das Modul-Chart zeigt den Anteil je module_ref — Balken anklicken filtert die Top-20 auf dieses Modul; über das ✕-Feld zurücksetzen."),
                new("A warning box lists controllers that only produced status 2 ('camera did not judge'). The shift comparison contrasts the current shift's rate with the previous shift's.",
                    "Ein Warnfeld listet Controller, die nur Status 2 lieferten ('Kamera bewertet nicht'). Der Schichtvergleich stellt die Quote der aktuellen Schicht der Vorschicht gegenüber."),
            }),
            new("Filters, refresh, export", "Filter, Aktualisierung, Export", new List<HelpStep>
            {
                new("Filter by time window (shift / 8 h / 24 h / 7 days) and controller. Auto-refresh runs every N seconds (default 30, editable); on a DB error a hint is shown instead of a crash.",
                    "Nach Zeitfenster (Schicht / 8 h / 24 h / 7 Tage) und Controller filtern. Auto-Refresh läuft alle N Sekunden (Standard 30, editierbar); bei DB-Fehler erscheint ein Hinweis statt eines Absturzes."),
                new("TV mode enlarges everything for a wall display. CSV-Export writes the current Top-20 to a semicolon CSV.",
                    "Der TV-Modus vergrößert alles für eine Wandanzeige. CSV-Export schreibt die aktuelle Top-20 in eine Semikolon-CSV."),
            }),
        },
        Shortcuts: new List<HelpShortcut>
        {
            new("F5", "Refresh now", "Jetzt aktualisieren"),
            new("F1", "Open this help", "Diese Hilfe öffnen"),
        });

    public static HelpContent Server(string version) => new(
        AppName: "HarryDataServer V2",
        Version: version,
        DescriptionEn:
            "HarryDataServer is the data-acquisition server for the razor-head line. It receives the camera " +
            "telegrams, serves the seven PLC channels, writes to MySQL, exports CSV, builds collages and runs " +
            "the MSA evaluation. Each tab shows one subsystem and its live status.",
        DescriptionDe:
            "HarryDataServer ist der Datenerfassungs-Server der Rasierkopf-Linie. Er empfängt die Kamera-" +
            "Telegramme, bedient die sieben SPS-Kanäle, schreibt in MySQL, exportiert CSV, baut Collagen und " +
            "führt die MSA-Auswertung durch. Jeder Tab zeigt ein Subsystem und dessen Live-Status.",
        Sections: new List<HelpSection>
        {
            new("Overview", "Überblick", new List<HelpStep>
            {
                new("Each tab (Cameras, PLC, Scanner, MSA, Database, CSV, Collage, Log, Tools) shows one subsystem and its live status.",
                    "Jeder Tab (Cameras, PLC, Scanner, MSA, Database, CSV, Collage, Log, Tools) zeigt ein Subsystem und dessen Live-Status."),
                new("'Telegramme mitschneiden' (top bar) writes all incoming raw telegrams to the Capture folder next to the exe — a test/commissioning aid.",
                    "'Telegramme mitschneiden' (oben) schreibt alle eingehenden Roh-Telegramme in den Capture-Ordner neben der Exe — Test-/Inbetriebnahme-Hilfe."),
                new("The status bar shows overall health, the error count, uptime and the loaded Harry.ini. Truncated texts show their full content as a tooltip on hover.",
                    "Die Statusleiste zeigt Gesamt-Health, Fehlerzähler, Laufzeit und die geladene Harry.ini. Abgeschnittene Texte zeigen ihren vollen Inhalt als Tooltip beim Überfahren."),
            }),
            new("Cameras & PLC", "Cameras & PLC", new List<HelpStep>
            {
                new("Cameras tab: one card per controller shows the connection LEDs, the operating mode and the last telegrams (at least 4 lines, scrollable — some controllers inspect 4 parts per cycle). Right-click a telegram line to copy its serial.",
                    "Cameras-Tab: eine Karte je Controller zeigt die Verbindungs-LEDs, den Betriebsmodus und die letzten Telegramme (mindestens 4 Zeilen, scrollbar — manche Controller prüfen 4 Teile pro Takt). Rechtsklick auf eine Telegrammzeile kopiert die Seriennummer."),
                new("PLC tab (formerly SPS): one card per PLC channel (KeepAlive, Part Exit, the five MSA channels). Each card shows the port, the connection LED and the last requests/responses (up to 20 lines, scrollable).",
                    "PLC-Tab (früher SPS): eine Karte je SPS-Kanal (KeepAlive, Part Exit, die fünf MSA-Kanäle). Jede Karte zeigt Port, Verbindungs-LED und die letzten Requests/Responses (bis zu 20 Zeilen, scrollbar)."),
            }),
            new("MSA & LimitSample", "MSA & LimitSample", new List<HelpStep>
            {
                new("The MSA tab shows, per selected run, a parts list (DMC · verdict · x/y ok · matched MSA1 reference) and, for the selected part, its measurements (ok / not ok / n.a. + reason).",
                    "Der MSA-Tab zeigt je gewähltem Lauf eine Teile-Liste (DMC · Ergebnis · x/y ok · zugeordnete MSA1-Referenz) und, für das gewählte Teil, dessen Messungen (ok / nicht ok / n.a. + Grund)."),
                new("Buttons act on the selected part: Open PDF Complete, Open PDF (failures only) and Open Folder. LimitSample/MSA1 generate one PDF pair PER PART (BaseID + DMC in the file name).",
                    "Buttons wirken auf das gewählte Teil: PDF komplett öffnen, PDF (nur Fehler) und Ordner öffnen. LimitSample/MSA1 erzeugen ein PDF-Paar PRO TEIL (BaseID + DMC im Dateinamen)."),
                new("A run is only OK when the COMPLETE run passes — a premature/partial evaluation stays in Wait and never reports OK. A part without its reference file is INVALID. LimitSample checks both directions: every prepared error (ShouldFail) rejected AND every good feature (ShouldPass) accepted.",
                    "Ein Lauf ist nur OK, wenn der VOLLSTÄNDIGE Lauf besteht — eine vorzeitige/unvollständige Auswertung bleibt auf Wait und meldet nie OK. Ein Teil ohne Referenzdatei ist INVALID. LimitSample prüft beide Richtungen: jeder vorbereitete Fehler (ShouldFail) abgewiesen UND jedes Gut-Merkmal (ShouldPass) angenommen."),
                new("LimitSample references are one file per part (per DMC) under <ReferencePath>\\<Module>\\LimitSamples\\<DMC>.json, taught with the HarryLimitSample tool. MSA1 uses per-part reference files with automatic best-match; a blank DEMO_<Module>.json template is created per module to copy, rename and fill in.",
                    "LimitSample-Referenzen sind eine Datei pro Teil (pro DMC) unter <ReferencePath>\\<Modul>\\LimitSamples\\<DMC>.json, eingelernt mit dem Tool HarryLimitSample. MSA1 nutzt Referenzdateien pro Teil mit automatischem Best-Match; je Modul wird eine leere DEMO_<Modul>.json-Vorlage angelegt zum Kopieren, Umbenennen und Ausfüllen."),
            }),
            new("Notes", "Hinweise", new List<HelpStep>
            {
                new("Configuration lives in F:\\002_Configs (Harry.ini + Templates). The database and all tables are created automatically at startup.",
                    "Die Konfiguration liegt in F:\\002_Configs (Harry.ini + Templates). Datenbank und alle Tabellen werden beim Start automatisch angelegt."),
                new("The Tools tab launches the companion apps (Analysis, Graph, Counter, LimitSample, CollageCreator, Pareto). Each of them has its own F1 help. HarryPareto shows a live Top-20 of the production defect reasons and can also run remotely on another PC.",
                    "Der Tools-Tab startet die Companion-Apps (Analysis, Graph, Counter, LimitSample, CollageCreator, Pareto). Jede hat ihre eigene F1-Hilfe. HarryPareto zeigt live die Top-20 der Produktions-Fehlergründe und kann auch remote auf einem anderen PC laufen."),
            }),
        },
        Shortcuts: new List<HelpShortcut>
        {
            new("F1", "Open this help", "Diese Hilfe öffnen"),
        });
}
