# 壁纸性能优化方案

> 版本：v2 · 2026-09-15
> 状态：Stage 0 / 1 / 4 已落地并复核通过；Stage 2 维持暂缓；Stage 3 按其原始意图重新定义；新增 N1–N8 复核结论
> 范围：`wallpaper/src/liquid/` 与其渲染器适配层，必要时包含 `wallpaper/vendor/liquid-glass-webgl`
> 约束：不降低默认视觉效果、不改变歌词同步与配置属性语义、不迁移 WebGL 版本。

---

## 1. 目标与原则

### 目标

在不改变当前默认视觉效果的前提下，降低 Wallpaper Engine 运行时的 GPU 占用、CPU 占用、功耗和背景切换卡顿，并保持所有现有功能可用：

- 歌词逐行滚动、提前收敛和同步偏移；
- 深度缩放、透明度、玻璃折射、高光、阴影及“歌词在玻璃后”模式；
- 背景布局、随机背景轮播、缩放和偏移；
- 无播放内容时的空闲时钟；
- 所有既有 Wallpaper Engine 属性与参数面板配置。

### 实施原则

1. **先测量、后优化**：每阶段先增加可观测数据，再根据实测决定是否继续。
2. **默认画面一致**：默认参数、默认渲染路径和截图像素差异必须在设定阈值内。
3. **小步可回退**：每项优化独立提交，保留开关或旧路径至少一个发布周期。
4. **优先减少工作量**：先消除无效帧和全屏重绘，再考虑 API 或渲染技术升级。
5. **区分“纯减法”与“改画面”**：只删除无收益工作的改动可以合并；任何改变运动过程中画面的改动必须单独提交，并附实机比对。

---

## 2. 当前渲染链路与瓶颈

### 2.1 现状链路

```text
requestAnimationFrame（持续运行，main.ts tick）
  → ReferenceLyricsWallpaper.draw()
  → commitScrollY()  ← ScrollRenderGate：位移 < 0.05 CSS px 时不提交
  → renderer.setScrollY()  ——仅当 gate 放行
  → renderer.render()      ——needsRedraw=false 时立即返回
  → 全屏背景绘制至场景 FBO (fboA)
  → [isolateBackdrop] 全屏快照 fboA → bgOnlyFbo
  → 逐行玻璃卡片 / 文字 / 阴影 / 高光（逐元素 FBO 路径）
  → 全屏复制 fboA → 默认帧缓冲
```

### 2.2 v1 瓶颈清单复核（2026-09-15）

| v1 编号 | 结论 | 证据 |
|---|---|---|
| P0 静止时仍持续请求重绘 | **已解决** | `liquid/render-scheduler.ts` 的 `ScrollRenderGate`（阈值 0.05 CSS px），在 `reference-wallpaper.ts:447-450` 生效。停稳后不再触发 `rebuild`，`methods-render.ts:24-25` 的 `needsRedraw` 检查使 GPU render 归零。 |
| P1 静态背景参与每次动态合成 | **暂缓，判断正确** | 玻璃 shader 仍依赖不透明场景 FBO；`methods-render-glass-backdrop.ts:390-392` 的独立采样分支决定了透明顶层输出会使折射失效。 |
| P2 滚动期间高频重建配置 | **仍存在，且比 v1 描述更严重** | v1 的猜测（“字号、透明度、玻璃强度连续变化时缓存仍可能失效”）成立；此外还有 N2、N3 两条 v1 未指出的机制。 |
| P3 背景合成包含 PNG 中转 | **已解决** | `liquid/background-composer.ts:25-75` 直接产出 Canvas，`:82-84` 直接作为 `texImage2D` 来源，PNG Blob 编解码链路已移除。 |
| Stage 5 完整复制上游 renderer | **取消（维持）** | 子模块 + 本地适配层的结构成立，无需变更。 |

**v1 未覆盖的瓶颈：N1–N8。**

### 2.3 N1 · `isolateBackdrop` 全程开启，但其纹理从不被采样

`reference-wallpaper.ts:107` 无条件设置 `quickToggles.isolateBackdrop = true`。于是**每个真正出图的帧**都付出：

- `methods-render.ts:143-151`：一次全屏 `drawCopy(fboA → bgOnlyFbo)`；
- `methods-render.ts:245-249`（主循环）与 `:347-349`（renderOnTop 循环）：**每个非玻璃元素再绘制第二遍**到 `bgOnlyFbo`，每次各含一次纹理绑定、约 15 个 uniform 与一次 `drawArrays`。默认配置下即 5 行歌词文字被重复绘制。

而在默认与“高质量全场模糊”两种配置下，`bgOnlyTex` 都不会被采样：所有歌词玻璃行都满足 `independent`（`reference-wallpaper.ts:703-708` 设置 `independentBackdrop` / `directBackdropSample`，经 `methods-render-glass-transform.ts:288-291` 判定），`resolveBackdropTex` 在 `methods-render-glass-backdrop.ts:390-392` 提前返回，导致 `:402` 与 `:582` 两个 `bgOnlyTex` 分支不可达。

**代价**：每帧 +1 次全屏 blit、+5 次重复绘制。
**注意**：在壁纸纹理加载失败的降级路径下，玻璃元素会变为非独立，此时 `bgOnlyTex` 确实会被采样。因此正确的修法是让快照与重复绘制**以“存在非独立玻璃元素”为条件**，而不是直接关掉开关。

### 2.4 N2 · 滚动期间每个玻璃行必然 cache miss，且后置 pass 从不参与缓存

两套机制叠加：

1. `methods-render-glass-pef-cache-resolve.ts:77` —— `position_mismatch`。歌词行依靠 renderer 的 `scroll` 位移（`reference-wallpaper.ts:670` 输出的 `ey0Top` 每帧都在变），命中判定必然失败，于是每帧重跑完整的 element pass（`methods-render-glass-pef.ts:130-175`）。
2. `methods-render-glass-pef.ts:203-212` 已注明：post passes（外阴影、玻璃体复合、内阴影、压感辉光、边缘高光）**不缓存，每帧重画**。

**代价**：默认渲染距离下每帧约 5 行 × 4–5 个 draw 属于“每帧重做”，且其中 element pass 是 bbox 尺寸的完整片元着色（折射 + 染色 + 高光合成）。
**结论**：逐元素 FBO 缓存（上游渲染器的主要性能特性）在本项目的滚动场景中几乎不产生收益。这是滚动期最大的单项开销。

### 2.5 N3 · 滚动期每帧重建配置，并连带触发 FBO 重建与文字重栅格

`reference-wallpaper.ts:177-184`：只要 `scrollY` 相对上次布局变化超过 0.05 px 就调用 `rebuild()`。`rebuild` 内 `focus = scrollY / rowGap` 是**连续量**（`:182`），经 `depthScale()` 进入 `fitTypography()`（`:604-610`），得到连续变化的 `fontSize`。由此产生三条连锁反应：

1. **elFbo 反复销毁与重建**：`rect.w/h` 由 `Math.ceil` 得到整数，字号连续变化会不断跨越整数边界，使 `elementCacheSignature`（`methods-elements.ts:26-62`，`rect.w/h` 在第 28 行）变化并触发 `markElementDirty`；同时 `methods-render-glass-pef-cache-resolve.ts:134-143` 走 `size_mismatch` 分支，执行 `deleteFramebuffer` + `deleteTexture` + `createFBO`（含一次 `texImage2D` 分配）。
2. **文字纹理反复重栅格**：`rect.w/h` 变化使元素进入 `fgDirtyIds`，触发 `rasterizeText`（`methods-raster.ts:124-266`）——Canvas2D `fillText` 加一次纹理上传。这正是 v1 P2 提到的那一项。
3. **纯 JS 与 GC 开销**：`setButtons`（`methods-elements.ts:81-234`）每帧对每个元素做一次线性 `find()`（第 88 行，整体 O(n²)）、对旧新配置各做一次 `JSON.stringify` 签名（`:206-215`），外加 `glassRow` / `base`（`reference-wallpaper.ts:676-723`）中大量的对象与数组字面量分配。

**代价**：滚动的那约 0.5–1 秒内每帧数百微秒的主线程开销，叠加不定期的 GPU 资源重建。
**可分离的廉价修法**：把 `focus` 量化（例如 `Math.round(focus * 4) / 4`）。`rect` 尺寸随之稳定，第 1、2 条消失，景深缩放出现 0.25 行的台阶——需要截图确认是否可见。

### 2.6 N4 · 启动时预分配约 12 张全屏缓冲，其中约 10 张在本项目永不被采样

`methods-fbo.ts:96-215` 的 `resizeFBOs` 无条件创建下表全部缓冲：

| 缓冲 | 分配位置 | 本项目是否被采样 |
|---|---|---|
| `fboA` | `methods-fbo.ts:103-106` | ✅ 唯一的累积目标（逐元素 FBO 路径下 `curFbo` 从不交换） |
| `fboB` | `methods-fbo.ts:104-108` |  `renderGlassElementPerFbo` 原样返回，`otherFbo/otherTex` 未被使用 |
| `tabsBackdrop` | `methods-fbo.ts:109-115` | ❌ 需要 `isBottomTabContainer`，本项目不设置 |
| `wallpaperBlur` / `blurFboA` / `blurFboB` / `dsBlurLevels` 池 | `methods-fbo.ts:133-205` | ❌ 仅在 `blurRadius ≥ 0.5` 时进入，默认 0 |
| `highlightMask` | `methods-fbo.ts:215-220` | ❌ 全仓库无其他引用 |
| `dialogBackdrop` | `methods-fbo.ts:221-227` | ❌ 需要 `backdropFbo` + `scrimColor`，本项目不设置 |
| `bgOnly` | `methods-fbo.ts:228-234` | ⚠️ 被写入，从不被读取（见 N1） |

**代价**：约 12 × 全屏 × 4 字节。1080p 约 100 MB，1440p 约 190 MB；实际需要的只有 `fboA` + 壁纸纹理 + 每行 elFbo（约 2 张全屏量级），**约 80% 是纯浪费**。多显示器下 Wallpaper Engine 每屏一个上下文，会成倍放大。
**修法**：`resizeFBOs` 只保留 `fboA`；其余缓冲改为首次真正使用时惰性创建，并在尺寸变化时失效。

### 2.7 N5 · rAF 永不休眠

`main.ts:137-152` 的 `tick` 无条件自我重排。停稳后它仍以 60 Hz 执行 `findCurrentLine`、弹簧积分与 `draw()` 记账。由于歌词切换是**时间驱动**的，可以在停稳后计算“距下一次需要出图还有多久”，改用 `setTimeout` 唤醒，空闲时让出主线程。
**说明**：Stage 1 的验收标准只约束了 *rendered* FPS，GPU 确已归零；本条针对的是仍持续占用的主线程与合成器唤醒。

### 2.8 N6 · 空闲时钟每秒整屏重绘一次

`idle-content.ts:17-28` 每秒调用 `setIdleClock`，该方法（`reference-wallpaper.ts:235-241`）**不比较内容**即执行 `rebuild(0)` + `markAllDirty()` + `requestRender()`。显示内容为“时:分”，每分钟才变化一次，即 59/60 的重绘没有意义。此外 `idle-content.ts:38-43` 每秒新建两个 `Intl.DateTimeFormat`，应当缓存。

### 2.9 N7 · `resize` 没有防抖

`main.ts:186` → `reference-wallpaper.ts:117-123` → `refreshBackground()`（`:452-486`）。每次 resize 事件都会：新建一张全屏 canvas、`drawImage` 背景大图、`texImage2D` 上传、`resizeFBOs` 重建全部 FBO、全量文字重栅格。跨显示器拖动窗口时会被连续触发。

### 2.10 N8 · 资源体积

- `wallpaper/public/backgrounds/wallhaven-vpolwm.jpg`：4.7 MB。
- `wallpaper/public/fonts/ChillRoundF.ttf`：6.2 MB。该字体只被 fallback 的 DOM 歌词与未挂载的参数面板使用；WebGL 文字走的是系统字体栈（`reference-wallpaper.ts:18` 的 `RENDERER_FONT_FAMILY`）。`main.ts` 在启动时 `await document.fonts.ready`，需要确认它不会落在启动关键路径上。

---

## 3. 基线测量与验收规范（Stage 0）

控制台可通过 `window.lyricWallpaperPerformance.setEnabled(true)` 开启统计，`snapshot()` 获取 rAF/实际 render 频率、帧耗时、draw call、缓存命中与当前管线；`reset()` 开始新的采样窗口。调试 URL 追加 `?perf=1` 可在启动时直接启用。

### 采样场景

| 场景 | 时长 | 用途 |
|---|---:|---|
| 无歌词 / 无播放 | 60 秒 | 检查空闲帧与资源释放 |
| 空闲时钟常驻 | 60 秒 | 验证 N6：整屏重绘应降至约 1 次/分钟 |
| 两句歌词之间、滚动已停稳 | 60 秒 | 验证空闲渲染归零 |
| 普通歌词切换 | 连续 10 次 | 检查滚动流畅与峰值帧耗时 |
| 长歌词 + 高光/阴影开启 | 连续 10 次 | 压力场景 |
| 背景轮播 / 4K 背景切换 | 5 次 | 检查卡顿、内存和竞态 |
| 分辨率切换 / 跨显示器拖动 | 5 次 | 验证 N7 的防抖效果 |
| `lyricBehindGlass` + 高质量模糊 | 连续 5 次 | 兼容路径 |

### 指标

- render 次数/秒、rAF 次数/秒；
- 平均与 P95 帧耗时、掉帧次数；
- 每帧 draw call、玻璃元素缓存命中率、模糊 pass 数（`PerfSnapshot` 现有字段）；
- **新增：每帧 elFbo 重建次数**（N3、N4 验证用）；
- **新增：每帧文字纹理重栅格次数**（N3 验证用）；
- 显存占用：`createFBO` 调用次数与 `about:gpu` 读数（N4 验证用）；
- Canvas CSS 尺寸、实际像素尺寸、DPR；
- 浏览器/Wallpaper Engine 的 CPU、GPU、显存占用（同一机器、同一分辨率横向比较）；
- 歌词时间误差与视觉截图比对。

### 全局验收门槛

- 默认预设在视觉回归截图中不出现可见差异；
- 歌词行切换时间误差不超过现状；
- 不播放和停稳状态不应产生连续 GPU render；
- 出现 WebGL 上下文丢失、背景加载失败或兼容错误时，保留现有 CSS fallback。

---

## 4. 分阶段优化计划（v2 修订）

### P0 · 纯减法（零视觉风险，可合并提交）

目标：只删除没有收益的工作，画面逐像素不变。

1. **isolateBackdrop 按需开启**（N1）：让 `methods-render.ts:143` 的全屏快照与 `:245-249`、`:347-349` 的重复绘制以“存在非独立玻璃元素”为条件。判定谓词与 `methods-render-glass-transform.ts:288-291` 保持一致，从而在壁纸纹理缺失的降级路径下行为不变。
2. **FBO 惰性分配**（N4）：`resizeFBOs` 只保留 `fboA`；`fboB`、`tabsBackdrop`、`wallpaperBlur`、`blurFboA/B`、`highlightMask`、`dialogBackdrop`、`bgOnly` 与 `dsBlurLevels` 池改为首次使用时创建，尺寸变化时置空。
3. **空闲时钟内容去重**（N6）：`setIdleClock` 比较 time/date，未变化则直接返回；`IdleContent` 缓存 `Intl.DateTimeFormat` 实例。
4. **resize 防抖**（N7）：`main.ts:186` 的监听改为 150–250 ms 防抖后再调 `wallpaper.resize()`。

**验收**：默认截图逐像素一致；`?perf=1` 下滚动帧的 draw call 数下降；空闲时钟场景的 render 次数从约 60 次/分降至约 1 次/分。

### P1 · 滚动期减负（会改变运动中的画面，逐项独立提交）

目标：降低滚动期的重栅格与主线程开销，滚动停止后画面与现状一致。

5. **量化景深 focus**（N3）：`rebuild` 的 `focus` 量化到 1/4 行，稳定 `rect` 尺寸，消除 elFbo 重建与文字重栅格。**需要截图确认 0.25 行的缩放台阶不可见**；不可接受时改为量化到 1/8 行或仅在滚动期间量化、停稳后立即用精确值重建一次。
6. **`setButtons` 快路径**（N3）：以 id → config 的 Map 取代线性 `find()`；以逐字段比较取代 `JSON.stringify` 签名。
7. **滚动期关闭后置 pass**（N2）：滚动期间临时关闭 `quickToggles.outerShadow` 与 `highlight`，停稳后恢复。运动本身会掩盖差异，但**必须在实机上确认**；若可见则放弃该项。

**验收**：同一滚动片段的 elFbo 重建次数与文字重栅格次数低于基线；滚动结束后的静置画面与基线截图一致。

### P2 · 滚动相干的玻璃体缓存（收益最大，风险最高）

8. 在 `methods-render-glass-pef-cache-resolve.ts:77` 的命中判定中引入位移容差：累计位移小于阈值（建议 8 px 起步）时复用已缓存的 elFbo，仅在超过阈值时重栅格。

依据：`blurRadius` 默认为 0、`refractionHeight` 约 4 px，玻璃边缘的折射最多采样到阈值之外的壁纸像素，视觉上几乎不可见；而 element pass 的工作量直接除以阈值倍数。这是滚动期最大的一笔收益。
**前提**：需要先完成 P1 的第 5 项（否则尺寸抖动会让复用在 `size_mismatch` 处提前失效）。
**验收**：滚动期 element pass 次数下降至基线的 1/5 以下；静置与滚动中的截图差异在阈值内。

### P3 · 启动与空闲

9. **空闲休眠**（N5）：停稳后用 `setTimeout` 唤醒替换常驻 rAF。改动主循环时序，歌词同步回归面较广，需覆盖切歌、暂停/恢复、无歌词与空闲时钟四条路径。
10. **资源压缩**（N8）：背景图转 WebP/AVIF；字体子集化为 woff2，或确认其已移出启动关键路径。

### 参照：Stage 2（原 P1 背景分层）

维持暂缓。待 P0–P2 取得基线数据后再评估，且必须保留 `renderPipeline: auto | layered | scene-fbo` 回退开关。

---

## 5. 暂不实施的方案

### WebGL 2 迁移

当前 renderer 创建的是 WebGL 1 上下文。WebGL 2 可带来更现代的 FBO、纹理和 GLSL 能力，但它需要着色器与资源路径整体迁移，并须在 Wallpaper Engine 的实际 CEF 环境逐机验证。

当前主要损耗来自无效帧、滚动期重复栅格化与冗余显存占用，而不是 WebGL 1 API 本身。因此在 P0–P2 完成并取得基线数据前，不启动迁移。后续如需评估，应做“WebGL 2 优先、WebGL 1 回退”的独立原型，不能直接替换现网路径。

### 完整复制上游 renderer

当前直接从子模块导入不产生额外运行时网络成本，构建产物已将所需模块打包。完整复制会提高合并上游修复的成本，暂不推荐。

推荐保留子模块，并建立小型本地适配层承载本项目专属的帧调度、背景分层、质量预设和统计接口；只有确实需要改动核心渲染管线时才 fork。已确认 `wallpaper/vendor/liquid-glass-webgl` 指向本项目自己的 fork（`ningjx/liquid-glass-webgl`），P0 第 1、2 项与 P2 第 8 项的改动落在该子模块内，需要在子模块与父仓库各提交一次。

---

## 6. 建议的提交与验证顺序

1. `perf: skip unused fullscreen work when backdrop is isolated`：P0 第 1 项。
2. `perf: allocate scene FBOs lazily`：P0 第 2 项。
3. `perf: avoid redundant idle-clock redraws`：P0 第 3 项。
4. `perf: debounce wallpaper resize`：P0 第 4 项。
5. `perf: stabilize lyric layout during scroll`：P1 第 5 项。
6. `perf: speed up element list diffing`：P1 第 6 项。
7. `perf: drop post passes while scrolling`：P1 第 7 项（若不通过实机确认则放弃）。
8. `perf: reuse glass body within a scroll tolerance`：P2 第 8 项。
9. `perf: sleep the frame loop when idle`：P3 第 9 项。
10. `perf: shrink shipped background and font payload`：P3 第 10 项。

P0 的四项互相独立，可合并为一个提交；P1 起每项独立提交，便于单独回退。

每个提交都应执行：

```powershell
cd wallpaper
npm run test
npm run build
```

并完成本文件第 3 节对应的实机场景验证。

---

## 7. 实施前确认清单

- [ ] 已在目标 Wallpaper Engine / 显卡 / 分辨率组合上记录基线。
- [ ] 已保存默认视觉的参考截图或短视频。
- [ ] Stage 1 仅消除无效请求，不改变弹簧、歌词索引与提前量。
- [ ] P0 各项完成后默认截图逐像素一致。
- [ ] P1 每项完成后的静置画面与基线一致，且运动中的差异已实机确认可接受。
- [ ] Stage 2 保留 `scene-fbo` 回退，且背景坐标统一计算。
- [ ] 每项优化后验证背景轮播、resize、暂停/恢复、切歌、无歌词、空闲时钟、`lyricBehindGlass`、高质量模糊。
- [ ] 不将性能优化与 WebGL 2 迁移混在同一变更中。
- [ ] 子模块改动已推送到 `ningjx/liquid-glass-webgl` 并更新父仓库指针。

---

## 8. 失败实验记录：`lyricBehindGlass` 背景模糊（2026-09-07）

目标是让歌词位于玻璃背后，同时保留“模糊背景 + 清晰歌词参与折射 + 顶层高光”的层次。以下尝试均已经过实机否定，**不得在没有可视化 FBO 验证的前提下恢复**：

1. 将预模糊壁纸直接写入主场景 `fboA`：会使玻璃外的整张壁纸一起模糊，违反分层要求。
2. 将预模糊纹理作为 `bgOnlyFbo` 的初始内容：实机中普通模糊和高质量模糊均没有出现在歌词玻璃内。`bgOnlyFbo` 是“隔离其他玻璃”的优化缓存，不能作为该功能的可靠语义层。
3. 新增专用歌词背板 FBO（模糊壁纸 → 绘制歌词 → 玻璃采样）：实机仍未产生预期模糊，说明仅按代码路径推断纹理输入不够，当前实现已撤销。
4. “开启后置模式时自动将默认半径从 0 调为 12”的假设错误：用户确认普通与高质量路径一直都失效；该临时改动已撤销。

后续若重启该功能，先建立可验证的最小原型：在同一帧分别把原始壁纸、预模糊纹理、歌词合成背板输出到可切换的调试视图或捕获截图，确认每个 FBO 的像素内容、方向和 alpha；再接入正式歌词渲染。验收必须覆盖普通模糊、高质量模糊、歌词滚动、切歌、壁纸切换与 resize。