using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace SVS_Screenshot
{
    public enum ImageFormat { PNG, JPG }

    [BepInPlugin(GUID, PluginName, Version)]
    [BepInProcess("SamabakeScramble")]
    public class ScreenshotPlugin : BasePlugin
    {
        public const string GUID = "SVS_Screenshot";
        public const string PluginName = "Screenshot Manager";
        public const string Version = "1.0.0";

        internal static ManualLogSource Logger;

        internal static ConfigEntry<KeyboardShortcut> KeyCaptureScreen;
        internal static ConfigEntry<KeyboardShortcut> KeyCaptureRender;
        internal static ConfigEntry<KeyboardShortcut> KeyToggleAlpha;

        internal static ConfigEntry<int> ResolutionX;
        internal static ConfigEntry<int> ResolutionY;
        internal static ConfigEntry<bool> MatchWindowAspect;
        internal static ConfigEntry<int> DownscalingRate;
        internal static ConfigEntry<bool> Transparency;
        internal static ConfigEntry<bool> TransparencyPostProcessing;
        internal static ConfigEntry<bool> HideUI;

        internal static ConfigEntry<int> ScreenUpsampling;

        internal static ConfigEntry<string> OutputFolder;
        internal static ConfigEntry<ImageFormat> Format;
        internal static ConfigEntry<int> JpgQuality;
        internal static ConfigEntry<bool> DisableBuiltInScreenshot;

        public override void Load()
        {
            Logger = Log;

            KeyCaptureScreen = Config.Bind("Hotkeys", "Capture screen", new KeyboardShortcut(KeyCode.F9),
                "Save a screenshot of the game window exactly as it looks (including UI), optionally upsampled.");
            KeyCaptureRender = Config.Bind("Hotkeys", "Capture render", new KeyboardShortcut(KeyCode.F11),
                "Re-render the main camera at the configured resolution (no UI), optionally with a transparent background.");
            KeyToggleAlpha = Config.Bind("Hotkeys", "Toggle transparency", new KeyboardShortcut(KeyCode.F11, KeyCode.LeftShift),
                "Toggle the 'Transparent background' setting.");

            ResolutionX = Config.Bind("Render Settings", "Resolution X", 3840,
                new ConfigDescription("Output width of rendered screenshots. Independent of the game window size.", new AcceptableValueRange<int>(2, 16384)));
            ResolutionY = Config.Bind("Render Settings", "Resolution Y", 2160,
                new ConfigDescription("Output height of rendered screenshots. Independent of the game window size.", new AcceptableValueRange<int>(2, 16384)));
            MatchWindowAspect = Config.Bind("Render Settings", "Match window aspect ratio", false,
                "Ignore Resolution Y and compute the height from Resolution X and the game window's aspect ratio, so the framing matches what you see.");
            DownscalingRate = Config.Bind("Render Settings", "Supersampling rate", 2,
                new ConfigDescription("Render at N times the output resolution and downscale, for high-quality anti-aliasing. " +
                                      "Reduced automatically if the render would exceed the GPU's max texture size.", new AcceptableValueRange<int>(1, 4)));
            Transparency = Config.Bind("Render Settings", "Transparent background", false,
                "Render with a transparent background (always saved as PNG). Anything the camera sees, such as a map or a 3D background, still shows up; hide it first.");
            TransparencyPostProcessing = Config.Bind("Render Settings", "Post-processing in transparent shots", true,
                "Keep post-processing effects (bloom, color grading and so on) in transparent renders. Takes an extra render pass. " +
                "Turn it off if semi-transparent edges such as hair look wrong.");
            HideUI = Config.Bind("Render Settings", "Hide camera-space UI", true,
                "Hide UI canvases and UI overlay cameras attached to the main camera while rendering.");

            ScreenUpsampling = Config.Bind("Screen Capture Settings", "Upsampling rate", 1,
                new ConfigDescription("Multiply the window resolution by this factor for screen captures (the UI is scaled up too).", new AcceptableValueRange<int>(1, 4)));

            OutputFolder = Config.Bind("Output", "Folder", "",
                "Folder to save screenshots to. Leave empty to use UserData/cap in the game folder.");
            Format = Config.Bind("Output", "Format", ImageFormat.PNG,
                "Image format for non-transparent screenshots. Transparent renders are always PNG.");
            JpgQuality = Config.Bind("Output", "JPG quality", 95,
                new ConfigDescription("JPG quality, from 1 to 100.", new AcceptableValueRange<int>(1, 100)));
            DisableBuiltInScreenshot = Config.Bind("Output", "Disable built-in game screenshot", true,
                "Disable the game's own screenshot hotkey so it doesn't fire together with this plugin's hotkeys.");

            BuiltInScreenshotPatches.Apply();
            AddComponent<ScreenshotBehaviour>();
        }

        internal static string GetOutputDir()
        {
            var dir = OutputFolder.Value;
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Paths.GameRootPath, "UserData", "cap");
            else if (!Path.IsPathRooted(dir))
                dir = Path.Combine(Paths.GameRootPath, dir);
            Directory.CreateDirectory(dir);
            return dir;
        }

        internal static string GetFilename(string suffix, string extension)
        {
            var name = $"{Paths.ProcessName}-{DateTime.Now:yyyy-MM-dd-HH-mm-ss-fff}{suffix}.{extension}";
            return Path.Combine(GetOutputDir(), name);
        }
    }
}
