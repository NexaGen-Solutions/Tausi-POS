; Tausi POS terminal installer hooks (NSIS, included by Tauri's installer template).
;
; - Installs into C:\Program Files\Tausi POS\Terminal\ (the server lives next to it in ...\Server\).
; - Starts the launcher for every Windows user at sign-in (the machine's Run key). The launcher
;   (...\Tausi POS\Launcher\tausi-launcher.exe, its own folder) starts the app and watches it.
; - Before a new version replaces the app, the one installed is copied to ...\Terminal.previous, so
;   an update or reinstall cut off part-way (power cut, antivirus) leaves a version that runs: the
;   launcher goes back to it. A manual reinstall first runs the old uninstaller ("Uninstall before
;   installing"); that uninstall keeps the copy, the launcher and the sign-in entry. Only a real
;   uninstall (from Settings → Apps) removes them.
; - TausiPOS-Setup.exe /S SERVER=http://192.168.1.10:5080 pre-fills the server address: it is written
;   to C:\ProgramData\Tausi POS\Terminal\server.txt, which the app reads on its first start.

!include "${__FILEDIR__}\trust-cert.nsh"

!define TAUSI_TERMINAL_KEY "Software\NexaGen Solutions\Tausi POS Terminal"

; Keeps a copy of the installed app beside it (...\Terminal.previous) for the launcher to fall back on.
!macro TausiKeepPrevious
  ${If} ${FileExists} "$INSTDIR\${MAINBINARYNAME}.exe"
    CreateDirectory "$INSTDIR.previous"
    CopyFiles /SILENT "$INSTDIR\${MAINBINARYNAME}.exe" "$INSTDIR.previous"
  ${EndIf}
!macroend

!macro NSIS_HOOK_PREINSTALL
  ; The default folder is "Tausi POS"; the terminal has its own subfolder. A folder chosen by hand,
  ; or the one an earlier install used, is kept.
  ${If} $INSTDIR == "$PROGRAMFILES64\${PRODUCTNAME}"
    StrCpy $INSTDIR "$INSTDIR\Terminal"
    SetOutPath $INSTDIR
  ${EndIf}
  ; The launcher waits while this is set (up to 10 minutes) instead of restarting the app mid-install.
  WriteRegDWORD HKLM "${TAUSI_TERMINAL_KEY}" "Installing" 1
  !insertmacro TausiKeepPrevious
!macroend

!macro NSIS_HOOK_POSTINSTALL
  ; The launcher, in its own folder (a running launcher is renamed out of the way first).
  ${GetParent} "$INSTDIR" $R4
  ${If} ${FileExists} "$INSTDIR\tausi-launcher.exe"
    CreateDirectory "$R4\Launcher"
    Delete "$R4\Launcher\tausi-launcher.old.exe"
    Rename "$R4\Launcher\tausi-launcher.exe" "$R4\Launcher\tausi-launcher.old.exe"
    CopyFiles /SILENT "$INSTDIR\tausi-launcher.exe" "$R4\Launcher"
    Delete /REBOOTOK "$R4\Launcher\tausi-launcher.old.exe"
    StrCpy $R5 "$R4\Launcher\tausi-launcher.exe"
  ${Else}
    StrCpy $R5 "$INSTDIR\${MAINBINARYNAME}.exe"
  ${EndIf}

  ; Every user: start the launcher (which starts the app) at sign-in, and from the Start menu.
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Run" "Tausi POS" '"$R5"'
  ${If} ${FileExists} "$SMPROGRAMS\${PRODUCTNAME}.lnk"
    CreateShortcut "$SMPROGRAMS\${PRODUCTNAME}.lnk" "$R5" "" "$INSTDIR\${MAINBINARYNAME}.exe"
  ${EndIf}
  DeleteRegValue HKLM "${TAUSI_TERMINAL_KEY}" "Installing"

  ; A self-signed build: trust the certificate it is signed with.
  !insertmacro TausiTrustCert

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

; The uninstaller runs in place (from the install folder) only when a reinstall runs it ("Uninstall
; before installing"); from Settings → Apps, Windows copies it to a temporary folder first.
!macro NSIS_HOOK_PREUNINSTALL
  ${If} $EXEDIR == $INSTDIR
    !insertmacro TausiKeepPrevious
  ${EndIf}
!macroend

!macro NSIS_HOOK_POSTUNINSTALL
  ; An update or a reinstall: keep the launcher, the previous version and starting at sign-in, so a
  ; reinstall that fails half-way still leaves a version that runs.
  ${If} $UpdateMode <> 1
  ${AndIf} $EXEDIR != $INSTDIR
    DeleteRegValue HKLM "Software\Microsoft\Windows\CurrentVersion\Run" "Tausi POS"
    DeleteRegKey HKLM "${TAUSI_TERMINAL_KEY}"
    ${un.GetParent} "$INSTDIR" $R4
    RMDir /r /REBOOTOK "$R4\Launcher"
    RMDir /r /REBOOTOK "$INSTDIR.previous"
  ${EndIf}
!macroend
