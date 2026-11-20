using System;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Midian.TextureTools
{
    //Small preview textures for the tool windows, built from full-size pixel data
    internal static class Previews
    {
        public const int MaxSize = 256;

        //A preview at most MaxSize on its longer side, keeping the aspect. sample gets UVs at preview pixel centres
        public static Texture2D Make(int sourceWidth, int sourceHeight, Func<float, float, Color> sample)
        {
            float scale = Mathf.Min(1f, MaxSize / (float)Mathf.Max(sourceWidth, sourceHeight));
            int width = Mathf.Max(1, Mathf.RoundToInt(sourceWidth * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(sourceHeight * scale));

            var pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = sample((x + 0.5f) / width, (y + 0.5f) / height);

            //Not linear: previews show the stored values as they'd look in an image viewer
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels(pixels);
            texture.Apply(false);
            return texture;
        }

        public static Color Nearest(PixelImage image, float u, float v)
        {
            int x = Mathf.Clamp((int)(u * image.Width), 0, image.Width - 1);
            int y = Mathf.Clamp((int)(v * image.Height), 0, image.Height - 1);
            return image.Pixels[y * image.Width + x];
        }

        public static Color Grey(float value)
        {
            return new Color(value, value, value, 1f);
        }

        //The light/dark squares shown behind transparent areas
        public static Color Checker(float u, float v)
        {
            bool light = ((int)(u * 16f) + (int)(v * 16f)) % 2 == 0;
            return light ? new Color(0.8f, 0.8f, 0.8f, 1f) : new Color(0.55f, 0.55f, 0.55f, 1f);
        }

        //Draws previews side by side with captions, each fitted into a box of the given height
        public static void DrawRow(float boxHeight, params (string caption, Texture2D texture)[] items)
        {
            EditorGUILayout.BeginHorizontal();
            foreach (var (caption, texture) in items)
            {
                EditorGUILayout.BeginVertical();
                EditorGUILayout.LabelField(caption, EditorStyles.miniBoldLabel);
                Rect rect = GUILayoutUtility.GetRect(boxHeight, boxHeight, GUILayout.ExpandWidth(true));
                if (texture != null) GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, false);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndHorizontal();
        }

        public static void Destroy(ref Texture2D texture)
        {
            if (texture != null) Object.DestroyImmediate(texture);
            texture = null;
        }
    }
}
