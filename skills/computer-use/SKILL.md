---
name: computer-use
description: Use when a task requires seeing the screen and operating the mouse and keyboard (web pages, desktop apps, games). Core: look first and act fast with little reasoning, read detail from same-frame tiles, verify actions from click receipts, submit actions in one batch, record and replay to catch motion, and pin timing to on-screen endpoint frames.
---

# Computer Use discipline

On a desktop, **reasoning is the least efficient way to gather information**: it yields the fewest new facts, while one look at the screen gives you the whole display.
So the order is **look first, act fast, reason little**.

## 1. Look: take it all in one shot

- **To see the screen, use `screen_grid`**: one capture yields a thumbnail (for orientation) and 1:1 tiles (for precision), **always delivered in full**
  (2×3 tiles + 1 thumbnail by default) — the cost of a few extra images is far smaller than the cost of one more observation round.
- **To see a frame from "a moment ago"** (no longer on screen), use `screen_grid({atSeconds})`: pass that frame's capture time
  (take it from the per-image times `screen_frames` returned) and you get the same thumbnail + all tiles in **one** call.

## 2. Act: fire a whole batch

- **Submit several actions in one round**: actions run serially in submission order with no model in between. "Move → click → type" in one
  message is far faster than one screenshot per step; observe again after the batch (an action invalidates the previous screenshot).
- **Move and click belong in the same message**: `click` acts at **the cursor position read just before injection** and never moves the
  cursor itself ⇒ splitting them into two calls inserts a whole round (several seconds), during which another person or session may move
  the cursor and the click lands on empty space. The order is `cursor_state` (read current position) → `mouse_move_to` (absolute move) →
  `click`, all three submitted in the same batch.
- **Move by "read current position → compute the target → move absolutely"**: a system with pointer acceleration amplifies relative
  motion non-linearly.
- **Prefer Unicode injection for text** (`type_text`): while the input method is in a composition state, letter and arrow keys are taken
  for composition and the page never receives `keydown`. **To switch to English, use `hotkey(['ctrl', 'space'])`**; if that fails, try
  `press_key('shift')`. Judge the result **only by `ime_open` in the key receipt**: it must read `0`; a `1` means try the other method.
  Key combinations, digits and space are usually unaffected by the input method.
- **Confirm an action took effect from the change on screen.**

## 3. Chase motion: record, then replay

- **Start capture first** (`screen_watch start`, frame rate fixed by configuration at 62.5 fps ⇒ 16 ms per frame) and let it record at a fixed interval;
  afterwards pull that short stretch back by timestamp with `screen_frames`.
- **Sample densely, several frames at once**: pull the stretch you care about as densely as possible (at most 16 frames per call —
  just narrow the window to that stretch). That costs fewer reasoning rounds than several sparse requests: whatever is going to happen
  can only appear in the frames.
- **To read the content of one frame, use `screen_grid({atSeconds})` and take it all at once** (thumbnail + all tiles, the same form as
  looking at the current screen) — it has only this one form: **everything in one call**.

## 4. Pin timing: use on-screen endpoint frames

**This section is the settled approach, verified repeatedly**: both endpoints are picture frames read on the same clock, so rendering
latency cancels in the same direction and the difference stays within half a frame.

**Determine N** — the target is **an interval**, not an instant: find both ends of that interval and take its **midpoint** as the target frame.

- **Origin frame**: the frame in which your action **takes effect on screen** (the screen change it caused) — not the moment you issued
  the action, and not something to derive from a duration field.
- **Target frame**: the **midpoint** of the hit interval — **too early and too late are both dangerous**, so take neither end; read both
  sides clearly.

**Capture**: `screen_frames` returns that stretch, and every image carries its capture time (`capturedAtSeconds`, one-to-one with the image
order); `limit` defaults to 6 and caps at 16 — for finer resolution, narrow the window to under half a second and fetch in several calls.

**Reproduce**: submit "action → `wait(N)` → action" in **the same message** — adjacent commands in one batch are dispatched within less
than a frame, so the action happens exactly when you asked and no margin is needed.

**When it misses, check these two readings first** (every historical claim that "this section has a gap" traced back to one of them):

1. Is the origin frame the frame in which the action **takes effect on screen**? (If N can only be computed from "the moment an action was
   issued", the baseline is wrong.)
2. Is the target frame taken at the **midpoint** of the interval? (Hugging the leading edge lands early, hugging the trailing edge lands
   late — the marker/prompt frame that appears once the state shows up is the trailing edge.)

If both hold and it still misses, look at the operational layer (for example, the action was not received by the target, or it came too
late to trigger anything).
