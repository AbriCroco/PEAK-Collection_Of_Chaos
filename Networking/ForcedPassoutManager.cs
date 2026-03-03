using System.Collections.Generic;

namespace Chaos.Networking
{
    public static class ForcedPassoutManager
    {
        static readonly HashSet<int> suppressed = new();
        static readonly HashSet<int> allowOnce = new();
        static readonly object locker = new();

        public static void Add(int viewId)
        {
            lock (locker) { suppressed.Add(viewId); allowOnce.Remove(viewId); }
        }

        public static void Remove(int viewId)
        {
            lock (locker) { suppressed.Remove(viewId); allowOnce.Remove(viewId); }
        }

        public static bool Contains(int viewId)
        {
            lock (locker) { return suppressed.Contains(viewId); }
        }

        public static void AllowOnce(int viewId)
        {
            lock (locker)
            {
                if (suppressed.Contains(viewId))
                    allowOnce.Add(viewId);
            }
        }

        public static bool ConsumeAllowOnce(int viewId)
        {
            lock (locker)
            {
                if (allowOnce.Contains(viewId))
                {
                    allowOnce.Remove(viewId);
                    return true;
                }
                return false;
            }
        }

        public static bool HasAllowOnce(int viewId)
        {
            lock (locker) { return allowOnce.Contains(viewId); }
        }
    }
}
