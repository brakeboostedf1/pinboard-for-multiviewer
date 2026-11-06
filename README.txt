PINBOARD FOR MULTIVIEWER
Holds MultiViewer windows on up to 4 boards. One board shows at a time; slide between them.

NEEDS
Windows 10 or 11 (64-bit). MultiViewer installed. Nothing else.

INSTALL
Run Pinboard-Setup.exe. Choose install folder, desktop shortcut, Start menu entry and
start with Windows. Start order of Pinboard and MultiViewer does not matter.
First start shows How to use. Open it again any time: Ctrl+Alt+Up, or tray icon > How to use.

HOW TO USE
1  Start          Open Pinboard and MultiViewer, any order. Open streams and timing as usual.
2  Change board   Move mouse to left or right screen edge. Click arrow. Boards loop.
3  Move a window  Drag window. Hold it over arrow: board changes, window comes along.
4  Save layout    In MultiViewer, save setup while board 1 shows. Open it with Pinboard
                  running, on board 1: every window returns to its board.
5  Find a window  Ctrl+Alt+Up or tray icon. Click window: it comes to this board.
6  Exit           Ctrl+Alt+Up > Exit. All windows come back on screen.

SHORTCUTS
Ctrl+Alt+Left / Right   Previous / next board
Ctrl+Alt+Up             Menu: windows, how to use, exit
Ctrl+Alt+Down           Minimize / restore all boards

SETTINGS (pinboard.json in the install folder; restart Pinboard after a change)
Boards       Number of boards (2-4 is a good range).
SlideMs      Slide time in ms.
HoldMs       Hold time over an arrow before a dragged window changes board, in ms.
ArrowHideMs  Arrows hide this long after the mouse stops, in ms.
Protect      true: video shows black in screenshots, screen sharing and the taskbar preview.
             Timing windows stay visible.
Monitor      Monitor for the boards, e.g. \\.\DISPLAY2. Empty: main monitor.
             Windows put on another monitor stay outside the boards.

PROTECT: WHAT IT COVERS
Blocks video in captures of the screen (screenshots, full-screen sharing, screen recording).
It cannot block a capture that targets one MultiViewer window directly: only MultiViewer
itself can protect its own windows.

UNINSTALL
Windows Settings > Apps > Pinboard for MultiViewer > Uninstall. Removes Pinboard, its
shortcuts and %LOCALAPPDATA%\Pinboard. MultiViewer and its setups stay as they are.
Pinboard changes nothing in MultiViewer and sends nothing over the network.

Pinboard is an add-on for MultiViewer.
