# rainylove — 最后一人存活

多人模式（场上 ≥2 人）中，**当场上只剩最后一个存活玩家时，播放 `rainylove`**。

游戏：Sephiria（TEAM HORAY，Unity 6 / BepInEx 5.4）
类型：纯客户端表现层 mod，只读游戏状态，不改任何服务端数据，联机安全。

---

## 功能逻辑

按配置项扫描场上存活玩家数：

1. 取场景中所有 `PlayerAvatar`，只统计 `isInDungeon != 0`（同一地牢内）的玩家。
2. 存活人数 < 2（单人局 / 还没进地牢）→ 不触发。
3. 存活人数从 ≥2 降到 **1** → 触发一次播放。
4. 存活人数回升到 ≥2，视为新的一轮，重置状态（下一局可再次触发）。
5. 按 **F7** 可手动重播（走正常判定），方便自测。

## 单机测试音频播放（不用开联机）

在**主菜单**按 **F6**（`TestPlayKey`，可在配置里改）：
绕过所有存活判定，直接完整走一遍"ffmpeg 解码 → AudioClip.Create → AudioSource 播放"链路。

预期结果：

| 现象 | 结论 |
|---|---|
| 音乐响起 | 音频链路完全正常 |
| 日志出现 `[rainylove] 播放失败：…` | 反射/解码问题，把该行发我 |
| 按了没反应、也没日志 | 输入桥问题，看启动日志的 `输入系统可用` 与 `输入自检` 两行 |

## 安装

将压缩文件里的rainylove文件夹拖到 Sephiria游戏根目录\BepInEx\plugins文件夹里。启动游戏之后主菜单界面按F6能够播放说明安装成功。F7停止音频播放。

## 配置

启动游戏生成 `BepInEx\config\com.rainylove.lastsurvivor.cfg` 后可修改：

| 键 | 默认 | 说明 |
|---|---|---|
| `Enabled` | true | 总开关 |
| `Volume` | 1 | 播放音量 0~1 |
| `AudioFilePath` | 空 | 音频目录或文件路径，留空用默认位置 |
| `ScanIntervalSec` | 0.25 | 存活人数扫描间隔（秒） |
| `OnlyWhenSelfIsLast` | false | 仅当自己就是最后存活者才播放 |
| `EnableTestKey` | true | F7 手动重播 |
| `TestPlayKey` | f8 | 强制播放测试键（主菜单可用，绕过判定） |
| `ResampleHz` | 44100 | 解码采样率；44100 约 70MB 内存，22050 约减半 |
