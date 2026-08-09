using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Host-only per-player bonds built from events Buddy can actually observe locally.
    /// Storage is deliberately minimal: at most eight entries of three small bounded integers,
    /// keyed by a 16-bit non-reversible digest. No names, Steam IDs, chat text, transcripts or
    /// timestamps are ever written to disk, and nothing here is replicated to clients.
    /// </summary>
    internal static class BuddyRelationships
    {
        private const string DigestSaveKey = "LethalAICrewmate_BondDigests";
        private const string ValueSaveKey = "LethalAICrewmate_BondValues";
        private const float PollSeconds = 2f;
        private const float TogetherGrantSeconds = 90f;
        private const float AwayGrantSeconds = 45f;

        private static readonly Dictionary<uint, BuddyBond> Bonds = new Dictionary<uint, BuddyBond>();
        private static readonly Dictionary<uint, float> TogetherSince = new Dictionary<uint, float>();
        private static readonly Dictionary<uint, float> AwaySince = new Dictionary<uint, float>();
        private static float _nextPollAt;
        private static bool _loaded;
        private static bool _dirty;
        private static float _nextSaveAt;

        /// <summary>Display name of whoever Buddy is currently answering. Session-only, never persisted.</summary>
        private static string _currentSpeaker;

        internal static bool Active => Plugin.PlayerRelationships?.Value == true && CrewmateSpawner.IsHost();

        /// <summary>Records who Buddy is replying to so the prompt can colour the reply.</summary>
        internal static void NoteAddressing(string playerName)
        {
            if (!Active) return;
            _currentSpeaker = string.IsNullOrWhiteSpace(playerName) ? null : playerName;
        }

        /// <summary>Relationship guidance for the player Buddy is currently answering, or null.</summary>
        internal static string CurrentPromptLine() => PromptLineFor(_currentSpeaker);

        internal static BuddyBond BondFor(string playerName)
        {
            if (!Active || string.IsNullOrWhiteSpace(playerName)) return default;
            return Bonds.TryGetValue(BuddyRelationshipModel.IdentityDigest(playerName), out BuddyBond bond)
                ? bond
                : default;
        }

        internal static string PromptLineFor(string playerName)
        {
            if (!Active || string.IsNullOrWhiteSpace(playerName)) return null;
            BuddyBond bond = BondFor(playerName);
            if (bond.IsBlank) return null;
            return BuddyRelationshipModel.PromptLine(playerName, bond);
        }

        /// <summary>Affinity used when Buddy must pick whom to follow or answer first.</summary>
        internal static int AffinityFor(string playerName) =>
            Active ? BuddyRelationshipModel.Affinity(BondFor(playerName)) : 0;

        internal static void Note(string playerName, BuddyRelationEvent kind)
        {
            if (!Active || string.IsNullOrWhiteSpace(playerName)) return;
            try
            {
                uint digest = BuddyRelationshipModel.IdentityDigest(playerName);
                if (!Bonds.TryGetValue(digest, out BuddyBond bond))
                {
                    if (!MakeRoom()) return;
                    bond = default;
                }
                Bonds[digest] = BuddyRelationshipModel.Apply(bond, kind);
                _dirty = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("Buddy relationship note: " + ex.Message);
            }
        }

        internal static void Tick()
        {
            try
            {
                if (!Active) return;
                if (Time.unscaledTime < _nextPollAt) return;
                _nextPollAt = Time.unscaledTime + PollSeconds;

                if (!_loaded) Load();

                CrewmateData data = CrewmateRegistry.GetPrimary();
                if (data?.Enemy == null) return;

                Vector3 origin = data.Enemy.transform.position;
                bool dangerNearby = HostileWithin(origin, 18f);
                PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
                if (players == null) return;

                float now = Time.unscaledTime;
                foreach (PlayerControllerB player in players)
                {
                    if (player == null || !player.isPlayerControlled || player.isPlayerDead) continue;
                    string name = player.playerUsername;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    uint digest = BuddyRelationshipModel.IdentityDigest(name);

                    float distance = Vector3.Distance(origin, player.transform.position);
                    if (distance <= 14f)
                    {
                        AwaySince.Remove(digest);
                        if (!TogetherSince.TryGetValue(digest, out float since))
                        {
                            TogetherSince[digest] = now;
                        }
                        else if (now - since >= TogetherGrantSeconds)
                        {
                            TogetherSince[digest] = now;
                            Note(name, dangerNearby ? BuddyRelationEvent.SharedDanger : BuddyRelationEvent.TimeTogether);
                        }
                        else if (dangerNearby && now - since >= 8f)
                        {
                            TogetherSince[digest] = now;
                            Note(name, BuddyRelationEvent.SharedDanger);
                        }
                    }
                    else if (distance >= 40f)
                    {
                        TogetherSince.Remove(digest);
                        if (!AwaySince.TryGetValue(digest, out float since))
                        {
                            AwaySince[digest] = now;
                        }
                        else if (now - since >= AwayGrantSeconds)
                        {
                            AwaySince[digest] = now;
                            Note(name, BuddyRelationEvent.LeftBuddyBehind);
                        }
                    }
                    else
                    {
                        TogetherSince.Remove(digest);
                        AwaySince.Remove(digest);
                    }
                }

                if (_dirty && now >= _nextSaveAt)
                {
                    _nextSaveAt = now + 20f;
                    Save();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("Buddy relationships: " + ex.Message);
            }
        }

        private static bool HostileWithin(Vector3 origin, float radius)
        {
            try
            {
                foreach (EnemyAI enemy in UnityEngine.Object.FindObjectsOfType<EnemyAI>())
                {
                    if (enemy == null || enemy.isEnemyDead) continue;
                    if (CrewmateRegistry.IsCrewmate(enemy)) continue;
                    if (Vector3.Distance(origin, enemy.transform.position) <= radius) return true;
                }
            }
            catch { /* ignore */ }
            return false;
        }

        /// <summary>Keeps the tracked set at or below the hard cap by dropping the least-known bond.</summary>
        private static bool MakeRoom()
        {
            if (Bonds.Count < BuddyRelationshipModel.MaxTrackedPlayers) return true;
            uint weakest = 0;
            int weakestScore = int.MaxValue;
            bool found = false;
            foreach (KeyValuePair<uint, BuddyBond> entry in Bonds)
            {
                int score = entry.Value.Familiarity + Math.Abs(entry.Value.Trust);
                if (score < weakestScore) { weakestScore = score; weakest = entry.Key; found = true; }
            }
            if (!found) return false;
            Bonds.Remove(weakest);
            TogetherSince.Remove(weakest);
            AwaySince.Remove(weakest);
            return true;
        }

        private static void Load()
        {
            _loaded = true;
            try
            {
                string saveFile = GameNetworkManager.Instance?.currentSaveFileName;
                if (string.IsNullOrWhiteSpace(saveFile)) return;
                if (!ES3.KeyExists(DigestSaveKey, saveFile) || !ES3.KeyExists(ValueSaveKey, saveFile)) return;

                int[] digests = ES3.Load<int[]>(DigestSaveKey, saveFile, null);
                int[] values = ES3.Load<int[]>(ValueSaveKey, saveFile, null);
                if (digests == null || values == null) return;

                int count = Math.Min(Math.Min(digests.Length, values.Length), BuddyRelationshipModel.MaxTrackedPlayers);
                Bonds.Clear();
                for (int i = 0; i < count; i++)
                {
                    uint digest = (uint)(digests[i] & 0xFFFF);
                    BuddyBond bond = BuddyRelationshipModel.Unpack(values[i]);
                    if (bond.IsBlank) continue;
                    Bonds[digest] = bond;
                }
                Plugin.Log?.LogInfo("Buddy loaded " + Bonds.Count + " stored player bond(s).");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("Buddy relationship load: " + ex.Message);
                Bonds.Clear();
            }
        }

        private static void Save()
        {
            _dirty = false;
            try
            {
                string saveFile = GameNetworkManager.Instance?.currentSaveFileName;
                if (string.IsNullOrWhiteSpace(saveFile)) return;

                int count = Math.Min(Bonds.Count, BuddyRelationshipModel.MaxTrackedPlayers);
                int[] digests = new int[count];
                int[] values = new int[count];
                int index = 0;
                foreach (KeyValuePair<uint, BuddyBond> entry in Bonds)
                {
                    if (index >= count) break;
                    digests[index] = (int)(entry.Key & 0xFFFFu);
                    values[index] = BuddyRelationshipModel.Pack(entry.Value);
                    index++;
                }
                ES3.Save(DigestSaveKey, digests, saveFile);
                ES3.Save(ValueSaveKey, values, saveFile);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("Buddy relationship save: " + ex.Message);
            }
        }

        internal static void ResetSession()
        {
            Bonds.Clear();
            TogetherSince.Clear();
            AwaySince.Clear();
            _currentSpeaker = null;
            _nextPollAt = 0f;
            _nextSaveAt = 0f;
            _loaded = false;
            _dirty = false;
        }
    }
}
