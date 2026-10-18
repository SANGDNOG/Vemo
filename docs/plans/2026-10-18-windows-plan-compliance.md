# Windows approved-plan compliance

Goal: apply approved A Native White to the existing Windows app and connect real Google OAuth/Drive, preserving recordings, processing, drag/drop and other worktrees.

Sources: user-approved START_HERE.txt, IMPLEMENTATION_HANDOFF.md and exports at /Users/sangdong/Documents/ChatGPT/Vemo/designs/2026-10-18-android-windows; existing Mac OAuth/Drive and Android protocol implementations. User explicitly requests execution of the already-approved design; no new approval loop.

## Ownership and acceptance
- Root: MainWindow integration, lifecycle, settings navigation, automatic sync, final review/install and evidence. MainWindow settings live in right pane while library/list remain visible. Cancel/close must not leak login listeners or leave disposed controls handling callbacks.
- Team1 complex: SettingsView/SettingsWindow and styles. Grouped rows, advanced paths collapsed, real directory pickers, functional Google controls, accessible keyboard state, errors and cancel preserved. No mock successes or unsupported enabled switches.
- Team1 deep: Core Google OAuth/Drive implementation and unit tests. PKCE S256 and state, loopback only, fixed HTTPS Google endpoints without redirects, current-user encrypted token storage, bounded responses, cancellation, refresh and disconnect. Independent mirror avoids changing local MobileDirectory. Drive app-file scope, explicit chosen folder, safe protocol validation and no foreign overwrite/delete.
- Team2 complex: audit reference vs implementation; then independent regression tests. Distinguish UI, HTTP fixture, native runtime and real account acceptance.

## Order
- [x] Read original handoff and current implementation; preserve isolated Windows branch.
- [ ] Save concrete design/function discrepancy inventory.
- [ ] Build OAuth/backend and grouped embedded settings independently with fixed interface agreement.
- [ ] Integrate login/state/folder management/manual+automatic sync and shutdown lifecycle.
- [ ] Address audit's concrete main-window design and working-control discrepancies; preserve existing functionality.
- [ ] Independent tests: wrong/duplicate state, denial, timeout, refresh rejection, disconnect races, wrong processor/hash, duplicate imports, network failure, keyboard/focus/selection, settings save/reopen, existing DnD/inference.
- [ ] Compare rendered app settings and main views with reference; report physical-DPI limits.
- [ ] Native Windows build/tests and side-by-side install after verification; user OAuth login approval only when concrete app is ready.
- [ ] Record actual evidence and gaps on NAS; do not call mocked or headless results real account verification.

Real Google authorization requires user interaction; credentials must never appear in reports, commits or test fixtures. Existing authorized Desktop client configuration may be imported locally, not published. Mounted G: is not a substitute for OAuth.
