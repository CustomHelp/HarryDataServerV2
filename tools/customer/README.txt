HarryDataServer companion tool - customer package
==================================================

This ZIP is self-contained: it already includes the .NET runtime, so you do
NOT need to install anything. Just:

1. Unzip this folder anywhere (e.g. C:\HarryTools\<ToolName>).
2. Double-click <ToolName>.exe to start it.
3. On first start the tool reads the database connection from the Harry.ini
   next to the exe. Open Harry.ini in a text editor and set:
       Server       = the host / IP of your MySQL server
       GetPassword  = the read-only password your administrator gave you
   (The user "GetData" is read-only and can only SELECT from camera_data.)

4. If the tool cannot find a config, or you want to use a different one, use
   "Config-Pfad andern..." in the top bar to browse to a Harry.ini or type a
   path. Your choice is remembered per tool (stored under
   %APPDATA%\<ToolName>).

Notes
-----
- These tools are READ-ONLY. They never change production data.
- HarryPareto has its own connection dialog (IP + user + password) instead of
  a Harry.ini; enter the read-only account there.
- Features that need shares that do not exist on your PC (image folders, CSV
  export targets, etc.) are simply disabled - the tool will not crash.

Database access from another PC
-------------------------------
Your administrator must allow the read-only user from your network and open
the MySQL port - see readonly_user.sql and the deployment notes.
