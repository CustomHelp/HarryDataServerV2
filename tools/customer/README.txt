HarryDataServer companion tool - customer package
==================================================

PREREQUISITE: .NET 8 Desktop Runtime (x64)
------------------------------------------
These tools need the Microsoft ".NET 8 Desktop Runtime (x64)" on the PC. The
installer is provided in the SAME folder as these ZIP files
(windowsdesktop-runtime-8.x.x-win-x64.exe).

Steps (in this order):
  1. Install the .NET 8 Desktop Runtime (x64) - ONCE per PC. Skip if it is already
     installed. (Run the windowsdesktop-runtime-8.x.x-win-x64.exe from this folder.)
  2. Unzip this tool's ZIP anywhere (e.g. C:\HarryTools\<ToolName>).
  3. Double-click <ToolName>.exe to start it.
  4. Set the database connection on first start:
       - Most tools read the Harry.ini next to the exe: open it and set
         Server = your MySQL host / IP, and GetPassword = the read-only password.
       - Or click "Change config path..." in the top bar to point at another
         Harry.ini (the choice is remembered per tool under %APPDATA%).
       - HarryPareto has its own connection dialog instead (enter host / user /
         password there).

Notes
-----
- These tools are READ-ONLY. They never change production data.
- The user "GetData" is read-only (SELECT on camera_data only).
- Features that need network shares which do not exist on your PC (image folders,
  CSV export targets, ...) are simply disabled - the tool will not crash.

Database access from another PC
-------------------------------
Your administrator must allow the read-only user from your network and open the
MySQL port - see readonly_user.sql and the deployment notes.
