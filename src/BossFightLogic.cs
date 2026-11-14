namespace RainyLove
{
    /// <summary>
    /// 'Is a mini-boss / boss fight going on?' hysteresis, kept free of Unity types so it can be driven from a
    /// plain console harness.
    ///
    /// Why hysteresis: the raw signal (a living UnitAvatar with monsterType Miniboss/Boss) flickers. It drops to
    /// false during summon gaps, and it drops to false the instant the boss dies even though the victory cut-scene
    /// and the reward sequence are still running. Ending the hold on that first false frame would cut the music in
    /// the middle of the boss death animation, so the end is only accepted after EndGraceSec of continuous silence.
    ///
    /// Starting is immediate on purpose: a fight that begins must lock the music in at once.
    /// </summary>
    internal static class BossFightLogic
    {
        /// <summary>Continuous 'no living boss' time required before the fight counts as over.</summary>
        internal const float EndGraceSec = 1.5f;

        /// <summary>
        /// How close a boss must be to count as 'the one the player is fighting' while its AI has no hostile
        /// target yet. 12 is not a guess: UnitAI_NewBasic uses 'distance &gt; 12' to decide that the current
        /// target has walked into a different room and gives up on it, so 12 is the game's own room radius.
        /// </summary>
        internal const float EngageRadius = 12f;

        /// <summary>
        /// Pure predicate: is this unit a boss that is actually being fought right now?
        ///
        /// 'A living boss-type unit exists somewhere in the loaded scene' is NOT the same thing, and using it
        /// was the 1.4.7/1.4.8 bug. Two independent reasons:
        ///  - PlayerAvatar : UnitAvatar with a serialized monsterType of Boss, so the local player always
        ///    qualified (distance 0, alive all run). The caller must skip PlayerAvatar before calling this.
        ///  - Dungeon floors pre-spawn their rooms, so a MiniBoss standing in a room the player has not
        ///    entered yet also satisfied it.
        /// Either way the hold engaged immediately and - because nothing reset it - never released, so AIZO
        /// stopped following the health threshold for the rest of the run.
        ///
        /// Two ways to qualify, because the engine has both:
        ///  - inBattle: UnitAI_NewBasic.OnFoundTarget calls Avatar.StartBattle() as soon as the AI has a hostile
        ///    target (UnitAI_BossBasic inherits that and does not override it), and isInBattle is a SyncVar, so
        ///    this is visible on clients and means 'this boss is fighting someone'.
        ///  - within EngageRadius: covers the moment the boss has been spawned and the player is in its room but
        ///    the AI has not picked a target yet (entry sequence, door closing).
        /// </summary>
        internal static bool IsBossEngaged(bool isBossType, bool isDead, bool inBattle,
                                           float sqrDistance, float engageRadiusSqr)
        {
            if (!isBossType || isDead) return false;
            if (inBattle) return true;
            return sqrDistance <= engageRadiusSqr;
        }

        internal struct State
        {
            internal bool Active;
            internal float NoBossSince;   // Unscaled time the boss last went missing; <0 = not counting
        }

        /// <summary>One evaluation. Returns true when Active changed (so the caller can log exactly once).</summary>
        internal static bool Step(bool bossAlive, float now, ref State s)
        {
            if (bossAlive)
            {
                s.NoBossSince = -1f;
                if (s.Active) return false;
                s.Active = true;
                return true;
            }

            if (!s.Active) return false;
            if (s.NoBossSince < 0f)
            {
                s.NoBossSince = now;
                return false;
            }
            if (now - s.NoBossSince < EndGraceSec) return false;

            s.Active = false;
            s.NoBossSince = -1f;
            return true;
        }

        internal static void Reset(ref State s)
        {
            s.Active = false;
            s.NoBossSince = -1f;
        }
    }
}
