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
                new("Each tab (Cameras, SPS, Database, CSV, Collage, MSA, Log, Tools) shows one subsystem and its live status.",
                    "Jeder Tab (Cameras, SPS, Database, CSV, Collage, MSA, Log, Tools) zeigt ein Subsystem und dessen Live-Status."),
                new("'Telegramme mitschneiden' (top bar) writes all incoming raw telegrams to the Capture folder next to the exe — a test/commissioning aid.",
                    "'Telegramme mitschneiden' (oben) schreibt alle eingehenden Roh-Telegramme in den Capture-Ordner neben der Exe — Test-/Inbetriebnahme-Hilfe."),
                new("The status bar shows overall health, the error count, uptime and the loaded Harry.ini.",
                    "Die Statusleiste zeigt Gesamt-Health, Fehlerzähler, Laufzeit und die geladene Harry.ini."),
            }),
            new("Notes", "Hinweise", new List<HelpStep>
            {
                new("Configuration lives in F:\\002_Configs (Harry.ini + Templates). The database and all tables are created automatically at startup.",
                    "Die Konfiguration liegt in F:\\002_Configs (Harry.ini + Templates). Datenbank und alle Tabellen werden beim Start automatisch angelegt."),
                new("The Tools tab launches the companion apps (Analysis, Graph, Counter, LimitSample, CollageCreator). Each of them has its own F1 help.",
                    "Der Tools-Tab startet die Companion-Apps (Analysis, Graph, Counter, LimitSample, CollageCreator). Jede hat ihre eigene F1-Hilfe."),
            }),
        },
        Shortcuts: new List<HelpShortcut>
        {
            new("F1", "Open this help", "Diese Hilfe öffnen"),
        });
}
