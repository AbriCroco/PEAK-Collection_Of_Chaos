using System;
using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;
using Chaos.Utils;
using Chaos.Networking;
using Chaos.Manager;
using Chaos.Patches;

namespace Chaos.Effects
{
    public class CatchDynamiteEffect : IEffect
    {
        public byte Id => EffectIds.CatchDynamite;
        public string IntroMessage => "Is that a dynamite?!";
        public int CountdownSeconds => 3;
        private static float MinFuseTime = 30f;
        private static float MaxFuseTime = 75f;
        public static readonly List<Player> DynamiteVictims = new();

        public static readonly Dictionary<int, HashSet<byte>> availableSlots = new();

        public static class DynamiteFuseKeys
        {
            public static string Key(Guid guid) => $"Chaos_DynaFuse_{guid}";
        }

        public void Apply(string? triggererName = null)
        {
            if (!PhotonNetwork.IsMasterClient)
            {
                return;
            }

            // select all alive player
            var all = PlayerHandler.GetAllPlayerCharacters();
            var candidates = new List<Character>();
            foreach (var c in all)
            {
                if (c == null) continue;
                if (c.isBot) continue;
                if (c.data.dead) continue;
                if (!c.data.fullyConscious) continue;

                int key = c.player.photonView.OwnerActorNr;
                if (!availableSlots.TryGetValue(key, out var set))
                {
                    set = new HashSet<byte>();
                    foreach (var slot in c.player.itemSlots)
                        set.Add(slot.itemSlotID);

                    availableSlots[key] = set;
                }

                if (set.Count == 0) continue;

                candidates.Add(c);
            }

            if (candidates.Count == 0)
            {
                ModLogger.Log("[CatchDynamiteEffect] No valid candidates.");
                return;
            }

            var victims = new List<Character>();

            // make a random list of a maximum of 1/2 victims
            int maxVictims = Mathf.Max(1, candidates.Count / 2);
            int victimCount = UnityEngine.Random.Range(1, maxVictims + 1);

            for (int i = 0; i < victimCount; i++)
            {
                int index = UnityEngine.Random.Range(0, candidates.Count);
                victims.Add(candidates[index]);
                candidates.RemoveAt(index);
            }

            if (!ModItemIDs.Initialized)
            {
                ModLogger.Log("[CatchDynamiteEffect] ModItemIDs not initialized yet.");
                return;
            }

            ushort dynamiteID = ModItemIDs.Dynamite;
            float fuseTime = UnityEngine.Random.Range(MinFuseTime, MaxFuseTime);

            // Add Dynamite to each victim
            foreach (var v in victims)
            {
                if (v.player != null)
                {
                    DynamiteVictims.Add(v.player);

                    var instanceData = new ItemInstanceData(Guid.NewGuid());
                    ItemInstanceDataHandler.AddInstanceData(instanceData);

                    // set a custom fuse (seconds) and mark as lit
                    instanceData.RegisterEntry(DataEntryKey.Fuel, new FloatItemData { Value = fuseTime });
                    //instanceData.RegisterEntry(DataEntryKey.FlareActive, new BoolItemData { Value = true });

                    PhotonNetwork.CurrentRoom.SetCustomProperties(new ExitGames.Client.Photon.Hashtable { { DynamiteFuseKeys.Key(instanceData.guid), fuseTime } });

                    SpecialItems.SetModSpawned(instanceData, true);
                    v.player.AddItem(ModItemIDs.Dynamite, instanceData, out var slot);
                    ModLogger.Log($"[CatchDynamiteEffect] Dynamite spawned to {v.name}");
                }
                else
                {
                    ModLogger.Log($"[CatchDynamiteEffect] No Player found for character {v.name}");
                }
            }
        }
    }
}
