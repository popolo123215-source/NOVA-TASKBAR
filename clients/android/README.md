# NOVA Connect for Android

Native Kotlin companion app for sending files directly between Android and NOVA TaskBar on Windows over a local Wi-Fi/LAN connection. It has no cloud dependency or broad storage permission.

## Features

- Automatically discovers nearby NOVA devices over the same Wi-Fi/LAN using multicast with IPv4 broadcast fallback; the peer address and listener port are taken from the announcement.
- Provides a **Refresh** action that restarts only the LAN discovery socket and immediately resumes announcements; transfers and the app service remain running.
- Keeps manual IPv4/port/pairing-code entry collapsed as a fallback for networks that block local discovery.
- Pairs by comparing the full SHA-256 certificate fingerprint; transfers use mutually authenticated TLS with pinned peer certificates.
- Uses TLS 1.2 for LAN transfers and authorizes Android Keystore's raw RSA operation and un-digested signature path required by Conscrypt during mutual-TLS handshakes.
- Sends one or more files through Android's document picker and receives files into a user-selected Storage Access Framework folder.
- Requires per-file approval, streams large files without buffering them in memory, verifies SHA-256, preserves existing files with collision-safe names, and reports progress, speed, cancellation, status, and recent history.
- Runs the listener and discovery as a connected-device foreground service so incoming requests can be handled while the activity is backgrounded.

## Build and test

From this directory, with Android SDK installed:

```powershell
.\gradlew.bat :app:testDebugUnitTest :app:assembleDebug
.\gradlew.bat :app:assembleRelease
```

The Release APK is emitted under `app\build\outputs\apk\release\`. This checkout uses a locally generated release key under the ignored `release-key\` directory and `keystore.properties`; neither is included in source or release archives. Back up the key securely to preserve update compatibility, and never commit or share it. A fresh checkout without a signing key produces an unsigned Release APK unless you configure your own key.

## Pairing and manual connection

1. On both devices, exchange the `NOVA1:` pairing code over a trusted channel and compare the complete fingerprint before approving.
2. Keep NOVA running on Windows. The Transfer widget shows its IPv4 addresses and the TCP listener port.
3. If automatic discovery does not list the Windows computer, enter one IPv4 address, the listener port, and its pairing code in Android's Manual connection section.
4. Choose the Android download folder before approving the first incoming file.

Windows retains its existing transfer implementation and protocol. This app speaks its version-1 JSON framing and `offer` → `decision` → file bytes → `complete` exchange.

## Android 1.0.4 TLS key migration

The TLS identity key is upgraded to allow RSA `NoPadding` and `DIGEST_NONE`, which Conscrypt requires for TLS signatures. This changes the Android certificate fingerprint. On Windows 6.1.12, select the phone, compare the refreshed full fingerprint, and explicitly confirm certificate replacement when prompted. Older Windows releases must remove the old pairing first. The Android app's trust of the Windows certificate is unchanged.

## Verification status

Automated Android unit tests and APK builds are recorded in `docs/IMPLEMENTATION_PLAN.md`. Real Android↔Windows device transfer and visual checks on physical-device DPI/screen sizes must be reported separately; they are not implied by a successful Gradle build.
