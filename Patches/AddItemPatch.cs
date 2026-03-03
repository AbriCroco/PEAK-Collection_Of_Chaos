using System;
using HarmonyLib;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Zorro.Core.Serizalization;
using Photon.Pun;
using Chaos.Manager;
using Chaos.Utils;
using Chaos.Effects;

namespace Chaos.Patches
{
    [HarmonyPatch(typeof(Player), "AddItem")]
    class AddItemPatch
    {
        static bool Prefix(ushort itemID, ref ItemInstanceData instanceData, ref ItemSlot? slot, Player __instance)
        {
            if (!SpecialItems.IsSpecialItem(itemID))
                return true;

            if (!SpecialItems.IsModSpawned(instanceData) && itemID == ModItemIDs.Dynamite)
                return true;


            if (instanceData == null)
            {
                instanceData = new ItemInstanceData(Guid.NewGuid());
                ItemInstanceDataHandler.AddInstanceData(instanceData);
            }
            if (!PhotonNetwork.IsMasterClient)
            {
                slot = null;
                return false;
            }

            if (!ItemDatabase.TryGetItem(itemID, out var ItemPrefab))
            {
                slot = null;
                return false;
            }

            var helperGO = __instance.gameObject.GetComponent<AddItemHelper>();
            if (helperGO == null)
                helperGO = __instance.gameObject.AddComponent<AddItemHelper>();

            helperGO.AddItemWithDrop(__instance, ItemPrefab, instanceData);
            return false;
        }
        static void Postfix(ushort itemID, ref ItemInstanceData instanceData, ref ItemSlot? slot, Player __instance, bool __result)
        {
            if (!__result || slot == null)
                return;

            if (itemID != ModItemIDs.Dynamite)
                return;

            if (instanceData == null || !DynamiteManager.IsTracked(instanceData.guid))
                return;

            DynamiteManager.RegisterDynamitePickedup(instanceData.guid, __instance, slot);
        }
    }

    public static class SpecialItems
    {
        private static readonly HashSet<ushort> RestrictedItems = new()
        {
            ModItemIDs.Dynamite
        };

        private static readonly ConditionalWeakTable<ItemInstanceData, ModFlags> table = new();

        public class ModFlags
        {
            public bool ModSpawnedSpecialItem = false;
        }

        public static bool IsSpecialItem(ushort itemID) => RestrictedItems.Contains(itemID);

        public static void SetModSpawned(ItemInstanceData data, bool value) =>
            table.GetOrCreateValue(data).ModSpawnedSpecialItem = value;

        public static bool IsModSpawned(ItemInstanceData data)
        {
            if (data == null) return false;
            return table.TryGetValue(data, out var flags) && flags.ModSpawnedSpecialItem;
        }
    }

    public class AddItemHelper : MonoBehaviour
    {
        private static Item? _dynamiteItem;
        public static Item DynamiteItem
        {
            get
            {
                if (_dynamiteItem == null)
                {
                    ItemDatabase.TryGetItem(ModItemIDs.Dynamite, out _dynamiteItem);
                }
                return _dynamiteItem;
            }
        }
        public static readonly Dictionary<Guid, (Player player, ItemSlot slot)> InstanceOwners = new();

        public void AddItemWithDrop(Player p, Item itemPrefab, ItemInstanceData instanceData)
        {
            if (!PhotonNetwork.IsMasterClient)
                return;

            if (itemPrefab == DynamiteItem)
            {
                StartCoroutine(SpawnLitDynamite(p, instanceData));
                return;
            }

            for (int i = 0; i < p.itemSlots.Length; i++)
            {
                if (p.itemSlots[i].IsEmpty())
                {
                    if (p.itemSlots[i].itemSlotID == p.character.refs.items.currentSelectedSlot.Value)
                    {
                        p.character.photonView.RPC("RPCA_Chaos_EquipSlotRPC", p.character.photonView.Owner, (byte)179);
                    }

                    p.itemSlots[i].SetItem(itemPrefab, instanceData);

                    SpecialItems.SetModSpawned(instanceData, false);
                    byte[] syncData = IBinarySerializable.ToManagedArray(new InventorySyncData(p.itemSlots, p.backpackSlot, p.tempFullSlot));
                    p.view.RPC("SyncInventoryRPC", RpcTarget.Others, syncData, false);
                    return;
                }
            }

            int index = UnityEngine.Random.Range(0, 3);
            ItemSlot slotToDrop = p.itemSlots[index];

            StartDropThenAdd(p, slotToDrop, itemPrefab, instanceData);
        }

        private IEnumerator SpawnLitDynamite(Player p, ItemInstanceData instanceData)
        {
            float currentFuse = 30f;
            if (instanceData.TryGetDataEntry<FloatItemData>(DataEntryKey.Fuel, out var fd))
            {
                currentFuse = fd.Value;
            }

            if (!CatchDynamiteEffect.availableSlots.TryGetValue(p.photonView.OwnerActorNr, out var slots) || slots.Count == 0)
                yield break;

            for (int i = 0; i < p.itemSlots.Length; i++)
            {
                if (p.itemSlots[i].IsEmpty() && slots.Contains(p.itemSlots[i].itemSlotID))
                {
                    if (p.itemSlots[i].itemSlotID == p.character.refs.items.currentSelectedSlot.Value)
                    {
                        p.character.photonView.RPC("RPCA_Chaos_EquipSlotRPC", p.character.photonView.Owner, (byte)179);
                    }

                    InstanceOwners[instanceData.guid] = (p, p.itemSlots[i]);
                    CatchDynamiteEffect.availableSlots[p.photonView.OwnerActorNr].Remove(p.itemSlots[i].itemSlotID);

                    p.itemSlots[i].SetItem(DynamiteItem, instanceData);

                    if (PhotonNetwork.IsMasterClient)
                    {
                        if (DynamiteManager.Instance == null)
                        {
                            var goMgr = new GameObject("DynamiteManager");
                            DontDestroyOnLoad(goMgr);
                            goMgr.AddComponent<DynamiteManager>();
                            yield return null;
                        }
                        DynamiteManager.Instance?.RegisterInstance(instanceData);
                        //ModLogger.Log($"[SpawnLitDynamite] registered instance {instanceData.guid} (fuse={currentFuse})");
                    }

                    SpecialItems.SetModSpawned(instanceData, false);
                    byte[] syncData = IBinarySerializable.ToManagedArray(new InventorySyncData(p.itemSlots, p.backpackSlot, p.tempFullSlot));
                    p.view.RPC("SyncInventoryRPC", RpcTarget.Others, syncData, false);

                    //ModLogger.Log($"[SpawnLitDynamite] placed instance {instanceData.guid} into slot {i} of player {p.name}");
                    yield break;
                }
            }

            int target = UnityEngine.Random.Range(0, slots.Count);
            byte slotId = default;

            int j = 0;
            foreach (var id in slots)
            {
                if (j++ == target)
                {
                    slotId = id;
                    break;
                }
            }

            ItemSlot slotToDrop = p.GetItemSlot(slotId);

            if (slotToDrop == null)
                yield break;

            CatchDynamiteEffect.availableSlots[p.photonView.OwnerActorNr].Remove(slotToDrop.itemSlotID);
            StartDropThenAdd(p, slotToDrop, DynamiteItem, instanceData);
        }

        public void StartDropThenAdd(Player p, ItemSlot slotToDrop, Item itemPrefab, ItemInstanceData instanceData)
        {
            if (!PhotonNetwork.IsMasterClient)
                return;
            StartCoroutine(DropThenAdd(p, slotToDrop, itemPrefab, instanceData));
        }

        private IEnumerator DropThenAdd(Player p, ItemSlot slotToDrop, Item itemPrefab, ItemInstanceData instanceData)
        {
            if (slotToDrop == null)
            {
                ModLogger.Log("[DropThenAdd] Slot or data is null!");
                yield break;
            }

            if (!slotToDrop.IsEmpty())
            {
                ItemSlot currentSlot = p.GetItemSlot(p.character.refs.items.currentSelectedSlot.Value);
                Item currentItem = p.character.data.currentItem;

                if (currentItem != null && currentSlot.data == slotToDrop.data)
                {
                    p.character.photonView.RPC("DestroyHeldItemRpc", p.character.photonView.Owner);
                    p.character.photonView.RPC("RPCA_Chaos_EquipSlotRPC", p.character.photonView.Owner, (byte)179);
                }

                Transform hip = p.character.GetBodypart(BodypartType.Hip).transform;
                Vector3 spawnPos = hip.position + hip.forward * 0.6f;

                if (ItemDatabase.TryGetItem(slotToDrop.prefab.itemID, out var droppedPrefab))
                {
                    var spawnedObj = PhotonNetwork.InstantiateItemRoom(droppedPrefab.gameObject.name, spawnPos, Quaternion.identity);

                    if (spawnedObj != null)
                    {
                        var spawnedView = spawnedObj.GetComponent<PhotonView>();
                        if (spawnedView != null && slotToDrop.data != null)
                            spawnedView.RPC("SetItemInstanceDataRPC", RpcTarget.AllBuffered, slotToDrop.data);
                    }
                }

                slotToDrop.EmptyOut();
            }

            InstanceOwners[instanceData.guid] = (p, slotToDrop);
            slotToDrop.SetItem(itemPrefab, instanceData);

            if (itemPrefab.name == "Dynamite")
            {
                if (DynamiteManager.Instance == null)
                {
                    var goMgr = new GameObject("DynamiteManager");
                    DontDestroyOnLoad(goMgr);
                    goMgr.AddComponent<DynamiteManager>();
                    yield return null;
                }
                DynamiteManager.Instance?.RegisterInstance(instanceData);
            }

            SpecialItems.SetModSpawned(instanceData, false);
            byte[] syncData = IBinarySerializable.ToManagedArray(new InventorySyncData(p.itemSlots, p.backpackSlot, p.tempFullSlot));
            p.view.RPC("SyncInventoryRPC", RpcTarget.Others, syncData, false);
        }
    }
}
