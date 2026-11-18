# Third-party notices

## Atoll

Selected Windows media-session, weather-provider, timer, and system telemetry
implementation patterns in `src/Nova.WindowsClient/Services` are adapted from
Atoll, Copyright (c) 2026 mykolaichechelnytskyi, licensed under the MIT
License. The original license is preserved in [licenses/Atoll.LICENSE](licenses/Atoll.LICENSE).

Source: <https://github.com/Fuwni/dynamic-island-windows>

## NAudio

NAudio 3.3.0 is used for WASAPI loopback capture. NAudio is Copyright (c)
Mark Heath and contributors, and is licensed under the MIT License.

Source: <https://github.com/naudio/NAudio>
License: <https://licenses.nuget.org/MIT>

## LibreHardwareMonitor

LibreHardwareMonitorLib 0.9.6 is used to read hardware-provided CPU and GPU
temperature, clock, and video-memory sensors when available. It is licensed
under MPL-2.0. Sensor data is read locally; unsupported or inaccessible sensors
are reported as unavailable.

Source: <https://github.com/LibreHardwareMonitor/LibreHardwareMonitor>
License: <https://licenses.nuget.org/MPL-2.0>

LibreHardwareMonitorLib depends on DiskInfoToolkit 1.1.2 and
RAMSPDToolkit-NDD 1.4.2, both licensed under MPL-2.0, and HidSharp 2.6.4,
licensed under Apache-2.0. Its Windows dependency set also includes
BlackSharp.Core 1.0.7 (MPL-2.0), Mono.Posix.NETStandard 1.0.0 (Microsoft
.NET Library License), and Microsoft System.Management, System.Numerics.Tensors,
System.IO.FileSystem.AccessControl, and System.IO.Ports packages (MIT).

Sources:
- <https://github.com/Blacktempel/DiskInfoToolkit>
- <https://github.com/Blacktempel/RAMSPDToolkit>
- <https://software.seekye.com/hidsharp>
- <https://www.nuget.org/packages/BlackSharp.Core/1.0.7>
- <https://www.nuget.org/packages/Mono.Posix.NETStandard/1.0.0>

Licenses:
- <https://licenses.nuget.org/MPL-2.0>
- <https://licenses.nuget.org/Apache-2.0>
- <https://go.microsoft.com/fwlink/?linkid=869050>
- <https://licenses.nuget.org/MIT>

HidSharp copyright 2010-2025 James F. Bellinger.

## Yandex Music API reference

The optional lyrics client follows the reverse-engineered Yandex Music search
and LRC endpoint/signing behavior documented by the `yandex-music-api` project.
That project identifies the Yandex Music API as unofficial and is licensed
under LGPL-3.0. NOVA does not include that library or its code; the endpoint
contract may change without notice.

Source: <https://github.com/MarshalX/yandex-music-api>
License: <https://github.com/MarshalX/yandex-music-api/blob/master/LICENSE>

## OBS WebSocket protocol reference

NOVA implements the documented OBS WebSocket v5 JSON protocol using the
.NET `ClientWebSocket`; no OBS WebSocket library or OBS source code is bundled.
The protocol and server behavior references below are from obs-websocket, which
is licensed under GPL-2.0-or-later.

Protocol: <https://github.com/obsproject/obs-websocket/blob/e28f84975984340e84fc4879cfc05741cc0afde1/docs/generated/protocol.md>
Server configuration: <https://github.com/obsproject/obs-websocket/blob/e28f84975984340e84fc4879cfc05741cc0afde1/src/Config.cpp>
Listener behavior: <https://github.com/obsproject/obs-websocket/blob/e28f84975984340e84fc4879cfc05741cc0afde1/src/websocketserver/WebSocketServer.cpp>
License: <https://github.com/obsproject/obs-websocket/blob/master/LICENSE>

## MyMemory translation service

Screen Text translation sends the selected OCR text to MyMemory only after an
explicit confirmation. NOVA does not include a translation library or API key.
The free endpoint has a documented per-request length limit and external
service rate/privacy terms.

API: <https://mymemory.translated.net/doc/spec.php>
Privacy: <https://mymemory.translated.net/doc/usageterms.php>
