using System;
using System.IO;
using System.Runtime.CompilerServices;
using LethalAICrewmate;

internal static class SecurityRegressionChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        RunFinalStageGateChecks();
        RunRelationshipStorageChecks();
        RunSocialAndPacingChecks();

        string root = Directory.GetCurrentDirectory();
        string pluginPath = Path.Combine(root, "src", "Plugin.cs");
        Require(File.Exists(pluginPath), "release checks must locate src/Plugin.cs");
        if (File.Exists(pluginPath))
        {
            string plugin = File.ReadAllText(pluginPath);
            Require(plugin.Contains("ClearLegacyPlaintextKey(true);", StringComparison.Ordinal),
                    "legacy OpenAI keys must be deleted from plaintext config after import");
            Require(plugin.Contains("RemoveObsoleteConfigEntries(removeLegacyGroqKey: true, removeLegacyOpenAiKey: true);", StringComparison.Ordinal),
                    "obsolete plaintext provider key definitions must always be removed");
            Require(plugin.Contains("AlternatePushToTalkKey\", KeyCode.None", StringComparison.Ordinal),
                    "the normal game voice key must not record for Buddy by default");
            Require(plugin.Contains("SaveResponses\", false", StringComparison.Ordinal) &&
                    plugin.Contains("SavePromptContext\", false", StringComparison.Ordinal),
                    "raw response and prompt-context persistence must remain opt-in");
            Require(plugin.Contains("FinalStageHostileSpawns\", true", StringComparison.Ordinal),
                    "new installs must enable the requested final-stage hostile spawning feature");
            Require(plugin.Contains("BuddySettingsMenu.Register();", StringComparison.Ordinal),
                    "Buddy settings must be registered through the native LethalSettings menu");
        }

        string realtimePath = Path.Combine(root, "src", "OpenAiRealtimeVoiceClient.cs");
        Require(File.Exists(realtimePath), "release checks must locate OpenAiRealtimeVoiceClient.cs");
        string realtime = File.ReadAllText(realtimePath);
        Require(realtime.Contains("tool_choice\\\":\\\"auto", StringComparison.Ordinal) &&
                realtime.Contains("ToolDefinitionsJson", StringComparison.Ordinal) &&
                realtime.Contains("function_call_output", StringComparison.Ordinal) &&
                realtime.Contains("ExecuteRealtimeToolAsync", StringComparison.Ordinal),
                "Realtime must expose game tools, execute them on the host, and return results to the model");
        Require(!realtime.Contains("OpenAiTranscriptionModel", StringComparison.Ordinal) &&
                !realtime.Contains("input_audio_transcription", StringComparison.Ordinal) &&
                realtime.Contains("max_output_tokens\\\":1024", StringComparison.Ordinal),
                "Realtime must use only gpt-realtime-2.1-mini and retain enough audio output budget");

        string promptPath = Path.Combine(root, "src", "BuddyConversationPrompt.cs");
        string prompt = File.ReadAllText(promptPath);
        Require(prompt.Contains("Never recommend an exit", StringComparison.Ordinal) &&
                prompt.Contains("normal Lethal Company knowledge", StringComparison.Ordinal) &&
                prompt.Contains("Call the tool first", StringComparison.Ordinal) &&
                prompt.Contains("Say bazinga", StringComparison.Ordinal),
                "the rewritten prompt must lock direct, useful and natural behavior from the saved-session regressions");

        string settingsPath = Path.Combine(root, "src", "BuddySettingsMenu.cs");
        string settings = File.ReadAllText(settingsPath);
        Require(!settings.Contains("Buddy personality prompt", StringComparison.Ordinal),
                "the personality textbox must stay out of native settings");

        string memoryPath = Path.Combine(root, "src", "BuddyConversationMemory.cs");
        string memory = File.ReadAllText(memoryPath);
        Require(!memory.Contains("sb.Append(\"Buddy: \")", StringComparison.Ordinal),
                "long-term prompt context must not teach Buddy from its own prior bad replies");

        string chatPath = Path.Combine(root, "src", "ChatPatches.cs");
        Require(File.Exists(chatPath), "release checks must locate ChatPatches.cs");
        string chat = File.ReadAllText(chatPath);
        Require(!chat.Contains("Chat observed: '", StringComparison.Ordinal),
                "ordinary logs must not contain raw player chat");
        Require(!chat.Contains("MovementCommandParsing", StringComparison.Ordinal) &&
                !chat.Contains("ShipCommandParsing", StringComparison.Ordinal) &&
                chat.Contains("Natural-language action selection belongs", StringComparison.Ordinal),
                "chat must route natural language to Realtime tools instead of phrase parsers");
        Require(!File.Exists(Path.Combine(root, "src", "MovementCommandParsing.cs")) &&
                !File.Exists(Path.Combine(root, "src", "ShipCommandParsing.cs")),
                "legacy natural-language command parsers must stay removed");

        string setupMenuPath = Path.Combine(root, "src", "BuddySetupMenu.cs");
        Require(!File.Exists(setupMenuPath), "the old custom overlay must stay removed");

        string manifestPath = Path.Combine(root, "ThunderstorePackage", "manifest.json");
        string manifest = File.ReadAllText(manifestPath);
        Require(manifest.Contains("willis81808-LethalSettings-1.4.1", StringComparison.Ordinal),
                "Thunderstore package must declare the native settings dependency");

        string spawnerPath = Path.Combine(root, "src", "CrewmateSpawner.cs");
        string spawner = File.ReadAllText(spawnerPath);
        Require(spawner.Contains("IsLandingSettled()", StringComparison.Ordinal) &&
                spawner.Contains("outsideAINodes", StringComparison.Ordinal) &&
                spawner.Contains("CanTalkToBuddy", StringComparison.Ordinal),
                "Buddy must be voice-only in orbit and physically spawn outside after landing settles");

        string clientVoicePath = Path.Combine(root, "src", "BuddyClientVoice.cs");
        string clientVoice = File.ReadAllText(clientVoicePath);
        Require(clientVoice.Contains("inShipPhase == true) return player != null", StringComparison.Ordinal),
                "remote crewmates must be able to use the voice terminal while Buddy has no orbit body");

        string dangerPath = Path.Combine(root, "src", "BuddyDangerCallout.cs");
        string danger = File.ReadAllText(dangerPath);
        Require(danger.Contains("ThreatSeverity", StringComparison.Ordinal) &&
                danger.Contains("ClassifyThreat", StringComparison.Ordinal) &&
                danger.Contains("severity >= ThreatSeverity.High", StringComparison.Ordinal),
                "danger dialogue must scale fear and urgency from the confirmed enemy threat level");

        string workflowPath = Path.Combine(root, ".github", "workflows", "build.yml");
        Require(File.Exists(workflowPath), "release checks must locate the release workflow");
        string workflow = File.ReadAllText(workflowPath);
        Require(workflow.Contains("contents: read", StringComparison.Ordinal) &&
                workflow.Contains("persist-credentials: false", StringComparison.Ordinal) &&
                workflow.Contains("Scan release-branch Git history", StringComparison.Ordinal),
                "build CI must be read-only, discard checkout credentials and scan history");
    }

    /// <summary>
    /// The final stage can spawn hostile creatures, so every gate in front of it is locked here.
    /// </summary>
    private static void RunFinalStageGateChecks()
    {
        foreach (BuddyArcStage stage in Enum.GetValues(typeof(BuddyArcStage)))
            Require(BuddyMalicePolicy.StageAllowsHunting(stage) == (stage == BuddyArcStage.Feral),
                    "only the final Feral stage may ever hunt");

        Require(!BuddyMalicePolicy.CanHunt(BuddyArcStage.Feral, true, false, true, 3, 0, 9999f, 9999f),
                "hostile spawns must stay off without their explicit opt-in");
        Require(!BuddyMalicePolicy.CanHunt(BuddyArcStage.Feral, false, true, true, 3, 0, 9999f, 9999f),
                "hostile spawns must stay off when the slow burn is disabled");
        Require(!BuddyMalicePolicy.CanHunt(BuddyArcStage.Cold, true, true, true, 3, 0, 9999f, 9999f),
                "hostile spawns must stay off before the final stage");
        Require(!BuddyMalicePolicy.CanHunt(BuddyArcStage.Feral, true, true, false, 3, 0, 9999f, 9999f),
                "hostile spawns must stay off in orbit");
        Require(!BuddyMalicePolicy.CanHunt(BuddyArcStage.Feral, true, true, true, 3,
                    BuddyMalicePolicy.MaxHuntsPerRound, 9999f, 9999f),
                "the per-round hunt cap must hold");
        Require(!BuddyMalicePolicy.CanHunt(BuddyArcStage.Feral, true, true, true, 3, 0, 10f, 9999f),
                "hunting must not start immediately after landing");
        Require(!BuddyMalicePolicy.CanHunt(BuddyArcStage.Feral, true, true, true, 3, 0, 9999f, 10f),
                "the interval between hunts must hold");
        Require(BuddyMalicePolicy.CanHunt(BuddyArcStage.Feral, true, true, true, 3, 0, 9999f, 9999f),
                "a fully opted-in final-stage host still gets the feature");

        Require(!BuddyMalicePolicy.IsValidTarget(true, true, 20f), "players inside the ship are never targeted");
        Require(!BuddyMalicePolicy.IsValidTarget(false, false, 20f), "dead players are never targeted");
        Require(!BuddyMalicePolicy.IsValidSpawnDistance(BuddyMalicePolicy.MinSpawnDistance - 1f),
                "nothing may be spawned on top of a player");
        Require(!BuddyMalicePolicy.IsValidSpawnDistance(BuddyMalicePolicy.MaxSpawnDistance + 1f),
                "spawn distance stays bounded");
    }

    /// <summary>Relationship storage must stay small, bounded and non-reversible.</summary>
    private static void RunRelationshipStorageChecks()
    {
        var extreme = new BuddyBond { Trust = 9999, Familiarity = 9999, Friction = 9999 };
        BuddyBond clamped = BuddyRelationshipModel.Sanitize(extreme);
        Require(clamped.Trust == BuddyRelationshipModel.MaxTrust &&
                clamped.Familiarity == BuddyRelationshipModel.MaxFamiliarity &&
                clamped.Friction == BuddyRelationshipModel.MaxFriction,
                "bond values must clamp to their documented bounds");

        var bond = new BuddyBond();
        for (int i = 0; i < 500; i++)
        {
            foreach (BuddyRelationEvent kind in Enum.GetValues(typeof(BuddyRelationEvent)))
                bond = BuddyRelationshipModel.Apply(bond, kind);
            Require(bond.Trust >= BuddyRelationshipModel.MinTrust && bond.Trust <= BuddyRelationshipModel.MaxTrust,
                    "repeated events must never push trust out of range");
            Require(bond.Familiarity >= 0 && bond.Familiarity <= BuddyRelationshipModel.MaxFamiliarity,
                    "repeated events must never push familiarity out of range");
            Require(bond.Friction >= 0 && bond.Friction <= BuddyRelationshipModel.MaxFriction,
                    "repeated events must never push friction out of range");
        }

        BuddyBond sample = BuddyRelationshipModel.Sanitize(
            new BuddyBond { Trust = -37, Familiarity = 61, Friction = 12 });
        BuddyBond round = BuddyRelationshipModel.Unpack(BuddyRelationshipModel.Pack(sample));
        Require(round.Trust == sample.Trust && round.Familiarity == sample.Familiarity && round.Friction == sample.Friction,
                "packed bonds must survive a save/load round trip");
        Require(BuddyRelationshipModel.Unpack(-1).IsBlank, "corrupt stored bonds must decode to nothing");

        // The digest is what reaches disk: it must be tiny and must not echo the name back.
        uint digest = BuddyRelationshipModel.IdentityDigest("SomePlayerName");
        Require(digest <= 0xFFFF, "the stored identity digest must stay 16-bit");
        Require(BuddyRelationshipModel.IdentityDigest("someplayername") == digest,
                "the identity digest must be stable across capitalisation");
        Require(BuddyRelationshipModel.MaxTrackedPlayers <= 8, "the tracked-player cap must stay small");

        string line = BuddyRelationshipModel.PromptLine(new string('x', 200), sample);
        Require(line.IndexOf(new string('x', 40), StringComparison.Ordinal) < 0,
                "player-supplied names must be truncated before reaching the prompt");
    }

    /// <summary>Turn-taking and pacing must never gag a real danger callout.</summary>
    private static void RunSocialAndPacingChecks()
    {
        Require(!BuddySocialPolicy.ShouldWaitForTurn(BuddySpeechReason.Danger, 0f, 4),
                "confirmed danger must always be allowed to cut in");
        Require(BuddySocialPolicy.ShouldWaitForTurn(BuddySpeechReason.Unprompted, 1f, 3),
                "unprompted chatter must wait while humans are talking");
        Require(!BuddySocialPolicy.ShouldWaitForTurn(BuddySpeechReason.DirectlyAddressed, 5f, 3),
                "a direct question must still get an answer");

        BuddyPacingPlan danger = BuddyPacingPolicy.Plan(
            BuddyArcStage.Feral, BuddyPacingPolicy.MaxTension, 0f, 0f);
        Require(danger.ExtraSilenceSeconds <= 0f && danger.Presence == BuddyPresence.Normal,
                "high tension must drop staged horror behaviour instead of adding it");
        Require(danger.DialogueDensity >= 1, "Buddy must not be silenced during confirmed danger");

        BuddyPacingPlan ordinary = BuddyPacingPolicy.Plan(BuddyArcStage.Coworker, 0, 999f, 999f);
        Require(ordinary.Presence == BuddyPresence.Normal && ordinary.FollowDistanceScale == 1f,
                "the ordinary coworker stage must not stare or crowd anyone");

        for (int tension = 0; tension <= BuddyPacingPolicy.MaxTension; tension += 5)
        {
            foreach (BuddyArcStage stage in Enum.GetValues(typeof(BuddyArcStage)))
            {
                BuddyPacingPlan plan = BuddyPacingPolicy.Plan(stage, tension, 300f, 300f);
                Require(plan.FollowDistanceScale > 0f && plan.FollowDistanceScale <= 1f,
                        "follow spacing must never invert or grow without bound");
                Require(plan.ExtraSilenceSeconds >= 0f && plan.ExtraSilenceSeconds <= 120f,
                        "enforced silence must stay bounded");
            }
        }

        Require(BuddyPacingPolicy.Tension(9, true, true, 0f, true, 0) <= BuddyPacingPolicy.MaxTension,
                "tension must saturate rather than overflow");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Security regression check failed: " + message);
    }
}
