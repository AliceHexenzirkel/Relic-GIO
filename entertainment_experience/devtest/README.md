# DevTest: the dev-only in-game test bed

A DevTest build of the enhancements DLL (`entertainment_experience/tools/build_ee.ps1 -Config DevTest`) serves a file
channel inside the game: a script on this PC controls the running client and reads it in-process — status, waits,
screenshots of the back buffer, teleports, the free camera, entities, the game's UI tree and clicks, transient feature
switches, quit. No OS input, no window focus, nothing typed. The design is in docs/ENTERTAINMENT-EXPERIENCE.md
("DevTest").

**Never in a release.** The DevTest build carries a marker every release gate refuses (`build/pe_imports.ps1`
`Test-DevBuildMarker`, `publish.ps1`, `payload_manifest.py`, `dlls.yml`, the `devtestgate` spike). Nothing in this
folder ships: the runner is in no solution Relic.App builds, and `publish.ps1` publishes only Relic.App.

## The pieces

| Piece | What it does |
|---|---|
| `runner/` — `DevTestRunner` | Plays ONE session of an installed version through Relic's own `PlaySession` (gate, profile swap, Fiddler, payload, injected launch, post-exit decision) with the DevTest DLL, from a staged copy of the repository's payload; then puts the release DLL and the slot's screen values back. Opens no window, asks nothing. |
| `build/devtest_driver.py` | The channel (`cmd.json` → `results/<id>.json`, `status.json`) and a command line for every op. |
| `build/devtest_run.py` | A scenario end to end: preflight, the runner started detached, the steps and their checks, quit, the report. |
| `scenarios/*.json` | The scenarios (`m-dt1-28-smoke`, `m-dt2-28-*`); `scenarios/steps/*.json` are step blocks they include. |

## Running a scenario

```powershell
$env:RELIC_DEVTEST_HOST  = 'game.example.com'           # the server Relic must point at (checked, never written)
$env:RELIC_DEVTEST_AGENT = 'http://192.0.2.10:18080'    # the agent, for gm steps
$env:RELIC_DEVTEST_TOKEN = '<the agent token>'          # read from the environment only, never printed or stored
.\entertainment_experience\tools\build_ee.ps1 -Versions 28 -Config DevTest
python -B build\devtest_run.py entertainment_experience\devtest\scenarios\m-dt1-28-smoke.json
```

The report lands in `D:\relic-devtest\reports\<scenario>-<yyyymmdd-hhmmss>\` (`report.html`, `report.json`, `shots/`,
`runner.log`, `devtest.log`, `board.txt`, `output_log.tail.txt`); the exit code is the number of failed checks. The
login scenario also reads `RELIC_DEVTEST_ACCOUNT` / `RELIC_DEVTEST_PASSWORD`. A game that already runs with the
DevTest DLL is driven with `--no-launch`; `--keep-game` leaves the game open at the end.

The secrets stay in this script: every `RELIC_DEVTEST_*` variable, and the one `server.tokenEnv` names, is taken out
of the environment of every program it starts (the runner, and through it launcher.exe, the game and Fiddler). The
game is closed through the channel whenever a DevTest build was seen serving it — after a failed step, a Ctrl+C, the
wrong build, or a game that opened fullscreen although the runner asked for a window (`status.screen`, else the size
of the back buffer: such a game is closed at once, its steps skipped). A failed step skips the steps after it but
for those marked `"always": true` — the cleanup a scenario owes the server, such as a GM switch put back; what it
changes in the game itself, the final quit puts back.

One command at a time by hand: `python -B build\devtest_driver.py --version 2.8 ping` (or `status`, `shot --collect
<folder>`, `teleport X Y Z`, `ui-tree`, ...; `--help` lists them).

## Before the first run (the owner, once)

- Relic points at the test server (start screen); the runner only checks `--expect-host`.
- Fiddler is ready (one normal PLAY from Relic sets it up, the certificate consent included): the runner refuses
  rather than open a consent dialog.
- Relic's *In-game enhancements* setting is on, and the version was played once from Relic (its injector recorded).
- The live profile is loaded (no session left half-finished): `DevTestRunner --tray` starts the installed Relic,
  whose recovery puts it back.

## What a session changes, and puts back

- `D:\relic-devtest\stage\payload\` — the staged payload, rebuilt before every session (only that folder; the build's
  own `-StageDev` copies beside it are left alone). The repository's `payload\` and the installed launcher's are read,
  never written.
- The slot's `genshin.reg` (`%LOCALAPPDATA%\Relic\profiles\<slot>\`): the three Unity screen values set to a window
  (`--window 1600x900`, default) after a byte copy `genshin.reg.devtest-orig`; after the session the owner's three
  values go back into the slot's new export and the copy is deleted. Nothing else of the file, and never the live
  registry key.
- `<GameDir>\ayy\anime\ee\cfg-devtest.json` — the dev build's own config, from the scenario's `config` block (the
  owner's `cfg.json` is never read or written by a dev build).
- `<GameDir>\ayy\anime\ee\CLibrary.dll` — the DevTest DLL during the session; afterwards the release DLL from the
  installed launcher's payload (read only), checked by hash and for the marker — under the session gate, so a PLAY
  started right after the session never races it.
- `<GameDir>\ayy\anime\ee\devtest\` — the channel the DLL serves: `status.json`, `results\`, `devtest.log`, and
  `shots\`, whose pictures leave the game folder once collected (every shot of a scenario, `shot --collect`); a
  `cmd.json` is taken back once answered, also when the driver is interrupted.
- `HKCU\...\RunOnce\RelicRecover` — Relic's logon recovery, armed while the session's profile is loaded. The runner
  points it at the installed `Relic.exe --tray` as soon as the game is up; only without an installed Relic does it
  keep naming `DevTestRunner.exe --tray` in the runner's build folder, which must then stay until live is back.

## Runner exit codes

`0` done · `1` the session failed · `2` refused before anything changed · `3` the release DLL is not back
(`DevTestRunner restore --version <v>` once the game is closed) · `4` the screen values could not be put back (the
`.devtest-orig` copy keeps them) · `5` the session's profile is still loaded — a client was running when the session
ended: Relic's recovery puts the live profile back (the installed Relic's tray instance, its next start, or
`DevTestRunner --tray`), then `DevTestRunner restore-window --version <v>` when the screen values wait for it ·
`6` Fiddler is still running after the session · `64` usage. The runner's result file names what it found after the
session (`releaseDll`, `windowRestored`, `liveProfileBack`, `fiddlerRunning`), and the scenario runner checks each.

## Self-tests (no game)

```powershell
dotnet build entertainment_experience\devtest\runner\DevTestRunner.csproj -c Release
entertainment_experience\devtest\runner\bin\Release\net10.0-windows\DevTestRunner.exe selftest
python -B build\devtest_driver.py --selftest
python -B build\devtest_run.py --selftest
```
