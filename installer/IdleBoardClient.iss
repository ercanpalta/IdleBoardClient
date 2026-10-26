; =====================================================================
; IdleBoard Client - Profesyonel Windows Kurulum Sihirbazi (Inno Setup)
; =====================================================================

#define MyAppName "IdleBoard Client"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "IdleBoard"
#define MyAppExeName "IdleBoardClient.exe"

[Setup]
AppId={{5D8A382A-C1A4-4D46-9DFA-E5DC7D6BC471}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\IdleBoard\Client
DefaultGroupName=IdleBoard
DisableProgramGroupPage=yes
OutputBaseFilename=IdleBoardClient_Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequired=admin
OutputDir=..\installer_output
UninstallDisplayIcon={app}\{#MyAppExeName}

; Windows Dosya ve Versiyon Bilgileri
VersionInfoCompany=IdleBoard
VersionInfoDescription=IdleBoard Client Kurulumu
VersionInfoVersion=1.0.0.0
VersionInfoCopyright=Copyright 2026 Ercan Palta
VersionInfoProductName=IdleBoard Client

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Sadece masaustu kisayolu secenegi sunulur ve varsayilan olarak SECILI DEGILDIR (isteyen acar)
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "scripts\discover.ps1"; DestDir: "{tmp}"; Flags: dontcopy

[Dirs]
Name: "{app}\cache"; Permissions: users-full
Name: "{app}\logs"; Permissions: users-full

[Icons]
; Baslat menusu kisayolu varsayilan olarak olusturulur
Name: "{autoprograms}\IdleBoard\IdleBoard Client"; Filename: "{app}\{#MyAppExeName}"
; Masaustu kisayolu sadece kullanici sectiyse olusturulur
Name: "{autodesktop}\IdleBoard Client"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Windows ile otomatik baslatma (ZORUNLU - Kurulumda secenek gosterilmez, dogrudan eklenir)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "IdleBoardClient"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue

[Run]
; Windows Guvenlik Duvari gelen izin kurali ekleme
Filename: "netsh.exe"; Parameters: "advfirewall firewall add rule name=""IdleBoard Client"" dir=in action=allow program=""{app}\{#MyAppExeName}"" enable=yes"; Flags: runhidden
; Kurulum tamamlandiginda calistirma secenegi
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Kaldirma esnasinda Guvenlik Duvari kuralini silme
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""IdleBoard Client"""; Flags: runhidden

[UninstallDelete]
; Uygulama kaldirildiginda onbellek klasorunu, loglari ve konfigurasyonu temizle
Type: filesandordirs; Name: "{app}\cache"
Type: filesandordirs; Name: "{app}\logs"
Type: files; Name: "{app}\client-config.json"
Type: dirifempty; Name: "{app}"

[Code]
var
  ServerPage: TInputQueryWizardPage;
  ServerDiscovered: Boolean;
  DiscoveredUrl: String;

function DiscoverServerOnNetwork(): Boolean;
var
  ScriptPath: String;
  OutputPath: String;
  ResultCode: Integer;
  UrlContent: AnsiString;
begin
  Result := False;
  try
    ExtractTemporaryFile('discover.ps1');
    ScriptPath := ExpandConstant('{tmp}\discover.ps1');
    OutputPath := ExpandConstant('{tmp}\discovered_server.txt');

    if FileExists(OutputPath) then
      DeleteFile(OutputPath);

    // PowerShell uzerinden UDP kesif betigini sessizce calistir (maksimum 1.5 sn)
    if Exec('powershell.exe', '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ScriptPath + '" -OutputFile "' + OutputPath + '" -TimeoutMs 1500', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      if (ResultCode = 0) and FileExists(OutputPath) then
      begin
        if LoadStringFromFile(OutputPath, UrlContent) then
        begin
          DiscoveredUrl := Trim(String(UrlContent));
          if (DiscoveredUrl <> '') and (Pos('http', DiscoveredUrl) = 1) then
          begin
            Result := True;
          end;
        end;
      end;
    end;
  except
    Result := False;
  end;
end;

procedure InitializeWizard;
begin
  ServerDiscovered := False;
  DiscoveredUrl := '';

  // Manuel Giris Sayfasi (Yalnizca agda sunucu otomatik bulunamazsa acilir)
  ServerPage := CreateInputQueryPage(wpSelectDir,
    'Sunucu Baglanti Ayari',
    'Agda otomatik IdleBoard sunucusu bulunamadi.',
    'Yerel agda aktif bir IdleBoard sunucusu otomatik olarak tespit edilemedi.' + #13#10 +
    'Lutfen sunucunun calistigi bilgisayarin IP adresini ve portunu giriniz:' + #13#10 +
    '(Daha sonra uygulama acildiginda Ctrl + Shift + S tuslariyla da degistirebilirsiniz)');

  ServerPage.Add('Sunucu Adresi:', False);
  ServerPage.Values[0] := 'http://192.168.1.X:3000/events';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  // Dizin secim sayfasinda 'Ileri' butonuna basildiginda otomatik ag taramasi yap
  if CurPageID = wpSelectDir then
  begin
    if not ServerDiscovered then
    begin
      if DiscoverServerOnNetwork() then
      begin
        ServerDiscovered := True;
        ServerPage.Values[0] := DiscoveredUrl;
      end;
    end;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;

  // Eger sunucu agda otomatik olarak bulunduysa manuel giris sayfasini ATLA
  // Eger bulunamadiysa (ServerDiscovered = False) bu sayfa ACILIR
  if PageID = ServerPage.ID then
  begin
    Result := ServerDiscovered;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ServerUrlInput: String;
  ConfigContent: String;
  ConfigPath: String;
begin
  if CurStep = ssPostInstall then
  begin
    ServerUrlInput := Trim(ServerPage.Values[0]);
    if (ServerUrlInput <> '') and (Pos('192.168.1.X', ServerUrlInput) = 0) and (Pos('ServerIp', ServerUrlInput) = 0) then
    begin
      ConfigContent := '{"ServerUrl": "' + ServerUrlInput + '"}';
      ConfigPath := ExpandConstant('{app}\client-config.json');
      SaveStringToFile(ConfigPath, ConfigContent, False);

      RegWriteStringValue(HKEY_CURRENT_USER, 'Software\IdleBoardClient', 'ServerUrl', ServerUrlInput);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Onbellek dizini ve loglari temizle
    DelTree(ExpandConstant('{app}\cache'), True, True, True);
    DelTree(ExpandConstant('{app}\logs'), True, True, True);
    DeleteFile(ExpandConstant('{app}\client-config.json'));
    // Kayit defterini temizle
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\IdleBoardClient');
  end;
end;
