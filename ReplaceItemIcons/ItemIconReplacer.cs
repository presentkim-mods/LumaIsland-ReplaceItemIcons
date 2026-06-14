using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace kim.present.lumaisland.replaceitemicons
{
    /// <summary>
    /// Replaces in-game item sprites with custom PNG icons loaded from the mod directory.
    /// Patches <see cref="global::InventoryItemsExtensions.GetSprite"/> via Harmony.
    /// </summary>
    [HarmonyPatch(typeof(InventoryItemsExtensions), "GetSprite", typeof(InventoryItemsData), typeof(int?))]
    public class ItemIconReplacer : Feature, IDisposable
    {
        private static readonly Dictionary<string, Sprite> CustomIconSprites =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        private static bool _bypass;

        private readonly string _iconDirectory;

        /// <summary>
        /// Initializes a new instance of the <see cref="ItemIconReplacer"/> class.
        /// </summary>
        /// <param name="plugin">The owning BepInEx plugin instance.</param>
        /// <param name="logger">The BepInEx log source.</param>
        public ItemIconReplacer(BaseUnityPlugin plugin, ManualLogSource logger) : base(plugin, logger, "[Replace]")
        {
            _iconDirectory = Path.GetDirectoryName(plugin.Info.Location);
        }

        /// <summary>
        /// Loads all custom PNG icons from the mod directory into memory.
        /// </summary>
        public override void Run()
        {
            LoadAllCustomIcons();
        }

        /// <summary>
        /// Temporarily bypasses this patch so the original <c>GetSprite</c> logic
        /// runs instead of returning the custom icon.
        /// Use with <c>using (ItemIconReplacer.Bypass())</c> to automatically
        /// restore the patch when the scope ends.
        /// </summary>
        public static IDisposable Bypass()
        {
            _bypass = true;
            return new BypassScope();
        }

        /// <summary>
        /// Scans the mod directory for PNG files and loads them as sprite overrides.
        /// </summary>
        private void LoadAllCustomIcons()
        {
            if (!Directory.Exists(_iconDirectory))
                return;

            foreach (string filePath in Directory.GetFiles(_iconDirectory, "*.png"))
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
                    LogError($"Failed loading icon: {filePath}\n{ex}");
                }
            }

            LogInfo($"Loaded {CustomIconSprites.Count} custom icons");
        }

        /// <summary>
        /// Destroys all loaded custom sprites and releases their textures.
        /// </summary>
        public void Dispose()
        {
            foreach (Sprite sprite in CustomIconSprites.Values)
                DestroyCustomIcon(sprite);

            CustomIconSprites.Clear();
        }

        /// <summary>
        /// Safely destroys a sprite and its underlying texture.
        /// </summary>
        private static void DestroyCustomIcon(Sprite sprite)
        {
            if (!sprite) return;

            Texture2D texture = sprite.texture;
            Object.Destroy(sprite);
            if (texture) Object.Destroy(texture);
        }

        /// <summary>
        /// Harmony prefix for <c>InventoryItemsExtensions.GetSprite</c>.
        /// Returns the custom sprite if one exists for the requested item; otherwise falls through to the original method.
        /// </summary>
        [HarmonyPrefix]
        public static bool Prefix(InventoryItemsData itemType, int? skin, ref Sprite __result)
        {
            if (_bypass) return true;
            if (itemType is null || string.IsNullOrEmpty(itemType.Name)) return true;
            if (!CustomIconSprites.TryGetValue(itemType.Name, out Sprite customSprite)) return true;

            __result = customSprite;
            return false;
        }

        /// <summary>
        /// Resets the <c>_bypass</c> flag when disposed, re-enabling the custom icon patch.
        /// </summary>
        private sealed class BypassScope : IDisposable
        {
            public void Dispose() => _bypass = false;
        }
    }
}