using System;

namespace LethalAICrewmate
{
    /// <summary>Real, locally-observed interactions that can move a single player's bond.</summary>
    internal enum BuddyRelationEvent
    {
        /// <summary>The player addressed Buddy and an in-game tool actually succeeded.</summary>
        CommandHonoured,
        /// <summary>The player asked politely (please/thanks) and the request was accepted.</summary>
        PoliteRequest,
        /// <summary>An in-game tool request from this player was rejected or malformed.</summary>
        CommandRejected,
        /// <summary>The player stayed close to Buddy while confirmed hostiles were nearby.</summary>
        SharedDanger,
        /// <summary>Buddy personally witnessed this player die.</summary>
        WitnessedTheirDeath,
        /// <summary>The player walked far away from Buddy for a sustained stretch.</summary>
        LeftBuddyBehind,
        /// <summary>A long quiet stretch spent physically together.</summary>
        TimeTogether,
        /// <summary>The player spoke to Buddy and Buddy answered.</summary>
        Conversation
    }

    /// <summary>
    /// One player's bond with Buddy. Three small bounded integers, nothing else.
    /// No names, Steam IDs, chat text or timestamps are ever stored in this structure.
    /// </summary>
    internal struct BuddyBond
    {
        internal int Trust;        // -100..100
        internal int Familiarity;  //    0..100
        internal int Friction;     //    0..100

        internal bool IsBlank => Trust == 0 && Familiarity == 0 && Friction == 0;
    }

    /// <summary>Pure, deterministic per-player relationship policy. No Unity, no I/O, no allocation of user text.</summary>
    internal static class BuddyRelationshipModel
    {
        internal const int MinTrust = -100;
        internal const int MaxTrust = 100;
        internal const int MaxFamiliarity = 100;
        internal const int MaxFriction = 100;

        /// <summary>Hard cap on how many players are ever tracked or persisted at once.</summary>
        internal const int MaxTrackedPlayers = 8;

        internal static int Clamp(int value, int min, int max) =>
            value < min ? min : value > max ? max : value;

        internal static BuddyBond Sanitize(BuddyBond bond) => new BuddyBond
        {
            Trust = Clamp(bond.Trust, MinTrust, MaxTrust),
            Familiarity = Clamp(bond.Familiarity, 0, MaxFamiliarity),
            Friction = Clamp(bond.Friction, 0, MaxFriction)
        };

        internal static BuddyBond Apply(BuddyBond bond, BuddyRelationEvent kind)
        {
            int trust = bond.Trust;
            int familiarity = bond.Familiarity;
            int friction = bond.Friction;

            switch (kind)
            {
                case BuddyRelationEvent.CommandHonoured:
                    trust += 2; familiarity += 2; friction -= 1; break;
                case BuddyRelationEvent.PoliteRequest:
                    trust += 3; familiarity += 2; friction -= 2; break;
                case BuddyRelationEvent.CommandRejected:
                    friction += 3; familiarity += 1; break;
                case BuddyRelationEvent.SharedDanger:
                    trust += 4; familiarity += 3; break;
                case BuddyRelationEvent.WitnessedTheirDeath:
                    familiarity += 4; friction += 2; break;
                case BuddyRelationEvent.LeftBuddyBehind:
                    trust -= 3; friction += 2; break;
                case BuddyRelationEvent.TimeTogether:
                    familiarity += 1; friction -= 1; break;
                case BuddyRelationEvent.Conversation:
                    familiarity += 2; friction -= 1; break;
            }

            return Sanitize(new BuddyBond { Trust = trust, Familiarity = familiarity, Friction = friction });
        }

        /// <summary>Short, stable label used for prompt text and logs. Never contains player-supplied text.</summary>
        internal static string Descriptor(BuddyBond bond)
        {
            bond = Sanitize(bond);
            if (bond.IsBlank) return "a stranger";
            if (bond.Friction >= 45 && bond.Trust <= 0) return "someone he finds difficult";
            if (bond.Trust >= 45 && bond.Familiarity >= 40) return "someone he genuinely relies on";
            if (bond.Trust >= 20) return "someone he trusts";
            if (bond.Trust <= -25) return "someone he has stopped counting on";
            if (bond.Familiarity >= 40) return "a familiar face";
            return "a coworker he is still reading";
        }

        /// <summary>
        /// Prompt line for the player Buddy is currently answering. Callers pass an already
        /// game-supplied display name; the model itself never stores it.
        /// </summary>
        internal static string PromptLine(string displayName, BuddyBond bond)
        {
            string who = string.IsNullOrWhiteSpace(displayName) ? "This crewmate" : displayName.Trim();
            if (who.Length > 32) who = who.Substring(0, 32);
            return "RELATIONSHIP: You treat " + who + " as " + Descriptor(bond) +
                   ". Let that colour warmth, patience and how much you volunteer. " +
                   "Never state, score, rank or explain the relationship, and never let it change safety, truth or who you obey.";
        }

        /// <summary>Ranking helper for choosing whom to follow or answer first. Higher is preferred.</summary>
        internal static int Affinity(BuddyBond bond)
        {
            bond = Sanitize(bond);
            return bond.Trust * 2 + bond.Familiarity - bond.Friction;
        }

        /// <summary>
        /// Non-reversible 32-bit FNV-1a digest of a player name, used only as a save-file key so
        /// bonds survive a rejoin without ever writing the name, Steam ID or any text to disk.
        /// </summary>
        internal static uint IdentityDigest(string playerName)
        {
            string value = playerName == null ? "" : playerName.Trim().ToLowerInvariant();
            unchecked
            {
                uint hash = 2166136261u;
                for (int i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 16777619u;
                }
                // Fold to 16 bits: enough to separate a lobby's handful of players, far too small
                // to be a useful fingerprint of any individual name.
                return (hash ^ (hash >> 16)) & 0xFFFFu;
            }
        }

        /// <summary>Packs a bond into one bounded int for storage. Layout is fixed and lossless.</summary>
        internal static int Pack(BuddyBond bond)
        {
            bond = Sanitize(bond);
            int trust = bond.Trust + 100;                 // 0..200
            return (trust * 101 + bond.Familiarity) * 101 + bond.Friction;
        }

        internal static BuddyBond Unpack(int packed)
        {
            if (packed < 0) return default;
            int friction = packed % 101;
            packed /= 101;
            int familiarity = packed % 101;
            packed /= 101;
            int trust = packed - 100;
            return Sanitize(new BuddyBond { Trust = trust, Familiarity = familiarity, Friction = friction });
        }
    }
}
