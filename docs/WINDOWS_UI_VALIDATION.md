# Windows UI regression validation — 2026-10-18

The 0.1.1 Native White preview passed core tests but crashed on actual Windows navigation. The native .NET event log identified a null `FolderChoice` during Avalonia container recycling, triggered by replacing the sidebar source from its selection event. Folder creation could persist while the subsequent refresh failed.

The repair accepts null template content, defers selection/search refresh until the current input event finishes, and avoids replacing unchanged sidebar sources. Folder create/move actions share a context menu. Menu, popup, combo and settings states use the Native White palette. Invalid numeric settings and foreign processor folders fail visibly before applying the known invalid configuration. Registration is not an atomic transaction across background readers.

Run the UI and core suites:

```sh
dotnet test windows/Vemo.slnx
```

`Vemo.App.Tests` loads the real application theme and window in Avalonia Headless, uses disposable synthetic libraries, and dispatches pointer/keyboard events. It does not interact with a user's desktop. Baseline original-source tests reproduced 9 failures out of 11 cases. The initial repair passed 14 UI cases on macOS and Windows. Extended tests exposed stale title/date tracking because asynchronous TextChanged ran after the programmatic-update guard cleared. Synchronous TextChanging fixed those four cases. A separate unrealized-row test-helper failure was corrected by scrolling and realizing the requested item before input. The final suite passed 32 UI, 65 core, and 12 provision cases on macOS. The final self-contained executable passed all 32 UI cases on the user’s Windows 11 x64 PC (.NET10.0.13), zero failures/errors/skips, in 10.670 seconds.

```sh
dotnet publish windows/Vemo.App.Tests -c Release -r win-x64 --self-contained true
```

On Windows, run `Vemo.App.Tests.exe -noColor -trx headless-native.trx`. GUI rendering at actual display scale, native file pickers, and user-driven desktop behavior still require manual verification. Headless acceptance does not certify complete visual equivalence or all planned Mac features.

The user-PC repair is installed side by side at `%LOCALAPPDATA%\Programs\Vemo\0.1.3-ui-repair`. This was the installed 0.1.3 baseline; the Desktop shortcut now targets 0.1.4 (see [drag/drop validation](WINDOWS_DRAG_DROP.md)). Existing library/model settings were preserved; a currently open old process must be closed before reopening. Previous 0.1.1 is retained for rollback. No public release or main-branch merge is implied.

Remaining Windows feature gaps include folder rename/delete, playback, direct Google OAuth, automatic titles, and notes beyond 14,000 source characters. CPU transcription/notes/three-entry ZIP inference is independently verified; GPU notes compatibility remains unverified.
