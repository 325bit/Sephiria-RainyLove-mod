using Mirror;
using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// Locates the local player's PlayerAvatar.
    ///
    /// Do NOT use NetworkBehaviour.authority to answer 'which avatar is mine' in this game. The shipped Mirror build
    /// resolves authority as follows (NetworkBehaviour:48-69):
    ///     host   + syncDirection == ServerToClient -> true for EVERY avatar
    ///     client + syncDirection == ServerToClient -> false for EVERY avatar
    /// hp / IsDead / isInDungeon are all [SyncVar] with the default direction, so on the host 'the first avatar in
    /// FindObjectsOfType order' wins (usually a teammate) and on a pure client nothing matches at all. That is
    /// exactly the reported 'AIZO plays on the host with a teammate's health' / 'AIZO never plays on a client'.
    ///
    /// Fallback order, most authoritative first:
    ///   1. NetworkClient.localPlayer        - Mirror's own entry point; valid for host and client alike
    ///   2. NetworkBehaviour.isLocalPlayer   - what the game itself uses (camera shake, UI): avatar.cs 3713
    ///   3. NetworkBehaviour.isOwned         - true only for the connection owning the object
    ///   4. the single avatar in the scene   - offline play, or multiplayer before other players have spawned
    /// Steps 2-4 all answer false for every avatar on a pure client when Mirror has not spawned the local player yet,
    /// hence step 4 rather than returning an arbitrary element.
    /// </summary>
    internal static class PlayerLocator
    {
        /// <param name="candidates">Avatar list the caller already fetched (avoids a second FindObjectsOfType).</param>
        public static PlayerAvatar GetLocal(PlayerAvatar[] candidates = null)
        {
            NetworkIdentity local = NetworkClient.localPlayer;
            if (local != null)
            {
                PlayerAvatar owned = local.GetComponent<PlayerAvatar>();
                if (owned != null) return owned;
            }

            PlayerAvatar[] list = candidates ?? Object.FindObjectsOfType<PlayerAvatar>(true);
            if (list == null || list.Length == 0) return null;

            foreach (PlayerAvatar a in list)
            {
                if (a != null && a.isLocalPlayer) return a;
            }
            foreach (PlayerAvatar a in list)
            {
                if (a != null && a.isOwned) return a;
            }

            return list.Length == 1 ? list[0] : null;
        }
    }
}
