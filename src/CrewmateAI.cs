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
        private const float FollowDistance = 3.8f;
        private const float FollowResumeDistance = 5.4f;
        private const float PickupRange = 2f;
        private const float ShipDropRange = 4f;
        private const float AgentSpeed = 5.0f;
        private const float AiTickInterval = 0.12f;

        private static float _nextAiTick;

        public static void HostUpdate()
        {
            if (!CrewmateSpawner.IsHost()) return;

            // CRITICAL: vanilla DoAIInterval is driven from Masked.Update, which we skip.
            // Drive our own AI tick here so Buddy actually moves / fetches.
            bool runAi = Time.time >= _nextAiTick;
            if (runAi)
                _nextAiTick = Time.time + AiTickInterval;

            foreach (var data in CrewmateRegistry.All)
            {
                try
                {
                    if (data?.Enemy == null) continue;
                    if (data.Enemy.isEnemyDead) continue;

                    CrewmateRegistry.EnsureNetworkKey(data);
                    SyncHeldItemVisual(data);

                    if (runAi)
                        DoAIInterval(data.Enemy);

                    // Every frame: keep agent moving + simple transform fallback
                    DriveMovementFrame(data);
                    MaybeObserve(data);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError($"HostUpdate crewmate: {ex}");
                }
            }
        }

        private static void DriveMovementFrame(CrewmateData data)
        {
            var enemy = data.Enemy;
            if (enemy == null) return;
            EnsureAgent(enemy);

            if (enemy.agent != null && enemy.agent.enabled && enemy.agent.isOnNavMesh)
            {
                if (enemy.moveTowardsDestination && !enemy.agent.isStopped)
                {
                    // NavMeshAgent drives position; nudge sync fields
                    enemy.moveTowardsDestination = true;
                }
                return;
            }

            // Fallback: no navmesh (rare) — walk transform toward destination
            if (!enemy.moveTowardsDestination) return;
            if (data.ManualDestination == Vector3.zero) return;

            Vector3 pos = enemy.transform.position;
            Vector3 dest = data.ManualDestination;
            dest.y = pos.y;
            float step = AgentSpeed * Time.deltaTime;
            enemy.transform.position = Vector3.MoveTowards(pos, dest, step);
            Vector3 look = dest - pos;
            look.y = 0f;
            if (look.sqrMagnitude > 0.01f)
                enemy.transform.rotation = Quaternion.Slerp(
                    enemy.transform.rotation,
                    Quaternion.LookRotation(look.normalized),
                    10f * Time.deltaTime);
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
                    if (CrewmateSpawner.IsHost()) EnsureAgent(enemy);
                    return;
                }

                SyncHeldItemVisual(data);
                if (!CrewmateSpawner.IsHost())
                {
                    if (enemy.agent != null && enemy.agent.enabled)
                    {
                        try
                        {
                            if (enemy.agent.isOnNavMesh) enemy.agent.isStopped = true;
                            enemy.agent.enabled = false;
                        }
                        catch { }
                    }
                    return;
                }
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
                enemy.agent.stoppingDistance = 2.2f;
                enemy.agent.acceleration = 12f;
                enemy.agent.angularSpeed = 360f;
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

            // Facility / outside are separate navmeshes — warp through when owner changes area
            SyncAreaWithOwner(data, target);

            float dist = Vector3.Distance(enemy.transform.position, target.transform.position);
            // Hysteresis: stop inside FollowDistance, only resume after FollowResumeDistance
            // so he doesn't micro-adjust into your personal space
            if (dist <= FollowDistance)
            {
                StopMoving(enemy);
                return;
            }
            if (dist < FollowResumeDistance && !enemy.moveTowardsDestination)
            {
                // already stopped, not far enough to bother walking again
                return;
            }

            // Far separation (owner teleported / entrance) — hard follow
            if (dist > 42f && Time.time >= data.NextAreaTeleportAt)
            {
                TeleportBesidePlayer(enemy, target, enemy.isOutside);
                data.NextAreaTeleportAt = Time.time + 1.5f;
                return;
            }

            // Walk toward a point offset behind the player, not into their feet
            Vector3 followPoint = target.transform.position
                                  - target.transform.forward * 2.25f
                                  + target.transform.right * data.FollowSideOffset;
            MoveTo(enemy, followPoint);
        }

        /// <summary>
        /// LC indoor/outdoor are different NavMeshes. Match owner's factory/exterior state
        /// via Masked SetEnemyOutside + teleport next to them.
        /// </summary>
        private static void SyncAreaWithOwner(CrewmateData data, PlayerControllerB owner)
        {
            if (data?.Enemy == null || owner == null) return;
            if (Time.time < data.NextAreaTeleportAt) return;

            try
            {
                var enemy = data.Enemy;
                bool ownerInFactory = owner.isInsideFactory;
                bool buddyOutside = enemy.isOutside;

                // Owner entered complex — Buddy still outside
                if (ownerInFactory && buddyOutside)
                {
                    Plugin.Log?.LogInfo("Buddy following into facility…");
                    TeleportBesidePlayer(enemy, owner, setOutside: false);
                    data.NextAreaTeleportAt = Time.time + 2f;
                    return;
                }

                // Owner left complex to exterior — Buddy still inside
                if (!ownerInFactory && !buddyOutside && !owner.isInHangarShipRoom)
                {
                    Plugin.Log?.LogInfo("Buddy following out of facility…");
                    TeleportBesidePlayer(enemy, owner, setOutside: true);
                    data.NextAreaTeleportAt = Time.time + 2f;
                    return;
                }

                // Owner on ship, Buddy still deep in facility / far away
                if (owner.isInHangarShipRoom && !buddyOutside)
                {
                    float d = Vector3.Distance(enemy.transform.position, owner.transform.position);
                    if (d > 35f)
                    {
                        Plugin.Log?.LogInfo("Buddy warping to ship with owner…");
                        TeleportBesidePlayer(enemy, owner, setOutside: false);
                        data.NextAreaTeleportAt = Time.time + 2f;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"SyncAreaWithOwner: {ex.Message}");
            }
        }

        private static void TeleportBesidePlayer(MaskedPlayerEnemy enemy, PlayerControllerB owner, bool setOutside)
        {
            if (enemy == null || owner == null) return;
            try
            {
                Vector3 dest = owner.transform.position
                               + owner.transform.right * 1.1f
                               + owner.transform.forward * -0.4f
                               + Vector3.up * 0.1f;

                if (NavMesh.SamplePosition(dest, out var hit, 10f, NavMesh.AllAreas))
                    dest = hit.position;
                else if (NavMesh.SamplePosition(owner.transform.position, out hit, 12f, NavMesh.AllAreas))
                    dest = hit.position;

                // Preferred Masked API (syncs to clients)
                bool teleported = false;
                try
                {
                    enemy.TeleportMaskedEnemyAndSync(dest, setOutside);
                    teleported = true;
                }
                catch
                {
                    try
                    {
                        enemy.TeleportMaskedEnemy(dest, setOutside);
                        teleported = true;
                    }
                    catch
                    {
                        try { enemy.SetEnemyOutside(setOutside); } catch { /* ignore */ }
                    }
                }

                if (!teleported)
                {
                    try { enemy.SetEnemyOutside(setOutside); } catch { /* ignore */ }
                    enemy.transform.position = dest;
                    if (enemy.agent != null)
                    {
                        enemy.agent.enabled = true;
                        try { enemy.agent.Warp(dest); } catch { /* ignore */ }
                    }
                }

                try { enemy.SyncPositionToClients(); } catch { /* ignore */ }

                // Face owner
                var look = owner.transform.position - enemy.transform.position;
                look.y = 0f;
                if (look.sqrMagnitude > 0.01f)
                    enemy.transform.rotation = Quaternion.LookRotation(look.normalized);

                Plugin.Log?.LogInfo($"Buddy teleported beside owner (outside={setOutside}) at {dest}");
                if (CrewmateRegistry.TryGet(enemy, out var data) && data != null)
                    BuddyPoseSync.SendImmediate(data);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"TeleportBesidePlayer: {ex}");
            }
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
                if (CrewmateRegistry.TryGet(enemy, out var data) && data != null)
                    data.ManualDestination = worldPos;

                enemy.moveTowardsDestination = true;
                enemy.movingTowardsTargetPlayer = false;
                enemy.targetPlayer = null;

                EnsureAgent(enemy);

                // Prefer snapping dest onto navmesh
                Vector3 dest = worldPos;
                if (NavMesh.SamplePosition(worldPos, out var hit, 8f, NavMesh.AllAreas))
                    dest = hit.position;

                try { enemy.SetDestinationToPosition(dest, checkForPath: false); }
                catch { /* ignore */ }

                if (enemy.agent != null)
                {
                    if (!enemy.agent.enabled) enemy.agent.enabled = true;
                    if (!enemy.agent.isOnNavMesh)
                    {
                        if (NavMesh.SamplePosition(enemy.transform.position, out var here, 12f, NavMesh.AllAreas))
                            enemy.agent.Warp(here.position);
                    }
                    if (enemy.agent.isOnNavMesh)
                    {
                        enemy.agent.isStopped = false;
                        enemy.agent.speed = AgentSpeed;
                        enemy.agent.SetDestination(dest);
                    }
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
                if (CrewmateRegistry.TryGet(enemy, out var data) && data != null)
                    data.ManualDestination = Vector3.zero;

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
                // isPlayerControlled can be false for host in some states — only require alive
                if (data.Owner != null && !data.Owner.isPlayerDead)
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

        internal static bool RecoverStalled(CrewmateData data)
        {
            if (data?.Enemy == null) return false;
            try
            {
                if (data.State == CrewmateState.FollowOwner)
                {
                    var owner = GetFollowTarget(data);
                    if (owner == null) return false;
                    bool outside = !owner.isInsideFactory && !owner.isInHangarShipRoom;
                    TeleportBesidePlayer(data.Enemy, owner, outside);
                    return true;
                }

                if (data.State == CrewmateState.ReturnToShip || data.HeldItem != null)
                {
                    TeleportToPosition(data, GetShipDropPosition(), false, "return-to-ship stall");
                    return true;
                }

                if (data.State == CrewmateState.FetchScrap && data.FetchTarget != null)
                {
                    bool outside = data.Enemy.isOutside;
                    try { outside = !data.FetchTarget.isInFactory; } catch { }
                    TeleportToPosition(data, data.FetchTarget.transform.position, outside, "fetch stall");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"RecoverStalled: {ex.Message}");
            }
            return false;
        }

        private static void TeleportToPosition(CrewmateData data, Vector3 destination, bool outside, string reason)
        {
            var enemy = data.Enemy;
            if (NavMesh.SamplePosition(destination, out var hit, 12f, NavMesh.AllAreas))
                destination = hit.position;
            try { enemy.TeleportMaskedEnemyAndSync(destination, outside); }
            catch
            {
                try { enemy.TeleportMaskedEnemy(destination, outside); }
                catch
                {
                    try { enemy.SetEnemyOutside(outside); } catch { }
                    enemy.transform.position = destination;
                }
            }
            enemy.isOutside = outside;
            data.ManualDestination = destination;
            BuddyPoseSync.SendImmediate(data);
            Plugin.Log?.LogWarning($"Buddy safe teleport recovery reason={reason} outside={outside} position={destination}.");
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

        public static void ApplyCommandFromChat(string message, int requestingPlayerId = -1)
        {
            if (string.IsNullOrEmpty(message)) return;
            var data = CrewmateRegistry.GetPrimary();
            if (data == null)
            {
                Plugin.Log?.LogWarning("Command ignored: no crewmate registered.");
                return;
            }

            var lower = message.ToLowerInvariant();
            if (lower.Contains("follow") || lower.Contains("come") || lower == "here")
            {
                var players = StartOfRound.Instance?.allPlayerScripts;
                if (players != null && requestingPlayerId >= 0 && requestingPlayerId < players.Length)
                {
                    var requester = players[requestingPlayerId];
                    if (requester != null && !requester.isPlayerDead)
                    {
                        data.Owner = requester;
                        Plugin.Log?.LogInfo($"Buddy follow owner -> '{requester.playerUsername}' (playerId={requestingPlayerId}).");
                    }
                }
                ApplyCommand(data, "FOLLOW");
            }
            else if (lower.Contains("stay") || lower.Contains("wait") || lower.Contains("stop"))
                ApplyCommand(data, "STAY");
            else if (lower.Contains("ship") || lower.Contains("go home") || lower.Contains("return"))
                ApplyCommand(data, "SHIP");
            else if (lower.Contains("fetch") || lower.Contains("collect") || lower.Contains("scrap") || lower.Contains("loot"))
                ApplyCommand(data, "FETCH");
            else
                Plugin.Log?.LogInfo($"No command keyword in: '{message}'");
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
