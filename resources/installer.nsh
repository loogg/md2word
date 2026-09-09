; Only NSIS Setup writes this marker. Portable payloads remain unchanged.
!macro customInstallMode
  StrCpy $isForceCurrentInstall "1"
!macroend

!macro customInstall
  ClearErrors
  FileOpen $0 "$INSTDIR\resources\md2word-installed" w
  IfErrors marker_failed
  FileWrite $0 "setup"
  FileClose $0
  Goto marker_done
marker_failed:
  MessageBox MB_ICONSTOP "Unable to initialize the installed application."
  Abort
marker_done:
!macroend
