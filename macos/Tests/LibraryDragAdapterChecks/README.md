# Library drag adapter checks

Run on macOS from the repository root:

```sh
python3 macos/Tests/LibraryDragAdapterChecks/run.py --output-dir local-validation/drag-fix11/team2/final-adapter --mutation-check
```

The runner copies shipping AppModel library methods, UTType declaration, drag session, drop delegate, and scroll helper Surface unchanged. It compiles in Swift 6 with snapshots of real NativeCore sources. Real NSItemProvider registration/loading/NSCopying, UTType, LibraryFolderStore, NSScrollView and NSView are exercised. Only SwiftUI DropInfo/Binding/DropProposal are synthetic. A locked real-provider loader gate holds callbacks while tests change busy/readiness/library/current drag state. Each fixture has isolated persisted metadata under the output directory; no application data, recordings, engine, or UI automation is accessed.

The initial regression can be captured **before** replacing the old token implementation with `--baseline-only` and a fresh output directory. This intentionally exits 1 when its 3 positive controls pass and the legitimate base-only reconstruction is rejected. Baseline source, old payload snapshot, compiled binary, exact commands, and logs remain in the output. Running this option against fixed code is not an old-code reproduction; use the retained old snapshot for that evidence.

Current assertions cover legitimate base-only reconstructed/copy providers; complete drag UUID identity; pending exclusivity; malformed, foreign, stale, missing-field and oversized loaded data; cancellation; model changes during load; preserving and successfully using a newer drag; persistence; direct-only selection; cycle/noop guards; latest store destination rejection; and scoped scroll appearance/nonintercepting hit testing. Loaded-data limits test valid JSON padded with whitespace at 4096 and 4097 bytes. A foreign current model session and unsupported current version also exercise rule guards independently of mismatched loaded data.

Negative controls remove exact decoded equality, remove size enforcement, or make conditional finish unconditional only in generated test source. Each must compile and exit 1 with its named behavioral failure. Overall runner exit 0 means shipping checks pass, all requested controls fail as expected, and source hashes remain unchanged.

The harness does not reproduce AppKit's actual drag/pasteboard bridge, visibility across processes, or SwiftUI gestures/presentation. The NSScrollView check is a named in-memory fixture. Positive callback checks poll conditions for up to 3 seconds; forbidden stale callbacks have a bounded 75 ms observation window, followed by a newer valid drop where applicable. This bounds evidence and does not prove that a callback can never arrive arbitrarily late. Save acceptance is distinct from durability; durable success is checked after `flushLibraryChanges()` and reopening the real store. Error banners are inspected as model state, not visible UI.
