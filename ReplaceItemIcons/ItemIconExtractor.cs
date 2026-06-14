using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace kim.present.lumaisland.replaceitemicons
{
    public class ItemIconExtractor : IDisposable
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

        private const string LogTag = "[Extract]";

        private readonly ManualLogSource _logger;
        private readonly ConfigEntry<bool> _extractEnabled;
        private readonly string _extractDirectory;

        private readonly Camera _camera;
        private readonly SpriteRenderer _renderer;

        public ItemIconExtractor(ManualLogSource logger, ConfigEntry<bool> extractEnabled, string extractDirectory)
        {
            _logger = logger;
            _extractEnabled = extractEnabled;
            _extractDirectory = extractDirectory;

            // Create camera object for capturing item textures
            GameObject camObj = new GameObject("ItemTextureExtractor_Camera")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _camera = camObj.AddComponent<Camera>();
            _camera.orthographic = true;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.clear;
            _camera.enabled = false;

            // Create sprite renderer object for drawing item textures 
            GameObject spriteObj = new GameObject("ItemTextureExtractor_Sprite")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            _renderer = spriteObj.AddComponent<SpriteRenderer>();

            // Set layer to unique for prevent render other objects
            spriteObj.layer = 31;
            _camera.cullingMask = 1 << 31;
        }

        public IEnumerator TryExtract()
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
                    using (GenericGetSpritePatch.Bypass())
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
                using (GenericGetSpritePatch.Bypass())
                    sprite = itemType.GetSprite();
                if (!sprite) continue;

                if (TryExtractSpriteSync(sprite, itemType.Name)) extractedCount++;
                else failedCount++;

                yield return null;
            }

            LogInfo($"Extracted {extractedCount} sprites, {skippedCount} skipped, {failedCount} failed");
        }

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

        private void RenderSpriteToTexture(Sprite sprite, RenderTexture captureRenderTexture)
        {
            _camera.targetTexture = captureRenderTexture;
            _renderer.sprite = sprite;

            Bounds spriteBounds = _renderer.bounds;
            _camera.transform.position = new Vector3(spriteBounds.center.x, spriteBounds.center.y, -10f);
            _camera.orthographicSize = Mathf.Max(spriteBounds.extents.x, spriteBounds.extents.y, 0.01f);

            _camera.Render();
        }

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

        private static void ReleaseInFlightExtract(InFlightExtract extract)
        {
            if (extract.CaptureRenderTexture) RenderTexture.ReleaseTemporary(extract.CaptureRenderTexture);
        }

        private void LogInfo(string message) => _logger.LogInfo(LogTag + " " + message);
        private void LogWarning(string message) => _logger.LogWarning(LogTag + " " + message);
        private void LogError(string message) => _logger.LogError(LogTag + " " + message);

        public void Dispose()
        {
            if (_camera) Object.Destroy(_camera.gameObject);
            if (_renderer) Object.Destroy(_renderer.gameObject);
        }

        private sealed class InFlightExtract
        {
            public InventoryItemsData ItemType;
            public string ItemName;
            public RenderTexture CaptureRenderTexture;
            public AsyncGPUReadbackRequest ReadbackRequest;
        }
    }
}