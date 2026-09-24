/**
 * dsh-real-time-computer-use —— Computer Use（Windows）：让 Agent 看见 Windows 桌面并像人一样操作它。
 *
 * 架构：DSH 跑在 WSL 里，桌面在 Windows 上。这个插件是 **WSL 半边**（注册模型可见的工具、
 * 管理 helper 进程、把帧交给模型）；真正的采集与注入在 **Windows 半边的常驻 helper**
 * （`helper/CuHelper.exe`，源码 `helper/CuHelper.cs`）里完成，两者用 stdio 一行一条 JSON 通信。
 *
 * 三条设计要点（都由实测确定，别随手改）：
 *
 * 1. **时间戳一律来自 helper 的 QPC**，本插件绝不用自己的时钟推算时间。本机 WSL 的
 *    `CLOCK_MONOTONIC` 比真实时间慢约 10%（外部基准定标过），拿它做时间轴会系统性偏。
 * 2. **相对移动是一等公民**：校准显示绝对坐标的误差随「离光标距离」显著变大（中/远距差近 2 倍），
 *    相对位移基本与距离无关。所以动作集里 `mouse_move_by` 优先。
 * 3. **点位没有余量**：2560×1440 屏经 DSH 的图片预算缩到约 1066×600 后才给模型，
 *    实测定位中位误差约 9–10 px、`>20px` 占 12–16%。所以动作后**必须重新观察**再动下一步。
 *
 * 自包含：只用 node 内置模块；Windows 侧只用系统自带组件（.NET Framework + GDI/DXGI）。
 */

import { spawn } from 'node:child_process'
import { createInterface } from 'node:readline'
import {
  appendFileSync, copyFileSync, existsSync, mkdirSync, readdirSync,
  readFileSync, renameSync, rmSync, statSync,
} from 'node:fs'
import { fileURLToPath } from 'node:url'
import { basename, dirname, extname, join } from 'node:path'
import { homedir } from 'node:os'

export const name = 'dsh-real-time-computer-use'

export const inject = ['tools']

/** 默认配置；全部可在 cordis.patch.yml 的 config 里覆盖。 */
const DEFAULTS = {
  /** 采集后端：`dxgi`（GPU 侧拷贝，帧率高 CPU 低）或 `gdi`（回退）。 */
  backend: 'gdi',
  /** 持续采集的目标间隔（毫秒）；33 ≈ 30 fps。 */
  frameIntervalMs: 33,
  /** 环形缓冲保留的帧数（30 fps × 60 s = 1800）。 */
  frameCapacity: 1800,
  /** JPEG 质量。 */
  jpegQuality: 70,
  /**
   * 录像目录（可选）：非空时，每次 `screen_watch start` 都把采集帧**直接编码写入该目录里的 mp4**。
   *
   * **默认空（不录像）**：录像多占一个编码器与持续的磁盘写（30 fps 实测约 1.1 MB / 5 秒），
   * 只在需要"事后逐帧复盘"时打开；写进部署配置（cordis.patch.yml）比让模型每次自己传路径更可靠。
   * 路径用 Windows 形态（helper 是 Windows 进程），例如 `C:\Users\Administrator\dsh-cu-recordings`。
   * 录像只在 h264 编码下生效（见 helper 的 FileEncoder），带录像时 helper 会强制 codec=h264。
   */
  recordDir: '',
  /**
   * 是否注册「**盯着画面等条件的自动触发**」类工具（`wait_for_change` / `act_when`）。
   *
   * **默认 false**：主人 2026-09-22 明确——本机这套 Computer Use 正被他用于论文的 GUI 评测，而在该口径下
   * "程序化地等某个视觉条件达成再自动动作"**算作弊**。被允许的是：**按已知信息定时等待 + 动作**
   * （`wait` 与动作同批提交）、以及各类**纯观察**（截图 / 取帧 / 窗口查询）。
   * 只有论文口径变了才需要打开，所以它是显式开关，不做成默认行为。
   */
  triggerTools: false,
  /**
   * 是否注册 `screen_diff`（相邻帧差分，回"哪块变了"的矩形序列）。
   *
   * **默认 false**：主人 2026-09-22 20:13 定——他怀疑它给出的"变化起点"误导了执行者，
   * 而它的精度本来也不足以当判据（网格单元 + 阈值 ⇒ 只给单元粒度的矩形、可能静默漏检、
   * 不给精确帧时刻）。判断时机一律用**画面端点帧**。只有口径变了才打开。
   */
  diffTool: false,
  /**
   * 是否注册「**只返回一张图**」的观察工具（`screen_observe` / `region_observe`）。
   *
   * **默认 false**：主人 2026-09-22 23:23 定——「把只返回一张图的 CU 工具都给禁掉」。
   * 理由：单图形态必然二选一——要么被 DSH 缩到像素预算内（小控件看不清、诱导在缩图上判读），
   * 要么只能覆盖一小块（看清整屏得多次取，还可能各取到不同帧）。看清屏幕的推荐形态只有
   * `screen_grid`：**同一次抓帧**给缩略图（定方位）+ 若干 1:1 分块（给精度），要看哪块取哪块；
   * 回看某一帧同理用 `screen_grid({atSeconds})`。只有口径变了才打开这个开关。
   */
  singleImageTools: false,
  /**
   * 是否**整体停用** `read_image`（通用图像工具，不在本插件里）。
   *
   * **本机部署为 true**（主人 2026-09-22 23:25：「启用 CU 工具时把 read_image(path, region={...}) 禁掉」）：
   * 它与"只返回一张图"的工具同族——整图读会被缩到像素预算内，带 `region` 又能把任意一张图裁成一块、
   * 1:1 交给模型，两条路都能绕开 `screen_grid` 的分块形态。**打开即拦下全部 `read_image` 调用**
   * （带不带 `region` 都拦）。代码默认即 true：它拦的是宿主自带的通用读图工具，而"看画面"这件事
   * 在本插件里有唯一推荐形态（`screen_grid`）；要恢复整图读，把它改成 false 即可。
   */
  denyReadImage: true,
  /**
   * 帧的存储编码：`jpeg`（逐帧写盘，默认）或 `h264`（内存切片，20 分钟常驻用）。
   * 默认保持 jpeg——换编码会改变 `screen_frames` 的时间语义，这件事要让部署方显式选择。
   * （h264 下"正在写的那一片"由采集端按需结算，所以不再有"最近 5 秒取不到"的硬缺口。）
   */
  codec: 'jpeg',
  /** helper 可执行文件路径；留空则用随包的 `helper/CuHelper.exe`。 */
  helperPath: '',
  /**
   * DSH 对**每张**交付给模型的图的像素预算（超过就自动等比缩小）。
   * 必须与 DSH 侧的 `imagePixelBudget` 一致——否则工具以为"还在预算内"、实际已被缩回，
   * 放大等于白做。默认 640000（DSH 默认值，本机实测生效值）。
   */
  imagePixelBudget: 640000,
}

/** helper 单次请求的默认超时（毫秒）。抓一整帧要几十毫秒，给足余量。 */
const CALL_TIMEOUT_MS = 30_000

/**
 * 按**物理时间**等待指定毫秒，并用单调钟逐步交叉校验。
 *
 * 为什么主计时用墙钟：本机 WSL 的 `CLOCK_MONOTONIC` 走得比真实时间慢（外部基准定标约 1.18），
 * 而 `setTimeout` 正是按单调钟计时 ⇒ `wait(20000)` 实测等了 **23677 ms**；helper 侧的等待走
 * Windows QPC 是准的（`wait_change` 8 条实测偏差 +0.2~1.3%），同一插件里两种等待差 18% 会让人
 * 对时间失去判断，所以用 `Date.now()` 定目标时刻、分段小睡逼近它。
 *
 * 为什么还要单调钟：**本机墙钟会向前跳变**（2026-09-22 实测 60 秒内 2 次约 3.6 秒）。跳变会让
 * `deadline − now` 瞬间变负 ⇒ 等待**提前结束且完全静默**：实测把 `wait(1390)` 变成实际 603 ms，
 * 而回执报 `elapsed_ms=3469`（墙钟差被跳变撑大），两个数字都不可信，使用者按它推游戏时间就会失准。
 * 单调钟不跳，但比物理时间慢 ⇒ 只用它做**跳变探测**：某一步墙钟增量远超单调钟增量就按差值把
 * deadline 后移，并把累计跳变量交给调用方。
 * @param ms - 目标等待毫秒数。
 * @returns 请求值、去跳变后的实际等待、单调钟差值、检测到的累计跳变量（毫秒）。
 */
async function sleepFor(ms) {
  const wall0 = Date.now()
  const mono0 = performance.now()
  let deadline = wall0 + ms
  let prevWall = wall0
  let prevMono = mono0
  let jumped = 0
  for (;;) {
    const wall = Date.now()
    const mono = performance.now()
    const wallStep = wall - prevWall
    const monoStep = mono - prevMono
    // 正常一步里两个钟的增量同量级（本机墙钟略快于单调钟）；任一侧偏到 3 倍以外就是时钟异常，
    // 按差值补偿 deadline，并把累计量报给调用方——绝不静默地少等。
    if (wallStep > monoStep * 3 + 200) {
      const drift = wallStep - monoStep
      jumped += drift
      deadline += drift
    } else if (monoStep > 200 && wallStep < monoStep / 3 - 200) {
      const drift = wallStep - monoStep
      jumped += drift
      deadline += drift
    }
    prevWall = wall
    prevMono = mono
    const remain = deadline - wall
    if (remain <= 0) break
    await new Promise(resolve => setTimeout(resolve, Math.min(remain, 50)))
  }
  return {
    requestedMs: ms,
    elapsedMs: Math.round(Date.now() - wall0 - jumped),
    monoElapsedMs: Math.round(performance.now() - mono0),
    clockJumpMs: Math.round(jumped),
  }
}

const HERE = dirname(fileURLToPath(import.meta.url))

/** 诊断日志的单文件上限；超过就轮转一份到 `.1`（helper 的 stderr 正常时很少，出问题时会刷）。 */
const TRACE_MAX_BYTES = 1024 * 1024

/** 固定副本的保留时长：超过就删，避免"每取一次帧就留一份"长期堆积。 */
const FRAME_PIN_MAX_AGE_MS = 60 * 60 * 1000

/**
 * 把刚取回的帧**固定一份**到插件自己的目录，返回带 `pinnedPath` 的副本。
 *
 * 为什么需要：helper 的帧文件受环形淘汰与 `PruneOldFrames` 管辖，而早期 `pathsOnly` 是"先拿路径、
 * 稍后再读"的用法——实测出现过"精读到一半文件不存在"。固定副本把"稍后再读"变成可靠动作；
 * 复制失败时**保持原路径**（让调用方拿到真实的"文件不存在"，而不是一条看起来正确的错路径）。
 * @param frames - `[{ path, ... }]`，path 是 helper 给的 Windows 路径。
 * @returns 逐项带回 `pinnedPath` 的新数组。
 */
/** 固定副本目录的文件数上限；超过就从 mtime 最旧的删起。 */
const FRAME_PIN_MAX_FILES = 500

/**
 * 固定副本的目录：默认 `$DSH_HOME/computer-use/frames`，**`$DSH_CU_FRAMES_DIR` 可覆盖**——
 * 冒烟与探针据此写自己的临时目录，测试产物不混进真实副本目录。
 * @returns 目录的绝对路径。
 */
function framePinDir() {
  const override = process.env.DSH_CU_FRAMES_DIR
  if (typeof override === 'string' && override.trim() !== '') return override
  const home = process.env.DSH_HOME ?? join(homedir(), '.dsh')
  return join(home, 'computer-use', 'frames')
}

/**
 * 说明「这一窗口里没有帧」到底是哪种情况。两种成因要的补救动作相反：采集没开就该 start，
 * 采集在跑而窗口落在覆盖区间之外就该改窗口——只报「先 start」会把模型带进无用的重试。
 * @param session - helper 会话。
 * @param from - 请求窗口起点（helper 时间轴，秒）。
 * @param to - 请求窗口终点（helper 时间轴，秒）。
 * @returns 面向模型的诊断句。
 */
async function describeEmptyWindow(session, from, to) {
  const hasWindow = Number.isFinite(Number(from)) && Number.isFinite(Number(to))
  const wanted = hasWindow
    ? `，而请求的是 [${Number(from).toFixed(2)}, ${Number(to) >= 1e9 ? '最新' : Number(to).toFixed(2)}] 秒`
    : ''
  let stats
  try {
    stats = await session.call({ cmd: 'live_stats' })
  } catch {
    return '取不到帧，也读不到采集状态——先 screen_watch start 录一段（若仍失败，helper 可能刚重启过）。'
  }
  const running = stats?.running === true
  const frames = Number(stats?.frames ?? 0)
  const span = Number(stats?.span_s ?? 0)
  const retained = Number(stats?.retained_s ?? 0)
  // 能回看的跨度：h264 只有已封片的片算数，jpeg 没有 retained_s ⇒ 取"大于 0 的那个"。
  // ⚠️ 别写成 `retained_s ?? span_s`：0 不是 null，helper 在 h264 未封片时会真给 0，
  // 于是"缓冲里有 1800 帧"也会被错判成"还没有帧"（2026-09-22 测试组抓到的误诊）。
  const coverage = retained > 0 ? retained : span
  if (!running) {
    return '缓冲里没有帧，采集也没在运行——先 screen_watch start 录一段再回看。'
  }
  if (frames <= 0) {
    // 在跑但还没进帧：说"采集也没在运行"是错的，那是两个不同的补救动作（一个是 start，一个是等）。
    return '采集在运行，但缓冲里还没有帧（刚开始录？）——稍等一下再取，或先用 screen_watch 的 stats 看覆盖时长。'
  }
  if (!(coverage > 0)) {
    return `采集在运行、缓冲里有 ${frames} 帧，但读不到可回看的跨度——先用 screen_watch 的 stats 看覆盖时长，再决定时间窗。`
  }
  let now = NaN
  try {
    const latest = await session.call({ cmd: 'latest' })
    if (latest?.ok === true) now = Number(latest.t)
  } catch { /* 拿不到最新帧时刻就退化成只说覆盖长度 */ }
  if (!Number.isFinite(now)) {
    return `取不到帧：采集在运行，但缓冲只覆盖最近 ${coverage.toFixed(1)} 秒${wanted}。改用 lastSeconds 取最近的帧。`
  }
  return `该时间窗内没有帧：采集在运行，缓冲覆盖 [${Math.max(0, now - coverage).toFixed(2)}, ${now.toFixed(2)}] 秒${wanted}。`
    + '改用 lastSeconds，或把 from/to 挪进覆盖区间再取。'
}

function pinFrameFiles(frames) {
  const dir = framePinDir()
  try {
    mkdirSync(dir, { recursive: true })
    const now = Date.now()
    const kept = []
    for (const name of readdirSync(dir)) {
      const full = join(dir, name)
      try {
        const mtime = statSync(full).mtimeMs
        if (now - mtime > FRAME_PIN_MAX_AGE_MS) rmSync(full, { force: true })
        else kept.push({ full, mtime })
      } catch { /* 并发删除/统计失败都不影响本次固定 */ }
    }
    // 时间清理挡不住"一小时内高频取帧"把目录撑大：超过上限就从最旧的删起。
    if (kept.length > FRAME_PIN_MAX_FILES) {
      kept.sort((left, right) => left.mtime - right.mtime)
      for (const item of kept.slice(0, kept.length - FRAME_PIN_MAX_FILES)) {
        try { rmSync(item.full, { force: true }) } catch { /* 同上 */ }
      }
    }
  } catch { /* 清理失败不阻塞固定 */ }
  return frames.map((frame) => {
    const source = process.platform === 'win32' ? String(frame.path ?? '') : toWslPath(frame.path)
    try {
      // 名字 = 帧名 + 墙钟 + 随机：helper 的帧号在采集重启后会从头开始，只按帧号命名会让新副本
      // 盖掉旧的同号副本；而毫秒时间戳在同一毫秒内仍会撞（测试组指出）⇒ 再缀 6 位随机。
      const ext = extname(source)
      const target = join(dir, `pin-${basename(source, ext)}-${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}${ext}`)
      copyFileSync(source, target)
      return { ...frame, pinnedPath: target }
    } catch {
      // 原帧已被淘汰：如实保留原路径，调用方读它就会得到"不存在"，而不是一条假路径。
      return frame
    }
  })
}

/**
 * 诊断落盘：把 helper 的 stderr 与生命周期事件（启动 / 退出码 / stdin 断开 / 请求超时）追加写
 * `$DSH_HOME/logs/dsh-computer-use.log`。
 *
 * 为什么必须落盘：这些证据原先只经宿主 stdout 输出，而宿主若从终端启动（stdout 指向
 * `/dev/pts/N`），窗口一滚就没了——2026-09-22 10:5x 那次「helper 失去响应、宿主因 EPIPE
 * 崩溃」的现场正是这样丢掉的，死因至今没有证据。
 * @returns 追加一行的函数；写盘失败时静默降级（诊断失败绝不影响工具行为）。
 */
function createTraceLog() {
  const home = process.env.DSH_HOME ?? join(homedir(), '.dsh')
  const file = join(home, 'logs', 'dsh-computer-use.log')
  return (line) => {
    try {
      mkdirSync(dirname(file), { recursive: true })
      if (existsSync(file) && statSync(file).size > TRACE_MAX_BYTES) renameSync(file, `${file}.1`)
      appendFileSync(file, `${new Date().toISOString()} ${line}\n`)
    } catch { /* 诊断写不进去不该影响工具 */ }
  }
}

/**
 * 把 helper 返回的 Windows 路径换算成 WSL 能读的路径。
 * helper 把帧写在 Windows 临时目录，我们经 `/mnt/c/...` 读——比把 MB 级 base64 塞进 JSON 行划算。
 * @param winPath - `C:\...\x.jpg` 形态的路径。
 * @returns WSL 侧可读的路径。
 */
function toWslPath(winPath) {
  const text = String(winPath ?? '')
  const drive = /^([A-Za-z]):[\\/](.*)$/.exec(text)
  if (drive !== null) return `/mnt/${drive[1].toLowerCase()}/${drive[2].replace(/\\/g, '/')}`
  return text.replace(/\\/g, '/')
}

/**
 * 与 Windows 侧常驻 helper 的一条长连接。
 *
 * helper 是常驻进程：启动一次、反复请求。每帧重新起进程会白付几十毫秒的启动成本
 * （它还要初始化 GDI/DXGI），对 30 fps 是致命的。
 */
class HelperSession {
  #exePath
  #capacity
  #proc = null
  #pending = new Map()
  #seq = 0
  #log = () => {}

  /**
   * @param exePath - helper 可执行文件路径（WSL 视角）。
   * @param capacity - helper 自己保留的临时帧数上限。
   * @param log - 诊断输出回调。
   */
  constructor(exePath, capacity, log) {
    this.#exePath = exePath
    this.#capacity = capacity
    if (typeof log === 'function') this.#log = log
  }

  /** 启动 helper（已启动则直接返回）。 */
  async start() {
    if (this.#proc !== null) return
    if (!existsSync(this.#exePath)) {
      throw new Error(`dsh-computer-use: 找不到 helper：${this.#exePath}`)
    }
    const proc = spawn(this.#exePath, [String(this.#capacity)], {
      stdio: ['pipe', 'pipe', 'pipe'],
    })
    proc.stderr.setEncoding('utf8')
    proc.stderr.on('data', (chunk) => this.#log(`helper stderr: ${String(chunk).trim()}`))
    proc.stdout.setEncoding('utf8')
    createInterface({ input: proc.stdout, crlfDelay: Number.POSITIVE_INFINITY }).on('line', (line) => {
      const text = line.trim()
      if (!text.startsWith('{')) return
      // 协议是严格的一问一答，所以"最早那条未决请求"就是这条响应的归属。
      const entry = this.#pending.entries().next()
      if (entry.done === true) return
      const [id, waiter] = entry.value
      this.#pending.delete(id)
      clearTimeout(waiter.timer)
      try {
        waiter.resolve(JSON.parse(text))
      } catch (error) {
        waiter.reject(new Error(`dsh-computer-use: helper 返回了非 JSON：${text.slice(0, 200)}`))
      }
    })
    // helper 是子进程，随时可能死（自己崩、被外部杀、被换 exe）：**stdin 的 'error'（EPIPE）
    // 如果没有监听器，Node 会把它当作未捕获的 'error' 事件直接掀掉整个宿主进程**——2026-09-22
    // 实测崩掉过一次 dsh web（栈顶就是 call() 里的 stdin.write）。所以两个流都必须自己兜住：
    // 收敛成"这次调用失败"，把 helper 标记为已死，下次调用自然重新拉起。
    proc.stdin.on('error', (error) => {
      this.#log(`helper 的 stdin 断开（${error.code ?? error.message}）——本次调用失败，下次调用会自动重拉`)
      this.#proc = null
      this.#failAll(`helper 的 stdin 出错（${error.code ?? error.message}）`)
    })
    proc.stdout.on('error', () => { /* 读端出错由下面的 close 兜底 */ })
    proc.once('error', (error) => {
      this.#log(`helper 启动失败：${error.message}`)
      this.#failAll(`helper 启动失败：${error.message}`)
    })
    proc.once('close', (code) => {
      this.#log(`helper 已退出（code ${code}）`)
      this.#proc = null
      this.#failAll(`helper 已退出（code ${code}）`)
    })
    this.#proc = proc
    await this.call({ cmd: 'ping' })
    this.#log(`helper 就绪（pid ${proc.pid ?? '?'}）`)
  }

  /**
   * 发一条请求并等它的一行响应。
   * @param payload - 形如 `{ cmd, ... }` 的请求体。
   * @returns helper 的响应对象。
   */
  async call(payload, timeoutMs = CALL_TIMEOUT_MS) {
    const proc = this.#proc
    if (proc === null) throw new Error('dsh-computer-use: helper 未启动')
    this.#seq += 1
    const id = this.#seq
    return await new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.#pending.delete(id)
        this.#log(`请求超时（${timeoutMs} ms）：${JSON.stringify(payload)}`)
        reject(new Error(`dsh-computer-use: helper 超时（${timeoutMs} ms）：${JSON.stringify(payload)}`
          + '。helper 是单线程串行的：若刚有 wait_change 正在等屏幕变化，这次调用可能是在**排队**，'
          + '而不是 helper 无响应——重试一次通常就通了（2026-09-22 这条消息曾把三个人一起带向"找进程死因"）。'))
      }, timeoutMs)
      this.#pending.set(id, { resolve, reject, timer })
      // 同步写入也可能直接抛（helper 已退出 / 管道已关）：这里兜住并立刻结算这次调用，
      // 否则异常会冒到工具层、变成一条看不懂的堆栈（更糟的是漏成未捕获事件掀掉宿主）。
      try {
        proc.stdin.write(`${JSON.stringify(payload)}\n`)
      } catch (error) {
        clearTimeout(timer)
        this.#pending.delete(id)
        this.#proc = null
        reject(new Error(`dsh-computer-use: 写不进 helper（${error.code ?? error.message}）`))
      }
    })
  }

  /** 停掉 helper 与采集线程。 */
  stop() {
    const proc = this.#proc
    this.#proc = null
    if (proc === null) return
    try { proc.stdin.write('{"cmd":"quit"}\n') } catch { /* 已关闭 */ }
    // 给 helper 一点时间自己收尾（它会停采集线程再退出），超时就强杀，不留孤儿进程。
    const killer = setTimeout(() => { try { proc.kill() } catch { /* 已退出 */ } }, 1500)
    killer.unref?.()
  }

  #failAll(reason) {
    for (const [, waiter] of this.#pending) {
      clearTimeout(waiter.timer)
      waiter.reject(new Error(`dsh-computer-use: ${reason}`))
    }
    this.#pending.clear()
  }
}

/**
 * 把 helper 抓到的帧读进来、交给 attachments，变成模型能看的图像。
 * @param ctx - Cordis 上下文。
 * @param frame - helper `capture`/`region` 的响应。
 * @param label - 附件名（便于排查）。
 * @returns 附件引用。
 */
async function frameToAttachment(ctx, frame, label) {
  const attachments = ctx.get('attachments')
  if (attachments === undefined) {
    throw new Error('dsh-computer-use: 没有 attachments 服务，无法把屏幕交给模型')
  }
  const wslPath = toWslPath(frame.path)
  let data
  try {
    data = readFileSync(wslPath)
  } catch (error) {
    throw new Error(`dsh-computer-use: 读不到帧文件 ${wslPath}（${error.message}）`)
  }
  const refs = await attachments.saveImages([{
    data,
    mediaType: 'image/jpeg',
    name: `${label}-${frame.seq}.jpg`,
  }])
  const ref = refs[0]
  if (ref === undefined) throw new Error('dsh-computer-use: 保存帧附件失败')
  return ref
}

// helper 的 reference_t / t 是它自己进程内的单调秒，调用方无法与墙钟或其它工具的时刻比对
// （实测踩过：跨工具换算时间戳会得到非线性偏差）。用"回包到达的墙钟"作锚点把基准帧时刻换算过来：
// 基准帧墙钟 = 回包墙钟 − (t − reference_t)，误差是一次 IPC 往返（几十毫秒）。
function baselineOfHelperFrame(raw, returnedAt) {
  if (!Number.isFinite(raw.reference_t) || !Number.isFinite(raw.t)) return {}
  const ageMs = Math.max(0, Math.round((Number(raw.t) - Number(raw.reference_t)) * 1000))
  return { baselineAt: new Date(returnedAt - ageMs).toISOString(), baselineAgeMs: ageMs }
}

/**
 * 动作类工具的统一外壳：调用 helper → 返回"命令 + 时间戳"。
 *
 * 动作工具一律**不声明** `isConcurrencySafe`：DSH 会把它们按提交顺序**串行**执行，
 * 这正是"一轮提交多个动作"要的语义（模型可以先移动再点击再输入，顺序有保证）。
 * @param ctx - Cordis 上下文。
 * @param session - helper 会话。
 * @param spec - 工具名、描述、参数 schema、以及到 helper 命令的映射。
 */
/**
 * 动作回执末尾的时间口径说明。
 *
 * 定位要说准：它**不是防线**——"文案能挡住误用"已被否证（主人论文实验里加过贴身警示，Agent 照样把回执
 * 当画面时刻用；2026-09-22 当天三个 Agent 也各自踩过）。它只做两件事：解释"为什么这里没有时刻"，
 * 并**堵住找替代通道**——否则调用方会以为工具坏了，或拿 `elapsedMs` / `host_clock_*` 反推一个时刻出来。
 *
 * 改文案时守住三条：① 绝不写"回执是准的"（那句会诱导混用，正是贴身警示失效的原因）；② 点明原因
 * （动作时刻与画面时刻差 1–5 帧）；③ 指明**唯一**替代来源（画面事件的时间戳）。
 */
const ACTION_TIME_TIP = '（刻意不含动作时刻：它与画面时刻差 1–5 帧、不是同一个量，混用会静默算错。'
  + '计时请以画面事件的时间戳为基准；`elapsedMs` 只是耗时，不能反推动作发生在哪一刻。）'

function registerActionTool(ctx, session, spec) {
  // spec.zoom：动作成功后以**落点**为心取一张放大图（`{half, scale, quality, anchor}`），
  // 让"点没点对"当场可判——否则模型只能再补一次观察，而一轮观察要 3–6 秒。
  // 取景失败只写进 detail（附图是增强，不是动作的一部分），绝不让动作本身失败。
  const zoom = spec.zoom ?? null
  ctx.tools.register({
    name: spec.name,
    description: spec.description,
    parameters: spec.parameters,
    output: {
      schema: {
        type: 'object',
        additionalProperties: false,
        properties: {
          ok: { type: 'boolean' },
          detail: { type: 'string', description: '结果摘要：含 injected / winerr（注入是否成功）、落点、窗口，以及本次耗时 elapsedMs。**刻意不含执行时刻**——时刻另有画面帧的采集时刻可作时间基准。' },
          ...(zoom === null ? {} : {
            zoomedAt: {
              type: 'object',
              description: '附图覆盖的原屏矩形（屏幕像素）——图内量到的位置 + 这个起点 = 屏幕坐标。',
              additionalProperties: false,
              properties: {
                x: { type: 'integer' },
                y: { type: 'integer' },
                width: { type: 'integer' },
                height: { type: 'integer' },
                scale: { type: 'integer' },
              },
            },
            attachment: {
              type: 'object',
              required: ['attachmentId', 'mediaType', 'bytes', 'width', 'height'],
              additionalProperties: true,
              properties: {
                attachmentId: { type: 'string' },
                mediaType: { type: 'string' },
                bytes: { type: 'integer' },
                width: { type: 'integer' },
                height: { type: 'integer' },
                name: { type: 'string' },
              },
            },
          }),
        },
        required: ['ok', 'detail'],
      },
      render: (_args, value) => (value.attachment === undefined
        ? [{ type: 'text', text: value.detail }]
        : [
          {
            type: 'text',
            text: `${value.detail}　落点附图：以落点为心取原屏 ${value.zoomedAt.width}×${value.zoomedAt.height}`
              + `（起点 (${value.zoomedAt.x}, ${value.zoomedAt.y})、放大 ${value.zoomedAt.scale} 倍，最近邻）——`
              + '用它确认是否点中目标；图内的位置 + 该起点 = 屏幕坐标。',
          },
          { type: 'image', attachment: value.attachment },
        ]),
    },
    async execute(args) {
      // 动作工具也可能是本轮**第一个**被调用的工具（helper 还没起来）：只读工具各自
      // 都先 start 了，动作工具走同一个 session，所以这里也必须先确保它在跑，
      // 否则会以「helper 未启动」失败——而这个失败与参数、坐标都无关，很难查。
      await session.start()
      const payload = spec.toPayload(args ?? {})
      // 个别工具本身就要等（`wait` 可以等 60 秒），统一 30 秒超时会把它们的合法等待判成失败。
      const timeoutMs = typeof spec.timeoutMs === 'function'
        ? spec.timeoutMs(args ?? {})
        : (spec.timeoutMs ?? CALL_TIMEOUT_MS)
      const callStartedAt = Date.now()
      const result = await session.call(payload, timeoutMs)
      // 这一次调用花了多久。只做**量级参考**：宿主钟有约 10% 偏差且会偶发跳变，而且它**测不到**"被别的
      // 会话插队"——插队发生在 helper 读到这条命令之前（主循环被占用时不读 stdin），那段时间不在这个差值里。
      const elapsedMs = Date.now() - callStartedAt
      if (result.ok !== true) {
        throw new Error(`dsh-computer-use: ${spec.name} 失败：${String(result.error ?? '未知原因')}`)
      }
      // 动作回执**不含执行时刻**（2026-09-22 主人定，日常与评测一致）：`t_before`/`t_after` 是 helper QPC
      // 的**绝对时刻**，与"画面变化的时刻"是两个不同基准（差 1–5 帧渲染延迟）——两个数各自都对，混用却会
      // **静默**算出偏 1–5 帧的时间。实证过"贴身警示挡不住"（主人论文实验 + 当天三个 Agent 各自踩过），
      // 所以从形态上不给：直接把时刻字段剥掉，而不是提醒别用。
      // 与时间无关的诊断字段（injected/winerr/落点/window）保留——"按键到底有没有注入成功"仍要能判。
      const rest = {}
      for (const [key, value] of Object.entries(result)) {
        if (key === 'ok' || key === 't_before' || key === 't_after') continue
        rest[key] = value
      }
      const base = {
        ok: true,
        detail: `${spec.name} 已执行：${JSON.stringify({ ...rest, elapsedMs })}${ACTION_TIME_TIP}`,
      }
      // verify: false 时跳过附图：连点、或只在意"点了就行"的场景能省一张图（约 400 token）。
      if (zoom === null || (args ?? {}).verify === false) return base
      try {
        const anchor = zoom.anchor(args ?? {}, result)
        if (anchor === null || anchor === undefined) return base
        const shot = await session.call({
          cmd: 'region',
          x: Math.round(Number(anchor.x)),
          y: Math.round(Number(anchor.y)),
          w: zoom.half * 2,
          h: zoom.half * 2,
          scale: zoom.scale,
          quality: zoom.quality,
          // center=1：把落点当中心、由 helper 夹到屏内——点在屏幕边缘时也能取到图。
          center: 1,
        })
        if (shot.ok !== true) throw new Error(String(shot.error ?? '未知原因'))
        return {
          ...base,
          attachment: await frameToAttachment(ctx, shot, 'zoom'),
          zoomedAt: {
            x: Number(shot.x),
            y: Number(shot.y),
            width: Number(shot.w),
            height: Number(shot.h),
            scale: Number(shot.scale ?? zoom.scale),
          },
        }
      } catch (error) {
        // 附图取不到不该影响动作本身，但也不能静默——理由写进 detail，读的人才知道这次没有回执图。
        return { ...base, detail: `${base.detail}（落点附图取景失败：${String(error.message)}）` }
      }
    },
  })
}

/**
 * 插件入口。
 * @param ctx - Cordis 上下文。
 * @param config - 来自 cordis.patch.yml 的配置。
 */
export function apply(ctx, config = {}) {
  const resolved = { ...DEFAULTS, ...(config ?? {}) }
  const exePath = resolved.helperPath !== ''
    ? resolved.helperPath
    : join(HERE, '..', 'helper', 'CuHelper.exe')
  // 诊断两条腿：宿主日志（当场看得到）+ 落盘（终端滚掉了、或事后查死因时还能翻）。
  const trace = createTraceLog()
  const session = new HelperSession(exePath, resolved.frameCapacity, (line) => {
    console.warn(`dsh-computer-use: ${line}`)
    trace(line)
  })

  // `read_image` 与「只返回一张图」的工具同族：整图读会被缩到像素预算内，带 `region` 又能把任意一张图
  // 裁成一块 1:1 交给模型——两条路都能绕开 `screen_grid` 的分块形态。`denyReadImage` 打开时用 guard
  // **拦下全部 `read_image` 调用**（带不带 `region` 都拦）；拒绝文案里给出正确替代路径，
  // 否则模型会以为工具坏了。
  if (resolved.denyReadImage === true && typeof ctx.tools?.guard === 'function') {
    ctx.tools.guard(execution => {
      if (execution?.name !== 'read_image') return undefined
      return 'read_image 已停用（带不带 region 都拦）：单张图看不清细节——整屏图会被缩到像素预算内，'
        + '裁一块又只见局部。看清画面请用 screen_grid（同一次抓帧给缩略图 + 全部 1:1 分块，一次看全）；'
        + '要看过去的某一刻用 screen_grid({atSeconds})。'
    })
  }

  // 观察：看整块屏幕。模型拿到的是**缩放后**的图（受 DSH 的像素预算约束），
  // 所以描述里必须写明"这张图是 2560×1440 缩下来的"，否则模型会按错的尺度报坐标。
  // ⚠️ **默认不注册**：主人 2026-09-22 23:23「只返回一张图的 CU 工具都给禁掉」，
  // 见 DEFAULTS 里 `singleImageTools` 的说明；要看清屏幕请用 screen_grid（同帧缩略图 + 1:1 分块）。
  if (resolved.singleImageTools === true) ctx.tools.register({
    name: 'screen_observe',
    description:
      '看一眼 Windows 当前**整块屏幕**（一张整屏图；DSH 会把它缩到像素预算内），返回截图与采集时刻。'
      + '它适合**快速确认当前状态**——窗口切到哪了、上一步动作生效没有、整体布局如何。'
      + '但**要看清小控件/小字、或要精确定位目标时不要用它**：整屏缩图本机是 2560×1440 → 1066×600，'
      + '一个 46 px 的控件只剩 19 px，会看不清、进而来回琢磨。那种任务用 **screen_grid**'
      + '（同帧六块按原始像素 + 一张缩略图定方位），实测定位误差中位 28.3 px → 5.7 px'
      + '（该实验用合成图 + 直连 API 的理想图源，真实截屏的幅度可能略小）。'
      + '无论用哪个：动作之后必须重新观察，上一次观察已经作废。'
      + '截图的坐标口径：返回的不是原始像素图，先看结果里的 width/height 说明。',
    parameters: { type: 'object', additionalProperties: false, properties: {} },
    output: {
      schema: {
        type: 'object',
        additionalProperties: false,
        properties: {
          attachment: {
            type: 'object',
            // 平台在缩图时会注入 originalDimensions、裁剪时注入 croppedFrom；
            // 漏声明任何一个，整支截图都会被判成"返回了非法输出"。
            required: ['attachmentId', 'mediaType', 'bytes', 'width', 'height'],
            additionalProperties: true,
            properties: {
              attachmentId: { type: 'string' },
              mediaType: { type: 'string' },
              bytes: { type: 'integer' },
              width: { type: 'integer' },
              height: { type: 'integer' },
              name: { type: 'string' },
            },
          },
          width: { type: 'integer', description: '屏幕宽度（原始像素）。' },
          height: { type: 'integer', description: '屏幕高度（原始像素）。' },
          capturedAtSeconds: {
            type: 'number',
            description: '这一帧的采集时刻（helper 的单调秒）。回看历史帧时用它做时间基准。',
          },
        },
        required: ['attachment', 'width', 'height', 'capturedAtSeconds'],
      },
      render: (_args, value) => [
        { type: 'image', attachment: value.attachment },
        {
          type: 'text',
          text: `屏幕 ${value.width}×${value.height}，采集于 t=${value.capturedAtSeconds.toFixed(3)}s。`
            + '要看清细节或精确定位目标，改用 screen_grid（同帧六块 + 缩略图）。'
            + '动作之后必须重新观察。',
        },
      ],
    },
    async execute() {
      await session.start()
      const frame = await session.call({ cmd: 'capture', quality: resolved.jpegQuality })
      if (frame.ok !== true) throw new Error(`dsh-computer-use: 截屏失败：${String(frame.error ?? '')}`)
      const attachment = await frameToAttachment(ctx, frame, 'screen')
      return {
        attachment,
        width: Number(frame.w),
        height: Number(frame.h),
        capturedAtSeconds: Number(frame.t_before),
      }
    },
  })

  // 区域裁剪：整屏图受像素预算所限必然被缩小（本机 2560×1440 → 1066×600，一个 46 px 的
  // 控件在模型眼里只剩 19 px），而裁一块 ≤640000 px 的区域**不会触发缩放**、原样交给模型，
  // 等于给它一块 1:1 的视野。这是"精点"路径：先整屏定位，再裁块确认，然后才动手。
  // ⚠️ **默认不注册**：同上（只返回一张图）。要看细节请用 `screen_grid`——它固定交付
  // 同帧缩略图 + 全部分块；看过去某一帧就用 `screen_grid({atSeconds})`。
  if (resolved.singleImageTools === true) ctx.tools.register({
    name: 'region_observe',
    description:
      '看屏幕上的**一块区域**（按原始像素给，不缩放）。整屏图会被 DSH 缩到像素预算内，'
      + '小控件因此看不清；要精确点击小目标时用它：先 screen_grid 定位（缩略图 + 1:1 分块），'
      + '再把那一块裁出来看——裁剪结果 ≤640000 像素时按 1:1 原样交给模型。'
      + '坐标一律是**屏幕像素**（左上角为原点），不是任何截图上的像素。'
      + '返回值里的 croppedFrom 说明这块在原屏的哪个位置：屏幕坐标 = croppedFrom.x + 块内 x。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        x: { type: 'integer', description: '区域左边缘的屏幕 x 坐标（像素）。' },
        y: { type: 'integer', description: '区域上边缘的屏幕 y 坐标（像素）。' },
        width: { type: 'integer', description: '区域宽度（像素）。' },
        height: { type: 'integer', description: '区域高度（像素）。' },
        quality: { type: 'integer', description: 'JPEG 质量；省略则用配置里的值。' },
        scale: {
          type: 'integer',
          description: '整数放大倍数（1 = 不放大）。放大是为了把**更小的区域**铺满同一份像素预算',
        },
      },
      required: ['x', 'y', 'width', 'height'],
    },
    output: {
      schema: {
        type: 'object',
        additionalProperties: false,
        properties: {
          attachment: {
            type: 'object',
            required: ['attachmentId', 'mediaType', 'bytes', 'width', 'height'],
            additionalProperties: true,
            properties: {
              attachmentId: { type: 'string' },
              mediaType: { type: 'string' },
              bytes: { type: 'integer' },
              width: { type: 'integer' },
              height: { type: 'integer' },
              name: { type: 'string' },
            },
          },
          width: { type: 'integer', description: '这块的宽度（原始像素）。' },
          height: { type: 'integer', description: '这块的高度（原始像素）。' },
          scale: { type: 'integer', description: '整数放大倍数；1 表示原样。' },
          outputWidth: { type: 'integer', description: '交给模型的那张图的宽度（含放大）。' },
          outputHeight: { type: 'integer', description: '交给模型的那张图的高度（含放大）。' },
          croppedFrom: {
            type: 'object',
            additionalProperties: false,
            description: '这块取自原屏的哪个矩形——还原屏幕坐标要靠它。',
            required: ['x', 'y', 'width', 'height', 'sourceWidth', 'sourceHeight'],
            properties: {
              x: { type: 'integer' },
              y: { type: 'integer' },
              width: { type: 'integer' },
              height: { type: 'integer' },
              sourceWidth: { type: 'integer' },
              sourceHeight: { type: 'integer' },
            },
          },
          capturedAtSeconds: {
            type: 'number',
            description: '这一块的采集时刻（helper 的单调秒）。',
          },
        },
        required: ['attachment', 'width', 'height', 'croppedFrom', 'capturedAtSeconds'],
      },
      render: (_args, value) => [
        { type: 'image', attachment: value.attachment },
        {
          type: 'text',
          text: `区域 ${value.width}×${value.height}，取自原屏 ${value.croppedFrom.sourceWidth}×`
            + `${value.croppedFrom.sourceHeight} 的 (${value.croppedFrom.x}, ${value.croppedFrom.y})，`
            + `采集于 t=${value.capturedAtSeconds.toFixed(3)}s。`
            + '屏幕坐标 = 这块的偏移 + 你在图里量到的位置。动作之后必须重新观察。',
        },
      ],
    },
    async execute(args) {
      await session.start()
      // 区域越出屏幕时不报错：helper 会裁到屏幕内（起点完全在图外才抛错），
      // 返回的 w/h 是**实际**裁到的尺寸，croppedFrom 据此填写。
      const frame = await session.call({
        cmd: 'region',
        x: Math.round(args.x),
        y: Math.round(args.y),
        w: Math.round(args.width),
        h: Math.round(args.height),
        quality: Number.isInteger(args.quality) ? args.quality : resolved.jpegQuality,
        scale: Number.isInteger(args.scale) ? args.scale : 1,
      })
      if (frame.ok !== true) throw new Error(`dsh-computer-use: 裁块失败：${String(frame.error ?? '')}`)
      const info = await session.call({ cmd: 'ping' })
      const attachment = await frameToAttachment(ctx, frame, 'region')
      return {
        attachment,
        width: Number(frame.w),
        height: Number(frame.h),
        scale: Number(frame.scale ?? 1),
        outputWidth: Number(frame.out_w ?? frame.w),
        outputHeight: Number(frame.out_h ?? frame.h),
        croppedFrom: {
          x: Number(frame.x),
          y: Number(frame.y),
          width: Number(frame.w),
          height: Number(frame.h),
          sourceWidth: Number(info.primary?.w ?? 0),
          sourceHeight: Number(info.primary?.h ?? 0),
        },
        capturedAtSeconds: Number(frame.t_before),
      }
    },
  })

  // 同帧切块：一次抓帧 → 切成 cols×rows 块 → 一次交给模型。
  //
  // 为什么"同帧"是硬要求：连续调 N 次 region 各自抓一帧，在滚动、动画、视频里
  // 拼出来的画面现实中并不存在，而模型不会知道。块数由调用方按屏幕尺寸与像素预算算
  // （每块 ≤640000 像素才会原样进模型），这里只提供机制、不写死排版。
  ctx.tools.register({
    name: 'screen_grid',
    description:
      '**看屏幕的首选方式**：一次抓帧，切成 cols×rows 块，**外加一张同帧缩略图**，一起交给你。'
      + '为什么不是"整屏一张图"：整屏会被缩到像素预算内（本机 2560×1440 → 1066×600，'
      + '一个 46 px 的控件只剩 19 px），看不清就会反复琢磨——实测同一目标的定位误差中位 '
      + '从 28.3 px 降到 5.7 px，而推理 token 反而减半（14.4k → 7.4k）。'
      + '⚠️ 那组实验用的是**合成图 + 直连 API**（无损、无抗锯齿 = 理想图源）；真实截屏是 JPEG 有损，'
      + '幅度可能略小。可靠的是方向与机制（"看得清"比"看得多"重要），不是那个绝对值。'
      + '块给精度（每块 1:1 原始像素），缩略图给方位（一眼看全局、判断目标在哪一块），两者同帧。'
      + '块数按像素预算选：本机取 2×3，每块 1280×480 = 614400 px 刚好原样进模型；'
      + '切得更粗（2×2 的 1280×720）会超预算被缩回去、反而更糊。'
      + '每块带它在原屏的左上角坐标，屏幕坐标 = 块的 x/y + 块内位置。'
      + '**报位置时请只报「块编号 + 块内像素」**（例如"第 3 块 (337, 480)"），或直接说在缩略图的哪一侧；'
      + '**不要自己在脑内做跨块换算**——跨层换算容易引入整块级的偏差，'
      + '而"块内位置 + 调用方加偏移"没有这个失败模式。'
      + '**回看过去某一帧**（那一刻已不在屏上）：先用 `screen_frames` 取回那一段的帧（每张都带采集时刻），'
      + '再把选中的时刻传给 `atSeconds` ⇒ 一次就拿到那一帧的缩略图 + 全部分块，'
      + '形态与看当前屏完全一致（不要一块一块地看：那要多次调用，还可能各取到不同帧）。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        cols: { type: 'integer', description: '横向块数（1–16，默认 2）；与 rows 一起决定分块粒度，'
          + '**总块数不得少于 6**（`cols × rows ≥ 6`）——本工具不交付"少看图"的形态。' },
        rows: { type: 'integer', description: '纵向块数（1–16，默认 3）；**总块数不得少于 6**。' },
        quality: { type: 'integer', description: 'JPEG 质量；省略则用配置里的值。' },
        scale: { type: 'integer', description: '整数放大倍数（1 = 不放大）。' },
        atSeconds: {
          type: 'number',
          description: '要看**过去**某一刻的画面时给这个：从录制缓冲里取该时刻那一帧当图源，'
            + '交付形态与看当前屏**完全一样**（同帧缩略图 + 全部分块，一次给全）。'
            + '时刻取自 `screen_frames` 交回的帧的采集时刻（helper 单调钟秒）。'
            + '不给 = 抓当前屏。**回看某一帧就用它**——一次给全，不必多次调用、也不会各取到不同帧。',
        },
      },
    },
    output: {
      schema: {
        type: 'object',
        additionalProperties: false,
        properties: {
          cols: { type: 'integer' },
          rows: { type: 'integer' },
          scale: { type: 'integer' },
          sourceWidth: { type: 'integer' },
          sourceHeight: { type: 'integer' },
          capturedAtSeconds: { type: 'number' },
          fromBuffer: {
            type: 'boolean',
            description: 'true = 图源是录制缓冲里的历史帧（回看），false/缺省 = 刚抓的当前屏。',
          },
          thumbnail: {
            type: 'object',
            additionalProperties: false,
            required: ['attachment', 'width', 'height'],
            properties: {
              attachment: {
                type: 'object',
                required: ['attachmentId', 'mediaType', 'bytes', 'width', 'height'],
                additionalProperties: true,
                properties: {
                  attachmentId: { type: 'string' },
                  mediaType: { type: 'string' },
                  bytes: { type: 'integer' },
                  width: { type: 'integer' },
                  height: { type: 'integer' },
                  name: { type: 'string' },
                },
              },
              width: { type: 'integer', description: '缩略图宽度（像素）。' },
              height: { type: 'integer', description: '缩略图高度（像素）。' },
            },
          },
          tiles: {
            type: 'array',
            items: {
              type: 'object',
              additionalProperties: false,
              required: ['attachment', 'row', 'col', 'x', 'y', 'width', 'height', 'outputWidth', 'outputHeight'],
              properties: {
                attachment: {
                  type: 'object',
                  required: ['attachmentId', 'mediaType', 'bytes', 'width', 'height'],
                  additionalProperties: true,
                  properties: {
                    attachmentId: { type: 'string' },
                    mediaType: { type: 'string' },
                    bytes: { type: 'integer' },
                    width: { type: 'integer' },
                    height: { type: 'integer' },
                    name: { type: 'string' },
                  },
                },
                row: { type: 'integer', description: '从 0 起的行号。' },
                col: { type: 'integer', description: '从 0 起的列号。' },
                x: { type: 'integer', description: '这块左上角在原屏的 x。' },
                y: { type: 'integer', description: '这块左上角在原屏的 y。' },
                width: { type: 'integer' },
                height: { type: 'integer' },
                outputWidth: { type: 'integer' },
                outputHeight: { type: 'integer' },
              },
            },
          },
        },
        required: ['cols', 'rows', 'scale', 'sourceWidth', 'sourceHeight', 'capturedAtSeconds', 'tiles'],
      },
      render: (_args, value) => [
        ...(value.thumbnail === undefined ? [] : [{ type: 'image', attachment: value.thumbnail.attachment }]),
        ...value.tiles.map(tile => ({ type: 'image', attachment: tile.attachment })),
        {
          type: 'text',
          text: `同一帧：${value.thumbnail === undefined ? '' : '先看缩略图定方位，再按块看细节；'}`
            + `${value.cols}×${value.rows} 块（原屏 ${value.sourceWidth}×${value.sourceHeight}，`
            + `${value.fromBuffer === true ? '回看录制缓冲里的帧，采集于 ' : '采集于 '}`
            + `t=${value.capturedAtSeconds.toFixed(3)}s）。块编号 = (行-1)×${value.cols} + 列，左上角是第 1 块；`
            + '本次给出 '
            + value.tiles.map(tile => `第 ${tile.row * value.cols + tile.col + 1} 块 (${tile.x},${tile.y})`).join('、')

            + '。屏幕坐标 = 该块的坐标 + 你在块内量到的位置；'
            + (value.fromBuffer === true
              ? '这是**历史画面**——要判断"现在"必须重新观察。'
              : '动作之后必须重新观察。'),
        },
      ],
    },
    async execute(args) {
      await session.start()
      // 图源：给了 atSeconds 就从录制缓冲里取那一帧（回看），否则抓当前屏。
      // 窗口给 ±0.5 秒再挑最接近的一帧：时刻来自 screen_frames 交回的帧，正常情况下误差为 0；
      // 窗口开太窄会因片边界/采样间隔取不到帧（h264 的片内时刻还要靠片首片尾线性映射）。
      let sourceFramePath = ''
      let sourceFrameAt
      if (Number.isFinite(args.atSeconds)) {
        const want = Number(args.atSeconds)
        const got = await session.call({ cmd: 'frames', from: want - 0.5, to: want + 0.5, limit: 8 })
        if (got.ok !== true) throw new Error(`dsh-computer-use: 取回看帧失败：${String(got.error ?? '')}`)
        const list = Array.isArray(got.frames) ? got.frames : []
        if (list.length === 0) {
          throw new Error(`dsh-computer-use: 录制缓冲里拿不到 t=${want.toFixed(3)}s 附近的帧——`
            + '要么没在录（先 screen_watch start），要么该时刻超出缓冲覆盖范围。'
            + '用 screen_frames({lastSeconds: 1}) 看当前可用区间与每帧的采集时刻。')
        }
        let best = list[0]
        for (const item of list) {
          if (Math.abs(Number(item.t) - want) < Math.abs(Number(best.t) - want)) best = item
        }
        sourceFramePath = String(best.path ?? '')
        sourceFrameAt = Number(best.t)
      }
      // 主人 2026-09-22 23:30：「screen_grid 我就要返回 7 张图，一次看多张图有益」。
      // 所以它**只有一种交付形态**：同帧缩略图 + 全部分块（本机 2×3 ⇒ 6 块 + 1 缩略图 = 7 张）。
      // 参数里不再有"少看几张"的口子（`blocks` / `includeThumbnail` 已删），块数下限也钉住：
      // 传更小的 cols/rows 会让"一次看全"落空，所以要求 `cols × rows ≥ 6`。
      const wantCols = Number.isInteger(args.cols) ? args.cols : 2
      const wantRows = Number.isInteger(args.rows) ? args.rows : 3
      if (wantCols * wantRows < 6) {
        throw new Error('dsh-computer-use: screen_grid 不交付"少看图"的形态——总块数不得少于 6'
          + `（现在是 ${wantCols}×${wantRows} = ${wantCols * wantRows} 块）；` 
          + '默认 2×3 = 6 块 + 1 张同帧缩略图 = 7 张。')
      }
      const grid = await session.call({
        cmd: 'grid',
        cols: Number.isInteger(args.cols) ? args.cols : 2,
        rows: Number.isInteger(args.rows) ? args.rows : 3,
        quality: Number.isInteger(args.quality) ? args.quality : resolved.jpegQuality,
        scale: Number.isInteger(args.scale) ? args.scale : 1,
        // ≤0 让 helper 不出缩略图。
        // 缩略图恒开：它是"方位"这一半信息，没有它 6 块就只是 6 块局部。
        thumb_max_pixels: 640000,
        // 空串 = 抓当前屏；非空 = 以那张帧文件为图源（helper 侧两条路共用同一套切块逻辑）。
        frame: sourceFramePath,
      })
      if (grid.ok !== true) throw new Error(`dsh-computer-use: 切块失败：${String(grid.error ?? '')}`)
      let thumbnail
      const thumb = grid.thumbnail
      if (thumb !== undefined && String(thumb.path ?? '') !== '') {
        const attachment = await frameToAttachment(ctx, { path: thumb.path, seq: 'thumb' }, 'grid-thumbnail')
        thumbnail = { attachment, width: Number(thumb.w ?? 0), height: Number(thumb.h ?? 0) }
      }
      const cols = Number(grid.cols)
      const rows = Number(grid.rows)
      // 全部块，一个不少：多给几张图的 token 花在"少走几轮"上更划算（每轮往返 5–13 秒 + 一大段推理）。
      const selected = grid.tiles ?? []
      const tiles = []
      for (const tile of selected) {
        const attachment = await frameToAttachment(ctx, tile, `grid-r${tile.row}c${tile.col}`)
        tiles.push({
          attachment,
          row: Number(tile.row),
          col: Number(tile.col),
          x: Number(tile.x),
          y: Number(tile.y),
          width: Number(tile.w),
          height: Number(tile.h),
          outputWidth: Number(tile.out_w ?? tile.w),
          outputHeight: Number(tile.out_h ?? tile.h),
        })
      }
      const fromBuffer = sourceFramePath !== ''
      return {
        cols: Number(grid.cols),
        rows: Number(grid.rows),
        scale: Number(grid.scale ?? 1),
        sourceWidth: Number(grid.screen_w),
        sourceHeight: Number(grid.screen_h),
        // 回看模式的"采集时刻"必须取帧自己的时刻：helper 的 t_before/t_after 只在抓屏路径有意义
        // （读文件那条路没有抓屏动作，给的是 0）。
        capturedAtSeconds: fromBuffer ? Number(sourceFrameAt) : Number(grid.t_before),
        fromBuffer,
        ...(thumbnail === undefined ? {} : { thumbnail }),
        tiles,
      }
    },
  })

  // 变化检测：把"画面哪里变了"交给像素去算，而不是让模型逐帧比对。
  // ⚠️ **默认不注册**：主人 2026-09-22 20:13 定，见 DEFAULTS 里 `diffTool` 的说明。
  if (resolved.diffTool === true) ctx.tools.register({
    name: 'screen_diff',
    description:
      '**看"变了什么"**：给一段时间窗，返回窗口内**变化区域的矩形序列**（相邻帧差分算出来的），而不是一批图。'
      + '回答的是"刚才有没有动、动在哪一块"——追踪移动物体、判断你的动作有没有让页面响应、定位瞬时事件的落点。'
      + '在此之前得自己取一批帧再逐张比对（每轮几秒）；这一步现在由像素算完，你只拿结论。'
      + '⚠️ 它**不告诉你变的是什么**：拿到矩形后仍要用 `screen_grid({atSeconds})` 去看那一刻的画面。'
      + '⚠️ 要先有采集（`screen_watch start`）才有帧可算；窗口落在缓冲覆盖之外会被如实报空。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        from: { type: 'number', description: '窗口起点（秒，helper 时间轴；省略则由 lastSeconds 推）。' },
        to: { type: 'number', description: '窗口终点（秒）；省略表示到最新。' },
        lastSeconds: { type: 'number', description: '不给 from 时用它推起点（默认 3 秒）。' },
        limit: { type: 'integer', description: '窗口内取多少帧参与比较（相邻两两成对，默认 8，上限 24）。' },
        cell: { type: 'integer', description: '网格单元边长（像素，默认 160）：越小定位越细、噪声越多。' },
        ratio: { type: 'number', description: '单元内变化像素比例超过它才算"这个单元变了"（默认 0.02）。' },
        maxBoxes: { type: 'integer', description: '每对帧最多回几个矩形（默认 8，按面积从大到小）。' },
      },
    },
    output: {
      schema: {
        type: 'object',
        additionalProperties: false,
        properties: {
          frames: { type: 'integer', description: '参与比较的帧数。' },
          cell: { type: 'integer', description: '本次使用的网格单元边长。' },
          changes: {
            type: 'array',
            description: '每个相邻帧对一条；boxes 为空表示这一对之间没有超过阈值的变化。',
            items: {
              type: 'object',
              additionalProperties: false,
              required: ['tFrom', 'tTo', 'boxes'],
              properties: {
                tFrom: { type: 'number' },
                tTo: { type: 'number' },
                boxes: {
                  type: 'array',
                  items: {
                    type: 'object',
                    additionalProperties: false,
                    required: ['x', 'y', 'width', 'height'],
                    properties: {
                      x: { type: 'integer', description: '矩形左上角 x（屏幕像素）。' },
                      y: { type: 'integer', description: '矩形左上角 y（屏幕像素）。' },
                      width: { type: 'integer' },
                      height: { type: 'integer' },
                    },
                  },
                },
              },
            },
          },
        },
        required: ['frames', 'cell', 'changes'],
      },
      render: (_args, value) => {
        const describe = change => {
          if (change.boxes.length === 0) return `· ${change.tFrom.toFixed(2)}→${change.tTo.toFixed(2)}s：无`
          // 自己挑最大的一块、不假设上游排过序："最大"是对模型的承诺，由这里保证。
          const biggest = change.boxes.reduce((left, right) =>
            (right.width * right.height > left.width * left.height ? right : left))
          return `· ${change.tFrom.toFixed(2)}→${change.tTo.toFixed(2)}s：${change.boxes.length} 处，最大 `
            + `${biggest.width}×${biggest.height} 在 (${biggest.x},${biggest.y})`
        }
        const moved = value.changes.filter(change => change.boxes.length > 0)
        return [{
          type: 'text',
          text: value.changes.length === 0
            ? `没有可比较的帧对（窗口里不足 2 帧）——先 screen_watch start 录一段。`
            : `比较了 ${value.frames} 帧（相邻两两成对，共 ${value.changes.length} 对），其中 ${moved.length} 对之间有变化：\n`
              + value.changes.map(describe).join('\n')
              + '\n坐标与屏幕同口径；要看清变的是什么，用 screen_grid({atSeconds}) 看那一刻的画面。',
        }]
      },
    },
    async execute(args) {
      await session.start()
      let from = Number.isFinite(args.from) ? Number(args.from) : undefined
      const to = Number.isFinite(args.to) ? Number(args.to) : 1e9
      if (from === undefined) {
        const latest = await session.call({ cmd: 'latest' })
        if (latest.ok !== true) throw new Error(`dsh-computer-use: ${await describeEmptyWindow(session)}`)
        const window = Number.isFinite(args.lastSeconds) ? Number(args.lastSeconds) : 3
        from = Math.max(0, Number(latest.t ?? 0) - window)
      }
      const raw = await session.call({
        cmd: 'diff',
        from,
        to,
        limit: Number.isInteger(args.limit) ? Math.min(24, Math.max(2, args.limit)) : 8,
        cell: Number.isInteger(args.cell) ? Math.min(640, Math.max(16, args.cell)) : 160,
        ratio: Number.isFinite(args.ratio) ? Math.min(1, Math.max(0.0001, args.ratio)) : 0.02,
        max_boxes: Number.isInteger(args.maxBoxes) ? Math.min(24, Math.max(1, args.maxBoxes)) : 8,
      })
      if (raw.ok !== true) throw new Error(`dsh-computer-use: 差分失败：${String(raw.error ?? '')}`)
      return {
        frames: Number(raw.frames ?? 0),
        cell: Number(raw.cell ?? 160),
        changes: (raw.changes ?? []).map(change => ({
          tFrom: Number(change.t_from),
          tTo: Number(change.t_to),
          boxes: (change.boxes ?? []).map(box => ({
            x: Number(box.x),
            y: Number(box.y),
            width: Number(box.w),
            height: Number(box.h),
          })),
        })),
      }
    },
  })

  // 持续采集：实时界面的正解不是"更快地截图"，而是让 helper 自己按固定间隔抓帧进环形缓冲，
  // 事后按时间戳把那一小段取回来——模型的"观察→思考→动作"一轮要好几秒，而画面变化可能
  // 只持续 1 秒，靠提高观察频率追不上；采集线程不受这个限制。
  ctx.tools.register({
    name: 'screen_watch',
    description:
      '开/关**持续采集**（helper 自己按固定间隔抓帧进环形缓冲），并查看状态。'
      + '动态界面里这是唯一跟得上节奏的办法：你的"观察→思考→动作"一轮要好几秒，'
      + '而画面变化可能只持续一秒；先 start 让它自己录，事后用 screen_frames 把那一秒取回来看。'
      + `成本随帧率上升（本机实测 30 fps ≈ 单核 44%、写盘约 9 MB/s），所以按需开、用完 stop；默认 ${Math.round(1000 / DEFAULTS.frameIntervalMs)} fps。`,
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        action: { type: 'string', enum: ['start', 'stop', 'stats'], description: 'start 开始录、stop 停止、stats 看状态。' },
        fps: {
          type: 'integer',
          description: `start 时的目标帧率（1–60，默认 ${Math.round(1000 / DEFAULTS.frameIntervalMs)}）。`
            + '只是为了"看个大概"时，调低到 10 能明显省 CPU 与磁盘。',
        },
        capacity: { type: 'integer', description: `环形缓冲保留的帧数（默认 ${DEFAULTS.frameCapacity}，满了丢最旧的）。` },
        quality: { type: 'integer', description: 'JPEG 质量；省略则用配置里的值。' },
        codec: {
          type: 'string',
          enum: ['jpeg', 'h264'],
          description: '帧的存储编码（默认 jpeg）。h264 = 内存切片：同分辨率同帧率下体积约为逐帧 JPEG 的'
            + '二十分之一，因此能常驻 20 分钟；代价是取帧要现场解码（每帧几十毫秒）。'
            + '正在写的那一片默认不在环里，但取帧或问"最新一帧"时会让采集端立刻结算它（最多等约 1 秒）——'
            + '不用自己等片封口。',
        },
      },
      required: ['action'],
    },
    output: {
      schema: {
        type: 'object',
        additionalProperties: false,
        properties: {
          action: { type: 'string' },
          running: { type: 'boolean' },
          frames: { type: 'integer', description: '缓冲里的帧数。' },
          spanSeconds: { type: 'number', description: '缓冲覆盖的时间跨度。' },
          fps: { type: 'number', description: '实测帧率。' },
          bytes: { type: 'integer', description: '缓冲占用的字节数。' },
          intervalMs: { type: 'integer' },
          capacity: { type: 'integer' },
          codec: { type: 'string', description: '当前生效的存储编码。' },
          recordPath: {
            type: 'string',
            description: '本次录像写往的文件（未配置录像目录时为空串）。录像与内存缓冲互相独立，'
              + 'stop 之后文件依然完整可播，适合事后逐帧复盘。',
          },
          recordFrames: { type: 'integer', description: '已写入录像文件的帧数。' },
          backend: {
            type: 'string',
            description: '当前采集后端：dxgi（GPU 侧拷贝，读帧 0.42ms）或 gdi（回退路径，读帧约 29ms）。'
              + 'helper 一直有这个字段，插件先前把它丢了——"想确认现在跑哪条后端"不该逼人去翻配置。',
          },
          retainedSeconds: {
            type: 'number',
            description: '现在还能回看多久。jpeg 是缓冲覆盖的跨度；h264 只算已封片的片（最近一片还在写）。',
          },
          unsealedSeconds: {
            type: 'number',
            description: 'h264 下"最后一片封片之后积了多久"——这段默认不在环里，取帧时会按需结算；jpeg 恒为 0。',
          },
        },
        required: ['action', 'running', 'frames', 'spanSeconds', 'fps', 'bytes'],
      },
      render: (_args, value) => {
        // h264 下 spanSeconds 是"采集了多久"，能回看的只有 retainedSeconds——两个都报，
        // 免得调用方按采集时长去要一段注定取不到的时间。
        const coverage = value.codec === 'h264'
          ? `可回看 ${value.retainedSeconds.toFixed(1)}s（仅已封片）`
            + (value.unsealedSeconds > 0.3 ? `，另有 ${value.unsealedSeconds.toFixed(1)}s 未封片（取帧时会自动结算）` : '')
          : `覆盖 ${value.spanSeconds.toFixed(2)}s`
        return [{
          type: 'text',
          text: `持续采集：${value.running ? '运行中' : '已停止'}，缓冲 ${value.frames} 帧 / `
            + `${coverage} / 实测 ${value.fps.toFixed(1)} fps / ${(value.bytes / 1e6).toFixed(1)} MB`
            + `（${value.codec}, ${value.backend}）。要用录下来的帧，调 screen_frames。`
            + (value.recordPath === '' ? '' : `\n录像：${value.recordPath}（已写 ${value.recordFrames} 帧）`),
        }]
      },
    },
    async execute(args) {
      await session.start()
      const action = String(args.action ?? 'stats')
      if (action === 'start') {
        // 默认帧率由配置的 frameIntervalMs 推出（33 ms ⇒ 30 fps）：主机级的目标帧率属于
        // 部署选择，不该再在工具里写一个与之无关的常数。
        const fps = Number.isInteger(args.fps)
          ? Math.min(60, Math.max(1, args.fps))
          : Math.round(1000 / resolved.frameIntervalMs)
        // 录像路径：只在配置给了目录时录（见 DEFAULTS.recordDir 的说明）。文件名带时间戳，
        // 免得同一天里录第二段时把第一段覆盖掉——录像正是用来事后逐帧复盘的，不能只有最后一段。
        const recordDir = typeof resolved.recordDir === 'string' ? resolved.recordDir.trim() : ''
        const stamp = new Date().toISOString().slice(0, 19).replace(/[-:]/g, '').replace('T', '-')
        const recordPath = recordDir === '' ? '' : `${recordDir.replace(/[\\/]+$/, '')}\\cu-${stamp}.mp4`
        const started = await session.call({
          cmd: 'live_start',
          interval_ms: Math.round(1000 / fps),
          quality: Number.isInteger(args.quality) ? args.quality : resolved.jpegQuality,
          capacity: Number.isInteger(args.capacity) ? args.capacity : resolved.frameCapacity,
          backend: resolved.backend,
          codec: args.codec === 'h264' || args.codec === 'jpeg' ? args.codec : resolved.codec,
          // 录像路径由**配置**决定而不是模型传参：录不录像属于部署选择（要落盘、要占磁盘），
          // 不该让每次调用自己决定；模型也省得记住一个 Windows 路径。
          record_path: recordPath,
        })
        if (started.ok !== true) throw new Error(`dsh-computer-use: 开始采集失败：${String(started.error ?? '')}`)
      } else if (action === 'stop') {
        const stopped = await session.call({ cmd: 'live_stop' })
        if (stopped.ok !== true) throw new Error(`dsh-computer-use: 停止采集失败：${String(stopped.error ?? '')}`)
      }
      // 三种动作的结果一律取 `live_stats` 的快照：只有它同时给出 running / 帧数 / 跨度 /
      // 实测 fps（start 与 stop 的响应里没有这些字段，别拿它们当状态）。
      const raw = await session.call({ cmd: 'live_stats' })
      if (raw.ok !== true) throw new Error(`dsh-computer-use: 读采集状态失败：${String(raw.error ?? '')}`)
      return {
        action,
        running: raw.running === true,
        frames: Number(raw.frames ?? 0),
        spanSeconds: Number(raw.span_s ?? 0),
        fps: Number(raw.fps ?? 0),
        bytes: Number(raw.bytes ?? 0),
        intervalMs: Number(raw.interval_ms ?? 0),
        capacity: Number(raw.capacity ?? 0),
        codec: String(raw.codec ?? 'jpeg'),
        // 录像（配置 recordDir 才有）：recordPath 是本次写往的文件，recordFrames 是**已落进文件**的帧数
        // ——它与缓冲帧数不是一回事，录像独立于内存环，stop 后依然完整可播。
        recordPath: String(raw.record_path ?? ''),
        recordFrames: Number(raw.record_frames ?? 0),
        // helper 的 `live_stats` 一直回 `backend`，但先前插件没把它带进 value，于是渲染文本里没有、
        // 模型也就"看不到自己在用哪条后端"（2026-09-22 软糖实测 + 千瞳复核指出）。
        // 三层要分开核：helper 有 ≠ 插件透传 ≠ 模型可见。
        backend: String(raw.backend ?? ''),
        // jpeg 路径没有 retained_s：那种编码下缓冲跨度就是它能回看的范围。
        retainedSeconds: Number(raw.retained_s ?? raw.span_s ?? 0),
        // h264 的"未封片窗口"（最后一片封片之后积了多久）。它默认取不到，但取帧会按需结算——
        // 报出来是为了让调用方知道"再取一次就能拿到"，而不是以为这段时间没录上。
        unsealedSeconds: Number(raw.unsealed_s ?? 0),
      }
    },
  })

  // 从环形缓冲回看：把录下来的某一小段取出来交给模型。
  // 抽样的理由：3 秒的 10 fps 录制有 30 帧，全交给模型既贵又没必要——先按均匀间隔取
  // limit 张看整体过程，需要细节时再缩小时间窗重取。
  ctx.tools.register({
    name: 'screen_frames',
    description:
      '从 screen_watch 录下的缓冲里**按时间戳取帧**（回看刚才发生了什么）。'
      + '典型用法：猜不到哪一刻是关键，或变化比你观察得快——先录下来，再取那一段。'
      + '用 lastSeconds 取最近 N 秒最省事；帧数超过 limit 时按**均匀间隔抽样**，'
      + '所以先少取几张看过程，需要细节时再缩小时间窗重取。'
      + '**每次至少交 6 张图、最多 16 张**（默认 6）：时间轴必须配图——光有时刻表看不出那段里发生了什么。'
      + '每张都带采集时刻 `capturedAtSeconds`，"起点帧/目标帧"就从这些时刻里找。'
      + '注意交付给你的帧是**整屏缩图**（2560×1440 会被 DSH 缩到 1066×600），小目标会糊——'
      + '它们只够"看过程、找时刻"。要**看清某一帧里的细节**（几十像素的小控件、小字），'
      + '就用 `screen_grid({atSeconds})`：给它这一帧的采集时刻，一次拿到同帧缩略图 + 全部分块（1:1），'
      + '形态与看当前屏完全一致。'
      + '⚠️ 缓冲是**环形**的、满了就丢最旧的帧：要精读就得趁早，别等录完几分钟再回头找。'
      + '⚠️ **抽样会漏峰值**：瞬时事件（出现不到 1 秒）要先把时间窗缩到事件附近，'
      + '或让采样足够密——取帧间隔要在事件时长的 1/3 以内，否则你取到的是"事件与事件之间"的帧，'
      + '据此数的数量会系统性偏少。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        lastSeconds: { type: 'number', description: '取最近 N 秒的帧（与 from/to 二选一，默认 2）。' },
        from: { type: 'number', description: '起始时间戳（秒，helper 单调钟）。' },
        to: { type: 'number', description: '结束时间戳（秒）。' },
        limit: { type: 'integer', description: '返回几帧（默认 6，**下限 6、上限 16**）：任何取值都不会少于 6 张。' },
      },
    },
    output: {
      schema: {
        type: 'object',
        additionalProperties: false,
        properties: {
          available: {
            type: 'integer',
            description: 'jpeg：缓冲里落在该时间窗内的帧数；h264：该窗口实际解出的帧数'
              + '（切片按需解码，受抽样预算限制）。',
          },
          returned: { type: 'integer', description: '实际交给模型的帧数（抽样后）。' },
          fromSeconds: { type: 'number' },
          toSeconds: { type: 'number' },
          codec: { type: 'string', description: '当前生效的存储编码。' },
          frames: {
            type: 'array',
            items: {
              type: 'object',
              additionalProperties: false,
              required: ['atSeconds'],
              properties: {
                attachment: {
                  type: 'object',
                  required: ['attachmentId', 'mediaType', 'bytes', 'width', 'height'],
                  additionalProperties: true,
                  properties: {
                    attachmentId: { type: 'string' },
                    mediaType: { type: 'string' },
                    bytes: { type: 'integer' },
                    width: { type: 'integer' },
                    height: { type: 'integer' },
                    name: { type: 'string' },
                  },
                },
                atSeconds: { type: 'number', description: '这一帧的采集时刻。' },
                path: { type: 'string', description: '（保留字段：`pathsOnly` 形态已废除，不再交付路径。）' },
                localPath: { type: 'string', description: '（保留字段：`pathsOnly` 形态已废除，不再交付路径。）' },
                bytes: { type: 'integer', description: '（保留字段：`pathsOnly` 形态已废除，不再交付路径。）' },
              },
            },
          },
        },
        required: ['available', 'returned', 'fromSeconds', 'toSeconds', 'frames'],
      },
      render: (args, value) => {
        // h264 下"窗口内帧数"是抽样解码出来的、不是缓冲里的总帧数，文案必须说实话：
        // 说成"窗口内 N 帧"会让模型以为那些帧一直在那儿等着被取。
        const summary = value.codec === 'h264'
          ? `回看 ${value.fromSeconds.toFixed(2)}s → ${value.toSeconds.toFixed(2)}s：`
            + `按时间窗解出 ${value.returned} 张（切片按需解码；未封片那段已在本次请求里按需结算）。`
          : `回看 ${value.fromSeconds.toFixed(2)}s → ${value.toSeconds.toFixed(2)}s：`
            + `窗口内 ${value.available} 帧，按均匀间隔取了 ${value.returned} 张。`
        // 每帧的采集时刻必须**跟着图一起**给出来：卡时机用的"画面端点帧"靠的就是它
        // （主人定的口径：时机用画面端点帧标定）。只交图不给时刻，这套方法就没法用。
        const times = value.frames
          .map((frame, index) => `第 ${index + 1} 张 ${Number(frame.atSeconds).toFixed(3)}s`)
          .join('、')
        return [
          ...value.frames.map(frame => ({ type: 'image', attachment: frame.attachment })),
          {
            type: 'text',
            text: summary + `每帧采集时刻（与图的顺序一一对应）：${times}。`
              + '要看清其中某一帧的细节（几十像素的小控件、小字），用 `screen_grid({atSeconds})`'
              + '——把这一帧的时刻给它，一次给同帧缩略图 + 全部分块，与看当前屏同一形态。',
          },
        ]
      },
    },
    async execute(args) {
      await session.start()
      let from = Number.isFinite(args.from) ? Number(args.from) : undefined
      const to = Number.isFinite(args.to) ? Number(args.to) : 1e9
      if (from === undefined) {
        const latest = await session.call({ cmd: 'latest' })
        // 读不到最新帧有两种成因：还没录过，或采集被中断（helper 刚重启）。直接报「读最新帧失败」
        // 会把模型留在原地；这里走与空窗口一致的诊断。
        if (latest.ok !== true) throw new Error(`dsh-computer-use: ${await describeEmptyWindow(session)}`)
        const window = Number.isFinite(args.lastSeconds) ? Number(args.lastSeconds) : 2
        from = Math.max(0, Number(latest.t ?? 0) - window)
      }
      // 主人 2026-09-22 23:40：「screen_frames 至少得 6 张以上」「时间轴不配图有个屁用」
      // ⇒ 恒交付图像 + 每张的采集时刻，张数固定 6–16（`pathsOnly` 形态已废除）。
      // 窗口太短就先把它放长到能装下 limit 帧（30 fps 下 limit 帧 ≈ limit/30 秒），
      // 这样"任何窗口都不能少于 6 张"才成立。
      const limit = Number.isInteger(args.limit)
        ? Math.min(16, Math.max(6, args.limit))
        : 6
      const minWindow = Math.max(0.2, limit / 30)
      if (to - from < minWindow) from = Math.max(0, to - minWindow)
      // 一次多取一些再抽样：limit 是"交给模型的张数"，不是缓冲里的帧数。
      const raw = await session.call({ cmd: 'frames', from, to, limit: 512 })
      if (raw.ok !== true) throw new Error(`dsh-computer-use: 取帧失败：${String(raw.error ?? '')}`)
      const all = Array.isArray(raw.frames) ? raw.frames : []
      if (all.length === 0) {
        throw new Error(`dsh-computer-use: ${await describeEmptyWindow(session, from, to)}`)
      }
      if (all.length < 6) {
        throw new Error(`dsh-computer-use: 这个窗口里只有 ${all.length} 帧，而本工具**至少交 6 张**——`
          + '把窗口放长一点（30 fps 下 6 帧约 0.2 秒），或先 screen_watch start 让它多录一会儿。')
      }
      const picked = []
      if (all.length <= limit) {
        picked.push(...all)
      } else if (limit === 1) {
        // 只取一张时"均匀间隔"没有定义：step = (n-1)/(limit-1) 会除零 ⇒ 索引成 NaN ⇒
        // all[NaN] 是 undefined，交付分支再去读 .t / .path 就崩（两条分支各崩各的）。
        // 取窗口正中：一张要代表整段，取端点会偏向某一端。
        picked.push(all[Math.floor((all.length - 1) / 2)])
      } else {
        const step = (all.length - 1) / (limit - 1)
        for (let index = 0; index < limit; index += 1) {
          picked.push(all[Math.round(index * step)])
        }
      }
      // 裁剪（旧的 `region` 参数）已下线：要看清某一帧的细节走 `screen_grid({atSeconds})`——
      // 它一次给同帧缩略图 + 全部分块，形态与看当前屏一致，不会产生"这张图是原帧哪一块"的换算负担。
      const frames = []
      for (const frame of picked) {
        const source = frame
        const base = { atSeconds: Number(frame.t) }
          frames.push({
            ...base,
            attachment: await frameToAttachment(ctx, source, 'frame'),
          })
      }
      return {
        available: all.length,
        returned: frames.length,
        fromSeconds: Number(all[0].t),
        toSeconds: Number(all[all.length - 1].t),
        // helper 的 h264 分支会带 decoded 字段；逐帧 JPEG 路径没有。
        codec: raw.decoded !== undefined ? 'h264' : 'jpeg',
        frames,
      }
    },
  })

  // 事件驱动的等待：给一块区域，变化发生就返回那一帧。
  // ⚠️ **默认不注册**：主人 2026-09-22 明确，论文评测口径下"盯着画面等条件达成再动作"算作弊
  // （`act_when` 同理）。开关是 `triggerTools`，只有口径变了才打开——见 DEFAULTS 里的说明。
  if (resolved.triggerTools === true) ctx.tools.register({
    name: 'wait_for_change',
    description:
      '等屏幕上一块区域**发生变化**，变化一发生就返回那一帧（超时则返回"没变"）。'
      + '这是"事件什么时候发生我不知道"的标准解法：不要用 wait 盲等猜出来的时长，'
      + '给它一块你关心的区域（牌桌、按钮、状态栏），它替你盯着——你的思考时间不再落进窗口里。'
      + '⚠️ **区域要贴住目标**：threshold 是"区域内变化像素的比例"（默认 0.01 即 1%），'
      + '框开得比目标大，变化就被稀释——同一个事件在 760×500 的框里 diff=0.0104（贴着阈值，会静默漏检），'
      + '换 240×260 的框是 0.0481（稳定触发）；区域小轮询也更便宜。'
      + '超时（changed 为 false）有三种成因，补救动作不同：①事件还没来——继续等；'
      + '②区域选错、它本来就不会变——换区域；③**事件发生在你上一次动作与这次等待之间的间隙里**'
      + '（基准帧已经含事件，此后不会再变）——看 baselineAt 判断，然后重新观察当前画面，'
      + '不要继续等。'
      + '返回里带 diff（实际变化比例）、waitedMs（等了多久）与那一帧。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        x: { type: 'integer', description: '区域左边缘的屏幕 x 坐标（像素）。' },
        y: { type: 'integer', description: '区域上边缘的屏幕 y 坐标（像素）。' },
        width: { type: 'integer', description: '区域宽度（像素）。' },
        height: { type: 'integer', description: '区域高度（像素）。' },
        timeoutMs: {
          type: 'integer',
          description: '最长等多久（默认 5000，**上限 10000**）。⚠️ 这条命令在 helper 主循环里'
            + '同步轮询，等待期间**其他会话的 Computer Use 调用会排队**，所以上限压在 10 秒；'
            + '要等更久请分多次调用。',
        },
        threshold: { type: 'number', description: '变化比例阈值（默认 0.01）。' },
      },
      required: ['x', 'y', 'width', 'height'],
    },
    output: {
      schema: {
        type: 'object',
        additionalProperties: false,
        properties: {
          changed: { type: 'boolean', description: '等待期间该区域是否变了。' },
          diff: { type: 'number', description: '变化像素比例（0..1）。' },
          waitedMs: { type: 'integer', description: '实际等待了多少毫秒。' },
          atSeconds: { type: 'number', description: '变化那一刻（helper 单调秒）；没变时缺省。' },
          baselineAt: {
            type: 'string',
            description: '基准帧（本工具开始盯的那一帧）的墙钟时刻（ISO 8601）；仅超时时给出。'
              + '用它判断事件是否已经发生在基准帧之前。',
          },
          baselineAgeMs: {
            type: 'integer',
            description: '基准帧距本次返回已过去多少毫秒（≈ 本次等待时长）；仅超时时给出。',
          },
          attachment: {
            type: 'object',
            additionalProperties: true,
            properties: {
              attachmentId: { type: 'string' },
              mediaType: { type: 'string' },
              bytes: { type: 'integer' },
              width: { type: 'integer' },
              height: { type: 'integer' },
              name: { type: 'string' },
            },
          },
        },
        required: ['changed', 'diff', 'waitedMs'],
      },
      render: (_args, value) => (value.changed === true && value.attachment !== undefined
        ? [
          { type: 'image', attachment: value.attachment },
          { type: 'text', text: `变化发生在第 ${value.waitedMs} ms（diff=${value.diff}），这一帧是变化后的画面。` },
        ]
        : [{
          type: 'text',
          text: `${value.waitedMs} ms 内该区域没有变化（diff=${value.diff}）——`
            + '成因有三种：事件还没来 / 区域选得不对（它本来就不会变）/ 事件已经发生在这次等待开始'
            + `之前（基准帧拍于 ${value.baselineAt ?? '未知'}）。`
            + '若怀疑是第三种，重新观察当前画面，不要继续等；若怀疑是第二种，把区域收小到贴住目标。',
        }]),
    },
    async execute(args) {
      await session.start()
      const timeoutMs = Number.isInteger(args.timeoutMs) ? Math.min(10_000, Math.max(200, args.timeoutMs)) : 5000
      const threshold = Number.isFinite(args.threshold) ? Number(args.threshold) : 0.01
      const raw = await session.call({
        cmd: 'wait_change',
        x: Math.round(args.x),
        y: Math.round(args.y),
        w: Math.round(args.width),
        h: Math.round(args.height),
        timeout_ms: timeoutMs,
        threshold,
        interval_ms: 100,
      })
      const returnedAt = Date.now()
      if (raw.ok !== true) throw new Error(`dsh-computer-use: 等变化失败：${String(raw.error ?? '')}`)
      const baseline = baselineOfHelperFrame(raw, returnedAt)
      if (raw.changed !== true) {
        return {
          changed: false,
          diff: Number(raw.diff ?? 0),
          waitedMs: Number(raw.waited_ms ?? 0),
          ...baseline,
        }
      }
      const attachment = await frameToAttachment(ctx, raw, 'change')
      return {
        changed: true,
        diff: Number(raw.diff ?? 0),
        waitedMs: Number(raw.waited_ms ?? 0),
        atSeconds: Number(raw.t),
        attachment,
      }
    },
  })

  // 条件触发：把"看"和"动"放进同一次抓屏之后（毫秒级窗口的解法）。
  // ⚠️ **默认不注册**：与 `wait_for_change` 同一个口径判断——主人 2026-09-22 明确论文评测下算作弊。
  if (resolved.triggerTools === true) ctx.tools.register({
    name: 'act_when',
    description:
      '**盯住一块区域，条件达成的瞬间就在原地执行动作**——毫秒级时间窗口只有这一条路。'
      + '由你"看到画面再调工具"的链路（读帧 → 推理 → 调用 → 注入）实测定时精度只有 ±0.2~0.3 秒，'
      + '而目标窗口可能只有 125ms；所以凡是"目标一出现就要立刻点击/按键"的任务都用它。'
      + '判据：mode="change"（区域相对本次调用开始时发生变化，ratio 为变化像素比例，默认 0.02）'
      + '或 mode="match"（区域内出现 color 指定的颜色，tolerance 为每通道容差、ratio 为像素比例门槛）。'
      + '动作：action="click"（在 at 坐标点击，缺省点当前光标位置）| "key"（按 key，holdMs>0 则按住这么久再抬）'
      + '| "none"（只观测，不注入）。'
      + '💡 若是"点 Start 才开始计时"的任务，把那次点击放进 `primeAt`（同一次调用里先点、再盯）——'
      + '分两次调用会隔着一整轮推理（秒级），而事件可能在点下后 1 秒就发生。'
      + '⚠️ 动作**在你收到回执前就已经发出**，触发帧会一并交给你核对时机。'
      + '⚠️ 超时（fired=false）说明条件没在窗口内达成——先看 metric 差多远，再决定是改判据/区域而不是加大 timeoutMs'
      + '（它占用 helper 主循环，等待期间其他会话的 Computer Use 命令会排队）。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        x: { type: 'integer', description: '监视区域左边缘的屏幕 x（像素）。' },
        y: { type: 'integer', description: '监视区域上边缘的屏幕 y（像素）。' },
        width: { type: 'integer', description: '监视区域宽度（像素）。**贴住目标**——判据是区域内比例，框大了会被稀释。' },
        height: { type: 'integer', description: '监视区域高度（像素）。' },
        mode: { type: 'string', enum: ['change', 'match'], description: '判据：区域变化（默认）或指定颜色出现。' },
        color: { type: 'string', description: 'mode="match" 时必填，形如 #RRGGBB。' },
        tolerance: { type: 'integer', description: 'match 模式的每通道容差（默认 24）。' },
        ratio: { type: 'number', description: '触发门槛，占采样点的比例（默认 0.02）。' },
        action: { type: 'string', enum: ['click', 'key', 'none'], description: '条件达成后做什么（默认 click）。' },
        key: { type: 'string', description: 'action="key" 的键名（如 space、right、e）。' },
        holdMs: { type: 'integer', description: 'action="key" 时按住的毫秒数；0 = 瞬时按放（默认）。' },
        at: {
          type: 'object',
          additionalProperties: false,
          properties: { x: { type: 'integer' }, y: { type: 'integer' } },
          required: ['x', 'y'],
          description: 'action="click" 的落点；缺省点当前光标位置。',
        },
        delayMs: { type: 'integer', description: '触发后再等这么多毫秒才动作（默认 0）。' },
        primeAt: {
          type: 'object',
          additionalProperties: false,
          properties: { x: { type: 'integer' }, y: { type: 'integer' } },
          required: ['x', 'y'],
          description: '**预备动作**：开始盯之前先点这里一下（默认不做）。'
            + '时机类任务的通用形态是"点 Start 才开始计时"，而"点 Start"与"盯住目标出现"必须在同一次调用里'
            + '——分两次调用会隔着一整轮推理（秒级），事件可能在点下后 1 秒就发生。基准帧在点击之后才拍。',
        },
        button: { type: 'string', enum: ['left', 'right', 'middle'], description: 'action="click" 用哪个键（默认 left）。' },
        timeoutMs: { type: 'integer', description: '最长等多久（默认 5000，**上限 10000**）。' },
        intervalMs: { type: 'integer', description: '轮询间隔（默认 20ms；越小越快，也越费 CPU）。' },
        includeFrame: { type: 'boolean', description: '是否附触发帧（默认 true）——用它核对时机；只要结论不要图时置 false。' },
      },
      required: ['x', 'y', 'width', 'height'],
    },
    output: {
      schema: {
        type: 'object',
        additionalProperties: false,
        properties: {
          fired: { type: 'boolean', description: '条件是否在超时前达成（达成时动作已发出）。' },
          metric: { type: 'number', description: '触发时（或超时时）的判据读数。' },
          waitedMs: { type: 'integer', description: '从开始盯到检出条件用了多少毫秒。' },
          reactionMs: { type: 'integer', description: '检出到动作注入完成用了多少毫秒；未触发时缺省。' },
          baselineAt: { type: 'string', description: '基准帧的墙钟时刻（ISO 8601），用来判断条件是否在开始前就已成立。' },
          baselineAgeMs: { type: 'integer', description: '基准帧距本次返回已过去多少毫秒。' },
          actDetail: { type: 'string', description: '注入回执原样（含 injected / winerr / ime_open 等判读字段）；action="none" 时缺省。' },
          attachment: {
            type: 'object',
            additionalProperties: true,
            properties: {
              attachmentId: { type: 'string' },
              mediaType: { type: 'string' },
              bytes: { type: 'integer' },
              width: { type: 'integer' },
              height: { type: 'integer' },
              name: { type: 'string' },
            },
          },
        },
        required: ['fired', 'metric', 'waitedMs'],
      },
      render: (_args, value) => (value.fired === true
        ? [
          ...(value.attachment === undefined ? [] : [{ type: 'image', attachment: value.attachment }]),
          {
            type: 'text',
            text: `条件在第 ${value.waitedMs} ms 达成（判据读数 ${value.metric}），动作已发出`
              + `（检出到注入用了 ${value.reactionMs ?? '?'} ms）。这一帧就是触发时的画面。`
              + (value.actDetail === undefined ? '' : `　注入回执：${value.actDetail}`),
          },
        ]
        : [{
          type: 'text',
          text: `${value.waitedMs} ms 内条件未达成（判据读数 ${value.metric}）——`
            + '先看这个读数离门槛差多远：差得远说明判据或区域选错了（区域要贴住目标），'
            + '不是"再等久一点"能解决的；基准帧拍于 ' + (value.baselineAt ?? '未知') + '。',
        }]),
    },
    async execute(args) {
      await session.start()
      const mode = args.mode === 'match' ? 'match' : 'change'
      const action = args.action === 'key' || args.action === 'none' ? args.action : 'click'
      if (mode === 'match' && typeof args.color !== 'string') {
        throw new Error('dsh-computer-use: mode="match" 需要 color（#RRGGBB）；只想知道"变没变"就用 mode="change"')
      }
      if (action === 'key' && typeof args.key !== 'string') {
        throw new Error('dsh-computer-use: action="key" 需要 key（键名，如 space / right / e）')
      }
      const at = args.at ?? null
      const raw = await session.call({
        cmd: 'act_when',
        x: Math.round(args.x),
        y: Math.round(args.y),
        w: Math.round(args.width),
        h: Math.round(args.height),
        mode,
        color: args.color ?? '',
        tol: Number.isInteger(args.tolerance) ? args.tolerance : 24,
        ratio: Number.isFinite(args.ratio) ? Number(args.ratio) : 0.02,
        timeout_ms: Number.isInteger(args.timeoutMs) ? Math.min(10_000, Math.max(50, args.timeoutMs)) : 5000,
        interval_ms: Number.isInteger(args.intervalMs) ? Math.max(10, args.intervalMs) : 20,
        action,
        name: args.key ?? '',
        hold_ms: Number.isInteger(args.holdMs) ? args.holdMs : 0,
        ax: at === null ? 0 : Math.round(at.x),
        ay: at === null ? 0 : Math.round(at.y),
        has_point: at === null ? 0 : 1,
        button: args.button ?? 'left',
        delay_ms: Number.isInteger(args.delayMs) ? Math.max(0, args.delayMs) : 0,
        has_prime: args.primeAt === undefined || args.primeAt === null ? 0 : 1,
        prime_x: args.primeAt === undefined || args.primeAt === null ? 0 : Math.round(args.primeAt.x),
        prime_y: args.primeAt === undefined || args.primeAt === null ? 0 : Math.round(args.primeAt.y),
      }, 20_000)
      const returnedAt = Date.now()
      if (raw.ok !== true) throw new Error(`dsh-computer-use: 条件触发失败：${String(raw.error ?? '未知原因')}`)
      const base = {
        fired: raw.fired === true,
        metric: Number(raw.metric ?? 0),
        waitedMs: Number(raw.waited_ms ?? 0),
        ...(Number.isFinite(raw.reaction_ms) ? { reactionMs: Number(raw.reaction_ms) } : {}),
        ...baselineOfHelperFrame(raw, returnedAt),
      }
      if (base.fired !== true) return base
      const detail = raw.act === null || raw.act === undefined ? {} : { actDetail: JSON.stringify(raw.act) }
      if (args.includeFrame === false) return { ...base, ...detail }
      try {
        return { ...base, ...detail, attachment: await frameToAttachment(ctx, raw, 'when') }
      } catch (error) {
        // 触发帧取不到不该改变"动作已经发出"这个事实，但必须留痕，读的人才知道这次没有回执图。
        return { ...base, ...detail, actDetail: `${detail.actDetail ?? ''}（触发帧取景失败：${String(error.message)}）`.trim() }
      }
    },
  })

  // 窗口查询：回答"这个坐标上是谁"与"现在谁在前台"。
  // 它的价值在于区分两种看起来一样的现象：那里**确实没有东西**，还是**有东西但当前采集路径看不见**。
  ctx.tools.register({
    name: 'screen_windows',
    description:
      '查窗口：给 x/y 就返回**那个坐标上**的窗口（标题 + 类名）；不给就返回**当前前台窗口**。'
      + '用来区分"那块区域确实没东西"与"有东西但我看不见"（某些浮层/原生对话框在这种采集路径下'
      + '可能抓不到），也用来确认一次点击到底落在谁身上——配合动作回执使用。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        x: { type: 'integer', description: '要查的屏幕 x 坐标（像素）。给 x/y 就查该点上的窗口。' },
        y: { type: 'integer', description: '要查的屏幕 y 坐标（像素）。' },
        all: { type: 'boolean', description: '置 true 则**枚举**屏幕上所有可见且有标题的顶层窗口（标题、矩形、是否前台、z 序、是否最小化、是否模态）。' },
        includeMinimized: {
          type: 'boolean',
          description: '枚举时是否连最小化的窗口一起列出（默认 false）。最小化的窗口在屏幕上不可见，'
            + '默认滤掉是为了不让它们淹没真正看得见的窗口。',
        },
      },
    },
    output: {
      schema: {
        type: 'object',
        additionalProperties: false,
        properties: {
          mode: { type: 'string', description: 'point 表示查了坐标，foreground 表示前台窗口。' },
          x: { type: 'integer' },
          y: { type: 'integer' },
          title: { type: 'string', description: '窗口标题。' },
          className: { type: 'string', description: '窗口类名。' },
          atSeconds: { type: 'number' },
          hiddenMinimized: { type: 'integer', description: '被过滤掉的最小化窗口数（枚举模式）。' },
          windows: {
            type: 'array',
            description: 'all 为 true 时给出：每个可见且有标题的顶层窗口。',
            items: {
              type: 'object',
              additionalProperties: false,
              required: ['title', 'className', 'x', 'y', 'width', 'height', 'foreground', 'z', 'minimized', 'modal'],
              properties: {
                title: { type: 'string' },
                className: { type: 'string' },
                x: { type: 'integer' },
                y: { type: 'integer' },
                width: { type: 'integer' },
                height: { type: 'integer' },
                foreground: { type: 'boolean' },
                z: { type: 'integer', description: 'z 序：0 表示最上层。判断"谁盖在我要看的东西上面"用这个。' },
                minimized: { type: 'boolean', description: '是否已最小化（屏幕上不可见）。' },
                modal: { type: 'boolean', description: '是否模态对话框（Win32 对话框类 #32770，或属主窗口被禁用）。' },
              },
            },
          },
        },
        required: ['mode', 'title', 'className', 'atSeconds'],
      },
      render: (_args, value) => {
        if (value.mode === 'list') {
          const hidden = value.hiddenMinimized > 0
            ? `（另有 ${value.hiddenMinimized} 个最小化的窗口未列出，要看就传 includeMinimized: true）`
            : ''
          return [{
            type: 'text',
            text: `屏幕上有 ${value.windows.length} 个可见且有标题的窗口${hidden}：\n`
              + value.windows.map(item => `· ${item.modal ? '【模态】' : ''}${item.foreground ? '【前台】' : ''}${item.title}`
                + `（z=${item.z}，${item.x},${item.y} 起，${item.width}×${item.height}）`).join('\n'),
          }]
        }
        return [{
          type: 'text',
          text: value.mode === 'point'
            ? `(${value.x}, ${value.y}) 上是窗口「${value.title}」（类 ${value.className}）`
            : `前台窗口是「${value.title}」（类 ${value.className}）`,
        }]
      },
    },
    async execute(args) {
      await session.start()
      if (args.all === true) {
        // 枚举：回答"屏幕上还有哪些我没注意到的窗口"——尤其是浮在最上层挡住内容的那种。
        const raw = await session.call({ cmd: 'windows' })
        if (raw.ok !== true) throw new Error(`dsh-computer-use: 枚举窗口失败：${String(raw.error ?? '')}`)
        const mapped = (raw.windows ?? []).map(item => ({
          title: String(item.title ?? ''),
          className: String(item.class ?? ''),
          x: Number(item.x),
          y: Number(item.y),
          width: Number(item.w),
          height: Number(item.h),
          foreground: item.foreground === true,
          z: Number(item.z ?? 0),
          minimized: item.minimized === true,
          modal: item.modal === true,
        }))
        // 最小化窗口在屏幕上不可见、坐标恒为 (-32000,-32000)，混在列表里只会淹没有用的那些。
        const keepMinimized = args.includeMinimized === true
        const windows = keepMinimized ? mapped : mapped.filter(item => !item.minimized)
        // z 是 helper 全体窗口的访问序号（被跳过的也占号），所以按它排序才是真实的层叠顺序。
        windows.sort((left, right) => left.z - right.z)
        return {
          mode: 'list',
          title: '',
          className: '',
          atSeconds: Number(raw.t ?? 0),
          hiddenMinimized: mapped.length - windows.length,
          windows,
        }
      }
      const hasPoint = Number.isFinite(args.x) && Number.isFinite(args.y)
      const raw = hasPoint
        ? await session.call({ cmd: 'window_under', x: Math.round(args.x), y: Math.round(args.y) })
        : await session.call({ cmd: 'foreground' })
      if (raw.ok !== true) throw new Error(`dsh-computer-use: 查窗口失败：${String(raw.error ?? '')}`)
      const window = raw.window ?? {}
      return {
        mode: hasPoint ? 'point' : 'foreground',
        ...(hasPoint ? { x: Number(raw.x), y: Number(raw.y) } : {}),
        title: String(window.title ?? ''),
        className: String(window.class ?? ''),
        atSeconds: Number(raw.t ?? 0),
      }
    },
  })

  // 光标位置：相对移动需要知道"当前在哪"，模型据此算 dx/dy。
  ctx.tools.register({
    name: 'cursor_state',
    description:
      '读当前鼠标指针的屏幕坐标。相对移动（mouse_move_by）之前先看它，'
      + '这样你才知道自己在哪、该往哪个方向移多少。',
    parameters: { type: 'object', additionalProperties: false, properties: {} },
    output: {
      schema: {
        type: 'object',
        additionalProperties: false,
        properties: {
          x: { type: 'integer' },
          y: { type: 'integer' },
          atSeconds: { type: 'number' },
        },
        required: ['x', 'y', 'atSeconds'],
      },
      // atSeconds（helper 读数的绝对时刻）**只留在结构化值里、不进渲染**：2026-09-22 主人要求
      // "不要暴露执行时刻、间接暴露也不行"——它虽是观察时刻、不绑定任何动作，但对"按时间对齐"没用，
      // 留在渲染里只会多一个可能被混用的绝对时刻。
      render: (_args, value) => [{ type: 'text', text: `光标在 (${value.x}, ${value.y})` }],
    },
    async execute() {
      await session.start()
      const state = await session.call({ cmd: 'cursor' })
      if (state.ok !== true) throw new Error(`dsh-computer-use: 读光标失败：${String(state.error ?? '')}`)
      return { x: Number(state.x), y: Number(state.y), atSeconds: Number(state.t) }
    },
  })

  registerActionTool(ctx, session, {
    name: 'mouse_move_by',
    description:
      '把鼠标从**当前位置**移动一个相对位移。这是首选的点位方式：实测相对位移的误差'
      + '与「离光标多远」基本无关，而绝对坐标的误差会随距离明显变大。'
      + 'dx 向右为正、dy 向下为正，单位是屏幕像素。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        dx: { type: 'integer', description: '水平位移（像素），向右为正。' },
        dy: { type: 'integer', description: '垂直位移（像素），向下为正。' },
      },
      required: ['dx', 'dy'],
    },
    toPayload: args => ({ cmd: 'mouse_move_by', dx: Math.round(args.dx), dy: Math.round(args.dy) }),
  })

  registerActionTool(ctx, session, {
    name: 'mouse_move_to',
    description:
      '把鼠标移动到屏幕上的**绝对**坐标（左上角为原点）。适合跨屏大范围跳转做粗定位，'
      + '精调请改用 mouse_move_by。坐标是屏幕像素（不是截图上的像素）。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        x: { type: 'integer', description: '屏幕 x 坐标（像素）。' },
        y: { type: 'integer', description: '屏幕 y 坐标（像素）。' },
      },
      required: ['x', 'y'],
    },
    toPayload: args => ({ cmd: 'mouse_move', x: Math.round(args.x), y: Math.round(args.y) }),
  })

  registerActionTool(ctx, session, {
    name: 'click',
    description:
      '在鼠标**当前位置**点击。要点击某处，先用 mouse_move_by / mouse_move_to 把指针移过去，'
      + '再调用本工具。'
      + '**默认会附一张以落点为心的放大图**（原屏 400×400、放大 2 倍、最近邻）——'
      + '先看它确认是否点中目标，不必再补一次整屏观察；只在意"点了就行"时传 verify: false 省一张图。'
      + '回执里 `x_landed`/`y_landed` 是**注入前**读到的落点（点击不移动光标，用的就是它）；'
      + '`x_after`/`y_after` 只是"读回这一刻光标在哪"——光标是全局唯一资源，多会话并发时会被别人'
      + '挪走，**不能当落点验证**用。要验证落点，看附图、或点击前后裁同一小块对比。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        button: { type: 'string', enum: ['left', 'right', 'middle'], description: '鼠标键，默认左键。' },
        count: { type: 'integer', description: '连击次数，默认 1；双击传 2。' },
        verify: {
          type: 'boolean',
          description: '是否附一张**以落点为心**的放大图（默认 true）——用它确认是否点中目标，'
            + '省掉事后补一次观察（一轮观察要 3–6 秒）。连点、或只在意"点了就行"时置 false 省一张图。',
        },
      },
    },
    toPayload: args => ({
      cmd: 'click',
      button: args.button ?? 'left',
      action: 'click',
      count: Number.isInteger(args.count) ? args.count : 1,
    }),
    // 400×400 放大 2 倍 = 640000 px，正好卡满 DSH 的单图像素预算；再大就会被缩回去（等于白放大）。
    zoom: {
      half: 200,
      scale: 2,
      quality: resolved.jpegQuality,
      // 用**注入前**读到的落点（x_landed）；旧 helper 没有该字段时退回 x_after——那是"读回这一刻
      // 光标在哪"，并发下可能已被别的会话挪走，所以它只是回退、不是首选（2026-09-22 千瞳指出）。
      anchor: (_args, result) => (Number.isFinite(result.x_landed) && Number.isFinite(result.y_landed)
        ? { x: result.x_landed, y: result.y_landed }
        : (Number.isFinite(result.x_after) && Number.isFinite(result.y_after)
          ? { x: result.x_after, y: result.y_after }
          : null)),
    },
  })

  registerActionTool(ctx, session, {
    name: 'type_text',
    description:
      '在当前焦点处输入一段文本。走的是 Unicode 注入，因此中文、emoji、各种符号都能输入，'
      + '也不受键盘布局影响，并且**不会动你的剪贴板**。'
      + '**输入法开着（回执里 `ime_open=1`）时也照常生效**——`press_key` 的字母键会被输入法截获，'
      + '本工具不会，所以"网页/游戏里按键没反应"时优先用它。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        text: { type: 'string', description: '要输入的文本（原样输入，不需要转义）。' },
      },
      required: ['text'],
    },
    toPayload: args => ({ cmd: 'type_text', text: String(args.text ?? '') }),
  })

  registerActionTool(ctx, session, {
    name: 'press_key',
    description:
      '按一个键。键名用常见写法：a-z、0-9、enter、tab、esc、space、backspace、delete、'
      + 'up/down/left/right、home/end、pageup/pagedown、f1-f12 等。组合键用 hotkey。'
      + '回执含 `scan`（该键的物理扫描码）、`injected`（本次注入的**事件总数**，按下即抬起时为 2 = down+up）、'
      + '`events`（down/up 各几个）、`hold_ms`（保持时长）、`winerr`（失败时的 GetLastError），'
      + '据此判读：**`injected=0` 或 `winerr≠0` ⇒ 事件没注入成功**（多被系统或权限拦住）；'
      + '**`injected>0` 但目标毫无反应 ⇒ 按键送到了、目标忽略了它**——已知两类原因：缺扫描码的合成按键会被浏览器'
      + '丢弃；**零间隔的 down+up 会被按 requestAnimationFrame 轮询按键状态的页面整帧漏掉**（2026-09-22 评测页'
      + '实测：`press_key` 零响应，改成保持按下立即生效 ⇒ 调大 `holdMs` 即可）。'
      + '还要看 `ime_open`：**`1` = 前台窗口的输入法开着（中文态）**——此时**字母键与方向键会被输入法'
      + '截获去做拼音组合，目标页面只收到 keyup、收不到 keydown**（2026-09-22 用记录 keydown 的探针页实测），'
      + '表现与"完全没注入"一模一样；绕法是改用 `type_text`（Unicode 路径不受影响）或先把输入法切到英文。'
      + '`0` = 直通/英文，`-1` = 查不到。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        key: { type: 'string', description: '键名，例如 enter、esc、left、f5。' },
        holdMs: {
          type: 'integer',
          description: '按下与抬起之间的保持毫秒数（默认 50）。人类点击的按下时长本就在 50~100ms 量级，'
            + '所以默认值不改变"点一下"的语义；传 0 = 严格零间隔（对按 rAF 轮询按键状态的页面会被整帧漏掉，'
            + '只在确实需要瞬时按下时用）。',
        },
      },
      required: ['key'],
    },
    toPayload: args => {
      const payload = { cmd: 'key', name: String(args.key ?? ''), action: 'press' }
      if (args.holdMs !== undefined) payload.holdMs = args.holdMs
      return payload
    },
  })

  registerActionTool(ctx, session, {
    name: 'hotkey',
    description: '按一组组合键（例如 ctrl+c、alt+tab、win+d）。修饰键会在按键期间保持按住。'
      + '回执含 `injected`（本组合共注入的事件数）与 `winerr`：正常情况下 `injected` = 键数 × 2，'
      + '偏少说明有事件没送出去。**组合键不受输入法影响**——所以"`hotkey` 有效而 `press_key` 无效"'
      + '本身就是输入法截获字母键的症状（回执里 `ime_open=1` 即证实，改走 `type_text`）。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        keys: {
          type: 'array',
          items: { type: 'string' },
          description: '按顺序按下的键名，例如 ["ctrl", "c"]。',
        },
      },
      required: ['keys'],
    },
    toPayload: args => ({
      cmd: 'hotkey',
      keys: Array.isArray(args.keys) ? args.keys.map(String) : [],
    }),
  })

  registerActionTool(ctx, session, {
    name: 'scroll',
    description: '滚动滚轮。vertical 为正向上、负向下；horizontal 为正向右、负向左。单位是"格"。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        vertical: { type: 'integer', description: '垂直滚动格数，正数向上。' },
        horizontal: { type: 'integer', description: '水平滚动格数，正数向右。' },
      },
    },
    toPayload: args => ({
      cmd: 'scroll',
      vertical: Number.isInteger(args.vertical) ? args.vertical : 0,
      horizontal: Number.isInteger(args.horizontal) ? args.horizontal : 0,
    }),
  })

  // 鼠标按住/松开：拖拽与框选的基础动作。与 click 分开，是因为"按住不放"本身
  // 就是独立的一步——拖拽必须先 down、移动、再 up。
  registerActionTool(ctx, session, {
    name: 'mouse_button',
    description:
      '按下或松开鼠标键（按住不放，不会自动弹起）。拖拽、框选这类操作要成对使用：'
      + '先 down，然后移动（mouse_move_by），最后 up。点一下就完事的场景请用 click。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        button: { type: 'string', enum: ['left', 'right', 'middle'], description: '鼠标键，默认左键。' },
        action: { type: 'string', enum: ['down', 'up'], description: 'down 按住，up 松开。' },
      },
      required: ['action'],
    },
    toPayload: args => ({
      cmd: 'click',
      button: args.button ?? 'left',
      action: String(args.action ?? 'down'),
    }),
  })

  // 键盘按住/松开：与 press_key（按下即抬起）互补。
  registerActionTool(ctx, session, {
    name: 'key_state',
    description:
      '按住或松开一个键。需要让某个键保持按下时用它（例如按住 shift 连续选择、'
      + '按住方向键持续移动），记得最后一定要 up，否则键盘会停在被按下的状态。'
      + '按一下就走请用 press_key。'
      + '回执含 `scan` / `injected` / `winerr`：`injected=0` 或 `winerr≠0` ⇒ 没注入成功；'
      + '`injected>0` 而目标毫无反应 ⇒ 送到了但被目标忽略了（若同时 `ime_open=1`，字母键与方向键会被'
      + '输入法截获，目标只收得到 keyup）。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        key: { type: 'string', description: '键名，例如 shift、ctrl、right。' },
        action: { type: 'string', enum: ['down', 'up'], description: 'down 按住，up 松开。' },
      },
      required: ['key', 'action'],
    },
    toPayload: args => ({
      cmd: 'key',
      name: String(args.key ?? ''),
      action: String(args.action ?? 'down'),
    }),
  })

  registerActionTool(ctx, session, {
    name: 'drag',
    description:
      '从鼠标**当前位置**拖拽：按下 → 沿着路径分段移动 → 松开。'
      + '分段是刻意的：人的拖动是连续轨迹，一次跳到位会被很多应用丢掉拖拽语义'
      + '（拖滚动条、画图、拖放到目标上尤其明显）。'
      + '拖文件、拖滚动条、拖选文本都用它；单纯移动请用 mouse_move_by。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        dx: { type: 'integer', description: '水平位移（像素），向右为正。' },
        dy: { type: 'integer', description: '垂直位移（像素），向下为正。' },
        button: { type: 'string', enum: ['left', 'right', 'middle'], description: '用哪个键拖，默认左键。' },
        steps: { type: 'integer', description: '分成几步走，默认 12；越大越接近人的拖动、也越慢。' },
      },
      required: ['dx', 'dy'],
    },
    toPayload: args => ({
      cmd: 'drag',
      dx: Math.round(args.dx),
      dy: Math.round(args.dy),
      button: args.button ?? 'left',
      steps: Number.isInteger(args.steps) ? args.steps : 12,
    }),
  })

  // 等待：**在插件侧等**，不走 helper。
  // 为什么必须挪出来：helper 是单线程串行（一次只处理一条命令），而它的 `wait` 命令是在
  // 主循环里同步 `Thread.Sleep` —— 一条 wait 会把**所有会话**的请求一起堵在队列里，宿主侧
  // 只表现为「helper 超时」，看起来像进程死了（2026-09-22 实测：一条 wait(50000) 让两个人
  // 的调用全部超时，三条独立线索追了半小时才发现是排队）。等待是纯时间语义，放插件侧等价，
  // 而且不再占用任何共享资源——顺带也不需要启动 helper。
  ctx.tools.register({
    name: 'wait',
    description:
      '等待一段时间（毫秒），用于让界面把加载、动画、页面切换做完，或**按已知时序卡准动作时刻**。'
      + '比"反复截图看好了没"省得多——一次等待只花时间，反复观察要花掉整轮推理。'
      + '⚠️ **上限 5 秒**，且等待期间 helper 不处理别的请求（其他会话的 Computer Use 调用会排队）'
      + '——要等更久请分多次调用。'
      + '⚠️ 计时由 **helper 用 Windows QPC** 完成（物理时间）：宿主侧（WSL）自己的钟比物理时间**慢约 12%**'
      + '（2026-09-22 实测：请求 1500ms 在物理上走了 1683ms），所以**按已知时序卡点必须以 `elapsed_ms` 为准**，'
      + '不要拿宿主时钟去推。`host_clock_ms` / `host_clock_drift_ms` 是这次等待在宿主钟上的读数与偏差'
      + '（都是**耗时/偏差**，不是时刻）。',
    parameters: {
      type: 'object',
      additionalProperties: false,
      properties: {
        ms: { type: 'integer', description: '等待毫秒数（0–5000；要更久请分多次调用）。' },
      },
      required: ['ms'],
    },
    output: {
      schema: {
        type: 'object',
        additionalProperties: false,
        properties: {
          ok: { type: 'boolean' },
          detail: { type: 'string', description: '实际等待的毫秒数。' },
        },
        required: ['ok', 'detail'],
      },
      render: (_args, value) => [{ type: 'text', text: value.detail }],
    },
    async execute(args) {
      const ms = Math.min(5_000, Math.max(0, Math.round(Number(args?.ms) || 0)))
      // 走 helper 的 `wait`：它用 Windows QPC 计时，量的是**物理时间**。宿主侧（WSL）自己的钟比物理时间
      // 慢约 12%（实测请求 1500ms 在 QPC 上走了 1683ms）——"按已知时序卡动作时刻"必须落在物理时间上，
      // 否则 125ms 级窗口会被系统性错过（这正是测试组窄窗口题打不中的根因之一）。
      // 代价：等待期间 helper 主循环被占、其他会话的调用排队 ⇒ 所以把上限压到 5 秒。
      const wall0 = Date.now()
      try {
        await session.start()
        const raw = await session.call({ cmd: 'wait', ms }, ms + 10_000)
        if (raw.ok !== true) throw new Error(String(raw.error ?? '未知原因'))
        const physical = Math.round(Number(raw.actual_ms ?? ms))
        const hostMs = Date.now() - wall0
        // **不透传 helper 的 QPC 时刻**（2026-09-22 主人定）：绝对执行时刻一律不给模型——它和"画面变化的
        // 时刻"是两个不同基准，混用会静默算出偏 1–5 帧的时间。这里只给耗时与宿主钟偏差（都是 delta）。
        return {
          ok: true,
          detail: `wait 已执行：{"requested_ms":${ms},"elapsed_ms":${physical}`
            + `,"host_clock_ms":${hostMs},"host_clock_drift_ms":${hostMs - physical}}`,
        }
      } catch (error) {
        // helper 起不来时退回宿主侧等待：宿主钟比物理时间慢约 12%，所以如实标注，别让它看起来一样准。
        const t = await sleepFor(ms)
        return {
          ok: true,
          detail: `wait 已执行（helper 不可用，退回宿主时钟：${String(error.message)}）：`
            + `{"requested_ms":${t.requestedMs},"elapsed_ms":${t.elapsedMs},"host_clock_only":true}`,
        }
      }
    },
  })

  // 卸载即效果：停掉 helper（它会先停采集线程再退出），不留孤儿进程。
  ctx.effect(() => () => session.stop(), 'dsh-computer-use: helper 进程')
}
