using HarmonyLib;

namespace Weathering
{
    /// <summary>
    /// Records a regrowth site when a standing tree is felled. Vanilla calls the private
    /// <c>TreeBase.SpawnLog</c> exactly once per felled tree, on its owner, right before destroying it
    /// (later hits on a dead tree only destroy it), so a postfix there fires once and never for logs,
    /// stumps or saplings, which have no <c>TreeBase</c>.
    /// </summary>
    [HarmonyPatch(typeof(TreeBase), "SpawnLog")]
    internal static class TreePatches
    {
        private static void Postfix(TreeBase __instance)
        {
            RegrowthSites.Record(__instance.gameObject);
        }
    }
}
