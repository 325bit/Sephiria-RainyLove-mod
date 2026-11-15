# Sephiria-RainyLove-AIZO-mod

残血时播放 AIZO；多人模式（场上 ≥2 人）中，当场上只剩最后一个存活玩家时播放雨爱。

- **进入存档后**按 **F7** 呼出配置界面（音量、开关、触发阈值、优先级等）。
  配置保存在 `%USERPROFILE%\AppData\LocalLow\TEAMHORAY\Sephiria\rainylove\config.json`
  （和其它赛菲莉娅 mod 同一个目录；旧版本放在 mod 文件夹里的 config.json 会在首次进存档时自动迁移过去）。
- 纯客户端表现层 mod：只读游戏状态，不改任何服务端数据，不产生需要同步的内容。
    
  已声明 `multiplayer.installation: local_only`，**其他参与者无需安装本 mod**。
- 音频只支持 **MP3**（`Asset/rainylove.mp3`、`Asset/AIZO.mp3`，随包附带）。WAV 播放路径已移除，
  放同名 `.wav` 不会生效；想换曲子就用同名 `.mp3` 覆盖。
