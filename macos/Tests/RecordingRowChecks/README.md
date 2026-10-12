Run the regression contract from the repository root:

    python3 macos/Tests/RecordingRowChecks/run-native.py --output-dir local-validation/notes-repair13/team1/native-final

The runner extracts the shipping recording List, row, recording input, drag provider, scroll helper, and drag session without editing them. It compiles these with the actual LibraryItemInteraction source and core drag rules. The model and recording data are synthetic; selection changes increment a fixture load-trigger counter rather than reading real notes.

Checks cover List-to-native hit routing, full row content height, layout-neutral/no-paint button behavior, click selection and List highlight state, context-menu forwarding, retained table selection binding, exact recording drag data, folder destination compatibility, click suppression during drag, and selection after cancellation. Drag start is intercepted on the process-local Surface class and returns an inert session. No live app, global mouse/key events, actual drag bridge, engine, notes file, or private recording is exercised.

`run.py` is an initial diagnostic comparing List rows with no modifiers, onDrag, and onDrag plus contextMenu. Direct CellHostingView method calls did not reproduce modifier-specific selection failure; programmatic table selection updated all three bindings. This diagnostic is not a runtime bug reproduction.

The original folder regression should also pass after shared Surface changes:

    python3 macos/Tests/FolderUIRegressionChecks/run-native.py --output-dir local-validation/notes-repair13/team1/folder-native-final

The root owns the final product build and physical input validation.
