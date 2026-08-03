HarryDataServer companion tools - customer package
==================================================

WHAT YOU HAVE
-------------
  Install.cmd     the installer - start here
  Tools\          the programs (HarryAnalysis, HarryGraph, HarryCounter,
                  HarryLimitSample, HarryCollageCreator, HarryPareto)
  Runtime\        the Microsoft .NET 8 Desktop Runtime (x64) installer
  Harry.ini       (inside each tool folder) the database settings
  readonly_user.sql  for your database administrator

If you received a single-tool ZIP instead, it holds one tool plus the same
Install.cmd.


INSTALLATION (2 minutes)
------------------------
  1. Extract the ZIP anywhere (e.g. to your Downloads folder).
  2. Double-click Install.cmd.
       - It installs to C:\HarryTools\<ToolName>. If that folder cannot be
         created it uses %LOCALAPPDATA%\HarryTools instead - either way NO
         D: drive and no second drive is needed.
       - It installs the .NET 8 Desktop Runtime if it is not on the PC yet
         (Windows may ask for administrator rights for that step only).
       - It creates a desktop shortcut per tool.
       - You may pass your own target folder:  Install.cmd C:\Programs\Harry
  3. Open the database settings once per tool (see next section).
  4. Start the tool from the desktop shortcut.

Re-running Install.cmd upgrades an existing installation and KEEPS your
already-filled-in Harry.ini.


DATABASE SETTINGS (once per tool)
---------------------------------
Open  C:\HarryTools\<ToolName>\Harry.ini  in Notepad and fill in the two
values marked <...>:

    [MySQL]
    Server=<DB_HOST_OR_IP>            -> host name or IP of the MySQL server
    GetPassword=<READONLY_PASSWORD>   -> the read-only password your admin set

That is all that is required. Everything else in the file is optional and
commented.

Alternatives:
  - Click "Change config path..." in a tool's top bar to point it at one shared
    Harry.ini (e.g. on a network share). The choice is remembered per tool in
    %APPDATA%\<Tool>\config.json.
  - Advanced: set the environment variable HARRY_CONFIG_DIR to the folder that
    holds your Harry.ini - all tools then use it.
  - HarryPareto has no Harry.ini: it asks for host / user / password in its own
    dialog on first start and stores it encrypted under %APPDATA%\HarryPareto.


GOOD TO KNOW
------------
- These tools are READ-ONLY. They never change production data.
- The account "GetData" only has SELECT on the camera_data database.
- Any feature that needs a folder or network share which does not exist on your
  PC (image folders, CSV export targets, the DMC scanner bridge) is simply
  disabled or falls back to a dialog - no tool crashes because of it.
- All user settings (theme, window state, config path, HarryPareto connection)
  are stored per user under %APPDATA% / %LOCALAPPDATA%, never on a fixed drive.
- Files the tools write by default (LimitSample references, CSV exports) go to
  %USERPROFILE%\Documents\HarryTools - change the paths in Harry.ini if you
  prefer somewhere else.


DATABASE ACCESS FROM ANOTHER PC
-------------------------------
Your administrator has to create the read-only user, allow it from your
network and open the MySQL port - see readonly_user.sql.
