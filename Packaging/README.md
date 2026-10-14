# Windows distributions and native integration

## End-user lifecycle

The portable ZIP and installed application share the same managed application code. The ZIP requires no installation, signing credentials, registry changes, or package registration. Extract it and launch the desktop executable. Existing portable profile locations are unchanged.

The per-user x64 MSIX targets Windows 11. Install a **trusted, signed** package by opening it in Windows App Installer or through standard enterprise package deployment. No administrator elevation is required for ordinary per-user deployment. A valid signature alone is insufficient: its certificate chain must be trusted and local policy must permit installation. Never disable signature checks or install an arbitrary development certificate to work around a production installation failure.

After installation, select one or more filesystem files in Explorer and choose **Attach to [application name]** in the primary Windows 11 context menu. The application opens/restores and attaches the files to the current chat draft. Existing draft text and attachments remain; **no message is submitted and no file is uploaded automatically**. While the app is starting, busy, or displaying a modal dialog, requests wait with a cancel action. Account changes require confirmation; conversation changes can cancel a stale pending selection.

Folders and virtual shell items are not supported. Missing/inaccessible files and files over 25 MiB are reported individually. External batches have a 128 MiB admitted-content budget and at most 1,024 paths. Successful files in a mixed batch remain attached. Files are read into immutable draft snapshots, not linked for later automatic reading.

Explorer always activates the installed host package. Portable and installed distributions use separate instance and state scopes. The two authentication hosts also have distinct package identities. Launching a second installed instance redirects to the existing one and restores its window. A shared portable profile has one writer; a second extracted copy cannot overwrite another copy's active profile.

Install a newer package with the **same package name and certificate publisher** to upgrade. Close the app gracefully first, or use deferred enterprise deployment while it is in use; never force termination of an active chat. Package-local settings, credentials, and history survive upgrades. Downgrades are rejected by normal deployment. A failed package deployment does not replace the previous working version.

Uninstall through Windows Settings / Installed apps. Windows removes installed package files, Start menu/Explorer/COM registration, and package-local state. Export important conversations first. User-selected source files, exported conversations, external media directories, and portable profiles are not deleted. Reinstall begins with fresh package-local state. Portable credentials/history are not silently copied into the installed application.

The enterprise host accepts a user-managed configuration override in its resolved profile directory; see [configuration loading](../Samples/AIHappey.Desktop.AzureAuth/EntraAuthentication.cs:21). Use the existing enterprise configuration format. Package files are immutable, so do not edit configuration under WindowsApps. A clean source checkout contains only an empty configuration fallback and no tenant secrets.

## Local builds

Prerequisites: .NET 10, the desktop project's NuGet feeds and pinned SDK packages, and sibling runtime/provider repositories at the revisions in [the source lock](sources.json). The publishing script still requires the upstream provider catalog; it never substitutes an old snapshot during publishing.

Portable-only builds need no native compiler, certificate, publisher, or installer runner:

```powershell
./publish.ps1 -HostName HeaderAuth -Distribution Portable
```

Building MSIX additionally requires CMake, Visual Studio 2022 C++ build tools, Windows SDK tools, and a code-signing certificate in the current-user personal certificate store:

```powershell
./publish.ps1 -HostName HeaderAuth -Distribution Installer -ReleaseVersion 261014.0631 `
    -Publisher 'CN=Your exact code-signing subject' -CertificateThumbprint $thumbprint `
    -TimestampUrl 'https://your-approved-rfc3161-endpoint'
```

The combined distribution option writes the independently buildable ZIP before attempting installer packaging. A signing failure blocks only MSIX; it does not invalidate or remove the ZIP. Installer-only packaging can reuse a previously built payload with the skip-publish switch, or call [the installer builder](New-Msix.ps1) directly. Common resources and dependencies are hash-checked during staging; first-party PE signatures intentionally differ between an unsigned portable payload and a signed installer payload.

Release names use UTC date/minute versions. Mapping to four-part MSIX versions is defined in [the packaging utilities](Tools.ps1:3): year, numeric month/day, numeric hour/minute, zero revision. Package identity and COM identifiers in [the host identity configuration](hosts.json) are independent of branding. Never change them after releasing a production identity. An existing output version is not overwritten, and retry publication compares existing asset hashes rather than replacing them.

Development packaging requires the explicit development option and a separate identity/COM class. Unsigned packages are permitted only with both development and unsigned options for validation, never production installation/publication. A development certificate must be trusted **only inside a disposable test VM** and removed after testing. No end-user trust installation script is supplied.

## Production CI configuration

[The Windows workflow](../.github/workflows/release.yml) builds on source pushes/PRs and publishes on valid version tags or an explicit version dispatch on the main branch. Dependencies and workflow actions are pinned. Update the source lock deliberately when upstream revisions change.

The portable release job depends only on the shared build. It publishes ZIPs, SHA-256 checksums, and source provenance without signing secrets. Native compilation validates the integration source in the build job, but portable-only local publishing has no native-tool requirement.

MSIX release is opt-in through the installer dispatch input or repository variable ENABLE_MSIX_RELEASE. Configure the protected **windows-signing** environment with required reviewer/branch/tag protections and these inputs:

| Input | Kind | Purpose |
|---|---|---|
| WINDOWS_SIGNING_PFX_BASE64 | Secret | Base64-encoded production code-signing PFX |
| WINDOWS_SIGNING_PFX_PASSWORD | Secret | PFX password |
| WINDOWS_PACKAGE_PUBLISHER | Variable | Exact certificate subject / stable manifest publisher |
| WINDOWS_SIGNING_THUMBPRINT | Variable | Expected certificate thumbprint |
| WINDOWS_TIMESTAMP_URL | Variable | Approved HTTPS RFC 3161 endpoint |
| WINDOWS11_LIFECYCLE_ENABLED | Variable | Set to true only after the isolated runner is provisioned |

Certificate import, first-party executable/package signing, timestamp validation, publisher/chain checking, and unconditional key/file cleanup run on an ephemeral hosted runner. Pull requests never enter signing jobs. Configure environment restrictions in GitHub itself; a workflow cannot provision or enforce its own reviewer policy. Keep the signing identity stable across renewals; changing publisher is a new package family, not an upgrade.

Provision a disposable interactive Windows 11 runner carrying the **disposable-windows11** label and environment AIHAPPEY_DISPOSABLE_WINDOWS11=1. Restrict its **windows11-lifecycle** environment to trusted release refs and require approval. Revert/destroy the VM after each job. Do not expose this runner to untrusted pull requests, and do not substitute hosted Windows Server runners for primary-menu checks.

The signing job creates the release package and a signed higher-version upgrade fixture. The lifecycle gate validates deployment and registered native COM delivery before the installer-release job attaches production MSIX assets to the already independently published portable release. Upgrade fixtures and diagnostic probes are CI test artifacts, not user-facing release assets. Missing signing credentials, failed timestamp/trust checks, or a missing/failing lifecycle runner blocks MSIX publication only.

No auto-update feed or Store submission is configured. Direct installation/enterprise deployment is the supported path. Provision real credentials and runner policy outside the repository before enabling production MSIX releases.

## Tests and evidence

```powershell
dotnet run --project Tests/AIHappey.Desktop.Tests/AIHappey.Desktop.Tests.csproj -- --explorer-only
./Tests/check-packaging.ps1
./Tests/run-ui-tests.ps1 -ExplorerOnly
./Tests/run-installer-tests.ps1 -Package $releasePackage -Probe $nativeProbe `
    -UpgradePackage $higherVersionPackage -DisposableEnvironment
```

The non-visual runner tests request/path/envelope limits, private ACLs, duplicate claims, acknowledgements, expiry, and host/user/distribution isolation. Composer UI checks cover startup delivery, busy/modal queueing and cancellation, mixed-file errors, preserved drafts/navigation, stale context, shutdown, and zero submitted messages. Their host throws if a gateway authentication is attempted.

The lifecycle runner refuses to run unless explicitly marked disposable, interactive, and Windows 11; it refuses to replace an existing installation. It installs real signed packages, invokes the registered COM command, checks cold/warm acknowledgements and instance counts, opens Explorer to verify primary-menu visibility without Show more options, tests deferred/running upgrades, state retention, downgrade and corrupt-package failure, uninstall registration cleanup, and clean reinstall. Cleanup runs even on failure. Keep the desktop session unlocked and English/Dutch while executing the primary-menu check.

Before rollout, also record interactive evidence for multiple-user isolation, both hosts coinstalled, installed/portable coexistence, cloud/UNC/long-path failure cases, minimized-window restoration, and local runtime service shutdown. These environment-sensitive cases must not be represented as passed by a source-text audit. No selected file contents or credentials should be uploaded in diagnostics.

The native extension and signed package lifecycle require native build/deployment tools and actual package trust. Managed builds and request/UI tests do not prove primary-menu registration. Production release gates intentionally remain closed until the actual lifecycle run succeeds.

## API references

The design follows Microsoft's [Windows 11 context-menu guidance](https://blogs.windows.com/windowsdeveloper/2021/07/19/extending-the-context-menu-and-share-dialog-in-windows-11/), [package context-menu schema](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop4-fileexplorercontextmenus), [packaged COM surrogate schema](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-com-surrogateserver), [single-instance WinUI guidance](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-single-instance), and [MSIX signing guidance](https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview). Shell code remains native and package-registered; request processing and all chat behavior remain in the shared application codebase.
