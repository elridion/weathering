using System;
using System.Collections.Generic;

namespace Weathering
{
    /// <summary>
    /// Applies an overnight heal result on the peer that owns the zone. While everyone sleeps the
    /// server heals zones players stand in, but a zone a connected peer owns may only be written by
    /// that peer: its loaded TerrainComp saves over anything else, and its newer edits would be lost.
    /// So the server sends the owner the new data together with the data revision it healed from,
    /// and the owner writes it only if it still owns every zone in the message and none changed
    /// since; otherwise it drops the whole message and the zones stay pending. The owner's loaded
    /// TerrainComp then reloads the new data by itself, and the write syncs back like any edit.
    /// </summary>
    internal static class HealApply
    {
        // Networked identifier: never rename once shipped.
        internal const string RpcName = "Weathering_ApplyHeal";

        private static ZRoutedRpc s_registered;

        /// <summary>One zone's new saved data. Null data or stamps, and a last of 0, keep what is there.</summary>
        internal struct Entry
        {
            public ZDOID Id;
            public uint Revision;
            public byte[] TerrainData;
            public byte[] Stamps;
            public long Last;
            public bool Pending;
        }

        /// <summary>Registers the RPC on the current ZRoutedRpc, once per game session. Runs on every peer.</summary>
        internal static void EnsureRegistered()
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || rpc == s_registered) return;
            rpc.Register<ZPackage>(RpcName, RPC_ApplyHeal);
            s_registered = rpc;
        }

        /// <summary>Writes one zone's result into a ZDO the caller may write.</summary>
        internal static void Write(ZDO zdo, byte[] terrainData, byte[] stamps, long last, bool pending)
        {
            if (terrainData != null) zdo.Set(ZDOVars.s_TCData, terrainData);
            if (stamps != null) StampStore.SaveEncoded(zdo, stamps);
            if (last != 0L) StampStore.SetLast(zdo, last);
            StampStore.SetPending(zdo, pending);
        }

        /// <summary>Sends the results to the peer that owns those zones, as one all-or-nothing message.</summary>
        internal static void Send(long peer, List<Entry> entries)
        {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || entries.Count == 0) return;
            var pkg = new ZPackage();
            pkg.Write(entries.Count);
            foreach (Entry entry in entries)
            {
                pkg.Write(entry.Id);
                pkg.Write(entry.Revision);
                // An empty array stands for "keep": ZPackage has no null byte array.
                pkg.Write(entry.TerrainData ?? new byte[0]);
                pkg.Write(entry.Stamps ?? new byte[0]);
                pkg.Write(entry.Last);
                pkg.Write(entry.Pending);
            }
            rpc.InvokeRoutedRPC(peer, RpcName, pkg);
        }

        private static void RPC_ApplyHeal(long sender, ZPackage pkg)
        {
            ZNet net = ZNet.instance;
            ZDOMan zdoMan = ZDOMan.instance;
            // Only the server heals; the host writes its own zones directly and never sends this.
            if (!net || net.IsServer() || zdoMan == null) return;
            ZNetPeer server = net.GetServerPeer();
            if (server == null || server.m_uid != sender) return;

            var entries = new List<Entry>();
            try
            {
                int count = pkg.ReadInt();
                for (int n = 0; n < count; n++)
                {
                    var entry = new Entry
                    {
                        Id = pkg.ReadZDOID(),
                        Revision = pkg.ReadUInt(),
                        TerrainData = pkg.ReadByteArray(),
                        Stamps = pkg.ReadByteArray(),
                        Last = pkg.ReadLong(),
                        Pending = pkg.ReadBool(),
                    };
                    if (entry.TerrainData.Length == 0) entry.TerrainData = null;
                    if (entry.Stamps.Length == 0) entry.Stamps = null;
                    entries.Add(entry);
                }
            }
            catch (Exception)
            {
                // A malformed message is dropped; the server retries those zones later.
                return;
            }

            // All or nothing: a border mirrored into a neighbour must land together with its zone.
            foreach (Entry entry in entries)
            {
                ZDO zdo = zdoMan.GetZDO(entry.Id);
                if (zdo == null || !zdo.IsOwner() || zdo.DataRevision != entry.Revision) return;
            }
            foreach (Entry entry in entries)
                Write(zdoMan.GetZDO(entry.Id), entry.TerrainData, entry.Stamps, entry.Last, entry.Pending);
        }
    }
}
