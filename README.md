# Pinboard for MultiViewer

Pinboard for MultiViewer takes MultiViewer media players and puts them into a single tab in your Windows taskbar. Great for single monitor use and tab switch friendly. Up to 4 pinboards in one window. Change the board with the arrows at the screen edge, the dots at the top of the screen, or the keyboard.

Pinboard for MultiViewer is an add-on application. It is not an official F1 or MultiViewer product. It moves MultiViewer media players into it only. It does not change MultiViewer or its files.

## Download

1. Open [Releases](../../releases/latest).
2. Download `Pinboard-for-MultiViewer-Setup.exe`.

Windows only release. No Mac or Linux

You need Windows 10 or Windows 11, 64-bit, and [MultiViewer](https://multiviewer.app/).

Windows can show "Windows protected your PC". The file has no code signature hence the warning. Select **More info**, then **Run anyway**.

### Check the file

Each release shows the SHA-256 value of the file. In PowerShell:

```powershell
Get-FileHash .\Pinboard-for-MultiViewer-Setup.exe
```

The value must be the same as the value in the release.

GitHub builds each release from this source code and signs a record of the build. To check it, with the [GitHub CLI](https://cli.github.com):

```
gh attestation verify Pinboard-for-MultiViewer-Setup.exe --repo brakeboostedf1/pinboard-for-multiviewer
```

## Install

Run `Pinboard-for-MultiViewer-Setup.exe`. Setup installs Pinboard in Program Files for all users. Windows asks for administrator permission one time.

## How to use

1. **Start.** Open Pinboard and MultiViewer. Pinboard waits in the system tray. It opens when your setup opens, on board 1.
2. **Change board.** Click the arrow at the left or right edge, or click a dot at the top of the screen. The dots fade when a player is under them. Touch the top edge of the screen to show them again.
3. **Move a player.** Drag the player onto an arrow and hold. Or press Ctrl+Shift+Left or Ctrl+Shift+Right: the player in front goes to the previous or next board.
4. **Full screen.** Press F11: the board also covers the taskbar. Press F11 again to show the taskbar.
5. **Save.** Arrange your boards, then save the setup in MultiViewer. The setup opens with each player on its board.
6. **Find a player.** If a player is lost or minimized, press Ctrl+Up or click the tray icon. Click the player: it comes back on this board.

### Shortcuts

The shortcuts work when Pinboard or MultiViewer is in front. Other apps keep these keys. You can change them in Settings.

| Keys | Action |
| --- | --- |
| Ctrl+Left / Ctrl+Right | Previous / next board |
| Ctrl+Shift+Left / Ctrl+Shift+Right | Send the player in front to the previous / next board |
| Ctrl+Up | Player list and menu |
| Ctrl+1 to Ctrl+4 | Go to board 1 to 4 |
| F11 | Full screen |

### Settings

Open the menu (Ctrl+Up or the tray icon), then **Settings**.

- **Boards:** number of boards (1 to 4), board names, slide speed.
- **Shortcuts:** click a shortcut, then press the new keys.
- **Behaviour:** start with Windows, and what happens to the players when you close Pinboard.

MultiViewer needs no settings. If a player snaps to half the screen at an arrow, turn off **Settings > System > Multitasking > Snap windows** in Windows.

### Screen captures

Video players show black in screenshots, screen recordings and screen sharing. F1TV video has copyright protection, so DRM is always on. Timing windows stay visible.

## Uninstall

Open **Windows Settings > Apps**, find **Pinboard for MultiViewer** and select **Uninstall**. MultiViewer and its setups do not change.

## Privacy

Pinboard sends nothing over the network and collects no data. Your settings are in `%LOCALAPPDATA%\Pinboard for MultiViewer`.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet publish Pinboard.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o out
```

`out\Pinboard.exe` is the app. Rename a copy to `Pinboard-for-MultiViewer-Setup.exe` and it becomes the setup.

## License

- Code: [MIT](LICENSE).
- Font: Exo 2, by The Exo 2 Project Authors, under the [SIL Open Font License 1.1](fonts/OFL.txt).
