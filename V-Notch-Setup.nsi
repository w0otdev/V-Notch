; V-Notch-Setup.nsi
; Bootstrapper for the WPF-based V-Notch setup experience
; AV-friendly: includes version info, visible UI, user-level install
; Framework-dependent: checks/installs .NET 10 Desktop Runtime

!define APP_NAME "V-Notch"
!define APP_PUBLISHER "w0otdev"
!define APP_EXE "V-Notch.exe"
!define APP_BUILD_DIR "release"
!define APP_URL "https://github.com/w0otdev/V-Notch"

; Match the application's net10.0 target. Digest comes from Microsoft's 10.0 release metadata.
!define DOTNET_INSTALLER_URL "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.15/windowsdesktop-runtime-10.0.15-win-x64.exe"
!define DOTNET_INSTALLER_SHA512 "f838960962172f1a769c49fa600e6f60f56f8d08a28e3cb878890ffc2e3a7a2e1b65697588e23efa767b8b8e8523ec01f55e875120507dde16d9a0869102dab3"
!define DOTNET_INSTALLER_ARGS "/install /quiet /norestart"

; Passed from V-Notch.csproj by build-installer.ps1.
!ifndef APP_VERSION_FULL
  !error "APP_VERSION_FULL must be supplied by build-installer.ps1"
!endif
!ifndef APP_VERSION
  !define APP_VERSION "${APP_VERSION_FULL}"
!endif

!include "LogicLib.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"

Var UpdateArguments
Var WizardRunning

; ── Version Info (critical for AV trust) ───────────────────
VIProductVersion "${APP_VERSION_FULL}"
VIFileVersion "${APP_VERSION_FULL}"
VIAddVersionKey "ProductName"      "${APP_NAME}"
VIAddVersionKey "CompanyName"      "${APP_PUBLISHER}"
VIAddVersionKey "LegalCopyright"   "Copyright © 2026 ${APP_PUBLISHER}"
VIAddVersionKey "FileDescription"  "${APP_NAME} Installer"
VIAddVersionKey "FileVersion"      "${APP_VERSION_FULL}"
VIAddVersionKey "ProductVersion"   "${APP_VERSION}"
VIAddVersionKey "OriginalFilename" "V-Notch-Setup.exe"
VIAddVersionKey "InternalName"     "V-Notch-Setup"

Name "${APP_NAME} ${APP_VERSION}"
OutFile "installers\V-Notch-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
RequestExecutionLevel user
!ifdef PACKED_PAYLOAD
    SetCompressor zlib
!else
    SetCompressor /SOLID lzma
    SetCompressorDictSize 96
!endif

; Keep extraction visible: a large payload can take several seconds before the
; WPF wizard starts, especially during updates or on slower disks/AV scanners.
SilentInstall normal
Page instfiles
ShowInstDetails nevershow
AutoCloseWindow true

Icon "Services\icons\logo.ico"

Function .onUserAbort
    ; Extraction/runtime failures must remain dismissible. Protect staging only
    ; while the WPF wizard is actually using it; that wizard owns cancellation.
    ${If} $WizardRunning == 1
        Abort
    ${EndIf}
FunctionEnd

Section "Install"
    ; Extract the real app payload to a temp staging folder first. During auto-update,
    ; the installed V-Notch.exe is still running and locked, so writing directly to
    ; $INSTDIR here can make the silent bootstrapper fail before the WPF setup opens.
    InitPluginsDir
    !ifdef PACKED_PAYLOAD
    SetOutPath "$PLUGINSDIR"
    SetCompress off
    File /oname=extract-payload.exe "${PACKED_PAYLOAD}"
    SetCompress auto
    DetailPrint "Extracting V-Notch..."
    nsExec::Exec '"$PLUGINSDIR\extract-payload.exe" -y "-o$PLUGINSDIR\payload"'
    Pop $0
    Delete "$PLUGINSDIR\extract-payload.exe"
    ${If} $0 != "0"
        SetErrorLevel 1
        MessageBox MB_OK|MB_ICONSTOP "Could not extract the V-Notch installation files."
        Abort
    ${EndIf}
    SetOutPath "$PLUGINSDIR\payload"
    !else
    SetOutPath "$PLUGINSDIR\payload"
    ; Keep the shared single-file host/runtime within the compression window:
    ; the small uninstaller comes before the larger application bundle.
    File "${APP_BUILD_DIR}\uninstall.exe"
    File "${APP_BUILD_DIR}\${APP_EXE}"
    File /r /x "uninstall.exe" /x "${APP_EXE}" "${APP_BUILD_DIR}\*"
    !endif

    ; Check and install .NET 10 Desktop Runtime if needed
    !ifndef SELF_CONTAINED
        Call CheckAndInstallDotNet
    !endif

    ; Launch the WPF setup experience from the staged payload. The WPF setup is
    ; responsible for closing running instances and copying files into $INSTDIR.
    ; Forward only the explicit update switch, never arbitrary bootstrapper arguments.
    StrCpy $UpdateArguments ""
    ${GetParameters} $R0
    ClearErrors
    ${GetOptions} $R0 "--update" $R1
    ${Unless} ${Errors}
        StrCpy $UpdateArguments "--update"
    ${EndUnless}
    StrCpy $WizardRunning 1
    GetDlgItem $2 $HWNDPARENT 2
    EnableWindow $2 0
    ExecWait '"$PLUGINSDIR\payload\${APP_EXE}" --setup --setup-source "$PLUGINSDIR\payload" --installer-path "$EXEPATH" $UpdateArguments' $0
    StrCpy $WizardRunning 0
    EnableWindow $2 1
    SetErrorLevel $0
SectionEnd

; Legacy uninstall commands are registry data and must never execute in this bootstrapper.
; Users can remove old Inno installations through Windows Settings.

; ── .NET 10 Desktop Runtime Detection & Installation ────────
!ifndef SELF_CONTAINED
!include "Setup\DesktopRuntime.nsh"
!insertmacro DefineDesktopRuntimeDetector "$PROGRAMFILES64\dotnet\shared\Microsoft.WindowsDesktop.App" HKLM "SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"
Function CheckAndInstallDotNet
    Call FindDesktopRuntime
    StrCmp $R2 1 dotnet_found

    MessageBox MB_YESNO|MB_ICONQUESTION \
        "${APP_NAME} requires .NET 10 Desktop Runtime.$\n$\nWould you like to download and install it now?" \
        IDYES do_install

    MessageBox MB_OK|MB_ICONEXCLAMATION \
        "${APP_NAME} cannot run without .NET 10 Desktop Runtime.$\nPlease install it manually from:$\nhttps://dotnet.microsoft.com/download/dotnet/10.0"
    Abort

    do_install:
    InitPluginsDir
    StrCpy $R3 "$PLUGINSDIR\windowsdesktop-runtime-10-win-x64.exe"
    File /oname=$PLUGINSDIR\verify-dotnet-runtime.ps1 "Setup\Verify-DotNetRuntime.ps1"

    DetailPrint "Downloading .NET 10 Desktop Runtime..."
    ; Use the OS binary directly; never search the current directory or PATH.
    nsExec::ExecToStack '"$SYSDIR\curl.exe" --location --fail --proto "=https" --proto-redir "=https" --output "$R3" "${DOTNET_INSTALLER_URL}"'
    Pop $0
    Pop $1
    StrCmp $0 "0" download_ok

    MessageBox MB_OK|MB_ICONEXCLAMATION \
        "Failed to download .NET 10 Desktop Runtime.$\nPlease install it manually from:$\nhttps://dotnet.microsoft.com/download/dotnet/10.0"
    ExecShell "open" "https://dotnet.microsoft.com/download/dotnet/10.0"
    Abort

    download_ok:
    nsExec::ExecToStack '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\verify-dotnet-runtime.ps1" -InstallerPath "$R3" -ExpectedSha512 "${DOTNET_INSTALLER_SHA512}"'
    Pop $0
    Pop $1
    StrCmp $0 "0" runtime_verified
    Delete "$R3"
    MessageBox MB_OK|MB_ICONSTOP "Runtime verification failed. No downloaded executable was run. Please install .NET 10 Desktop Runtime from dotnet.microsoft.com."
    Abort

    runtime_verified:
    DetailPrint "Installing verified .NET 10 Desktop Runtime..."
    ExecWait '"$R3" ${DOTNET_INSTALLER_ARGS}' $0
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
!endif
