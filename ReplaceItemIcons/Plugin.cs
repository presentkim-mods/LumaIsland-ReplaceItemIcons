using BepInEx;
using HarmonyLib;

namespace kim.present.lumaisland.replaceitemicons
{
    [BepInPlugin("kim.present.lumaisland.replaceitemicons", "ReplaceItemIcons", "1.0.1")]
    public class ReplaceItemIcons : BaseUnityPlugin
    {
        private ItemIconExtractor _extractor;
        private ItemIconReplacer _replacer;

        private void Awake()
        {
            _replacer = new ItemIconReplacer(this, Logger);
            _extractor = new ItemIconExtractor(this, Logger);

            _replacer.Run();
            _extractor.Run();

            new Harmony(Info.Metadata.GUID).PatchAll();
        }

        private void OnDestroy()
        {
            _replacer.Dispose();
            _extractor.Dispose();
        }
    }
}