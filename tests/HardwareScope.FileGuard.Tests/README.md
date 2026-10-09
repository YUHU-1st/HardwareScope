# Native FileGuard integration tests

Build `HardwareScope.FileGuard.Tests.vcxproj` with Visual Studio 2022 Build Tools, Release x64. Run the resulting console executable with `--report <JSON path>`. Add `--service` to exercise the installed broker through its public named-pipe client.

Every action targets a child created by the test in a kill-on-close job. The child holds disposable Unicode paths under the user's temporary directory and exits after three minutes even if its parent disappears. The suite does not install or change the Windows service, and it never selects a user's application for termination. A fabricated System PID is used only to verify rejection.

The suite verifies genuine rename and deletion denial, exact process/handle discovery, nested directory filtering, rejection of stale process and kernel object identities, selection path validation, selective handle release, preserved process/content/second lock, and termination of the exact fixture. When Windows redacts kernel object addresses from a standard user, direct mode verifies that forced release is refused and still checks termination; `--service` requires a real broker and full handle-release verification. Mapped-view discovery is recorded as optional because it depends on what the Windows discovery APIs expose; clearing its lock after fixture termination is required.

Service mode also sends a malformed request directly over the wire and requires rejection. When the executable is run elevated, it derives a restricted medium-integrity client token and verifies that the broker hides and refuses termination of the test's own elevated fixture for that same-user client. A normal run records that boundary check as skipped; an elevated service run is required to verify it.
