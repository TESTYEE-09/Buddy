using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Host-only, evidence-driven conversation initiator. It queues compact context for the model;
    /// player turns remain higher priority and observations are dropped rather than accumulated.
    /// </summary>
    internal static class BuddyAutonomy
    {
        private sealed class PendingEvent
        {
            internal BuddyContextEvent Kind;
            internal string Evidence;
            internal int Importance;
        }

        private static readonly Dictionary<BuddyContextEvent, float> LastEventAt = new Dictionary<BuddyContextEvent, float>();
        private static PendingEvent _pending;
        private static float _nextPollAt;
        private static float _lastSpokeAt = -999f;
        private static float _travelStartedAt;
        private static float _separatedAt;
        private static bool _stateKnown;
        private static bool _wasInside;
        private static bool _wasInShip;
        private static int _lastValuableScrapId;

        internal static void Queue(BuddyContextEvent kind, string evidence)
        {
            if (string.IsNullOrWhiteSpace(evidence)) return;
            if (kind != BuddyContextEvent.WitnessedDeathReport && kind != BuddyContextEvent.HazardNearby)
                return;
            int importance = BuddyAutonomyPolicy.Importance(kind);
            if (_pending != null && _pending.Importance > importance) return;
            _pending = new PendingEvent { Kind = kind, Evidence = evidence.Trim(), Importance = importance };
        }

        internal static void Tick()
        {
            try
            {
                if (!CrewmateSpawner.IsHost() || Time.unscaledTime < _nextPollAt) return;
                _nextPollAt = Time.unscaledTime + 0.75f;

                CrewmateData data = CrewmateRegistry.GetPrimary();
                PlayerControllerB owner = data?.Owner;
                if (data?.Enemy == null || owner == null || owner.isPlayerDead) return;

                ObserveTransitions(data, owner);
                ObserveTravel(data, owner);
                ObserveValuableScrap(data);
                TrySpeak();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("Buddy autonomy: " + ex.Message);
            }
        }

        private static void ObserveTransitions(CrewmateData data, PlayerControllerB owner)
        {
            bool inside = owner.isInsideFactory;
            bool inShip = owner.isInHangarShipRoom;
            if (!_stateKnown)
            {
                _stateKnown = true;
                _wasInside = inside;
                _wasInShip = inShip;
                return;
            }

            if (inside != _wasInside)
                Queue(inside ? BuddyContextEvent.EnteredFacility : BuddyContextEvent.LeftFacility,
                    inside ? "Buddy and his followed crewmate have just entered the facility."
                           : "Buddy and his followed crewmate have just left the facility for the moon exterior.");
            if (inShip && !_wasInShip)
                Queue(BuddyContextEvent.ReturnedToShip,
                    "Buddy and his followed crewmate have just returned to the ship after being outside.");
            _wasInside = inside;
            _wasInShip = inShip;
        }

        private static void ObserveTravel(CrewmateData data, PlayerControllerB owner)
        {
            float distance = Vector3.Distance(data.Enemy.transform.position, owner.transform.position);
            bool moving = data.Enemy.moveTowardsDestination && data.ManualDestination != Vector3.zero;
            if (moving)
            {
                if (_travelStartedAt <= 0f) _travelStartedAt = Time.unscaledTime;
                if (Time.unscaledTime - _travelStartedAt >= 45f)
                {
                    Queue(BuddyContextEvent.LongTravel,
                        "Buddy and the crew have been travelling together for roughly 45 seconds without a notable event.");
                    _travelStartedAt = Time.unscaledTime;
                }
            }
            else _travelStartedAt = 0f;

            if (distance >= 28f)
            {
                if (_separatedAt <= 0f) _separatedAt = Time.unscaledTime;
                if (Time.unscaledTime - _separatedAt >= 18f)
                    Queue(BuddyContextEvent.Separated,
                        "Buddy is genuinely separated from his followed crewmate by " + Mathf.RoundToInt(distance) + " metres.");
            }
            else _separatedAt = 0f;

            if (!moving && distance <= 14f && Time.unscaledTime - LlmClient.LastPlayerInteractionAt >= 105f)
                Queue(BuddyContextEvent.QuietDowntime,
                    "The nearby crew has been quiet for a long stretch of safe downtime. Start one brief normal coworker conversation if it feels natural.");
        }

        private static void ObserveValuableScrap(CrewmateData data)
        {
            GrabbableObject best = null;
            int value = 0;
            foreach (var item in UnityEngine.Object.FindObjectsOfType<GrabbableObject>())
            {
                if (item?.itemProperties == null || !item.itemProperties.isScrap || item.isHeld || item.isInShipRoom) continue;
                if (Vector3.Distance(data.Enemy.transform.position, item.transform.position) > 12f) continue;
                if (item.scrapValue > value) { best = item; value = item.scrapValue; }
            }
            if (best == null || value < 80 || best.GetInstanceID() == _lastValuableScrapId) return;
            _lastValuableScrapId = best.GetInstanceID();
            Queue(BuddyContextEvent.ValuableScrap,
                "Buddy has just come within 12 metres of confirmed loose scrap named " +
                (best.itemProperties.itemName ?? "scrap") + " worth " + value + ".");
        }

        private static void TrySpeak()
        {
            if (_pending == null || !LlmClient.HasApiKey) return;
            float now = Time.unscaledTime;
            if (!LastEventAt.TryGetValue(_pending.Kind, out float sameEventAt))
                sameEventAt = -999f;
            if (!BuddyAutonomyPolicy.CanSpeak(now, _lastSpokeAt, LlmClient.LastPlayerInteractionAt, sameEventAt, _pending.Kind))
                return;

            PendingEvent current = _pending;

            // The pacing director owns how quiet Buddy currently is, and the social layer owns
            // whether the humans still have the floor. Safety-relevant events bypass neither
            // check silently: they simply carry a higher-priority speech reason.
            BuddySpeechReason reason = SpeechReason(current.Kind);
            if (reason != BuddySpeechReason.Danger)
            {
                if (BuddyPacingDirector.SuppressSmallTalk && BuddyAutonomyPolicy.Importance(current.Kind) < 70)
                    return;
                if (now - _lastSpokeAt < BuddyAutonomyPolicy.GlobalCooldown + BuddyPacingDirector.ExtraSilenceSeconds)
                    return;
            }
            if (BuddySocialIntelligence.ShouldWaitForTurn(reason)) return;

            if (!LlmClient.TryEnqueueObservation(
                current.Evidence + " Initiate at most one short, natural line grounded only in this evidence and live context. Silence is acceptable if danger or player speech has priority."))
                return;
            _pending = null;
            _lastSpokeAt = now;
            LastEventAt[current.Kind] = now;
        }

        private static BuddySpeechReason SpeechReason(BuddyContextEvent kind)
        {
            if (kind == BuddyContextEvent.WitnessedDeathReport || kind == BuddyContextEvent.UnusualEnemy)
                return BuddySpeechReason.Danger;
            if (kind == BuddyContextEvent.HazardNearby || kind == BuddyContextEvent.Separated)
                return BuddySpeechReason.OpenQuestion;
            return BuddySpeechReason.Unprompted;
        }

        internal static void ResetSession()
        {
            LastEventAt.Clear();
            _pending = null;
            _nextPollAt = 0f;
            _lastSpokeAt = -999f;
            _travelStartedAt = 0f;
            _separatedAt = 0f;
            _stateKnown = false;
            _wasInside = false;
            _wasInShip = false;
            _lastValuableScrapId = 0;
        }
    }
}
