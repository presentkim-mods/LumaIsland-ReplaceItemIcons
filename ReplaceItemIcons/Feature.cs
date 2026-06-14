using BepInEx;
using BepInEx.Logging;

namespace kim.present.lumaisland.replaceitemicons
{
    /// <summary>
    /// Provides a base class for mod features with logging support and a <see cref="BaseUnityPlugin"/> reference.
    /// </summary>
    public abstract class Feature
    {
        /// <summary>
        /// The owning BepInEx plugin instance.
        /// </summary>
        protected readonly BaseUnityPlugin Plugin;

        private readonly ManualLogSource _logger;
        private readonly string _prefix;

        /// <summary>
        /// Initializes a new instance of the <see cref="Feature"/> class.
        /// </summary>
        /// <param name="plugin">The owning BepInEx plugin instance.</param>
        /// <param name="logger">The BepInEx log source.</param>
        /// <param name="prefix">A tag prepended to all log messages (e.g. "[Extract]").</param>
        protected Feature(BaseUnityPlugin plugin, ManualLogSource logger, string prefix)
        {
            Plugin = plugin;
            _logger = logger;
            _prefix = prefix;
        }

        /// <summary>
        /// Executes the feature's main logic. Called once during plugin startup.
        /// </summary>
        public abstract void Run();

        /// <summary>
        /// Logs an informational message with the feature's prefix tag.
        /// </summary>
        protected void LogInfo(string message) => _logger.LogInfo(_prefix + " " + message);

        /// <summary>
        /// Logs a warning message with the feature's prefix tag.
        /// </summary>
        protected void LogWarning(string message) => _logger.LogWarning(_prefix + " " + message);

        /// <summary>
        /// Logs an error message with the feature's prefix tag.
        /// </summary>
        protected void LogError(string message) => _logger.LogError(_prefix + " " + message);
    }
}