Run from the repository root:

    python3 macos/Tests/ScrollTrackChecks/run.py --output-dir /tmp/vemo-scroll-track-checks

The runner extracts the shipping `VemoScrollAppearance` unchanged and compiles it with an AppKit/SwiftUI harness. A real enclosing NSScrollView checks both axes, overlay compatibility, repeated-configuration identity, passive hit testing, and unrelated scroll/global-style preservation. NSBitmapImageRep checks that slot drawing preserves a contrasting background for both highlight states and scroller styles; inherited AppKit knob drawing must still change pixels and retain nonempty geometry.

The standard NSScroller slot is printed as a baseline observation. This is an offscreen primitive check, not evidence of the actual app's hover appearance, mouse tracking, or the root cause of its white scrollbar area. No global preferences or UI actions are changed.
