using System;
using System.Collections.Generic;
using System.Globalization;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace Weathering
{
    /// <summary>
    /// Console commands for testing healing without waiting days. They act on the server's saved data,
    /// so they only work on the server or the host; on a client they say so and do nothing.
    /// </summary>
    internal static class HealCommands
    {
        // Zones around the player that weathering_age touches.
        private const int AgeRadius = 3;

        public static void Register()
        {
            CommandManager.Instance.AddConsoleCommand(new AgeCommand());
            CommandManager.Instance.AddConsoleCommand(new StatusCommand());
        }

        private static void Print(string text)
        {
            if (global::Console.instance) global::Console.instance.Print(text);
        }

        private static bool CheckServer()
        {
            if (ZNet.instance && ZNet.instance.IsServer()) return true;
            Print("This command must run on the server or host.");
            return false;
        }

        private class AgeCommand : ConsoleCommand
        {
            public override string Name => "weathering_age";

            public override string Help =>
                "<days> - test command: make terrain edits and felled trees within 3 zones of you that many in-game days older; " +
                "walk away to let them heal and regrow " +
                "(server or host; on a host it takes ownership of those zones)";

            public override bool IsCheat => true;

            public override void Run(string[] args)
            {
                if (!CheckServer()) return;
                if (args.Length < 1 || !float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float days) ||
                    !(days > 0f))
                {
                    Print("Usage: weathering_age <days>, with days above 0.");
                    return;
                }
                Player player = Player.m_localPlayer;
                if (!player || !HealRunner.EnsureGrid())
                {
                    Print("Weathering is not ready yet.");
                    return;
                }

                long day = HealRunner.DayLength();
                long now = (long)ZNet.instance.GetTimeSeconds();
                int nowMinutes = (int)(now / 60);
                long shiftSeconds = (long)Math.Round(days * (double)day);
                int shiftMinutes = (int)Math.Round(days * (double)day / 60.0);
                int pitch = HealRunner.Pitch;

                var zdos = new List<ZDO>();
                HealRunner.FindCompilers(ZoneSystem.GetZone(player.transform.position), AgeRadius, zdos);
                foreach (ZDO zdo in zdos)
                {
                    int[] stamps = StampStore.Load(zdo, pitch * pitch);
                    // Stamp edits the runner has not seen yet, so they age along with the rest.
                    if (TerrainData.TryRead(zdo, pitch, out TerrainData data)) StampStore.EnsureStamped(data, stamps, nowMinutes);
                    for (int i = 0; i < stamps.Length; i++)
                        if (stamps[i] != 0) stamps[i] = Math.Max(1, stamps[i] - shiftMinutes);

                    // A zone never seen counts as seen now, so the first pass heals instead of only stamping.
                    long last = StampStore.GetLast(zdo);
                    if (last == 0L) last = now;

                    HealRunner.Claim(zdo);
                    StampStore.Save(zdo, stamps);
                    StampStore.SetLast(zdo, Math.Max(1L, last - shiftSeconds));
                    StampStore.SetPending(zdo, true);
                }
                int zones = zdos.Count;

                RegrowthRunner.FindSites(ZoneSystem.GetZone(player.transform.position), AgeRadius, zdos);
                foreach (ZDO zdo in zdos)
                {
                    int felled = zdo.GetInt(RegrowthSites.FelledKey);
                    HealRunner.Claim(zdo);
                    zdo.Set(RegrowthSites.FelledKey, Math.Max(1, Math.Min(felled, nowMinutes) - shiftMinutes));
                    RegrowthRunner.ForgetBlocked(zdo);
                }

                Print($"Aged {zones} edited zones and {zdos.Count} felled trees by {days.ToString(CultureInfo.InvariantCulture)} days. " +
                    $"Walk more than {HealRunner.IdleDistance() * 64} m away to let them heal and regrow.");
            }
        }

        private class StatusCommand : ConsoleCommand
        {
            public override string Name => "weathering_status";

            public override string Help => "- how many zones wait to heal and trees wait to regrow, and the state of the zone you stand in (server or host)";

            public override void Run(string[] args)
            {
                if (!CheckServer()) return;
                if (BaseIndex.IsReady)
                {
                    RegrowthRunner.Count(out int sites, out int due);
                    Print($"Zones waiting to heal: {HealRunner.CountPending()}. Felled trees waiting to regrow: {sites}, " +
                        $"{due} of them old enough (as of the last sweeps).");
                }
                else
                {
                    Print("Weathering is not running yet: the base index is not built.");
                }

                Player player = Player.m_localPlayer;
                if (!player || !HealRunner.EnsureGrid()) return;

                Vector2s zone = ZoneSystem.GetZone(player.transform.position);
                var zdos = new List<ZDO>();
                HealRunner.FindCompilers(zone, 0, zdos);
                if (zdos.Count == 0)
                {
                    Print($"Zone {zone.x},{zone.y}: no terrain edits.");
                    return;
                }

                ZDO zdo = zdos[0];
                long last = StampStore.GetLast(zdo);
                string lastText = last == 0L
                    ? "never healed"
                    : $"last healed {((ZNet.instance.GetTimeSeconds() - last) / HealRunner.DayLength()).ToString("0.00", CultureInfo.InvariantCulture)} days ago";
                Print($"Zone {zone.x},{zone.y}: pending {StampStore.IsPending(zdo)}, {lastText}, idle {DescribeIdle(zone, zdo)}.");
            }

            private static string DescribeIdle(Vector2s zone, ZDO zdo)
            {
                int distance = HealRunner.NearestViewerDistance(zone);
                int needed = HealRunner.IdleDistance();
                if (distance <= needed)
                    return $"no (a player is {distance} zones away; healing needs everyone more than {needed} zones away)";
                if (!HealRunner.IsZoneIdle(zone, zdo)) return "no (the zone is loaded on the server)";
                return "yes";
            }
        }
    }
}
