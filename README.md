# dsh-computer-use

Computer Use for DeepSeek Harness on Windows: an agent can see the Windows desktop and operate it like a person —
full-screen capture, look back at any frame of the last ~20 minutes by timestamp, and mouse/keyboard injection.

## Architecture

Two halves talking over stdio, one JSON request per line:

| Half | Where | What it does |
|---|---|---|
| Plugin (`lib/index.js`) | WSL, inside the DSH host process | registers the model-visible tools, owns the helper process, hands frames to the model |
| Helper (`helper/CuHelper.exe`) | Windows | desktop capture (DXGI, GDI fallback), H.264 segment ring buffer, keyboard/mouse injection, window enumeration |

The plugin starts the helper once and keeps it resident (a fresh process per frame would pay tens of milliseconds
of GDI/DXGI initialization every time). Frame files land in the Windows temp directory and are read back through
`/mnt/<drive>/...`.

## Requirements

- DSH running in **WSL**; the Windows helper is launched through WSL interop. A pure-Windows DSH install does not
  work as written, because Windows paths are mapped as `/mnt/<drive>/...`.
- Windows with **.NET Framework 4** (present on every supported Windows). No SDK, no ffmpeg, no third-party binary.
- Screen resolution decides the tile geometry: tiles are sized to fit the harness image budget, so a 2560×1440
  desktop yields 2×3 tiles of 1280×480 (614,400 px each, delivered unscaled).

## Install

```sh
# 1. clone the repository somewhere permanent, then link it into the profile
git clone https://github.com/1azybug/dsh-real-time-computer-use.git
pnpm dsh plugin --profile web add link:"$PWD/dsh-real-time-computer-use"
# 2. restart dsh web — host-layer plugins are only read at startup
```

Install the operating discipline as a skill (the package ships an English `SKILL.md`; nothing installs it for you):

```sh
cp -r skills/computer-use ~/.dsh/skills/      # or $DSH_HOME/skills/
```

## Tools

Capture and inspection: `screen_grid` (one capture → thumbnail + 1:1 tiles, optionally `atSeconds` for a frame from
the buffer), `screen_frames` (pull a time range out of the recording buffer; every image carries its capture time),
`screen_watch` (start/stop/stats of the continuous capture), `screen_windows` (window under a point, foreground
window, or full enumeration), `cursor_state`.

Actions: `mouse_move_to`, `mouse_move_by`, `click`, `mouse_button`, `drag`, `scroll`, `type_text`, `press_key`,
`key_state`, `hotkey`, `wait`.

Optional tools stay unregistered unless enabled in configuration: `wait_for_change` / `act_when` (`triggerTools`),
`screen_observe` / `region_observe` (`singleImageTools`), `screen_diff` (`diffTool`).

## Configuration

Every knob lives in `cordis.patch.yml`, and **the shipped defaults are not a neutral baseline**: they are the
configuration one GUI-evaluation setup runs with. Out of the box you get a **16-tool** surface; switching the four
optional groups on widens it to **21 tools** and re-enables ordinary image reading.

Read those four defaults as answers to four questions, not as arbitrary flags:

| Switch | Default | With the default | Turning it on adds |
|---|---|---|---|
| `triggerTools` | `false` | nothing watches the screen waiting for a condition | `wait_for_change`, `act_when` — poll a region until it changes, then act |
| `singleImageTools` | `false` | screen reading only ever arrives as the same-frame thumbnail + tiles form | `screen_observe`, `region_observe` — hand back a single image |
| `denyReadImage` | **`true`** | a tool guard rejects **every** `read_image` call — with or without `region`, for every session, not only screen captures | normal image reading works again |
| `diffTool` | `false` | no frame differencing | `screen_diff` — report which grid cells changed |

Why those defaults: on a 2560×1440 desktop a single full-screen image is either downscaled past legibility (a 46 px
control becomes 19 px) or covers one corner, so the plugin standardizes on one full-view form — `screen_grid`, one
capture turned into a same-frame thumbnail plus 1:1 tiles. Letting `read_image` back in would restore the
single-image path for *any* image, not only screen captures. The `triggerTools` entry is an evaluation constraint
(watching for a visual condition and then acting counts as cheating in that setting), not a technical limit. The
comments in the file record each decision.

The remaining keys are ordinary capture settings:

| Key | Default | Meaning |
|---|---|---|
| `backend` | `dxgi` | capture backend: `dxgi` (GPU copy, ~0.42 ms/frame) or `gdi` (~28.9 ms/frame) |
| `frameIntervalMs` | `16` | capture interval ≈ 62.5 fps — **the only place the capture rate is decided** (`screen_watch` has no `fps` argument). Deliberately far below the 33.333 ms per-frame bound: at 25 ms only 8.3 ms of headroom is left and the once-per-segment encoder pre-build (10–15 ms of concurrent work) crosses it; at 16 ms the headroom is 17.3 ms and the same jitter stays inside. Measured under a full-screen load with a second helper recording: 4/2408 misses at 25 ms vs 1/11217 at 16 ms. Costs ≈79% of one core (vs 52%) and 1.5× the recording size. |
| `frameCapacity` | `1800` | frames retained by the ring buffer; only the `jpeg` codec uses it — `h264` windows are bounded by `retain_s` and the byte budget |
| `jpegQuality` | `70` | JPEG quality |
| `codec` | `h264` | frame storage: `h264` (in-memory segments, ~445 MB per 20 min) or `jpeg` (per-frame files, 6–11 GB) |
| `captureDir` | `''` | frame output directory; empty means the helper's own temp directory |
| `recordDir` | `''` | **recording** directory (Windows path). When set, every `screen_watch start` also encodes the captured frames straight into an mp4 in this directory — one continuous encoder, timestamps taken from the real capture instants, so a fluctuating capture rate does not compress playback. Measured at 2560×1440: ~1.1 MB per 5 s, versus ~631 MB for the same span written as per-frame JPEG. Recording implies `h264`. Empty means no recording. |
| `helperPath` | `''` | helper executable; empty means the bundled `helper/CuHelper.exe` |

The plugin's own code defaults differ for `frameIntervalMs` (`33`), `backend` (`gdi`) and `codec` (`jpeg`): the patch
file is where a deployment states its choice, and the code keeps the conservative value for deployments that mount the
plugin without it. `denyReadImage` is `true` in both.

## Host APIs used

The plugin registers against the harness `ToolService` (`ctx.tools.register`, `ctx.tools.guard`, `ctx.tools.restrict`)
and Cordis `ctx.effect` for ownership.

Verified by a live run on a clean install: harness `@deepseek-ai/dsh@0.1.7-rc.1` (the newest published release at the
time of writing) in a fresh `DSH_HOME`, this repository cloned from GitHub and linked into the profile — the plugin
loads and all 16 tools register. The same symbols are also present in `@deepseek-ai/dsh-tools@0.1.5-rc.3`.

## Limits

- **The desktop is a single shared resource.** Keyboard and mouse are global: two DSH instances on the same machine
  must not act at the same time, or their actions interleave.
- Capture runs at the rate fixed by `frameIntervalMs` (40 fps in the shipped patch) and costs roughly a quarter of
  one core while active. The rate is a deployment choice, not a tool argument: `screen_watch` has no `fps` parameter.
- **The per-frame interval bound is met at the shipped rate, with a rare exception.** The target is that no frame
  interval exceeds **33.333 ms**; the shipped `frameIntervalMs: 16` (62.5 fps) leaves 17.3 ms of headroom, so the
  once-per-segment encoder pre-build (~220 ms of Media Foundation work started concurrently with capture, costing the
  capture thread 10–15 ms) no longer crosses the bound. Measured under a full-screen animation load with a second
  helper recording alongside: **1 frame of 11217** at 16 ms, versus 4 of 2408 at 25 ms; that single miss was a
  system-level spike (64.7 ms), not the pre-build. Sealing (`Finish`+`Dispose`, 176 ms under load) runs on its own
  thread. **Zero misses are not guaranteed** — a system-level stall can still exceed the bound at any rate. A tested
  alternative (resident pre-build thread, capture at `AboveNormal` / background at `BelowNormal`) only moved the same
  stall to a different frame and made the worst case worse, so it was **not adopted**. Numbers/conditions:
  `helper/README.md`.
- H.264 segments are lossy (PSNR 44.8–50 dB); it does not affect reading small text, but it is not lossless.
- The helper must be recompiled with the Windows-shipped `csc.exe` if you change `helper/*.cs`; the build command is
  in `helper/README.md`.
