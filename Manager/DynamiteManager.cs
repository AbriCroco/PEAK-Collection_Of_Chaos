using System;
using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;
using Zorro.Core;
using Zorro.Core.Serizalization;
using Chaos.Utils;
using Chaos.Patches;
using Chaos.Effects;

namespace Chaos.Manager
{
    // Master-only manager that ticks mod-dynamite instance data (Fuel) and triggers explosion when time runs out.
    public class DynamiteManager : MonoBehaviourPun
    {
        private static float WaitTime = 5f;
        private static float BoomTime = 0.15f;
        public static DynamiteManager? Instance { get; private set; }

        private static Dictionary<Guid, bool> OwnerChanged = new();
        private static Dictionary<Guid, int> OwnerChangeBuffer = new();
        private static Dictionary<Guid, HashSet<PhotonView>> DynamiteItems = new();

        private static HashSet<Guid> tracked = new HashSet<Guid>();
        public static bool IsTracked(Guid guid) => tracked.Contains(guid);
        private static HashSet<Guid> pendingDrop = new HashSet<Guid>();

        private static Dictionary<Guid, (Player player, ItemSlot slot)> DynamitePickedup = new();

        //The list of goofy players holding 4 dynamites, among which one is in temp slot and belongs to someone else
        private static Dictionary<Guid, Player> GoofyPlayer = new();

        private Coroutine? tickCoroutine;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public static void ClearDynamiteManager()
        {
            tracked.Clear();
            pendingDrop.Clear();
            OwnerChanged.Clear();
            OwnerChangeBuffer.Clear();
            DynamitePickedup.Clear();
            GoofyPlayer.Clear();
            AddItemHelper.InstanceOwners.Clear();
            CatchDynamiteEffect.availableSlots.Clear();
        }

        private static void RemoveDictionariesEntries(Guid guid, Player player, ItemSlot slot)
        {
            if (CatchDynamiteEffect.availableSlots.TryGetValue(player.photonView.OwnerActorNr, out var set))
                set.Add(slot.itemSlotID);

            AddItemHelper.InstanceOwners.Remove(guid);
            tracked.Remove(guid);
            pendingDrop.Remove(guid);
            OwnerChanged.Remove(guid);
            OwnerChangeBuffer.Remove(guid);
            DynamitePickedup.Remove(guid);
            GoofyPlayer.Remove(guid);
        }

        public static void RegisterDynamitePickedup(Guid guid, Player player, ItemSlot slot)
        {
            if (!tracked.Contains(guid))
                return;

            DynamitePickedup[guid] = (player, slot);
        }

        // Call from master client when you have inserted the instanceData into inventory and want it ticked.
        public void RegisterInstance(ItemInstanceData instanceData)
        {
            if (!PhotonNetwork.IsMasterClient)
                return;

            if (instanceData == null)
                return;

            if (tracked.Contains(instanceData.guid))
                return;

            //ModLogger.Log($"[DynamiteManager] RegisterInstance {instanceData.guid}");

            tracked.Add(instanceData.guid);
            OwnerChanged[instanceData.guid] = false;
            OwnerChangeBuffer[instanceData.guid] = 60;

            if (tickCoroutine == null)
                tickCoroutine = StartCoroutine(TickRoutine());
        }

        private IEnumerator TickRoutine()
        {

            while (tracked.Count > 0)
            {
                float dt = Time.deltaTime;
                var toProcess = new List<Guid>(tracked);

                foreach (var guid in toProcess)
                {

                    if (!AddItemHelper.InstanceOwners.TryGetValue(guid, out var info))
                    {
                        continue;
                    }

                    Player owner = info.player;
                    ItemSlot slot = info.slot;

                    if (!ItemInstanceDataHandler.TryGetInstanceData(guid, out var idata))
                    {
                        RemoveDictionariesEntries(guid, owner, slot);
                        continue;
                    }

                    if (!idata.TryGetDataEntry<FloatItemData>(DataEntryKey.Fuel, out var fuelEntry))
                    {
                        RemoveDictionariesEntries(guid, owner, slot);
                        continue;
                    }

                    bool hasWorldItem = TryGetWorldItem(guid, out var pv) && pv != null;
                    bool inSlot = !info.slot.IsEmpty() && info.slot.data?.guid == guid;

                    if (!inSlot && !hasWorldItem)
                    {
                        if (DynamitePickedup.ContainsKey(guid) || GoofyPlayer.ContainsKey(guid))
                            continue;

                        RemoveDictionariesEntries(guid, owner, slot);
                        continue;
                    }

                    fuelEntry.Value -= dt;
                    float percentage = 1000f;

                    if (PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(CatchDynamiteEffect.DynamiteFuseKeys.Key(guid), out var maxFuse))
                    {
                        percentage = fuelEntry.Value / (float)maxFuse;
                    }

                    if (!idata.TryGetDataEntry<FloatItemData>(DataEntryKey.UseRemainingPercentage, out var remainingUse))
                    {
                        remainingUse = idata.RegisterEntry<FloatItemData>(DataEntryKey.UseRemainingPercentage, new FloatItemData { Value = percentage });
                    }
                    else
                    {
                        remainingUse.Value = percentage;
                    }

                    if (Time.frameCount % 30 == 0)
                    {
                        if (owner.view != null)
                        {
                            var inv = IBinarySerializable.ToManagedArray(new InventorySyncData(owner.itemSlots, owner.backpackSlot, owner.tempFullSlot));
                            owner.view.RPC("SyncInventoryRPC", RpcTarget.Others, inv, false);
                        }
                    }

                    if (hasWorldItem || fuelEntry.Value <= BoomTime)
                    {
                        if (OwnerChanged[guid] == true && fuelEntry.Value > BoomTime)
                        {
                            if (OwnerChangeBuffer[guid] > 0)
                            {
                                OwnerChangeBuffer[guid] -= 1;
                                continue;
                            }
                            else
                            {
                                OwnerChanged[guid] = false;
                                OwnerChangeBuffer[guid] = 60;
                            }
                        }

                        if (!pendingDrop.Contains(guid) && pv != null)
                        {
                            pendingDrop.Add(guid);
                            StartCoroutine(BackToSlot(info.player, info.slot, idata));
                        }

                        if (fuelEntry.Value <= BoomTime && IsItemInSlot(owner, info.slot, idata, shouldExist: true) && IsTracked(guid))
                        {
                            HandleExplosionForInstance(idata, hasWorldItem);
                            PhotonNetwork.CurrentRoom.SetCustomProperties(new ExitGames.Client.Photon.Hashtable { { CatchDynamiteEffect.DynamiteFuseKeys.Key(guid), null } });
                            RemoveDictionariesEntries(guid, owner, slot);
                        }
                    }
                }

                yield return null;
            }

            tickCoroutine = null;
        }

        private IEnumerator BackToSlot(Player p, ItemSlot slot, ItemInstanceData idata)
        {
            float timer = WaitTime;
            float closeRadiusSqr = 10f * 10f;
            float touchRadiusSqr = 1.5f * 1.5f;

            if (p == null || p.character == null)
            {
                ModLogger.Log($"[BackToSlot] Null, stopping");
                pendingDrop.Remove(idata.guid);
                yield break;
            }

            Transform ownerHip = p.character.GetBodypart(BodypartType.Hip).transform;

            while (tracked.Contains(idata.guid) && pendingDrop.Contains(idata.guid) && p != null)
            {
                if (DynamitePickedup.TryGetValue(idata.guid, out var pickup))
                {
                    DynamitePickedup.Remove(idata.guid);
                    Player picker = pickup.player;
                    ItemSlot pickedSlot = pickup.slot;

                    ItemSlot? targetSlot;
                    if (pickedSlot.itemSlotID < 3)
                    {
                        // Regular slot — use directly
                        targetSlot = pickedSlot;
                        //ModLogger.Log($"[BackToSlot] {picker.name} has picked the dynamite, it is in {targetSlot}.");
                    }
                    else
                    {
                        // Temp slot — try to find an available mod slot
                        targetSlot = PickRandomAvailableSlot(picker);
                        //ModLogger.Log($"[BackToSlot] {picker.name} has picked the dynamite, it is in {targetSlot}.");
                    }
                    if (targetSlot == null)
                    {
                        // No available mod slots — mark as goofy,
                        //ModLogger.Log($"[BackToSlot] {picker.name} has no mod slots. Marking as Goofy.");
                        GoofyPlayer[idata.guid] = picker;
                        continue;
                    }
                    AddItemHelper.InstanceOwners[idata.guid] = (picker, targetSlot);

                    // Release old owner's slot
                    if (CatchDynamiteEffect.availableSlots.TryGetValue(p.photonView.OwnerActorNr, out var fromSlot))
                        fromSlot.Add(slot.itemSlotID);

                    if (CatchDynamiteEffect.availableSlots.TryGetValue(picker.photonView.OwnerActorNr, out var toSlot))
                        toSlot.Remove(targetSlot.itemSlotID);

                    OwnerChanged[idata.guid] = true;
                    pendingDrop.Remove(idata.guid);
                    yield break;
                }

                if (AddItemHelper.InstanceOwners.TryGetValue(idata.guid, out var info))
                {
                    if (info.player != p)
                    {
                        ModLogger.Log($"[BackToSlot] Owner Changed, stopping");
                        pendingDrop.Remove(idata.guid);
                        yield break;
                    }
                }

                if (!idata.TryGetDataEntry<FloatItemData>(DataEntryKey.Fuel, out var fuelEntry))
                {
                    pendingDrop.Remove(idata.guid);
                    yield break;
                }

                if (!TryGetWorldItem(idata.guid, out var pv) || pv == null)
                {
                    pendingDrop.Remove(idata.guid);
                    yield break;
                }

                Vector3 dynamitePos = pv.transform.position;
                Vector3 offset = dynamitePos - ownerHip.position;

                bool isInsideZone = offset.sqrMagnitude <= closeRadiusSqr;
                Character? touched = FindTouchedCharacter(dynamitePos, p.character, touchRadiusSqr);

                if (touched != null && OwnerChanged[idata.guid] == false)
                {
                    TransferDynamiteToPlayer(p, touched.player, slot, idata);
                    OwnerChanged[idata.guid] = true;
                    yield break;
                }

                timer = isInsideZone ? WaitTime : timer - Time.deltaTime;

                if (timer <= 0f || fuelEntry.Value <= BoomTime + 0.25f)
                {
                    if (IsItemInSlot(p, slot, idata, shouldExist: true))
                    {
                        pendingDrop.Remove(idata.guid);
                        yield break;
                    }

                    if (GoofyPlayer.ContainsKey(idata.guid))
                    {
                        CleaningGoofyPlayer(idata.guid);
                    }

                    var helper = p.GetComponent<AddItemHelper>();
                    if (helper == null)
                    {
                        helper = p.gameObject.AddComponent<AddItemHelper>();
                    }

                    pendingDrop.Remove(idata.guid);

                    PhotonNetwork.Destroy(pv);

                    ItemSlot currentSlot = p.GetItemSlot(p.character.refs.items.currentSelectedSlot.Value);
                    if (currentSlot != slot && !currentSlot.IsEmpty() && currentSlot.data?.guid == idata.guid)
                    {
                        currentSlot.EmptyOut();
                        var inv = IBinarySerializable.ToManagedArray(new InventorySyncData(p.itemSlots, p.backpackSlot, p.tempFullSlot));
                        p.view.RPC("SyncInventoryRPC", RpcTarget.Others, inv, false);
                    }

                    helper.StartDropThenAdd(p, slot, AddItemHelper.DynamiteItem, idata);
                    StartCoroutine(DelayedPickupAccepted(p, idata.guid));
                    yield break;
                }
                yield return null;
            }
        }
        private ItemSlot? PickRandomAvailableSlot(Player p)
        {
            if (!CatchDynamiteEffect.availableSlots.TryGetValue(p.photonView.OwnerActorNr, out var slots) || slots.Count == 0)
                return null;

            int target = UnityEngine.Random.Range(0, slots.Count);
            int j = 0;
            foreach (var id in slots)
            {
                if (j++ == target)
                    return p.GetItemSlot(id);
            }
            return null;
        }
        private void CleaningGoofyPlayer(Guid guid)
        {
            if (!GoofyPlayer.TryGetValue(guid, out var holder))
                return;

            GoofyPlayer.Remove(guid);

            if (holder == null || holder.character == null)
                return;

            // Destroy held item if they're holding this dynamite
            Item currentItem = holder.character.data.currentItem;
            if (currentItem != null && currentItem.data?.guid == guid)
            {
                holder.character.photonView.RPC("DestroyHeldItemRpc", holder.character.photonView.Owner);
                holder.character.photonView.RPC("RPCA_Chaos_EquipSlotRPC", holder.character.photonView.Owner, (byte)179);
            }

            // Destroy any registered world item
            if (TryGetWorldItem(guid, out var worldPv) && worldPv != null)
                PhotonNetwork.Destroy(worldPv);

            // Clear the temp slot
            if (!holder.tempFullSlot.IsEmpty() && holder.tempFullSlot.data?.guid == guid)
                holder.tempFullSlot.EmptyOut();

            var inv = IBinarySerializable.ToManagedArray(new InventorySyncData(holder.itemSlots, holder.backpackSlot, holder.tempFullSlot));
            holder.view.RPC("SyncInventoryRPC", RpcTarget.Others, inv, false);
        }
        private IEnumerator DelayedPickupAccepted(Player p, Guid guid)
        {
            if (p == null || p.character == null)
                yield break;

            yield return new WaitUntil(() => AddItemHelper.InstanceOwners.TryGetValue(guid, out var info) && info.player == p && !info.slot.IsEmpty() && info.slot.data?.guid == guid);

            if (!tracked.Contains(guid))
                yield break;

            PhotonView view = p.character.photonView;
            if (view == null)
                yield break;

            if (AddItemHelper.InstanceOwners.TryGetValue(guid, out var owner))
            {
                view.RPC("RPCA_Chaos_EquipSlotRPC", view.Owner, owner.slot.itemSlotID);
                p.character.refs.climbing.StopAnyClimbing();
            }
        }

        private void HandleExplosionForInstance(ItemInstanceData idata, bool hasWorldItem)
        {

            Player? owner = null;
            byte? slotId = null;
            Item? currentItem = null;

            if (AddItemHelper.InstanceOwners.TryGetValue(idata.guid, out var info))
            {
                owner = info.player;
                slotId = info.slot.itemSlotID;
            }

            if (owner == null || slotId == null)
                return;

            ItemSlot slot = info.slot;

            Vector3 spawnPos = Vector3.zero;
            Transform CharacterHip = owner.character.GetBodypart(BodypartType.Hip).transform;
            Vector3 center = CharacterHip.position;

            Campfire? camp = FindNearestUnlitCampfire(Character.localCharacter.Center);

            if (owner.character == null) return;

            currentItem = owner.character.data.currentItem;

            if (camp != null)
            {
                Vector3 firePos = camp.transform.position;

                Vector3 toFire = firePos - center;
                toFire.y = 0f;

                float maxRadius = 0.6f;

                Vector3 offset;
                if (toFire.sqrMagnitude > maxRadius * maxRadius)
                {
                    offset = toFire.normalized * maxRadius;
                }
                else
                {
                    offset = toFire;
                }
                spawnPos = center + offset;
            }
            else
            {
                spawnPos = center + CharacterHip.forward * 0.6f;
            }


            if (hasWorldItem)
            {
                TryGetWorldItem(idata.guid, out var pv);
                if (currentItem != null && currentItem.data.guid == idata.guid)
                {
                    owner.character.photonView.RPC("DestroyHeldItemRpc", owner.character.photonView.Owner);
                    owner.character.photonView.RPC("RPCA_Chaos_EquipSlotRPC", owner.character.photonView.Owner, (byte)179);
                    owner.GetItemSlot(owner.character.refs.items.currentSelectedSlot.Value).EmptyOut();
                }
                if (pv != null)
                {
                    PhotonNetwork.Destroy(pv);
                    //ModLogger.Log($"[DynamiteManager] Item detected - Deleting Item.");
                }

            }

            if (IsItemInSlot(owner, slot, idata, shouldExist: true))
            {
                try
                {
                    //ModLogger.Log($"[DynamiteManager] Still in slot {slot} - Deleting Item in slot.");
                    slot.EmptyOut();
                }
                catch (Exception ex)
                {
                    ModLogger.Log($"Error emptying slot for explosion: {ex}");
                }
            }

            var inv = IBinarySerializable.ToManagedArray(new InventorySyncData(owner.itemSlots, owner.backpackSlot, owner.tempFullSlot));
            owner.view.RPC("SyncInventoryRPC", RpcTarget.Others, inv, false);

            if (ItemDatabase.TryGetItem(ModItemIDs.Dynamite, out var dynamitePrefab))
            {
                var instantiated = PhotonNetwork.InstantiateItemRoom(dynamitePrefab.gameObject.name, spawnPos, Quaternion.identity);
                if (instantiated != null)
                {
                    var pv = instantiated.GetComponent<PhotonView>();
                    var item = instantiated.GetComponent<Item>();
                    if (pv != null && item != null)
                    {
                        pv.RPC("SetItemInstanceDataRPC", RpcTarget.AllBuffered, idata);
                        pv.RPC("RPC_Explode", RpcTarget.All);

                        PhotonNetwork.Destroy(instantiated.GetComponent<PhotonView>());
                        //ModLogger.Log($"[DynamiteManager] WE EXPLODEEE.");
                    }
                }
                else
                {
                    ModLogger.Log("[DynamiteManager] Failed to instantiate dynamite for explosion.");
                }
            }
            else
            {
                ModLogger.Log("[DynamiteManager] Could not find dynamite prefab in ItemDatabase to spawn explosion.");
            }
        }
        public static void RegisterDynamiteItem(Guid guid, PhotonView view)
        {
            if (view == null) return;
            if (!DynamiteItems.TryGetValue(guid, out var views))
            {
                views = new HashSet<PhotonView>();
                DynamiteItems[guid] = views;
            }

            views.Add(view);
        }

        public static void UnregisterDynamiteItem(Guid guid, PhotonView view)
        {
            if (view == null) return;

            if (DynamiteItems.TryGetValue(guid, out var views))
            {
                views.Remove(view);

                if (views.Count == 0)
                {
                    DynamiteItems.Remove(guid);
                }
            }
        }

        public static bool TryGetWorldItem(Guid guid, out PhotonView? view)
        {
            view = null;

            if (!DynamiteItems.TryGetValue(guid, out var views))
                return false;

            PhotonView? dead = null;

            foreach (var v in views)
            {
                if (v == null)
                {
                    dead = v;
                    continue;
                }

                view = v;
                return true;
            }

            if (dead != null)
            {
                views.Remove(dead);
                if (views.Count == 0)
                    DynamiteItems.Remove(guid);
            }

            return false;
        }

        public static Campfire? FindNearestUnlitCampfire(Vector3 fromPosition)
        {
            Campfire? nearest = null;
            float bestDistance = float.MaxValue;

            foreach (Campfire campfire in UnityEngine.Object.FindObjectsByType<Campfire>(FindObjectsSortMode.None))
            {
                if (campfire.state != Campfire.FireState.Off)
                    continue;

                float dist = Vector3.Distance(fromPosition, campfire.transform.position);

                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    nearest = campfire;
                }
            }
            return nearest;
        }

        private bool IsItemInSlot(Player p, ItemSlot slot, ItemInstanceData idata, bool shouldExist)
        {
            if (p == null || slot == null || idata == null)
                return false;

            bool inSlot = !slot.IsEmpty() && slot.data != null && slot.data.guid == idata.guid;
            return shouldExist ? inSlot : !inSlot;
        }

        private Character? FindTouchedCharacter(Vector3 dynamitePos, Character owner, float touchRadiusSqr)
        {
            foreach (var character in PlayerHandler.GetAllPlayerCharacters())
            {
                if (character == null) continue;
                if (character == owner) continue;
                if (character.isBot) continue;
                if (character.data.dead) continue;
                if (!character.data.fullyConscious) continue;

                if (GoofyPlayer.ContainsValue(character.player))
                    continue;

                if (CatchDynamiteEffect.availableSlots.TryGetValue(character.player.photonView.OwnerActorNr, out var slots))
                    if (slots.Count == 0) continue;

                var hip = character.GetBodypart(BodypartType.Hip)?.transform;
                if (hip == null) continue;

                Vector3 offset = dynamitePos - hip.position;
                if (offset.sqrMagnitude <= touchRadiusSqr)
                    return character;
            }
            return null;
        }
        private void TransferDynamiteToPlayer(Player from, Player to, ItemSlot fromSlot, ItemInstanceData idata)
        {
            pendingDrop.Remove(idata.guid);

            Item currentItem = from.character.data.currentItem;

            if (CatchDynamiteEffect.availableSlots.TryGetValue(from.photonView.OwnerActorNr, out var set))
            {
                set.Add(fromSlot.itemSlotID);
            }

            if (currentItem != null && currentItem.data.guid == idata.guid)
            {
                from.character.photonView.RPC("DestroyHeldItemRpc", from.character.photonView.Owner);
                from.character.photonView.RPC("RPCA_Chaos_EquipSlotRPC", from.character.photonView.Owner, (byte)179);
            }

            if (IsItemInSlot(from, fromSlot, idata, shouldExist: true))
            {
                from.EmptySlot(Optionable<byte>.Some(fromSlot.itemSlotID));
            }

            if (TryGetWorldItem(idata.guid, out var pv) && pv != null)
            {
                PhotonNetwork.Destroy(pv);
            }

            SpecialItems.SetModSpawned(idata, true);
            to.AddItem(ModItemIDs.Dynamite, idata, out var toSlot);

            StartCoroutine(DelayedPickupAccepted(to, idata.guid));
        }
    }
}
