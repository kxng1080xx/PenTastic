# PenTastic

Use a Samsung Galaxy phone and its S Pen as a pressure-sensitive pen tablet for a Windows 11 PC.
Everything stays on your devices: no account, no cloud, no internet needed.

- **Control mode**: the S Pen drives the Windows pen (pressure, tilt, hover, the eraser end, and the
  pen button) in any app that supports a pen.
- **Draw mode**: you draw on the PenTastic canvas on the PC. It has pages, white or black paper,
  saves itself, and copies or saves to PNG.
- **PC screen on the phone**: the phone can show the PC screen (up to 120 fps over USB), so you can see
  what you draw on.
- **Shortcut keys**: a strip of buttons on the phone that sends keys to the PC (undo, redo, pan,
  brush and so on). You can change each one.

Requirements: Windows 10 2004 or later (Windows 11 recommended), an Android 11+ phone with a stylus
(made for the Galaxy S23 Ultra).

## Install

1. **PC**: copy `PenTastic.exe` anywhere (for example `C:\Program Files\PenTastic`) and run it.
   It puts an icon in the system tray and opens the dashboard. It includes everything it needs,
   including adb for USB.
2. **Phone**: copy the APK to the phone and open it. Android asks you to allow installing
   apps from the app you opened it with (Files, for example).

If you installed a test build from Android Studio or `adb install` before, uninstall it first: a
release build is signed with a different key and Android refuses to install over it. You will pair
again afterwards.

## Connect

Open PenTastic on the phone and pick how to connect.

| | How | Notes |
|---|---|---|
| **USB** | Turn on USB debugging on the phone (Settings › About phone › Software information, tap *Build number* 7 times, then Settings › Developer options › USB debugging). Plug in the cable and allow the PC when the phone asks. | Fastest. PC screen up to 120 fps. |
| **Wi‑Fi** | Phone and PC on the same Wi‑Fi or hotspot. On the PC dashboard click **Show pairing code** (or tray icon › Pair a phone…), then scan the code with the phone's camera. | Encrypted. Includes the PC screen. If the phone can't find the PC, click **Allow through Windows Firewall** in the dashboard settings. |
| **Bluetooth** | Turn on Bluetooth on both. Pair with the code as for Wi‑Fi (the code includes the PC's Bluetooth address). | No network needed. Pen and keys only, no PC screen. |

You pair once; the phone remembers the PC.

## Use

On the phone, the toolbar on the left switches between Control and Draw mode, picks the brush,
shape, colour and size, and has undo, redo and clear (Draw mode). **View** turns on the PC screen,
the grid, the shortcut keys and the paper colour. **Settings** has smoothing, pressure feel and what
the S Pen button does. Pinch with two fingers to zoom into part of the PC screen.

The canvas window on the PC:

| | |
|---|---|
| Pages | ‹ › or Page Up / Page Down to move between pages, + or Ctrl+N for a new page, the bin to delete a page |
| Zoom | − and +, Ctrl+mouse wheel, **Fit** or Ctrl+0 |
| Full screen | the corner button or F11, Esc to leave |
| Export | **Copy** (Ctrl+C) to paste into any app, **Save PNG** (Ctrl+S) on the paper or with a transparent background |

The canvas is saved a second after every change and when PenTastic quits, in
`%LOCALAPPDATA%\PenTastic\Canvas` (Windows Ink `.isf` files plus `pages.json`). The log is
`%LOCALAPPDATA%\PenTastic\pentastic.log` (tray icon › Open log).

With more than one monitor, pick the display the pen uses in the dashboard's **Display** card
(**Identify** shows each display's number on it). The choice is remembered.

Dashboard settings: start with Windows, minimize to tray, and the firewall rule for Wi‑Fi.

## Build from source

### Windows app (`windows/`)

Needs the .NET 8 SDK or later.

```
cd windows
dotnet test PenTastic.Tests                       # unit tests
dotnet run --project PenTastic                    # debug build
dotnet publish PenTastic -c Release -o ..\dist    # dist\PenTastic.exe, one self-contained file
```

The release build bundles adb from `%LOCALAPPDATA%\Android\Sdk\platform-tools` (installed with
Android Studio, or download *SDK Platform-Tools for Windows*). To use another folder, add
`-p:AdbDir=C:\path\to\platform-tools`.

### Phone app (`android/`)

Needs JDK 17 or later and the Android SDK (Android Studio installs both).

```
cd android
gradlew test                # unit tests
gradlew assembleDebug       # app\build\outputs\apk\debug\app-debug.apk
gradlew assembleRelease     # app\build\outputs\apk\release\app-release.apk
```

A release APK must be signed. Make a key once and keep it safe: every update has to be signed
with the same key.

```
keytool -genkeypair -keystore pentastic.jks -alias pentastic -keyalg RSA -keysize 4096 -validity 10000
```

(`keytool` is in Android Studio's `jbr\bin` folder.) Then create `android/keystore.properties`:

```
storeFile=C:/path/to/pentastic.jks
storePassword=your store password
keyAlias=pentastic
keyPassword=your key password
```

Both files are in `.gitignore`. Without `keystore.properties`, `assembleRelease` makes
`app-release-unsigned.apk`, which Android won't install.

## More

- [docs/protocol.md](docs/protocol.md): the phone ↔ PC protocol.
- [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md): icons, libraries and tools PenTastic includes.
