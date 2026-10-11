OB2 - download manager for Windows (portable)
=============================================

Requirements: Windows 10 1809 or later, or Windows 11, 64-bit. No .NET install needed.

Run
---
1. Unzip this folder anywhere you can write to (e.g. D:\Apps\OB2 or a USB stick).
2. Double-click OB2.exe. On first launch it asks where downloads should be saved.
3. Settings, history, and logs are kept next to OB2.exe because portable.flag is present.
   Delete portable.flag to keep them in %APPDATA%\OB2 instead. If this folder is
   read-only, OB2 uses %APPDATA%\OB2 automatically.

The first launch on a PC takes a few seconds longer: Windows unpacks OB2's runtime files
into a temporary folder once.

Add downloads by pasting a URL (Ctrl+N), dragging a link onto the window, or from the
command line:   OB2.exe "https://example.com/file.zip"

Chrome / Edge integration (optional)
------------------------------------
1. In OB2: Options > Browser > Register (covers both Chrome and Edge).
2. In the browser: open chrome://extensions (Edge: edge://extensions), turn on Developer
   mode, click "Load unpacked", and choose the chrome-extension folder next to OB2.exe.
3. Click a download link: OB2 takes it over. Hold Shift while clicking to let the
   browser download it instead. If OB2 cannot be reached, the browser downloads normally.
Moving this folder later is fine: OB2 re-registers itself on the next launch.

Antivirus / SmartScreen
-----------------------
OB2.exe and host.exe are not code-signed. Windows SmartScreen may show "Windows protected
your PC" the first time: choose "More info" > "Run anyway". Some antivirus products flag
unsigned single-file programs; compare the files with SHA256SUMS.txt from the release.

Uninstall
---------
Options > Browser > Unregister (if you registered it), then delete this folder.
