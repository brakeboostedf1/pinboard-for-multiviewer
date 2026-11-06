PINBOARD FOR MULTIVIEWER  1.1.0
Holds MultiViewer players on up to 4 boards. One board shows at a time; slide between them.

NEEDS
Windows 10 or 11 (64-bit). MultiViewer installed. Nothing else.

INSTALL
Run Pinboard-for-MultiViewer-Setup.exe. Installs to Program Files for all users (one admin prompt).
Choose desktop shortcut, Start menu entry, start with Windows.
Windows may warn "unrecognized app": the file is not code-signed. More info > Run anyway.

HOW TO USE
1  Start          Open Pinboard and MultiViewer. Pinboard waits in system tray. It shows in taskbar
                  when your setup opens.
2  Change board   Click arrow at left or right screen edge. Up to 4 boards.
3  Move player    Drag player onto arrow and hold. Or press Ctrl+Shift+Left / Right: player in front
                  goes to previous / next board.
4  Save           Arrange boards, then save setup in MultiViewer. Setup opens with each player on its board.
5  Find player    Player lost or minimized: press Ctrl+Up, or right-click tray icon. Click player: it comes
                  back on this board.

SHORTCUTS (work while Pinboard or MultiViewer is in front; other apps keep these keys)
Ctrl+Left / Right         Previous / next board
Ctrl+Shift+Left / Right   Send player to previous / next board
Ctrl+Up                   Player list and menu

SETTINGS NEEDED
MultiViewer   None.
Windows       Player snaps to half screen at arrow: turn off Settings > System > Multitasking > Snap windows.

PINBOARD SETTINGS (tray icon > Settings; restart Pinboard after a change)
File: %LOCALAPPDATA%\Pinboard for MultiViewer\settings.json
Boards       Number of boards, 1-4.
SlideMs      Slide time in ms. 0 = no slide.
HoldMs       Hold time over an arrow before a dragged player changes board, in ms.
ArrowHideMs  Arrows hide this long after the mouse stops, in ms.
Protect      true (default): video shows black in screenshots, screen sharing, screen recording and the
             taskbar preview. Timing windows stay visible.
Monitor      Monitor for the boards, e.g. \\.\DISPLAY2. Empty: main monitor.
Back         Board colour.

PROTECT: WHAT IT COVERS
Blocks video in captures of the whole screen. It cannot block a capture that targets one MultiViewer
window directly: only MultiViewer itself can protect its own windows.

UNINSTALL
Windows Settings > Apps > Pinboard for MultiViewer > Uninstall. Removes Pinboard.exe, its shortcuts and
your settings. MultiViewer and its setups stay as they are.

PRIVACY
Pinboard sends nothing over the network and changes nothing in MultiViewer.

Pinboard is an add-on for MultiViewer.
