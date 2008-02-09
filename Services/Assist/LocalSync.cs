﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

using RiseOp.Implementation;
using RiseOp.Implementation.Dht;
using RiseOp.Implementation.Protocol;
using RiseOp.Implementation.Protocol.Net;

using RiseOp.Services.Assist;
using RiseOp.Services.Location;

namespace RiseOp.Services.Assist
{
    internal delegate byte[] GetLocalSyncTagHandler();
    internal delegate void LocalSyncTagReceivedHandler(ulong user, byte[] tag);

    // gives any service the ability to store a little piece of data on the network for anything
    // usually its version number purpose of this is to ease the burdon of patching the local area
    // as services increase, patch size remains constant
    

    class LocalSync : OpService
    {
        public string Name { get { return "LocalSync"; } }
        public ushort ServiceID { get { return 11; } }

        OpCore Core;
        DhtNetwork Network;
        DhtStore Store;

        enum DataType { SyncObject = 1 };

        internal VersionedCache Cache;

        internal Dictionary<ulong, ServiceData> InRange = new Dictionary<ulong, ServiceData>();
        internal Dictionary<ulong, ServiceData> OutofRange = new Dictionary<ulong, ServiceData>();

        internal ServiceEvent<GetLocalSyncTagHandler> GetTag = new ServiceEvent<GetLocalSyncTagHandler>();
        internal ServiceEvent<LocalSyncTagReceivedHandler> TagReceived = new ServiceEvent<LocalSyncTagReceivedHandler>();

      
        internal LocalSync(OpCore core)
        {
            Core = core;
            Network = core.OperationNet;
            Store = Network.Store;
            Core.Sync = this;

            Core.Locations.GetTag[ServiceID, (ushort) DataType.SyncObject] += new GetLocationTagHandler(Locations_GetTag);
            Core.Locations.TagReceived[ServiceID, (ushort) DataType.SyncObject] += new LocationTagReceivedHandler(Locations_TagReceived);

            Store.ReplicateEvent[ServiceID, (ushort)DataType.SyncObject] += new ReplicateHandler(Store_Replicate);

            Cache = new VersionedCache(Network, ServiceID, (ushort)DataType.SyncObject, false);
            Cache.FileAquired += new FileAquiredHandler(Cache_FileAquired);
            Cache.FileRemoved += new FileRemovedHandler(Cache_FileRemoved);
            Cache.Load();

            // if sync file for ourselves does not exist create
            if (!Cache.FileMap.SafeContainsKey(Core.LocalDhtID))
                UpdateLocal();
        }

        public void Dispose()
        {
            Core.Locations.GetTag[ServiceID, (ushort)DataType.SyncObject] -= new GetLocationTagHandler(Locations_GetTag);
            Core.Locations.TagReceived[ServiceID, (ushort)DataType.SyncObject] -= new LocationTagReceivedHandler(Locations_TagReceived);

            Store.ReplicateEvent[ServiceID, (ushort)DataType.SyncObject] -= new ReplicateHandler(Store_Replicate);

            Cache.FileAquired -= new FileAquiredHandler(Cache_FileAquired);
            Cache.FileRemoved -= new FileRemovedHandler(Cache_FileRemoved);
            Cache.Dispose();
        }

        public List<MenuItemInfo> GetMenuInfo(InterfaceMenuType menuType, ulong user, uint project)
        {
            return null;
        }

        internal void UpdateLocal()
        {
            ServiceData data = new ServiceData();
            data.Date = Core.TimeNow.ToUniversalTime();

            foreach (ushort service in GetTag.HandlerMap.Keys)
                foreach (ushort datatype in GetTag.HandlerMap[service].Keys)
                {
                    LocationTag tag = new LocationTag();

                    tag.Service = service;
                    tag.DataType = datatype;
                    tag.Tag = GetTag[service, datatype].Invoke();

                    if (tag.Tag != null)
                    {
                        Debug.Assert(tag.Tag.Length < 8);

                        if (tag.Tag.Length < 8)
                            data.Tags.Add(tag);
                    }
                }

            Cache.UpdateLocal("", null, data.Encode(Core.Protocol));
        }

        void InvokeTags(ulong user, ServiceData data)
        {
            foreach (LocationTag tag in data.Tags)
                if (TagReceived.Contains(tag.Service, tag.DataType))
                    TagReceived[tag.Service, tag.DataType].Invoke(user, tag.Tag);
        }

        void Cache_FileAquired(OpVersionedFile file)
        {
            ServiceData data = ServiceData.Decode(Core.Protocol, file.Header.Extra);

            if (Network.Routing.InCacheArea(file.DhtID))
                InRange[file.DhtID] = data;
            else
                OutofRange[file.DhtID] = data;

            InvokeTags(file.DhtID, data);
        }

        void Cache_FileRemoved(OpVersionedFile file)
        {
            if(InRange.ContainsKey(file.DhtID))
                InRange.Remove(file.DhtID);

            if (OutofRange.ContainsKey(file.DhtID))
                OutofRange.Remove(file.DhtID);
        }

        List<byte[]> Store_Replicate(DhtContact contact)
        {
            // indicates cache area has changed, move contacts between out and in range

            // move in to out
            List<ulong> remove = new List<ulong>();

            foreach(ulong user in InRange.Keys)
                if (!Network.Routing.InCacheArea(user))
                {
                    OutofRange[user] = InRange[user];
                    remove.Add(user);
                }

            foreach (ulong key in remove)
                InRange.Remove(key);

            // move out to in
            remove.Clear();

            foreach (ulong user in OutofRange.Keys)
                if (Network.Routing.InCacheArea(user))
                {
                    InRange[user] = OutofRange[user];
                    remove.Add(user);
                }

            foreach (ulong key in remove)
                OutofRange.Remove(key);

            // invoke tags on data moving in range so all services are cached
            foreach (ulong key in remove)
                InvokeTags(key, InRange[key]);

            return null;
        }

        byte[] Locations_GetTag()
        {
            OpVersionedFile file = Cache.GetFile(Core.LocalDhtID);

            return (file != null) ? BitConverter.GetBytes(file.Header.Version) : null;
        }

        void Locations_TagReceived(DhtAddress address, ulong user, byte[] tag)
        {
            if (tag.Length < 4)
                return;

            uint version = 0;

            OpVersionedFile file = Cache.GetFile(user);

            if (file != null)
            {
                version = BitConverter.ToUInt32(tag, 0);

                if (version < file.Header.Version)
                    Store.Send_StoreReq(address, 0, new DataReq(null, file.DhtID, ServiceID, (ushort)DataType.SyncObject, file.SignedHeader));
            }

            if ((file != null && version > file.Header.Version) ||
                (file == null && Network.Routing.InCacheArea(user)))
            {
                Cache.Research(user);

                Network.Searches.SendDirectRequest(address, user, ServiceID, (ushort)DataType.SyncObject, BitConverter.GetBytes(version));
            }
        }


        internal void Research(ulong user)
        {
            Cache.Research(user);
        }
    }

    internal class SyncPacket
    {
        internal const byte ServiceData = 0x10;
    }

    internal class ServiceData : G2Packet
    {
        const byte Packet_Date = 0xE0;
        const byte Packet_Tag = 0xF0;

        internal DateTime Date;
        internal List<LocationTag> Tags = new List<LocationTag>();


        internal override byte[] Encode(G2Protocol protocol)
        {
            lock (protocol.WriteSection)
            {
                G2Frame data = protocol.WritePacket(null, SyncPacket.ServiceData, null);

                protocol.WritePacket(data, Packet_Date, BitConverter.GetBytes(Date.ToBinary()));

                foreach (LocationTag tag in Tags)
                    protocol.WritePacket(data, Packet_Tag, tag.ToBytes());

                return protocol.WriteFinish();
            }
        }

        internal static ServiceData Decode(G2Protocol protocol, byte[] data)
        {
            G2Header root = new G2Header(data);

            protocol.ReadPacket(root);

            if (root.Name != LocPacket.LocationData)
                return null;

            ServiceData packet = new ServiceData();
            G2Header child = new G2Header(root.Data);

            while (G2Protocol.ReadNextChild(root, child) == G2ReadResult.PACKET_GOOD)
            {
                if (!G2Protocol.ReadPayload(child))
                    continue;

                switch (child.Name)
                {
                    case Packet_Date:
                        packet.Date = DateTime.FromBinary(BitConverter.ToInt64(child.Data, child.PayloadPos));
                        break;

                    case Packet_Tag:
                        packet.Tags.Add(LocationTag.FromBytes(child.Data, child.PayloadPos, child.PayloadSize));
                        break;
                }
            }

            return packet;
        }
    }
}