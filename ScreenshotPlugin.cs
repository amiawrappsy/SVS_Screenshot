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
        public const string Version = "1.0.1";

        // Present only while an H scene is running; used to skip transparency there
        internal const string HSceneTypeName = "SV.H.HScene";

        // Scene where rendered screenshots are turned off because they can't reproduce what the screen shows
        // (only while no dialogue or H scene is running)
        internal const string NoRenderSceneTypeName = "SV.SimulationScene";

        // Dialogue backgrounds that a re-render can't reproduce: normal (non-transparent) renders during
        // dialogue capture the screen with the UI hidden instead
        internal static readonly bool DialogueUsesScreenCapture = true;

        // The dialogue capture hides every screen-overlay canvas (HUDs made only of images) instead of only those
        // that look like UI. Off where dialogue backgrounds can be overlay canvases (SamabakeScramble).
        internal static readonly bool DialogueCaptureHidesAllOverlays = false;

        // During dialogue, hide other 3D cameras (SamabakeScramble draws free-roam characters behind the conversation)
        internal static readonly bool HideOtherCamerasInDialogue = true;

        // Dialogue (ADV) is running when one of these components exists and, if a property is given, it's true.
        // Transparent renders during dialogue keep only the characters' layers.
        internal static readonly (string type, string property)[] DialogueIndicators =
        {};

        // Free-roam scene where transparency is turned off (the map is always in the way), except during dialogue
        internal const string NoTransparencySceneTypeName = null;

        // Cameras the game uses for specific jobs, read from these components when they exist:
        // dialogue (ADV) characters, and backgrounds that must never be hidden as UI
        internal static readonly (string type, string property, GameCameraRole role)[] GameCameras =
        {
            ("ADV.ADVScene", "ADVCamera", GameCameraRole.Characters),
            ("ADV.ADVScene", "BackCamera", GameCameraRole.Background),
            ("Manager.MapManager", "CameraBackGround", GameCameraRole.Background),
        };

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
        internal static ConfigEntry<bool> NoTransparencyInH;
        internal static ConfigEntry<bool> LogCameraDetails;

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
                "Hide UI (buttons, sliders and text) while rendering. Image-only layers, such as dialogue scene backgrounds, are kept.");
            NoTransparencyInH = Config.Bind("Render Settings", "Disable transparency in H scenes", true,
                "Always render H scenes normally, even when Transparent background is on. The map is always in the way there, so transparency isn't useful.");

            LogCameraDetails = Config.Bind("Advanced", "Log camera details", false,
                "Write the cameras, canvases and backgrounds used for each rendered screenshot to the BepInEx log. Useful when reporting a scene that renders wrong.");

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
