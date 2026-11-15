# 可行性报告：AIZO「BOSS 战期间不中断 + 播完循环」

> 范围：仅 MP3 路径（`mpegvideo` 驱动）。WAV 路径按你的决定废弃，见附录 B。
> 本文是决策文档，不含代码改动。结论基于 `Assembly-CSharp.dll` 反编译 + 本机 MCI 实测。

---

## 1. 需求转述

在 AIZO 页新增一个开关，开启后：

1. 处于**小 BOSS / BOSS 战斗**中时，AIZO **不停止**——无论血量是否回到阈值以上、无论顺延开关怎么设。
2. 战中如果整首播完而战斗还没结束，**继续从头重播**（可多轮）。
3. 战斗结束后，才按「顺延播放」开关执行：**开** = 顺延到时间轴节点再停；**关** = 立即停。
4. 战斗结束后如果血量仍在阈值以下，则照常继续播（不适用停止逻辑）。

触发条件不变：仍然是本机玩家血量低于阈值才开始播。本功能只改「什么时候停」和「播完要不要重播」。

---

## 2. 结论摘要

| 环节 | 可行性 | 依据 |
|---|---|---|
| 客户端判定「正在小 BOSS / BOSS 战」 | **可行，可靠** | `UnitAvatar.monsterType` + `IsDead` 都是 `[SyncVar]`，联机下客户端可见 |
| 战斗结束判定 | **可行** | 同上，最后一个 BOSS 死亡即结束（口径见 §3.4） |
| MP3 无缝循环 | **可行，已实测** | MCI `play <alias> repeat` 在 `mpegvideo` 上生效 |
| 循环中仍能顺延到节点 | **可行** | `status <alias> position` 在 repeat 下返回「当前这一轮」的位置，已实测 |
| 新增审核风险 | **几乎为零** | 不新增依赖、不新增 DLL、不新增 P/Invoke；`repeat` 只是已有 `mciSendString` 的另一条命令 |

**整体：可行。** 有一处必须避开的坑（§3.1 第一条），以及 3 个需要你拍板的口径（§8）。

---

## 3. 敌情检测（本功能的技术核心）

### 3.1 候选信号逐个排查

反编译 `Assembly-CSharp.dll` 后，与本需求相关的信号只有四个：

| 信号 | 客户端可见 | 结论 |
|---|---|---|
| `BossSpawner.IsBossBattleInProgress` | **否** | ❌ **不能用**（详见下） |
| `UnitAvatar.monsterType`（`EMonsterType`） | 是（`[SyncVar]`） | ✅ 主信号 |
| `UnitAvatar.IsDead` | 是（`[SyncVar]`） | ✅ 主信号 |
| `spawner.playerPrevent` | 是（`[SyncVar]`） | ⚠️ 可用但**不区分** BOSS 房和普通战斗房 |
| `UI_BossHPBar` 实例存在 | 是（纯客户端） | ⚠️ 只有 BOSS 有，小 BOSS 没有 |

**第一个坑**——`BossSpawner` 里有一个看起来完全对味的公开属性：

```csharp
// bossspawner.cs:202
public bool IsBossBattleInProgress => battlePhase == EBattlePhase.BeginBattle;
```

但 `battlePhase` **只在服务端 `Update()` 里被改写**（`bossspawner.cs:412-414`，整段包在 `if (base.isServer)` 里），
而客户端 RPC 的实现体是空的：

```csharp
// bossspawner.cs:1537
protected void UserCode_RpcStartBattle()
{
    ClientOpenBossHPBar();      // 只开血条 UI，不动 battlePhase
}
```

也就是说这个属性**在房主端为真、在所有客机端恒为假**。这个 mod 是纯客户端 mod，必须对非房主玩家生效，
所以 `IsBossBattleInProgress` 一旦被当成主判据，会重演 1.4.5 那次「AIZO 只在房主端触发」的事故。
**结论：不采用。**

### 3.2 推荐判据

```csharp
// 语义：场上存在存活的小 BOSS 或 BOSS
foreach (var u in Object.FindObjectsOfType<UnitAvatar>())
    if (!u.IsDead && (u.monsterType == EMonsterType.Miniboss || u.monsterType == EMonsterType.Boss))
        return true;
```

`EMonsterType` 的枚举定义正好对上你说的两类：

```csharp
public enum EMonsterType { Normal, Miniboss, Boss, Dummy }
```

- `Miniboss` = 你说的**小 BOSS**（游戏内常量也叫 `minibossBonusHpByPlayerNumber`）
- `Boss` = 关底 BOSS
- `Dummy` = 训练木桩，排除

**为什么「场上存在存活 BOSS」等价于「正在 BOSS 战」**：两类 BOSS 都由房间生成器在玩家进入时生成
（`BossSpawner` 用碰撞区/开关检测玩家进入后 `StartBattle`；普通战斗房由 `CommonEnemySpawner` 在玩家进入时开波），
且开战后用 `playerPrevent` 把玩家锁在房间内。所以只要它活着，玩家就一定被锁在它这一场里，不存在「隔壁房间的 BOSS 误判」。

### 3.3 成本与防抖

- `FindObjectsOfType<UnitAvatar>()` 每 **0.5s** 一次；地牢同屏单位通常数十个，开销可忽略
  （现有 `LastSurvivorWatcher` 已经在按 0.25s 做同样的全文扫描，只是扫的是 `PlayerAvatar`）。
- 结束判定加 **1.5s 滞后**：连续 1.5s 都找不到存活 BOSS 才算战斗结束，避免 BOSS 召唤波次之间的空档、
  以及「BOSS 死了但演出还在播」时音乐被切。

### 3.4 需要你选：战斗结束的口径

| 口径 | 音乐停的时机 | 实现 |
|---|---|---|
| **A（推荐）** BOSS 死亡即结束 | BOSS 血条清零后约 1.5s | 只看 `IsDead` |
| B 胜利演出播完才结束 | 掉落/结算界面出现之后 | 额外与 `spawner.playerPrevent`（SyncVar，死亡演出期间仍为 true）取与 |

A 更符合「战斗结束」的直觉，B 会让音乐盖住胜利演出。**默认按 A 实现**，B 作为可选，你说一句就换。

---

## 4. 播放层改造

### 4.1 现状

`AudioChannel` 是单通道仲裁器，靠 `_endTime` 定时器判断自然结束：

```csharp
// AudioChannel.cs:70
if (_current != null && _endTime > 0f && Time.unscaledTime >= _endTime)
{
    SystemAudio.Stop(_currentPath);
    Log.Message("[rainylove] 音频自然结束，已复位状态（" + src + "）。");
    ResetState();
}
```

没有任何循环概念，所以「播完再播」目前必须靠外部重新 `Play()`——而 `Play()` 会 `CloseDevice()` 再 `open`，
实测会掉到 0.7s 左右的位置（上一轮 `mci_restart` 已量过），听感上是明显的断点。

### 4.2 MP3 用设备级循环（已实测）

MCI 支持 `play <alias> repeat`，且 `mpegvideo` 驱动接受该标志。本机实测（只循环前 3000ms，便于观察）：

```
play rep1 repeat from 0 to 3000 -> ok
    t= 1.0s pos=  906ms mode=playing
    t= 2.0s pos=  372ms mode=playing
    t= 3.0s pos= 1338ms mode=playing
    t= 4.0s pos=  738ms mode=playing
    t= 5.0s pos=  160ms mode=playing
stop -> ok   after stop: pos=921 mode=stopped
```

位置在 0~3000 之间反复回卷、`mode` 始终 `playing`——**真正的无缝循环，没有 close/open 的空档**。
`stop` 仍能随时打断。

改造点很小：`SystemAudio.PlayOnWorker` 里把 `"play " + alias` 换成
`loop ? "play " + alias + " repeat" : "play " + alias`，其余（音量、单设备互斥、停止、关闭）全部不动。

### 4.3 循环中仍要能顺延到节点

循环状态下 Unity 侧的时间轴是无效的（`_endTime` 永不到达，`Time.unscaledTime - _startTime` 会一路超过曲长）。
而「战斗结束后按顺延开关停」需要知道**当前这一轮播到哪**。

实测 `status <alias> position` 在 repeat 下可用，返回的是当前这一轮的位置（上表 906/372/1338/738/160）。
所以给 `SystemAudio` 加一个查询：

```
set <alias> time format milliseconds
status <alias> position   →  当前轮内的毫秒数
```

`AudioChannel.GetElapsedSeconds()` 在循环态改为返回「轮内位置」，而不是 `Time.unscaledTime - _startTime`。
这样 `PlaybackNodeScheduler`（p2 00:43.5 / p3 01:38 / p4 02:20 / p5 曲末）可以原样复用，一行不用改。

### 4.4 完整状态机

设 `hold = 开关开启 && BossFightWatcher.Active`：

| # | 战中 hold | AIZO 正在自动播 | 血量仍低 | 行为 |
|---|---|---|---|---|
| 1 | 是 | 是 | 任意 | **不停止**；以循环方式继续（`repeat`） |
| 2 | 是 | 否 | 是 | 照常触发播放（新触发也直接带循环） |
| 3 | 是 | 否 | 否 | 不启动（保持现有触发条件不变，见 §8-②） |
| 4 | 否（刚结束） | 是 | 否 + 顺延**关** | 立即停止 |
| 5 | 否（刚结束） | 是 | 否 + 顺延**开** | 从当前轮内位置顺延到下一个节点再停 |
| 6 | 否（刚结束） | 是 | 是 | 继续播（触发条件仍成立），退出循环态 |
| 7 | 否 | 是 | — | 完全走现有逻辑，行为与 1.4.6 一致 |

关键实现细节：**退出循环态时不需要重新开设备**。`repeat` 只是「播到末尾继续」，一旦条件解除走
`StopAuto` 即可关设备；只有 #6 需要「停止重复但保持播放」——这种情况实际选择是**继续循环到底**，
因为触发条件（低血量）还在，它本来就该一直响；等血量恢复后落入 #4/#5 正常停止。这样既不需要
中途取消 repeat（MCI 不支持单独取消，必须 stop+play、会跳回 0:00），也符合直觉。

---

## 5. 配置与界面

新增字段（AIZO 组）：

```csharp
// ModConfig
public static bool AizoBossHold = false;      // JSON 键名 AizoBossHold
```

- **默认关闭**（推荐）：这是会明显改变听感的行为，默认开启会让没看说明的玩家再次抱怨「音乐一直响」。
  想用的人自己在面板里打开。
- 老配置文件里没有这个键 → Newtonsoft 反序列化后保持字段初始值 `false`，不影响其它设置。

UI 位置：AIZO 页。现布局已占满 118~440（窗口 620×500），需要在「顺延播放」说明与分隔线之间插入一行：

```
播放音量                              40%
开始播放的血量阈值（%）                70%
开始播放的血量固定值                   70
BOSS 战期间保持播放（战后按顺延设置停止）  [ 开关 ]
顺延播放：血量回升到阈值以上时…
────────────────────────────
状态 / 音频播放测试 / 停止 / 完成
```

需要把 308 以下的元素整体下移约 48px，或把窗口高度从 500 提到 548。两行文案要能说清「战中不停、战后按顺延」，
否则和现有「顺延播放」的措辞容易混。

---

## 6. 版本与发布

- 版本 → **1.4.7**（1.4.6 你已上传，DLL 变了必须重新过审）。
- 包体积不变（无新增文件、无新增资源）。
- 审核面不变：仍是纯 C# + 已有的 `mciSendString`；`repeat` 只是命令串多了 7 个字符，不引入新 DLL、新 P/Invoke、新网络行为。
- `metadata.json` 的 `multiplayer.installation` 保持 `local_only`（纯客户端表现层，不改游戏状态）。

---

## 7. 测试计划

**单元/回归（可离线跑，沿用现有手法）**

1. `BossFightWatcher` 判定：把 `UnitAvatar` 列表做成可注入，覆盖
   空场 / 只有 Normal / 只有 Dummy / Miniboss 存活 / Boss 存活 / Miniboss 已死(尸体还在) / BOSS 死+新波次生成。
2. 防抖：0.0s / 1.0s / 1.6s 三个时刻的 Active 结果。
3. 状态机：§4.4 的 7 种组合逐一断言（是否停止、是否循环）。
4. 顺延节点：循环态下 `GetElapsedSeconds` 返回轮内位置后，`PlaybackNodeScheduler` 的现有 25 项断言仍需全过。

**MCI 层（脚本，几分钟）**

5. `repeat` + `setaudio volume`：确认音量在循环下不失效。
6. `repeat` → `stop` → `close`：确认不残留设备、进程退出后无残留音频。
7. 循环边界听感：用真实 AIZO.mp3 循环 2 次，确认无爆音/无静音空档。

**实机（必须，各 1 次）**

8. 小 BOSS 房：进房 → 压血触发 → 血量回满 → 确认**不中断**；打死小 BOSS → 确认按顺延开关停止。
9. BOSS 房：同上；另外确认「整首播完战没结束」时会重播（可以把阈值调高、血量压低，故意拖时间）。
10. 顺延开 / 关两种设置各跑一次战后停止时机。
11. 战中死亡与被救起：确认音乐不误停、也不无限延续。
12. 连续两个 BOSS（如果该层有）：确认第二个 BOSS 接上时不会误判结束。
13. 联机：房主 + 客机各一份日志，确认两端都在自己战斗时保持、结束后停止（这条是当初 `authority` 事故的回归点）。

---

## 8. 三个口径（已拍板 2026-11-15）

**① 战斗结束的判定时机 → A：BOSS 死亡 + 1.5s 滞后。** 实现见 `BossFightLogic.EndGraceSec`。

**② 战中「本来没在播」要不要强制开播 → 不强制。** 仍以低血量为唯一触发条件，hold 只延长、不起播。

**③ 战中被 RainyLove 抢占 → A：不动优先级。** `RainyLoveFirst` 下 RainyLove 仍然可以接管并让 AIZO 停下，
这与「战中 AIZO 不停止」的冲突以优先级为准，不在 hold 里做特殊处理。

实现与验证结果见 `MIGRATION-WORKSHOP.md` §13。

---

## 9. 改动清单（预计）

| 文件 | 改动 | 规模 |
|---|---|---|
| `src/BossFightWatcher.cs` | 新增：0.5s 扫描 + 1.5s 滞后，暴露 `Active` | ~90 行 |
| `src/AudioLoader.cs` | `SystemAudio.Play(..., bool loop)`；新增 `GetPositionSeconds()` | ~40 行 |
| `src/AudioChannel.cs` | 循环态标记；`GetElapsedSeconds` 在循环态改走 MCI 位置；自然结束定时器在循环态不自燃 | ~40 行 |
| `src/AizoWatcher.cs` | `Evaluate()` 插入 hold 分支与战后过渡 | ~50 行 |
| `src/ModConfig.cs` | `AizoBossHold` + `Settings` 字段 | ~4 行 |
| `src/ConfigUi.cs` | AIZO 页新增开关 + 布局下移 | ~25 行 |
| `metadata.json` ×2 | 版本 → 1.4.7 | 2 行 |

无新增依赖、无新增 DLL、无新增 P/Invoke。

---

## 附录 A：证据位置

| 事实 | 位置 |
|---|---|
| `IsBossBattleInProgress` 只用 `battlePhase` | `BossSpawner.cs:202` |
| `battlePhase` 仅在服务端 `Update()` 内改写 | `BossSpawner.cs:412-414` + `:786` + `:998` |
| 客户端 RPC 只开/关血条 | `BossSpawner.cs:1537-1539` |
| `playerPrevent` 是 SyncVar（客户端可见） | `BossSpawner.cs:106/230-239`、`CommonEnemySpawner.cs:39/110-119` |
| 开战置 true / 清场置 false | `BossSpawner.cs:787 / 998`、`CommonEnemySpawner.cs:452 / 430` |
| `EMonsterType { Normal, Miniboss, Boss, Dummy }` | `EMonsterType.cs` |
| `UnitAvatar.monsterType` / `IsDead` 均为 SyncVar | `UnitAvatar.cs:61-62` / `:274` |
| 血条由 `ClientOpenBossHPBar` 创建 | `BossSpawner.cs:1439` |
| 只走 `mpegvideo`（1.4.10 起 WAV 路径已删） | `AudioLoader.cs` 的 `PlayOnWorker` |
| 自然结束靠 `_endTime` 定时器 | `AudioChannel.cs:67-77` |

## 附录 B：WAV 路径的处置（已在 1.4.10 执行）

你已决定废弃 WAV 路径。现状是 `.wav` 在 `ModConfig.AudioExtensions` 里排在 `.mp3` 前面、优先级更高，
且 `Asset/README.txt` 对外宣传「可放同名 wav 做无损替换」。真正的废弃需要动四处：

1. `ModConfig.AudioExtensions` 去掉 `.wav`（否则玩家放了 wav 仍然会被选中）；
2. `AudioLoader.RenderProcessedWav` / 音量烘焙分支整段删除（waveaudio 无音量控制，这条路径才存在）；
3. `Asset/README.txt` 与 `README.md` 的说明改写；
4. `SystemAudio` 里 `device` 的扩展名判断可以简化成固定 `mpegvideo`。

**执行记录（1.4.10）**：上面四项全部完成，另外还删了 `GetWavDuration`、`_volumeBaked` 标志与 `ResampleHz`
字段，`_currentWav` 重命名为 `_currentPath`。详细清单与回归测试见 `MIGRATION-WORKSHOP.md` §16。
`play ... repeat` 失败后回退单次播放这段**保留**（防御 mpegvideo 本身异常），但它不再与格式相关。

当初的建议是「单独一版做，不要混进 1.4.7」——本次正是如此：1.4.7/1.4.8/1.4.9 处理 BOSS 战保持的 bug，
WAV 清理放在 1.4.10，两者互不干扰。补充一条实测数据：`waveaudio` 不支持 `repeat`，
`play w1 repeat from 0 to 1000` 直接返回错误码 **259（MCIERR_UNSUPPORTED_FUNCTION）**；
这也说明保留 WAV 路径的话，「战中循环」在 WAV 上只能退化成有断点的重启，体验不一致——更加印证废弃是对的。
