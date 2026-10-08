using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SVS_Screenshot
{
    /// <summary>What a camera listed in <see cref="ScreenshotPlugin.GameCameras"/> is used for.</summary>
    public enum GameCameraRole
    {
        /// <summary>Draws the characters of a dialogue (ADV) scene.</summary>
        Characters,
        /// <summary>Draws a background image; its canvases are never hidden as UI.</summary>
        Background,
    }

    public class ScreenshotBehaviour : MonoBehaviour
    {
        public ScreenshotBehaviour(IntPtr ptr) : base(ptr) { }

        private const int UILayer = 5;

        // On-screen message overlays (MessageCenter) hidden while a screen capture is taken,
        // restored a couple of frames later together with this plugin's own message
        private static readonly List<Behaviour> _hiddenOverlays = new List<Behaviour>();
        private static int _restoreOverlaysFrame = -1;
        private static string _pendingMessage;

        private void Update()
        {
            try
            {
                if (_restoreOverlaysFrame >= 0 && Time.frameCount >= _restoreOverlaysFrame)
                    RestoreOverlays();

                if (ScreenshotPlugin.KeyToggleAlpha.Value.IsDown())
                {
                    ScreenshotPlugin.Transparency.Value = !ScreenshotPlugin.Transparency.Value;
                    ScreenshotPlugin.Logger.LogMessage("Transparent background " + (ScreenshotPlugin.Transparency.Value ? "ON" : "OFF"));
                }
                else if (ScreenshotPlugin.KeyCaptureRender.Value.IsDown())
                    CaptureRender();
                else if (ScreenshotPlugin.KeyCaptureScreen.Value.IsDown())
                    CaptureScreen();
            }
            catch (Exception e)
            {
                ScreenshotPlugin.Logger.LogError(e);
            }
        }

        // ---------------------------------------------------------------- screen capture

        private static void CaptureScreen()
        {
            var path = ScreenshotPlugin.GetFilename("", "png");
            var rate = Mathf.Clamp(ScreenshotPlugin.ScreenUpsampling.Value, 1, 4);

            // The capture happens at the end of this frame, so hide notifications now
            // and only show our own message once the capture is done
            HideOverlays();
            ScreenCapture.CaptureScreenshot(path, rate);
            _pendingMessage = $"Screen captured ({Screen.width * rate}x{Screen.height * rate}): {Path.GetFileName(path)}";
            _restoreOverlaysFrame = Time.frameCount + 2;
        }

        /// <summary>
        /// Captures the screen as shown, but with UI canvases (text, buttons) and notifications hidden for that
        /// frame, at a multiple of the window size close to the configured render width.
        /// </summary>
        private static void CaptureScreenWithoutUI(IList<Camera> backgroundCams)
        {
            var path = ScreenshotPlugin.GetFilename("", "png");
            int rate = Mathf.Clamp(Mathf.CeilToInt(ScreenshotPlugin.ResolutionX.Value / (float)Screen.width), 1, 4);

            HideOverlays();
            if (ScreenshotPlugin.HideUI.Value)
            {
                // Make UI canvases see-through rather than disabling them: some game UI scripts throw errors
                // when their canvas is switched off for a frame
                var canvasLog = new List<string>();
                foreach (var canvas in Object.FindObjectsOfType<Canvas>())
                {
                    if (!canvas.enabled || !canvas.isRootCanvas) continue;

                    // Games whose dialogue backgrounds can be overlay canvases (SamabakeScramble) only hide
                    // canvases that look like UI
                    if (!ScreenshotPlugin.DialogueCaptureHidesAllOverlays)
                    {
                        bool isUI = !backgroundCams.Any(c => canvas.worldCamera == c) && RenderScope.LooksLikeUI(canvas);
                        canvasLog.Add(DescribeCanvas(canvas) + (isUI ? " HIDDEN" : ""));
                        if (isUI) CullGraphics(canvas, keepFullWidth: false);
                        continue;
                    }

                    // Letterbox canvases can also hold HUD elements (e.g. Aicomi's relationship display):
                    // keep only the full-width bars
                    if (canvas.name.IndexOf("letterbox", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var culled = CullGraphics(canvas, keepFullWidth: true);
                        canvasLog.Add(DescribeCanvas(canvas) + (culled.Count > 0 ? $" kept bars, hid: {string.Join(", ", culled)}" : " kept"));
                        continue;
                    }

                    // Overlay canvases always draw on top of the scene, so they're never a background; hide all
                    // of them (e.g. image-only HUDs). Camera-space canvases can be backgrounds, so only those
                    // that look like UI are hidden.
                    bool hide = canvas.renderMode switch
                    {
                        RenderMode.ScreenSpaceOverlay => true,
                        RenderMode.ScreenSpaceCamera => !backgroundCams.Any(c => canvas.worldCamera == c) && RenderScope.LooksLikeUI(canvas),
                        _ => false,
                    };
                    canvasLog.Add(DescribeCanvas(canvas) + (hide ? " HIDDEN" : ""));
                    if (!hide) continue;

                    // Hide each graphic via CanvasRenderer.cull: game scripts don't notice it (disabling canvases made
                    // some throw errors) and fades/tweens don't reset it (they reset CanvasGroup alpha)
                    CullGraphics(canvas, keepFullWidth: false);
                }
                if (ScreenshotPlugin.LogCameraDetails.Value && canvasLog.Count > 0)
                    ScreenshotPlugin.Logger.LogInfo("Canvases for dialogue capture:\n  " + string.Join("\n  ", canvasLog));
            }

            ScreenCapture.CaptureScreenshot(path, rate);
            _pendingMessage = $"Captured dialogue scene without UI ({Screen.width * rate}x{Screen.height * rate}): {Path.GetFileName(path)}";
            _restoreOverlaysFrame = Time.frameCount + 2;
        }

        private static readonly List<CanvasRenderer> _culledRenderers = new List<CanvasRenderer>();

        /// <summary>
        /// Hides every graphic in the canvas via CanvasRenderer.cull, which game scripts don't notice.
        /// With keepFullWidth, graphics spanning nearly the full canvas width (letterbox bars) are kept.
        /// Returns the names of the hidden graphics.
        /// </summary>
        private static List<string> CullGraphics(Canvas canvas, bool keepFullWidth)
        {
            var names = new List<string>();
            var canvasRect = canvas.GetComponent<RectTransform>();
            foreach (var graphic in canvas.GetComponentsInChildren<Graphic>())
            {
                if (keepFullWidth && canvasRect != null &&
                    graphic.rectTransform.rect.width * graphic.rectTransform.lossyScale.x >=
                    canvasRect.rect.width * canvasRect.lossyScale.x * 0.9f) continue;
                var cr = graphic.canvasRenderer;
                if (cr == null || cr.cull) continue;
                cr.cull = true;
                _culledRenderers.Add(cr);
                names.Add(graphic.name);
            }
            return names;
        }

        private static void HideOverlays()
        {
            try
            {
                var type = FindManagedType("BepInEx.MessageCenterComponent");
                if (type == null) return;

                foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.From(type)))
                {
                    var behaviour = obj.TryCast<Behaviour>();
                    if (behaviour == null || !behaviour.enabled) continue;
                    behaviour.enabled = false;
                    _hiddenOverlays.Add(behaviour);
                }
            }
            catch (Exception e)
            {
                ScreenshotPlugin.Logger.LogDebug("Could not hide message overlay: " + e.Message);
            }
        }

        private static void RestoreOverlays()
        {
            foreach (var b in _hiddenOverlays)
                if (b != null) b.enabled = true;
            _hiddenOverlays.Clear();

            foreach (var cr in _culledRenderers)
                if (cr != null) cr.cull = false;
            _culledRenderers.Clear();
            _restoreOverlaysFrame = -1;

            if (_pendingMessage != null)
            {
                ScreenshotPlugin.Logger.LogMessage(_pendingMessage);
                _pendingMessage = null;
            }
        }

        // ---------------------------------------------------------------- render capture

        private static void CaptureRender()
        {
            bool alpha = ScreenshotPlugin.Transparency.Value;
            bool hideUI = ScreenshotPlugin.HideUI.Value;

            string alphaSkippedReason = null;
            if (alpha && ScreenshotPlugin.NoTransparencyInH.Value && InHScene())
            {
                alpha = false;
                alphaSkippedReason = "H scenes";
            }

            bool inDialogue = IsInDialogue();
            if (alpha && !inDialogue && IsSceneActive(ScreenshotPlugin.NoTransparencySceneTypeName))
            {
                alpha = false;
                alphaSkippedReason = "free roam";
            }

            if (ScreenshotPlugin.LogCameraDetails.Value)
            {
                LogCameraInventory();
                LogBackgrounds();
            }

            var gameCams = GetGameCameras();
            var backgroundCams = gameCams.Where(g => g.role == GameCameraRole.Background).Select(g => g.cam).ToList();
            // During dialogue, the game's ADV camera shows the characters that matter
            var advCam = gameCams.Where(g => g.role == GameCameraRole.Characters && g.cam.cullingMask != 0)
                .Select(g => g.cam).FirstOrDefault();

            // Some scenes (e.g. SamabakeScramble's free roam) can't be re-rendered faithfully;
            // a screen capture works fine there
            if (advCam == null && !InHScene() && IsSceneActive(ScreenshotPlugin.NoRenderSceneTypeName))
            {
                ScreenshotPlugin.Logger.LogMessage("Rendered screenshots aren't supported in free roam. Use the screen capture hotkey (F9 by default) instead.");
                return;
            }

            // Some games draw dialogue backgrounds in a way a re-render can't reproduce;
            // capture the screen with the UI hidden instead
            if (!alpha && (advCam != null || inDialogue) && ScreenshotPlugin.DialogueUsesScreenCapture)
            {
                CaptureScreenWithoutUI(backgroundCams);
                return;
            }

            // Opaque renders reproduce everything the screen shows (some scenes, like ADV dialogue,
            // layer several cameras). Transparent renders use only the camera showing the characters,
            // so that background cameras don't fill in the transparent areas.
            var cams = new List<Camera>();
            Camera isolatedOverlay = null;
            var hiddenOverlays = new List<Camera>();
            if (!alpha)
            {
                cams = GetScreenCameras();
                // In dialogue, leave out other 3D cameras (e.g. free-roam characters behind the ADV background)
                if (ScreenshotPlugin.HideOtherCamerasInDialogue && advCam != null && IsOverlay(advCam))
                {
                    var advBase = FindBaseCameraFor(advCam);
                    if (advBase != null)
                        hiddenOverlays.AddRange(GetStack(advBase).Where(c =>
                            c != advCam && c.enabled && !IsUIOnly(c.cullingMask) && !backgroundCams.Contains(c)));
                }
            }

            if (cams.Count == 0)
            {
                var main = advCam ?? FindMainCamera();
                if (main != null && IsOverlay(main))
                {
                    // Overlay cameras can't render on their own; render the base camera they're stacked on
                    var baseCam = FindBaseCameraFor(main);
                    if (baseCam != null)
                    {
                        cams.Add(baseCam);
                        if (alpha) isolatedOverlay = main;
                    }
                }
                else if (main != null)
                {
                    cams.Add(main);
                }
            }
            if (cams.Count == 0)
            {
                ScreenshotPlugin.Logger.LogMessage("Screenshot failed: no active camera found");
                return;
            }

            // In dialogue scenes where one camera draws both the map and the characters, a transparent
            // render keeps only the layers the characters are on
            int? characterMask = null;
            if (alpha && inDialogue)
            {
                var charCam = isolatedOverlay ?? cams[0];
                int layers = GetCharacterLayers(charCam);
                if (layers != 0) characterMask = layers;
            }

            if (ScreenshotPlugin.LogCameraDetails.Value)
                ScreenshotPlugin.Logger.LogInfo("Rendering cameras: " + string.Join(", ", cams.Select(DescribeCamera)) +
                                                (isolatedOverlay != null ? $"; showing only overlay {isolatedOverlay.name}" : "") +
                                                (hiddenOverlays.Count > 0 ? $"; hiding {string.Join(", ", hiddenOverlays.Select(c => c.name))}" : "") +
                                                (characterMask.HasValue ? $"; character layers only (mask 0x{characterMask.Value:X})" : "") +
                                                (gameCams.Count > 0 ? $"; game cameras: {string.Join(", ", gameCams.Select(g => $"{g.cam.name} ({g.role})"))}" : ""));

            int outW = ScreenshotPlugin.ResolutionX.Value;
            int outH = ScreenshotPlugin.MatchWindowAspect.Value
                ? Mathf.Max(2, Mathf.RoundToInt(outW * (float)Screen.height / Screen.width))
                : ScreenshotPlugin.ResolutionY.Value;

            int maxSize = SystemInfo.maxTextureSize;
            if (outW > maxSize || outH > maxSize)
            {
                ScreenshotPlugin.Logger.LogMessage($"Screenshot failed: {outW}x{outH} exceeds the GPU's max texture size of {maxSize}");
                return;
            }

            int rate = Mathf.Clamp(ScreenshotPlugin.DownscalingRate.Value, 1, 4);
            while (rate > 1 && (outW * rate > maxSize || outH * rate > maxSize)) rate--;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            byte[] result;
            using (new RenderScope(cams, alpha, isolatedOverlay, hideUI, backgroundCams, hiddenOverlays, characterMask))
            {
                if (!alpha)
                {
                    result = RenderPass(cams, outW, outH, rate, null, true);
                    // Opaque output: force alpha to 255 so PNG viewers don't show holes
                    for (int i = 3; i < result.Length; i += 4) result[i] = 255;
                }
                else
                {
                    var black = RenderPass(cams, outW, outH, rate, Color.black, false);
                    var white = RenderPass(cams, outW, outH, rate, Color.white, false);
                    var color = ScreenshotPlugin.TransparencyPostProcessing.Value
                        ? RenderPass(cams, outW, outH, rate, Color.black, true)
                        : black;
                    result = CombineAlpha(black, white, color);
                }
            }

            var tex = new Texture2D(outW, outH, TextureFormat.RGBA32, false);
            try
            {
                unsafe
                {
                    fixed (byte* p = result) tex.LoadRawTextureData((IntPtr)p, result.Length);
                }
                tex.Apply(false);

                bool jpg = !alpha && ScreenshotPlugin.Format.Value == ImageFormat.JPG;
                var encoded = jpg
                    ? ImageConversion.EncodeToJPG(tex, ScreenshotPlugin.JpgQuality.Value)
                    : ImageConversion.EncodeToPNG(tex);
                var bytes = encoded.AsSpan().ToArray();
                var path = ScreenshotPlugin.GetFilename(alpha ? "-alpha" : "", jpg ? "jpg" : "png");

                Task.Run(() =>
                {
                    try { File.WriteAllBytes(path, bytes); }
                    catch (Exception e) { ScreenshotPlugin.Logger.LogError("Failed to save screenshot: " + e); }
                });

                ScreenshotPlugin.Logger.LogMessage($"Rendered {outW}x{outH}{(rate > 1 ? $" ({rate}x supersampled)" : "")}" +
                                                   $"{(alpha ? " transparent" : "")}{(alphaSkippedReason != null ? $" (transparency is off in {alphaSkippedReason})" : "")}" +
                                                   $" in {sw.ElapsedMilliseconds}ms: {Path.GetFileName(path)}");
            }
            finally
            {
                Object.Destroy(tex);
            }
        }

        /// <summary>
        /// Saves and restores everything about a base camera (and the overlay cameras stacked on it)
        /// that a render pass changes.
        /// </summary>
        private sealed class CameraState
        {
            public readonly Camera Camera;
            private readonly UniversalAdditionalCameraData _urpData;
            private readonly RenderTexture _target;
            private readonly CameraClearFlags _clear;
            private readonly Color _bg;
            private readonly bool _pp;
            private readonly List<(Camera cam, UniversalAdditionalCameraData data, bool pp)> _overlays = new();

            public CameraState(Camera cam)
            {
                Camera = cam;
                _urpData = cam.GetUniversalAdditionalCameraData();
                _target = cam.targetTexture;
                _clear = cam.clearFlags;
                _bg = cam.backgroundColor;
                _pp = _urpData != null && _urpData.renderPostProcessing;

                foreach (var overlay in GetStack(cam))
                {
                    var data = overlay.GetUniversalAdditionalCameraData();
                    _overlays.Add((overlay, data, data != null && data.renderPostProcessing));
                }
            }

            public void Setup(RenderTexture rt, Color? clearColor, bool postProcessing)
            {
                float aspect = (float)rt.width / rt.height;
                Camera.targetTexture = rt;
                Camera.aspect = aspect;
                if (clearColor.HasValue)
                {
                    Camera.clearFlags = CameraClearFlags.SolidColor;
                    var c = clearColor.Value;
                    c.a = 0f;
                    Camera.backgroundColor = c;
                }
                if (_urpData != null && !postProcessing)
                    _urpData.renderPostProcessing = false;

                foreach (var (cam, data, _) in _overlays)
                {
                    cam.aspect = aspect;
                    if (data != null && !postProcessing) data.renderPostProcessing = false;
                }
            }

            public void Restore()
            {
                if (Camera == null) return;
                Camera.targetTexture = _target;
                Camera.ResetAspect();
                Camera.clearFlags = _clear;
                Camera.backgroundColor = _bg;
                if (_urpData != null) _urpData.renderPostProcessing = _pp;

                foreach (var (cam, data, pp) in _overlays)
                {
                    if (cam == null) continue;
                    cam.ResetAspect();
                    if (data != null) data.renderPostProcessing = pp;
                }
            }
        }

        /// <summary>
        /// Render the cameras (in order, the first one optionally cleared to clearColor) into an offscreen target
        /// of (w*rate, h*rate), read it back and box-downscale to (w, h).
        /// Returns RGBA32 bytes, bottom row first (Unity texture order).
        /// </summary>
        private static byte[] RenderPass(IList<Camera> cams, int w, int h, int rate, Color? clearColor, bool postProcessing)
        {
            int rw = w * rate, rh = h * rate;

            var rt = RenderTexture.GetTemporary(rw, rh, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, rate == 1 ? 4 : 1);
            var tex = new Texture2D(rw, rh, TextureFormat.RGBA32, false);
            var states = new List<CameraState>();
            var oldActive = RenderTexture.active;

            try
            {
                // Temporary render textures can contain old data; start from black in case no camera clears
                RenderTexture.active = rt;
                GL.Clear(true, true, Color.black);

                for (int i = 0; i < cams.Count; i++)
                {
                    var state = new CameraState(cams[i]);
                    states.Add(state);
                    state.Setup(rt, i == 0 ? clearColor : null, postProcessing);
                }

                foreach (var state in states)
                    state.Camera.Render();

                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rw, rh), 0, 0, false);

                var raw = tex.GetRawTextureData().AsSpan();
                return rate == 1 ? raw.ToArray() : Downscale(raw.ToArray(), rw, rh, rate);
            }
            finally
            {
                RenderTexture.active = oldActive;
                foreach (var state in states)
                    state.Restore();
                RenderTexture.ReleaseTemporary(rt);
                Object.Destroy(tex);
            }
        }

        private static byte[] Downscale(byte[] src, int srcW, int srcH, int rate)
        {
            int w = srcW / rate, h = srcH / rate;
            var dst = new byte[w * h * 4];
            int div = rate * rate;
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0;
                    for (int sy = 0; sy < rate; sy++)
                    {
                        int row = ((y * rate + sy) * srcW + x * rate) * 4;
                        for (int sx = 0; sx < rate; sx++)
                        {
                            int i = row + sx * 4;
                            r += src[i]; g += src[i + 1]; b += src[i + 2]; a += src[i + 3];
                        }
                    }
                    int o = (y * w + x) * 4;
                    dst[o] = (byte)(r / div);
                    dst[o + 1] = (byte)(g / div);
                    dst[o + 2] = (byte)(b / div);
                    dst[o + 3] = (byte)(a / div);
                }
            });
            return dst;
        }

        /// <summary>
        /// Recover alpha from renders over black and white backgrounds: a pixel with coverage a and color c
        /// shows as a*c over black and a*c + (1-a) over white, so a = 1 - (white - black).
        /// The color is then un-premultiplied from the black (or post-processed black) render.
        /// This works regardless of what the game's shaders write to the alpha channel.
        /// </summary>
        private static byte[] CombineAlpha(byte[] black, byte[] white, byte[] color)
        {
            var dst = new byte[black.Length];
            int pixels = black.Length / 4;
            Parallel.For(0, pixels, p =>
            {
                int i = p * 4;
                int diff = (white[i] - black[i]) + (white[i + 1] - black[i + 1]) + (white[i + 2] - black[i + 2]);
                int a = 255 - (diff + 1) / 3;
                if (a <= 0)
                {
                    dst[i] = dst[i + 1] = dst[i + 2] = dst[i + 3] = 0;
                    return;
                }
                if (a > 255) a = 255;
                dst[i] = (byte)Math.Min(255, color[i] * 255 / a);
                dst[i + 1] = (byte)Math.Min(255, color[i + 1] * 255 / a);
                dst[i + 2] = (byte)Math.Min(255, color[i + 2] * 255 / a);
                dst[i + 3] = (byte)a;
            });
            return dst;
        }

        // ---------------------------------------------------------------- camera selection

        /// <summary>
        /// The base cameras that draw to the screen, in the order the game renders them (each also renders
        /// its stack of overlay cameras). Rendering all of them in this order reproduces the screen.
        /// </summary>
        private static List<Camera> GetScreenCameras()
        {
            var cams = new List<Camera>();
            foreach (var cam in Camera.allCameras)
            {
                if (cam == null || !cam.isActiveAndEnabled || cam.targetTexture != null || cam.targetDisplay != 0) continue;
                if (IsOverlay(cam)) continue;
                cams.Add(cam);
            }
            cams.Sort((a, b) => a.depth.CompareTo(b.depth));
            return cams;
        }

        /// <summary>
        /// Reads the cameras listed in <see cref="ScreenshotPlugin.GameCameras"/> from the game's components,
        /// returning only active ones.
        /// </summary>
        private static List<(Camera cam, GameCameraRole role)> GetGameCameras()
        {
            var result = new List<(Camera, GameCameraRole)>();
            foreach (var (typeName, property, role) in ScreenshotPlugin.GameCameras)
            {
                try
                {
                    var type = FindManagedType(typeName);
                    if (type == null) continue;
                    var obj = Object.FindObjectOfType(Il2CppType.From(type));
                    if (obj == null) continue;
                    var instance = Activator.CreateInstance(type, obj.Pointer);
                    var cam = type.GetProperty(property)?.GetValue(instance) as Camera;
                    if (cam != null && cam.isActiveAndEnabled) result.Add((cam, role));
                }
                catch (Exception e)
                {
                    ScreenshotPlugin.Logger.LogDebug($"Could not read {typeName}.{property}: {e.Message}");
                }
            }
            return result;
        }

        private static Camera FindMainCamera()
        {
            var main = Camera.main;
            if (main != null && main.isActiveAndEnabled && main.targetTexture == null) return main;

            // Fallback: the enabled base camera rendering to screen that sees the most layers
            Camera best = null;
            int bestLayers = -1;
            foreach (var cam in Camera.allCameras)
            {
                if (cam.targetTexture != null || IsOverlay(cam)) continue;
                int layers = CountBits(cam.cullingMask);
                if (layers > bestLayers || (layers == bestLayers && best != null && cam.depth < best.depth))
                {
                    best = cam;
                    bestLayers = layers;
                }
            }
            return best;
        }

        private static Camera FindBaseCameraFor(Camera overlay)
        {
            foreach (var cam in Camera.allCameras)
            {
                if (cam == null || IsOverlay(cam)) continue;
                if (GetStack(cam).Any(c => c == overlay)) return cam;
            }
            return null;
        }

        private static bool IsOverlay(Camera cam)
        {
            var data = cam.GetUniversalAdditionalCameraData();
            return data != null && data.renderType == CameraRenderType.Overlay;
        }

        /// <summary>Overlay cameras stacked on a base camera (empty for overlay cameras themselves).</summary>
        private static List<Camera> GetStack(Camera cam)
        {
            var result = new List<Camera>();
            var data = cam.GetUniversalAdditionalCameraData();
            if (data == null || data.renderType != CameraRenderType.Base) return result;
            var stack = data.cameraStack;
            if (stack == null) return result;
            for (int i = 0; i < stack.Count; i++)
                if (stack[i] != null) result.Add(stack[i]);
            return result;
        }

        private static bool IsUIOnly(int mask) => (mask & (1 << UILayer)) != 0 && (mask & ~(1 << UILayer)) == 0;

        private static string DescribeCamera(Camera cam) =>
            $"{cam.name} ({(IsOverlay(cam) ? "overlay" : "base")}, depth {cam.depth}, clear {cam.clearFlags}, mask 0x{cam.cullingMask:X}" +
            $"{(cam.targetTexture != null ? $", renders to texture {cam.targetTexture.width}x{cam.targetTexture.height}" : "")})";

        /// <summary>Logs every active camera, to help diagnose scenes that render wrong.</summary>
        private static void LogCameraInventory()
        {
            try
            {
                var lines = new List<string>();
                foreach (var cam in Camera.allCameras)
                {
                    if (cam == null) continue;
                    var stack = GetStack(cam);
                    lines.Add(DescribeCamera(cam) + (stack.Count > 0 ? " stack: [" + string.Join(", ", stack.Select(c => c.name + (c.enabled ? "" : " (off)"))) + "]" : ""));
                }
                ScreenshotPlugin.Logger.LogInfo($"Active cameras (main: {(Camera.main != null ? Camera.main.name : "none")}):\n  " + string.Join("\n  ", lines));
            }
            catch (Exception e)
            {
                ScreenshotPlugin.Logger.LogDebug("Could not list cameras: " + e.Message);
            }
        }

        private static int CountBits(int v)
        {
            int c = 0;
            for (uint u = (uint)v; u != 0; u &= u - 1) c++;
            return c;
        }

        // ---------------------------------------------------------------- scene detection

        private static Type _hSceneType;
        private static bool _hSceneTypeResolved;

        private static bool InHScene()
        {
            try
            {
                if (!_hSceneTypeResolved)
                {
                    _hSceneTypeResolved = true;
                    _hSceneType = FindManagedType(ScreenshotPlugin.HSceneTypeName);
                }
                return _hSceneType != null && Object.FindObjectOfType(Il2CppType.From(_hSceneType)) != null;
            }
            catch (Exception e)
            {
                ScreenshotPlugin.Logger.LogDebug("Could not check for H scene: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Layers that characters are drawn on, among those the camera renders. Characters are skinned meshes
        /// (bodies, hair, clothes) while maps are almost entirely static, so the layers of visible skinned
        /// meshes identify them; accessories share those layers. Returns 0 if nothing was found.
        /// </summary>
        private static int GetCharacterLayers(Camera cam)
        {
            int mask = 0;
            try
            {
                foreach (var smr in Object.FindObjectsOfType<SkinnedMeshRenderer>())
                {
                    if (smr == null || !smr.enabled || !smr.gameObject.activeInHierarchy) continue;
                    int bit = 1 << smr.gameObject.layer;
                    if ((cam.cullingMask & bit) != 0) mask |= bit;
                }
            }
            catch (Exception e)
            {
                ScreenshotPlugin.Logger.LogDebug("Could not find character layers: " + e.Message);
            }
            return mask;
        }

        private static bool IsInDialogue()
        {
            foreach (var (typeName, property) in ScreenshotPlugin.DialogueIndicators)
            {
                try
                {
                    var type = FindManagedType(typeName);
                    if (type == null) continue;
                    var obj = Object.FindObjectOfType(Il2CppType.From(type));
                    if (obj == null) continue;
                    var behaviour = obj.TryCast<Behaviour>();
                    if (behaviour != null && !behaviour.isActiveAndEnabled) continue;
                    if (property == null) return true;
                    var instance = Activator.CreateInstance(type, obj.Pointer);
                    var value = type.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(instance);
                    if (ScreenshotPlugin.LogCameraDetails.Value)
                        ScreenshotPlugin.Logger.LogInfo($"Dialogue check: {typeName}.{property} = {value ?? "(not found)"}");
                    if (value is true) return true;
                }
                catch (Exception e)
                {
                    ScreenshotPlugin.Logger.LogDebug($"Could not check {typeName}.{property}: {e.Message}");
                }
            }
            return false;
        }

        private static bool IsSceneActive(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            try
            {
                var type = FindManagedType(typeName);
                return type != null && Object.FindObjectOfType(Il2CppType.From(type)) != null;
            }
            catch (Exception e)
            {
                ScreenshotPlugin.Logger.LogDebug($"Could not check for {typeName}: {e.Message}");
                return false;
            }
        }

        /// <summary>Logs every BackGroundParam (the games' background image component), to help diagnose missing backgrounds.</summary>
        private static void LogBackgrounds()
        {
            try
            {
                var lines = new List<string>();
                foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>())
                {
                    var il2CppType = mb.GetIl2CppType();
                    if (il2CppType.Name != "BackGroundParam") continue;
                    var type = FindManagedType(il2CppType.FullName);
                    if (type == null) continue;
                    var instance = Activator.CreateInstance(type, mb.Pointer);
                    var canvas = type.GetProperty("canvas")?.GetValue(instance) as Canvas;
                    var cam = type.GetProperty("cam")?.GetValue(instance) as Camera;
                    var image = type.GetProperty("image")?.GetValue(instance) as Image;
                    lines.Add($"{mb.gameObject.name} (active {mb.isActiveAndEnabled}): canvas {DescribeCanvas(canvas)}, " +
                              $"camera {(cam != null ? DescribeCamera(cam) + (cam.isActiveAndEnabled ? "" : " (off)") : "none")}, " +
                              $"image {(image != null ? $"{image.name} (shown {image.isActiveAndEnabled}, sprite {(image.sprite != null ? image.sprite.name : "none")})" : "none")}");
                }
                if (lines.Count > 0)
                    ScreenshotPlugin.Logger.LogInfo("Backgrounds:\n  " + string.Join("\n  ", lines));
            }
            catch (Exception e)
            {
                ScreenshotPlugin.Logger.LogDebug("Could not list backgrounds: " + e.Message);
            }
        }

        private static string DescribeCanvas(Canvas canvas) =>
            canvas == null ? "none" :
            $"{canvas.name} ({canvas.renderMode}, camera {(canvas.worldCamera != null ? canvas.worldCamera.name : "none")}, " +
            $"order {canvas.sortingOrder}, distance {canvas.planeDistance}, layer {canvas.gameObject.layer}, shown {canvas.isActiveAndEnabled})";

        private static Type FindManagedType(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(fullName, false))
                .FirstOrDefault(t => t != null);

        // ---------------------------------------------------------------- what to show while rendering

        /// <summary>
        /// Temporarily changes the scene for a render:
        /// - Opaque renders hide camera-space canvases that look like UI (buttons, sliders or text);
        ///   image-only canvases and anything drawn by a background camera are kept. Overlay cameras
        ///   in hiddenOverlays (other 3D cameras during dialogue) are switched off.
        /// - Transparent renders hide all camera-space canvases and every overlay camera except the one
        ///   showing the characters, and stop the base camera from drawing anything of its own when the
        ///   characters are on an overlay.
        /// Screen-space-overlay canvases never end up in an offscreen render, so they need no handling.
        /// </summary>
        private sealed class RenderScope : IDisposable
        {
            private readonly List<Behaviour> _disabled = new List<Behaviour>();
            private readonly List<(Camera cam, int mask)> _masks = new List<(Camera, int)>();

            public RenderScope(IList<Camera> cams, bool alpha, Camera isolatedOverlay, bool hideUI,
                               IList<Camera> backgroundCams, IList<Camera> hiddenOverlays, int? characterMask)
            {
                if (characterMask.HasValue)
                {
                    var charCam = isolatedOverlay ?? cams[0];
                    _masks.Add((charCam, charCam.cullingMask));
                    charCam.cullingMask &= characterMask.Value;
                }

                var canvasLog = new List<string>();
                foreach (var canvas in Object.FindObjectsOfType<Canvas>())
                {
                    if (!canvas.enabled || !canvas.isRootCanvas || canvas.renderMode == RenderMode.ScreenSpaceOverlay) continue;
                    bool hide = hideUI && canvas.renderMode == RenderMode.ScreenSpaceCamera &&
                                (alpha || (!backgroundCams.Any(c => canvas.worldCamera == c) && LooksLikeUI(canvas)));
                    if (hide) Disable(canvas);
                    canvasLog.Add(DescribeCanvas(canvas) + (hide ? " HIDDEN" : ""));
                }
                if (canvasLog.Count > 0 && ScreenshotPlugin.LogCameraDetails.Value)
                    ScreenshotPlugin.Logger.LogInfo("Camera-space canvases:\n  " + string.Join("\n  ", canvasLog));

                foreach (var overlay in hiddenOverlays)
                    Disable(overlay);

                if (!alpha) return;

                foreach (var cam in cams)
                {
                    foreach (var overlay in GetStack(cam))
                    {
                        if (!overlay.enabled) continue;
                        bool hide = isolatedOverlay != null ? overlay != isolatedOverlay : hideUI && IsUIOnly(overlay.cullingMask);
                        if (hide) Disable(overlay);
                    }
                    if (isolatedOverlay != null)
                    {
                        _masks.Add((cam, cam.cullingMask));
                        cam.cullingMask = 0;
                    }
                }
            }

            /// <summary>
            /// A canvas counts as UI if it shows text or a visible button/slider. Backgrounds (the games'
            /// BackGroundParam component) never count, even if they share a canvas with an invisible
            /// click-to-advance button.
            /// </summary>
            internal static bool LooksLikeUI(Canvas canvas)
            {
                foreach (var behaviour in canvas.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour != null && behaviour.GetIl2CppType().Name == "BackGroundParam")
                        return false;
                }

                foreach (var selectable in canvas.GetComponentsInChildren<Selectable>())
                {
                    var g = selectable.targetGraphic;
                    if (g != null && g.enabled && g.color.a > 0.01f) return true;
                }

                foreach (var graphic in canvas.GetComponentsInChildren<Graphic>())
                {
                    if (graphic.enabled && IsText(graphic)) return true;
                }
                return false;
            }

            private static bool IsText(Graphic graphic)
            {
                for (var t = graphic.GetIl2CppType(); t != null; t = t.BaseType)
                {
                    var name = t.Name;
                    if (name == "Text" || name == "TMP_Text") return true;
                    if (name == "Graphic") break;
                }
                return false;
            }

            private void Disable(Behaviour b)
            {
                b.enabled = false;
                _disabled.Add(b);
            }

            public void Dispose()
            {
                foreach (var b in _disabled)
                    if (b != null) b.enabled = true;
                foreach (var (cam, mask) in _masks)
                    if (cam != null) cam.cullingMask = mask;
            }
        }
    }
}
