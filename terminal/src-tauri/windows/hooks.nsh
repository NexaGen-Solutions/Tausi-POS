; Tausi POS terminal installer hooks (NSIS, included by Tauri's installer template).
;
; - Installs into C:\Program Files\Tausi POS\Terminal\ (the server lives next to it in ...\Server\).
; - Starts the app for every Windows user at sign-in (the machine's Run key).
; - TausiPOS-Setup.exe /S SERVER=http://192.168.1.10:5080 pre-fills the server address: it is written
;   to C:\ProgramData\Tausi POS\Terminal\server.txt, which the app reads on its first start.

!macro NSIS_HOOK_PREINSTALL
  ; The default folder is "Tausi POS"; the terminal has its own subfolder. A folder chosen by hand,
  ; or the one an earlier install used, is kept.
  ${If} $INSTDIR == "$PROGRAMFILES64\${PRODUCTNAME}"
    StrCpy $INSTDIR "$INSTDIR\Terminal"
    SetOutPath $INSTDIR
  ${EndIf}
!macroend

!macro NSIS_HOOK_POSTINSTALL
  ; Every user: start the terminal at sign-in.
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Run" "Tausi POS" '"$INSTDIR\${MAINBINARYNAME}.exe"'

  ; SERVER=... on the command line: the address the first screen offers.
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "SERVER=" $R1
  ${IfNot} ${Errors}
  ${AndIf} $R1 != ""
    ; All users' application data: C:\ProgramData (the per-machine install already uses this context).
    SetShellVarContext all
    CreateDirectory "$APPDATA\Tausi POS\Terminal"
    FileOpen $R2 "$APPDATA\Tausi POS\Terminal\server.txt" w
    FileWrite $R2 "$R1$\r$\n"
    FileClose $R2
  ${EndIf}
!macroend

!macro NSIS_HOOK_POSTUNINSTALL
  ; An update uninstalls the old version first: keep starting at sign-in then.
  ${If} $UpdateMode <> 1
    DeleteRegValue HKLM "Software\Microsoft\Windows\CurrentVersion\Run" "Tausi POS"
  ${EndIf}
!macroend
