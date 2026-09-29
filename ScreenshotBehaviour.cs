using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace SVS_Screenshot
{
    public class ScreenshotBehaviour : MonoBehaviour
    {
        public ScreenshotBehaviour(IntPtr ptr) : base(ptr) { }

        private const int UILayer = 5;

        private void Update()
        {
            try
            {
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
            ScreenCapture.CaptureScreenshot(path, rate);
            ScreenshotPlugin.Logger.LogMessage($"Screen captured ({Screen.width * rate}x{Screen.height * rate}): {Path.GetFileName(path)}");
        }

        // ---------------------------------------------------------------- render capture

        private static void CaptureRender()
        {
            var cam = FindMainCamera();
            if (cam == null)
            {
                ScreenshotPlugin.Logger.LogMessage("Screenshot failed: no active camera found");
                return;
            }

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

            bool alpha = ScreenshotPlugin.Transparency.Value;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            byte[] result;
            using (new UIHider(cam, ScreenshotPlugin.HideUI.Value))
            {
                if (!alpha)
                {
                    result = RenderPass(cam, outW, outH, rate, null, true);
                    // Opaque output: force alpha to 255 so PNG viewers don't show holes
                    for (int i = 3; i < result.Length; i += 4) result[i] = 255;
                }
                else
                {
                    var black = RenderPass(cam, outW, outH, rate, Color.black, false);
                    var white = RenderPass(cam, outW, outH, rate, Color.white, false);
                    var color = ScreenshotPlugin.TransparencyPostProcessing.Value
                        ? RenderPass(cam, outW, outH, rate, Color.black, true)
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
                                                   $"{(alpha ? " transparent" : "")} in {sw.ElapsedMilliseconds}ms: {Path.GetFileName(path)}");
            }
            finally
            {
                Object.Destroy(tex);
            }
        }

        /// <summary>
        /// Render the camera into an offscreen target of (w*rate, h*rate), read it back and box-downscale to (w, h).
        /// Returns RGBA32 bytes, bottom row first (Unity texture order).
        /// </summary>
        private static byte[] RenderPass(Camera cam, int w, int h, int rate, Color? clearColor, bool postProcessing)
        {
            int rw = w * rate, rh = h * rate;

            var rt = RenderTexture.GetTemporary(rw, rh, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, rate == 1 ? 4 : 1);
            var tex = new Texture2D(rw, rh, TextureFormat.RGBA32, false);

            var urpData = cam.GetUniversalAdditionalCameraData();
            var oldTarget = cam.targetTexture;
            var oldClear = cam.clearFlags;
            var oldBg = cam.backgroundColor;
            var oldPP = urpData != null && urpData.renderPostProcessing;
            var oldActive = RenderTexture.active;

            try
            {
                cam.targetTexture = rt;
                cam.aspect = (float)rw / rh;
                if (clearColor.HasValue)
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    var c = clearColor.Value;
                    c.a = 0f;
                    cam.backgroundColor = c;
                }
                if (urpData != null && !postProcessing)
                    urpData.renderPostProcessing = false;

                cam.Render();

                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rw, rh), 0, 0, false);

                var raw = tex.GetRawTextureData().AsSpan();
                return rate == 1 ? raw.ToArray() : Downscale(raw.ToArray(), rw, rh, rate);
            }
            finally
            {
                RenderTexture.active = oldActive;
                cam.targetTexture = oldTarget;
                cam.ResetAspect();
                cam.clearFlags = oldClear;
                cam.backgroundColor = oldBg;
                if (urpData != null) urpData.renderPostProcessing = oldPP;
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

        private static Camera FindMainCamera()
        {
            var main = Camera.main;
            if (main != null && main.isActiveAndEnabled && main.targetTexture == null) return main;

            // Fallback: the enabled base camera rendering to screen that sees the most layers
            Camera best = null;
            int bestLayers = -1;
            foreach (var cam in Camera.allCameras)
            {
                if (cam.targetTexture != null) continue;
                var data = cam.GetUniversalAdditionalCameraData();
                if (data != null && data.renderType != CameraRenderType.Base) continue;
                int layers = CountBits(cam.cullingMask);
                if (layers > bestLayers || (layers == bestLayers && best != null && cam.depth < best.depth))
                {
                    best = cam;
                    bestLayers = layers;
                }
            }
            return best;
        }

        private static int CountBits(int v)
        {
            int c = 0;
            for (uint u = (uint)v; u != 0; u &= u - 1) c++;
            return c;
        }

        /// <summary>
        /// Temporarily hides camera-space canvases and UI-only overlay cameras attached to the rendered camera.
        /// Screen-space-overlay canvases never end up in an offscreen render, so they need no handling.
        /// </summary>
        private sealed class UIHider : IDisposable
        {
            private readonly List<Behaviour> _disabled = new List<Behaviour>();

            public UIHider(Camera cam, bool hide)
            {
                if (!hide) return;

                foreach (var canvas in Object.FindObjectsOfType<Canvas>())
                {
                    if (canvas.enabled && canvas.isRootCanvas && canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera == cam)
                        Disable(canvas);
                }

                var data = cam.GetUniversalAdditionalCameraData();
                var stack = data?.cameraStack;
                if (stack == null) return;
                for (int i = 0; i < stack.Count; i++)
                {
                    var overlay = stack[i];
                    if (overlay == null || !overlay.enabled) continue;
                    int mask = overlay.cullingMask;
                    bool uiOnly = (mask & (1 << UILayer)) != 0 && (mask & ~(1 << UILayer)) == 0;
                    if (uiOnly) Disable(overlay);
                }
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
            }
        }
    }
}
