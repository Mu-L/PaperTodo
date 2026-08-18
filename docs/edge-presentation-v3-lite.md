# Edge Presentation V3 Lite

- WPF owns Resting, Hover and Preview morph inside one per-paper bounded host.
- The bounded host is capped by the legal preview size; it never spans a work area or queue.
- DirectComposition owns only translation of a same-size live HWND surface.
- A real HWND settles to its endpoint once under cover; per-frame native movement is forbidden.
- Drag/floating authority may remain direct while eligible peers are translated by DComp.
- Gesture state is not visual authority: an active floating cover blocks a second owner.
- Snapshot, freeze, RevealTarget, ConcealSource and resize handoff are not normal-path capabilities.
- WPF morph and DComp translation share one QPC start timestamp.
- A failed authority rollback restores visible real HWNDs immediately.
