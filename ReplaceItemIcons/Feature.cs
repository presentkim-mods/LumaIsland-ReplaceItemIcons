using BepInEx;
using BepInEx.Logging;

namespace kim.present.lumaisland.replaceitemicons
{
    public abstract class Feature
    {
        protected readonly BaseUnityPlugin Plugin;
        private readonly ManualLogSource _logger;
        private readonly string _prefix;

        protected Feature(BaseUnityPlugin plugin, ManualLogSource logger, string prefix)
        {
            Plugin = plugin;
            _logger = logger;
            _prefix = prefix;
        }

        public abstract void Run();

        protected void LogInfo(string message) => _logger.LogInfo(_prefix + " " + message);
        protected void LogWarning(string message) => _logger.LogWarning(_prefix + " " + message);
        protected void LogError(string message) => _logger.LogError(_prefix + " " + message);
    }
}