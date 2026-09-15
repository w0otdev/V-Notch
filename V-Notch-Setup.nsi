; V-Notch-Setup.nsi
; Bootstrapper for the WPF-based V-Notch setup experience
; AV-friendly: includes version info, visible UI, user-level install
; Framework-dependent: checks/installs .NET 10 Desktop Runtime

!define APP_NAME "V-Notch"
!define APP_PUBLISHER "rainaku"
!define APP_EXE "V-Notch.exe"
!define APP_GUID "{B2567AF4-8BAE-46D2-B83E-56539C82F888}"
!define APP_BUILD_DIR "release"
!define APP_URL "https://github.com/rainaku/V-Notch"

; .NET 10 Desktop Runtime requirement
!define DOTNET_VERSION "10.0"
!define DOTNET_INSTALLER_URL "https://download.visualstudio.microsoft.com/download/pr/dotnet-runtime-10.0-win-x64.exe"
!define DOTNET_INSTALLER_ARGS "/install /quiet /norestart"

; Passed from V-Notch.csproj by build-installer.ps1.
!ifndef APP_VERSION_FULL
  !error "APP_VERSION_FULL must be supplied by build-installer.ps1"
!endif
!define APP_VERSION "${APP_VERSION_FULL}"

!include "LogicLib.nsh"
!include "x64.nsh"

; ── Version Info (critical for AV trust) ───────────────────
VIProductVersion "${APP_VERSION_FULL}"
VIFileVersion "${APP_VERSION_FULL}"
VIAddVersionKey "ProductName"      "${APP_NAME}"
VIAddVersionKey "CompanyName"      "${APP_PUBLISHER}"
VIAddVersionKey "LegalCopyright"   "Copyright © 2026 ${APP_PUBLISHER}"
VIAddVersionKey "FileDescription"  "${APP_NAME} Installer"
VIAddVersionKey "FileVersion"      "${APP_VERSION_FULL}"
VIAddVersionKey "ProductVersion"   "${APP_VERSION_FULL}"
VIAddVersionKey "OriginalFilename" "V-Notch-Setup.exe"
VIAddVersionKey "InternalName"     "V-Notch-Setup"

Name "${APP_NAME} ${APP_VERSION}"
OutFile "installers\V-Notch-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
RequestExecutionLevel user
SetCompressor /SOLID lzma

; Hide the NSIS window — the WPF setup wizard is the real UI
SilentInstall silent
ShowInstDetails nevershow
AutoCloseWindow true

Icon "Services\icons\logo.ico"

Section "Install"
    ; Extract the real app payload to a temp staging folder first. During auto-update,
    ; the installed V-Notch.exe is still running and locked, so writing directly to
    ; $INSTDIR here can make the silent bootstrapper fail before the WPF setup opens.
    InitPluginsDir
    SetOutPath "$PLUGINSDIR\payload"
    File /r "${APP_BUILD_DIR}\*"

    ; Check and install .NET 10 Desktop Runtime if needed
    !ifndef SELF_CONTAINED
        Call CheckAndInstallDotNet
    !endif

    ; Launch the WPF setup experience from the staged payload. The WPF setup is
    ; responsible for closing running instances and copying files into $INSTDIR.
    ExecWait '"$PLUGINSDIR\payload\${APP_EXE}" --setup --setup-source "$PLUGINSDIR\payload" --installer-path "$EXEPATH"' $0
    SetErrorLevel $0
SectionEnd

Function .onInit
    ; Clean up older Inno Setup installs before the new WPF bootstrapper runs
    ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_GUID}_is1" "UninstallString"
    StrCmp $0 "" check_hklm
    Goto uninstall_inno

    check_hklm:
    ReadRegStr $0 HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_GUID}_is1" "UninstallString"
    StrCmp $0 "" done_check

    uninstall_inno:
    ExecWait '$0 /VERYSILENT /SUPPRESSMSGBOXES /NORESTART'

    done_check:
FunctionEnd

; ── .NET 10 Desktop Runtime Detection & Installation ────────
Function CheckAndInstallDotNet
    ; 1) Ask the .NET host first. Avoid /C:"... ..." quoting because NSIS/cmd can
    ; strip nested quotes; two findstr stages are more reliable here.
    nsExec::ExecToStack 'cmd.exe /c "dotnet --list-runtimes 2>nul | findstr /I Microsoft.WindowsDesktop.App | findstr /R 10\."'
    Pop $0 ; return code
    Pop $1 ; first matching output line
    StrCmp $0 "0" dotnet_found

    ; 2) Check actual Desktop Runtime folders. This catches repaired installs even if
    ; registry metadata is incomplete.
    ${If} ${RunningX64}
        IfFileExists "$PROGRAMFILES64\dotnet\shared\Microsoft.WindowsDesktop.App\10.*\*" dotnet_found
    ${EndIf}
    IfFileExists "$PROGRAMFILES\dotnet\shared\Microsoft.WindowsDesktop.App\10.*\*" dotnet_found

    ; 3) Enumerate x64 registry values. Installed values are exact patch versions
    ; such as 10.0.1, so do not hardcode 10.0.0.
    ${If} ${RunningX64}
        SetRegView 64
    ${EndIf}
    StrCpy $R0 0

    reg_loop_64:
    EnumRegValue $R1 HKLM "SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App" $R0
    StrCmp $R1 "" reg_check_default
    StrCpy $R2 $R1 3
    StrCmp $R2 "10." dotnet_found
    IntOp $R0 $R0 + 1
    Goto reg_loop_64

    reg_check_default:
    SetRegView Default
    StrCpy $R0 0

    reg_loop_default:
    EnumRegValue $R1 HKLM "SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App" $R0
    StrCmp $R1 "" dotnet_install
    StrCpy $R2 $R1 3
    StrCmp $R2 "10." dotnet_found
    IntOp $R0 $R0 + 1
    Goto reg_loop_default

    dotnet_install:
    ${If} ${RunningX64}
        SetRegView Default
    ${EndIf}

    MessageBox MB_YESNO|MB_ICONQUESTION \
        "${APP_NAME} requires .NET 10 Desktop Runtime.$\n$\nWould you like to download and install it now?" \
        IDYES do_install

    MessageBox MB_OK|MB_ICONEXCLAMATION \
        "${APP_NAME} cannot run without .NET 10 Desktop Runtime.$\nPlease install it manually from:$\nhttps://dotnet.microsoft.com/download/dotnet/10.0"
    Abort

    do_install:
    InitPluginsDir
    StrCpy $R3 "$PLUGINSDIR\windowsdesktop-runtime-10-win-x64.exe"

    DetailPrint "Downloading .NET 10 Desktop Runtime..."
    nsExec::ExecToStack 'powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -UseBasicParsing -Uri ''https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe'' -OutFile ''$R3''; exit 0 } catch { exit 1 }"'
    Pop $0
    StrCmp $0 "0" download_ok

    ; Fallback to curl.exe on Windows 10/11 when PowerShell download is blocked.
    nsExec::ExecToStack 'curl.exe -L --fail -o $R3 https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe'
    Pop $0
    StrCmp $0 "0" download_ok

    MessageBox MB_OK|MB_ICONEXCLAMATION \
        "Failed to download .NET 10 Desktop Runtime.$\nPlease install it manually from:$\nhttps://dotnet.microsoft.com/download/dotnet/10.0"
    ExecShell "open" "https://dotnet.microsoft.com/download/dotnet/10.0"
    Abort

    download_ok:
    DetailPrint "Installing .NET 10 Desktop Runtime..."
    ExecWait '"$R3" /install /quiet /norestart' $0
    Delete "$R3"

    ${If} $0 == 0
    ${OrIf} $0 == 3010
        Goto dotnet_found
    ${EndIf}

    MessageBox MB_OK|MB_ICONEXCLAMATION \
        ".NET 10 Desktop Runtime installation failed (code: $0).$\nPlease install it manually from:$\nhttps://dotnet.microsoft.com/download/dotnet/10.0"
    ExecShell "open" "https://dotnet.microsoft.com/download/dotnet/10.0"
    Abort

    dotnet_found:
    ${If} ${RunningX64}
        SetRegView Default
    ${EndIf}
FunctionEnd
