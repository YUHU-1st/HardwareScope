# Third-party notices

HardwareScope uses unmodified third-party libraries. Package versions and license metadata are included in the Windows distribution under `licenses/dependencies.json`; license texts are included beside that file. Dynamic Windows and NVIDIA system libraries are supplied by the operating system or installed drivers, not copied into this distribution.

| Component | License / source |
|---|---|
| LibreHardwareMonitorLib 0.9.6 | MPL-2.0; [source commit](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701) |
| RAMSPDToolkit-NDD 1.4.2 | MPL-2.0; [source](https://github.com/Blacktempel/RAMSPDToolkit/tree/3b47b960e0830fef344624ad5e389675d5f0a1ce) |
| BlackSharp.Core 1.0.7 | MPL-2.0; [source](https://github.com/Blacktempel/BlackSharp) |
| DiskInfoToolkit 1.1.2 | MPL-2.0; [source](https://github.com/Blacktempel/DiskInfoToolkit) |
| InTheHand.Net.Bluetooth 4.2.5 | MIT; [source](https://github.com/inthehand/32feet) |
| Linux.Bluetooth 5.67.1 | Apache-2.0; [source](https://github.com/SuessLabs/Linux.Bluetooth) |
| Tmds.DBus 0.95.1 | MIT; [source](https://github.com/tmds/Tmds.DBus) |
| Mono.Posix.NETStandard 1.0.0 and MonoPosixHelper | [upstream license and component notices](https://github.com/mono/mono/blob/main/LICENSE); [source](https://github.com/mono/mono/tree/main/mcs/class/Mono.Posix) |
| Microsoft.Diagnostics.Tracing.TraceEvent 3.2.8 | MIT; [source](https://github.com/microsoft/perfview) |
| .NET and Windows Desktop runtime / Microsoft libraries | MIT and included notices; [source](https://github.com/dotnet) |
| HidSharp 2.6.4 | Apache-2.0; included original package license |
| Kotlin runtime 2.1.21 | Apache-2.0; [source](https://github.com/JetBrains/kotlin) |
| BouncyCastle.Cryptography 2.6.2 (Windows) / bcprov-jdk18on 1.86 (Android) | Bouncy Castle license (MIT); [C# source](https://github.com/bcgit/bc-csharp), [Java source and license](https://github.com/bcgit/bc-java) |
| JUnit 4.13.2 (test dependency) | EPL-1.0; [license](https://github.com/junit-team/junit4/blob/main/LICENSE-junit.txt) |
| org.json 20250517 (test dependency) | Public domain; [license](https://github.com/stleary/JSON-java/blob/20250517/LICENSE) |
| Inno Setup installer | [Inno Setup license](https://jrsoftware.org/files/is/license.txt) |
| Simplified Chinese installer translation | Zhenghan Yang (Kira); [original messages and attribution](https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation) |

No modifications were made to LibreHardwareMonitor or RAMSPDToolkit source files. Their MPL covered files remain governed by MPL-2.0. The application source uses these libraries through their published APIs.

## Interface design reference

HardwareScope 1.2.0 and FileGuard 1.1.0 reference [KUNLUN Design System by yoruuuchan](https://github.com/yoruuuchan/yoru-design-systems/tree/main/KUNLUN%20Design%20System), including its [design instructions](https://github.com/yoruuuchan/yoru-design-systems/blob/main/KUNLUN%20Design%20System/SKILL.md), for colors and layout as visual inspiration. Their interfaces are original native WPF and Win32 implementations for HardwareScope. No upstream UI components, application code, logos, font files, or web assets are copied or bundled. The interfaces use Microsoft YaHei UI and Consolas supplied by Windows instead of downloading web fonts. The KUNLUN reference does not imply affiliation or endorsement.

The PKCS#12 file under `android/app/src/test/resources/https-test.p12` is a public, disposable localhost TLS fixture generated for this project's tests. It is not a user certificate, release-signing key, or production credential.
