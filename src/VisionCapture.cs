using System;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Capture a low-res JPEG of the host view for Qwen vision (optional).
    /// </summary>
    public static class VisionCapture
    {
        public static bool TryCaptureJpegBase64(out string base64Jpeg, int maxWidth = 512, int quality = 40)
        {
            base64Jpeg = null;
            try
            {
                if (Plugin.VisionEnabled == null || !Plugin.VisionEnabled.Value)
                    return false;

                Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
                if (shot == null) return false;

                Texture2D resized = shot;
                if (shot.width > maxWidth)
                {
                    int w = maxWidth;
                    int h = Mathf.Max(1, (int)(shot.height * (maxWidth / (float)shot.width)));
                    resized = ScaleTexture(shot, w, h);
                    UnityEngine.Object.Destroy(shot);
                }

                byte[] jpg = resized.EncodeToJPG(Mathf.Clamp(quality, 20, 80));
                UnityEngine.Object.Destroy(resized);

                if (jpg == null || jpg.Length < 100) return false;
                base64Jpeg = Convert.ToBase64String(jpg);
                Plugin.Log?.LogInfo($"Vision capture {jpg.Length} bytes jpeg");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"VisionCapture: {ex.Message}");
                return false;
            }
        }

        private static Texture2D ScaleTexture(Texture2D src, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }
    }
}
