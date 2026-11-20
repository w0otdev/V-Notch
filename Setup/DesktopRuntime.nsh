; A framework-dependent x64 apphost needs a stable patch of the target major/minor.
!define DOTNET_VERSION "10.0"
; Input: version name in $R1. Output: 1 (match) or 0 in $R2.
Function MatchesDesktopRuntime
    StrLen $R4 "${DOTNET_VERSION}."
    StrCpy $R2 $R1 $R4
    StrCmp $R2 "${DOTNET_VERSION}." 0 mismatch
    StrCpy $R3 $R1 "" $R4
    IntFmt $R2 "%u" $R3
    StrCmp $R2 $R3 0 mismatch
    StrCpy $R2 1
    Return
    mismatch:
    StrCpy $R2 0
FunctionEnd

!macro ScanDesktopRegistry view hive key
    SetRegView ${view}
    StrCpy $R0 0
    registry_loop_${view}:
        EnumRegValue $R1 ${hive} "${key}" $R0
        StrCmp $R1 "" registry_done_${view}
        Call MatchesDesktopRuntime
        StrCmp $R2 1 registry_found
        IntOp $R0 $R0 + 1
        Goto registry_loop_${view}
    registry_done_${view}:
!macroend

!macro DefineDesktopRuntimeDetector folder hive key
Function FindDesktopRuntime
    FindFirst $R0 $R1 "${folder}\${DOTNET_VERSION}.*"
    folder_loop:
        StrCmp $R1 "" folder_done
        Call MatchesDesktopRuntime
        StrCmp $R2 1 0 next_folder
        IfFileExists "${folder}\$R1\PresentationFramework.dll" folder_found
        next_folder:
        FindNext $R0 $R1
        Goto folder_loop
    folder_found:
        FindClose $R0
        Return
    folder_done:
        FindClose $R0

    ; Registered x64 runtimes can also live outside Program Files.
    !insertmacro ScanDesktopRegistry 64 ${hive} "${key}"
    !insertmacro ScanDesktopRegistry 32 ${hive} "${key}"
    StrCpy $R2 0
    registry_found:
    SetRegView Default
FunctionEnd
!macroend
