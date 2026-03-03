using System;
using System.Collections.Generic;

namespace Chaos.Networking
{
    // Caches a prepared preview per activation, keyed by (effectId, triggererName).
    // Stores both the display text and a numeric value used by Apply.
    public static class EffectIntroHandler
    {
        class Entry
        {
            public string Text = string.Empty;
            public float Value;
            public DateTime Expiry;
        }

        static readonly Dictionary<string, Entry> s_cache = new Dictionary<string, Entry>();
        static string Key(byte effectId, string triggererName) => effectId + ":" + (triggererName ?? string.Empty);

        public static void Set(byte effectId, string triggererName, string text, float value, float ttlSeconds = 20f)
        {
            var k = Key(effectId, triggererName);
            var e = new Entry
            {
                Text = text ?? string.Empty,
                Value = value,
                Expiry = DateTime.UtcNow.AddSeconds(Math.Max(1f, ttlSeconds))
            };
            lock (s_cache) s_cache[k] = e;
        }

        public static bool TryGet(byte effectId, string triggererName, out string text, out float value)
        {
            var k = Key(effectId, triggererName);
            lock (s_cache)
            {
                if (s_cache.TryGetValue(k, out var e))
                {
                    if (DateTime.UtcNow <= e.Expiry)
                    {
                        text = e.Text;
                        value = e.Value;
                        return true;
                    }
                    s_cache.Remove(k);
                }
            }
            text = string.Empty;
            value = float.NaN;
            return false;
        }

        // For UI display on any client: get the most recent non-expired entry for this effect id
        public static bool TryGetLatest(byte effectId, out string text, out float value)
        {
            DateTime? now = DateTime.UtcNow;
            string prefix = effectId + ":";
            string? bestKey = null;
            Entry? best = null;

            lock (s_cache)
            {
                var toRemove = new List<string>();
                foreach (var kv in s_cache)
                {
                    if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;

                    var e = kv.Value;
                    if (now > e.Expiry)
                    {
                        toRemove.Add(kv.Key);
                        continue;
                    }

                    if (best == null || e.Expiry > best.Expiry)
                    {
                        best = e;
                        bestKey = kv.Key;
                    }
                }

                foreach (var k in toRemove)
                    s_cache.Remove(k);
            }

            if (best != null)
            {
                text = best.Text;
                value = best.Value;
                return true;
            }

            text = string.Empty;
            value = float.NaN;
            return false;
        }

        public static bool Consume(byte effectId, string triggererName, out string text, out float value)
        {
            var k = Key(effectId, triggererName);
            lock (s_cache)
            {
                if (s_cache.TryGetValue(k, out var e))
                {
                    if (DateTime.UtcNow <= e.Expiry)
                    {
                        text = e.Text;
                        value = e.Value;
                        s_cache.Remove(k);
                        return true;
                    }
                    s_cache.Remove(k);
                }
            }
            text = string.Empty;
            value = float.NaN;
            return false;
        }
    }
}
