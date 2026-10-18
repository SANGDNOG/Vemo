# Windows drag/drop

Windows now wires Avalonia native drag/drop for folder reparenting, selected recordings, and local audio import. This is additional implementation after the 0.1.3 UI crash repair; previous releases did not implement these gestures.

- Drop a folder on another folder to nest it, or on **전체 녹음** in the sidebar to move it to the root.
- Drop one or multiple selected recordings on a folder to move them. Drop on **미분류** to remove their folder assignment. Internal recording drops on **전체 녹음** and its list background are rejected to avoid accidentally unfiling records.
- Drop finished local audio files from Explorer onto a folder or the recording list to import copies. Supported extensions match file import: WAV, MP3, M4A, MP4, FLAC, OGG, AAC, WMA. Original files remain unchanged.
- Self/descendant folder moves, same destinations, invalid/foreign payloads, unsupported/mixed file batches and trash targets are rejected. Valid targets receive a lavender highlight.

Internal payloads use Avalonia's in-process format and window identity. Drag start snapshots multi-selection before the list's normal click behavior and starts only after the platform threshold; capture loss clears pending gestures. Refresh is deferred during a drag. File import commits its destination in the same store operation before the queued recording becomes visible to the processor. Export after a recording move runs away from the UI event handler; old exported archives remain preserved by the existing archive policy.

Validation uses the real window and theme in Avalonia Headless: pointer-down/move threshold and payload tests with a test platform drag adapter, routed/raw drag enter/over/drop tests, and existing UI action regressions. The test-only platform adapter depends on pinned Avalonia 12.1.4 private API, producing AVA3001; it is not shipped with the app.

An opt-in native test drops a real M4A into a nested folder, starts the window's actual background engine, checks transcript/notes and exactly three ZIP entries, verifies audio hashes, title/date filename, nested folder placement and persistence after reopening. Set `VEMO_TEST_AUDIO` and `VEMO_TEST_SETTINGS` before running the self-contained test executable. Without both variables the model test is explicitly skipped.

Framework-event tests do not certify a physical Explorer/OLE pointer gesture or actual display scaling. Parsec input was intentionally not used while the user worked on the Mac. Current verified results and remaining limits are recorded in the task evidence and NAS worklog. Folder rename/delete, playback, direct Windows Google OAuth, automatic titles and long-note synthesis remain separate feature gaps.

## Verified 0.1.4 build

On 2026-10-18 the final self-contained Windows suite passed **61/61**, with zero failures or skips (91.865 seconds). The real M4A background-processing case passed in 73.031 seconds. Local core and provision suites passed 65 and 12 cases respectively. The first native run had one all-recordings target-policy assertion failure; the corrected policy and test passed in the final run.

Installed at `%LOCALAPPDATA%\Programs\Vemo\0.1.4-drag-drop`; Desktop `Vemo.lnk` points to it. Close an old running instance before reopening. Installed App/Core DLL hashes match the binaries executed by the final native tests. Version 0.1.3 remains available for rollback. This is a local preview, not a public release.
