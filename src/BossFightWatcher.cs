using UnityEngine;

namespace RainyLove
{
    /// <summary>
    /// Detects whether a mini-boss or boss fight is currently going on, for the 'keep playing through a boss
    /// fight' option that AIZO and RainyLove both offer (ModConfig.AizoBossHold / RainyLoveBossHold).
    ///
    /// The signal is deliberately NOT BossSpawner.IsBossBattleInProgress: that property reads a battlePhase field
    /// which is only ever written inside BossSpawner.Update() under 'if (base.isServer)', and the client RPC
    /// UserCode_RpcStartBattle() merely opens the boss HP bar. On every non-host client it is therefore false for
    /// the whole fight, which would reproduce the 1.4.5 bug where AIZO only ever reacted on the host.
    ///
    /// 1.4.7 used 'at least one living UnitAvatar whose monsterType is Miniboss or Boss exists in the loaded
    /// scene'. Both fields are SyncVars, so clients see them, but the test answers the wrong question twice over:
    ///
    ///  1. PlayerAvatar derives from UnitAvatar and its serialized monsterType is Boss, so the local player
    ///     themselves satisfied it - at distance 0, alive for the whole run. That alone made the answer a
    ///     constant 'true' from the moment the avatar spawned. The game guards the same way: UI_EnemyViewer
    ///     checks '!(obj is PlayerAvatar)' before trusting monsterType.
    ///  2. Floors pre-spawn their rooms, so a boss waiting in a room the player has not entered yet also
    ///     counted.
    ///
    /// Because engage() latched and nothing reset it at a run boundary, the hold never released and AIZO
    /// stopped obeying the health threshold for the rest of the run (see BossFightLogic.IsBossEngaged for the
    /// replacement rule).
    ///
    /// Detection now requires a local player, skips the player and anything the player owns, and asks whether a
    /// boss is actually being fought. The world-load window (before the first PlayerAvatar spawns) can no
    /// longer report a fight.
    ///
    /// Cost: Object.FindObjectsOfType<UnitAvatar> every ScanSec. A dungeon room holds tens of units, and the mod
    /// already performs the same kind of scan for PlayerAvatar every 0.25s.
    /// </summary>
    internal static class BossFightWatcher
    {
        internal const float ScanSec = 0.5f;

        private static BossFightLogic.State _state;
        private static float _nextScan;

        /// <summary>Test seam: a harness can inject the unit list instead of scanning the live scene.</summary>
        internal static System.Func<UnitAvatar[]> Source;

        /// <summary>Test seam: a harness can inject the local player instead of resolving it through PlayerLocator.</summary>
        internal static System.Func<PlayerAvatar> PlayerSource;

        /// <summary>True while a mini-boss / boss fight is considered ongoing.</summary>
        internal static bool Active => _state.Active;

        internal static void Reset()
        {
            BossFightLogic.Reset(ref _state);
            _nextScan = 0f;
        }

        /// <summary>The local player, resolved through PlayerLocator (never through NetworkBehaviour.authority).</summary>
        internal static PlayerAvatar ResolvePlayer()
        {
            return PlayerSource != null ? PlayerSource() : PlayerLocator.GetLocal();
        }

        /// <summary>Called from both watchers' Update; throttled internally, so calling it twice per frame is fine
        /// (whichever track is enabled keeps the detector warm).</summary>
        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < _nextScan) return;
            _nextScan = now + ScanSec;

            PlayerAvatar self = ResolvePlayer();
            UnitAvatar engaged;
            bool fighting = AnyBossEngaged(self, out engaged);

            if (!BossFightLogic.Step(fighting, now, ref _state)) return;

            if (_state.Active)
                Log.Message("[rainylove][BOSS] 检测到小BOSS/BOSS → 战斗期间不停止播放，播完自动重播。（" +
                            Describe(engaged, self) + "）");
            else
                Log.Message("[rainylove][BOSS] BOSS 战结束（场上已无正在交战的 BOSS）→ 按各曲自己的停止设置处理停止。");
        }

        /// <summary>
        /// True when a living Miniboss/Boss is actually being fought (its AI has a hostile target, or it stands
        /// inside the player's room). Requires a local player: without one there is no fight to be in.
        /// Returns the unit that satisfied the test through <paramref name="first"/> for the transition log.
        /// </summary>
        internal static bool AnyBossEngaged(PlayerAvatar self, out UnitAvatar first)
        {
            first = null;
            if (self == null) return false;

            UnitAvatar[] units = Source != null ? Source() : Object.FindObjectsOfType<UnitAvatar>();
            if (units == null) return false;

            Vector3 p = self.transform.position;
            float radiusSqr = BossFightLogic.EngageRadius * BossFightLogic.EngageRadius;

            for (int i = 0; i < units.Length; i++)
            {
                UnitAvatar u = units[i];
                if (u == null) continue;

                // PlayerAvatar : UnitAvatar, and its serialized monsterType is Boss (the value is on the prefab;
                // no code assigns it). So 'any living Boss/Miniboss unit' was ALWAYS true - the player themselves
                // satisfied it at distance 0 - which is why 1.4.7 and 1.4.8 both latched the hold on save load and
                // never released it. The game guards the same way: UI_EnemyViewer does
                // 'if (!(obj is PlayerAvatar) && obj.monsterType == ...)' before trusting monsterType.
                if (u is PlayerAvatar) continue;

                // Player-owned follower / summon: on the player's side, so it is not a boss the player fights.
                if (u.NetworkLeader is PlayerAvatar) continue;

                bool bossType = u.monsterType == EMonsterType.Miniboss || u.monsterType == EMonsterType.Boss;
                if (!bossType) continue;
                if (u.IsDead) continue;

                Vector3 d = u.transform.position - p;
                float sqr = d.x * d.x + d.y * d.y;

                if (!BossFightLogic.IsBossEngaged(true, false, u.IsInBattle, sqr, radiusSqr)) continue;

                first = u;
                return true;
            }
            return false;
        }

        /// <summary>One-line evidence for the detection log: which unit qualified and why.</summary>
        private static string Describe(UnitAvatar u, PlayerAvatar self)
        {
            if (u == null) return "unit=null";
            float dist = self != null ? Vector2.Distance(self.transform.position, u.transform.position) : -1f;
            return "unit=" + u.name
                   + " type=" + u.monsterType
                   + " inBattle=" + u.IsInBattle
                   + " dist=" + dist.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
