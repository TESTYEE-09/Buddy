using System;
using System.IO;
using System.Runtime.CompilerServices;
using LethalAICrewmate;

internal static class SecurityRegressionChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        Require(RemoteActionAuthorization.ShouldBlockUnauthenticatedTextRequest(
                    "buy 3 flashlights", LobbyVisibility.Public, publicLobbyOptIn: false),
                "public lobby must block unauthenticated state-changing text");
        Require(RemoteActionAuthorization.ShouldBlockUnauthenticatedTextRequest(
                    "route titan", LobbyVisibility.Unknown, publicLobbyOptIn: false),
                "unknown lobby must fail closed for unauthenticated state-changing text");
        Require(!RemoteActionAuthorization.ShouldBlockUnauthenticatedTextRequest(
                    "status", LobbyVisibility.Public, publicLobbyOptIn: false),
                "read-only status requests remain available in public lobbies");
        Require(!RemoteActionAuthorization.ShouldBlockUnauthenticatedTextRequest(
                    "buy 1 flashlight", LobbyVisibility.Friends, publicLobbyOptIn: false),
                "verified friends lobby keeps intended remote actions");
        Require(!RemoteActionAuthorization.ShouldBlockUnauthenticatedTextRequest(
                    "buy 1 flashlight", LobbyVisibility.Public, publicLobbyOptIn: true),
                "explicit public-lobby opt-in permits remote actions");
        Require(RemoteActionAuthorization.AllowsStateChangingRequest(
                    LobbyVisibility.Public, publicLobbyOptIn: false, trustedInternalHostRequest: true),
                "trusted internal host requests remain authoritative");

        RunFinalStageGateChecks();
        RunRelationshipStorageChecks();
        RunSocialAndPacingChecks();

        string root = Directory.GetCurrentDirectory();
        string pluginPath = Path.Combine(root, "src", "Plugin.cs");
        if (File.Exists(pluginPath))
        {
            string plugin = File.ReadAllText(pluginPath);
            Require(plugin.Contains("ClearLegacyPlaintextKey(false);", StringComparison.Ordinal) &&
                    plugin.Contains("ClearLegacyPlaintextKey(true);", StringComparison.Ordinal),
                    "legacy provider keys must be deleted from plaintext config after import");
            Require(plugin.Contains("RemoveObsoleteConfigEntries(removeLegacyGroqKey: true, removeLegacyOpenAiKey: true);", StringComparison.Ordinal),
                    "obsolete plaintext provider key definitions must always be removed");
        }
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
