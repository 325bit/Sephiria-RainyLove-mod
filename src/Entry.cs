using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// Official Workshop DLL module entry point (replaces the old BepInEx Plugin).
    ///
    /// Inherits WorkshopModBase. OnModLoaded runs when the world scene loads (NOT on the title screen); that is
    /// where the runtime objects are created. Context / metadata are valid from OnModLoaded onward (null in the ctor).
    /// </summary>
    public class Entry : WorkshopModBase
    {
        public static LastSurvivorWatcher Watcher;
        public static AizoWatcher Aizo;

        private static GameObject _root;

        // Called once per world entry. Subscribe to events only here (this mod does not subscribe to any).
        protected override void OnModLoaded()
        {
            ModConfig.PackageRoot = Context.PackageRoot;
            ModConfig.Load();

            _root = new GameObject("RainyLove.Root");
            Object.DontDestroyOnLoad(_root);
            Watcher = _root.AddComponent<LastSurvivorWatcher>();
            Aizo = _root.AddComponent<AizoWatcher>();
            _root.AddComponent<AudioChannel>();
            _root.AddComponent<ConfigUi>();

            Watcher.Initialize();
            Aizo.Initialize();

            Log.Info("[rainylove] v" + metadata.modVersion + " 已加载，RainyLove=" + ModConfig.Enabled
                     + " AIZO=" + ModConfig.AizoEnabled + " 优先级=" + ModConfig.AudioPriority);
            Log.Info("[rainylove] 呼出配置界面按键=" + ModConfig.MenuKey.ToUpperInvariant()
                     + "，输入系统可用=" + InputBridge.Available
                     + "，按键映射=" + InputBridge.IsKeyMapped(ModConfig.MenuKey));
        }

        // Called when leaving the world. Release resources and stop playback.
        protected override void OnModUnloaded()
        {
            AudioChannel.Instance?.StopAll();
            if (_root != null)
            {
                Object.Destroy(_root);
                _root = null;
            }
            Watcher = null;
            Aizo = null;
        }
    }
}
