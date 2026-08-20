# Edge Presentation V3 Lite

## Authority model

- `Bounds` is the visible WPF shape.
- `HostBounds` is one wall-pinned, grow-only bounded HWND capacity.
- `VisualAuthority` is explicit and separate from pointer `Gesture`.
- At every error boundary at least one of the real HWND or the queue
  compositor owns visible pixels.

## Rendering split

- WPF owns Resting, Hover, Active and Preview width/height, rounded shape,
  content opacity, layout and hit testing.
- DirectComposition owns only X/Y translation of a same-size live HWND
  surface.
- The real HWND settles to its target host once under cover. Per-frame
  `SetWindowPos` is forbidden.
- The compositor API surface has no clip, scale, effect, snapshot, reveal,
  conceal or deferred-resize capability.

## Successor and drag

- A successor samples the active predecessor immediately before root
  replacement.
- Source ownership transfers per handle. A dragged/floating owner is
  revealed directly while eligible peers continue in the successor.
- Cancellation is a normal path and uses idempotent source release and
  visual disposal.

## Boundaries

- Startup publishes root, cloak changes, endpoint placement and controller
  routing through one verified DWM boundary.
- Handoff reveals real HWNDs and detaches the root through one verified DWM
  boundary.
- A failed rollback immediately restores real visible authority; it never
  waits for the 50 ms completion retry.

## Verification gates

- normal Hover/Preview snapshot count: `0`
- DComp clip/scale/opacity animation count: `0`
- per-frame real HWND movement during queue translation: `0`
- floating/docking cover with a second preview owner: `0`
- all-hidden interval on injected failures: `0`
- WPF and DComp consume one QPC start and duration per queue transaction
