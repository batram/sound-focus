# SoundFocus

Global hotkey that jumps to the window — or browser tab — of whatever app is currently
making sound. Press it again to cycle through the others if more than one is noisy, and a
second hotkey takes you back to what you were doing.

- **Alt+Shift+D** — go to sound. 
- **Alt+Shift+F** — go back to what was focused before

- Default hotkeys can be changed via command-line arguments `--hotkey ctrl+alt+f9` `--return-hotkey ctrl+alt+f8`

<p align="center">
  <img align="center" width="576" height="200" alt="image" src="https://github.com/user-attachments/assets/3112eda0-a85e-4335-a95f-76568e9d2771" />
</p>

## Build

```powershell
dotnet build -c Release
```

Output: `bin\Release\net48\SoundFocus.exe` — a single ~130 KB exe, no files to ship
alongside it.

Requires only the .NET SDK; no Visual Studio, no Windows SDK, no targeting pack. It
targets **net48** on purpose: .NET Framework 4.8 ships in-box with Windows 10 and 11, so
the result runs anywhere without installing a runtime, and the WinRT projection this app
reaches by reflection is present there. `Microsoft.NETFramework.ReferenceAssemblies`
supplies the reference assemblies, so nothing outside the repo needs to exist on the
machine. If `SoundFocus.ico` is missing, the build draws it first via `make-icon.ps1`.

## Run

```powershell
cd bin\Release\net48

.\SoundFocus.exe                       # tray app: Alt+Shift+D jumps, Alt+Shift+F goes back
.\SoundFocus.exe --hotkey ctrl+alt+f9  # any ctrl/alt/shift/win + letter or F-key
.\SoundFocus.exe --return-hotkey ctrl+alt+f8
.\SoundFocus.exe --no-tray             # headless: hotkeys only, no icon, no menu, no balloons
.\SoundFocus.exe --list                # print what is making sound right now, then exit
.\SoundFocus.exe --menu                # print what the tray menu would list
.\SoundFocus.exe --debug               # raw per-session dump (devices / state / peak / pid)

.\SoundFocus.exe --log                 # trace tray clicks and menu behaviour to %TEMP%\soundfocus.log
.\SoundFocus.exe --log C:\path\to.log  # ...or somewhere of your choosing

.\SoundFocus.exe --test-tab            # select the playing tab without raising its window
.\SoundFocus.exe --no-tab              # skip UI Automation, windows only
.\SoundFocus.exe --mute-label "Tab stummschalten"   # non-English Firefox

# fallback for apps whose tabs cannot be reached: type an in-app shortcut instead
# (Firefox and Chromium tabs are reached directly, so they need none of this)
.\SoundFocus.exe --send example.exe=ctrl+alt+j
```

Jumping to the playing **tab** is on by default — no extension needed. `--send exe=chord`
may be repeated for different apps. A `--send` chord equal to the global hotkey is refused:
it would be swallowed by our own hotkey and retrigger SoundFocus instead of reaching the app.

## Autostart

```powershell
powershell -NoProfile -File install-autostart.ps1
powershell -NoProfile -File install-autostart.ps1 -Arguments '--hotkey ctrl+alt+f9'
powershell -NoProfile -File install-autostart.ps1 -Remove
```

Writes a shortcut into the per-user Startup folder — no admin rights, nothing in the
registry, removable with `-Remove`. It builds the exe first if it is missing.

## Going back — Alt+Shift+F

Stop or change what is playing, then return to what you were doing. The return hotkey
focuses the window you were on before SoundFocus moved you, and from then on toggles
between the two.

Cycling does **not** overwrite the saved origin: pressing the jump hotkey several times to
step through noisy windows still returns you to the thing you were actually working on,
not to the previous noisy window. The tray menu shows the destination by name
(`Back to <window title>`), so it is never a blind jump.

Note that `Alt+Shift+F` is *free as a global hotkey* — verified with `RegisterHotKey`, no
other app claims it — but VS Code and Visual Studio use Shift+Alt+F for Format Document as
an **in-app** shortcut. A global hotkey wins, so those editors will stop seeing it. If you
format code that way, pick something else with `--return-hotkey`.

## Tray

`--no-tray` runs headless: the two hotkeys and nothing else — no icon, no menu, and the
balloon notifications degrade to silence (they still go to `--log` if enabled). Since
there is no Exit menu item, stop it with `taskkill /im SoundFocus.exe` or Task Manager.

Left-click jumps, exactly like the hotkey. Right-click opens a menu that is **rebuilt every
time it opens** — the list is only true at the moment it is shown.

Two Win32 details keep the tray menu honest, both found by tracing real clicks with `--log`:

- **The menu is built and its window handle forced into existence at startup.** A
  `ToolStripDropDown` that is still empty and handle-less when `Show` is first called
  swallows that first call: the `Opening` handler runs and fills the menu, but the display
  attempt is already lost — so the first right-click did nothing and only the second
  worked. Notably it is *not* the usual foreground-activation story: the trace showed the
  app foreground, `SetForegroundWindow` succeeding and `Opening` firing, with `Opened`
  never following.
- **It is shown by hand after taking the foreground**, not via
  `NotifyIcon.ContextMenuStrip`, with a dummy message posted afterwards. That is what makes
  the menu close when you click away from it.

```
Playing now
  vlc.exe   —  Huntrix - Golden (Lyrics) KPop Demon Hunters.m4a - VLC…
  firefox.exe  —  Harry Styles Saved Us - YouTube
  ─────────────
  Jump to sound (alt+shift+d)
  ─────────────
  Exit
```

Each entry jumps straight to that one window or tab, bypassing the cycle. Entries show the
**tab** title when a tab was identified, otherwise the window title. When nothing is
listed, the menu distinguishes "(nothing)" from "(no active playback device)" — silence and
having no working speakers look identical otherwise.

The **History (last observed)** section keeps the five most recently audible apps/sources,
including a non-clickable **System sounds** entry when Windows plays a system sound.
System sounds cannot identify the individual app that requested a toast. Entries are
newest first, with the local date and time sound was last detected. Repeated observations
update the same process entry. History is collected even while the menu is closed and
entries already shown under Playing now are hidden from History until they go quiet. It
lasts for the current SoundFocus session. Processes that have exited remain visible as
disabled entries; running apps with an available window can still be clicked after they
go quiet. History retains the resolved tab title when available, otherwise the observed
window title. Clicking an entry searches that process's existing tabs for an exact,
case-insensitive title match after removing browser audio/memory status suffixes. It
selects a matching tab, or focuses the app if none matches within two seconds. This does
not reopen closed tabs. History still keeps one entry per process; changed titles may
no longer match, and identical titles select the first matching tab.

The icon is generated by `make-icon.ps1` (a speaker with sound waves, one accent blue that
stays legible on dark and light taskbars) into an 8-size `.ico`, 16px through 256px. It is
linked into the exe twice: as a Win32 resource so Explorer shows it, and as an embedded
resource the tray loads, so the tray gets the real 16px frame instead of a downscaled 32px
one. The build regenerates it if the file is missing.

## How it works

- Enumerates **every active render endpoint** once, subscribes to new-session and
  session-state callbacks, and retains a registry keyed by endpoint and session instance.
  A 30-second reconciliation repairs missed events without rebuilding healthy managers.
- Device callbacks update the endpoint registry when devices are added, removed or change
  state. An expired session is normal stream teardown, not a device removal.
- A single MTA worker samples active sessions: every 25 ms during the first 750 ms
  after creation/activation, then 50 ms for quiet active streams or 150 ms while recently
  audible. Inactive sessions need no periodic meter reads. With no active streams the
  worker sleeps until an event, history-expiry deadline, or reconciliation is due.
  On a transition to inactive it also reads the final meter value once, catching short
  bursts that ended between timer ticks without adding a permanent fast timer.
- Peak >= 0.0002 confirms non-silent samples. A running but silent stream does not enter
  history. Peak meters retain only the preceding device period: this greatly improves
  short-sound detection, but does not guarantee arbitrarily short transients.
- An app stays in the list for 5 s after it goes quiet, so gaps between tracks or quiet
  passages do not make it vanish mid-press.
- System sounds are retained in history without a window-focus action.
- Audio frequently comes from a helper process with no UI (Chrome/Edge audio service,
  Electron utility processes). The owning process is found by walking up the parent chain
  to the nearest ancestor that actually owns a visible top-level window. Process identity
  and ancestry are captured when a session appears, before short-lived helpers exit.
  Targeted process queries validate creation times at each parent link to avoid PID reuse;
  failed parent queries retain the known source identity. Window resolution happens on a
  separate metadata worker. History can arrive shortly after the sound is detected.
- Windows on **other virtual desktops** are DWM-cloaked, exactly like suspended UWP ghost
  windows. Only the ghosts are filtered out: a cloaked window is rejected solely when
  `IVirtualDesktopManager` says it is on the desktop you are already looking at. Activating
  a window on another desktop is what makes Windows switch to it, with `SwitchToThisWindow`
  as the fallback when `SetForegroundWindow` only flashes the taskbar.
- **Picking the right window of one process.** A browser routes every tab's audio through
  one process that owns all its windows, so the PID cannot tell them apart, and the audio
  session carries no per-window information (no display name — verified). The tiebreaker is
  the now-playing title from the Windows media transport layer
  (`GlobalSystemMediaTransportControlsSessionManager`), matched against each candidate
  window title: exact containment first, then word overlap, needing a score of 0.5. That is
  reached by reflection, since consuming WinRT at compile time needs a Windows SDK winmd
  this machine does not have.
- Cycling: pressing the hotkey while already focused on a noisy window advances to the next
  one; pressing again within 4 s also advances. Order is stable (oldest noisy app first).

## Reaching the *tab*, not just the window

Tabs are not windows, so no Win32 call can activate one — but browsers publish their tab
strip through **UI Automation**, and that is enough to do it directly, with no extension
and no synthetic keystrokes.

The signal is the speaker icon on a playing tab: in the accessibility tree it is a child
button of the tab named **"Mute tab"**. Only an audible tab has it. A *muted* tab says
"Unmute tab" and is deliberately not matched, since a muted tab makes no sound.

Verified in Firefox and in Chromium/Chrome. Chromium publishes both markers — the same
`Mute tab` child button, plus **"Audio playing"** inside the tab's own accessible name
(`Some Video - YouTube - Audio playing - Memory usage - 425 MB`); either one matches. An
idle tab in another window of the same browser carries neither and is correctly skipped.

This is strictly better than matching window titles, because it identifies the tab itself:
it works when the audible tab is a **background tab**, whose title never reaches the window
title. Selecting it uses `SelectionItemPattern.Select()`, and Firefox raises that window on
its own.

Two signals, deliberately ranked. A tab whose accessible **name** contains *"Audio playing"*
is stating a fact and wins outright. A **"Mute tab" button** on the tab only means the tab
*can* make sound: Firefox shows one exactly while a tab is audible, but other apps keep
theirs after the sound stops, so it must never outrank a tab that claims to be playing.

### Making your own app discoverable

Any app with tabs can be found the same way, with no cooperation from SoundFocus. Verified
against an Electron app ([once](https://github.com/batram/once)) by doing exactly this:

1. Mark the tab strip `role="tablist"` and each tab `role="tab"` — those surface as
   `ControlType.Tab` and `TabItem`.
2. When a tab is audible, put **`- Audio playing`** in its accessible name, matching
   Chromium's wording: `aria-label="${title} - Audio playing"`. SoundFocus strips that
   suffix again before showing the title.
3. **Electron: nothing.** Do *not* call `app.setAccessibilitySupportEnabled(true)` for
   this — that switches on the full screen-reader tree for every page, and on a
   60,000-node page it costs seconds of the main process and a hundred megabytes per
   tab. Chromium builds a basic tree by itself the moment a UI Automation client touches
   the window, Electron included; it just arrives a beat later than the first answer.
   That first answer is the bare window, about fifteen nodes, which is what an earlier
   version of this README mistook for "Electron never switches it on". SoundFocus now
   treats a window that answers with fewer than 20 nodes as still warming up and asks
   again a few times, 300 ms apart; measured against an Electron shell the strip is
   there within one second.

The strip search is breadth-first to depth 12 with a 400-node budget, because depth varies
by application: browsers keep their tab strip about four levels down in native chrome,
while an Electron shell draws tabs in HTML and buries them around nine. Measured worst case
for a window with no tab strip at all is ~100 ms, once, then cached — plus up to 1.5 s of
retries for a window that answers sparsely, which only a cold Chromium window does.

Safeguards, since this talks to another process:

- The search runs on its own MTA thread with a hard timeout (2 s), so a busy browser can
  never wedge the hotkey.
- The walk toward the tab strip is depth-bounded (6 levels). The page content's
  accessibility tree can be enormous and must not be descended into. Measured cost of
  finding the strip: 30–60 ms per window.
- Fallbacks in order: audible-tab button → tab whose title matches the now-playing title →
  window-title matching → plain window focus.

### Keeping it fast

Reaching into another process over UI Automation is slow enough to be felt in a menu that
opens on right-click, so three things keep it off the critical path. Measured with three
sources playing across several browser windows:

| | |
|---|---|
| first scan, caches empty | 578 ms |
| rescan, tab strips cached | 5 ms |
| what the menu actually reads | 0 ms |

- **One bulk fetch per tab strip.** A `CacheRequest` over the strip's subtree pulls every
  tab name, control type and child in a single cross-process call. Walking it live with a
  `TreeWalker` instead costs a round trip *per node*, which with a few dozen tabs is what
  made the menu feel slow.
- **The tab strip element is remembered per window** (60 s), including the fact that a
  window has none — that negative result is what makes non-browser windows cheap to skip.
  Locating a strip is a bounded tree walk costing ~60 ms per window, and after the bulk
  fetch it was the whole remaining cost. A stale element (a window that rebuilt its chrome)
  throws on use and is re-found once.
- **The target list is resolved in the background**, and the menu reads that snapshot
  without ever scanning while it opens — a menu that appears half a second after the click
  reads as a click that did nothing. The trade is a list up to one refresh interval old.
- **Audio discovery uses Windows callbacks.** Healthy sessions and managers are retained,
  rather than repeatedly released and recreated. Callbacks enqueue work and retain the
  new session safely; the MTA worker unregisters and releases everything on removal or exit.
- **Peak sampling does no window scans or process-tree enumeration.** Process identity is
  captured once per new session. Subsequent samples update private timestamps; history
  snapshots are copied only for consumers.
- **Metadata cannot block audio detection.** Media transport refresh and tab/title work
  run outside the audio worker. The target worker wakes when the audible app set changes,
  refreshes periodically while apps remain audible, and sleeps when none do.

The timings above describe the earlier tab-cache work, not benchmarks of the new watcher.
Use the integration probe below to compare watcher builds on the same machine.

`--menu` prints these timings, so a regression here is visible rather than merely felt.

### Memory: nothing waits for the garbage collector

Transient UI Automation elements and device enumerators are explicitly released rather
than left to the GC. Audio sessions and managers now live for the duration of their
subscriptions and are released on retirement or shutdown. Audio ownership uses balanced
`Marshal.ReleaseComObject` calls: enumeration and callbacks can refer to the same RCW,
so `FinalReleaseComObject` there would invalidate a still-live reference. This is not
optional hygiene here but the difference between 40 MB and gigabytes: each element is a
tiny managed wrapper around a native cross-process proxy and cached property store, so
thousands of them exert no pressure on a GC that only weighs managed bytes. It simply
never collects, while the native side balloons. Measured on a long-running instance:
**233 MB private memory, of which the managed heap was 1.3 MB** — with 2,132 dead elements
sitting in the finalization queue.

For the same reason the tab hunt uses the raw `IUIAutomation` COM interface rather than
`System.Windows.Automation`: the managed wrapper offers no way to release an element
explicitly, and it drags in ~20 MB of WPF (`PresentationCore`/`WindowsBase`) besides.
After the change, a stress run at 15–20× normal polling holds flat around 38 MB with a
finalization queue of single digits.

`--stress` runs every loop at that accelerated rate, so an hour of drift shows up in a
minute — that flat line is checkable, not folklore.

`--mute-label "<text>"` sets the button label for non-English Firefox builds.
`--no-tab` disables UI Automation entirely. `--test-tab` selects the playing tab without
raising its window, which is handy for checking the mechanism in isolation.

### Fallback: `--send`

If UI Automation is unavailable (`accessibility.force_disabled`) or an app exposes no tab
strip, `--send exe=chord` focuses the window and types a chord into it — e.g. the shortcut
of a browser extension that jumps to the audible tab. It only fires when the tab could not
be reached directly. Two details keep it from being racy: it waits for the target window to
really be foreground and confirms the foreground window belongs to the intended app before
typing anything, and it waits for the physical modifiers of the triggering hotkey to be
released first — otherwise still holding Ctrl+Alt turns an injected Alt+Shift+D into
Ctrl+Alt+Shift+D and the extension never fires.

## Known limits

- Asking a browser for its accessibility tree makes it turn its accessibility engine on,
  which costs it some memory. If a Firefox profile has `accessibility.force_disabled` set,
  tab hunting finds nothing and the fallbacks take over.
- The "Mute tab" label is localised. Non-English builds need `--mute-label`.
- For apps with no tab strip and no media metadata (a game, a notification sound), there is
  no tiebreaker between several windows of one process: they all stay in the cycle so
  repeated presses can reach the right one.

## Tuning

Fields at the top of `AudioWatcher` in `AudioWatcher.cs`:
`Threshold` (0.0002), `MemoryMs` (5000), `BurstPollMs` (25), `BurstMs` (750),
`QuietPollMs` (50), `PollMs` (150), `RescanMs` (30000),
plus `Program.CycleResetMs` (4000).


## Audio detection integration probe

```powershell
dotnet build -c Release -o artifacts/candidate
dotnet build tests/AudioProbe.csproj -c Release
powershell -NoProfile -File tests/run-probe.ps1 -Variant candidate
```

The Windows-only probe plays quiet, brief tones from child processes. It creates an owner
window on a private desktop to verify parent attribution without taking focus. The wrapper
uses the local hidden-desktop helper at `C:\Users\mjb\tools\agent-scripts\Invoke-OnHiddenDesktop.ps1`.
To use another machine, run `AudioProbe.exe <absolute-path-to-SoundFocus.exe> <report-path>`
on a test desktop. The probe tests 20/50/100/571 ms fresh streams, a sound after silence
inside an already-active stream, silent-stream rejection, immutable history snapshots,
system-sound capture, PowerShell SoundPlayer attribution, 80 short-lived silent helpers,
and disposal. It measures watcher-process CPU, handles, private bytes and generation-zero
collections. The 20/50 ms results are characterization, not a guarantee; 100/571 ms and
the delayed 100 ms cases are required to pass. Other apps playing during the run can
affect history capacity and resource measurements.

Reports go to `artifacts/<variant>-probe.txt`. `-Trace trace` enables detailed event/peak
logging for diagnosis; leave it off for resource measurements. `--debug` now reports the
live session registry plus meter-read, wakeup, discovery, retirement, device-scan and error
counters, using the same MTA watcher as normal operation.
