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

Every key lives in `cordis.patch.yml`. The shipped defaults follow one specific operating profile — a GUI evaluation
setup where a single-image view and a global `read_image` are deliberately disabled — which is why every optional tool
starts off and `denyReadImage` is `true`. The comments in the file record the reasoning; turn them on if your use case
wants them.

| Key | Default | Meaning |
|---|---|---|
| `backend` | `dxgi` | capture backend: `dxgi` (GPU copy, ~0.42 ms/frame) or `gdi` (~28.9 ms/frame) |
| `frameIntervalMs` | `33` | capture interval ≈ 30 fps |
| `frameCapacity` | `1800` | frames retained by the ring buffer (30 fps × 60 s) |
| `jpegQuality` | `70` | JPEG quality |
| `codec` | `h264` | frame storage: `h264` (in-memory segments, ~445 MB per 20 min) or `jpeg` (per-frame files, 6–11 GB) |
| `captureDir` | `''` | frame output directory; empty means the helper's own temp directory |
| `helperPath` | `''` | helper executable; empty means the bundled `helper/CuHelper.exe` |
| `triggerTools` | `false` | register `wait_for_change` / `act_when` |
| `singleImageTools` | `false` | register `screen_observe` / `region_observe` (single-image forms) |
| `denyReadImage` | `false` | reject `read_image` through a tool guard |
| `diffTool` | `false` | register `screen_diff` |

## Host APIs used

The plugin registers against the harness `ToolService` (`ctx.tools.register`, `ctx.tools.guard`, `ctx.tools.restrict`)
and Cordis `ctx.effect` for ownership.

Verified by a live run on a clean install: harness `@deepseek-ai/dsh@0.1.7-rc.1` (the newest published release at the
time of writing) in a fresh `DSH_HOME`, this repository cloned from GitHub and linked into the profile — the plugin
loads and all 16 tools register. The same symbols are also present in `@deepseek-ai/dsh-tools@0.1.5-rc.3`.

## Limits

- **The desktop is a single shared resource.** Keyboard and mouse are global: two DSH instances on the same machine
  must not act at the same time, or their actions interleave.
- Capture runs at 30 fps and costs roughly a quarter of one core while active.
- H.264 segments are lossy (PSNR 44.8–50 dB); it does not affect reading small text, but it is not lossless.
- The helper must be recompiled with the Windows-shipped `csc.exe` if you change `helper/*.cs`; the build command is
  in `helper/README.md`.
