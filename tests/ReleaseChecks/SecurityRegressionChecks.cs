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

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Security regression check failed: " + message);
    }
}
