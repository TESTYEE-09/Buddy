using System;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Host-only director for sparse, evidence-triggered character beats. It changes presentation,
    /// never gameplay authority: Buddy remains neutralized, useful and deterministic.
    /// </summary>
    internal static class BuddyCharacterDirector
    {
        private const float BeatCooldownSeconds = 150f;
        private const string SaveKey = "LethalAICrewmate_CharacterArcProgress";
        private const string QuotaSaveKey = "LethalAICrewmate_CharacterArcQuotaCycles";
        private static float _nextPollAt;
        private static float _nextBeatAt;
        private static bool _initialized;
        private static bool _roundSeedKnown;
        private static int _lastRoundSeed;
        private static int _completedRounds;
        private static int _witnessedDeaths;
        private static int _lastLivingPlayers;
        private static int _lastQuotaCycles;
        private static int _progress;
        // Just the sentence now. This was a small class carrying an event kind and a variant seed
        // so the deleted dialogue catalogue could pick which hardcoded line to use; with the model
        // writing the line, only the fact it is reacting to still matters.
        private static string _pending;

        internal static BuddyArcStage CurrentStage { get; private set; } = BuddyArcStage.Coworker;

        internal static string PromptMemory()
        {
            if (!_initialized) return "History: none yet. Do not invent any.";
            return BuddyCharacterArc.ContinuitySummary(_lastQuotaCycles, _completedRounds, _witnessedDeaths);
        }

        internal static void Tick()
        {
            try
            {
                if (!CrewmateSpawner.IsHost()) return;
                if (Time.unscaledTime < _nextPollAt) return;
                _nextPollAt = Time.unscaledTime + 1f;

                var sor = StartOfRound.Instance;
                var buddy = CrewmateRegistry.GetPrimary();
                if (sor == null || buddy?.Enemy == null) return;

                int quotaCycles = 0;
                try { if (TimeOfDay.Instance != null) quotaCycles = Mathf.Max(0, TimeOfDay.Instance.timesFulfilledQuota); }
                catch { }

                if (Plugin.ResetSlowBurnProgress?.Value == true)
                {
                    _progress = 0;
                    _lastQuotaCycles = quotaCycles;
                    SaveProgress();
                    Plugin.ResetSlowBurnProgress.Value = false;
                    Plugin.SaveConfiguration();
                    _initialized = false;
                    _roundSeedKnown = false;
                    _completedRounds = 0;
                    _witnessedDeaths = 0;
                    _pending = null;
                    CurrentStage = BuddyArcStage.Coworker;
                    Plugin.Log?.LogInfo("Buddy character arc reset for the current save.");
                }
                if (Plugin.SlowBurnHorror?.Value != true)
                {
                    CurrentStage = BuddyArcStage.Coworker;
                    _pending = null;
                    return;
                }

                if (!_initialized)
                {
                    _initialized = true;
                    _lastLivingPlayers = Mathf.Max(0, sor.livingPlayers);
                    LoadProgress(quotaCycles);
                    CurrentStage = BuddyCharacterArc.StageForScore(_progress);
                    Plugin.Log?.LogInfo("Buddy character arc loaded stage=" + CurrentStage + " progress=" + _progress + ".");
                    return;
                }

                string evidenceBeat = null;
                bool landed = !sor.inShipPhase && sor.shipHasLanded;
                if (landed)
                {
                    int seed = sor.randomMapSeed;
                    if (!_roundSeedKnown)
                    {
                        _roundSeedKnown = true;
                        _lastRoundSeed = seed;
                    }
                    else if (seed != _lastRoundSeed)
                    {
                        _lastRoundSeed = seed;
                        _completedRounds++;
                        _progress = BuddyCharacterArc.AdvanceScore(_progress,
                            BuddyCharacterArc.EventPoints(BuddyArcEvent.RoundStarted));
                        evidenceBeat = "The crew has landed for another shift";
                    }

                    // A global living-player count is not evidence Buddy witnessed a death.
                    // Follow-target death handling records only locally confirmable events.
                    _lastLivingPlayers = Mathf.Max(0, sor.livingPlayers);
                }

                if (quotaCycles > _lastQuotaCycles)
                {
                    _progress = BuddyCharacterArc.AdvanceScore(_progress,
                        BuddyCharacterArc.QuotaDeltaPoints(_lastQuotaCycles, quotaCycles));
                    evidenceBeat = "The crew has made quota again";
                    _lastQuotaCycles = quotaCycles;
                }

                BuddyArcStage previous = CurrentStage;
                if (evidenceBeat != null) SaveProgress();
                CurrentStage = BuddyCharacterArc.StageForScore(_progress);
                if (CurrentStage > previous)
                {
                    Plugin.Log?.LogInfo("Buddy character arc advanced " + previous + " -> " + CurrentStage + " at progress=" + _progress + ".");
                    _pending = "Something in you has shifted a little further";
                }
                else if (evidenceBeat != null && _pending == null)
                    _pending = evidenceBeat;

                if (_pending != null && CurrentStage != BuddyArcStage.Coworker && Time.unscaledTime >= _nextBeatAt)
                {
                    // Hand the model the thing that happened and let it find the words. The old
                    // path picked from a hardcoded catalogue per stage, so the same two lines came
                    // back forever and landed as stage dressing rather than as Buddy.
                    // Only drop the beat and start the cooldown once it has actually been taken.
                    // A queue that is full, or a player pressing push-to-talk, would otherwise
                    // consume a stage moment that then waits another two and a half minutes.
                    if (LlmClient.TryEnqueueObservation(_pending +
                        ". One short line if it is worth one, otherwise nothing at all."))
                    {
                        _pending = null;
                        _nextBeatAt = Time.unscaledTime + BeatCooldownSeconds;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("Buddy character director: " + ex.Message);
            }
        }

        internal static void ResetSession()
        {
            _nextPollAt = 0f;
            _nextBeatAt = 0f;
            _initialized = false;
            _roundSeedKnown = false;
            _lastRoundSeed = 0;
            _completedRounds = 0;
            _witnessedDeaths = 0;
            _lastLivingPlayers = -1;
            _lastQuotaCycles = 0;
            _progress = 0;
            _pending = null;
            CurrentStage = BuddyArcStage.Coworker;
        }

        internal static void RecordWitnessedDeath(string playerName)
        {
            if (!CrewmateSpawner.IsHost() || Plugin.SlowBurnHorror?.Value != true) return;
            _witnessedDeaths++;
            int living = Mathf.Max(0, StartOfRound.Instance?.livingPlayers ?? 0);
            BuddyArcEvent kind = living <= 1 ? BuddyArcEvent.LastCrewmate : BuddyArcEvent.CrewDeath;
            _progress = BuddyCharacterArc.AdvanceScore(_progress, BuddyCharacterArc.EventPoints(kind, 1));
            _pending = "You watched " + (string.IsNullOrWhiteSpace(playerName) ? "a crewmate" : playerName) +
                       " die, right next to you";
            SaveProgress();
        }

        private static void LoadProgress(int currentQuotaCycles)
        {
            try
            {
                string saveFile = GameNetworkManager.Instance?.currentSaveFileName;
                if (string.IsNullOrWhiteSpace(saveFile))
                {
                    _progress = BuddyCharacterArc.InitialProgress(false, 0, currentQuotaCycles);
                    _lastQuotaCycles = currentQuotaCycles;
                    return;
                }
                bool hasSavedProgress = ES3.KeyExists(SaveKey, saveFile);
                int savedProgress = ES3.Load<int>(SaveKey, saveFile, 0);
                _progress = BuddyCharacterArc.InitialProgress(hasSavedProgress, savedProgress, currentQuotaCycles);
                _lastQuotaCycles = hasSavedProgress
                    ? Mathf.Max(0, ES3.Load<int>(QuotaSaveKey, saveFile, currentQuotaCycles))
                    : currentQuotaCycles;
                if (!hasSavedProgress) SaveProgress();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("Buddy character progress load: " + ex.Message);
                _progress = BuddyCharacterArc.InitialProgress(false, 0, currentQuotaCycles);
                _lastQuotaCycles = currentQuotaCycles;
            }
        }

        private static void SaveProgress()
        {
            try
            {
                string saveFile = GameNetworkManager.Instance?.currentSaveFileName;
                if (string.IsNullOrWhiteSpace(saveFile)) return;
                ES3.Save(SaveKey, Mathf.Max(0, _progress), saveFile);
                ES3.Save(QuotaSaveKey, Mathf.Max(0, _lastQuotaCycles), saveFile);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("Buddy character progress save: " + ex.Message);
            }
        }
    }
}
