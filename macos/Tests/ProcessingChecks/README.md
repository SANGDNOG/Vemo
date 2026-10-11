# Manual processing checks

Run from the repository root on macOS with Python 3 and Swift installed:

```sh
python3 macos/Tests/ProcessingChecks/run.py
```

For retained evidence and a regression mutation check:

```sh
python3 macos/Tests/ProcessingChecks/run.py --output-dir local-validation/processing-feedback-review/shipping --mutation-check
```

The runner copies the current AppModel processing/notes/status methods unchanged into a generated harness. It compiles them in Swift 6 mode with the real `WireTypes.swift` and `Validation.swift`. A fake actor replaces only the transport, recording attempted/successful WireCommands and injecting transport errors. The harness exercises manual routing, eligibility, payloads/order, captured pause/selection, request identities, AC preference, demo isolation, pause visibility, and errors. It uses synthetic jobs/transcripts; it starts no engine, reads no application DB or recordings, and invokes no inference, download, SwiftPM, or native suites.

`--mutation-check` replaces only generateNotes in generated test source with the old implementation. It must reproduce the missing-resume assertion with process exit 1; runner exit 0 means current checks and the expected negative control both behaved correctly. Product files are never modified. Evidence includes generated sources, build/run logs, result counts, exact compilation arguments, and source/method SHA256.

AppModel exposes no handle for its unstructured Tasks. Positive checks await recorded sends or surfaced errors; forbidden-send guards allow a bounded 50 ms scheduling window. This verifies command boundaries, not an engine acknowledgement, durable scheduling, inference completion, or SwiftUI presentation. Sequential sends within one action are tested; atomic delivery relative to unrelated user actions is not promised. A change to a tested method during a run is reported as SOURCE_CHANGED. AppModel changes outside the copied methods do not invalidate a run.
