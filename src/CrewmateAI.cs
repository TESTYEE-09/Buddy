using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace LethalAICrewmate
{
    public static class CrewmateAI
    {
        private const float FollowDistance = 3f;
        private const float PickupRange = 2f;
        private const float ShipDropRange = 4f;
        private const float AgentSpeed = 5.5f;

        public static void HostUpdate()
        {
            if (!CrewmateSpawner.IsHost()) return;

            foreach (var data in CrewmateRegistry.All)
            {
                try
                {
                    if (data?.Enemy == null) continue;
                    CrewmateRegistry.EnsureNetworkKey(data);
                    SyncHeldItemVisual(data);
                    MaybeObserve(data);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError($"HostUpdate crewmate: {ex}");
                }
            }
        }

        public static void DoAIInterval(MaskedPlayerEnemy enemy)
        {
            if (enemy == null) return;
            if (!CrewmateRegistry.TryGet(enemy, out var data)) return;
            if (!CrewmateSpawner.IsHost()) return;
            if (enemy.isEnemyDead) return;

            try
            {
                if (!data.Neutralized)
                    MaskedNeutralizePatches.Neutralize(enemy, data);

                EnsureAgent(enemy);

                switch (data.State)
                {
                    case CrewmateState.FollowOwner:
                        TickFollow(data);
                        break;
                    case CrewmateState.Stay:
                        TickStay(data);
                        break;
                    case CrewmateState.ReturnToShip:
                        TickReturnToShip(data, dropItem: false);
                        break;
                    case CrewmateState.FetchScrap:
                        TickFetchScrap(data);
                        break;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"DoAIInterval: {ex}");
                try
                {
                    if (data.HeldItem != null)
                        DropHeldItem(data, enemy.transform.position);
                    CrewmateRegistry.SetState(data, CrewmateState.FollowOwner);
                }
                catch { /* crash-proof */ }
            }
        }

        public static void CrewmateUpdate(MaskedPlayerEnemy enemy)
        {
            // Client + host: keep held scrap parented, suppress hostility flags, keep agent alive
            try
            {
                if (enemy == null) return;

                // Always clear kill/chase flags so a partial vanilla path can't re-arm
                enemy.targetPlayer = null;
                enemy.movingTowardsTargetPlayer = false;
                enemy.inKillAnimation = false;
                enemy.mimickingPlayer = null;

                if (!CrewmateRegistry.TryGet(enemy, out var data))
                {
                    // Known net-id only (client before full register) — still maintain agent if present
                    EnsureAgent(enemy);
                    return;
                }

                SyncHeldItemVisual(data);
                EnsureAgent(enemy);

                // Drive agent along destination when host AI set moveTowardsDestination
                if (CrewmateSpawner.IsHost() && enemy.agent != null && enemy.agent.isOnNavMesh)
                {
                    if (enemy.moveTowardsDestination && !enemy.agent.pathPending)
                    {
                        enemy.agent.isStopped = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"CrewmateUpdate: {ex}");
            }
        }

        private static void EnsureAgent(MaskedPlayerEnemy enemy)
        {
            if (enemy.agent == null) return;
            try
            {
                if (!enemy.agent.enabled) enemy.agent.enabled = true;
                enemy.agent.speed = AgentSpeed;
                enemy.agent.stoppingDistance = 0.5f;
                if (!enemy.agent.isOnNavMesh)
                {
                    if (NavMesh.SamplePosition(enemy.transform.position, out var hit, 10f, NavMesh.AllAreas))
                    {
                        enemy.agent.Warp(hit.position);
                    }
                }
            }
            catch
            {
                // ignore agent issues
            }
        }

        private static void TickFollow(CrewmateData data)
        {
            var enemy = data.Enemy;
            var target = GetFollowTarget(data);
            if (target == null)
            {
                StopMoving(enemy);
                return;
            }

            data.Owner = target;
            float dist = Vector3.Distance(enemy.transform.position, target.transform.position);
            if (dist <= FollowDistance)
            {
                StopMoving(enemy);
                return;
            }

            MoveTo(enemy, target.transform.position);
        }

        private static void TickStay(CrewmateData data)
        {
            var enemy = data.Enemy;
            float dist = Vector3.Distance(enemy.transform.position, data.StayPosition);
            if (dist > 1.5f)
                MoveTo(enemy, data.StayPosition);
            else
                StopMoving(enemy);
        }

        private static void TickReturnToShip(CrewmateData data, bool dropItem)
        {
            var enemy = data.Enemy;
            var shipPos = GetShipDropPosition();
            float dist = Vector3.Distance(enemy.transform.position, shipPos);
            if (dist <= ShipDropRange)
            {
                StopMoving(enemy);
                if (dropItem && data.HeldItem != null)
                    DropHeldItem(data, shipPos);
                CrewmateRegistry.SetState(data, CrewmateState.FollowOwner);
                return;
            }
            MoveTo(enemy, shipPos);
        }

        private static void TickFetchScrap(CrewmateData data)
        {
            var enemy = data.Enemy;

            // Already holding scrap -> deliver to ship
            if (data.HeldItem != null)
            {
                TickReturnToShip(data, dropItem: true);
                return;
            }

            // Acquire target
            if (data.FetchTarget == null || !IsValidScrap(data.FetchTarget))
            {
                data.FetchTarget = FindNearestScrap(enemy.transform.position);
                if (data.FetchTarget == null)
                {
                    Plugin.Log?.LogInfo("No scrap found for fetch; returning to follow.");
                    CrewmateRegistry.SetState(data, CrewmateState.FollowOwner);
                    return;
                }
            }

            float dist = Vector3.Distance(enemy.transform.position, data.FetchTarget.transform.position);
            if (dist <= PickupRange)
            {
                PickUpItem(data, data.FetchTarget);
                data.FetchTarget = null;
                return;
            }

            MoveTo(enemy, data.FetchTarget.transform.position);
        }

        private static void MoveTo(MaskedPlayerEnemy enemy, Vector3 worldPos)
        {
            try
            {
                enemy.moveTowardsDestination = true;
                enemy.movingTowardsTargetPlayer = false;
                enemy.targetPlayer = null;
                enemy.SetDestinationToPosition(worldPos, checkForPath: false);
                if (enemy.agent != null && enemy.agent.isOnNavMesh)
                {
                    enemy.agent.isStopped = false;
                    enemy.agent.SetDestination(worldPos);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"MoveTo failed: {ex.Message}");
            }
        }

        private static void StopMoving(MaskedPlayerEnemy enemy)
        {
            try
            {
                enemy.moveTowardsDestination = false;
                enemy.movingTowardsTargetPlayer = false;
                if (enemy.agent != null && enemy.agent.isOnNavMesh)
                {
                    enemy.agent.isStopped = true;
                    enemy.agent.ResetPath();
                }
            }
            catch
            {
                // ignore
            }
        }

        private static PlayerControllerB GetFollowTarget(CrewmateData data)
        {
            try
            {
                if (data.Owner != null && !data.Owner.isPlayerDead && data.Owner.isPlayerControlled)
                    return data.Owner;

                // Nearest living player
                var sor = StartOfRound.Instance;
                if (sor?.allPlayerScripts == null || data.Enemy == null) return null;

                PlayerControllerB best = null;
                float bestDist = float.MaxValue;
                foreach (var p in sor.allPlayerScripts)
                {
                    if (p == null || p.isPlayerDead) continue;
                    float d = Vector3.Distance(data.Enemy.transform.position, p.transform.position);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = p;
                    }
                }
                return best;
            }
            catch
            {
                return data.Owner;
            }
        }

        private static Vector3 GetShipDropPosition()
        {
            try
            {
                var sor = StartOfRound.Instance;
                if (sor?.middleOfShipNode != null)
                    return sor.middleOfShipNode.position;
                if (sor?.insideShipPositions != null && sor.insideShipPositions.Length > 0 && sor.insideShipPositions[0] != null)
                    return sor.insideShipPositions[0].position;
                if (sor?.shipBounds != null)
                    return sor.shipBounds.bounds.center;
            }
            catch
            {
                // ignore
            }
            return Vector3.zero;
        }

        private static bool IsValidScrap(GrabbableObject item)
        {
            if (item == null) return false;
            try
            {
                if (item.deactivated) return false;
                if (item.isHeld || item.isHeldByEnemy || item.heldByPlayerOnServer) return false;
                if (item.itemProperties == null || !item.itemProperties.isScrap) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static GrabbableObject FindNearestScrap(Vector3 from)
        {
            GrabbableObject best = null;
            float bestDist = float.MaxValue;
            try
            {
                var items = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
                foreach (var item in items)
                {
                    if (!IsValidScrap(item)) continue;
                    // Prefer scrap not already in ship
                    if (item.isInShipRoom) continue;
                    float d = Vector3.Distance(from, item.transform.position);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = item;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"FindNearestScrap: {ex.Message}");
            }
            return best;
        }

        public static void PickUpItem(CrewmateData data, GrabbableObject item)
        {
            if (data?.Enemy == null || item == null) return;
            try
            {
                if (data.HeldItem != null)
                    DropHeldItem(data, data.Enemy.transform.position);

                item.isHeldByEnemy = true;
                item.grabbable = false;
                try { item.GrabItemFromEnemy(data.Enemy); } catch { /* may not run fully without ownership */ }
                try { item.EnablePhysics(false); } catch { /* ignore */ }

                // Disable colliders / hide mesh slightly by parenting
                try
                {
                    item.transform.SetParent(data.Enemy.transform, true);
                    item.transform.localPosition = new Vector3(0f, 1.2f, 0.6f);
                }
                catch { /* ignore */ }

                data.HeldItem = item;

                ulong crewId = data.NetworkObjectId;
                ulong itemId = 0;
                try { if (item.IsSpawned) itemId = item.NetworkObjectId; } catch { /* ignore */ }
                NetMessenger.BroadcastItemAttach(crewId, itemId, attached: true);

                Plugin.Log?.LogInfo($"Crewmate picked up scrap '{item.itemProperties?.itemName}'");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"PickUpItem: {ex}");
                try { DropHeldItem(data, data.Enemy.transform.position); } catch { /* ignore */ }
            }
        }

        public static void DropHeldItem(CrewmateData data, Vector3 dropPos)
        {
            if (data == null) return;
            var item = data.HeldItem;
            data.HeldItem = null;
            if (item == null) return;

            try
            {
                try { item.DiscardItemFromEnemy(); } catch { /* ignore */ }
                item.isHeldByEnemy = false;
                item.grabbable = true;
                item.isHeld = false;

                try { item.transform.SetParent(null, true); } catch { /* ignore */ }
                item.transform.position = dropPos + Vector3.up * 0.2f;
                item.targetFloorPosition = item.transform.position;
                item.startFallingPosition = item.transform.position;

                // If near ship, mark as collected
                bool inShip = false;
                try
                {
                    var sor = StartOfRound.Instance;
                    if (sor?.shipInnerRoomBounds != null)
                        inShip = sor.shipInnerRoomBounds.bounds.Contains(dropPos);
                    else if (sor?.shipBounds != null)
                        inShip = sor.shipBounds.bounds.Contains(dropPos);
                }
                catch { /* ignore */ }

                if (inShip)
                {
                    item.isInShipRoom = true;
                    item.isInElevator = true;
                    try
                    {
                        // Avoid double-counting if a player later grabs the same scrap this round
                        int instId = item.GetInstanceID();
                        bool alreadyCounted = data.ScrapCountedInstanceIds.Contains(instId);
                        if (!alreadyCounted)
                        {
                            RoundManager.Instance?.CollectNewScrapForThisRound(item);
                            data.ScrapCountedInstanceIds.Add(instId);
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning($"CollectNewScrapForThisRound: {ex.Message}");
                    }
                }

                try { item.EnablePhysics(true); } catch { /* ignore */ }
                try { item.FallToGround(false, false, item.transform.position); } catch { /* ignore */ }

                ulong crewId = data.NetworkObjectId;
                ulong itemId = 0;
                try { if (item.IsSpawned) itemId = item.NetworkObjectId; } catch { /* ignore */ }
                NetMessenger.BroadcastItemAttach(crewId, itemId, attached: false);

                Plugin.Log?.LogInfo($"Crewmate dropped scrap (inShip={inShip})");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"DropHeldItem: {ex}");
            }
        }

        private static void SyncHeldItemVisual(CrewmateData data)
        {
            if (data?.HeldItem == null || data.Enemy == null) return;
            try
            {
                var item = data.HeldItem;
                if (item.transform.parent != data.Enemy.transform)
                    item.transform.SetParent(data.Enemy.transform, true);
                item.transform.localPosition = new Vector3(0f, 1.2f, 0.6f);
                item.transform.localRotation = Quaternion.identity;
            }
            catch
            {
                // ignore
            }
        }

        private static void MaybeObserve(CrewmateData data)
        {
            try
            {
                float interval = Plugin.ObservationIntervalSeconds?.Value ?? 0f;
                if (interval <= 0f) return;
                if (Time.time < data.NextObservationAt) return;

                // Jitter next observation between interval and interval*2 (e.g. 45-90 when set ~45)
                data.NextObservationAt = Time.time + interval + UnityEngine.Random.Range(0f, Mathf.Max(1f, interval));

                string summary = BuildObservationSummary(data);
                LlmClient.EnqueueObservation(summary);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"MaybeObserve: {ex.Message}");
            }
        }

        public static string BuildObservationSummary(CrewmateData data)
        {
            try
            {
                string planet = StartOfRound.Instance?.currentLevel?.PlanetName ?? "unknown moon";
                string time = "unknown time";
                try
                {
                    if (TimeOfDay.Instance != null)
                        time = $"{TimeOfDay.Instance.dayMode} (hour {TimeOfDay.Instance.hour})";
                }
                catch { /* ignore */ }

                int nearbyEnemies = 0;
                int nearbyScrap = 0;
                var pos = data.Enemy != null ? data.Enemy.transform.position : Vector3.zero;

                try
                {
                    foreach (var e in UnityEngine.Object.FindObjectsOfType<EnemyAI>())
                    {
                        if (e == null || e.isEnemyDead) continue;
                        if (CrewmateRegistry.IsCrewmate(e)) continue;
                        if (Vector3.Distance(pos, e.transform.position) <= 20f)
                            nearbyEnemies++;
                    }
                }
                catch { /* ignore */ }

                try
                {
                    foreach (var g in UnityEngine.Object.FindObjectsOfType<GrabbableObject>())
                    {
                        if (!IsValidScrap(g)) continue;
                        if (Vector3.Distance(pos, g.transform.position) <= 20f)
                            nearbyScrap++;
                    }
                }
                catch { /* ignore */ }

                int shipScrap = 0;
                try
                {
                    if (RoundManager.Instance != null)
                        shipScrap = RoundManager.Instance.valueOfFoundScrapItems;
                }
                catch { /* ignore */ }

                return $"Planet: {planet}. Time: {time}. Nearby enemies (20m): {nearbyEnemies}. Nearby scrap (20m): {nearbyScrap}. Ship scrap value: {shipScrap}. Make a short in-character remark.";
            }
            catch (Exception ex)
            {
                return $"Situation unclear ({ex.Message}). Remark briefly.";
            }
        }

        public static void ApplyCommand(CrewmateData data, string command)
        {
            if (data == null || string.IsNullOrEmpty(command)) return;
            switch (command.ToUpperInvariant())
            {
                case "FOLLOW":
                    CrewmateRegistry.SetState(data, CrewmateState.FollowOwner);
                    break;
                case "STAY":
                    CrewmateRegistry.SetState(data, CrewmateState.Stay);
                    break;
                case "SHIP":
                    CrewmateRegistry.SetState(data, CrewmateState.ReturnToShip);
                    break;
                case "FETCH":
                    CrewmateRegistry.SetState(data, CrewmateState.FetchScrap);
                    break;
            }
        }

        public static void ApplyCommandFromChat(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            var data = CrewmateRegistry.GetPrimary();
            if (data == null) return;

            var lower = message.ToLowerInvariant();
            if (lower.Contains("follow"))
                ApplyCommand(data, "FOLLOW");
            else if (lower.Contains("stay") || lower.Contains("wait") || lower.Contains("stop"))
                ApplyCommand(data, "STAY");
            else if (lower.Contains("ship") || lower.Contains("go home") || lower.Contains("return"))
                ApplyCommand(data, "SHIP");
            else if (lower.Contains("fetch") || lower.Contains("collect") || lower.Contains("scrap") || lower.Contains("loot"))
                ApplyCommand(data, "FETCH");
        }

        /// <summary>Client-side attach/detach mirror for held scrap visuals.</summary>
        public static void ClientAttachItem(ulong crewmateNetId, ulong itemNetId, bool attached)
        {
            try
            {
                MaskedPlayerEnemy enemy = FindCrewmateByNetId(crewmateNetId);
                GrabbableObject item = FindItemByNetId(itemNetId);
                if (enemy == null || item == null) return;

                if (attached)
                {
                    item.isHeldByEnemy = true;
                    try { item.EnablePhysics(false); } catch { /* ignore */ }
                    item.transform.SetParent(enemy.transform, true);
                    item.transform.localPosition = new Vector3(0f, 1.2f, 0.6f);
                    if (CrewmateRegistry.TryGet(enemy, out var data))
                        data.HeldItem = item;
                }
                else
                {
                    item.isHeldByEnemy = false;
                    try { item.transform.SetParent(null, true); } catch { /* ignore */ }
                    try { item.EnablePhysics(true); } catch { /* ignore */ }
                    if (CrewmateRegistry.TryGet(enemy, out var data) && data.HeldItem == item)
                        data.HeldItem = null;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"ClientAttachItem: {ex.Message}");
            }
        }

        private static MaskedPlayerEnemy FindCrewmateByNetId(ulong id)
        {
            try
            {
                foreach (var m in UnityEngine.Object.FindObjectsOfType<MaskedPlayerEnemy>())
                {
                    if (m != null && m.IsSpawned && m.NetworkObjectId == id)
                        return m;
                }
            }
            catch { /* ignore */ }
            return null;
        }

        private static GrabbableObject FindItemByNetId(ulong id)
        {
            if (id == 0) return null;
            try
            {
                foreach (var g in UnityEngine.Object.FindObjectsOfType<GrabbableObject>())
                {
                    if (g != null && g.IsSpawned && g.NetworkObjectId == id)
                        return g;
                }
            }
            catch { /* ignore */ }
            return null;
        }
    }
}
