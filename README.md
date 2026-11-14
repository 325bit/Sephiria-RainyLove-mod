# Sephiria-RainyLove-AIZO-mod

残血时播放 AIZO；多人模式（场上 ≥2 人）中，当场上只剩最后一个存活玩家时播放雨爱。

- **进入游戏世界后**按 **F7** 呼出配置界面（音量、开关、触发阈值、优先级等）。
- 纯客户端表现层 mod：只读游戏状态，不改任何服务端数据，不产生需要同步的内容。    
  已声明 `multiplayer.installation: local_only`，**其他参与者无需安装本 mod**。

## 项目结构
- `src/`：全部 C# 源码（`rainylove.csproj` 用 `src/**/*.cs` 显式声明编译范围）。
- `docs/MIGRATION-WORKSHOP.md`：BepInEx → 官方创意工坊迁移清单（英文）。
- `package/`：构建产物 + `metadata.json` + 预览图；`package-upload/`：不含 WAV 的上传专用干净包。
- `Asset/`（已 gitignore）：本地测试用的 WAV 与图片，**不进上传包**。

