# PenTastic wire protocol, version 1

Phone and PC talk over one byte stream: TCP (USB via `adb reverse`, or Wi-Fi), later Bluetooth RFCOMM.
Every message is the same on every transport.

- Little endian everywhere.
- TCP port **47810**, UDP discovery port **47811**.
- Frame: `u16 length` (bytes that follow), `u8 type`, payload.
- `str8`: `u8` byte count, then UTF-8 (truncated to 255 bytes).
- `MAGIC`: ASCII `PNTC` (`50 4E 54 43`).
- A receiver ignores frames with an unknown type, so later versions can add messages.
  Malformed frames (truncated, trailing bytes, bad magic, bad enum value) close the connection.

## Messages

| Type | Name | Direction | Payload |
|---|---|---|---|
| `0x01` | SERVER_HELLO | PC → phone, first, on accept | `MAGIC`, `u16 version`, `serverId[16]`, `nonce[16]`, `name str8` |
| `0x02` | CLIENT_HELLO | phone → PC | `MAGIC`, `u16 version`, `clientId[16]`, `u8 authKind`, auth data, `name str8` |
| `0x03` | WELCOME | PC → phone | `secret[32]`, TargetInfo |
| `0x04` | REJECT | PC → phone, then close | `u8 reason`, `message str8` |
| `0x05` | TARGET | PC → phone | TargetInfo; sent whenever the target monitor or layout changes |
| `0x10` | SAMPLES | phone → PC | `u8 count`, then `count` samples (14 bytes each) |
| `0x20` | MIRROR | phone → PC | `u8 on`, `u16 maxWidth`, `u16 maxHeight` (0 = native). Every "on" starts a fresh stream (new encoder, keyframe first) |
| `0x21` | VIDEO | PC → phone | `u32 frameId`, `u8 flags`, `u16 width`, `u16 height`, then H.264 Annex B bytes (rest of the frame) |
| `0x22` | VIDEO_ACK | phone → PC | `u32 frameId`, sent when that frame is on screen |
| `0x23` | VIEW | phone → PC | `u16 x`, `u16 y`, `u16 size`: the part of the target the pad shows (pinch zoom), fractions 0..65535 of the target; whole target = 0, 0, 65535. Sent on connect and on every change |
| `0x40` | DRAW_STATE | phone → PC | `u8 mode`, `u8 brush`, `u32 color` (ARGB), `u16 width`; sent on connect and on every change |
| `0x41` | INK_ACTION | phone → PC | `u8 command`: 1 undo, 2 clear, 3 redo (Draw mode only); applies to the canvas page on screen |
| `0x42` | PAPER | both | `u8 paper`: 1 white, 2 black (0 is legacy dark and is treated as black); the background of the canvas page on screen, also used for copy and save. The PC sends it when the paper is changed on the PC or another page is shown |
| `0x43` | PAGE | PC → phone | `u8 index` (1-based), `u8 count`: the canvas page on screen, sent when the PC switches, adds or deletes pages. The phone clears its Draw-mode ink when the page changes |
| `0x50` | KEY | phone → PC | `u8 action` (0 tap, 1 press, 2 release), `u8 mods` (bit 0 Ctrl, bit 1 Shift, bit 2 Alt), `u16 vk` (Windows virtual-key code). The PC injects the keys; held keys are released when the phone disconnects |
| `0x7F` | HEARTBEAT | both | empty |

**Modes:** 0 Control (samples drive the Windows pen), 1 Draw (samples draw on the PenTastic canvas
with the brush in DRAW_STATE; nothing is injected). **Brush:** 0 pen, 1 pencil, 2 marker, 3 eraser.
**Width** is the brush size as a fraction of the phone's drawing area width (0..65535), so a stroke
keeps its proportions on a canvas of any size. A sample with the eraser flag always erases.

**authKind and auth data**

| authKind | Meaning | Auth data |
|---|---|---|
| 0 | loopback (USB through adb reverse); accepted only from 127.0.0.1 | none |
| 1 | pairing | `hmac[32]` = HMAC-SHA256(token, nonce ‖ clientId), token from the pairing QR code |
| 2 | proof | `hmac[32]` = HMAC-SHA256(secret, nonce ‖ clientId) |

The secret field in WELCOME is always zeros (reserved): the secret never crosses the network. See **Pairing** below.

**REJECT reasons:** 1 version mismatch, 2 bad or expired pairing QR, 3 not paired, 4 busy.

**TargetInfo:** `i32 targetW`, `i32 targetH`, `u8 selected` (monitor index, `0xFF` = all monitors),
`u8 count`, then `count` × `{i32 x, i32 y, i32 w, i32 h, u8 flags}` (flags bit 0 = primary).
All values are physical pixels. The phone letterboxes its active area to `targetW : targetH`.

**Sample (14 bytes)**

| Field | Type | Notes |
|---|---|---|
| kind | u8 | 0 hover, 1 down, 2 move, 3 up, 4 hover exit, 5 cancel |
| flags | u8 | bit 0 barrel button, bit 1 eraser, bit 2 tilt valid |
| x | u16 | 0..65535 across the active area |
| y | u16 | 0..65535 |
| pressure | u16 | 0..65535, pressure curve already applied by the phone |
| tiltX | i8 | degrees, −90..90, positive = pen top leans right |
| tiltY | i8 | degrees, −90..90, positive = pen top leans toward the user |
| time | u32 | phone event time in ms (wraps) |

One Android MotionEvent becomes one SAMPLES message: all historical samples, then the current one.

## Session

1. Phone connects. PC sends SERVER_HELLO.
2. Phone sends CLIENT_HELLO.
3. PC replies WELCOME, or REJECT and closes.
4. Phone streams SAMPLES. PC sends TARGET when the monitor changes.
5. Both sides send HEARTBEAT after 1 s without sending anything else, and drop the connection after
   3 s without receiving anything. When a connection ends for any reason the PC lifts the pen and
   takes it out of range, so it can never stay pressed.
6. A new connection replaces the current one.

The PC turns samples into Windows pointer frames: a hover frame before the first contact,
DOWN with FIRSTBUTTON_DOWN, UPDATE while in contact, UP with FIRSTBUTTON_UP, then out of range.

## Discovery (UDP 47811)

The phone broadcasts `MAGIC, u16 version, u8 0x30` to every interface's broadcast address.
The PC answers by unicast: `MAGIC, u16 version, u8 0x31, serverId[16], u16 tcpPort, name str8,
i32 targetW, i32 targetH, u8 monitorCount`. The phone uses the answer whose serverId is its paired PC.

## Pairing (QR code)

The tray's **Pair a phone** window shows a QR code with a link:

```
pentastic://pair?id=<serverId, 32 hex>&t=<token, 32 hex>&p=<tcpPort>&h=<IPv4>,<IPv4>…&n=<PC name, URL-encoded>
```

The token is 16 random bytes, new each time the window opens, valid only while it is open and for one
phone. The phone's Camera app opens PenTastic with the link. The phone connects (USB, or the listed
addresses) and sends CLIENT_HELLO with authKind 1: `HMAC-SHA256(token, nonce ‖ clientId)`, so the token
never crosses the network. Both sides then remember the same secret,
`secret = HMAC-SHA256(token, "PNTC secret" ‖ clientId)`, and the PC closes the window. Later connections
use authKind 2. Over USB (loopback) no auth is needed. Unknown link parameters are ignored.

The PC keeps its serverId and the paired phones in `%LOCALAPPDATA%\PenTastic\pairing.json`. The phone
keeps one paired PC and the addresses it last reached it at; it tries USB, then discovery, then those.

## Encryption (Wi-Fi only)

After WELCOME, every frame in both directions on a non-loopback connection is AES-256-GCM encrypted:
`u16 length`, then ciphertext of `type + payload`, then the 16 byte tag (length covers both).
`key = HMAC-SHA256(secret, "PNTC key" ‖ nonce ‖ clientId)`. The 12 byte IV is never sent: byte 0 is the
direction (1 PC → phone, 2 phone → PC), bytes 1–3 zero, bytes 4–11 the sender's frame counter (u64,
from 0). A frame that fails authentication closes the connection. Vectors (secret 00..1F, nonce 20..2F,
clientId 00..0F): HEARTBEAT as the PC's first frame `11 00 78 87 45 C3 1C 6C F3 3C 75 03 98 A4 6A BC 86 41 BD`,
as the phone's first frame `11 00 E6 78 B5 8B 51 A9 B2 CB 9E 5F DF BF B3 A0 3A F5 7B`.

## Test vectors

Both test suites (`windows/PenTastic.Tests`, `android/app/src/test`) encode and decode these exact bytes.

**HEARTBEAT**
```
01 00 7F
```

**SAMPLES**: down, barrel, x 0x8000, y 0x4000, pressure 0xFFFF, tilt (−10, 20), time 0x12345678;
then move, eraser + tilt valid, x 0xFFFF, y 0, pressure 0x0400, tilt (0, −90), time 1
```
1E 00 10 02
01 01 00 80 00 40 FF FF F6 14 78 56 34 12
02 06 FF FF 00 00 00 04 00 A6 01 00 00 00
```

**CLIENT_HELLO**: version 1, clientId 00..0F, loopback, name "S23"
```
1C 00 02 50 4E 54 43 01 00
00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F
00 03 53 32 33
```

**CLIENT_HELLO**: same, pairing, hmac 30..4F
```
3C 00 02 50 4E 54 43 01 00
00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F
01 30 31 32 33 34 35 36 37 38 39 3A 3B 3C 3D 3E 3F
40 41 42 43 44 45 46 47 48 49 4A 4B 4C 4D 4E 4F
03 53 32 33
```

**SERVER_HELLO**: version 1, serverId 10..1F, nonce 20..2F, name "PC"
```
2A 00 01 50 4E 54 43 01 00
10 11 12 13 14 15 16 17 18 19 1A 1B 1C 1D 1E 1F
20 21 22 23 24 25 26 27 28 29 2A 2B 2C 2D 2E 2F
02 50 43
```

**WELCOME**: zero secret, one 1920 × 1080 primary monitor, selected 0
```
3C 00 03
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
80 07 00 00 38 04 00 00 00 01
00 00 00 00 00 00 00 00 80 07 00 00 38 04 00 00 01
```

**TARGET**: all monitors (3840 × 1080); primary at 0,0 and a second monitor at −1920,0
```
2D 00 05 00 0F 00 00 38 04 00 00 FF 02
00 00 00 00 00 00 00 00 80 07 00 00 38 04 00 00 01
80 F8 FF FF 00 00 00 00 80 07 00 00 38 04 00 00 00
```

**REJECT**: version mismatch, message "v"
```
04 00 04 01 01 76
```

**DRAW_STATE**: Draw mode, pen, color 0xFFFFD166, width 1083
```
09 00 40 01 00 66 D1 FF FF 3B 04
```

**INK_ACTION**: undo
```
02 00 41 01
```

**PAPER**: white
```
02 00 42 01
```

**PAGE**: page 2 of 5
```
03 00 43 02 05
```

**KEY**: tap Ctrl+Z
```
05 00 50 00 01 5A 00
```

## PC screen mirroring

The PC captures the target monitor with Desktop Duplication, converts it to NV12 on the GPU and encodes
H.264 with the hardware encoder of the GPU that drives that monitor (low-latency mode, no B-frames, CBR).
Only frames where the screen changed are encoded. One encoded frame is sent as one or more VIDEO
messages (each ≤ 60000 data bytes): **flags** bit 0 first part, bit 1 last part, bit 2 keyframe,
bit 3 new stream (reset the decoder). The PC keeps at most three frames un-acknowledged, so a slow link
lowers the frame rate instead of adding delay; if acks stop for a second it carries on anyway.

**MIRROR**: on, 1920 × 1080
```
06 00 20 01 80 07 38 04
```

**VIDEO**: frame 7, first + last part, keyframe, new stream, 1920 × 1080, data `00 00 00 01`
```
0E 00 21 07 00 00 00 0F 80 07 38 04 00 00 00 01
```

**VIDEO_ACK**: frame 7
```
05 00 22 07 00 00 00
```

## Zoom

The phone pinch-zooms into part of the target. Samples are still sent in whole-target fractions (the
phone maps the pad onto its view first), so the pen and the canvas need nothing from VIEW. The mirror
streams only the VIEW area, scaled up to the same video size, so the video stays under the pen.
DRAW_STATE width is relative to the whole target, so zoomed in it shrinks: the brush keeps its size on
the phone and draws finer on the PC.

**VIEW**: x 0x4000, y 0x2000, size 0x8000 (2x zoom, top left at 25 %, 12.5 %)
```
07 00 23 00 40 00 20 00 80
```
