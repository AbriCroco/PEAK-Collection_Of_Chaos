using Zorro.Core;

namespace Chaos.Manager
{
    public static class ModItemIDs
    {
        public static ushort Dynamite { get; private set; }

        public static bool Initialized { get; private set; }

        public static void Initialize()
        {
            if (Initialized)
                return;

            var lookup = SingletonAsset<ItemDatabase>.Instance.itemLookup;

            foreach (var kvp in lookup)
            {
                if (kvp.Value == null)
                    continue;

                switch (kvp.Value.name)
                {
                    case "Dynamite":
                        Dynamite = kvp.Key;
                        break;
                }
            }

            Initialized = true;
        }
    }
}
