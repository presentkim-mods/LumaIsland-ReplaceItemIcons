using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;

namespace kim.present.lumaisland.replaceitemicons
{
    [BepInPlugin("kim.present.lumaisland.replaceitemicons", "ReplaceItemIcons", "1.0.1")]
    public class ReplaceItemIcons : BaseUnityPlugin
    {
        private static string ModDirectory => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        private ItemIconExtractor _extractor;
        private ItemIconReplacer _replacer;

        private void Awake()
        {
            _replacer = new ItemIconReplacer(Logger, ModDirectory);
            _extractor = new ItemIconExtractor(
                Logger,
                Config.Bind(
                    "Extract Settings",
                    "EnableItemIconExtract",
                    false,
                    "Extract the item texture to the 'extracted' directory in the directory where the mod dll is located."
                ),
                Path.Combine(ModDirectory, "extracted"));

            _replacer.LoadAllCustomIcons();
            StartCoroutine(_extractor.TryExtract());

            new Harmony("kim.present.lumaisland.replaceitemicons").PatchAll();
        }

        private void OnDestroy()
        {
            _replacer.Dispose();
            _extractor.Dispose();
        }
    }
}