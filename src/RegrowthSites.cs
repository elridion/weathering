using System;
using System.Collections.Generic;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace Weathering
{
    /// <summary>
    /// Remembers where trees were felled so the server can plant their sapling there later. A site is
    /// an invisible persistent marker object: its ZDO saves with the world and syncs to the server
    /// like any other, so the owner that felled the tree never has to talk to the server directly.
    /// Only trees some vanilla sapling grows into get a site; the map is built from the saplings'
    /// own <c>Plant.m_grownPrefabs</c>, so modded saplings count too.
    /// </summary>
    internal static class RegrowthSites
    {
        // Persisted identifiers: never rename once shipped.
        public const string MarkerName = "weathering_regrowth_site";
        public static readonly int MarkerHash = MarkerName.GetStableHashCode();
        public static readonly int TreeKey = "weathering_tree".GetStableHashCode();
        public static readonly int StumpKey = "weathering_stump".GetStableHashCode();
        public static readonly int FelledKey = "weathering_felled".GetStableHashCode();

        // Grown prefab hash -> sapling prefab hash.
        private static readonly Dictionary<int, int> s_saplings = new Dictionary<int, int>();
        private static GameObject s_marker;

        public static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            try
            {
                var prefab = PrefabManager.Instance.CreateEmptyPrefab(MarkerName, true);
                // The empty prefab is a cube: a marker must be invisible and never block anything.
                UnityEngine.Object.DestroyImmediate(prefab.GetComponent<Collider>());
                UnityEngine.Object.DestroyImmediate(prefab.GetComponent<MeshRenderer>());
                UnityEngine.Object.DestroyImmediate(prefab.GetComponent<MeshFilter>());
                prefab.GetComponent<ZNetView>().m_persistent = true;
                PrefabManager.Instance.AddPrefab(new CustomPrefab(prefab, false));
                s_marker = prefab;
            }
            catch (Exception e)
            {
                Jotunn.Logger.LogError($"Could not create the regrowth marker, felled trees will not regrow: {e}");
            }
        }

        public static void BuildSaplingMap()
        {
            s_saplings.Clear();
            var scene = ZNetScene.instance;
            if (!scene) return;

            foreach (var prefab in scene.m_prefabs)
            {
                if (!prefab) continue;
                var plant = prefab.GetComponent<Plant>();
                if (!plant || plant.m_grownPrefabs == null) continue;

                int saplingHash = prefab.name.GetStableHashCode();
                foreach (var grown in plant.m_grownPrefabs)
                {
                    if (!grown) continue;
                    int grownHash = grown.name.GetStableHashCode();
                    // First sapling wins, so the choice is stable across loads when two grow the same tree.
                    if (!s_saplings.ContainsKey(grownHash)) s_saplings.Add(grownHash, saplingHash);
                }
            }
        }

        public static bool TryGetSapling(int treeHash, out int saplingHash) => s_saplings.TryGetValue(treeHash, out saplingHash);

        /// <summary>Records a site for a tree being felled; call only on the tree's owner.</summary>
        public static void Record(GameObject tree)
        {
            if (!s_marker || !tree) return;
            var nview = tree.GetComponent<ZNetView>();
            var treeBase = tree.GetComponent<TreeBase>();
            if (!nview || !nview.IsValid() || !treeBase) return;
            var stumpPrefab = treeBase.m_stubPrefab;

            int treeHash = nview.GetZDO().GetPrefab();
            if (!s_saplings.ContainsKey(treeHash)) return;
            // Inside a base the server drops the site anyway, but only once that zone is idle, so
            // around a lived-in base the markers would pile up. The base's objects are loaded here on
            // the felling owner; Warden stones count when their PlayerBase area is on, and the
            // server's check catches the rest (margins, buildings, Warden-only radii).
            if (EffectArea.IsPointInsideArea(tree.transform.position, EffectArea.Type.PlayerBase)) return;

            var marker = UnityEngine.Object.Instantiate(s_marker, tree.transform.position, tree.transform.rotation);
            var markerView = marker.GetComponent<ZNetView>();
            if (!markerView || !markerView.IsValid()) return;

            var zdo = markerView.GetZDO();
            zdo.Set(TreeKey, treeHash);
            // A standing stump blocks regrowth, so it must be the hash of the prefab vanilla spawns.
            zdo.Set(StumpKey, stumpPrefab ? stumpPrefab.name.GetStableHashCode() : 0);
            zdo.Set(FelledKey, Math.Max(StampStore.Now(), 1));
        }
    }
}
