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
        private const float PickupRange = 2f;
        private const float ShipDropRange = 4f;
        private const float MinScoutDistance = 4f;
        private const float MaxScoutDistance = 18f;
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

            bool visiblyMoving = enemy.moveTowardsDestination;
            try
            {
                visiblyMoving = visiblyMoving && enemy.agent != null && enemy.agent.enabled &&
                                  enemy.agent.isOnNavMesh && enemy.agent.velocity.sqrMagnitude > 0.04f;
            }
            catch { }
            BuddyAnimation.Apply(enemy, visiblyMoving);

            if (enemy.agent != null && enemy.agent.enabled && enemy.agent.isOnNavMesh)
            {
                if (enemy.moveTowardsDestination && !enemy.agent.isStopped)
                {
                    // NavMeshAgent drives position; nudge sync fields
                    enemy.moveTowardsDestination = true;
                }
                return;
            }

            // Never fly the raw transform toward a destination. Waiting for a valid NavMesh
            // bind is less visible and much safer than clipping, floating or drifting away.
            if (enemy.moveTowardsDestination)
                Plugin.Log?.LogDebug("Buddy movement paused while waiting for a valid NavMesh position.");
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
                    case CrewmateState.ScoutAhead:
                        TickScoutAhead(data);
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
                enemy.agent.acceleration = 8f;
                enemy.agent.angularSpeed = 220f;
                if (!enemy.agent.isOnNavMesh)
                {
                    if (NavMesh.SamplePosition(enemy.transform.position, out var hit, 4f, NavMesh.AllAreas))
                    {
                        enemy.agent.Warp(hit.position);
                        enemy.transform.position = hit.position;
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
            if (HandleFollowTargetDeath(data))
                return;

            var target = GetFollowTarget(data);
            if (target == null)
            {
                StopMoving(enemy);
                return;
            }

            data.Owner = target;

            if (WaitNaturallyAtClosedDoor(data, target))
                return;

            // Facility / outside are separate navmeshes — warp through when owner changes area
            if (SyncAreaWithOwner(data, target))
                return;

            float dist = Vector3.Distance(enemy.transform.position, target.transform.position);
            if (ApplyIntentionalHorrorPause(data, dist))
                return;
            if (BuddyPacingDirector.TryHoldAndWatch(data, target, dist))
                return;
            // Hysteresis: stop inside FollowDistance, only resume after FollowResumeDistance
            // so he doesn't micro-adjust into your personal space
            if (dist <= BuddyMovementPolicy.FollowStopDistance)
            {
                StopMoving(enemy);
                ApplyIdleLook(data, target);
                MaybeReportWitnessedDeath(data, target, dist);
                return;
            }
            if (dist < BuddyMovementPolicy.FollowResumeDistance && !enemy.moveTowardsDestination)
            {
                // already stopped, not far enough to bother walking again
                return;
            }

            // Walk toward a point offset behind the player, not into their feet
            float spacing = BuddyPacingDirector.FollowSpacing(
                BuddyCharacterDirector.CurrentStage >= BuddyArcStage.Cold ? 3.0f : 2.35f);
            Vector3 followPoint = target.transform.position
                                  - target.transform.forward * spacing
                                  + target.transform.right * data.FollowSideOffset;
            if (enemy.agent != null)
                enemy.agent.speed = BuddyMovementPolicy.FollowSpeed(dist);
            MoveTo(enemy, followPoint);
        }

        /// <summary>
        /// LC indoor/outdoor are different NavMeshes. Match owner's factory/exterior state
        /// via Masked SetEnemyOutside + teleport next to them.
        /// </summary>
        private static bool SyncAreaWithOwner(CrewmateData data, PlayerControllerB owner)
        {
            if (data?.Enemy == null || owner == null) return false;
            try
            {
                var enemy = data.Enemy;
                bool ownerInFactory = owner.isInsideFactory;
                bool buddyOutside = enemy.isOutside;
                bool buddyInShip = IsInsideShip(enemy.transform.position);
                bool mismatch = (ownerInFactory && (buddyOutside || buddyInShip)) ||
                                (owner.isInHangarShipRoom && !buddyInShip) ||
                                (!ownerInFactory && !owner.isInHangarShipRoom && !buddyOutside);

                if (!mismatch)
                {
                    ResetAreaMismatch(data);
                    return false;
                }

                if (data.AreaMismatchStartedAt <= 0f)
                {
                    data.AreaMismatchStartedAt = Time.time;
                    data.AreaPathRebuildAttempts = 0;
                    data.NextAreaPathRebuildAt = Time.time + BuddyMovementPolicy.PathRebuildDelay;
                    StopMoving(enemy);
                    return true;
                }

                float waiting = Time.time - data.AreaMismatchStartedAt;
                if (data.AreaPathRebuildAttempts < BuddyMovementPolicy.RebuildsBeforeEmergency &&
                    Time.time >= data.NextAreaPathRebuildAt)
                {
                    data.AreaPathRebuildAttempts++;
                    data.NextAreaPathRebuildAt = Time.time + BuddyMovementPolicy.PathRebuildDelay;
                    MoveTo(enemy, owner.transform.position);
                    Plugin.Log?.LogWarning(
                        $"Buddy area transition path rebuild {data.AreaPathRebuildAttempts}/{BuddyMovementPolicy.RebuildsBeforeEmergency} " +
                        $"after {waiting:F1}s of mismatch.");
                    return true;
                }

                float separation = Vector3.Distance(enemy.transform.position, owner.transform.position);
                if (!BuddyMovementPolicy.ShouldEmergencyRecover(
                        waiting,
                        data.AreaPathRebuildAttempts,
                        separation,
                        waiting))
                    return true;

                if (Time.time < data.NextAreaTeleportAt)
                    return true;

                bool setOutside = !ownerInFactory && !owner.isInHangarShipRoom;
                string direction = ownerInFactory ? "through a facility entrance" : "to the exterior";
                Plugin.Log?.LogWarning(
                    $"Buddy emergency-recovering {direction} after {waiting:F1}s and " +
                    $"{data.AreaPathRebuildAttempts} path rebuilds.");

                if (TeleportBesidePlayer(enemy, owner, setOutside))
                {
                    data.NextAreaTeleportAt = Time.time + 10f;
                    ResetAreaMismatch(data);
                }
                else
                {
                    data.NextAreaTeleportAt = Time.time + BuddyMovementPolicy.PathRebuildDelay;
                }
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"SyncAreaWithOwner: {ex.Message}");
                return true;
            }
        }

        private static void ResetAreaMismatch(CrewmateData data)
        {
            if (data == null) return;
            data.AreaMismatchStartedAt = 0f;
            data.AreaPathRebuildAttempts = 0;
            data.NextAreaPathRebuildAt = 0f;
        }

        private static bool IsInsideShip(Vector3 position)
        {
            try
            {
                var sor = StartOfRound.Instance;
                if (sor?.shipInnerRoomBounds != null) return sor.shipInnerRoomBounds.bounds.Contains(position);
                if (sor?.shipBounds != null) return sor.shipBounds.bounds.Contains(position);
            }
            catch { }
            return false;
        }

        private static bool TeleportBesidePlayer(MaskedPlayerEnemy enemy, PlayerControllerB owner, bool setOutside)
        {
            if (enemy == null || owner == null) return false;
            try
            {
                Vector3 dest = owner.transform.position
                               + owner.transform.right * 1.1f
                               + owner.transform.forward * -0.4f
                               + Vector3.up * 0.1f;

                // Only ever teleport to a position that exists on a NavMesh. If neither the
                // offset nor the owner's position resolve (owner falling into the void, or a
                // scene where no mesh is baked yet), skip the teleport entirely — warping to a
                // raw world position would drop Buddy into the void with the owner.
                bool anchored = false;
                if (NavMesh.SamplePosition(dest, out var hit, 10f, NavMesh.AllAreas))
                {
                    dest = hit.position;
                    anchored = true;
                }
                else if (NavMesh.SamplePosition(owner.transform.position, out hit, 12f, NavMesh.AllAreas))
                {
                    dest = hit.position;
                    anchored = true;
                }
                if (!anchored)
                {
                    Plugin.Log?.LogWarning($"Buddy teleport skipped: no NavMesh near owner at {owner.transform.position}.");
                    return false;
                }

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
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"TeleportBesidePlayer: {ex}");
                return false;
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
                if (data.DeliverFetchToOwner && TickDeliverToOwner(data))
                    return;
                TickReturnToShip(data, dropItem: true);
                return;
            }

            // Acquire target
            if (data.FetchTarget == null || !IsValidScrap(data.FetchTarget))
            {
                data.FetchTarget = string.IsNullOrWhiteSpace(data.FetchItemFilter)
                    ? FindUsefulScrap(enemy.transform.position)
                    : FindScrapNamed(data.FetchItemFilter, enemy.transform.position);
                if (data.FetchTarget == null)
                {
                    Plugin.Log?.LogInfo(string.IsNullOrWhiteSpace(data.FetchItemFilter)
                        ? "No scrap found for fetch; returning to follow."
                        : "No scrap named '" + data.FetchItemFilter + "' found for fetch; returning to follow.");
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

        private static void TickScoutAhead(CrewmateData data)
        {
            var enemy = data.Enemy;
            if (enemy == null) return;

            if (Time.time - data.ScoutStartedAt > 20f)
            {
                StopMoving(enemy);
                CrewmateRegistry.SetState(data, CrewmateState.FollowOwner);
                LlmClient.EnqueueObservation("You could not get any farther ahead safely and are heading back.");
                return;
            }

            var owner = GetFollowTarget(data);
            if (owner != null && Vector3.Distance(enemy.transform.position, owner.transform.position) > 35f)
            {
                StopMoving(enemy);
                CrewmateRegistry.SetState(data, CrewmateState.FollowOwner);
                return;
            }

            float distance = Vector3.Distance(enemy.transform.position, data.ScoutDestination);
            if (distance > 1.8f)
            {
                MoveTo(enemy, data.ScoutDestination);
                return;
            }

            StopMoving(enemy);
            if (data.ScoutArrivedAt <= 0f)
                data.ScoutArrivedAt = Time.time;
            if (!data.ScoutReportSent)
            {
                data.ScoutReportSent = true;
                LlmClient.EnqueueObservation(BuildScoutReport(data));
            }
            if (Time.time - data.ScoutArrivedAt >= 2.5f)
                CrewmateRegistry.SetState(data, CrewmateState.FollowOwner);
        }

        private static string BuildScoutReport(CrewmateData data)
        {
            EnemyAI nearestThreat = null;
            float nearestDistance = 16f;
            foreach (var candidate in UnityEngine.Object.FindObjectsOfType<EnemyAI>())
            {
                if (candidate == null || candidate.isEnemyDead || CrewmateRegistry.IsCrewmate(candidate)) continue;
                if (candidate.isOutside != data.Enemy.isOutside) continue;
                string name = (candidate.enemyType?.enemyName ?? candidate.GetType().Name).ToLowerInvariant();
                if (name.Contains("manticoil") || name.Contains("roaming locust")) continue;
                float distance = Vector3.Distance(data.Enemy.transform.position, candidate.transform.position);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestThreat = candidate;
                }
            }

            if (nearestThreat != null)
            {
                string name = nearestThreat.enemyType?.enemyName;
                if (string.IsNullOrWhiteSpace(name)) name = "something hostile";
                return $"You scouted ahead: {name} is about {Mathf.CeilToInt(nearestDistance)} metres further on. Warn them.";
            }

            int scrap = 0;
            foreach (var item in UnityEngine.Object.FindObjectsOfType<GrabbableObject>())
                if (IsValidScrap(item) && Vector3.Distance(data.Enemy.transform.position, item.transform.position) <= 12f)
                    scrap++;
            return scrap > 0
                ? $"You scouted ahead: it is clear, with {scrap} piece{(scrap == 1 ? "" : "s")} of scrap around."
                : "You scouted ahead: it is clear, nothing worth taking.";
        }

        private static bool TryBeginScout(CrewmateData data, PlayerControllerB requester, float requestedDistance, out string failure)
        {
            failure = null;
            requester = requester ?? GetFollowTarget(data);
            if (data?.Enemy == null || requester == null || requester.isPlayerDead)
            {
                failure = "I need a living crewmate to point the way.";
                return false;
            }

            Vector3 direction = requester.transform.forward;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.01f) direction = data.Enemy.transform.forward;
            direction.Normalize();
            float distance = Mathf.Clamp(requestedDistance, MinScoutDistance, MaxScoutDistance);

            if (!TryResolveScoutDestination(data.Enemy, requester.transform.position, direction, distance, out Vector3 destination))
            {
                failure = "I can't find a safe path forward from here.";
                return false;
            }

            data.Owner = requester;
            CrewmateRegistry.SetState(data, CrewmateState.ScoutAhead);
            data.ScoutDestination = destination;
            data.ScoutStartedAt = Time.time;
            data.ScoutArrivedAt = 0f;
            data.ScoutReportSent = false;
            MoveTo(data.Enemy, destination);
            Plugin.Log?.LogInfo($"Buddy scout -> player='{requester.playerUsername}' distance={distance:F1} destination={destination}.");
            return true;
        }

        private static bool TryResolveScoutDestination(MaskedPlayerEnemy enemy, Vector3 origin, Vector3 direction, float distance, out Vector3 destination)
        {
            destination = Vector3.zero;
            bool buddyOnNavMesh = enemy.agent != null && enemy.agent.enabled && enemy.agent.isOnNavMesh;
            if (!buddyOnNavMesh)
            {
                destination = origin + direction * distance;
                destination.y = enemy.transform.position.y;
                return true;
            }

            if (!NavMesh.SamplePosition(enemy.transform.position, out var start, 5f, NavMesh.AllAreas))
                return false;
            for (float candidateDistance = distance; candidateDistance >= MinScoutDistance; candidateDistance -= 2f)
            {
                Vector3 candidate = origin + direction * candidateDistance;
                if (!NavMesh.SamplePosition(candidate, out var end, 4f, NavMesh.AllAreas)) continue;
                var path = new NavMeshPath();
                if (NavMesh.CalculatePath(start.position, end.position, NavMesh.AllAreas, path) &&
                    path.status == NavMeshPathStatus.PathComplete)
                {
                    destination = end.position;
                    return true;
                }
            }
            return false;
        }

        private static void MoveTo(MaskedPlayerEnemy enemy, Vector3 worldPos)
        {
            try
            {
                enemy.moveTowardsDestination = true;
                enemy.movingTowardsTargetPlayer = false;
                enemy.targetPlayer = null;

                EnsureAgent(enemy);

                // Prefer snapping dest onto navmesh
                Vector3 dest = worldPos;
                if (NavMesh.SamplePosition(worldPos, out var hit, 8f, NavMesh.AllAreas))
                    dest = hit.position;
                if (CrewmateRegistry.TryGet(enemy, out var data) && data != null)
                    data.ManualDestination = dest;

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
                        float distance = Vector3.Distance(enemy.transform.position, dest);
                        enemy.agent.speed = data != null && data.State == CrewmateState.FollowOwner
                            ? BuddyMovementPolicy.FollowSpeed(distance)
                            : AgentSpeed;
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

        private static bool HandleFollowTargetDeath(CrewmateData data)
        {
            PlayerControllerB owner = data.Owner;
            if (owner == null || !owner.isPlayerDead)
            {
                if (data.FollowTargetDiedAt <= 0f) return false;
            }
            else if (data.FollowTargetDiedAt <= 0f)
            {
                float distance = Vector3.Distance(data.Enemy.transform.position, owner.transform.position);
                bool ownerOutside = !owner.isInsideFactory && !owner.isInHangarShipRoom;
                bool sameArea = owner.isInsideFactory ? !data.Enemy.isOutside : data.Enemy.isOutside == ownerOutside;
                data.FollowTargetDiedAt = Time.time;
                data.NextFollowAcquireAt = Time.time + BuddyMovementPolicy.DeathReactionDelay(data.NetworkObjectId);
                data.FollowTargetDeathPosition = owner.transform.position;
                data.FollowTargetDeathName = string.IsNullOrWhiteSpace(owner.playerUsername) ? "the other crewmate" : owner.playerUsername;
                data.FollowTargetDeathWitnessed = BuddyMovementPolicy.CouldWitnessDeath(
                    distance, sameArea, HasLineOfSightTo(data.Enemy, owner));
                data.DeathReportPending = data.FollowTargetDeathWitnessed;
                if (data.FollowTargetDeathWitnessed)
                {
                    BuddyCharacterDirector.RecordWitnessedDeath(data.FollowTargetDeathName);
                    BuddyRelationships.Note(owner?.playerUsername, BuddyRelationEvent.WitnessedTheirDeath);
                }
                StopMoving(data.Enemy);
                Plugin.Log?.LogInfo("Buddy follow target died; witnessed=" + data.FollowTargetDeathWitnessed +
                                    " delay=" + (data.NextFollowAcquireAt - Time.time).ToString("F1") + "s.");
            }

            if (Time.time < data.NextFollowAcquireAt)
            {
                StopMoving(data.Enemy);
                Vector3 look = data.FollowTargetDeathPosition - data.Enemy.transform.position;
                look.y = 0f;
                if (look.sqrMagnitude > 0.05f)
                    data.Enemy.transform.rotation = Quaternion.Slerp(
                        data.Enemy.transform.rotation, Quaternion.LookRotation(look.normalized), Time.deltaTime * 1.4f);
                return true;
            }

            PlayerControllerB next = FindNearestLivingPlayer(data);
            if (next == null)
            {
                StopMoving(data.Enemy);
                return true;
            }
            data.Owner = next;
            data.FollowTargetDiedAt = 0f;
            data.NextFollowAcquireAt = 0f;
            // No teleport and no target snap: the next normal follow tick builds a walking path.
            return false;
        }

        private static void MaybeReportWitnessedDeath(CrewmateData data, PlayerControllerB target, float distance)
        {
            if (!data.DeathReportPending || !data.FollowTargetDeathWitnessed || target == null || distance > 8f) return;
            data.DeathReportPending = false;
            BuddyAutonomy.Queue(BuddyContextEvent.WitnessedDeathReport,
                "Buddy personally witnessed " + (data.FollowTargetDeathName ?? "the previous crewmate") +
                " die nearby, then travelled normally to " + (target.playerUsername ?? "another crewmate") +
                ". Tell this crewmate unprompted that the other player died. Do not claim details Buddy did not witness.");
        }

        private static PlayerControllerB FindNearestLivingPlayer(CrewmateData data)
        {
            PlayerControllerB best = null;
            float bestDistance = float.MaxValue;
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || data?.Enemy == null) return null;
            foreach (PlayerControllerB player in players)
            {
                if (player == null || player.isPlayerDead || !player.isPlayerControlled) continue;
                float distance = Vector3.Distance(data.Enemy.transform.position, player.transform.position);
                if (distance < bestDistance) { best = player; bestDistance = distance; }
            }
            // Reacquisition only. An explicit "follow me" still sets the owner directly, so this
            // can never override a crewmate who actually asked for Buddy.
            return BuddySocialIntelligence.ChooseAttentionTarget(data, best);
        }

        private static bool HasLineOfSightTo(MaskedPlayerEnemy enemy, PlayerControllerB player)
        {
            if (enemy == null || player == null) return false;
            Vector3 from = enemy.transform.position + Vector3.up * 1.45f;
            Vector3 to = player.transform.position + Vector3.up * 1.1f;
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance <= 0.05f) return true;
            RaycastHit[] hits = Physics.RaycastAll(from, delta / distance, distance, ~0, QueryTriggerInteraction.Ignore);
            float nearestDistance = float.MaxValue;
            RaycastHit nearest = default;
            bool found = false;
            foreach (RaycastHit hit in hits)
            {
                if (hit.transform == null || hit.transform == enemy.transform || hit.transform.IsChildOf(enemy.transform)) continue;
                if (hit.distance < nearestDistance) { nearest = hit; nearestDistance = hit.distance; found = true; }
            }
            if (!found) return true;
            return nearest.transform.GetComponentInParent<PlayerControllerB>() == player;
        }

        private static void ApplyIdleLook(CrewmateData data, PlayerControllerB owner)
        {
            if (data?.Enemy == null || owner == null || Time.time < data.NextIdleLookAt) return;
            data.NextIdleLookAt = Time.time + UnityEngine.Random.Range(7f, 16f);
            Vector3 look;
            if (BuddyCharacterDirector.CurrentStage >= BuddyArcStage.Cold)
                look = owner.transform.position - data.Enemy.transform.position;
            else
                look = owner.transform.forward + owner.transform.right * UnityEngine.Random.Range(-0.65f, 0.65f);
            look.y = 0f;
            if (look.sqrMagnitude > 0.05f)
                data.Enemy.transform.rotation = Quaternion.Slerp(
                    data.Enemy.transform.rotation, Quaternion.LookRotation(look.normalized), 0.35f);
        }

        private static bool ApplyIntentionalHorrorPause(CrewmateData data, float distance)
        {
            BuddyArcStage stage = BuddyCharacterDirector.CurrentStage;
            if (stage < BuddyArcStage.Unsettling || distance < 7f || distance > 18f) return false;
            if (Time.time < data.IntentionalPauseUntil)
            {
                StopMoving(data.Enemy);
                return true;
            }
            if (Time.time < data.NextIntentionalPauseAt) return false;
            float duration = stage >= BuddyArcStage.Cold
                ? UnityEngine.Random.Range(1.2f, 2.0f)
                : UnityEngine.Random.Range(0.55f, 1.0f);
            data.IntentionalPauseUntil = Time.time + duration;
            data.NextIntentionalPauseAt = Time.time + UnityEngine.Random.Range(45f, 85f);
            StopMoving(data.Enemy);
            return true;
        }

        private static PlayerControllerB GetFollowTarget(CrewmateData data)
        {
            try
            {
                // isPlayerControlled can be false for host in some states — only require alive
                if (data.Owner != null && !data.Owner.isPlayerDead &&
                    (data.Owner.isPlayerControlled || data.Owner.isHostPlayerObject))
                    return data.Owner;

                return FindNearestLivingPlayer(data);
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

        private static GrabbableObject FindUsefulScrap(Vector3 from)
        {
            GrabbableObject best = null;
            float bestScore = float.MinValue;
            try
            {
                var items = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
                foreach (var item in items)
                {
                    if (!IsValidScrap(item)) continue;
                    // Prefer scrap not already in ship
                    if (item.isInShipRoom) continue;
                    float d = Vector3.Distance(from, item.transform.position);
                    float score = BuddyCrewmateRoutinePolicy.ScrapScore(item.scrapValue, d);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = item;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"FindUsefulScrap: {ex.Message}");
            }
            return best;
        }

        /// <summary>Nearest loose scrap whose item name contains the speaker's filter, or null.</summary>
        private static GrabbableObject FindScrapNamed(string nameFilter, Vector3 from)
        {
            string want = (nameFilter ?? "").Trim().ToLowerInvariant();
            if (want.Length == 0) return null;
            GrabbableObject best = null;
            float bestDist = float.MaxValue;
            try
            {
                var items = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
                foreach (var item in items)
                {
                    if (!IsValidScrap(item)) continue;
                    if (item.isHeld || item.isInShipRoom) continue;
                    string itemName = item.itemProperties?.itemName ?? "";
                    if (itemName.Length == 0 || !itemName.ToLowerInvariant().Contains(want)) continue;
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
                Plugin.Log?.LogWarning($"FindScrapNamed: {ex.Message}");
            }
            return best;
        }

        private static bool TickDeliverToOwner(CrewmateData data)
        {
            PlayerControllerB owner = data.Owner;
            if (owner == null || owner.isPlayerDead)
            {
                data.DeliverFetchToOwner = false;
                return false;
            }

            float distance = Vector3.Distance(data.Enemy.transform.position, owner.transform.position);
            if (distance <= BuddyCrewmateRoutinePolicy.HandoffDistance)
            {
                DropHeldItem(data, owner.transform.position + owner.transform.forward * 0.8f);
                data.DeliverFetchToOwner = false;
                CrewmateRegistry.SetState(data, CrewmateState.FollowOwner);
                return true;
            }
            MoveTo(data.Enemy, owner.transform.position - owner.transform.forward * 1.5f);
            return true;
        }

        private static bool WaitNaturallyAtClosedDoor(CrewmateData data, PlayerControllerB owner)
        {
            if (Time.time < data.DoorWaitUntil)
            {
                StopMoving(data.Enemy);
                return true;
            }
            if (Time.time < data.NextDoorCheckAt) return false;
            data.NextDoorCheckAt = Time.time + 0.6f;
            if (Time.time < data.NextDoorWaitAllowedAt) return false;
            try
            {
                foreach (DoorLock door in UnityEngine.Object.FindObjectsOfType<DoorLock>())
                {
                    if (door == null || Vector3.Distance(data.Enemy.transform.position, door.transform.position) > 2.8f) continue;
                    if (!TryReadDoorFlag(door, "isDoorOpened", out bool open) || open) continue;
                    float ownerDoorDistance = Vector3.Distance(owner.transform.position, door.transform.position);
                    if (!BuddyCrewmateRoutinePolicy.ShouldWaitAtDoor(ownerDoorDistance)) continue;
                    data.DoorWaitUntil = Time.time + BuddyCrewmateRoutinePolicy.DoorWaitSeconds;
                    data.NextDoorWaitAllowedAt = Time.time + BuddyCrewmateRoutinePolicy.DoorRetrySeconds;
                    StopMoving(data.Enemy);
                    return true;
                }
            }
            catch (Exception ex) { Plugin.Log?.LogDebug("Door-aware wait: " + ex.Message); }
            return false;
        }

        private static bool TryReadDoorFlag(DoorLock door, string fieldName, out bool value)
        {
            value = false;
            try
            {
                var field = door.GetType().GetField(fieldName);
                if (field == null || field.FieldType != typeof(bool)) return false;
                value = (bool)field.GetValue(door);
                return true;
            }
            catch { return false; }
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
                    return TeleportBesidePlayer(data.Enemy, owner, outside);
                }

                if (data.State == CrewmateState.ReturnToShip || data.HeldItem != null)
                {
                    return TeleportToPosition(data, GetShipDropPosition(), false, "return-to-ship stall");
                }

                if (data.State == CrewmateState.FetchScrap && data.FetchTarget != null)
                {
                    bool outside = data.Enemy.isOutside;
                    try { outside = !data.FetchTarget.isInFactory; } catch { }
                    return TeleportToPosition(data, data.FetchTarget.transform.position, outside, "fetch stall");
                }

                if (data.State == CrewmateState.ScoutAhead)
                {
                    StopMoving(data.Enemy);
                    CrewmateRegistry.SetState(data, CrewmateState.FollowOwner);
                    LlmClient.EnqueueObservation("That route is blocked, so you are coming back.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"RecoverStalled: {ex.Message}");
            }
            return false;
        }

        private static bool TeleportToPosition(CrewmateData data, Vector3 destination, bool outside, string reason)
        {
            var enemy = data.Enemy;
            if (!NavMesh.SamplePosition(destination, out var hit, 12f, NavMesh.AllAreas))
            {
                Plugin.Log?.LogWarning($"Buddy refused unsafe teleport recovery reason={reason}: no NavMesh near {destination}.");
                return false;
            }
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
            return true;
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

        private static void ApplyMovementState(CrewmateData data, BuddyMovementActionKind action)
        {
            if (data == null) return;
            switch (action)
            {
                case BuddyMovementActionKind.Follow:
                    CrewmateRegistry.SetState(data, CrewmateState.FollowOwner);
                    break;
                case BuddyMovementActionKind.Stay:
                    CrewmateRegistry.SetState(data, CrewmateState.Stay);
                    // A stay order must cancel the previous follow/fetch path in the same tool
                    // dispatch. Waiting for the next 120 ms AI tick lets a fast agent keep running
                    // and can carry it far enough that the raw transform anchor is re-sampled onto
                    // a different NavMesh point.
                    StopMoving(data.Enemy);
                    if (data.Enemy != null)
                    {
                        Vector3 anchor = data.Enemy.transform.position;
                        if (NavMesh.SamplePosition(anchor, out var hit, 1.5f, NavMesh.AllAreas))
                            anchor = hit.position;
                        data.StayPosition = anchor;
                    }
                    break;
                case BuddyMovementActionKind.ReturnToShip:
                    CrewmateRegistry.SetState(data, CrewmateState.ReturnToShip);
                    break;
                case BuddyMovementActionKind.FetchScrap:
                    CrewmateRegistry.SetState(data, CrewmateState.FetchScrap);
                    break;
            }
        }

        public static string ExecuteToolAction(string action, int requestingPlayerId, float scoutDistance, bool bringToPlayer, string itemName = null)
        {
            string failure = null;
            if (!CrewmateSpawner.IsHost()) return "Tool failed: Buddy actions run on the host.";
            if (string.IsNullOrWhiteSpace(action)) return "Tool failed: no movement action was supplied.";
            var data = CrewmateRegistry.GetPrimary();
            if (data == null)
            {
                Plugin.Log?.LogWarning("Movement tool ignored: no crewmate registered.");
                failure = !string.IsNullOrWhiteSpace(NetMessenger.HostCompatibilityWarning)
                    ? NetMessenger.HostCompatibilityWarning
                    : "I can't move right now—my body isn't available.";
                return failure;
            }

            BuddyMovementAction movement;
            switch (action.Trim().ToLowerInvariant())
            {
                case "follow": movement = new BuddyMovementAction(BuddyMovementActionKind.Follow); break;
                case "stay": movement = new BuddyMovementAction(BuddyMovementActionKind.Stay); break;
                case "return_to_ship": movement = new BuddyMovementAction(BuddyMovementActionKind.ReturnToShip); break;
                case "fetch_scrap":
                    movement = new BuddyMovementAction(BuddyMovementActionKind.FetchScrap, deliverToRequester: bringToPlayer, fetchItemName: itemName);
                    break;
                case "scout_ahead": movement = new BuddyMovementAction(BuddyMovementActionKind.ScoutAhead, scoutDistance); break;
                default: return "Tool failed: unknown movement action '" + action + "'.";
            }
            var requester = ResolveRequestingPlayer(requestingPlayerId);
            switch (movement.Kind)
            {
                case BuddyMovementActionKind.Follow:
                    if (requester != null && !requester.isPlayerDead)
                    {
                        data.Owner = requester;
                        Plugin.Log?.LogInfo($"Buddy follow owner -> '{requester.playerUsername}' (playerId={requestingPlayerId}).");
                    }
                    ApplyMovementState(data, movement.Kind);
                    return "ok: state=following target=" + (requester?.playerUsername ?? "requesting_player");
                case BuddyMovementActionKind.Stay:
                    ApplyMovementState(data, movement.Kind);
                    return "ok: state=holding_position";
                case BuddyMovementActionKind.ReturnToShip:
                    ApplyMovementState(data, movement.Kind);
                    return "ok: state=returning_to_ship";
                case BuddyMovementActionKind.FetchScrap:
                    data.Owner = requester ?? data.Owner;
                    data.DeliverFetchToOwner = movement.DeliverToRequester;
                    data.FetchItemFilter = string.IsNullOrWhiteSpace(movement.FetchItemName) ? null : movement.FetchItemName.Trim();
                    if (!string.IsNullOrWhiteSpace(data.FetchItemFilter))
                    {
                        var named = FindScrapNamed(data.FetchItemFilter, data.Enemy != null ? data.Enemy.transform.position : Vector3.zero);
                        if (named == null)
                        {
                            data.FetchItemFilter = null;
                            return "failed: no_loose_scrap_matching name='" + movement.FetchItemName + "'";
                        }
                        data.FetchTarget = named;
                    }
                    ApplyMovementState(data, movement.Kind);
                    return movement.DeliverToRequester ? "ok: state=fetching_scrap deliver_to=requesting_player" : "ok: state=fetching_scrap deliver_to=ship";
                case BuddyMovementActionKind.ScoutAhead:
                    return TryBeginScout(data, requester, movement.ScoutDistance, out failure)
                        ? "ok: state=scouting_ahead distance_metres=" + Mathf.Clamp(movement.ScoutDistance, MinScoutDistance, MaxScoutDistance).ToString("F0")
                        : string.IsNullOrWhiteSpace(failure) ? "Tool failed: Buddy could not scout ahead." : failure;
                default:
                    return "Tool failed: unsupported movement action.";
            }
        }

        private static PlayerControllerB ResolveRequestingPlayer(int playerId)
        {
            try
            {
                var players = StartOfRound.Instance?.allPlayerScripts;
                if (players == null) return null;
                foreach (var player in players)
                    if (player != null && (int)player.playerClientId == playerId)
                        return player;
                if (playerId >= 0 && playerId < players.Length) return players[playerId];
            }
            catch { }
            return null;
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
