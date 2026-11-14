using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace RainyLove
{
    /// <summary>
    /// Keyboard key query bridge.
    ///
    /// This game's Player Settings switch Input Handling to the Input System package; calling the legacy
    /// UnityEngine.Input.GetKeyDown then throws InvalidOperationException. Because the exception occurs inside Update,
    /// it also aborts the rest of that frame's survivor scan, so Input System is called via reflection instead.
    ///
    /// Three easy-to-miss details (all verified against Unity.InputSystem.dll with Mono.Cecil):
    ///   1. Keyboard has no generated key properties such as f7. It has only 9 non-key properties;
    ///      keys always go through the indexer: Keyboard.current[Key.F7]. In reflection the indexer is named 'Item'.
    ///   2. wasPressedThisFrame is defined on ButtonControl, not on Keyboard,
    ///      so it must be found by walking up from the key control's runtime type (KeyControl -> ButtonControl).
    ///      The indexer returns a KeyControl (derived from ButtonControl), so the property is reachable.
    ///   3. The last step of the reflection chain depends on runtime state: Keyboard.current is usually still null during Awake
    ///      (the Input runtime creates devices only after the first frame). That must be reported as 'device not ready', not 'resolution failed',
    ///      otherwise the startup log gets a misleading False.
    ///
    /// All calls swallow exceptions: if the input system breaks, only F7 stops working; core survivor detection and playback are unaffected.
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

        /// <summary>Key name -> Key enum value. Mapped once; the hot path (every frame) only does a Dictionary lookup.</summary>
        private static readonly ConcurrentDictionary<string, object> KeyValueCache
            = new ConcurrentDictionary<string, object>();

        /// <summary>Key control type -> pressed-state reader, avoiding a base-class walk every frame.</summary>
        private static readonly ConcurrentDictionary<Type, Func<object, object>> PressedReaderCache
            = new ConcurrentDictionary<Type, Func<object, object>>();

        private static bool _resolveTried;
        private static bool _available;
        private static string _structureError;

        // ---- Deferred self-check state ----
        private static bool _selfCheckScheduled;
        private static bool _selfCheckLogged;
        private static float _selfCheckElapsed;

        /// <summary>Whether the API layer is usable (independent of device availability).</summary>
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
                // The official Workshop runtime resolves assemblies differently from BepInEx.
                // Prefer the already-loaded Unity.InputSystem (the Input System is initialized by the game at boot),
                // and only fall back to Assembly.Load when it is not present in the current AppDomain.
                Assembly assembly = null;
                foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(loaded.GetName().Name, "Unity.InputSystem", StringComparison.Ordinal))
                    {
                        assembly = loaded;
                        break;
                    }
                }
                if (assembly == null) assembly = Assembly.Load("Unity.InputSystem");
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

        /// <summary>Find the Key enum indexer by parameter type (Keyboard usually has both Key and string overloads).</summary>
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

        /// <summary>Map an external key name (e.g. 'f7') to an InputSystem.Key enum value. Returns false if the key is absent from the enum.</summary>
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

                KeyValueCache[keyName] = null;   // Remember a 'mapping failed' result so it is not retried every frame
                Log.Warning("[rainylove] 键名 " + keyName
                                      + " 在 InputSystem.Key 里不存在，手动重播不生效（自动触发不受影响）。");
                return false;
            }
            catch (Exception e)
            {
                KeyValueCache[keyName] = null;
                Log.Warning("[rainylove] 键名 " + keyName + " 解析失败：" + e.Message);
                return false;
            }
        }

        /// <summary>Whether the key name was successfully mapped to the Key enum (does not include device availability).</summary>
        internal static bool IsKeyMapped(string keyName)
        {
            if (!MapKey(keyName)) return false;
            object value;
            return KeyValueCache.TryGetValue(keyName, out value) && value != null;
        }

        /// <summary>
        /// Runtime self-check: whether the key's pressed state can actually be read right now.
        /// A null reason means everything is fine; otherwise it holds the specific failure reason (for log diagnostics).
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

        /// <summary>Query whether a key was pressed this frame. Any exception returns false.</summary>
        internal static bool WasKeyPressedThisFrame(string keyName)
        {
            if (!Available) return false;

            try
            {
                if (!MapKey(keyName)) return false;

                object keyValue;
                if (!KeyValueCache.TryGetValue(keyName, out keyValue) || keyValue == null) return false;

                object keyboard = _currentProperty.GetValue(null, null);
                if (keyboard == null) return false;   // No keyboard device / window not focused

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
        /// Get the pressed-state reader for a key control (cached by control type): prefer a property, then a get_xxx() method.
        /// Re-resolved at every checkpoint so a new Keyboard.current device instance is picked up.
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
        /// Called every frame. Runs one self-check about 2 seconds after startup and logs a single line:
        /// During Awake Keyboard.current is usually still null, so a check then would always fail; wait for the runtime to be ready.
        /// The self-check logs once only, to avoid spam.
        /// </summary>
        internal static void TickSelfCheck(string keyName, float deltaTime)
        {
            if (_selfCheckLogged) return;

            // Report an unusable API layer explicitly instead of silently returning: otherwise a broken
            // Input System resolution looks identical to 'the mod never loaded' in the log.
            if (!Available)
            {
                _selfCheckLogged = true;
                Log.Warning("[rainylove] 输入自检失败：" + _structureError
                               + "。F7 配置界面无法呼出，但存活检测与自动播放不受影响。");
                return;
            }

            if (!_selfCheckScheduled)
            {
                _selfCheckScheduled = true;
                _selfCheckElapsed = 0f;
                return;             // Do not start the timer on the first frame, giving the Input runtime time to initialize
            }

            _selfCheckElapsed += deltaTime;
            if (_selfCheckElapsed < 2f) return;

            _selfCheckLogged = true;

            string reason;
            if (TryProbe(keyName, out reason))
            {
                Log.Info("[rainylove] 输入自检通过：F7 可正常读取（Input System 运行正常）。");
            }
            else
            {
                Log.Warning("[rainylove] 输入自检失败：" + reason
                               + "。F7 手动重播不可用，但存活检测与自动播放不受影响。");
            }
        }
    }
}
