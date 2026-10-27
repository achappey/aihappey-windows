# Desktop test runners

Run the complete available Windows test inventory from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tests/run-all-tests.ps1
```

The runner builds the current nonvisual projects before executing their assemblies, runs every standalone check and read-only synchronization check, runs the packaging source checks, and publishes the current native UI application. Build failures block dependent suites; old binaries are never a fallback. Independent checks continue after failures. A nonzero exit code means a failure, a timeout, or an explicitly reported coverage blocker. UTF-8 logs and machine-readable summaries are retained under the printed artifacts directory.

The native UI runner uses an isolated process for each entry in the UI manifest, exercises both light and dark themes, retains its reports, and requires both a successful process exit and a successful completion marker. It opens test windows using isolated test profiles. Its compatibility switch to skip builds no longer bypasses publication. Unknown suite names fail rather than running a different suite.

## Focused diagnostics

```powershell
dotnet run --project Tests/AIHappey.Desktop.Tests -- --list-suites
dotnet run --project Tests/AIHappey.Desktop.Tests -- --primitive-routing-only
powershell -NoProfile -ExecutionPolicy Bypass -File Tests/run-ui-tests.ps1 -Suite startup
node Tests/check-runner.cjs
node Tests/check-usage-parity.cjs
```

Focused core suites do not run unrelated prerequisites. The full core run aggregates failures across suites while retaining strict, fail-fast assertions within each suite. The usage snapshot mode produces only its JSON document; the parity checker builds it before execution. Runner regression checks validate failed-build refusal, focused selection, aggregate failure accounting, snapshot isolation, manifest coverage, and rejection of incomplete or failed UI reports.

## Coverage limitations

The signed installer lifecycle is deliberately not installed on a development workstation. It requires signed release and upgrade packages, a compiled native Explorer probe, and an explicitly disposable, interactive Windows 11 environment. The consolidated summary records this as blocked, not passed. See the packaging documentation for its guarded lifecycle runner and additional manual deployment evidence.

The optional bundled-runtime integration requires an actual published runtime directory, supplied as the existing positional argument to the core runner. Credential-free protocol and in-memory transport tests do not prove live provider availability, microphone/camera permissions, or real enterprise authentication. These are not claimed as passed by a source audit.

No resource generators run as part of the consolidated checks: synchronization commands use their read-only check mode. The existing build target may synchronize the provider snapshot from the neighboring browser checkout, so review the working tree after a build.
