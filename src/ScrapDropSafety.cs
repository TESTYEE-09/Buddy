using System;
using HarmonyLib;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// The original drop path cleared data.HeldItem before restoring the object's physics/parent.
    /// If a game API throws midway through, that can orphan an ungrabbable item on Buddy. Replace
    /// the drop with a cleanup-first implementation whose finally block always restores the item.
    /// </summary>
    [HarmonyPatch(typeof(CrewmateAI), nameof(CrewmateAI.DropHeldItem))]
    internal static class Patch_CrewmateAI_FailureSafeDrop
    {
        [HarmonyPrefix]
        private static bool Prefix(CrewmateData data, Vector3 dropPos)
        {
            if (data == null)
                return false;

            var item = data.HeldItem;
            if (item == null)
            {
                data.HeldItem = null;
                return false;
            }

            bool inShip = false;
            ulong crewId = data.NetworkObjectId;
            ulong itemId = 0;

            try
            {
                try { if (item.IsSpawned) itemId = item.NetworkObjectId; } catch { /* optional */ }
                try { item.DiscardItemFromEnemy(); } catch (Exception ex) { Plugin.Log?.LogWarning($"DiscardItemFromEnemy: {ex.Message}"); }

                try
                {
                    var sor = StartOfRound.Instance;
                    if (sor?.shipInnerRoomBounds != null)
                        inShip = sor.shipInnerRoomBounds.bounds.Contains(dropPos);
                    else if (sor?.shipBounds != null)
                        inShip = sor.shipBounds.bounds.Contains(dropPos);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"Drop ship-bounds check: {ex.Message}");
                }

                if (inShip)
                {
                    try
                    {
                        item.isInShipRoom = true;
                        item.isInElevator = true;
                        int instId = item.GetInstanceID();
                        if (!data.ScrapCountedInstanceIds.Contains(instId))
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
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Failure-safe scrap drop: {ex}");
            }
            finally
            {
                // These restoration steps are deliberately independent: one failure must not stop
                // the remaining cleanup from making the item usable again.
                try { item.isHeldByEnemy = false; } catch { /* ignore */ }
                try { item.isHeld = false; } catch { /* ignore */ }
                try { item.grabbable = true; } catch { /* ignore */ }
                try { item.transform.SetParent(null, true); } catch { /* ignore */ }
                try
                {
                    item.transform.position = dropPos + Vector3.up * 0.2f;
                    item.targetFloorPosition = item.transform.position;
                    item.startFallingPosition = item.transform.position;
                }
                catch { /* ignore */ }
                try { item.EnablePhysics(true); } catch { /* ignore */ }
                try { item.FallToGround(false, false, item.transform.position); } catch { /* ignore */ }

                data.HeldItem = null;
                try
                {
                    if (crewId != 0 && itemId != 0)
                        NetMessenger.BroadcastItemAttach(crewId, itemId, attached: false);
                }
                catch { /* ignore */ }
            }

            Plugin.Log?.LogInfo($"Crewmate dropped scrap safely (inShip={inShip})");
            return false;
        }
    }
}
