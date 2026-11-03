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

### Composer keyboard checks

The `composer-keyboard` suite in the [native UI runner](run-ui-tests.ps1) exercises chat (both authentication hosts), images, video, and speech in both themes using real Windows keyboard input. It checks Enter-to-send, native Shift+Enter insertion and selection replacement, existing multiline drafts, and unavailable sends. Run it in an interactive desktop session with the test window in the foreground and no modifier keys held; the suite refuses to inject keys if those conditions are not met. Avoid typing or switching windows during the run. A failed focus/injection check is not evidence that keyboard behavior passed.

For manual IME coverage, compose text with a Japanese or Chinese input method in each prompt. Enter must confirm the active composition without submitting; a subsequent Enter sends. Also verify multiline text paste and chat attachment paste shortcuts remain native.

## Client/runtime dependency boundary

The [dependency check](check-dependency-boundary.cjs) runs automatically with the standalone checks. It follows desktop project and solution references (including Vercel's project references), rejects missing or server-side projects, and guards against server-model/mapper imports in desktop source. References used only to publish or launch the bundled runtimes are deliberately allowed.

The audit found no desktop use of Agents Common types or extension methods and no public desktop signatures exposing them. The existing [agent tests](AIHappey.Desktop.Tests/AgentRegressionTests.cs) cover JSON round-trips, nested unknown properties, omitted versus null/empty fields, storage, and complete inline HTTP request payloads. [Chat-error tests](AIHappey.Desktop.Tests/ChatErrorRegressionTests.cs) use the public Vercel wire models, not Unified. The three backend error-mapping assertions now live in the AI repository's backend test project instead of creating a desktop compile-time dependency.

To verify compilation without the Agents source repository, make a fresh sibling layout containing only this desktop repository and the separate Vercel repository. Do not copy previous build/intermediate directories or runtime binaries. On Windows with the required SDKs and package feeds, run from the desktop repository:

```powershell
dotnet build AIHappey.Desktop.slnx -c Debug
dotnet build Samples/AIHappey.Desktop.HeaderAuth/AIHappey.Desktop.HeaderAuth.csproj -c Debug -p:Platform=x64
dotnet build Samples/AIHappey.Desktop.AzureAuth/AIHappey.Desktop.AzureAuth.csproj -c Debug -p:Platform=x64
node Tests/check-dependency-boundary.cjs
dotnet run --project Tests/AIHappey.Desktop.Tests --no-build -- --agents-only
dotnet run --project Tests/AIHappey.Desktop.Tests --no-build -- --chat-errors-only
dotnet run --project Tests/AIHappey.Desktop.Tests --no-build -- --catalog-only
```

Use the solution's default platform; x64 is selected explicitly for direct host builds. Inspect the restored project graph to confirm that all project references resolve within those two repositories and that Agents/Unified implementation dependencies are absent. Ordinary builds can use the checked-in provider-catalog snapshot without a browser checkout. Full bundle publishing is a separate operation that intentionally builds the runtime services from source; it is not the source-independent client compilation check.

## Coverage limitations

The signed installer lifecycle is deliberately not installed on a development workstation. It requires signed release and upgrade packages, a compiled native Explorer probe, and an explicitly disposable, interactive Windows 11 environment. The consolidated summary records this as blocked, not passed. See the packaging documentation for its guarded lifecycle runner and additional manual deployment evidence.

The optional bundled-runtime integration requires an actual published runtime directory, supplied as the existing positional argument to the core runner. Credential-free protocol and in-memory transport tests do not prove live provider availability, microphone/camera permissions, or real enterprise authentication. These are not claimed as passed by a source audit.

No resource generators run as part of the consolidated checks: synchronization commands use their read-only check mode. The existing build target may synchronize the provider snapshot from the neighboring browser checkout, so review the working tree after a build.
