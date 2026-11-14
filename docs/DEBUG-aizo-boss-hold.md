# AIZO「BOSS 战保持」停止失效 —— 调试报告（1.4.7 → 1.4.9）

> 症状：打完 BOSS 进下一层后，血量回到阈值以上，推图打小怪时 AIZO 仍然不停止播放。
> **真因：`PlayerAvatar` 继承 `UnitAvatar`，而玩家 prefab 上 `monsterType` 序列化的值就是 `Boss`。**
> 「场上存在存活的 Miniboss/Boss 单位」这条判据因此**从玩家出生那一刻起恒为真**（距离 0、全程存活），
> 「保持」永久锁死，`AizoWatcher.Evaluate` 里所有停止路径变成不可达代码。不是音频层问题。
>
> 本报告含两轮：1.4.8 修了「口径」但漏掉玩家自身，仍然失败；1.4.9 定位到玩家自身并修掉。

---

## 1. 复现条件（稳定复现）

1. 配置：`AizoBossHold=true`、`DeferStop=true`、阈值 70%/70。
2. 进存档 → 打掉任意小 BOSS 或 BOSS → 进下一层。
3. 血量回满，继续推普通房：AIZO 不停止；此后整局都不再停止。

---

## 2. 取证

### 2.1 1.4.8 的关键一行（决定性证据）

判据日志（1.4.8 新增的常驻诊断）打出：

```
[rainylove][AIZO] 检测到小BOSS/BOSS → 战斗期间不停止播放，播完自动重播。
   （unit=PlayerAvatar [connId=0] type=Boss inBattle=False dist=0.0）
```

满足判据的单位是**玩家自己**：`type=Boss`、`dist=0.0`、`inBattle=False`（后者说明当时还没进战斗，
是「距离 ≤ 12」这条把它放进来的——但即使去掉距离条件，玩家只要在战斗里 `inBattle=True` 同样会命中）。

同一份日志里 `检测到` 只出现一次（L417，玩家出生即触发），`BOSS 战结束` **一次都没有**，
最后停播是 L2153 退出世界时 `StopAll` 干的。⇒ `Active` 从出生到退出连续为真。

### 2.2 1.4.7 的日志为何也一致

那次 `检测到` 出现在 L355、紧接着才是 `avatar=new`（L356），`BOSS 战结束` 只出现一次（L2032，
下一次载入存档、场上还没生成任何单位时）。而 L1256 的循环起播在代码上要求 `Active == true`。
⇒ 同样是「一出生就真、直到世界重建才假」。两次运行是同一个根因。

### 2.3 静态证据

- `PlayerAvatar : UnitAvatar`（`PlayerAvatar.cs:11`）。
- 全仓库没有任何 `monsterType = EMonsterType.Boss` 的赋值语句 —— 它是 **prefab 序列化字段**，
  所以玩家 prefab 上就是 `Boss`。
- 游戏自己在用 `monsterType` 前会排除玩家：`UI_EnemyViewer.cs:112`
  `if (!(obj is PlayerAvatar) && obj.monsterType == EMonsterType.Normal)`。

---

## 3. 因果链（四环，逐环有证据）

| 环 | 内容 | 证据 |
|---|---|---|
| ① 触发 | 判据「场景里存在存活的 Miniboss/Boss 单位」把**玩家自己**算进去了（`PlayerAvatar` 的 `monsterType` 就是 `Boss`），因此玩家一出生就成立；次要来源：楼层预生成的未进房 BOSS 也算 | 日志 L417 `unit=PlayerAvatar…type=Boss dist=0.0` + `PlayerAvatar.cs:11` + `UI_EnemyViewer.cs:112` |
| ② 中间态 | 状态没有局/层边界重置：`LastSurvivorWatcher.LeaveRun()` 当时只调自己的 `ResetRunState()` | `LastSurvivorWatcher.cs:243` + 日志无第二次「检测到」 |
| ③ 状态变脏 | `hold = AizoBossHold && Active` 恒真 → `_holdLatch=true` → `Evaluate` 直接 `return`，`_sched` 每帧被清空 | 日志 L1202 + 代码 |
| ④ 症状 | 顺延节点停止、立即停止、`TickScheduledStop` 三条路径全在 `return` 之后，不可达；血量回满/打小怪都不再影响播放 | 代码 + 现象 |

---

## 4. 假设清单与判定

| # | 假设 | 判定 | 依据 |
|---|---|---|---|
| H1 | **玩家自己的 `PlayerAvatar`（`monsterType=Boss`）满足判据** | **成立（真因）** | L417 `unit=PlayerAvatar…type=Boss dist=0.0`；`PlayerAvatar : UnitAvatar`；游戏自身 `UI_EnemyViewer` 也这么排除 |
| H2 | 未进房间、预生成在楼上的 BOSS 也算 | 成立（次要来源） | 判据与「玩家是否在打」无关 |
| H3 | 保持态没有楼/局边界重置 | 成立（放大器） | `LeaveRun` 未调 `Entry.Aizo.ResetRunState()` |
| H4 | 本地 avatar 消失时 `_holdLatch` 不清 | 成立（放大器） | `Evaluate` 在 `FindSelf()==null` 时提前 return |
| H5 | 循环播放无「自燃」结束（`_endTime=0`） | 不构成成因 | 停止逻辑整段不可达，与循环无关；循环是既定设计 |
| H6 | 用 `BossSpawner.IsBossBattleInProgress` 替代 | 排除 | 反编译：`battlePhase` 只在 `if (base.isServer)` 的 `Update()` 里写，客机恒 false |
| H7 | `EMonsterType` 枚举错位使所有单位都命中 | 排除（但结论碰巧相似） | `{Normal, Miniboss, Boss, Dummy}`，`Normal=0`；**真正的原因不是默认值，而是玩家 prefab 显式写了 Boss** |
| H8 | 音频层 `StopAuto` 对循环播放无效 | 排除 | 1.4.7 日志 L1986 `系统音频已中断` |
| H9 | 血量字段读错导致「low 恒真」 | 排除 | 1.4.7 日志 L1258 `cur=67.51 max=105 pct=64` 正常 |

---

## 5. 修复

### 5.1 判据（1.4.9 终版）

```
遍历场景里所有 UnitAvatar：
  u == null                                   → 跳过
  u is PlayerAvatar                           → 跳过   ← 1.4.9 的关键一行
  u.NetworkLeader is PlayerAvatar             → 跳过（玩家随从/召唤物，站在玩家这边的）
  monsterType ∉ {Miniboss, Boss}              → 跳过
  u.IsDead                                    → 跳过
  IsBossEngaged = inBattle || sqrDistance ≤ 12²   → 命中
且必须先有本地玩家（PlayerLocator.GetLocal()），否则一律 false。
```

- `inBattle` = `UnitAvatar.IsInBattle`（SyncVar）。`UnitAI_NewBasic.OnFoundTarget` 在 AI 拿到敌对目标时
  调 `Avatar.StartBattle()`；`UnitAI_BossBasic : UnitAI_NewBasic` **没有**重写
  `OnFoundTarget/OnLostTarget`，所以 BOSS 单位也有这个标志，且客户端可见
  （`BossSpawner.IsBossBattleInProgress` 是房主专属，不能用）。
- `12` 抄自游戏：`UnitAI_NewBasic` 用「距离 > 12 = 目标已换房间、放弃追击」，12 就是它的房间半径。
  只用来补「BOSS 已刷在房里、AI 还没选目标」的进门窗口。
- 必须有本地玩家 → 载入存档的窗口期（第一个 `PlayerAvatar` 出现之前）不再误判。

### 5.2 边界重置

- `LastSurvivorWatcher.LeaveRun()` 追加 `Entry.Aizo?.ResetRunState()`（离开地下城 / 场上无玩家这两条
  不换场景的路径也要清）。
- `AizoWatcher.Evaluate()` 在 `FindSelf()==null` 时清 `_holdLatch/_holdLogged`。

### 5.3 常驻诊断（不是临时探针）

「检测到」日志带 `unit=名字 type=类型 inBattle=真假 dist=距离`。这次正是它一行定案的。

---

## 6. 验证协议（一次运行即可判读）

| 观察点 | 期望 |
|---|---|
| 载入存档、血量满、未进 BOSS 房 | **不应**出现「检测到小BOSS/BOSS」 |
| 走进 BOSS/小 BOSS 房 | 出现「检测到…（unit=具体单位名 type=Boss/Miniboss inBattle=True dist=…）」；`unit` 必须**不是** `PlayerAvatar` |
| 残血 → 起播 | `自动触发 · 循环` + `BOSS 战中 → 不停止播放（循环中…）` |
| BOSS 死亡 | 1.5s 后 `BOSS 战结束（场上已无正在交战的 BOSS）`，紧接 `已退出 BOSS 战保持 → 恢复常规停止逻辑` |
| 之后血量回满 | `DeferStop` 开：安排到下一节点并到点停；关：`立即停止播放` |
| 进下一层、推普通房 | **不应**再出现「BOSS 战中 → 不停止播放」 |

判读铁律：有「检测到」就必须有「BOSS 战结束」+「已退出 BOSS 战保持」，两段成对。
若 `unit=` 又指向某个奇怪的单位，那行字就是下一轮的定位依据。

---

## 7. 回归测试

| 套件 | 结果 | 覆盖 |
|---|---|---|
| `D:/tmp/bossfight` | **69 passed** | 交战判据真值表；**玩家自身回归（M 组）**：玩家 `type=Boss dist=0` 不算、玩家在战斗中也不算、队友的 PlayerAvatar 不算、玩家随从不算、真 BOSS 仍命中；1.4.7 回归（未进房 40 格 / 隔壁房 13 格 不算、进门窗口 11.9 格 算）；尸体/木桩/普通怪过滤；无本地玩家一律不算；1.5s 滞后；波次抖动；0.5s 节流；`Reset` 可打断；两个注入缝 |
| `D:/tmp/aizohold` | 36 passed | 保持状态机十一条路径 |
| `D:/tmp/rlworld` | 35 passed | 循环管道 / 位置 / 节流 |
| `D:/tmp/cfgtest` | 26 passed | 配置路径与迁移 |
| `D:/tmp/gtest` | 19 passed | 雨爱触发主流程 |

> 1.4.8 为何没测出来：测试桩里的 `PlayerAvatar` 是个独立类、不在 `UnitAvatar` 扫描列表里，
> 恰好绕开了唯一的真凶。1.4.9 把桩改成 `PlayerAvatar : UnitAvatar` 以镜像真实继承，并补上 M 组。

---

## 8. 本轮为什么不需要再加探针

1.4.8 已经把「满足判据的单位是谁」写进了常驻日志，用户这次的一次正常游玩就把它打印出来了
（`unit=PlayerAvatar…type=Boss`），静态证据（`PlayerAvatar : UnitAvatar`、prefab 序列化值、
游戏自身的 `UI_EnemyViewer` 排除写法）与之完全吻合，因果链闭合。再加一轮探针不会提高信息量。
