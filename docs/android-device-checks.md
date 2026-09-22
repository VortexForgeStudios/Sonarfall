# Android device checks — haptics, reminders, permissions

Everything below runs against an installed build on a phone. Nothing here can be verified in the
editor: the editor has no vibrator, no AlarmManager and no permission dialogs.

`adb` ships with the Unity Android SDK:

```
"C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
```

## 1. The permission set is exactly three

```
adb shell dumpsys package com.vortexforgestudios.sonarfall | grep -A6 "requested permissions"
```

Expected: `VIBRATE`, `POST_NOTIFICATIONS`, `RECEIVE_BOOT_COMPLETED`. **No `INTERNET`.**
(`AndroidManifestPostProcessor` strips INTERNET; the privacy policy and the Play Data-safety form both
promise the app has no network access.)

## 2. Haptics

Settings → hold the **VIBRATION** row for a second. Six pulses play, light to heavy, and a banner
prints the platform report. The same line lands in logcat:

```
adb logcat -s Unity | grep "Haptics"
```

Read it left to right:

| field | means |
|---|---|
| `android ok / sdk NN` | vibrator found; anything else here is the actual fault |
| `amplitude control yes/NO` | NO = budget motor; tiers are separated by duration only, and that is expected |
| `VibrationAttributes(MEDIA/TOUCH)` | Android 13+. `AudioAttributes(GAME/…)` below that. Both are correct |
| `system touch-feedback OFF` | only affects the button tick — gameplay buzzes are filed as MEDIA on purpose |
| `media vibration OFF` / `MASTER OFF` | the phone's own **Media vibration** slider or master haptics switch is off. Gameplay haptics cannot play until the user raises it (Samsung: Sounds and vibration → Vibration intensity → Media; Pixel/Moto: Vibration & haptics → Media vibration). This is the user's setting, and the game must not override it |
| `in-game toggle OFF` | Settings → VIBRATION |

Battery Saver also silences media-class vibrations on Android 13+. That is the OS, not the app.

## 3. Reminders reach a closed app

1. First tap of PLAY on a fresh install shows the Android 13+ notification prompt. Allow it.
2. Settings → **REMINDERS** row: `ON` = granted, `BLOCKED` = the OS is refusing (tap the row to open
   the system page), `OFF` = the player turned them off in-game.
3. Hold the REMINDERS row for a second: *TEST REMINDER ARMED*. Now **leave the app** (home, or swipe it
   out of recents). The test notification arrives about a minute later, with the sonar glyph in the
   status bar. If it does, the real ones will: they use the same path.

To see what is queued without waiting:

```
adb shell dumpsys alarm | grep -B2 -A6 sonarfall
```

Normal schedule after backgrounding: Daily at 10:00, streak warning at 20:00 (only with a streak ≥ 2),
win-back three days out. Alarms are deliberately **inexact** — exact alarms need a permission Google Play
only grants to alarm-clock and calendar apps — so on a phone that has sat untouched overnight the
morning one lands at the next Doze window or when the phone is picked up.

If the test never arrives:

- `adb shell dumpsys package com.vortexforgestudios.sonarfall | grep POST_NOTIFICATIONS` → `granted=false`
  means permission. Settings → REMINDERS will read BLOCKED; tap it.
- Realme/Oppo/Xiaomi/Vivo, and Samsung with "Put unused apps to sleep": the skin killed the alarm. The
  phone's battery settings must exclude Sonarfall from optimisation. No code path survives a force-stop;
  Android drops every alarm on purpose.
- Rebooting the phone keeps the schedule (`RECEIVE_BOOT_COMPLETED` + the package's restart receiver).

## 4. Before uploading to Play

- Build an AAB with the release keystore (`androidUseCustomKeystore` is back on; the password is
  per-session), bump `AndroidBundleVersionCode` past the last upload.
- `Sonarfall → Build Verification APK` writes `Builds/Verify/build-report.txt`; re-run §1 on that APK if
  anything touched packages, permissions or Player Settings.
- Play Data safety stays "No data collected": Engine Diagnostics is off, no analytics, no network.
