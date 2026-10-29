; ============================================================================
;  AICompanion.iss - Inno Setup 6 安装脚本
;
;  一般不用手写参数，直接跑 tools\pack.ps1 即可（它会带上全部 define）：
;    /DMyVersion=0.9.3
;    /DStageDir=<分发目录>
;    /DSelfContained=true|false     true 时不检查系统 .NET（运行时已内置）
;    /DIncludeModels=true|false      true 时把 models\ 一起装进去
;    /DOutSuffix=full|slim          安装包文件名后缀
;
;  设计取舍：
;   - 默认按用户安装到 %LOCALAPPDATA%\Programs\AICompanion，不需要管理员。
;     程序运行期要写 logs 与 Assets\appearance.json，装到 Program Files 会因无写权限失败。
;   - 自带运行时的包不再检查 .NET；依赖系统的包缺失时明确提示而不是让程序闪退。
;   - 模型体积大（1.2 GB），由 IncludeModels 决定是否打进安装包。
; ============================================================================

#ifndef MyVersion
  #define MyVersion "0.9.3"
#endif

#ifndef StageDir
  #define StageDir "..\dist\AICompanion-0.9.3-win-x64"
#endif

#ifndef SelfContained
  #define SelfContained "true"
#endif

#ifndef IncludeModels
  #define IncludeModels "true"
#endif

#ifndef IslPath
  #define IslPath "ChineseSimplified.isl"
#endif

#ifndef OutSuffix
  #define OutSuffix "full"
#endif

#define AppName "AI 桌面伴侣"
#define AppExe "AICompanion.exe"
#define AppPublisher "AICompanion"

[Setup]
AppId={{8E4C1F42-5B7A-4D3E-9C21-7A6F0B1D5E93}
AppName={#AppName}
AppVersion={#MyVersion}
AppVerName={#AppName} {#MyVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\AICompanion
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=AICompanion-{#MyVersion}-{#OutSuffix}-setup
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\Assets\app.ico
; 大包（>1 GB）需要允许较大的磁盘占用提示
DiskSpanning=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "{#IslPath}"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"
Name: "startup"; Description: "开机自动启动"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
#if IncludeModels == "true"
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#else
Source: "{#StageDir}\*"; DestDir: "{app}"; Excludes: "models\*"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[Dirs]
Name: "{app}\models"; Permissions: users-modify
Name: "{app}\models\llm"; Permissions: users-modify
Name: "{app}\models\asr"; Permissions: users-modify

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "AICompanion"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\logs"

[Code]
// ---------------------------------------------------------------------------
// .NET 8 桌面运行时检查（仅"依赖系统 .NET"的包需要）
// ---------------------------------------------------------------------------
function IsDotNet8DesktopInstalled(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;

  if RegGetSubkeyNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if Pos('8.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;

#if SelfContained == "true"
  // 运行时已内置，无需检查
#else
  if not IsDotNet8DesktopInstalled() then
  begin
    if MsgBox('未检测到 .NET 8 桌面运行时（Microsoft.WindowsDesktop.App 8.x）。' + #13#10 +
              '本安装包未内置运行时，程序需要它才能启动，是否仍然继续安装？' + #13#10#13#10 +
              '下载地址：https://dotnet.microsoft.com/download/dotnet/8.0',
              mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
#endif
end;