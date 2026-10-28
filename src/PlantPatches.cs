using HarmonyLib;

namespace Weathering
{
    /// <summary>
    /// Keeps saplings the mod planted healthy. Vanilla judges every sapling like a player-planted one
    /// (biome, heat, cold, roof, grow space), but a regrown sapling stands where its tree already grew,
    /// and natural forests are denser than those checks allow, so many would never grow. Runs on
    /// every peer that has the sapling loaded, so the hover text and model agree with the owner,
    /// whose <c>Plant.Grow</c> only grows a healthy plant.
    /// </summary>
    [HarmonyPatch(typeof(Plant), nameof(Plant.UpdateHealth))]
    internal static class PlantPatches
    {
        // Every loaded plant, every 10 s: no allocation, no logging.
        private static void Postfix(ref Plant.Status ___m_status, ZNetView ___m_nview)
        {
            if (___m_status == Plant.Status.Healthy) return;
            if (!___m_nview || !___m_nview.IsValid()) return;
            if (___m_nview.GetZDO().GetBool(RegrowthSites.RegrownKey)) ___m_status = Plant.Status.Healthy;
        }
    }
}
