# LogViewer

A Windows desktop log viewer (WPF, .NET 8) for reading plain-text and [CMTrace](https://learn.microsoft.com/mem/configmgr/develop/core/understand/cmtrace)-formatted log files, including live-tailing very large (multi-GB) files without loading them fully into memory.

## Solution layout

```
LogViewer.sln
src/
  LogViewer.Core/     Engine: encoding detection, CMTrace/plain-text parsing, bounded-memory
                       file indexing, filter engine, CLI argument parsing. No UI dependencies.
  LogViewer.App/       WPF application (views, view models, App/MainWindow startup, drag & drop).
tests/
  LogViewer.Core.Tests/  xUnit tests for LogViewer.Core (37 tests).
```

## Building

Requires the **.NET 8 SDK** and, on Windows, the WPF desktop workload (installed automatically with
the SDK via `Microsoft.WindowsDesktop.App`). Only `LogViewer.App` needs Windows; `LogViewer.Core` and
its tests are plain .NET and can be built/tested on any OS.

```powershell
dotnet build LogViewer.sln
dotnet test tests/LogViewer.Core.Tests/LogViewer.Core.Tests.csproj
```

## Running

```powershell
dotnet run --project src/LogViewer.App/LogViewer.App.csproj
```

or run the built executable directly: `src/LogViewer.App/bin/Debug/net8.0-windows/LogViewer.App.exe`.

### Publishing a single portable .exe

To produce one self-contained `LogViewer.App.exe` (no separate .dll files, no .NET runtime needed on
the target machine) for distribution:

```powershell
dotnet publish src/LogViewer.App/LogViewer.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o publish/LogViewer
```

This produces `publish/LogViewer/LogViewer.App.exe` (~68 MB, since it bundles the .NET 8 runtime) that
runs standalone on any 64-bit Windows machine. Omit `-p:DebugType=none` to keep a `.pdb` alongside it
for debugging. The `publish/` folder is git-ignored.

You can also open files by:
- Dragging one or more files from Windows File Explorer onto the window (drops on any part of the
  window are accepted, not just the tab strip).
- The **Open...** toolbar button / <kbd>Ctrl+O</kbd> (multi-select file dialog).
- Command-line arguments (see below).

## Command-line usage

```
LogViewer.App.exe [-File <path> [-File <path> ...]] [-Highlight <text>] [-Hide <text>] [-Filter <text>]
LogViewer.App.exe --help
```

| Option | Meaning |
|---|---|
| `-File <path>` | Open `<path>` in a new tab. Repeat `-File` to open several files at once. A bare path with no preceding switch is also accepted, e.g. `LogViewer.App.exe C:\logs\a.log` (useful for shell "Open with" registration). |
| `-Highlight <text>` | Highlight rows containing `<text>` (case-insensitive substring match). |
| `-Hide <text>` | Hide rows containing `<text>`. |
| `-Filter <text>` | Show only rows containing `<text>`. |
| `-Help`, `--help`, `-h`, `/?` | Print usage and exit without opening a window. |

Notes / exact semantics:
- `-Highlight` / `-Hide` / `-Filter` are applied as the **initial** filter state of every tab opened
  from this command line. Tabs opened later via drag-and-drop or the Open dialog start with empty
  filters, independent of any command-line filters. Each tab's filter state (raw/parsed view, follow
  state, Highlight/Hide/Filter text) is otherwise fully independent per tab.
- Matching is a simple case-insensitive **substring** match (no regex): e.g. `banan` matches both
  `bla bla banan` and `bla bla Banan bla bla`.
- The three filters combine as **Filter → Hide → Highlight**: Filter first restricts to only matching
  rows, then Hide removes rows from what remains, then Highlight colors rows in what's left. An empty
  field disables that filter.
- Quote values containing spaces or special characters, e.g. `-Filter "connection error"`.
- `--help` prints to the console when launched from one (e.g. `cmd`/PowerShell with output visible);
  if no console is attached it shows a message box instead, since a GUI app has no console by default.

Examples:

```powershell
LogViewer.App.exe -File "C:\Windows\CCM\Logs\ccmexec.log" -Highlight error
LogViewer.App.exe -File a.log -File b.log -Filter download -Hide heartbeat
LogViewer.App.exe C:\logs\app.log
```

## Feature overview

- **CMTrace + plain text**: Files are auto-detected as CMTrace (`<![LOG[...]LOG]!><...>` records) or
  treated as plain text (one record per line) otherwise. A toolbar toggle switches each tab between a
  **Raw** view (original line/record text) and a **Parsed** view (Timestamp / Component / Severity /
  Thread / Message columns) for CMTrace files. Both views read from the same underlying record data,
  so filter matching is identical regardless of which view is displayed.
- **Three independent filters** (Highlight/Hide/Filter), combined as Filter → Hide → Highlight, as
  described above.
- **Multi-file tabs**: one tab per open file; open several at once via drag-and-drop from Explorer, the
  Open dialog (multi-select), or repeated `-File` arguments.
- **Live tailing**: each tab polls its file in the background (roughly twice a second, and immediately
  after a filter/text change) and appends new records as they appear. **Follow** (a toggle in the
  toolbar) keeps the view scrolled to the newest record; toggling Follow off pauses auto-scrolling so
  you can read older content, but ingestion/indexing continues in the background regardless of Follow
  state - toggling Follow back on jumps straight to the current tail. Manually scrolling (scrollbar or
  mouse wheel) automatically pauses Follow.
- **Very large files (multi-GB)**: see "Large file design" below.
- **Read-only, non-destructive access**: files are always opened with
  `FileShare.ReadWrite | FileShare.Delete` and never written to. If another process holds an
  incompatible lock (a genuine exclusive/deny-read lock, which this sharing mode cannot bypass), the
  tab shows a clear error banner with a **Retry** button instead of crashing or blocking the UI.
- **Robust to log rotation/truncation**: shrinking file size or a changed file creation time triggers a
  full re-scan from the start (see "Known limitations" for the heuristic's edge cases); a still-growing
  final record (e.g. mid-write CMTrace entry) is shown immediately and grows in place as more of it is
  written, without being double-counted once it's followed by a new record.

## Large file design (bounded memory)

`LogFileIndex` (in `LogViewer.Core/IO`) is built so that memory use does not grow proportionally with
file size:

- It never stores per-line/per-record text. Instead, it keeps a **sparse checkpoint table**
  (record index → byte offset), one entry every 2048 records.
- Reading any window of rows (`GetRecords(startIndex, count)`) seeks to the nearest checkpoint at or
  before `startIndex`, then re-scans forward from disk, decoding only the requested window. This means
  scrolling to an arbitrary position in a multi-gigabyte file only ever holds a small window of decoded
  rows and a small, bounded number of checkpoints in memory - not the whole file, and not a growing
  in-memory line index.
- Growth (new data appended) is detected incrementally: `Refresh()` only scans the **new bytes** since
  the last known length, extending the checkpoint table without re-reading from the start.
- The WPF view (`LogTabView`) mirrors this at the UI layer: instead of relying on WPF's built-in list
  virtualization (which still requires an `IList` sized to the full row count), it keeps only a small
  materialized window of row view-models (viewport size + a small buffer) and pairs it with a
  `ScrollBar` sized to the full (possibly filtered) row count. Scrolling reloads just that window via
  `LogTabViewModel.LoadWindow`, off the UI thread for the actual disk I/O, keeping the UI responsive.
- Filtering (Highlight/Hide/Filter) does not require scanning the whole file up front:
  `FilteredRecordIndex` builds its list of matching record indices **incrementally**, in small bounded
  batches, on a background loop, so the UI stays responsive immediately after typing a filter and
  matches are found progressively - including matches far beyond the initially-loaded tail of a large
  file. When no Filter/Hide text is active, `FilteredRecordIndex` is a zero-overhead passthrough
  straight to `LogFileIndex`.
- All slow I/O (file scanning/refresh, filter match building) runs on a background `Task` per tab, with
  cancellation on tab close, and is debounced on filter text changes so fast typing doesn't trigger a
  full re-scan per keystroke.
- Honest scaling caveat: memory for the **filtered match list** scales with the number of matching
  records, not file size. A filter that matches nearly every line of a huge file will still use
  memory proportional to the match count (though not the full row text) - this is a deliberate,
  documented trade-off rather than a fully unbounded design, since an exact O(1) "matches beyond the
  visible window" index would require a much more complex disk-backed structure. In practice, filters
  used to narrow down interesting events (the common case) keep this small.

## Encoding support

- Detected via BOM: UTF-8 (with BOM), UTF-16 LE, UTF-16 BE.
- Files without a BOM are decoded as UTF-8, which is also correct for plain ASCII text - this covers
  the vast majority of Windows application/CMTrace logs.
- **Known limitation**: legacy code-page (e.g. Windows-1252/ANSI) files *without* a BOM are not
  auto-detected and will be decoded as UTF-8, which can produce replacement characters for non-ASCII
  bytes (e.g. some accented characters). Only BOM-tagged UTF-16 and UTF-8, plus ASCII-range no-BOM
  text, are guaranteed correct.
- Long/multiline CMTrace records (e.g. an embedded stack trace inside the message field, which is a
  legitimate embedded newline *inside* the `<![LOG[...]LOG]!>` message before its closing tag) are
  parsed as a single record; the line-splitting/record-boundary logic is encoding- and
  newline-alignment aware (including 2-byte-aligned splitting for UTF-16) so multi-byte sequences and
  multi-line records are not corrupted or split mid-character.

## Known limitations

- **Locked files**: if another process holds a lock incompatible with
  `FileShare.ReadWrite | FileShare.Delete` (a genuine deny-read/exclusive lock), LogViewer cannot open
  it - by design, it never attempts to bypass OS file locking. The tab shows the error with a Retry
  button so you can retry once the lock is released.
- **Rotation/truncation detection is heuristic**: detected via file shrinking or a changed
  `CreationTimeUtc`. A same-size file replaced in place with new content of the exact same size and an
  unchanged creation time (uncommon, but possible with certain rotation tools) could be missed until
  further growth or a size mismatch is observed.
- **No regex/advanced search**: filters are deliberately simple case-insensitive substring matches
  only, per the current requirements - no regex, wildcards, or multi-term boolean queries.
- **No settings persistence**: filter text, view mode, and follow state are per-tab and per-session
  only; nothing is saved between runs (deliberately - see "Choose sensible defaults rather than adding
  unnecessary configuration" in the project's guiding requirements).
- **No log level colorization beyond Highlight/severity column**: parsed CMTrace severity is shown as a
  column/text, not full row background colorization by severity (only the explicit Highlight filter
  colors rows).

## Tests

`tests/LogViewer.Core.Tests` covers: CMTrace parsing, the Filter/Hide/Highlight combination logic,
command-line argument parsing, and - most importantly - `LogFileIndex`/`FilteredRecordIndex` behavior:
incremental growth, truncation/rotation resets, UTF-16 handling, bounded checkpoint counts on a
20,000-line file, and incremental (batch-limited) filtered match building. All 37 tests pass
(`dotnet test tests/LogViewer.Core.Tests/LogViewer.Core.Tests.csproj`).

The WPF app itself was manually smoke-tested: launched with generated sample plain-text and CMTrace
log files via `-File`/`-Highlight`, confirmed it starts without exceptions and stays responsive
(including while a file it has open is being appended to live).
