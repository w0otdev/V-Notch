; Exercise the bootstrapper's actual detector with private folder and registry fixtures.
Name "Desktop Runtime detection test"
OutFile "${TEST_EXE}"
SilentInstall silent
RequestExecutionLevel user
!include "..\Setup\DesktopRuntime.nsh"
!define TEST_KEY "Software\VNotch-Detector-Tests\${TEST_ID}"
!insertmacro DefineDesktopRuntimeDetector "$PLUGINSDIR" HKCU "${TEST_KEY}"

Section
    InitPluginsDir
    StrCpy $0 1
    Call FindDesktopRuntime
    StrCmp $R2 0 +2
    Goto done
    WriteRegDWORD HKCU "${TEST_KEY}" "8.0.29" 1
    WriteRegDWORD HKCU "${TEST_KEY}" "10.0.16-preview.1" 1
    StrCpy $0 2
    Call FindDesktopRuntime
    StrCmp $R2 0 +2
    Goto done
    SetRegView 64
    WriteRegDWORD HKCU "${TEST_KEY}" "10.0.15" 1
    StrCpy $0 3
    Call FindDesktopRuntime
    StrCmp $R2 1 +2
    Goto done
    SetRegView 64
    DeleteRegKey HKCU "${TEST_KEY}"
    SetRegView 32
    WriteRegDWORD HKCU "${TEST_KEY}" "10.0.1" 1
    StrCpy $0 4
    Call FindDesktopRuntime
    StrCmp $R2 1 +2
    Goto done
    SetRegView 32
    DeleteRegKey HKCU "${TEST_KEY}"
    CreateDirectory "$PLUGINSDIR\10.0.15"
    StrCpy $0 5
    Call FindDesktopRuntime
    StrCmp $R2 0 +2
    Goto done
    FileOpen $1 "$PLUGINSDIR\10.0.15\PresentationFramework.dll" w
    FileClose $1
    StrCpy $0 6
    Call FindDesktopRuntime
    StrCmp $R2 1 +2
    Goto done
    StrCpy $0 0
    done:
    SetRegView 64
    DeleteRegKey HKCU "${TEST_KEY}"
    SetRegView 32
    DeleteRegKey HKCU "${TEST_KEY}"
    DeleteRegKey /ifempty HKCU "Software\VNotch-Detector-Tests"
    SetErrorLevel $0
SectionEnd
