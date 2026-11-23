# Versions, releases and updates

WpfStudio's version lives in one place, `Version.props`, and every project under `src/` takes it from there. Merging a pull request raises it, a workflow you run by hand publishes it as a GitHub release, and **Help > Check for updates** installs that release over a published package and starts it again.

| What | Where |
| --- | --- |
| The version | `Version.props` (`<VersionPrefix>`), read by `tools/Get-Version.ps1` |
| Build and test every pull request | `.github/workflows/ci.yml` |
| Raise the version when a pull request merges | `.github/workflows/version.yml`, using `tools/Bump-Version.ps1` |
| Publish a release | `.github/workflows/release.yml`, using `tools/Publish.ps1` |
| Install a release from inside WpfStudio | **Help > Check for updates** (`src/WpfStudio.Core/Updates`, `src/WpfStudio.App/Features/Updates`) |

## Checks on every pull request

**CI** restores, downloads the pinned debugger (`tools/Get-Debugger.ps1`), builds `WpfStudio.sln` in Release and runs every test project on `windows-latest`, for each pull request and each push to `main`. A newer push to the same pull request cancels the check still running. When tests fail, their `.trx` results are kept as the run's `test-results` artifact for two weeks.

## The version rises with each merged pull request

When a pull request is merged into `main`, the **Version** workflow raises `Version.props` and pushes the change to `main` as `Bump version to 0.4.3 (#57) [skip ci]`, committed by `github-actions[bot]`. The pull request's labels choose how much:

| Label | 0.4.2 becomes |
| --- | --- |
| none | 0.4.3 |
| `version:minor` | 0.5.0 |
| `version:major` | 1.0.0 |
| `version:skip` | 0.4.2, unchanged |

Create the three labels once under **Issues > Labels** (any colour). `version:skip` suits a pull request that should not count, such as a typo fix, or one that sets `Version.props` itself.

Pull requests never change the version on their own branch, so open pull requests do not conflict with one another over it. Merges are raised one at a time, in the order they merged; if another merge lands while the workflow runs, it starts again from the new `main`. A pull request closed without merging changes nothing.

The workflow runs on `pull_request_target`, so that pull requests from forks raise the version too. That is safe here because it never checks out or runs the pull request's code: it checks out `main` as merged and runs only `main`'s own scripts, and the labels reach the script as data in an environment variable.

**If `main` is protected**, the workflow's own token may not push to it. Either allow GitHub Actions to bypass the rule (**Settings > Rules** or **Branches**), or create a fine-grained personal access token for this repository with **Contents: Read and write** and save it as the repository secret `VERSION_BUMP_TOKEN`; the workflow uses that token when it exists. A push made with a personal token starts other workflows; `[skip ci]` in the message keeps CI from running for the one-line change.

To raise the version without a pull request, run the script and commit `Version.props`:

```powershell
./tools/Bump-Version.ps1 -Part minor
```

## Publishing a release

Open **Actions > Release > Run workflow** on GitHub, choose the branch (normally `main`) and run it. From a terminal with the GitHub CLI:

```powershell
gh workflow run release.yml --ref main
```

The workflow reads the version from `Version.props` and refuses to run when the tag `v<version>` or a release of that name exists already; merge a pull request, or raise the version by hand, first. Then it:

1. runs `tools/Publish.ps1 -Zip`, which runs every test suite, publishes the portable package for the installed .NET 10 Desktop runtime and checks it is complete;
2. runs it again with `-SelfContained`, without repeating the tests;
3. writes `SHA256SUMS.txt` with the hash of each zip;
4. creates the tag `v<version>` on the commit it built and the release **WpfStudio &lt;version&gt;**, with notes GitHub generates from the pull requests merged since the previous release, and attaches:
   - `WpfStudio-<version>-win-x64.zip`
   - `WpfStudio-<version>-win-x64-self-contained.zip`
   - `SHA256SUMS.txt`

Its options:

| Option | Effect |
| --- | --- |
| **prerelease** | Marks the release a pre-release. Check for updates never offers one. |
| **draft** | Saves a draft to read over and publish from the Releases page. Nothing is offered until it is published. |
| **run-tests** | On by default. Turn it off to package a commit whose tests already passed in CI. |

Both packages carry `WpfStudio.package.json`, which names the kind of package and the repository its releases come from (`tools/Publish.ps1 -ReleaseRepository`, which the workflow sets to the repository it runs in, so a fork publishes and updates from itself).

## Help > Check for updates

**Help > Check for updates** (also in the command palette, and the **Updates** tab of **Help > About WpfStudio**) asks GitHub for the repository's latest release: the newest that is neither a draft nor a pre-release. Nothing is sent until you ask; there is no check in the background. The request carries no sign-in and names only `WpfStudio/<version>` as its user agent, and it goes through the proxy in **Settings > Network**. GitHub allows about 60 such requests an hour from one address; past that, the page says when to try again.

When the release is newer than the running version, the page shows its notes and **Update and restart**:

1. WpfStudio downloads the zip that matches the running package (the self-contained one for a self-contained package) into `%LOCALAPPDATA%\WpfStudio\updates\<version>`, showing its progress; **Cancel** stops it. The download is refused unless its SHA-256 hash matches the one GitHub lists for the file, or failing that the release's `SHA256SUMS.txt`, and unless it holds a package of the same kind.
2. It closes the way the close button does: unsaved files and SQL queries are asked about first, and **Cancel** keeps WpfStudio open with the update ready (**Restart to update** installs it later). The workspace and open files are saved for the next start.
3. The downloaded package's own `WpfStudio.exe` starts with `--apply-update`, waits for WpfStudio to exit, and shows a small **Updating WpfStudio** window while it replaces the installed files.
4. The updated WpfStudio starts, reopens the workspace and files, and says **Updated WpfStudio from 0.4.2 to 0.4.3** in the status bar.

Each file that is replaced is first moved into `.update-backup` inside the package folder, which works even when a worker process that has not quite exited still has it open. If any file cannot be replaced within 30 seconds, every change is undone, the previous version starts again, and a message says why. When an update completes, its backup is marked complete, and WpfStudio deletes complete backups and the downloaded files the next time it starts. If an undo could not put every file back, the message names the backup folder that holds them, and that backup is kept until you remove it.

Every WpfStudio window shares `updates`. A window that is downloading an update, or holds one ready to install, keeps it for itself until it closes: other windows neither remove nor download over it, and **Restart to update** in a window whose download was removed anyway offers to download it again rather than closing. Files that only the old version had are left in place: the folder may hold things that are not WpfStudio's, such as a data folder set with `WPFSTUDIO_DATA_DIRECTORY`.

If the download finishes while the Updates page is closed, WpfStudio does not close under you: the status bar says the update is downloaded, and **Help > Check for updates** shows **Restart to update**.

**When WpfStudio cannot update itself**, the page says why and offers **Release page** instead:

- It runs from a build of the source (`dotnet run`, or `bin\Debug`), which has no `WpfStudio.package.json`. Pull and build, or download the release.
- Your Windows account cannot write to its folder, for example under `C:\Program Files`. Move the folder somewhere you own, or replace it by hand.
- The release has no zip for this kind of package.

**Restart to update** also waits while another WpfStudio window runs from the same folder, since its files would be replaced under it.

## Checking a release by hand

The update path can only be exercised end to end on Windows with two published releases. After publishing a release `N`:

1. Download `WpfStudio-<N-1>-win-x64.zip` from the previous release, extract it somewhere you own, start it and open a solution with an unsaved edit.
2. **Help > Check for updates** should offer `N` with its notes. Choose **Update and restart**, check the progress, then choose **Cancel** at the save prompt: WpfStudio stays open and the page offers **Restart to update**.
3. Choose **Restart to update** and save: the **Updating WpfStudio** window appears, then version `N` starts with the same solution and files and says so in the status bar. **Help > About** shows `N`.
4. Check that `.update-backup` is gone from the folder and `%LOCALAPPDATA%\WpfStudio\updates` is empty, and that `Get-FileHash` of the downloaded zip matches `SHA256SUMS.txt`.
5. Repeat with the self-contained package, and once with a file in the folder held open (for example `Get-Content -Wait` on one of its `.json` files in another window) to see the update undone and the previous version start again.
