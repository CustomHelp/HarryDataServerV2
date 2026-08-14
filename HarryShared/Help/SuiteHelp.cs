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
                new("Your selected module stays selected across scans (as long as the scanned part has data for it), so you can scan and save right away without re-picking it.",
                    "Das gewählte Modul bleibt über Scans hinweg ausgewählt (solange das gescannte Teil Daten dafür hat), du kannst also scannen und direkt speichern, ohne es erneut zu wählen."),
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
                new("The saved reference records the MSA/LimitSample run it was taught from (source_base_id), looked up from the part's DMC — for traceability. It stays empty only if the part has no MSA run on record.",
                    "Die gespeicherte Referenz vermerkt den MSA/LimitSample-Lauf, aus dem sie eingelernt wurde (source_base_id), ermittelt über den DMC des Teils — zur Rückverfolgbarkeit. Leer bleibt sie nur, wenn zum Teil kein MSA-Lauf vorliegt."),
                new("'Config-Pfad ändern…' (top bar) selects which Harry.ini — and thus which database — the tool uses; the choice is saved per tool under %APPDATA% and applies after a restart.",
                    "'Config-Pfad ändern…' (obere Leiste) wählt, welche Harry.ini — und damit welche Datenbank — das Werkzeug nutzt; die Auswahl wird pro Werkzeug unter %APPDATA% gespeichert und gilt nach einem Neustart."),
                new("On a stand-alone PC (customer install via Install.cmd) the Harry.ini next to the exe is used automatically — only [MySQL] Server and GetPassword need to be filled in. Paths in it may contain %USERPROFILE% / %LOCALAPPDATA%, so no particular drive letter is required.",
                    "Auf einem Einzelplatz-PC (Kundeninstallation über Install.cmd) wird automatisch die Harry.ini neben der exe genutzt — einzutragen sind nur [MySQL] Server und GetPassword. Pfade darin dürfen %USERPROFILE% / %LOCALAPPDATA% enthalten, ein bestimmter Laufwerksbuchstabe ist also nicht nötig."),
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
                new("'Config-Pfad ändern…' (top bar) selects which Harry.ini — and thus which database — the tool uses; the choice is saved per tool under %APPDATA% and applies after a restart.",
                    "'Config-Pfad ändern…' (obere Leiste) wählt, welche Harry.ini — und damit welche Datenbank — das Werkzeug nutzt; die Auswahl wird pro Werkzeug unter %APPDATA% gespeichert und gilt nach einem Neustart."),
                new("On a stand-alone PC (customer install via Install.cmd) the Harry.ini next to the exe is used automatically — only [MySQL] Server and GetPassword need to be filled in. Paths in it may contain %USERPROFILE% / %LOCALAPPDATA%, so no particular drive letter is required.",
                    "Auf einem Einzelplatz-PC (Kundeninstallation über Install.cmd) wird automatisch die Harry.ini neben der exe genutzt — einzutragen sind nur [MySQL] Server und GetPassword. Pfade darin dürfen %USERPROFILE% / %LOCALAPPDATA% enthalten, ein bestimmter Laufwerksbuchstabe ist also nicht nötig."),
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
                new("'Config-Pfad ändern…' (top bar) selects which Harry.ini — and thus which database — the tool uses; the choice is saved per tool under %APPDATA% and applies after a restart.",
                    "'Config-Pfad ändern…' (obere Leiste) wählt, welche Harry.ini — und damit welche Datenbank — das Werkzeug nutzt; die Auswahl wird pro Werkzeug unter %APPDATA% gespeichert und gilt nach einem Neustart."),
                new("On a stand-alone PC (customer install via Install.cmd) the Harry.ini next to the exe is used automatically — only [MySQL] Server and GetPassword need to be filled in. Paths in it may contain %USERPROFILE% / %LOCALAPPDATA%, so no particular drive letter is required.",
                    "Auf einem Einzelplatz-PC (Kundeninstallation über Install.cmd) wird automatisch die Harry.ini neben der exe genutzt — einzutragen sind nur [MySQL] Server und GetPassword. Pfade darin dürfen %USERPROFILE% / %LOCALAPPDATA% enthalten, ein bestimmter Laufwerksbuchstabe ist also nicht nötig."),
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
                new("'Config-Pfad ändern…' (top bar) selects which Harry.ini — and thus which database — the tool uses; the choice is saved per tool under %APPDATA% and applies after a restart.",
                    "'Config-Pfad ändern…' (obere Leiste) wählt, welche Harry.ini — und damit welche Datenbank — das Werkzeug nutzt; die Auswahl wird pro Werkzeug unter %APPDATA% gespeichert und gilt nach einem Neustart."),
                new("On a stand-alone PC (customer install via Install.cmd) the Harry.ini next to the exe is used automatically — only [MySQL] Server and GetPassword need to be filled in. Paths in it may contain %USERPROFILE% / %LOCALAPPDATA%, so no particular drive letter is required.",
                    "Auf einem Einzelplatz-PC (Kundeninstallation über Install.cmd) wird automatisch die Harry.ini neben der exe genutzt — einzutragen sind nur [MySQL] Server und GetPassword. Pfade darin dürfen %USERPROFILE% / %LOCALAPPDATA% enthalten, ein bestimmter Laufwerksbuchstabe ist also nicht nötig."),
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
                new("'Config-Pfad ändern…' (top bar) selects which Harry.ini the tool uses (optional here — the tool mainly edits Collage.ini); saved per tool under %APPDATA%, applies after a restart.",
                    "'Config-Pfad ändern…' (obere Leiste) wählt, welche Harry.ini das Werkzeug nutzt (hier optional — das Werkzeug bearbeitet vor allem Collage.ini); pro Werkzeug unter %APPDATA% gespeichert, gilt nach Neustart."),
                new("On a stand-alone PC (customer install via Install.cmd) the Harry.ini next to the exe is used automatically. Paths in it may contain %USERPROFILE% / %LOCALAPPDATA%, so no particular drive letter is required — [Collage] Collage_IniPath points at the layout file the tool opens on start.",
                    "Auf einem Einzelplatz-PC (Kundeninstallation über Install.cmd) wird automatisch die Harry.ini neben der exe genutzt. Pfade darin dürfen %USERPROFILE% / %LOCALAPPDATA% enthalten, ein bestimmter Laufwerksbuchstabe ist also nicht nötig — [Collage] Collage_IniPath zeigt auf die Layout-Datei, die das Werkzeug beim Start öffnet."),
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
            "that have not finished still count. Status 2 (not evaluated) never counts. By default a " +
            "station's cameras (KF1/KF3) are merged into one bar and _S1.._S5 sensors into one family; " +
            "clicking a bar opens the origin (module × nest) breakdown.",
        DescriptionDe:
            "HarryPareto zeigt live ein Pareto der Produktions-Fehlergründe. Kennzahl je Merkmal ist die " +
            "Anzahl BETROFFENER TEILE (eindeutige Seriennummern mit result_status = 0) im Zeitfenster, " +
            "dazu die Gesamt-Vorkommen als Zweitzahl. Nur lesend, kombiniert Rahmen- und Trimmer-Tabelle " +
            "(damit M20/M21 enthalten sind) und ohne Join auf dmcserial, sodass auch nicht abgeschlossene " +
            "Teile mitzählen. Status 2 (nicht bewertet) zählt nie mit. Standardmäßig werden die Kameras " +
            "einer Station (KF1/KF3) zu einem Balken und _S1..S5-Sensoren zu einer Familie zusammengefasst; " +
            "ein Klick auf einen Balken öffnet die Herkunft (Modul × Nest).",
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
                new("The Top-20 bars are largest first; each shows the affected-part count, its % and the occurrences, and is coloured by module_ref (see the legend). The trend arrow comes from the WITHIN-window trajectory: the defect rate is bucketed into N time slices and the last third is compared to the first third — red up = rising, green down = falling, grey = change below the neutral band (±10 %). The arrow's tooltip shows 'first third … % → last third … %'; a mini sparkline of the slice rates sits next to it ('Show sparklines').",
                    "Die Top-20-Balken stehen größter zuerst; jeder zeigt die Anzahl betroffener Teile, deren % und die Vorkommen, und ist nach module_ref eingefärbt (siehe Legende). Der Trend-Pfeil kommt aus dem VERLAUF INNERHALB des Fensters: die Fehlerrate wird in N Zeitscheiben gebucketed und das letzte Drittel mit dem ersten verglichen — rot hoch = steigend, grün runter = fallend, grau = Änderung unter dem Neutralband (±10 %). Der Tooltip des Pfeils zeigt 'first third … % → last third … %'; daneben eine Mini-Sparkline der Scheiben-Raten ('Show sparklines')."),
                new("A bar is a stack with one segment per camera (KF1/KF3, distinguished by shade) and one entry per sensor family — the hover tooltip lists the exact per-camera and per-sensor split so a skew (e.g. a feature only failing on KF3) stays visible.",
                    "Ein Balken ist ein Stack mit einem Segment je Kamera (KF1/KF3, per Helligkeit unterschieden) und je Sensor-Familie — der Tooltip beim Überfahren zeigt die genaue Aufteilung je Kamera und je Sensor, sodass eine Schieflage (z. B. ein Merkmal, das nur auf KF3 fehlschlägt) sichtbar bleibt."),
                new("The module chart shows the share per REAL origin module (M1x → M10/M11, M2x → M20/M21, from dmcserial); parts not yet exited form a separate '<ref> (unbekannt)' segment. Click a bar to filter the Top-20 to that origin; clear it with the ✕ chip.",
                    "Das Modul-Chart zeigt den Anteil je ECHTEM Herkunftsmodul (M1x → M10/M11, M2x → M20/M21, aus dmcserial); noch nicht ausgetretene Teile bilden ein eigenes Segment '<ref> (unbekannt)'. Balken anklicken filtert die Top-20 auf diese Herkunft; über das ✕-Feld zurücksetzen."),
                new("A warning box lists controllers that only produced status 2 ('camera did not judge'). The shift comparison contrasts the current shift's rate with the previous shift's.",
                    "Ein Warnfeld listet Controller, die nur Status 2 lieferten ('Kamera bewertet nicht'). Der Schichtvergleich stellt die Quote der aktuellen Schicht der Vorschicht gegenüber."),
            }),
            new("Origin (click a bar)", "Herkunft (Balken anklicken)", new List<HelpStep>
            {
                new("Click a bar to open the origin breakdown: a matrix of origin module (M10 vs M11, M20 vs M21, …) × nest, read from dmcserial for exactly this feature's parts.",
                    "Balken anklicken öffnet die Herkunft: eine Matrix aus Herkunftsmodul (M10 vs. M11, M20 vs. M21, …) × Nest, aus dmcserial für genau die Teile dieses Merkmals gelesen."),
                new("Each cell shows affected / inspected parts and the defect RATE (affected ÷ inspected of that module/nest). Cells with a clearly-elevated rate are highlighted red — read the rate, not only the count.",
                    "Jede Zelle zeigt betroffene / geprüfte Teile und die Fehler-RATE (betroffene ÷ geprüfte dieses Moduls/Nests). Zellen mit deutlich überhöhter Rate sind rot hervorgehoben — die Rate lesen, nicht nur die Anzahl."),
                new("Concentration on one module/nest → check the mechanics/process there; an even spread → more likely material or camera evaluation. Origin is only known after part exit (dmcserial); features without a strand reference (module_ref NoRef) show a note instead.",
                    "Konzentration auf ein Modul/Nest → Mechanik/Prozess dort prüfen; Gleichverteilung → eher Material oder Kameraauswertung. Die Herkunft ist erst nach dem Teile-Austritt bekannt (dmcserial); Merkmale ohne Strang-Bezug (module_ref NoRef) zeigen stattdessen einen Hinweis."),
            }),
            new("Filters, refresh, export", "Filter, Aktualisierung, Export", new List<HelpStep>
            {
                new("Filter by time window (Shift / 30 min / 1 h / 2 h / 4 h / 8 h / 16 h / 1 day / 2 days / 3 days / 7 days) and controller. Auto-refresh runs every N seconds (default 30, editable); on a DB error a hint is shown instead of a crash.",
                    "Nach Zeitfenster (Shift / 30 min / 1 h / 2 h / 4 h / 8 h / 16 h / 1 day / 2 days / 3 days / 7 days) und Controller filtern. Auto-Refresh läuft alle N Sekunden (Standard 30, editierbar); bei DB-Fehler erscheint ein Hinweis statt eines Absturzes."),
                new("'Split by camera' turns off the station merge (shows KF1/KF3 as their own bars); 'Split sensors' turns off the _S1.._S5 family grouping. Both default to merged. 'Reset View' clears the bar-click filter + selection and restores the default view; clicking the already-active module bar also clears the filter.",
                    "'Split by camera' hebt die Stations-Zusammenfassung auf (zeigt KF1/KF3 als eigene Balken); 'Split sensors' hebt die _S1..S5-Familien-Gruppierung auf. Beide standardmäßig zusammengefasst. 'Reset View' setzt Balken-Filter + Auswahl zurück auf die Standardansicht; ein erneuter Klick auf den aktiven Modul-Balken hebt den Filter ebenfalls auf."),
                new("TV mode enlarges everything for a wall display. CSV export writes the current Top-20 to a semicolon CSV.",
                    "Der TV-Modus vergrößert alles für eine Wandanzeige. CSV export schreibt die aktuelle Top-20 in eine Semikolon-CSV."),
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
                new("Each tab (Cameras, PLC, Scanner, MSA, Database, System, CSV, Collage, Log, Tools) shows one subsystem and its live status.",
                    "Jeder Tab (Cameras, PLC, Scanner, MSA, Database, System, CSV, Collage, Log, Tools) zeigt ein Subsystem und dessen Live-Status."),
                new("'Capture telegrams' (on the Log tab) writes all incoming raw telegrams to the Capture folder next to the exe — a test/commissioning aid.",
                    "'Capture telegrams' (im Log-Tab) schreibt alle eingehenden Roh-Telegramme in den Capture-Ordner neben der Exe — Test-/Inbetriebnahme-Hilfe."),
                new("The status bar shows overall health, the error count, uptime and the loaded Harry.ini. Truncated texts show their full content as a tooltip on hover.",
                    "Die Statusleiste zeigt Gesamt-Health, Fehlerzähler, Laufzeit und die geladene Harry.ini. Abgeschnittene Texte zeigen ihren vollen Inhalt als Tooltip beim Überfahren."),
            }),
            new("Cameras & PLC", "Cameras & PLC", new List<HelpStep>
            {
                new("Cameras tab: one card per controller shows the connection LEDs, the operating mode and the last telegrams (at least 4 lines, scrollable — some controllers inspect 4 parts per cycle). Right-click a telegram line to copy its serial.",
                    "Cameras-Tab: eine Karte je Controller zeigt die Verbindungs-LEDs, den Betriebsmodus und die letzten Telegramme (mindestens 4 Zeilen, scrollbar — manche Controller prüfen 4 Teile pro Takt). Rechtsklick auf eine Telegrammzeile kopiert die Seriennummer."),
                new("PLC tab (formerly SPS): one card per PLC channel (KeepAlive, Part Exit, the five MSA channels). Each card shows the port, the connection LED and the last requests/responses (up to 20 lines, oldest at the top, newest at the bottom). The Part-Exit card also shows each response's processing time, e.g. '…;true (87 ms)' — display only, the telegram sent to the PLC is unchanged.",
                    "PLC-Tab (früher SPS): eine Karte je SPS-Kanal (KeepAlive, Part Exit, die fünf MSA-Kanäle). Jede Karte zeigt Port, Verbindungs-LED und die letzten Requests/Responses (bis zu 20 Zeilen, älteste oben, neueste unten). Die Part-Exit-Karte zeigt zusätzlich die Verarbeitungsdauer je Antwort, z. B. '…;true (87 ms)' — reine Anzeige, das an die SPS gesendete Telegramm bleibt unverändert."),
                new("Auto-scroll (log tab and both channel lists): while the view sits at the bottom it follows new entries. Scrolling up — or clicking a line — pauses that: the view stays exactly where it is and new entries pile up below. A small '▼ n new' button then appears bottom-right; it counts the entries added since the pause and jumps back to the newest when clicked. Scrolling back to the bottom resumes following as well. Nothing scrolls while you hold the mouse button, and the selected line survives the periodic refresh — so 'Copy line' (context menu or Ctrl+C) works while the log keeps running.",
                    "Auto-Scroll (Log-Tab und beide Kanal-Listen): steht die Ansicht ganz unten, folgt sie neuen Einträgen. Nach-oben-Scrollen — oder ein Klick auf eine Zeile — pausiert das: die Ansicht bleibt exakt stehen, neue Einträge laufen unten weiter auf. Unten rechts erscheint dann ein kleiner Button '▼ n new'; er zählt die seit der Pause hinzugekommenen Einträge und springt bei Klick zurück zum neuesten. Zurückscrollen nach unten reaktiviert das Folgen ebenfalls. Solange die Maustaste gedrückt ist, wird nie gescrollt, und die gewählte Zeile übersteht die periodische Aktualisierung — 'Copy line' (Kontextmenü oder Strg+C) funktioniert also bei laufendem Log."),
            }),
            new("Part exit & images", "Teile-Austritt & Bilder", new List<HelpStep>
            {
                new("On each finished part (PLC channel 2, ST160) the server writes the dmcserial row and the production CSV row, then handles the part's low-res images. EVERY part-exit image action searches the low-res individual folder ONLY (recursively). The NG folder (03), the diagnostic folder (04) and the GoldenSample folder (05) are written by the camera and cleaned by retention — the part-exit flows never touch them. MSA images (Serial2 carries a DMC) and filenames that do not match the camera spec are never deleted.",
                    "Bei jedem fertigen Teil (SPS-Kanal 2, ST160) schreibt der Server die dmcserial-Zeile und die Produktions-CSV-Zeile und behandelt dann die LowRes-Bilder des Teils. JEDE Part-Exit-Bildaktion sucht NUR im LowRes-Einzelbildordner (rekursiv). Der NG-Ordner (03), der Diagnose-Ordner (04) und der GoldenSample-Ordner (05) werden von der Kamera geschrieben und nur von der Retention geräumt — die Part-Exit-Abläufe fassen sie nie an. MSA-Bilder (Serial2 trägt einen DMC) und Dateinamen, die nicht der Kamera-Spec entsprechen, werden nie gelöscht."),
                new("OK part: the image action depends on [Collage] Collage_Generate ONLY. Collage on → the collage is built into [Collage] Collage_ResultImages and the originals are then DELETED (the collage is the evidence). Collage off → the originals are MOVED to [NAS] BackupFolder\\YYYY\\MM\\DD (copy, size-verify, delete), aged out by [Retention] Images_Backup. [NAS] DeletePictures is deprecated and ignored — a still-present key is reported as a WARNING at startup.",
                    "GUT-Teil: die Bildaktion hängt AUSSCHLIESSLICH an [Collage] Collage_Generate. Collage an → die Collage wird nach [Collage] Collage_ResultImages geschrieben und die Originale danach GELÖSCHT (die Collage ist der Nachweis). Collage aus → die Originale werden nach [NAS] BackupFolder\\JJJJ\\MM\\TT VERSCHOBEN (kopieren, Größe prüfen, löschen) und über [Retention] Images_Backup nach Alter bereinigt. [NAS] DeletePictures ist abgekündigt und wird ignoriert — ein noch vorhandener Key wird beim Start als WARNUNG gemeldet."),
                new("NG part: the low-res images are DELETED without a replacement — the NG evidence is the full-res image in 03_High_Resolution_NG, which stays untouched. Unknown result (field 14 is neither OK/NG/DE): treated like NG (dmcserial row + image delete) but WITHOUT a CSV row, so the production CSV only ever contains parts whose result we understand; every such part is reported as a WARNING with the raw field and the raw telegram.",
                    "NG-Teil: die LowRes-Bilder werden ersatzlos GELÖSCHT — der NG-Nachweis ist das Vollbild in 03_High_Resolution_NG, das unberührt bleibt. Unbekanntes Ergebnis (Feld 14 ist weder OK/NG/DE): wird wie NG behandelt (dmcserial-Zeile + Bildlöschung), aber OHNE CSV-Zeile, damit in der Produktions-CSV nur Teile mit verstandenem Ergebnis stehen; jedes solche Teil wird als WARNUNG mit Rohfeld und Roh-Telegramm gemeldet."),
                new("DE (scrapped part): a part discarded at ST160 is not a finished part, so the server writes NOTHING to dmcserial and NOTHING to the production CSV — it only deletes the part's low-res images. DE carries the full frame SZID (assembled part), or only the trimmer serial (loose rejected trimmer), so images are deleted by BOTH the frame SZID (19) and the trimmer serial (13) when present. The measurement rows stay; the log line with the count and the keys is the record (WARNING when nothing is found).",
                    "DE (ausgeschleustes Teil): ein an ST160 verworfenes Teil ist kein fertiges Teil, daher schreibt der Server NICHTS in dmcserial und NICHTS in die Produktions-CSV — er löscht nur die LowRes-Bilder des Teils. DE trägt die volle Rahmen-SZID (montiertes Teil) oder nur die Trimmer-Serie (loser Ausschuss-Trimmer); gelöscht wird deshalb über BEIDE — Rahmen-SZID (19) UND Trimmer-Serie (13), sofern vorhanden. Die Messzeilen bleiben; die Log-Zeile mit Anzahl und Schlüsseln ist der Nachweis (WARNUNG, wenn nichts gefunden)."),
                new("Production CSV column layout (changed 2026-07-28): the two strands and the two M50 ST110 control windows now SHARE their measurement columns, because they are mutually exclusive per part and their variable names are identical. Header row 1 shows the merge group — 'M1x_<Station>_<KF>' for M10/M11, 'M2x_…' for M20/M21, 'M50_ST110' for KF1/KF3; every other controller is unchanged. That takes the file from 722 to 431 columns WITHOUT losing a value (the removed columns were always empty). The new meta column 'M50St110Kf' (1/3, empty without an ST110 measurement) sits behind M50Nest and records which control window supplied the values. Files written before the change keep the old layout — the header is fixed when a file is created, so no file mixes both.",
                    "Spaltenlayout der Produktions-CSV (geändert am 28.07.2026): die beiden Stränge und die beiden Kontrollfenster von M50 ST110 TEILEN sich jetzt ihre Messspalten, weil sie sich je Teil ausschließen und ihre Variablennamen identisch sind. Kopfzeile 1 zeigt die Merge-Gruppe — 'M1x_<Station>_<KF>' für M10/M11, 'M2x_…' für M20/M21, 'M50_ST110' für KF1/KF3; alle anderen Controller bleiben unverändert. Damit geht die Datei von 722 auf 431 Spalten, OHNE einen Wert zu verlieren (die entfallenen Spalten waren immer leer). Die neue Metaspalte 'M50St110Kf' (1/3, leer ohne ST110-Messung) steht hinter M50Nest und hält fest, welches Kontrollfenster die Werte geliefert hat. Dateien von vor der Änderung behalten das alte Layout — der Header wird beim Anlegen der Datei festgelegt, keine Datei mischt beide."),
                new("A part exit without a frame serial writes NO dmcserial row (it would collide with every other serial-less part on the unique serial key) — it is reported as a WARNING with the raw telegram instead. The PLC tab's Part-Exit card shows each response with its processing time, e.g. '…;true (87 ms)'; that suffix is display-only, the telegram sent to the PLC is unchanged.",
                    "Ein Part-Exit ohne Rahmen-Seriennummer schreibt KEINE dmcserial-Zeile (sie würde über den Unique-Key mit allen anderen serienlosen Teilen kollidieren) — er wird stattdessen als WARNUNG mit dem Roh-Telegramm gemeldet. Die Part-Exit-Karte im PLC-Tab zeigt zu jeder Antwort die Verarbeitungsdauer, z. B. '…;true (87 ms)'; dieser Zusatz ist reine Anzeige, das an die SPS gesendete Telegramm bleibt unverändert."),
                new("Serial normalisation: the frame serial (SZID, M1X/M5X) is 19 characters, the trimmer serial (Virtual Serial, M20/M21) 13. Both lengths are set in [General] (SerialNumberLength / TrimmerSerialNumberLength). The camera pads them with trailing zeros; both the camera and the ST160 paths normalise to these lengths so the DB, the part-exit measurement lookup and the image search all use the same unpadded serial.",
                    "Serien-Normalisierung: die Rahmen-Serie (SZID, M1X/M5X) hat 19 Zeichen, die Trimmer-Serie (Virtual Serial, M20/M21) 13. Beide Längen stehen in [General] (SerialNumberLength / TrimmerSerialNumberLength). Die Kamera füllt mit Nullen auf; Kamera- und ST160-Pfad normalisieren auf diese Längen, damit DB, Part-Exit-Messungssuche und Bildsuche dieselbe ungepaddete Serie verwenden."),
            }),
            new("MSA & LimitSample", "MSA & LimitSample", new List<HelpStep>
            {
                new("The MSA tab shows, per selected run, a parts list (DMC · verdict · x/y ok · matched MSA1 reference) and, for the selected part, its measurements (ok / not ok / n.a. + reason).",
                    "Der MSA-Tab zeigt je gewähltem Lauf eine Teile-Liste (DMC · Ergebnis · x/y ok · zugeordnete MSA1-Referenz) und, für das gewählte Teil, dessen Messungen (ok / nicht ok / n.a. + Grund)."),
                new("Buttons act on the selected part: Open PDF Complete, Open PDF (failures only) and Open Folder. LimitSample/MSA1 generate one PDF pair PER PART (BaseID + DMC in the file name).",
                    "Buttons wirken auf das gewählte Teil: PDF komplett öffnen, PDF (nur Fehler) und Ordner öffnen. LimitSample/MSA1 erzeugen ein PDF-Paar PRO TEIL (BaseID + DMC im Dateinamen)."),
                new("A run is only OK when the COMPLETE run passes — a premature/partial evaluation stays in Wait and never reports OK. A part without its reference file is INVALID. LimitSample checks both directions: every prepared error (ShouldFail) rejected AND every good feature (ShouldPass) accepted.",
                    "Ein Lauf ist nur OK, wenn der VOLLSTÄNDIGE Lauf besteht — eine vorzeitige/unvollständige Auswertung bleibt auf Wait und meldet nie OK. Ein Teil ohne Referenzdatei ist INVALID. LimitSample prüft beide Richtungen: jeder vorbereitete Fehler (ShouldFail) abgewiesen UND jedes Gut-Merkmal (ShouldPass) angenommen."),
                new("Good reference parts are allowed: a part with NO prepared error (only ShouldPass) passes as 'Gut-Referenz'; a false reject on such a part is a FAIL. But a run made up of ONLY good samples (no prepared error checked anywhere) is INVALID — it would prove nothing. Every INVALID shows a plain reason next to the badge (UI + PDF), e.g. 'nur Gut-Muster im Lauf, kein erwarteter Fehler geprüft' or 'Teil ohne Referenzdatei: <DMC>'.",
                    "Gut-Referenzteile sind erlaubt: ein Teil OHNE erwarteten Fehler (nur ShouldPass) besteht als 'Gut-Referenz'; ein Falsch-Ausschuss an so einem Teil ist FAIL. Ein Lauf aber, der NUR aus Gut-Mustern besteht (nirgends ein erwarteter Fehler geprüft), ist INVALID — er würde nichts belegen. Jedes INVALID zeigt neben dem Badge einen Klartext-Grund (UI + PDF), z. B. 'nur Gut-Muster im Lauf, kein erwarteter Fehler geprüft' oder 'Teil ohne Referenzdatei: <DMC>'."),
                new("LimitSample has its own compact PDF (head + 'prepared errors x of y detected' + 'deviations' + an Expected/Actual table). In the MSA tab the two PDF buttons sit UNDER the parts list and act on the SELECTED part (disabled until one is picked); 'Open Folder (Run)' at the top opens the whole run's folder. A deviation history lists recent runs (date · BaseID · verdict · which measurements deviated) — click one to load it. Everything a run produces lives in ONE folder under [MSA] ReportPath: PDF\\ (reports), RAW\\ (Minitab export) and IMG\\ (the run images, MOVED out of the GoldenSample transit folder). The separate MSA summary CSV under [CSV] CSV_MSAPath was removed — the numbers are in msa_results.",
                    "LimitSample hat ein eigenes kompaktes PDF (Kopf + 'Grenzmuster-Fehler x von y erkannt' + 'Abweichungen' + Tabelle Erwartet/Ist). Im MSA-Tab liegen die zwei PDF-Buttons UNTER der Teile-Liste und gelten fürs GEWÄHLTE Teil (deaktiviert bis eines gewählt ist); 'Open Folder (Run)' oben öffnet den Ordner des ganzen Laufs. Eine Abweichungs-Historie listet die letzten Läufe (Datum · BaseID · Ergebnis · abweichende Merkmale) — Klick lädt den Lauf. Alles, was ein Lauf erzeugt, liegt in EINEM Ordner unter [MSA] ReportPath: PDF\\ (Reports), RAW\\ (Minitab-Export) und IMG\\ (die Laufbilder, aus dem GoldenSample-Transitordner VERSCHOBEN). Die separate MSA-Summen-CSV unter [CSV] CSV_MSAPath wurde entfernt — die Zahlen stehen in msa_results."),
                new("The MSA views refresh automatically: switching the MSA tab or the module/type sub-tab reloads the runs, and when a new run finishes the history updates — if you are on the newest run it jumps to the new one, otherwise a 'New run available – click to load' banner appears without disturbing the run you are viewing.",
                    "Die MSA-Ansichten aktualisieren sich automatisch: Wechsel des MSA-Tabs oder des Modul-/Typ-Untertabs lädt die Läufe neu, und wenn ein neuer Lauf fertig ist, aktualisiert sich die Historie — auf dem neuesten Lauf springt sie mit, sonst erscheint ein Banner 'New run available – click to load', ohne den betrachteten Lauf wegzureißen."),
                new("LimitSample references are one file per part (per DMC) under <ReferencePath>\\<Module>\\LimitSamples\\<DMC>.json, taught with the HarryLimitSample tool. MSA1 uses per-part reference files with automatic best-match; a blank DEMO_<Module>.json template is created per module to copy, rename and fill in.",
                    "LimitSample-Referenzen sind eine Datei pro Teil (pro DMC) unter <ReferencePath>\\<Modul>\\LimitSamples\\<DMC>.json, eingelernt mit dem Tool HarryLimitSample. MSA1 nutzt Referenzdateien pro Teil mit automatischem Best-Match; je Modul wird eine leere DEMO_<Modul>.json-Vorlage angelegt zum Kopieren, Umbenennen und Ausfüllen."),
            }),
            new("System resources", "Systemauslastung", new List<HelpStep>
            {
                new("The System tab shows the live machine load: whole-machine CPU utilisation, physical RAM used of total, this server process's own CPU + memory, and the MySQL (mysqld) process CPU + memory with a running LED.",
                    "Der System-Tab zeigt die Live-Auslastung des Rechners: CPU-Auslastung der gesamten Maschine, belegter physischer RAM vom Gesamtspeicher, CPU + Speicher dieses Server-Prozesses selbst sowie CPU + Speicher des MySQL-Prozesses (mysqld) mit Läuft-LED."),
                new("Below the MySQL card, the active MySQL connections and the server uptime are read from the database. Load bars are green up to 70 %, orange above 70 % and red above 90 %.",
                    "Unter der MySQL-Karte werden die aktiven MySQL-Verbindungen und die Server-Laufzeit aus der Datenbank gelesen. Die Auslastungsbalken sind bis 70 % grün, über 70 % orange und über 90 % rot."),
                new("CPU and RAM refresh about every 2 seconds; the MySQL connection/uptime figures about every 5 seconds.",
                    "CPU und RAM aktualisieren sich etwa alle 2 Sekunden; die MySQL-Verbindungs-/Laufzeitwerte etwa alle 5 Sekunden."),
                new("A free-disk watchdog ([Monitoring]) checks every drive the server writes to — including MySQL's own tmpdir and datadir, which it asks the running server for — every DiskCheckIntervalMinutes (default 15). Below DiskWarnFreeGB (default 10) the log shows a WARNING, below DiskCriticalFreeGB (default 2) an ERROR; each line names the drive, the free space and everything that uses it. A level change is logged once and a drive that stays low is repeated only every 6 hours, so the warning counter is not flooded. The watchdog only reports — it never deletes anything.",
                    "Ein Speicherplatz-Wächter ([Monitoring]) prüft alle DiskCheckIntervalMinutes (Standard 15) jedes Laufwerk, auf das der Server schreibt — auch MySQLs eigenes tmpdir und datadir, die er beim laufenden Server erfragt. Unter DiskWarnFreeGB (Standard 10) erscheint eine WARNUNG im Log, unter DiskCriticalFreeGB (Standard 2) ein FEHLER; jede Zeile nennt Laufwerk, freien Platz und alles, was darauf schreibt. Ein Wechsel der Stufe wird einmal protokolliert, ein dauerhaft knappes Laufwerk nur alle 6 Stunden wiederholt, damit der Warnungszähler nicht überläuft. Der Wächter meldet nur — er löscht nie etwas."),
                new("Background: a full C: drive is not obvious from the application, because MySQL puts its temporary files there by default. Only queries big enough to spill a temp table to disk fail ('Error writing file ... errno 28') while everything small keeps working — on 2026-08-14 exactly that silently cost the MSA raw-data export of two M50 runs. On the line server MySQL's tmpdir was therefore moved to E:.",
                    "Hintergrund: Eine volle C-Platte fällt in der Anwendung nicht auf, weil MySQL seine temporären Dateien standardmäßig dort ablegt. Nur Abfragen, die groß genug für eine Temp-Tabelle auf Platte sind, schlagen fehl ('Error writing file … errno 28'), alles Kleine läuft weiter — am 14.08.2026 kostete genau das unbemerkt den MSA-Rohdaten-Export von zwei M50-Läufen. Auf dem Linien-Server wurde MySQLs tmpdir deshalb auf E: verlegt."),
            }),
            new("Notes", "Hinweise", new List<HelpStep>
            {
                new("Configuration lives in F:\\002_Configs (Harry.ini + Templates). The database and all tables are created automatically at startup.",
                    "Die Konfiguration liegt in F:\\002_Configs (Harry.ini + Templates). Datenbank und alle Tabellen werden beim Start automatisch angelegt."),
                new("Retention is central: the [Retention] section sets one age in DAYS per target (0 = never) — images (NG/Diagnostic/GoldenSample/Collage/Backup + \\Input leftovers), MSA reports, CSV exports and the database. One service runs at startup and every 24 h; production DB uses partition drop + bounded batch delete (no long locks), master data is never touched, and Database_MSA / Reports_MSA default to 0 (never) because they are QS data. Every target logs what it did; legacy retention keys still work as a deprecated fallback. MSA images are spared by the \\Input sweep in folders 01–04, but NOT in 05_GoldenSample: that folder is a transit buffer (a finished run moves its images to [MSA] ReportPath), so leftovers there — aborted runs and M1X production images — are deleted after Images_InputLeftovers days. A filename that cannot be parsed is never deleted anywhere.",
                    "Retention ist zentral: die Sektion [Retention] legt je Ziel EIN Alter in TAGEN fest (0 = nie) — Bilder (NG/Diagnose/GoldenSample/Collage/Backup + \\Input-Überbleibsel), MSA-Reports, CSV-Exporte und die Datenbank. Ein Dienst läuft beim Start und alle 24 h; die Produktions-DB nutzt Partition-Drop + begrenztes Batch-Delete (keine langen Locks), Stammdaten werden nie angefasst, und Database_MSA / Reports_MSA stehen per Default auf 0 (nie), weil es QS-Daten sind. Jedes Ziel protokolliert, was es getan hat; alte Retention-Keys wirken weiter als veralteter Fallback. MSA-Bilder werden vom \\Input-Sweep in den Ordnern 01–04 geschont, in 05_GoldenSample aber NICHT: dieser Ordner ist ein Transit-Puffer (ein fertiger Lauf verschiebt seine Bilder nach [MSA] ReportPath), daher werden Überbleibsel dort — Abbrüche und M1X-Produktionsbilder — nach Images_InputLeftovers Tagen gelöscht. Ein Dateiname, der nicht geparst werden kann, wird nirgends gelöscht."),
                new("The Tools tab launches the companion apps (Analysis, Graph, Counter, LimitSample, CollageCreator, Pareto). Each of them has its own F1 help. HarryPareto shows a live Top-20 of the production defect reasons and can also run remotely on another PC.",
                    "Der Tools-Tab startet die Companion-Apps (Analysis, Graph, Counter, LimitSample, CollageCreator, Pareto). Jede hat ihre eigene F1-Hilfe. HarryPareto zeigt live die Top-20 der Produktions-Fehlergründe und kann auch remote auf einem anderen PC laufen."),
            }),
        },
        Shortcuts: new List<HelpShortcut>
        {
            new("F1", "Open this help", "Diese Hilfe öffnen"),
        });
}
