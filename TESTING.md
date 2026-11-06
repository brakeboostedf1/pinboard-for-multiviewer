# Testing Pinboard on another machine

Every build already runs a self-test and an install test on a clean GitHub machine (see `.github/install-test.ps1`).
This list is for what only a person with MultiViewer can check. Write down your Windows version, display scale,
monitors and taskbar position, then do the steps. Note each step that fails.

## Where to test

- **Your own PC, another display scale:** Settings > System > Display > Scale: 125 %, then 150 %. Set it back after.
- **Windows Sandbox:** a clean Windows that is deleted when you close it. Needs Windows 10/11 Pro, CPU virtualization
  on in the BIOS, and Windows Sandbox on in "Turn Windows features on or off". Copy the setup into a folder and open
  a `.wsb` file that maps that folder (example in the Windows Sandbox documentation). Install MultiViewer in the
  sandbox too.
- **Another PC:** a laptop, a second monitor, Windows 10, the taskbar at the top or side.

## Steps

1. **Install.** Run `Pinboard-for-MultiViewer-Setup.exe`. Windows shows "Windows protected your PC": select
   **More info**, then **Run anyway**. Keep desktop shortcut and Start menu ticked.
   - Desktop shortcut and Start menu entry open Pinboard.
   - Windows search finds "Pinboard for MultiViewer".
   - Settings > Apps shows "Pinboard for MultiViewer" with the right version.
2. **First start.** The How to use window opens. Minimize works; "Don't show at start" works after a restart.
3. **Players.** Open MultiViewer and two players. The board opens on board 1, under the players, taskbar visible.
4. **Boards.** Change board with the edge arrows, the dots at the top and Ctrl+Left/Right. Players slide, video keeps
   playing, audio stays in sync.
5. **Timing windows.** Open Live Timing and a timing page (Race Trace, Lap Time Series). They go on the board and
   draw without changing to another app.
6. **Setups.** Put windows on boards 1 to 4, maximize one on board 3, save a setup in MultiViewer. Close all windows.
   Open the setup again: every window is on its saved board and place, board 1 shows, the maximized one is on board 3.
7. **F11.** The board covers the taskbar; F11 again shows it.
8. **Close.** Close Pinboard from its taskbar button: it asks to close Pinboard only or players too. Both work:
   players come back on screen, or close.
9. **Screen capture.** In a screenshot (Win+Shift+S), video players are black and timing windows show.
10. **Uninstall.** Settings > Apps > Pinboard for MultiViewer > Uninstall. Shortcuts and the Apps entry are gone.

## Report

Send the failed steps, a screenshot of the problem (crop out other apps) and
`%LOCALAPPDATA%\Pinboard for MultiViewer\pinboard.log` if it exists.
