; Installer pentru agentul TailControl (serviciu Windows + helper de blocare).
; Compilare: vezi build-installer.ps1 (ISCC /DAppVersion=... /DSourceDir=... /DOutputDir=...).
;
; Ce face la instalare (ca administrator):
;   1. copiaza aplicatia (self-contained, fara runtime .NET separat) in Program Files
;   2. creeaza %ProgramData%\TailControl\Agent\config (doar admin scrie) si \logs (serviciul scrie)
;   3. scrie config\agent.json (numele MagicDNS, DryRun, certificat) doar la prima instalare
;   4. obtine certificatul HTTPS cu `tailscale cert` (optional) + task programat de reinnoire
;   5. regula de firewall: TCP 8444 permis doar din 100.64.0.0/10, doar pentru TailControl.Agent.exe
;   6. serviciul TailControlAgent ca LocalService, cu privilegii restranse si repornire la esec
;   7. helper-ul de blocare pornit la logare (HKLM\...\Run) si pornit acum pentru utilizatorul curent
; Dezinstalarea le inlatura pe toate; datele din %ProgramData% se sterg doar daca alegi asta.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\agent-staging"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

#define ServiceName "TailControlAgent"
#define FirewallRule "TailControl Agent (Tailscale)"
#define CertTask "TailControl\AgentCertRenew"

[Setup]
AppId={{6F1C2E9A-4B7D-4E39-9A51-2D8C7B3F0A11}
AppName=TailControl Agent
AppVersion={#AppVersion}
AppPublisher=TailControl
AppPublisherURL=https://github.com/papura-octavian/tailcontrol
DefaultDirName={autopf}\TailControl\Agent
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=TailControl-Agent-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
UninstallDisplayName=TailControl Agent
UninstallDisplayIcon={app}\TailControl.Agent.exe
CloseApplications=no

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "get-magicdns.ps1"; Flags: dontcopy

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TailControlLockHelper"; ValueData: """{app}\TailControl.LockHelper.exe"""; Flags: uninsdeletevalue

[Code]
var
  HostPage: TInputQueryWizardPage;
  OptionsPage: TInputOptionWizardPage;

function ConfigDir(): String;
begin
  Result := ExpandConstant('{commonappdata}\TailControl\Agent\config');
end;

function AgentJson(): String;
begin
  Result := ConfigDir() + '\agent.json';
end;

function IsUpgrade(): Boolean;
begin
  Result := FileExists(AgentJson());
end;

function Run(const FileName, Params: String): Integer;
var
  Code: Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Log(Format('%s %s => %d', [FileName, Params, Code]));
  Result := Code;
end;

function Sys(const Exe: String): String;
begin
  Result := ExpandConstant('{sys}\') + Exe;
end;

function IsValidHost(const Host: String): Boolean;
var
  I: Integer;
  C: Char;
begin
  Result := (Length(Host) > 7) and (Copy(Host, Length(Host) - 6, 7) = '.ts.net');
  for I := 1 to Length(Host) do
  begin
    C := Host[I];
    if not (((C >= 'a') and (C <= 'z')) or ((C >= '0') and (C <= '9')) or (C = '.') or (C = '-')) then
      Result := False;
  end;
end;

procedure InitializeWizard();
var
  Detected: AnsiString;
begin
  HostPage := CreateInputQueryPage(wpWelcome,
    'Numele acestui PC in Tailscale',
    'Agentul accepta doar cereri adresate numelui MagicDNS al acestui PC.',
    'Numele complet MagicDNS (se termina in .ts.net). L-am completat din Tailscale, daca a fost gasit.');
  HostPage.Add('Nume MagicDNS:', False);

  OptionsPage := CreateInputOptionPage(HostPage.ID,
    'Optiuni', 'Cum va rula agentul.',
    'Le poti schimba mai tarziu in ' + AgentJson() + ' (apoi reporneste serviciul).', False, False);
  OptionsPage.Add('HTTPS cu certificat Tailscale (recomandat; HTTPS trebuie activat in consola Tailscale)');
  OptionsPage.Add('Mod test (DryRun): comenzile sunt doar logate, PC-ul nu se opreste');
  OptionsPage.Values[0] := True;
  OptionsPage.Values[1] := False;

  ExtractTemporaryFile('get-magicdns.ps1');
  Run(Sys('WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\get-magicdns.ps1') + '" -Out "' + ExpandConstant('{tmp}\magicdns.txt') + '"');
  if LoadStringFromFile(ExpandConstant('{tmp}\magicdns.txt'), Detected) then
    HostPage.Values[0] := Trim(String(Detected));
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // La actualizare pastram configurarea existenta.
  Result := IsUpgrade() and ((PageID = HostPage.ID) or (PageID = OptionsPage.ID));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = HostPage.ID then
  begin
    HostPage.Values[0] := Lowercase(Trim(HostPage.Values[0]));
    if not IsValidHost(HostPage.Values[0]) then
    begin
      MsgBox('Introdu numele MagicDNS complet, de exemplu pc.tailnet-1234.ts.net.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // Oprim serviciul si helper-ul ca fisierele sa poata fi inlocuite.
  Run(Sys('net.exe'), 'stop {#ServiceName}');
  Run(Sys('taskkill.exe'), '/F /IM TailControl.LockHelper.exe');
  Result := '';
end;

function JsonPath(const Path: String): String;
begin
  Result := Path;
  StringChangeEx(Result, '\', '\\', True);
end;

procedure WriteAgentJson(const Host: String; DryRun, WithCert: Boolean);
var
  Json, DryRunText: String;
begin
  if DryRun then DryRunText := 'true' else DryRunText := 'false';
  Json := '{' + #13#10 +
    '  "AllowedHosts": "' + Host + '",' + #13#10 +
    '  "Agent": {' + #13#10 +
    '    "DryRun": ' + DryRunText;
  if WithCert then
    Json := Json + ',' + #13#10 +
      '    "CertificatePath": "' + JsonPath(ConfigDir() + '\agent.crt') + '",' + #13#10 +
      '    "CertificateKeyPath": "' + JsonPath(ConfigDir() + '\agent.key') + '"';
  Json := Json + #13#10 + '  }' + #13#10 + '}' + #13#10;
  SaveStringToFile(AgentJson(), Json, False);
end;

procedure ConfigureService();
var
  Exe: String;
begin
  Exe := ExpandConstant('{app}\TailControl.Agent.exe');
  if Run(Sys('sc.exe'), 'query {#ServiceName}') <> 0 then
    Run(Sys('sc.exe'), 'create {#ServiceName} binPath= "\"' + Exe + '\"" start= delayed-auto obj= "NT AUTHORITY\LocalService" DisplayName= "TailControl Agent"')
  else
    Run(Sys('sc.exe'), 'config {#ServiceName} binPath= "\"' + Exe + '\"" start= delayed-auto obj= "NT AUTHORITY\LocalService"');

  Run(Sys('sc.exe'), 'description {#ServiceName} "TailControl: oprire, blocare si sleep la distanta, doar prin Tailscale."');
  Run(Sys('sc.exe'), 'failure {#ServiceName} reset= 86400 actions= restart/10000/restart/10000/restart/60000');
  // Doar privilegiile necesare (oprire/sleep); restul sunt eliminate din tokenul serviciului.
  Run(Sys('sc.exe'), 'privs {#ServiceName} SeShutdownPrivilege/SeChangeNotifyPrivilege/SeCreateGlobalPrivilege');
end;

procedure ConfigureFirewall();
begin
  Run(Sys('netsh.exe'), 'advfirewall firewall delete rule name="{#FirewallRule}"');
  Run(Sys('netsh.exe'), 'advfirewall firewall add rule name="{#FirewallRule}" dir=in action=allow protocol=TCP localport=8444 remoteip=100.64.0.0/10 profile=any program="' + ExpandConstant('{app}\TailControl.Agent.exe') + '"');
end;

function RenewCertificate(): Boolean;
begin
  Result := Run(Sys('WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\renew-cert.ps1') + '"') = 0;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  WizardForm.StatusLabel.Caption := 'Pregatesc folderele de date...';
  if Run(ExpandConstant('{app}\TailControl.Agent.exe'), 'prepare-directories') <> 0 then
    MsgBox('Folderele de date nu au putut fi pregatite. Vezi jurnalul de instalare.', mbError, MB_OK);

  if not IsUpgrade() then
  begin
    WriteAgentJson(HostPage.Values[0], OptionsPage.Values[1], False);
    if OptionsPage.Values[0] then
    begin
      WizardForm.StatusLabel.Caption := 'Obtin certificatul HTTPS (tailscale cert)...';
      if RenewCertificate() then
        WriteAgentJson(HostPage.Values[0], OptionsPage.Values[1], True)
      else
        MsgBox('Certificatul HTTPS nu a putut fi obtinut (este HTTPS activat in consola Tailscale?).' + #13#10 +
          'Agentul va folosi HTTP in tailnet. In aplicatie, dezactiveaza "Agentii folosesc HTTPS" sau reinstaleaza dupa ce activezi HTTPS.',
          mbInformation, MB_OK);
    end;
  end;

  WizardForm.StatusLabel.Caption := 'Configurez firewall-ul si serviciul...';
  ConfigureFirewall();
  ConfigureService();
  Run(Sys('schtasks.exe'), '/Create /F /TN "{#CertTask}" /SC WEEKLY /RU SYSTEM /RL HIGHEST /TR "' + Sys('WindowsPowerShell\v1.0\powershell.exe') + ' -NoProfile -ExecutionPolicy Bypass -File \"' + ExpandConstant('{app}\renew-cert.ps1') + '\""');

  if Run(Sys('net.exe'), 'start {#ServiceName}') <> 0 then
    MsgBox('Serviciul nu a pornit. Vezi Event Viewer > Windows Logs > Application (sursa TailControlAgent).', mbError, MB_OK);

  // Helper-ul de blocare ruleaza in sesiunea utilizatorului; il pornim acum, apoi porneste la fiecare logare.
  ExecAsOriginalUser(ExpandConstant('{app}\TailControl.LockHelper.exe'), '', '', SW_HIDE, ewNoWait, Code);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    Run(Sys('net.exe'), 'stop {#ServiceName}');
    Run(Sys('sc.exe'), 'delete {#ServiceName}');
    Run(Sys('taskkill.exe'), '/F /IM TailControl.LockHelper.exe');
    Run(Sys('netsh.exe'), 'advfirewall firewall delete rule name="{#FirewallRule}"');
    Run(Sys('schtasks.exe'), '/Delete /F /TN "{#CertTask}"');
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox('Stergi si datele agentului (clienti autorizati, certificat, jurnal de audit) din ' +
      ExpandConstant('{commonappdata}\TailControl\Agent') + '?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{commonappdata}\TailControl\Agent'), True, True, True);
  end;
end;
