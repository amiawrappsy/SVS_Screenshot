using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace SVS_Screenshot
{
    /// <summary>
    /// Blocks the game's own file screenshots (F11 in-game), which otherwise fire together with this plugin's hotkeys.
    /// Only methods that save a screenshot to a file are blocked; methods that hand a texture back to the game
    /// (e.g. ScreenshotHandlerBase.TakeScreenshot, used for card photos) are left alone.
    /// Types are looked up by name so a type missing after a game update doesn't break the rest.
    /// </summary>
    internal static class BuiltInScreenshotPatches
    {
        private static readonly string[] Assemblies = { "Assembly-CSharp", "IL" };

        private static readonly (string type, string method)[] Targets =
        {
            // Used by the maker, H scenes and the simulation scene
            ("ScreenshotHandlerBase", "SaveScreenshot"),
            ("ScreenshotHandlerBase", "SaveScreenshotScreenSize"),
            ("ScreenshotHandlerBase", "SaveScreenshotSimple"),
            // Older component-based screenshot helpers in Assembly-CSharp. The global ScreenShot helper
            // is left alone because FaceScreenShot uses it.
            ("GameScreenShot", "Capture"),
            ("GameScreenShot", "UnityCapture"),
            ("ScreenShotEx", "Capture"),
            // The same helpers in IL.dll
            ("ILLGames.Component.ScreenShot", "Capture"),
            ("ILLGames.Unity.Component.GameScreenShot", "Capture"),
            ("ILLGames.Unity.Component.GameScreenShot", "UnityCapture"),
            ("ILLGames.Unity.ScreenShotEx", "Capture"),
            ("ILLGames.Unity.ScreenShot", "Capture"),
        };

        public static void Apply()
        {
            var harmony = new Harmony(ScreenshotPlugin.GUID);
            var prefix = new HarmonyMethod(typeof(BuiltInScreenshotPatches), nameof(BlockPrefix));

            var assemblies = Assemblies.Select(TryLoad).Where(a => a != null).ToArray();

            foreach (var (typeName, methodName) in Targets)
            {
                try
                {
                    var type = assemblies.Select(a => a.GetType(typeName, false)).FirstOrDefault(t => t != null);
                    if (type == null)
                    {
                        ScreenshotPlugin.Logger.LogDebug($"Built-in screenshot type {typeName} not present, skipping");
                        continue;
                    }
                    foreach (var method in AccessTools.GetDeclaredMethods(type).Where(m => m.Name == methodName))
                    {
                        harmony.Patch(method, prefix: prefix);
                        ScreenshotPlugin.Logger.LogDebug($"Blocking built-in screenshot method {type.Name}.{method.Name}");
                    }
                }
                catch (Exception e)
                {
                    ScreenshotPlugin.Logger.LogWarning($"Failed to block {typeName}.{methodName}: {e.Message}");
                }
            }
        }

        private static Assembly TryLoad(string name)
        {
            try { return Assembly.Load(name); }
            catch (Exception e)
            {
                ScreenshotPlugin.Logger.LogWarning($"Could not load {name}: {e.Message}");
                return null;
            }
        }

        // Skipping the original leaves bool methods returning false ("screenshot not started")
        private static bool BlockPrefix()
        {
            if (!ScreenshotPlugin.DisableBuiltInScreenshot.Value) return true;
            ScreenshotPlugin.Logger.LogDebug("Blocked built-in game screenshot");
            return false;
        }
    }
}
