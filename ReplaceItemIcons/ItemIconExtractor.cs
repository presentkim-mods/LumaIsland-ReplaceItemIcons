using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace kim.present.lumaisland.replaceitemicons
{
    /// <summary>
    /// Extracts inventory item sprites to PNG files using asynchronous GPU readback with synchronous fallback.
    /// </summary>
    public class ItemIconExtractor : Feature, IDisposable
    {
        // Extract pipeline tuning (see TryExtract):
        // - MaxInFlightReadbacks: max concurrent GPU readbacks. Each holds a RenderTexture until readback completes.
        //   Unity/GPU drivers cap pending AsyncGPUReadback requests; values above ~4 often cause hasError failures.
        // - MaxReadbackStartsPerFrame: camera renders + readback requests started per frame (GPU work on main thread).
        // - MaxPngWritesPerFrame: PNG encodes + file writes per frame (CPU-heavy; main cause of frame stutter).
        // Ratio guide: keep both per-frame caps <= MaxInFlightReadbacks; 2:1 in-flight to per-frame (e.g. 8/4/4) is a
        // balanced default. If readback errors appear, lower MaxInFlightReadbacks first. If stuttering, lower per-frame caps.
        private const int MaxInFlightReadbacks = 16;
        private const int MaxReadbackStartsPerFrame = 8;
        private const int MaxPngWritesPerFrame = 8;

        private const int CameraLayer = 31;

        private readonly ConfigEntry<bool> _extractEnabled;
        private readonly string _extractDirectory;

        private readonly Camera _camera;
        private readonly SpriteRenderer _renderer;

        /// <summary>
        /// Initializes a new instance of the <see cref="ItemIconExtractor"/> class.
        /// Creates a hidden camera and sprite renderer for capturing item textures.
        /// </summary>
        /// <param name="plugin">The owning BepInEx plugin instance.</param>
        /// <param name="logger">The BepInEx log source.</param>
        public ItemIconExtractor(BaseUnityPlugin plugin, ManualLogSource logger) : base(plugin, logger, "[Extract]")
        {
            _extractEnabled = plugin.Config.Bind(
                "Extract Settings",
                "EnableItemIconExtract",
                false,
                "Extract the item texture to the 'extracted' directory in the directory where the mod dll is located."
            );
            _extractDirectory = Path.Combine(
                Path.GetDirectoryName(plugin.Info.Location) ?? throw new InvalidOperationException(),
                "extracted"
            );

            // Create camera object for capturing item textures
            _camera = new GameObject("ItemTextureExtractor_Camera")
            {
                hideFlags = HideFlags.HideAndDontSave
            }.AddComponent<Camera>();
            _camera.orthographic = true;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.clear;
            _camera.enabled = false;
            _camera.cullingMask = 1 << CameraLayer;

            // Create sprite renderer object for drawing item textures 
            _renderer = new GameObject("ItemTextureExtractor_Sprite")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = CameraLayer
            }.AddComponent<SpriteRenderer>();
        }

        /// <summary>
        /// Starts the extraction coroutine if the feature is enabled via config.
        /// </summary>
        public override void Run()
        {
            Plugin.StartCoroutine(TryExtract());
        }

        /// <summary>
        /// Iterates all inventory items, renders each sprite to a render texture, and saves as PNG.
        /// Uses async GPU readback for performance with a synchronous fallback for failed readbacks.
        /// </summary>
        private IEnumerator TryExtract()
        {
            if (!_extractEnabled.Value) yield break;

            LogInfo("Trying to extract item textures to " + _extractDirectory);

            // Wait until the main game database and inventory system are fully loaded and accessible
            while (GameData.Instance?.InventoryItemsDB is null) yield return null;

            LogInfo("Starts to extract item textures on background");

            Directory.CreateDirectory(_extractDirectory);
            int extractedCount = 0;
            int skippedCount = 0;
            int failedCount = 0;

            InventoryItemsData[] items = GameData.Instance.InventoryItemsDB.dataArray;
            var inFlight = new List<InFlightExtract>(MaxInFlightReadbacks);
            var retryItems = new List<InventoryItemsData>();
            int itemIndex = 0;

            while (itemIndex < items.Length || inFlight.Count > 0)
            {
                int readbackStartsThisFrame = 0;
                int pngWritesThisFrame = 0;

                for (int i = inFlight.Count - 1; i >= 0; i--)
                {
                    InFlightExtract extract = inFlight[i];
                    if (!extract.ReadbackRequest.done) continue;
                    if (pngWritesThisFrame >= MaxPngWritesPerFrame) continue;

                    if (TryWriteExtractFromReadback(extract)) extractedCount++;
                    else retryItems.Add(extract.ItemType);

                    ReleaseInFlightExtract(extract);
                    inFlight.RemoveAt(i);
                    pngWritesThisFrame++;
                }

                while (itemIndex < items.Length
                       && inFlight.Count < MaxInFlightReadbacks
                       && readbackStartsThisFrame < MaxReadbackStartsPerFrame)
                {
                    InventoryItemsData itemType = items[itemIndex++];
                    if (itemType is null || string.IsNullOrWhiteSpace(itemType.Name)) continue;

                    string outputPath = Path.Combine(_extractDirectory, $"{itemType.Name}.png");
                    if (File.Exists(outputPath))
                    {
                        skippedCount++;
                        continue;
                    }

                    Sprite sprite;
                    using (ItemIconReplacer.Bypass())
                        sprite = itemType.GetSprite();
                    if (!sprite) continue;

                    InFlightExtract extract = StartExtractReadback(sprite, itemType);
                    if (extract is null)
                    {
                        retryItems.Add(itemType);
                        continue;
                    }

                    inFlight.Add(extract);
                    readbackStartsThisFrame++;
                }

                yield return null;
            }

            foreach (InventoryItemsData itemType in retryItems)
            {
                if (itemType is null || string.IsNullOrWhiteSpace(itemType.Name)) continue;

                string outputPath = Path.Combine(_extractDirectory, $"{itemType.Name}.png");
                if (File.Exists(outputPath))
                {
                    skippedCount++;
                    continue;
                }

                Sprite sprite;
                using (ItemIconReplacer.Bypass())
                    sprite = itemType.GetSprite();
                if (!sprite) continue;

                if (TryExtractSpriteSync(sprite, itemType.Name)) extractedCount++;
                else failedCount++;

                yield return null;
            }

            LogInfo($"Extracted {extractedCount} sprites, {skippedCount} skipped, {failedCount} failed");
        }

        /// <summary>
        /// Renders a sprite to a render texture and initiates an async GPU readback.
        /// </summary>
        private InFlightExtract StartExtractReadback(Sprite sprite, InventoryItemsData itemType)
        {
            RenderTexture captureRenderTexture = null;

            try
            {
                captureRenderTexture = RenderTexture.GetTemporary(
                    Mathf.CeilToInt(sprite.rect.width),
                    Mathf.CeilToInt(sprite.rect.height),
                    0,
                    RenderTextureFormat.ARGB32
                );
                RenderSpriteToTexture(sprite, captureRenderTexture);

                return new InFlightExtract
                {
                    ItemType = itemType,
                    ItemName = itemType.Name,
                    CaptureRenderTexture = captureRenderTexture,
                    ReadbackRequest = AsyncGPUReadback.Request(captureRenderTexture)
                };
            }
            catch (Exception ex)
            {
                LogError($"Failed to start extract for item: {itemType.Name}\n{ex}");
                if (captureRenderTexture) RenderTexture.ReleaseTemporary(captureRenderTexture);
                return null;
            }
            finally
            {
                _camera.targetTexture = null;
            }
        }

        /// <summary>
        /// Processes a completed async readback: writes PNG on success, falls back to synchronous extraction on error.
        /// </summary>
        private bool TryWriteExtractFromReadback(InFlightExtract extract)
        {
            if (extract.ReadbackRequest.hasError)
            {
                LogWarning($"GPU readback failed for item: {extract.ItemName}, trying synchronous fallback");
                return TryWriteExtractSync(extract.CaptureRenderTexture, extract.ItemName);
            }

            try
            {
                WritePngFromRgba(
                    extract.ReadbackRequest.GetData<byte>(),
                    extract.ReadbackRequest.width,
                    extract.ReadbackRequest.height,
                    extract.ItemName
                );
                return true;
            }
            catch (Exception ex)
            {
                LogError($"Failed to write item: {extract.ItemName}\n{ex}");
                return false;
            }
        }

        /// <summary>
        /// Synchronously renders a sprite to a render texture and writes it as PNG.
        /// Used as a fallback when async GPU readback fails.
        /// </summary>
        private bool TryExtractSpriteSync(Sprite sprite, string itemName)
        {
            RenderTexture captureRenderTexture = null;

            try
            {
                captureRenderTexture = RenderTexture.GetTemporary(
                    Mathf.CeilToInt(sprite.rect.width),
                    Mathf.CeilToInt(sprite.rect.height),
                    0,
                    RenderTextureFormat.ARGB32
                );
                RenderSpriteToTexture(sprite, captureRenderTexture);
                return TryWriteExtractSync(captureRenderTexture, itemName);
            }
            catch (Exception ex)
            {
                LogError($"Failed to extract item icon: {itemName}\n{ex}");
                return false;
            }
            finally
            {
                _camera.targetTexture = null;
                if (captureRenderTexture) RenderTexture.ReleaseTemporary(captureRenderTexture);
            }
        }

        /// <summary>
        /// Reads pixel data from a render texture via <c>ReadPixels</c> and writes it as a PNG file.
        /// </summary>
        private bool TryWriteExtractSync(RenderTexture captureRenderTexture, string itemName)
        {
            Texture2D readbackTexture = null;
            RenderTexture previousActive = RenderTexture.active;

            try
            {
                readbackTexture = new Texture2D(
                    captureRenderTexture.width,
                    captureRenderTexture.height,
                    TextureFormat.RGBA32,
                    false
                );
                RenderTexture.active = captureRenderTexture;
                readbackTexture.ReadPixels(new Rect(0, 0, captureRenderTexture.width, captureRenderTexture.height), 0,
                    0);
                readbackTexture.Apply();
                WritePngFromRgba(
                    readbackTexture.GetRawTextureData<byte>(),
                    readbackTexture.width,
                    readbackTexture.height,
                    itemName);
                return true;
            }
            catch (Exception ex)
            {
                LogError($"Synchronous extract failed for item: {itemName}\n{ex}");
                return false;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (readbackTexture) Object.Destroy(readbackTexture);
            }
        }

        /// <summary>
        /// Positions the hidden camera to frame the given sprite and renders it to the target render texture.
        /// </summary>
        private void RenderSpriteToTexture(Sprite sprite, RenderTexture captureRenderTexture)
        {
            _camera.targetTexture = captureRenderTexture;
            _renderer.sprite = sprite;

            Bounds spriteBounds = _renderer.bounds;
            _camera.transform.position = new Vector3(spriteBounds.center.x, spriteBounds.center.y, -10f);
            _camera.orthographicSize = Mathf.Max(spriteBounds.extents.x, spriteBounds.extents.y, 0.01f);

            _camera.Render();
        }

        /// <summary>
        /// Encodes raw RGBA data to PNG and writes it to the extraction directory.
        /// </summary>
        private void WritePngFromRgba(NativeArray<byte> rgba, int width, int height, string itemName)
        {
            var pngNativeArr = ImageConversion.EncodeNativeArrayToPNG(
                rgba,
                GraphicsFormat.R8G8B8A8_UNorm,
                (uint)width,
                (uint)height
            );
            try
            {
                File.WriteAllBytes(Path.Combine(_extractDirectory, $"{itemName}.png"), pngNativeArr.ToArray());
            }
            finally
            {
                pngNativeArr.Dispose();
            }
        }

        /// <summary>
        /// Releases the temporary render texture held by an in-flight extract.
        /// </summary>
        private static void ReleaseInFlightExtract(InFlightExtract extract)
        {
            if (extract.CaptureRenderTexture) RenderTexture.ReleaseTemporary(extract.CaptureRenderTexture);
        }

        /// <summary>
        /// Destroys the hidden camera and sprite renderer game objects.
        /// </summary>
        public void Dispose()
        {
            if (_camera) Object.Destroy(_camera.gameObject);
            if (_renderer) Object.Destroy(_renderer.gameObject);
        }

        /// <summary>
        /// Holds state for a single sprite extraction that is waiting for GPU readback to complete.
        /// </summary>
        private sealed class InFlightExtract
        {
            public InventoryItemsData ItemType;
            public string ItemName;
            public RenderTexture CaptureRenderTexture;
            public AsyncGPUReadbackRequest ReadbackRequest;
        }
    }
}