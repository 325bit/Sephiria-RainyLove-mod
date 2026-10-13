using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// F7 呼出的配置界面（IMGUI），分为三个子项：RainyLove / AIZO / 主设置。
    ///
    /// 版式参考 SephiriaEnhancements 的 SettingsUi：暗色面板 + 自绘滑块 + 打开时接管光标并屏蔽角色输入。
    /// 所有控件直接读写 BepInEx 的 ConfigEntry，与 .cfg 文件天然同步；关闭窗口时统一 Config.Save() 落盘。
    /// </summary>
    public class ConfigUi : MonoBehaviour
    {
        private const float WindowWidth = 620f;
        private const float WindowHeight = 500f;
        private const int WindowId = 918243;

        private static readonly Color UiBackground = new Color(0.035f, 0.043f, 0.063f, 1f);
        private static readonly Color UiSurface = new Color(0.075f, 0.088f, 0.12f, 1f);
        private static readonly Color UiHover = new Color(0.10f, 0.12f, 0.16f, 1f);
        private static readonly Color UiBorder = new Color(0.16f, 0.19f, 0.25f, 1f);
        private static readonly Color UiText = new Color(0.93f, 0.95f, 0.98f, 1f);
        private static readonly Color UiMuted = new Color(0.58f, 0.63f, 0.72f, 1f);
        private static readonly Color UiAccent = new Color(0.29f, 0.64f, 1f, 1f);
        private static readonly Color UiDanger = new Color(0.80f, 0.31f, 0.31f, 1f);
        private static readonly Color UiOk = new Color(0.30f, 0.72f, 0.45f, 1f);

        private enum Tab { RainyLove, Aizo, Main }
        private Tab _tab = Tab.RainyLove;

        private GUIStyle _title, _body, _small, _smallRight, _okRight, _valueRight, _button, _track, _thumb, _window;
        private Rect _windowRect = new Rect(0f, 0f, WindowWidth, WindowHeight);
        private bool _visible;
        private bool _center = true;

        private bool _cursorOverridden;
        private bool _savedCursorVisible;
        private CursorLockMode _savedLockMode;
        private PlayerInputController _blockedInput;
        private bool _savedBlockAvatarInput;

        private void Update()
        {
            if (InputBridge.Available && InputBridge.WasKeyPressedThisFrame(Plugin.MenuKey.Value))
                SetVisible(!_visible);

            if (_visible)
            {
                OverrideCursor();
                BlockGameInput();
            }

            InputBridge.TickSelfCheck(Plugin.MenuKey.Value, Plugin.Log, Time.unscaledDeltaTime);
        }

        private void OnDestroy()
        {
            RestoreGameInput();
            RestoreCursor();
        }

        private void SetVisible(bool value)
        {
            if (_visible == value) return;
            _visible = value;
            if (value)
            {
                _center = true;
                OverrideCursor();
                BlockGameInput();
                Plugin.Log.LogMessage("[rainylove] 配置界面已打开（" + Plugin.MenuKey.Value.ToUpperInvariant() + " 关闭）。");
            }
            else
            {
                if (Plugin.Instance != null) Plugin.Instance.Config.Save();
                RestoreGameInput();
                RestoreCursor();
                Plugin.Log.LogMessage("[rainylove] 配置界面已关闭，设置已写入 cfg。");
            }
        }

        // ===================== 输入 / 光标接管 =====================

        private void BlockGameInput()
        {
            PlayerInputController current = PlayerInputController.Instance;
            if (current == null) return;
            if (_blockedInput != current)
            {
                RestoreGameInput();
                _blockedInput = current;
                _savedBlockAvatarInput = current.BlockAvatarInput;
            }
            current.BlockAvatarInput = true;
        }

        private void RestoreGameInput()
        {
            if (_blockedInput == null) return;
            _blockedInput.BlockAvatarInput = _savedBlockAvatarInput;
            _blockedInput = null;
        }

        private void OverrideCursor()
        {
            if (!_cursorOverridden)
            {
                _savedCursorVisible = Cursor.visible;
                _savedLockMode = Cursor.lockState;
                _cursorOverridden = true;
            }
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        private void RestoreCursor()
        {
            if (!_cursorOverridden) return;
            Cursor.visible = _savedCursorVisible;
            Cursor.lockState = _savedLockMode;
            _cursorOverridden = false;
        }

        // ===================== 绘制 =====================

        private void OnGUI()
        {
            if (!_visible) return;

            EnsureStyles();
            Matrix4x4 oldMatrix = GUI.matrix;
            Color oldColor = GUI.color;
            bool oldEnabled = GUI.enabled;
            try
            {
                GUI.color = Color.white;
                GUI.enabled = true;

                Fill(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.52f));

                float scale = Mathf.Max(0.1f, Mathf.Min(1f,
                    Mathf.Min((Screen.width - 32f) / WindowWidth, (Screen.height - 32f) / WindowHeight)));
                GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

                float width = Screen.width / scale;
                float height = Screen.height / scale;
                if (_center)
                {
                    _windowRect = new Rect((width - WindowWidth) * 0.5f, (height - WindowHeight) * 0.5f,
                        WindowWidth, WindowHeight);
                    _center = false;
                }

                _windowRect = GUI.Window(WindowId, _windowRect, DrawWindow, GUIContent.none, _window);
                _windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Mathf.Max(0f, width - WindowWidth));
                _windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Mathf.Max(0f, height - WindowHeight));
            }
            finally
            {
                GUI.matrix = oldMatrix;
                GUI.color = oldColor;
                GUI.enabled = oldEnabled;
            }
        }

        private void DrawWindow(int windowId)
        {
            Fill(new Rect(0f, 0f, WindowWidth, WindowHeight), UiBackground);

            GUI.Label(new Rect(24f, 13f, 500f, 28f), "rainylove · 配置", _title);
            GUI.Label(new Rect(24f, 41f, 500f, 18f),
                "RainyLove / AIZO / 主设置 · " + Plugin.MenuKey.Value.ToUpperInvariant() + " 呼出 / 关闭", _small);
            if (Button(new Rect(WindowWidth - 54f, 15f, 30f, 30f), "×")) SetVisible(false);
            Fill(new Rect(0f, 65f, WindowWidth, 1f), UiBorder);

            // ---- 标签页 ----
            DrawTab(new Rect(24f, 76f, 184f, 28f), "RainyLove", _tab == Tab.RainyLove);
            DrawTab(new Rect(218f, 76f, 184f, 28f), "AIZO", _tab == Tab.Aizo);
            DrawTab(new Rect(412f, 76f, 184f, 28f), "主设置", _tab == Tab.Main);
            Fill(new Rect(0f, 110f, WindowWidth, 1f), UiBorder);

            if (_tab == Tab.RainyLove) DrawRainyLove();
            else if (_tab == Tab.Aizo) DrawAizo();
            else DrawMain();

            GUI.DragWindow(new Rect(0f, 0f, WindowWidth - 64f, 65f));
        }

        private void DrawTab(Rect rect, string label, bool selected)
        {
            bool hover = rect.Contains(Event.current.mousePosition);
            Color color = selected ? UiAccent : (hover ? UiHover : UiSurface);
            Fill(rect, color);
            if (GUI.Button(rect, label, selected ? _button : _small)) _tab = TabFromLabel(label);
        }

        private static Tab TabFromLabel(string label)
        {
            if (label == "AIZO") return Tab.Aizo;
            if (label == "主设置") return Tab.Main;
            return Tab.RainyLove;
        }

        // ---- RainyLove 子页（无淡入，启用移到主设置）----
        private void DrawRainyLove()
        {
            SliderRow(118f, 144f, "播放音量", Percent(Plugin.Volume.Value),
                Plugin.Volume.Value, 0f, 1f, 0.05f, v => Plugin.Volume.Value = Mathf.Round(v * 100f) / 100f);

            GUI.Label(new Rect(24f, 190f, WindowWidth - 48f, 22f),
                "当不再是最后一名存活者时在节点处自动停止播放", _small);

            Fill(new Rect(24f, 222f, WindowWidth - 48f, 1f), UiBorder);

            LastSurvivorWatcher rl = Plugin.Watcher;
            DrawStatus(248f, rl != null && rl.IsAudioReady, rl != null && rl.IsPlaying, "RainyLove");

            bool rlOn = Plugin.Enabled.Value;
            bool rlDisabled = !rlOn;
            GUI.enabled = !rlDisabled;
            if (Button(new Rect(24f, 286f, 150f, 36f), "▶  测试播放", false, UiOk) && rl != null) rl.TestPlay();
            if (Button(new Rect(186f, 286f, 150f, 36f), "■  停止播放", false, UiDanger) && rl != null) rl.StopFromUi();
            GUI.enabled = true;

            if (Button(new Rect(WindowWidth - 144f, 286f, 120f, 36f), "完成", true)) SetVisible(false);

            GUI.Label(new Rect(24f, 336f, WindowWidth - 48f, 24f),
                "RainyLove 仅剩最后一名存活者时播放；条件解除后顺延到时间轴节点再停止。启用开关在主设置里。", _small);
        }

        // ---- AIZO 子页 ----
        private void DrawAizo()
        {
            SliderRow(118f, 144f, "播放音量", Percent(Plugin.AizoVolume.Value),
                Plugin.AizoVolume.Value, 0f, 1f, 0.05f, v => Plugin.AizoVolume.Value = Mathf.Round(v * 100f) / 100f);

            SliderRow(180f, 206f, "开始播放的血量阈值（%）", Mathf.RoundToInt(Plugin.AizoHealthPercent.Value) + "%",
                Plugin.AizoHealthPercent.Value, 0f, 99f, 1f, v => Plugin.AizoHealthPercent.Value = Mathf.Round(v));

            SliderRow(242f, 268f, "开始播放的血量固定值", Mathf.RoundToInt(Plugin.AizoHealthValue.Value).ToString(),
                Plugin.AizoHealthValue.Value, 0f, 100f, 1f, v => Plugin.AizoHealthValue.Value = Mathf.Round(v));

            GUI.Label(new Rect(24f, 308f, WindowWidth - 48f, 22f),
                "当血量回升到阈值以上时在节点处自动停止播放", _small);

            Fill(new Rect(24f, 340f, WindowWidth - 48f, 1f), UiBorder);

            AizoWatcher aizo = Plugin.Aizo;
            DrawStatus(366f, aizo != null && aizo.IsAudioReady, aizo != null && aizo.IsPlaying, "AIZO");

            bool aizoOn = Plugin.AizoEnabled.Value;
            bool disabled = !aizoOn;
            GUI.enabled = !disabled;
            if (Button(new Rect(24f, 404f, 150f, 36f), "▶  音频播放测试", false, UiOk) && aizo != null) aizo.TestPlay();
            if (Button(new Rect(186f, 404f, 150f, 36f), "■  音频播放停止", false, UiDanger) && aizo != null) aizo.StopFromUi();
            GUI.enabled = true;

            if (Button(new Rect(WindowWidth - 144f, 404f, 120f, 36f), "完成", true)) SetVisible(false);
        }

        // ---- 主设置子页 ----
        private void DrawMain()
        {
            GUI.Label(new Rect(24f, 124f, 400f, 32f), "启用 RainyLove（最后存活者播放）", _body);
            if (Toggle(new Rect(WindowWidth - 136f, 124f, 112f, 32f), Plugin.Enabled.Value)) Plugin.Enabled.Value = !Plugin.Enabled.Value;

            GUI.Label(new Rect(24f, 178f, 400f, 32f), "启用 AIZO（血量过低播放）", _body);
            if (Toggle(new Rect(WindowWidth - 136f, 178f, 112f, 32f), Plugin.AizoEnabled.Value)) Plugin.AizoEnabled.Value = !Plugin.AizoEnabled.Value;

            Fill(new Rect(24f, 224f, WindowWidth - 48f, 1f), UiBorder);

            GUI.Label(new Rect(24f, 244f, 500f, 26f), "音频播放优先级（两者同时想播时）", _body);
            PrioritySelector(278f);

            // 状态：两个来源是否正在播放
            bool rlPlaying = Plugin.Watcher != null && Plugin.Watcher.IsPlaying;
            bool aizoPlaying = Plugin.Aizo != null && Plugin.Aizo.IsPlaying;
            GUI.Label(new Rect(24f, 344f, 320f, 24f),
                "RainyLove：" + (rlPlaying ? "● 播放中" : "○ 未播放"), rlPlaying ? _okRight : _small);
            GUI.Label(new Rect(24f, 372f, 320f, 24f),
                "AIZO：" + (aizoPlaying ? "● 播放中" : "○ 未播放"), aizoPlaying ? _okRight : _small);

            if (Button(new Rect(WindowWidth - 144f, 412f, 120f, 36f), "完成", true)) SetVisible(false);
        }

        private void PrioritySelector(float y)
        {
            AudioPriority cur = Plugin.AudioPriority.Value;
            string[] labels = { "RainyLove优先", "AIZO优先", "先来后到" };
            AudioPriority[] vals = { AudioPriority.RainyLoveFirst, AudioPriority.AizoFirst, AudioPriority.FirstCome };
            float bw = 184f, gap = 10f, x = 24f;
            for (int i = 0; i < 3; i++)
            {
                Rect r = new Rect(x + i * (bw + gap), y, bw, 36f);
                bool sel = cur == vals[i];
                if (Button(r, labels[i], sel, sel ? UiAccent : UiSurface)) Plugin.AudioPriority.Value = vals[i];
            }
        }

        private void DrawStatus(float y, bool ready, bool playing, string name)
        {
            GUI.Label(new Rect(24f, y, 360f, 24f),
                ready ? (name + "：音频已就绪") : (name + "：未渲染（首次/改参数后自动渲染）"), _small);
            GUI.Label(new Rect(WindowWidth - 224f, y, 200f, 24f),
                playing ? "● 正在播放" : "○ 未播放", playing ? _okRight : _smallRight);
        }

        // ===================== 控件 =====================

        /// <summary>一行“标题 + 当前值 + [−] 滑块 [+]”。</summary>
        private void SliderRow(float labelY, float rowY, string label, string valueText,
            float current, float min, float max, float step, System.Action<float> apply)
        {
            GUI.Label(new Rect(24f, labelY, 380f, 26f), label, _body);
            GUI.Label(new Rect(WindowWidth - 224f, labelY, 200f, 26f), valueText, _valueRight);

            if (Button(new Rect(24f, rowY, 32f, 28f), "−")) apply(Mathf.Clamp(current - step, min, max));

            Rect sliderRect = new Rect(64f, rowY + 2f, WindowWidth - 64f - 64f, 24f);
            float sliderValue = Slider(sliderRect, current, min, max);
            if (!Mathf.Approximately(sliderValue, current)) apply(sliderValue);

            if (Button(new Rect(WindowWidth - 56f, rowY, 32f, 28f), "+")) apply(Mathf.Clamp(current + step, min, max));
        }

        private float Slider(Rect rect, float value, float min, float max)
        {
            float centerY = rect.y + rect.height * 0.5f - 2f;
            float trackWidth = rect.width - 16f;
            Fill(new Rect(rect.x + 8f, centerY, trackWidth, 4f), UiBorder);
            float fraction = max > min ? Mathf.Clamp01((value - min) / (max - min)) : 0f;
            Fill(new Rect(rect.x + 8f, centerY, trackWidth * fraction, 4f), UiAccent);

            Color previous = GUI.color;
            GUI.color = UiAccent;
            float result = GUI.HorizontalSlider(rect, value, min, max, _track, _thumb);
            GUI.color = previous;
            return result;
        }

        private bool Toggle(Rect rect, bool value)
        {
            return Button(rect, value ? "已启用" : "已关闭", value, value ? UiAccent : UiSurface);
        }

        private bool Button(Rect rect, string text, bool selected = false, Color? tint = null)
        {
            bool hover = GUI.enabled && rect.Contains(Event.current.mousePosition);
            Color color = selected ? UiAccent : (hover ? UiHover : (tint ?? UiSurface));
            Fill(rect, color);
            return GUI.Button(rect, text, _button);
        }

        // ===================== 样式与基础绘制 =====================

        private void EnsureStyles()
        {
            if (_title != null) return;
            _title = LabelStyle(19, UiText, FontStyle.Bold, TextAnchor.MiddleLeft);
            _body = LabelStyle(14, UiText, FontStyle.Normal, TextAnchor.MiddleLeft);
            _small = LabelStyle(12, UiMuted, FontStyle.Normal, TextAnchor.MiddleLeft);
            _smallRight = LabelStyle(12, UiMuted, FontStyle.Normal, TextAnchor.MiddleRight);
            _okRight = LabelStyle(12, UiOk, FontStyle.Bold, TextAnchor.MiddleRight);
            _valueRight = LabelStyle(15, UiAccent, FontStyle.Bold, TextAnchor.MiddleRight);
            _button = LabelStyle(13, UiText, FontStyle.Bold, TextAnchor.MiddleCenter);
            _button.hover.textColor = UiText;
            _button.active.textColor = UiText;
            _button.focused.textColor = UiText;

            _window = new GUIStyle();

            _track = new GUIStyle();
            _track.fixedHeight = 24f;

            _thumb = new GUIStyle();
            _thumb.fixedWidth = 16f;
            _thumb.fixedHeight = 24f;
            _thumb.normal.background = Texture2D.whiteTexture;
            _thumb.hover.background = Texture2D.whiteTexture;
            _thumb.active.background = Texture2D.whiteTexture;
            _thumb.focused.background = Texture2D.whiteTexture;
        }

        private static GUIStyle LabelStyle(int size, Color color, FontStyle weight, TextAnchor anchor)
        {
            GUIStyle style = new GUIStyle();
            style.fontSize = size;
            style.fontStyle = weight;
            style.alignment = anchor;
            style.normal.textColor = color;
            style.clipping = TextClipping.Clip;
            return style;
        }

        private static string Percent(float value)
        {
            return Mathf.RoundToInt(value * 100f) + "%";
        }

        private static void Fill(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = GUI.enabled ? color : new Color(color.r * 0.55f, color.g * 0.55f, color.b * 0.55f, color.a);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
        }
    }
}
