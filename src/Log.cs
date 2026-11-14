using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// Thin wrapper over UnityEngine.Debug, replacing the BepInEx ManualLogSource used by the old plugin build.
    /// Messages are expected to carry their own "[rainylove]" prefix (kept from the original code for consistency).
    /// </summary>
    internal static class Log
    {
        internal static void Info(string s) => Debug.Log(s);
        internal static void Warning(string s) => Debug.LogWarning(s);
        internal static void Message(string s) => Debug.Log(s);
    }
}
