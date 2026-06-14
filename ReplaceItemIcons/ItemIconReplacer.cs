using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace kim.present.lumaisland.replaceitemicons
{
    [HarmonyPatch(typeof(InventoryItemsExtensions), "GetSprite", typeof(InventoryItemsData), typeof(int?))]
    public class ItemIconReplacer : IDisposable
    {
        private static readonly Dictionary<string, Sprite> CustomIconSprites =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        private static bool _bypass;

        private readonly ManualLogSource _logger;
        private readonly string _modDirectory;

        public ItemIconReplacer(ManualLogSource logger, string modDirectory)
        {
            _logger = logger;
            _modDirectory = modDirectory;
        }

        /**
         * Temporarily bypasses this patch so the original <c>GetSprite</c> logic
         * runs instead of returning the custom icon.
         * Use with <c>using (ItemIconReplacer.Bypass())</c> to automatically
         * restore the patch when the scope ends.
         */
        public static IDisposable Bypass()
        {
            _bypass = true;
            return new BypassScope();
        }

        public void LoadAllCustomIcons()
        {
            if (!Directory.Exists(_modDirectory))
                return;

            foreach (string filePath in Directory.GetFiles(_modDirectory, "*.png"))
            {
                try
                {
                    string itemName = Path.GetFileNameWithoutExtension(filePath);
                    byte[] fileData = File.ReadAllBytes(filePath);

                    Texture2D texture = new Texture2D(2, 2);
                    if (!texture.LoadImage(fileData))
                    {
                        Object.Destroy(texture);
                        continue;
                    }

                    texture.filterMode = FilterMode.Bilinear;
                    if (CustomIconSprites.TryGetValue(itemName, out Sprite existingSprite))
                        DestroyCustomIcon(existingSprite);

                    CustomIconSprites[itemName] =
                        Sprite.Create(
                            texture,
                            new Rect(0, 0, texture.width, texture.height),
                            new Vector2(0.5f, 0.5f)
                        );
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed loading icon: {filePath}\n{ex}");
                }
            }

            _logger.LogInfo($"Loaded {CustomIconSprites.Count} custom icons");
        }

        public void Dispose()
        {
            foreach (Sprite sprite in CustomIconSprites.Values)
                DestroyCustomIcon(sprite);

            CustomIconSprites.Clear();
        }

        private static void DestroyCustomIcon(Sprite sprite)
        {
            if (!sprite) return;

            Texture2D texture = sprite.texture;
            Object.Destroy(sprite);
            if (texture) Object.Destroy(texture);
        }

        [HarmonyPrefix]
        public static bool Prefix(InventoryItemsData itemType, int? skin, ref Sprite __result)
        {
            if (_bypass) return true;
            if (itemType is null || string.IsNullOrEmpty(itemType.Name)) return true;
            if (!CustomIconSprites.TryGetValue(itemType.Name, out Sprite customSprite)) return true;

            __result = customSprite;
            return false;
        }

        private sealed class BypassScope : IDisposable
        {
            public void Dispose() => _bypass = false;
        }
    }
}