[Setup]
AppName=PrintMark Enterprise
AppVersion=2.0
DefaultDirName={autopf}\PrintMark
DefaultGroupName=PrintMark Enterprise
OutputDir=..\ReleasePackages
OutputBaseFilename=PrintMark-Enterprise-Setup
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64

[Types]
Name: "full"; Description: "Full Installation (Server & Client)"
Name: "server"; Description: "Server & Admin Console Only"
Name: "client"; Description: "Client Daemon Only"

[Components]
Name: "server"; Description: "Central Server & Admin Studio"; Types: full server
Name: "client"; Description: "Print Interception Client (Daemon & Ghostscript)"; Types: full client

[Files]
; Server & Admin Studio
Source: "C:\Users\SURFACE\Desktop\PrintMark\PrintMark.Server\bin\Release\net8.0-windows\win-x64\*"; DestDir: "{app}\Server"; Flags: ignoreversion recursesubdirs; Components: server
Source: "C:\Users\SURFACE\Desktop\PrintMark\PrintMark.AdminApp\bin\Release\net8.0-windows\win-x64\*"; DestDir: "{app}\AdminApp"; Flags: ignoreversion recursesubdirs; Components: server

; Client Daemon
Source: "C:\Users\SURFACE\Desktop\PrintMark\PrintMark.Client\bin\Release\net8.0-windows\win-x64\*"; DestDir: "{app}\Client"; Flags: ignoreversion recursesubdirs; Components: client

; Ghostscript Binaries
Source: "C:\Users\SURFACE\Desktop\PrintMark\Gs\gs10.08.0\bin\*"; DestDir: "{app}\Client"; Flags: ignoreversion; Components: client

[Icons]
Name: "{group}\PrintMark Management Studio"; Filename: "{app}\AdminApp\PrintMark.AdminApp.exe"; Components: server
Name: "{commondesktop}\PrintMark Studio"; Filename: "{app}\AdminApp\PrintMark.AdminApp.exe"; Components: server

[Run]
; ---- SERVER STARTUP ----
Filename: "netsh.exe"; Parameters: "advfirewall firewall add rule name=""PrintMark Server Port 5000"" dir=in action=allow protocol=TCP localport=5000"; Components: server; Flags: runhidden
Filename: "sc.exe"; Parameters: "create PrintMarkServer binPath= ""{app}\Server\PrintMark.Server.exe"" start= auto obj= LocalSystem"; Components: server; Flags: runhidden
Filename: "sc.exe"; Parameters: "start PrintMarkServer"; Components: server; Flags: runhidden

; ---- CLIENT STARTUP ----
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""if (-not (Get-PrinterPort -Name 'PM_Cap_9101' -ErrorAction SilentlyContinue)) {{ Add-PrinterPort -Name 'PM_Cap_9101' -PrinterHostAddress '127.0.0.1' -PortNumber 9101 }; if (-not (Get-Printer -Name 'PrintMark-Protected' -ErrorAction SilentlyContinue)) {{ Add-Printer -Name 'PrintMark-Protected' -DriverName 'Microsoft PS Class Driver' -PortName 'PM_Cap_9101' }; Set-PrintConfiguration -PrinterName 'PrintMark-Protected' -PaperSize A4 -ErrorAction SilentlyContinue"""; Components: client; Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "create PrintMarkClient binPath= ""{app}\Client\PrintMark.Client.exe"" start= auto obj= LocalSystem"; Components: client; Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "failure PrintMarkClient reset= 0 actions= restart/1000/restart/1000/restart/1000"; Components: client; Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "start PrintMarkClient"; Components: client; Flags: runhidden waituntilterminated

[UninstallRun]
; ---- SERVER TEARDOWN ----
Filename: "sc.exe"; Parameters: "stop PrintMarkServer"; Components: server; Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "delete PrintMarkServer"; Components: server; Flags: runhidden waituntilterminated
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PrintMark Server Port 5000"""; Components: server; Flags: runhidden waituntilterminated

; ---- CLIENT TEARDOWN ----
Filename: "sc.exe"; Parameters: "stop PrintMarkClient"; Components: client; Flags: runhidden waituntilterminated
Filename: "sc.exe"; Parameters: "delete PrintMarkClient"; Components: client; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -Command ""Remove-Printer -Name 'PrintMark-Protected' -ErrorAction SilentlyContinue; Remove-PrinterPort -Name 'PM_Cap_9101' -ErrorAction SilentlyContinue"""; Components: client; Flags: runhidden waituntilterminated