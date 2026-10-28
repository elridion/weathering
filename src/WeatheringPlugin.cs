using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;

namespace Weathering
{
    /// <summary>
    /// Lets player terrain edits outside bases weather back into the land. The plugin only binds the
    /// config; the work is done by Harmony patches and the heal runner. Every setting is read live.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    internal class WeatheringPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "elridion.weathering";
        public const string PluginName = "Weathering";
        public const string PluginVersion = "0.1.0";

        public static ConfigEntry<float> DelayDays;
        public static ConfigEntry<float> FadeDays;
        public static ConfigEntry<float> ErosionRate;
        public static ConfigEntry<float> BaseMargin;
        public static ConfigEntry<float> PieceMargin;
        public static ConfigEntry<string> ExtraBasePieces;

        private Harmony _harmony;

        private void Awake()
        {
            var adminOnly = new ConfigurationManagerAttributes { IsAdminOnly = true };

            DelayDays = Config.Bind("General", "DelayDays", 3f,
                new ConfigDescription("In-game days after a spot was last edited before it starts to heal, and after a tree was felled before its sapling appears.",
                    new AcceptableValueRange<float>(0.1f, 30f), adminOnly));
            FadeDays = Config.Bind("General", "FadeDays", 2f,
                new ConfigDescription("In-game days dirt and cultivated paint take to fade fully once healing starts.",
                    new AcceptableValueRange<float>(0.1f, 30f), adminOnly));
            ErosionRate = Config.Bind("General", "ErosionRate", 1f,
                new ConfigDescription("Relative strength of height erosion: 1 is the default look, 0 turns height erosion off, 2 is twice as fast.",
                    new AcceptableValueRange<float>(0f, 5f), adminOnly));
            BaseMargin = Config.Bind("General", "BaseMargin", 10f,
                new ConfigDescription("Extra protected distance in meters around every player base area.",
                    new AcceptableValueRange<float>(0f, 50f), adminOnly));
            PieceMargin = Config.Bind("General", "PieceMargin", 4f,
                new ConfigDescription("Protected distance in meters around any player-built piece.",
                    new AcceptableValueRange<float>(0f, 20f), adminOnly));
            ExtraBasePieces = Config.Bind("General", "ExtraBasePieces", "",
                new ConfigDescription("Extra prefabs that count as a base, as a comma-separated list of prefab:radius in meters " +
                    "(for example piece_example:30, radius above 0, up to 500). Use it for modded pieces that vanilla does not treat as a base.",
                    null, adminOnly));

            // Fires on every ZNetScene load, after Jotunn added modded prefabs, so their base areas count.
            PrefabManager.OnPrefabsRegistered += BaseIndex.Build;
            // Same timing for the sapling map, so saplings from other mods map their trees too.
            PrefabManager.OnPrefabsRegistered += RegrowthSites.BuildSaplingMap;
            PrefabManager.OnVanillaPrefabsAvailable += RegrowthSites.Register;

            // On the server it heals flagged zones nobody is near, and near sleeping players; on
            // clients it only registers the RPC that applies overnight heals to zones they own.
            gameObject.AddComponent<HealRunner>();
            // Likewise plants saplings where trees were felled (server only).
            gameObject.AddComponent<RegrowthRunner>();
            HealCommands.Register();

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(WeatheringPlugin).Assembly);
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }
}
