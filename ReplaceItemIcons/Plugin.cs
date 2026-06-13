using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace kim.present.lumaisland.replaceitemicons
{
    [BepInPlugin("kim.present.lumaisland.replaceitemicons", "ReplaceItemIcons", "0.0.1")]
    public class ReplaceItemIcons : BaseUnityPlugin
    {
        public static readonly Dictionary<string, Sprite> CustomIconSprites =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        private static string ModDirectory => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        private ItemIconExtractor _extractor;

        private void Awake()
        {
            _extractor = new ItemIconExtractor(
                Logger,
                Config.Bind(
                    "Extract Settings",
                    "EnableItemIconExtract",
                    false,
                    "Extract the item texture to the 'extracted' directory in the directory where the mod dll is located."
                ),
                Path.Combine(ModDirectory, "extracted"));

            LoadAllCustomIcons();
            new Harmony("kim.present.lumaisland.replaceitemicons").PatchAll();

            StartCoroutine(_extractor.TryExtract());
        }

        private void OnDestroy()
        {
            _extractor.Dispose();
            UnloadAllCustomIcons();
        }

        private static void DestroyCustomIcon(Sprite sprite)
        {
            if (!sprite) return;

            Texture2D texture = sprite.texture;
            Destroy(sprite);
            if (texture) Destroy(texture);
        }

        private static void UnloadAllCustomIcons()
        {
            foreach (Sprite sprite in CustomIconSprites.Values) DestroyCustomIcon(sprite);

            CustomIconSprites.Clear();
        }

        private void LoadAllCustomIcons()
        {
            if (!Directory.Exists(ModDirectory))
                return;

            foreach (string filePath in Directory.GetFiles(ModDirectory, "*.png"))
            {
                try
                {
                    string itemName = Path.GetFileNameWithoutExtension(filePath);
                    byte[] fileData = File.ReadAllBytes(filePath);

                    Texture2D texture = new Texture2D(2, 2);
                    if (!texture.LoadImage(fileData))
                    {
                        Destroy(texture);
                        continue;
                    }

                    texture.filterMode = FilterMode.Point;
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
                    Logger.LogError($"Failed loading icon: {filePath}\n{ex}");
                }
            }

            Logger.LogInfo($"Loaded {CustomIconSprites.Count} custom icons");
        }
    }

    // ========================================================================================
    // [Harmony] Intercepts item sprite generation in real-time
    // ========================================================================================
    [HarmonyPatch(typeof(InventoryItemsExtensions), "GetSprite", typeof(InventoryItemsData), typeof(int?))]
    public class GenericGetSpritePatch
    {
        public static bool BypassCustomIcons = false;

        [HarmonyPrefix]
        public static bool Prefix(InventoryItemsData itemType, int? skin, ref Sprite __result)
        {
            if (BypassCustomIcons) return true;
            if (itemType is null || string.IsNullOrEmpty(itemType.Name)) return true;
            if (!ReplaceItemIcons.CustomIconSprites.TryGetValue(itemType.Name, out Sprite customSprite)) return true;

            __result = customSprite;
            return false;
        }
    }
}