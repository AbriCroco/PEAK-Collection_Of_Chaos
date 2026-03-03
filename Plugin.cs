using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;
using Chaos.Networking;
using Chaos.Manager;
using Chaos.Patches;
using Chaos.Utils;
using Chaos.UI;

namespace Chaos;

// Here are some basic resources on code style and naming conventions to help
// you in your first CSharp plugin!
// https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions
// https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/identifier-names
// https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/names-of-namespaces

// This BepInAutoPlugin attribute comes from the Hamunii.BepInEx.AutoPlugin
// NuGet package, and it will generate the BepInPlugin attribute for you!
// For more info, see https://github.com/Hamunii/BepInEx.AutoPlugin
[BepInAutoPlugin]
public partial class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource? Log { get; private set; }
    public readonly Harmony _harmony = new(Id);

    private void Awake()
    {
        Log = Logger;
        ModLogger.Log($"Plugin {Name} is loaded!");
        _harmony.PatchAll(Assembly.GetExecutingAssembly());

        if (FindFirstObjectByType<EffectMessages>() == null)
        {
            var go = new GameObject("EffectMessagesListener");
            go.AddComponent<EffectMessages>();
            DontDestroyOnLoad(go);
            //ModLogger.Log("Plugin: Created EffectMessages listener.");
        }

        if (FindFirstObjectByType<RandomEventManager>() == null)
        {
            var go = new GameObject("RandomEventManager");
            go.AddComponent<RandomEventManager>();
            DontDestroyOnLoad(go);
            //ModLogger.Log("Plugin: Created RandomEventManager.");
        }
        if (FindFirstObjectByType<Countdown>() == null)
        {
            var go = new GameObject("Countdown");
            go.AddComponent<Countdown>();
            DontDestroyOnLoad(go);
            //ModLogger.Log("Plugin: Created Countdown.");
        }
        if (FindFirstObjectByType<Chaos.UI.LocalNotice>() == null)
        {
            var go = new GameObject("Chaos_LocalNoticeCanvas");
            go.AddComponent<Chaos.UI.LocalNotice>();
            DontDestroyOnLoad(go);
            //ModLogger.Log("Plugin: Created LocalNotice overlay.");
        }
        /*try
        {
            ScoutmasterThrowPatcher.ApplyPatches();
            //ModLogger.Log("[Chaos/Plugin] ScoutmasterThrowPatcher.ApplyPatches() called.");
        }
        catch (Exception ex)
        {
            ModLogger.Log("[Chaos/Plugin] Error while applying ScoutmasterThrowPatcher: " + ex);
        }*/
    }
}
