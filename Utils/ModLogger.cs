using System.IO;
using UnityEngine;

namespace Chaos.Utils
{
    public static class ModLogger
    {
        private static readonly string logPath = Path.Combine(Application.persistentDataPath, "mod_log.txt");

        public static void Log(string message)
        {
            try
            {
                File.AppendAllText(logPath, $"[{System.DateTime.Now:HH:mm:ss}] {message}\n");
            }
            catch
            {
                // Ignore file errors in case of permission issues
            }
        }
    }
}
