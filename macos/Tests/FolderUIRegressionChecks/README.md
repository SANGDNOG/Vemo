# Folder UI hosting regression checks

Synthetic offscreen native fixtures using real SwiftUI `NSHostingView` hierarchies and the **unmodified extracted** `VemoScrollAppearance` declaration. Tests mount a SwiftUI ScrollView (background on VStack), a sidebar List (background on cells), and an explicit NSScrollView with a white NSClipView and an NSHostingView document. Observable state drives empty → one → thirty → empty transitions. Output records ancestor classes, enclosing-scroll availability, scroll/clip background flags, style, hit-test behavior, and scroll/clip geometry.

```sh
python3 macos/Tests/FolderUIRegressionChecks/run.py --output-dir local-validation/folder-freeze12/team2/hosting-final
python3 macos/Tests/FolderUIRegressionChecks/run.py --output-dir local-validation/folder-freeze12/team2/hosting-always-final --always-scrollbars
python3 macos/Tests/FolderUIRegressionChecks/run.py --output-dir local-validation/folder-freeze12/team2/hosting-negative-final --always-scrollbars --negative-control
```

`--always-scrollbars` uses a process launch argument; it does not edit system defaults. `--negative-control` replaces configure with a no-op in the generated fixture only; **failure is expected**. Builds and runs are bounded (90s/15s). Offscreen windows attach AppKit hosting ancestors without presenting or activating any application. No synthesized clicks, accessibility automation, live-app manipulation, audio, personal names, or production queue/state reads occur.

This is a hierarchy/property/geometry check, not evidence of rendered pixels in the installed product. It does not exercise NavigationSplitView, the production AppModel, sheet lifecycle, Return handling, SwiftUI drag initiation/drop delivery, or mouse routing. Passing cannot establish that a reported live UI freeze, white appearance, or folder drag is fixed. Source/helper hashes accompany each run for concurrent-edit traceability.

## Split-view sheet lifecycle extension

```sh
python3 macos/Tests/FolderUIRegressionChecks/run.py --output-dir local-validation/folder-freeze12/team2/sheet-final --sheet-lifecycle
python3 macos/Tests/FolderUIRegressionChecks/run.py --output-dir local-validation/folder-freeze12/team2/sheet-always-final --sheet-lifecycle --always-scrollbars
python3 macos/Tests/FolderUIRegressionChecks/run.py --output-dir local-validation/folder-freeze12/team2/sheet-retained-control --sheet-lifecycle --retain-sheet-control
```

These fixtures order **only their owned windows offscreen** without activating or making the application key. A local legacy SwiftUI.State alias matches the product's CLT macro workaround. The split-view sidebar uses the same transition/animation-value/scroll-background arrangement, and .sheet is driven by local SwiftUI.State via direct closures. Four orders (publish then dismiss, dismiss then publish, next-tick publish, next-tick dismiss) each perform two real sheet cycles: 0 → 30 → 31 rows. They record attachedSheet, sheetParent, visibility/key state, firstResponder, editable field state, native root/local hit routes, and programmatic responder acceptance. No mouse, keyboard, accessibility actions, or events are sent. Sheet runs are bounded to 25s. `--retain-sheet-control` disables only the generated dismissal closure and is expected to fail residual-sheet/onDismiss checks. These checks deliberately do not equate native hit-test/responder acceptance with end-to-end mouse event delivery.

## Passive row comparison

```sh
python3 macos/Tests/FolderUIRegressionChecks/run-row.py --output-dir local-validation/folder-freeze12/team2/row-hit-final
```

This extracts the unmodified row, drag modifier, and theme from baseline 3f0b9c9 and current working source. It compares real row hosting with/without a drag closure, label-region native hit routes, and public native gesture recognizers. Generated sources and hashes preserve the actual tested concurrent source snapshot. It does **not** fire gestures, validate their priority, perform drops, or prove selection works. SwiftUI Button is not necessarily represented by NSButton; SwiftUI gestures need not appear in public NSView.gestureRecognizers.

## Native interaction boundary

```sh
python3 macos/Tests/FolderUIRegressionChecks/run-native.py --output-dir local-validation/folder-freeze12/team2/native-final
```

This compiles an exact byte-copy of product `LibraryItemInteraction.swift`, the actual row/session/theme extracts, and actual core sources. It mounts the product row and discovers its product NSControl overlay. Click and key checks call the control methods with constructed NSEvents **directly**; nothing is posted to NSApplication, global input, or a live app. A fixture-only process-local Objective-C override on this product Surface's inherited `beginDraggingSession` captures pasteboard items and returns an inert constructed NSDraggingSession. Product Swift source is unmodified. This tests threshold branching, exact source bytes, duplicate-start suppression, post-drag mouseUp behavior, and explicit source-end callback lifecycle; it does not test a real AppKit drag session.

SyntheticDraggingInfo implements the native protocol with a uniquely named fixture pasteboard (never general/drag pasteboard). Checks invoke actual entered/updated/prepare/perform/exited methods against exact, public-data-only, empty, multi-item, malformed, wrong-drag, replayed, oversized, and 4096-byte-boundary data. Session authentication code is extracted unchanged. App canDrop/receive closures are bounded synthetic witnesses rather than AppModel/store writes; fresh adapter/core checks cover that separate path. Actual native drag delivery and user mouse behavior require root/user live validation.

Final trackless-helper hosting checks also verify actual mounted scroller subclass, overlay compatibility, and repeated configuration retaining the same replacement instance. This establishes properties/geometry, not knob interaction or product pixels. Native fixture tests unsupported letter/Escape keys without selection. Native `--negative-control` removes only the generated product-copy mouseUp drag guard and must fail the post-drag no-selection assertion.

Review follow-up adds actual `Surface.menu(for: rightMouseDown)` on the hosted contextMenu row and ancestor-provider inventory. The baseline Surface returned nil, immediate platform host nil, next NSHostingView one menu item; root's ancestor-forwarding fix passes. Actual hosted accessibility label includes recordingCount, then a rootView update removes absent count and destination registration. No menu popup or VoiceOver action is simulated.
