using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace RainyLove
{
    /// <summary>
    /// 键盘按键查询桥。
    ///
    /// 本游戏的 Player Settings 把 Input Handling 切到了 Input System 包，此时调用旧的
    /// UnityEngine.Input.GetKeyDown 会抛 InvalidOperationException。由于异常发生在 Update 里，
    /// 会把同一帧后续的存活扫描一起打断，所以这里改成反射调用 Input System。
    ///
    /// 三个容易踩的点（均用 Mono.Cecil 核对过 Unity.InputSystem.dll 后确认）：
    ///   1. Keyboard 上没有生成的 f7 之类的按键属性。整个 Keyboard 只有 9 个非按键属性，
    ///      按键一律走索引器：Keyboard.current[Key.F7]。反射里索引器名为 "Item"。
    ///   2. wasPressedThisFrame 定义在 ButtonControl 上，不是 Keyboard 上，
    ///      必须按键控件的运行时类型（KeyControl -> ButtonControl）向上找。
    ///      索引器返回的是 KeyControl（派生自 ButtonControl），所以能读到。
    ///   3. 反射链的最后一步依赖运行时状态：Keyboard.current 在 Awake 阶段通常还是 null
    ///      （Input 运行时要等第一帧才建设备）。此时必须判定为"设备未就绪"而不是"解析失败"，
    ///      否则会在启动日志里产生一条误导性的 False。
    ///
    /// 全部调用都吞异常：输入系统有问题时只是 F7 失效，不影响核心的存活检测与播放。
    /// </summary>
    internal static class InputBridge
    {
        private const string KeyboardTypeName = "UnityEngine.InputSystem.Keyboard";
        private const string KeyEnumTypeName = "UnityEngine.InputSystem.Key";
        private const string IndexerName = "Item";
        private const string PressedStateName = "wasPressedThisFrame";

        private const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.Instance;
        private const BindingFlags DeclaredFlags = InstanceFlags | BindingFlags.DeclaredOnly;

        private static Type _keyboardType;
        private static Type _keyEnumType;
        private static PropertyInfo _currentProperty;
        private static PropertyInfo _indexerProperty;

        /// <summary>键名 -> Key 枚举值。只映射一次，热路径（每帧）只查 Dictionary。</summary>
        private static readonly ConcurrentDictionary<string, object> KeyValueCache
            = new ConcurrentDictionary<string, object>();

        /// <summary>按键控件类型 -> 按下状态的读取器，避免每帧在基类链上反复查找。</summary>
        private static readonly ConcurrentDictionary<Type, Func<object, object>> PressedReaderCache
            = new ConcurrentDictionary<Type, Func<object, object>>();

        private static bool _resolveTried;
        private static bool _available;
        private static string _structureError;

        // ---- 延迟自检状态 ----
        private static bool _selfCheckScheduled;
        private static bool _selfCheckLogged;
        private static float _selfCheckElapsed;

        /// <summary>API 层是否可用（不依赖设备是否在线）。</summary>
        internal static bool Available
        {
            get { EnsureResolved(); return _available; }
        }

        private static void EnsureResolved()
        {
            if (_resolveTried) return;
            _resolveTried = true;
            _structureError = "尚未初始化";

            try
            {
                Assembly assembly = Assembly.Load("Unity.InputSystem");
                if (assembly == null)
                {
                    _structureError = "未找到 Unity.InputSystem 程序集";
                    return;
                }

                _keyboardType = assembly.GetType(KeyboardTypeName, false);
                _keyEnumType = assembly.GetType(KeyEnumTypeName, false);
                if (_keyboardType == null || _keyEnumType == null)
                {
                    _structureError = "未找到 Keyboard / Key 类型";
                    return;
                }

                _currentProperty = _keyboardType.GetProperty("current", StaticFlags);
                if (_currentProperty == null)
                {
                    _structureError = "Keyboard 缺少静态 current 属性";
                    return;
                }

                _indexerProperty = FindKeyIndexer();
                if (_indexerProperty == null)
                {
                    _structureError = "Keyboard 缺少 Key 索引器";
                    return;
                }

                _available = true;
                _structureError = null;
            }
            catch (Exception e)
            {
                _structureError = "解析失败：" + e.Message;
            }
        }

        /// <summary>按参数类型找到 Key 枚举索引器（Keyboard 上通常同时存在 Key 和 string 两套）。</summary>
        private static PropertyInfo FindKeyIndexer()
        {
            foreach (PropertyInfo p in _keyboardType.GetProperties(InstanceFlags))
            {
                if (p.Name != IndexerName) continue;

                ParameterInfo[] indices = p.GetIndexParameters();
                if (indices.Length != 1) continue;
                if (indices[0].ParameterType == _keyEnumType) return p;
            }
            return null;
        }

        /// <summary>把外部键名（如 "f7"）映射成 InputSystem.Key 枚举值。返回 false 表示枚举里没有这个键。</summary>
        internal static bool MapKey(string keyName)
        {
            if (string.IsNullOrEmpty(keyName)) return false;
            if (!Available) return false;

            object existing;
            if (KeyValueCache.TryGetValue(keyName, out existing)) return existing != null;

            try
            {
                string pascalName = char.ToUpperInvariant(keyName[0]) + keyName.Substring(1).ToUpperInvariant();
                foreach (string candidate in new[] { pascalName, keyName })
                {
                    if (Enum.IsDefined(_keyEnumType, candidate))
                    {
                        KeyValueCache[keyName] = Enum.Parse(_keyEnumType, candidate);
                        return true;
                    }
                }

                KeyValueCache[keyName] = null;   // 记住"映射失败"，避免每帧重试
                Plugin.Log.LogWarning("[rainylove] 键名 " + keyName
                                      + " 在 InputSystem.Key 里不存在，手动重播不生效（自动触发不受影响）。");
                return false;
            }
            catch (Exception e)
            {
                KeyValueCache[keyName] = null;
                Plugin.Log.LogWarning("[rainylove] 键名 " + keyName + " 解析失败：" + e.Message);
                return false;
            }
        }

        /// <summary>键名是否已成功映射到 Key 枚举（不含设备是否在线）。</summary>
        internal static bool IsKeyMapped(string keyName)
        {
            if (!MapKey(keyName)) return false;
            object value;
            return KeyValueCache.TryGetValue(keyName, out value) && value != null;
        }

        /// <summary>
        /// 运行时自检：此刻能否真正读到该键的按下状态。
        /// reason 为 null 表示一切正常，否则为具体失败原因（用于日志诊断）。
        /// </summary>
        internal static bool TryProbe(string keyName, out string reason)
        {
            reason = null;
            if (!Available) { reason = _structureError; return false; }
            if (!MapKey(keyName)) { reason = "键名映射失败"; return false; }

            try
            {
                object keyboard = _currentProperty.GetValue(null, null);
                if (keyboard == null)
                {
                    reason = "键盘设备未就绪（Keyboard.current 为 null）";
                    return false;
                }

                object keyValue;
                if (!KeyValueCache.TryGetValue(keyName, out keyValue) || keyValue == null)
                {
                    reason = "键名映射失败";
                    return false;
                }

                object control = _indexerProperty.GetValue(keyboard, new[] { keyValue });
                if (control == null)
                {
                    reason = "按键控件解析为 null";
                    return false;
                }

                object pressed = ReadPressedState(control);
                if (!(pressed is bool))
                {
                    reason = "按下状态读取失败";
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                reason = "访问按键控件异常：" + e.Message;
                return false;
            }
        }

        /// <summary>查询某个键在本帧是否被按下。任何异常都返回 false。</summary>
        internal static bool WasKeyPressedThisFrame(string keyName)
        {
            if (!Available) return false;

            try
            {
                if (!MapKey(keyName)) return false;

                object keyValue;
                if (!KeyValueCache.TryGetValue(keyName, out keyValue) || keyValue == null) return false;

                object keyboard = _currentProperty.GetValue(null, null);
                if (keyboard == null) return false;   // 无键盘设备 / 窗口无焦点

                object control = _indexerProperty.GetValue(keyboard, new[] { keyValue });
                if (control == null) return false;

                object pressed = ReadPressedState(control);
                return pressed is bool && (bool)pressed;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 取按键控件的按下状态读取器（按控件类型缓存）：优先属性，其次 get_xxx() 方法。
        /// 每个检查点都会重新解析，这样 Keyboard.current 换成新的设备实例后也能跟上。
        /// </summary>
        private static object ReadPressedState(object control)
        {
            Func<object, object> reader;
            if (PressedReaderCache.TryGetValue(control.GetType(), out reader)) return reader(control);

            reader = BuildPressedReader(control.GetType());
            PressedReaderCache[control.GetType()] = reader;
            return reader(control);
        }

        private static Func<object, object> BuildPressedReader(Type controlType)
        {
            for (Type t = controlType; t != null; t = t.BaseType)
            {
                PropertyInfo p = t.GetProperty(PressedStateName, DeclaredFlags);
                if (p != null && p.PropertyType == typeof(bool))
                {
                    MethodInfo getter = p.GetGetMethod();
                    if (getter != null) return o => getter.Invoke(o, null);
                }
            }

            for (Type t = controlType; t != null; t = t.BaseType)
            {
                MethodInfo m = t.GetMethod("get_" + PressedStateName, DeclaredFlags);
                if (m != null && m.ReturnType == typeof(bool)) return o => m.Invoke(o, null);
            }

            return o => null;
        }

        /// <summary>
        /// 每帧调用。启动后约 2 秒做一次自检并打一条日志：
        /// Awake 阶段 Keyboard.current 通常还是 null，那时做判断必然失败，所以要等运行时就绪。
        /// 自检只打一次，不刷屏。
        /// </summary>
        internal static void TickSelfCheck(string keyName, BepInEx.Logging.ManualLogSource log, float deltaTime)
        {
            if (_selfCheckLogged || !Available) return;

            if (!_selfCheckScheduled)
            {
                _selfCheckScheduled = true;
                _selfCheckElapsed = 0f;
                return;             // 第一帧不计时，给 Input 运行时留出初始化时间
            }

            _selfCheckElapsed += deltaTime;
            if (_selfCheckElapsed < 2f) return;

            _selfCheckLogged = true;

            string reason;
            if (TryProbe(keyName, out reason))
            {
                log.LogInfo("[rainylove] 输入自检通过：F7 可正常读取（Input System 运行正常）。");
            }
            else
            {
                log.LogWarning("[rainylove] 输入自检失败：" + reason
                               + "。F7 手动重播不可用，但存活检测与自动播放不受影响。");
            }
        }
    }
}
