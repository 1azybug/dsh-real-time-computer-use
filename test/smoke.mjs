// 冒烟：验证「插件能加载 + helper 能起来 + 工具能跑通」。
//
// 动作类工具只发**零副作用**参数（零位移、空串、孤立的抬起、不存在的键名）：它们足以
// 证明「命令接线 + 参数解析 + 键名表」是对的，又不会真的动鼠标键盘。真实动作的验证
// 属于人工/端到端环节，不放在冒烟里。
import { apply } from '../lib/index.js'
import { existsSync, mkdtempSync, rmSync } from 'node:fs'
import { join } from 'node:path'
import { tmpdir } from 'node:os'

// 冒烟会真的取帧、也就真的写「固定副本」。把副本目录指向临时目录：测试产物属于测试，
// 不要混进真实副本目录（2026-09-22 测试组从 `~/.dsh/computer-use/frames/` 里翻出过冒烟写的图）。
const pinDir = mkdtempSync(join(tmpdir(), 'dsh-cu-smoke-'))
process.env.DSH_CU_FRAMES_DIR = pinDir

const registered = []
const disposers = []
const attachments = {
  async saveImages(images) {
    return images.map((image, index) => ({
      attachmentId: `mock-${index}`,
      mediaType: image.mediaType,
      bytes: image.data.byteLength,
      width: 2560,
      height: 1440,
      name: image.name,
    }))
  },
}
const guards = []
const ctx = {
  tools: { register: tool => registered.push(tool), guard: fn => guards.push(fn) },
  get: name => (name === 'attachments' ? attachments : undefined),
  // 真 effect 会立刻执行回调并登记它返回的清理函数；mock 也必须照做，否则最后
  // 收不到 session.stop，helper 进程会留在后台。
  effect: fn => {
    const disposer = fn()
    if (typeof disposer === 'function') disposers.push(disposer)
  },
  logger: () => ({ info() {}, warn() {} }),
  on: () => () => {},
}

let failures = 0
const check = (label, fn) => {
  try {
    const value = fn()
    if (value && typeof value.then === 'function') throw new Error('check 不接受异步断言')
    console.log(`  ✓ ${label}`)
  } catch (error) {
    failures += 1
    console.log(`  ✗ ${label} —— ${error.message}`)
  }
}
const checkAsync = async (label, fn) => {
  try {
    await fn()
    console.log(`  ✓ ${label}`)
  } catch (error) {
    failures += 1
    console.log(`  ✗ ${label} —— ${error.message}`)
  }
}

apply(ctx, {})

check('注册了工具', () => {
  if (registered.length === 0) throw new Error('一个都没注册')
})
const names = registered.map(t => t.name)
const byName = name => registered.find(t => t.name === name)
console.log('  工具:', names.join(', '))
check('每个工具都有 type:object 的 parameters', () => {
  for (const tool of registered) {
    if (tool.parameters?.type !== 'object') throw new Error(`${tool.name} 的 parameters 不是 object`)
  }
})
check('动作类工具都没有声明 isConcurrencySafe（要顺序执行）', () => {
  for (const tool of registered) {
    if (typeof tool.isConcurrencySafe === 'function') throw new Error(`${tool.name} 声明了它，会破坏批量动作的顺序`)
  }
})

// 两个「只返回一张图」的观察工具默认**不注册**（主人 2026-09-22 23:23 令）。它们的能力仍在，
// 所以用一个显式打开开关的实例继续做真实调用——覆盖不能因为默认关闭而丢掉。
check('默认不注册 screen_observe / region_observe（只返回一张图的工具）', () => {
  for (const name of ['screen_observe', 'region_observe']) {
    if (names.includes(name)) throw new Error(`${name} 默认被注册了`)
  }
})
const singleImageRegistered = []
const singleCtx = { ...ctx, tools: { register: tool => singleImageRegistered.push(tool) } }
apply(singleCtx, { singleImageTools: true })
const bySingleName = name => singleImageRegistered.find(t => t.name === name)
check('打开 singleImageTools 后这两个工具注册回来（双向断言）', () => {
  for (const name of ['screen_observe', 'region_observe']) {
    const tool = bySingleName(name)
    if (tool === undefined) throw new Error(`${name} 没注册`)
    if (tool.parameters?.type !== 'object') throw new Error(`${name} 的 parameters 不是 object`)
  }
})

// `read_image` 整体停用（主人 2026-09-22 23:40：「read_image 必须得禁」）⇒ 默认就拦，带不带 region 都拦。
check('默认就拦 read_image（denyReadImage 默认 true）', () => {
  if (guards.length !== 1) throw new Error(`默认注册了 ${guards.length} 条 guard`)
})
check('拦的是全部 read_image（整图与带 region 都拦），不误伤其他工具', () => {
  const guard = guards[0]
  const withRegion = guard({ name: 'read_image', arguments: { file_path: 'a.png', region: { x: 0, y: 0, width: 8, height: 8 } } })
  if (typeof withRegion !== 'string' || !withRegion.includes('screen_grid')) {
    throw new Error(`带 region 的没拦下或没给替代路径：${String(withRegion)}`)
  }
  const whole = guard({ name: 'read_image', arguments: { file_path: 'a.png' } })
  if (typeof whole !== 'string') throw new Error('整图读没拦下')
  if (guard({ name: 'screen_grid', arguments: { atSeconds: 1 } }) !== undefined) {
    throw new Error('误拦了别的工具')
  }
})
// 双向：显式关掉这个开关时不应再拦（部署方要保留读图能力时用）。
const openGuards = []
apply({ ...ctx, tools: { register: () => {}, guard: fn => openGuards.push(fn) } }, { denyReadImage: false })
check('denyReadImage:false 时不注册拦截', () => {
  if (openGuards.length !== 0) throw new Error(`注册了 ${openGuards.length} 条`)
})

console.log('\n真实调用（只读）：')
const observe = bySingleName('screen_observe')
const cursor = byName('cursor_state')
const frame = await observe.execute({})
console.log(`  screen_observe → ${frame.width}×${frame.height}, t=${frame.capturedAtSeconds.toFixed(3)}s,`
  + ` 附件 ${frame.attachment.bytes} 字节`)
if (!(frame.attachment.bytes > 10_000)) { failures += 1; console.log('  ✗ 附件字节数异常小') }

const pos = await cursor.execute({})
console.log(`  cursor_state → (${pos.x}, ${pos.y}) @ t=${pos.atSeconds.toFixed(3)}s`)

const rendered = observe.output.render({}, frame)
check('render 返回了图像块', () => {
  if (rendered[0]?.type !== 'image') throw new Error('第一个块不是 image')
})
console.log('渲染文本:', rendered[1].text)

// 区域裁剪是"精点"路径的唯一手段：必须真的裁到，并且说明这块取自原屏哪里
// （否则模型无法把块内坐标还原成屏幕坐标）。
const region = bySingleName('region_observe')
const crop = await region.execute({ x: 0, y: 0, width: 1280, height: 480 })
console.log(`  region_observe → ${crop.width}×${crop.height}，取自 (${crop.croppedFrom.x}, ${crop.croppedFrom.y})`
  + ` / 原屏 ${crop.croppedFrom.sourceWidth}×${crop.croppedFrom.sourceHeight}`)
check('region 裁块尺寸与请求一致', () => {
  if (crop.width !== 1280 || crop.height !== 480) throw new Error(`实际 ${crop.width}×${crop.height}`)
})
check('region 返回了块偏移与原屏尺寸', () => {
  const at = crop.croppedFrom
  if (at.x !== 0 || at.y !== 0) throw new Error(`偏移 ${at.x},${at.y}`)
  if (at.sourceWidth !== frame.width || at.sourceHeight !== frame.height) {
    throw new Error(`原屏 ${at.sourceWidth}×${at.sourceHeight} 与整屏 ${frame.width}×${frame.height} 不一致`)
  }
})
// 越出屏幕时取交集，不是报错：右下角只剩 20×20。
const edge = await region.execute({ x: frame.width - 20, y: frame.height - 20, width: 100, height: 100 })
check('region 越界取交集', () => {
  if (edge.width !== 20 || edge.height !== 20) throw new Error(`实际 ${edge.width}×${edge.height}`)
  if (edge.croppedFrom.x !== frame.width - 20) throw new Error(`偏移 x = ${edge.croppedFrom.x}`)
})
const regionRendered = region.output.render({}, crop)
check('region 的 render 说明块偏移', () => {
  if (regionRendered[0]?.type !== 'image') throw new Error('第一个块不是 image')
  if (!regionRendered[1].text.includes('屏幕坐标 = 这块的偏移')) throw new Error('缺少坐标还原说明')
})

// 放大：把更小的区域铺满同一份像素预算。266×150 的 4× = 1064×600 = 638400 px，卡在预算内。
const zoomed = await region.execute({ x: 200, y: 200, width: 266, height: 150, scale: 4 })
check('region 支持整数倍放大', () => {
  if (zoomed.scale !== 4) throw new Error(`scale = ${zoomed.scale}`)
  if (zoomed.outputWidth !== 1064 || zoomed.outputHeight !== 600) {
    throw new Error(`输出 ${zoomed.outputWidth}×${zoomed.outputHeight}`)
  }
})

// 同帧切块：一次抓帧切 6 块。滚动/动画里"各块是不是同一时刻"决定画面是否真实存在。
const grid = byName('screen_grid')
const whole = await grid.execute({ cols: 2, rows: 3 })
console.log(`  screen_grid → ${whole.tiles.length} 块 ${whole.tiles[0].width}×${whole.tiles[0].height}`
  + ` @ t=${whole.capturedAtSeconds.toFixed(3)}s`)
check('screen_grid 返回 2×3 块且每块尺寸一致', () => {
  if (whole.tiles.length !== 6) throw new Error(`实际 ${whole.tiles.length} 块`)
  for (const tile of whole.tiles) {
    if (tile.width !== 1280 || tile.height !== 480) {
      throw new Error(`块 (${tile.row},${tile.col}) 是 ${tile.width}×${tile.height}`)
    }
  }
})
check('screen_grid 每块都带屏幕坐标', () => {
  const corners = whole.tiles.map(tile => `${tile.x},${tile.y}`).sort().join('|')
  const want = ['0,0', '0,480', '0,960', '1280,0', '1280,480', '1280,960'].sort().join('|')
  if (corners !== want) throw new Error(corners)
})
check('screen_grid 的 render 给出缩略图与每块坐标', () => {
  const blocks = grid.output.render({}, whole)
  const images = blocks.filter(block => block.type === 'image').length
  if (images !== 7) throw new Error(`图像块数不对：${images}（期望缩略图 1 + 块 6）`)
  if (!blocks[blocks.length - 1].text.includes('(1280,480)')) throw new Error('说明里缺少块坐标')
})
// 缩略图与块**同帧**：缩略图定方位、块给精度；不同帧就会在动画里指错。它还必须落在
// 像素预算内，否则会被模型侧再缩一次（多一次重采样，且"原样进模型"不再成立）。
check('screen_grid 附同帧缩略图且不超像素预算', () => {
  if (whole.thumbnail === undefined) throw new Error('没有缩略图')
  if (whole.thumbnail.attachment === undefined) throw new Error('缩略图缺 attachment')
  const pixels = whole.thumbnail.width * whole.thumbnail.height
  if (pixels > 640000) throw new Error(`缩略图 ${whole.thumbnail.width}×${whole.thumbnail.height} = ${pixels} px 超预算`)
  if (pixels < 300000) throw new Error(`缩略图只有 ${pixels} px，方位判断会不够用`)
})
// 主人 2026-09-22 23:30：「screen_grid 我就要返回 7 张图」⇒ 只有一种交付形态：
// 同帧缩略图 + 全部分块（本机 2×3 ⇒ 7 张）。`blocks` / `includeThumbnail` 两个"少看图"的口子已删，
// 所以这里按测试组的验收判据断言：**任何参数都不得让交付少于 7 张**。
const countImages = value => grid.output.render({}, value).filter(block => block.type === 'image').length
check('默认调用交付 7 张（6 块 + 1 缩略图）', () => {
  if (countImages(whole) !== 7) throw new Error(`${countImages(whole)} 张`)
})

const noThumb = await grid.execute({ cols: 2, rows: 3, includeThumbnail: false })
check('传 includeThumbnail:false（参数已删）仍给 7 张，不会少给', () => {
  if (countImages(noThumb) !== 7) throw new Error(`${countImages(noThumb)} 张`)
})

const oneBlock = await grid.execute({ cols: 2, rows: 3, blocks: [3] })
check('传 blocks（参数已删）仍给 7 张，不会少给', () => {
  if (countImages(oneBlock) !== 7) throw new Error(`${countImages(oneBlock)} 张`)
})

let smallGridError = ''
try { await grid.execute({ cols: 1, rows: 1 }) } catch (error) { smallGridError = String(error?.message ?? '') }
check('cols×rows < 6 被拒（块数下限钉住）', () => {
  if (!smallGridError.includes('不得少于 6')) throw new Error(smallGridError || '没报错')
})

// 收尾：停掉 helper，别把进程留在后台；临时副本目录也一起清掉。
for (const dispose of disposers) dispose()
await new Promise(resolve => setTimeout(resolve, 300))
rmSync(pinDir, { recursive: true, force: true })

console.log(failures === 0 ? '\n冒烟通过 ✓' : `\n冒烟失败 ${failures} 项 ✗`)
process.exit(failures === 0 ? 0 : 1)
