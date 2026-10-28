using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Weathering
{
    /// <summary>
    /// A protected disc on the XZ plane: a player base (plus margin), a Warden stone or a piece.
    /// </summary>
    internal readonly struct ProtectedCircle
    {
        public readonly Vector2 Center;
        public readonly float Radius;

        public ProtectedCircle(Vector2 center, float radius)
        {
            Center = center;
            Radius = radius;
        }

        public bool Contains(Vector2 p) => (p - Center).sqrMagnitude <= Radius * Radius;
    }

    /// <summary>
    /// Finds player bases, Warden stones and player-built pieces around a zone from saved ZDOs rather
    /// than loaded objects, so unloaded neighbours and dedicated servers count. Base radii come from a
    /// scan of every networked prefab for a PlayerBase effect area, since a ZDO only knows its prefab.
    /// </summary>
    internal static class BaseIndex
    {
        // Warden's identifiers, owned by the Warden repo: never rename here.
        private const string WardenGuid = "elridion.warden";
        private const string WardenPrefabName = "piece_spawnward";
        private static readonly int s_wardenPrefab = WardenPrefabName.GetStableHashCode();
        private static readonly int s_wardenRadiusKey = "spawnward_radius".GetStableHashCode();

        // Warden's own default maximum (40 + 20 * (3 + 4)); also used when Warden is missing.
        private const float DefaultWardenMaxRadius = 180f;
        // Warden counts three distinct upgrade pieces besides the stacked ritual candles.
        private const int WardenDistinctUpgrades = 3;
        private const float ZoneSize = 64f;
        // Caps ExtraBasePieces radii: the sector scan grows with the square of the largest radius.
        private const float MaxExtraRadius = 500f;

        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> s_objectsBySector =
            AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
        // Portals are moved out of the sector lists into their own map, so they must be read separately.
        private static readonly AccessTools.FieldRef<ZDOMan, Dictionary<ZoneSystem.SectorIndex, List<ZDO>>> s_portalObjects =
            AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZoneSystem.SectorIndex, List<ZDO>>>("m_portalObjects");

        private static readonly Dictionary<int, float> s_baseRadii = new Dictionary<int, float>();
        private static float s_maxBaseRadius;
        private static bool s_built;

        private static readonly Dictionary<int, float> s_extraRadii = new Dictionary<int, float>();
        private static float s_maxExtraRadius;
        private static string s_parsedExtras;

        /// <summary>True once the prefab scan has run; callers must not heal before that.</summary>
        public static bool IsReady => s_built;

        /// <summary>Largest radius any known base can have, before BaseMargin.</summary>
        public static float MaxRadius
        {
            get
            {
                EnsureExtras();
                return Mathf.Max(Mathf.Max(s_maxBaseRadius, s_maxExtraRadius), WardenMaxRadius());
            }
        }

        public static void Build()
        {
            ZNetScene scene = ZNetScene.instance;
            if (!scene) return;

            // Called again on every world load; the prefab list may differ between sessions.
            s_baseRadii.Clear();
            s_maxBaseRadius = 0f;
            var areas = new List<EffectArea>();
            foreach (GameObject prefab in scene.m_prefabs)
            {
                if (!prefab) continue;
                try
                {
                    float radius = ScanPrefab(prefab, areas);
                    if (radius <= 0f) continue;
                    int hash = prefab.name.GetStableHashCode();
                    if (hash == s_wardenPrefab) continue;
                    s_baseRadii[hash] = radius;
                    if (radius > s_maxBaseRadius) s_maxBaseRadius = radius;
                }
                catch (Exception e)
                {
                    Jotunn.Logger.LogWarning($"Could not read the base area of {prefab.name}: {e.Message}");
                }
            }
            s_built = true;
        }

        /// <summary>
        /// Replaces the contents of <paramref name="result"/> with every protected circle that touches
        /// the zone: bases and Warden stones from the whole reach, pieces from the zone and its 8
        /// neighbours. Runs once per heal pass, not per frame.
        /// </summary>
        public static void Collect(Vector2s zone, List<ProtectedCircle> result)
        {
            result.Clear();
            ZDOMan zdoMan = ZDOMan.instance;
            if (zdoMan == null) return;
            List<ZDO>[] sectors = s_objectsBySector(zdoMan);
            Dictionary<ZoneSystem.SectorIndex, List<ZDO>> portals = s_portalObjects(zdoMan);
            if (sectors == null) return;

            EnsureExtras();
            float baseMargin = WeatheringPlugin.BaseMargin.Value;
            float pieceMargin = WeatheringPlugin.PieceMargin.Value;
            float wardenMax = WardenMaxRadius();
            float maxRadius = Mathf.Max(Mathf.Max(s_maxBaseRadius, s_maxExtraRadius), wardenMax);
            int reach = Mathf.CeilToInt((maxRadius + baseMargin) / ZoneSize);
            bool anyBases = s_baseRadii.Count > 0 || s_extraRadii.Count > 0;

            Vector2 zoneCenter = new Vector2(zone.x * ZoneSize, zone.y * ZoneSize);
            bool visitedOutside = false;
            for (int dy = -reach; dy <= reach; dy++)
            {
                for (int dx = -reach; dx <= reach; dx++)
                {
                    bool near = dx >= -1 && dx <= 1 && dy >= -1 && dy <= 1;
                    // Outer sectors only matter for bases and Warden stones.
                    if (!near && !anyBases && wardenMax <= 0f) continue;

                    ZoneSystem.SectorIndex index = ZoneSystem.SectorToIndex(zone.x + dx, zone.y + dy);
                    // Vanilla puts every sector outside the world grid into slot 0; read it only once.
                    if (index.Sector == 0)
                    {
                        if (visitedOutside) continue;
                        visitedOutside = true;
                    }

                    if (index.Sector < sectors.Length)
                        AddFrom(sectors[index.Sector], near, zoneCenter, baseMargin, pieceMargin, wardenMax, result);
                    if (portals != null && portals.TryGetValue(index, out List<ZDO> portalList))
                        AddFrom(portalList, near, zoneCenter, baseMargin, pieceMargin, wardenMax, result);
                }
            }
        }

        /// <summary>
        /// Largest radius a Warden stone can reach with Warden's current (server-synced) config, used for
        /// stones that never saved their radius. 180 m when Warden is not installed.
        /// </summary>
        public static float WardenMaxRadius()
        {
            if (!Chainloader.PluginInfos.TryGetValue(WardenGuid, out PluginInfo info)) return DefaultWardenMaxRadius;
            BaseUnityPlugin plugin = info.Instance;
            if (!plugin) return DefaultWardenMaxRadius;

            ConfigFile config = plugin.Config;
            if (!config.TryGetEntry("General", "BaseRadius", out ConfigEntry<float> baseRadius) ||
                !config.TryGetEntry("General", "RadiusPerUpgrade", out ConfigEntry<float> perUpgrade) ||
                !config.TryGetEntry("General", "MaxStackedUpgrades", out ConfigEntry<int> maxStacked))
                return DefaultWardenMaxRadius;

            return baseRadius.Value + perUpgrade.Value * (WardenDistinctUpgrades + maxStacked.Value);
        }

        private static void AddFrom(List<ZDO> zdos, bool near, Vector2 zoneCenter, float baseMargin,
            float pieceMargin, float wardenMax, List<ProtectedCircle> result)
        {
            if (zdos == null) return;
            for (int i = 0; i < zdos.Count; i++)
            {
                ZDO zdo = zdos[i];
                if (zdo == null) continue;
                int prefab = zdo.GetPrefab();

                float radius;
                if (prefab == s_wardenPrefab)
                {
                    // Switched-off stones still protect; the saved radius tracks the stone's level.
                    radius = zdo.GetFloat(s_wardenRadiusKey, -1f);
                    if (radius <= 0f) radius = wardenMax;
                    radius += baseMargin;
                }
                else if (s_extraRadii.TryGetValue(prefab, out float extra))
                {
                    radius = extra + baseMargin;
                }
                else if (s_baseRadii.TryGetValue(prefab, out float area))
                {
                    radius = area + baseMargin;
                }
                else
                {
                    if (!near || pieceMargin <= 0f) continue;
                    if (zdo.GetLong(ZDOVars.s_creator) == 0L) continue;
                    radius = pieceMargin;
                }

                Vector3 pos = zdo.GetPosition();
                var center = new Vector2(pos.x, pos.z);
                if (!TouchesZone(center, radius, zoneCenter)) continue;
                result.Add(new ProtectedCircle(center, radius));
            }
        }

        private static bool TouchesZone(Vector2 center, float radius, Vector2 zoneCenter)
        {
            const float half = ZoneSize / 2f;
            float dx = Mathf.Max(Mathf.Abs(center.x - zoneCenter.x) - half, 0f);
            float dy = Mathf.Max(Mathf.Abs(center.y - zoneCenter.y) - half, 0f);
            return dx * dx + dy * dy <= radius * radius;
        }

        private static float ScanPrefab(GameObject prefab, List<EffectArea> areas)
        {
            float best = 0f;
            prefab.GetComponentsInChildren(true, areas);
            for (int i = 0; i < areas.Count; i++)
            {
                EffectArea area = areas[i];
                if (!area || (area.m_type & EffectArea.Type.PlayerBase) == 0) continue;
                var sphere = area.GetComponent<SphereCollider>();
                if (!sphere) continue;
                Vector3 scale = sphere.transform.lossyScale;
                float radius = sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                if (radius > best) best = radius;
            }
            areas.Clear();
            return best;
        }

        // Reparses ExtraBasePieces only when the string changes, so a bad entry warns once, not per pass.
        private static void EnsureExtras()
        {
            string value = WeatheringPlugin.ExtraBasePieces.Value ?? "";
            if (value == s_parsedExtras) return;
            s_parsedExtras = value;

            s_extraRadii.Clear();
            s_maxExtraRadius = 0f;
            foreach (string raw in value.Split(','))
            {
                string entry = raw.Trim();
                if (entry.Length == 0) continue;

                int colon = entry.LastIndexOf(':');
                string name = colon > 0 ? entry.Substring(0, colon).Trim() : "";
                if (name.Length == 0 ||
                    !float.TryParse(entry.Substring(colon + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float radius) ||
                    !(radius > 0f) || radius > MaxExtraRadius)
                {
                    Jotunn.Logger.LogWarning($"Skipping ExtraBasePieces entry '{entry}': expected prefab:radius with a radius " +
                        $"above 0 and up to {MaxExtraRadius} m, for example piece_example:30.");
                    continue;
                }

                s_extraRadii[name.GetStableHashCode()] = radius;
                if (radius > s_maxExtraRadius) s_maxExtraRadius = radius;
            }
        }
    }
}
