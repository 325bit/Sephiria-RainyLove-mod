# rainylove — 最后一人存活

多人模式（场上 ≥2 人）中，**当场上只剩最后一个存活玩家时，播放 `rainylove`**。

游戏：Sephiria（TEAM HORAY，Unity 6 / BepInEx 5.4）
类型：纯客户端表现层 mod，只读游戏状态，不改任何服务端数据，联机安全。

---

## 功能逻辑

本 mod 含两个互相独立的音频触发器，由统一的 `AudioChannel` 仲裁“谁在播”：

### RainyLove（最后一人存活）
扫描场上存活玩家数：

1. 取场景中所有 `PlayerAvatar`，只统计 `isInDungeon != 0`（同一地牢内）的玩家。
2. 存活人数 < 2（单人局 / 还没进地牢）→ 不触发。
3. 存活人数从 ≥2 降到 **1** → 触发一次播放。
4. 存活人数回升到 ≥2，视为新的一轮，重置状态（下一局可再次触发）。

### AIZO（血量过低）
检测**自身玩家**（`authority` 标记）的血量：

- 当前血量百分比 `< AizoHealthPercent`（0~99%）**或** 当前血量固定值 `< AizoHealthValue`（0~100）时播放 `AIZO.wav`。
- 两个阈值都为 0 时视为“关闭该条件”（永远不满足）。
- 血量恢复高于阈值后停止（并允许下一轮再次触发）。
- 血量字段名在不同构建里不确定，插件用反射按候选名探测 `PlayerAvatar` 的 current / max / 直接百分比字段，
  启动后会在日志打印实际探测到的字段名（如 `current=CurrentLife(field)`），便于确认。

### 音频播放优先级（`AudioPriority`，主设置里切换）
两者同时想播放时：
- **RainyLove优先**：RainyLove 总是抢占 AIZO；
- **AIZO优先**：AIZO 总是抢占 RainyLove；
- **先来后到**：先开始播放的那个独占，另一个等它结束再播。

手动“测试播放 / 音频播放测试”是用户显式操作，优先级最高，总会立即接管当前声音；
且手动播放**只受对应的停止按钮控制**，不被任一存活 / 血量判定打断。

## 配置界面（F7）

按 **F7**（`MenuKey`，可在配置里改）呼出 / 关闭一个暗色设置面板，分为三个子项标签页：

### 标签页 1 · RainyLove
| 控件 | 作用 |
|---|---|
| 播放音量 | 0~100%，等价于 `Volume` |
| 淡出时长 | 0~300 秒，播放结尾由设定音量线性降到静音（**已移除淡入**） |
| ▶ 测试播放 | 不看存活判定，直接播放（只受停止按钮控制） |
| ■ 停止播放 | 立即中断当前 RainyLove 播放 |
| 完成 / × | 关闭面板并把配置写入 `.cfg` |

### 标签页 2 · AIZO
| 控件 | 作用 |
|---|---|
| 播放音量 | 0~100%，等价于 `AizoVolume` |
| 淡出时长 | 0~300 秒（0 表示不淡出），等价于 `AizoFadeOutSeconds` |
| 开始播放的血量阈值（%） | 0~99%，等价于 `AizoHealthPercent` |
| 开始播放的血量固定值 | 0~100，等价于 `AizoHealthValue` |
| ▶ 音频播放测试 | 直接播放 AIZO（只受停止按钮控制） |
| ■ 音频播放停止 | 立即中断当前 AIZO 播放 |

### 标签页 3 · 主设置
| 控件 | 作用 |
|---|---|
| 启用 RainyLove | 总开关，等价于 `Enabled` |
| 启用 AIZO | 总开关，等价于 `AizoEnabled` |
| 音频播放优先级 | RainyLove优先 / AIZO优先 / 先来后到，等价于 `AudioPriority` |

面板打开时会接管鼠标光标并屏蔽角色输入，避免点按钮时角色乱跑；关闭后自动还原。

预期结果：

| 现象 | 结论 |
|---|---|
| 点“测试播放”后音乐响起，开头有淡入 | 音频链路与包络渲染正常 |
| 日志出现 `[rainylove] 音频准备失败：…` | 找不到 rainylove.wav，检查 `BepInEx\plugins\rainylove\Asset\` |
| 按 F7 没反应、也没日志 | 输入桥问题，看启动日志的 `输入系统可用` 与 `输入自检` 两行 |

音频约 3 分 39 秒（48kHz 立体声 16-bit），播完自动停。

## 输入处理（重要）

本游戏 Player Settings 已把 **Input Handling 切到 Input System 包**，此时调用旧的
`UnityEngine.Input.GetKeyDown` 会抛 `InvalidOperationException`。

更麻烦的是：**异常发生在 `Update` 里会把整帧逻辑打断**。早期版本把按键检测写在 `Update` 开头，
导致存活扫描一帧都没执行过，mod 完全不工作，同时每帧刷一条错误（一次启动 2111 条，日志文件 820KB）。

反射访问按键时有**两个反直觉的点**（都用 Mono.Cecil 核对 `Unity.InputSystem.dll` 确认过）：

| 常见假设 | 实际情况 |
|---|---|
| `Keyboard` 上有生成的 `f7` 属性 | **没有。** 整个 Keyboard 只有 9 个非按键属性（`keyboardLayout`/`allKeys`/`current`/`Item` 等），按键一律走**索引器** `Keyboard.current[Key.F7]` |
| `wasPressedThisFrame` 在 `Keyboard` 上 | **不在。** 它在 `ButtonControl` 上，要按控件的运行时类型向上找基类链 |

用 `Keyboard.GetProperty("f7")` 会返回 null，反射调用时报"缺少 wasPressedThisFrame"，
表象很像"属性名错了"，实际是压根没这个成员。

**第三个坑（时序）**：反射链的最后一步依赖运行时状态。BepInEx 的 `Awake` 跑在所有 MonoBehaviour
之前，此时 Input 运行时还没建设备，`Keyboard.current` 是 `null`。第一版把"能否读到按下状态"
在 `Awake` 里判断，恒为 False，于是启动日志显示 `F7 已解析=False`——**这不是解析失败，只是设备没就绪**。

修复方式：

1. 新增 `InputBridge.cs`，用反射调 `Unity.InputSystem`：
   - 静态属性 `Keyboard.current` 取键盘
   - 索引器（反射里名为 `Item`，按**参数类型**筛选；实测 `Unity.InputSystem` 里只有 `Key` 一套，不存在 string 重载）
   - 传 `Key` 枚举值（如 `F7`）拿到 `KeyControl`，其基类为 `ButtonControl`
   - 从基类链上取 `wasPressedThisFrame`（属性 + `get_xxx()` 两种形式都兼容）
   - 映射与读取器全部按类型缓存，`Update` 热路径只查 Dictionary，不做 `Enum.Parse`
2. `Update` 里**扫描逻辑放在最前面**，按键检测放到最后且吞掉所有异常。
   即使输入系统彻底不可用，也只是 F7 失效，不影响自动触发。
3. **把"结构解析"和"设备就绪"拆成两层**，不再在 `Awake` 里判断可用性：
   - `Awake` 只报告 API 层是否与键名映射成功（这是确定的）
   - 运行时延迟约 2 秒做一次自检并打日志，失败时写明具体环节
     （`Keyboard.current 为 null` / 控件为 null / 读取失败 / 键名不在枚举里）
4. 真按下 F7 时额外打印一条 `[rainylove] F7 按下已识别`，给出正向确认。

日志里该看到的两行：

```
[rainylove] 输入系统可用=True，F7 键名映射=True
[rainylove] 输入自检通过：F7 可正常读取（Input System 运行正常）。
```

## 第四个坑：AudioSource.Play 的重载二义性

首版实测（双人联机）时，**检测逻辑每次都正确触发**，但播放时抛
`AmbiguousMatchException: Ambiguous match found`，声音从未响起。

原因：`AudioSource` 有两个公开重载 `Play()` 和 `Play(ulong delay)`，
反射里 `GetMethod("Play")` 按名字取方法，遇到重载直接抛二义性异常。
Cecil 实测确认（`UnityEngine.AudioModule.dll`）：

```
M  [pub] Void Play()
M  [pub] Void Play(UInt64)
```

修复：`GetMethod("Play", Type.EmptyTypes)` 按"无参"精确匹配，并缓存在 `AudioLoader` 里；
同时给 `PlayClip` 整体套 try/catch——播放环节出任何问题都记日志跳过，不影响存活检测。

**教训**：反射 `GetMethod(name)` / `GetProperty(name)` 只能用于**没有重载**的成员；
有重载时必须传 `Type[]` 参数列表精确匹配。写之前先拿 Cecil 看一眼有没有同名重载。

## 联机影响

**不需要房间任何一方额外安装，装了的那台机器自己生效。**

- 插件全程只**读** Mirror 同步下来的 `PlayerAvatar` 状态（`isInDungeon`、`IsDead`），
  没有任何 `Command` / `Rpc` / `NetworkIdentity` 写入，不发网络包、不改存档、不改游戏状态。
- 播放动作发生在本地：新建的 `GameObject` 挂的是普通 `MonoBehaviour`（非 `NetworkBehaviour`），
  `Destroy` 也是本地销毁，不会进入 Mirror 的同步列表。
- 所以 **房主没装、只有你装，也只有你能听到**；想让四个人都听到，四台机器都得装。

已知边界（与"纯客户端"相称，非缺陷）：

| 情况 | 表现 |
|---|---|
| 队友**掉线**而非死亡 | 不会被判为死亡，存活人数不降，不会触发 |
| 队友**观战 / 没进地牢** | 被 `isInDungeon` 过滤掉，不计入存活 |
| 只剩 1 人时想让所有人同时听到 | 各自客户端都会各自检测到并播放（不依赖房主） |
| 只希望最后存活的**那个人**听到 | 打开 `OnlyWhenSelfIsLast` |

## 安装

已部署到：

```
<游戏根目录>\BepInEx\plugins\rainylove\RainyLove.dll
<游戏根目录>\BepInEx\plugins\rainylove\Asset\rainylove.wav
<游戏根目录>\BepInEx\plugins\rainylove\Asset\AIZO.wav
```

手动安装：把 `bin\Debug\RainyLove.dll` 拷到 `BepInEx\plugins\rainylove\`，把 `rainylove.wav` 与 `AIZO.wav` 拷到 `BepInEx\plugins\rainylove\Asset\`。
mod 播放的是 WAV，不依赖 mp3（mp3 仅作为老安装、且本机装有 ffmpeg 时的解码兜底，可删）。

**卸载**：删除 `BepInEx\plugins\rainylove\` 整个文件夹即可，不影响其他 mod。

## 配置

启动游戏生成 `BepInEx\config\com.rainylove.lastsurvivor.cfg` 后可修改：

| 键 | 默认 | 说明 |
|---|---|---|
| `Enabled` | true | RainyLove 总开关 |
| `Volume` | 0.4 | RainyLove 播放音量 0~1 |
| `AudioFilePath` | 空 | RainyLove 音频目录或文件路径，留空用默认位置 |
| `ScanIntervalSec` | 0.25 | 存活人数扫描间隔（秒） |
| `OnlyWhenSelfIsLast` | false | 仅当自己就是最后存活者才播放 |
| `FadeOutSeconds` | 2 | RainyLove 播放结尾淡出时长（秒），0~300，0 表示不淡出 |
| `ResampleHz` | 44100 | 解码采样率；44100 约 70MB 内存，22050 约减半 |
| `AizoEnabled` | true | AIZO 总开关 |
| `AizoVolume` | 0.4 | AIZO 播放音量 0~1 |
| `AizoAudioFilePath` | 空 | AIZO 音频目录或文件路径，留空用默认位置 |
| `AizoFadeOutSeconds` | 2 | AIZO 播放结尾淡出时长（秒），0~300，0 表示不淡出 |
| `AizoHealthPercent` | 30 | AIZO 触发阈值：自身血量百分比低于该值(0~99%) |
| `AizoHealthValue` | 30 | AIZO 触发阈值：自身血量低于该固定值(0~100) |
| `AudioPriority` | RainyLoveFirst | 音频播放优先级：RainyLoveFirst / AizoFirst / FirstCome |
| `MenuKey` | f7 | 呼出 / 关闭配置界面的按键 |

> 旧版本的 `EnableTestKey` / `TestPlayKey`（F6 强制播放、F7 强制停止）已移除，
> 这两个功能改由 F7 配置界面里的“测试播放 / 停止播放”按钮提供。RainyLove 的淡入已移除（只保留淡出）。

## 音频说明

本构建里 Unity 的 `AudioClip.Create/SetData` 与 `UnityWebRequest`（含 `file://`）全部失效，
所以播放最终改走 **Windows 系统音频**（`winmm` 的 `PlaySound`，见 `AudioLoader.SystemAudio`），
完全绕开 Unity 自己的音频管线；停止用 `PlaySound(NULL, 0, SND_PURGE)`，不会变成“重播”。
所有对音频码流的处理（音量缩放、淡入淡出）都在写 WAV 之前用纯 C# 完成。

解码流程：

1. 优先使用打包的 `rainylove.wav`（16-bit PCM）；**音量与淡入淡出由纯 C# 直接改写 PCM 采样**烘焙进一份临时 WAV，
   不需要用户安装 ffmpeg，也不会动到分发包里的原始音频。
2. 音量≈100% 且淡入淡出都为 0 时跳过拷贝，直接播放原文件。
3. 渲染结果按“源文件 + 音量 + 淡入 + 淡出”缓存，参数没变就复用，避免反复点测试播放时重复渲染 40MB 音频。
4. 没有 wav 时若同目录有 `rainylove 1.03x.mp3` 且本机有 ffmpeg，则先解码成 WAV 再套用同样的音量 / 淡入淡出处理。

ffmpeg 查找顺序：`BepInEx\plugins\rainylove\tools\ffmpeg.exe` → `BepInEx\plugins\rainylove\ffmpeg.exe` →
`D:\tools\ffmpeg\bin\ffmpeg.exe` → 系统 PATH。找不到会在日志里警告（仅影响 mp3 兜底路径，已打包 wav 时用不到）。

## 开发

```bash
dotnet build -p:GameDir="F:\SteamLibrary\steamapps\common\Sephiria"
dotnet build -p:GameDir="F:\SteamLibrary\steamapps\common\Sephiria" -p:DeployMod=true   # 编译并直接部署
```

目录：

- `Plugin.cs` — BepInEx 入口、配置项、音频路径解析
- `LastSurvivorWatcher.cs` — RainyLove：存活人数扫描与触发
- `AizoWatcher.cs` — AIZO：自身血量检测与触发（反射探测血量字段）
- `AudioChannel.cs` — 播放仲裁器：RainyLove / AIZO 的优先级、手动/自动播放协调、自然结束复位
- `ConfigUi.cs` — F7 呼出的 IMGUI 配置界面（RainyLove / AIZO / 主设置 三个标签页）
- `AudioLoader.cs` — 音量 + 淡出渲染、WAV 解析、系统音频播放
- `InputBridge.cs` — Input System 键盘查询（反射，避旧的 `UnityEngine.Input` 抛异常）

调试：用 dnSpy 打开 `Assembly-CSharp.dll`（连同 `Sephiria_Data\Managed` 下所有 dll）查看游戏侧逻辑；
调试本插件可把 `BepInEx.cfg` 的 `LoadDumpedAssemblies` / `BreakBeforeLoadAssemblies` 打开后从 dnSpy 启动游戏。
