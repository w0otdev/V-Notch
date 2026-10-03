; V-Notch-Setup.nsi
; Bootstrapper for the WPF-based V-Notch setup experience
; AV-friendly: includes version info, visible UI, user-level install
; Framework-dependent: checks/installs .NET 8 Desktop Runtime

!define APP_NAME "V-Notch"
!define APP_PUBLISHER "rainaku"
!define APP_EXE "V-Notch.exe"
!define APP_BUILD_DIR "release"
!define APP_URL "https://github.com/rainaku/V-Notch"

; Match the application's net8.0 target. Digest comes from Microsoft's 8.0 release metadata.
!define DOTNET_VERSION "8.0"
!define DOTNET_INSTALLER_URL "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.29/windowsdesktop-runtime-8.0.29-win-x64.exe"
!define DOTNET_INSTALLER_SHA512 "02d272ee678f5bc8be522b0b8adaf2a3c9d35d1044d7737ea48e477928a839ad5344724b242b210afdc64775d8cf43655db58396fcf53dd185e23ba2b888ec44"
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

    ; Check and install .NET 8 Desktop Runtime if needed
    !ifndef SELF_CONTAINED
        Call CheckAndInstallDotNet
    !endif

    ; Launch the WPF setup experience from the staged payload. The WPF setup is
    ; responsible for closing running instances and copying files into $INSTDIR.
    ExecWait '"$PLUGINSDIR\payload\${APP_EXE}" --setup --setup-source "$PLUGINSDIR\payload" --installer-path "$EXEPATH"' $0
    SetErrorLevel $0
SectionEnd

; Legacy uninstall commands are registry data and must never execute in this bootstrapper.
; Users can remove old Inno installations through Windows Settings.

; ── .NET 8 Desktop Runtime Detection & Installation ────────
Function CheckAndInstallDotNet
    ; Check actual Desktop Runtime folders. This catches repaired installs even if
    ; registry metadata is incomplete.
    ${If} ${RunningX64}
        IfFileExists "$PROGRAMFILES64\dotnet\shared\Microsoft.WindowsDesktop.App\${DOTNET_VERSION}.*\*" dotnet_found
    ${EndIf}
    IfFileExists "$PROGRAMFILES\dotnet\shared\Microsoft.WindowsDesktop.App\${DOTNET_VERSION}.*\*" dotnet_found

    ; Enumerate x64 registry values. Installed values are exact patch versions
    ; such as 8.0.29, so do not hardcode a single patch version.
    ${If} ${RunningX64}
        SetRegView 64
    ${EndIf}
    StrCpy $R0 0

    reg_loop_64:
    EnumRegValue $R1 HKLM "SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App" $R0
    StrCmp $R1 "" reg_check_default
    StrCpy $R2 $R1 4
    StrCmp $R2 "${DOTNET_VERSION}." dotnet_found
    IntOp $R0 $R0 + 1
    Goto reg_loop_64

    reg_check_default:
    SetRegView Default
    StrCpy $R0 0

    reg_loop_default:
    EnumRegValue $R1 HKLM "SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App" $R0
    StrCmp $R1 "" dotnet_install
    StrCpy $R2 $R1 4
    StrCmp $R2 "${DOTNET_VERSION}." dotnet_found
    IntOp $R0 $R0 + 1
    Goto reg_loop_default

    dotnet_install:
    ${If} ${RunningX64}
        SetRegView Default
    ${EndIf}

    MessageBox MB_YESNO|MB_ICONQUESTION \
        "${APP_NAME} requires .NET 8 Desktop Runtime.$\n$\nWould you like to download and install it now?" \
        IDYES do_install

    MessageBox MB_OK|MB_ICONEXCLAMATION \
        "${APP_NAME} cannot run without .NET 8 Desktop Runtime.$\nPlease install it manually from:$\nhttps://dotnet.microsoft.com/download/dotnet/8.0"
    Abort

    do_install:
    InitPluginsDir
    StrCpy $R3 "$PLUGINSDIR\windowsdesktop-runtime-8-win-x64.exe"
    File /oname=$PLUGINSDIR\verify-dotnet-runtime.ps1 "Setup\Verify-DotNetRuntime.ps1"

    DetailPrint "Downloading .NET 8 Desktop Runtime..."
    ; Use the OS binary directly; never search the current directory or PATH.
    nsExec::ExecToStack '"$SYSDIR\curl.exe" --location --fail --proto "=https" --proto-redir "=https" --output "$R3" "${DOTNET_INSTALLER_URL}"'
    Pop $0
    Pop $1
    StrCmp $0 "0" download_ok

    MessageBox MB_OK|MB_ICONEXCLAMATION \
        "Failed to download .NET 8 Desktop Runtime.$\nPlease install it manually from:$\nhttps://dotnet.microsoft.com/download/dotnet/8.0"
    ExecShell "open" "https://dotnet.microsoft.com/download/dotnet/8.0"
    Abort

    download_ok:
    nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\verify-dotnet-runtime.ps1" -InstallerPath "$R3" -ExpectedSha512 "${DOTNET_INSTALLER_SHA512}"'
    Pop $0
    Pop $1
    StrCmp $0 "0" runtime_verified
    Delete "$R3"
    MessageBox MB_OK|MB_ICONSTOP "Runtime verification failed. No downloaded executable was run. Please install .NET 8 Desktop Runtime from dotnet.microsoft.com."
    Abort

    runtime_verified:
    DetailPrint "Installing verified .NET 8 Desktop Runtime..."
    ExecWait '"$R3" ${DOTNET_INSTALLER_ARGS}' $0
    Delete "$R3"

    ${If} $0 == 0
    ${OrIf} $0 == 3010
        Goto dotnet_found
    ${EndIf}

    MessageBox MB_OK|MB_ICONEXCLAMATION \
        ".NET 8 Desktop Runtime installation failed (code: $0).$\nPlease install it manually from:$\nhttps://dotnet.microsoft.com/download/dotnet/8.0"
    ExecShell "open" "https://dotnet.microsoft.com/download/dotnet/8.0"
    Abort

    dotnet_found:
    ${If} ${RunningX64}
        SetRegView Default
    ${EndIf}
FunctionEnd
