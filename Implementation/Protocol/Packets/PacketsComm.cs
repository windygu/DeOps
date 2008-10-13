using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

using RiseOp.Implementation.Transport;
using RiseOp.Implementation.Protocol;
using RiseOp.Implementation.Protocol.Net;


namespace RiseOp.Implementation.Protocol.Comm
{
    internal class CommPacket
    {
        internal const byte SessionRequest  = 0x10;
        internal const byte SessionAck      = 0x20;
        internal const byte KeyRequest      = 0x30;
        internal const byte KeyAck          = 0x40;
        internal const byte CryptStart      = 0x50;
        internal const byte CryptPadding    = 0x60;
        internal const byte Data            = 0x70;
        internal const byte ProxyUpdate     = 0x80;
        internal const byte Close           = 0x90;
    }

	internal class RudpPacketType 
    {
        internal const byte Syn   = 0x00; 
        internal const byte Ack   = 0x10;
        internal const byte Ping  = 0x20;
        internal const byte Pong  = 0x30;
        internal const byte Data  = 0x40;
        internal const byte Fin   = 0x50;

        internal const byte Light = 0x60;
        internal const byte LightAck = 0x70;
    };

	internal class RudpPacket : G2Packet
	{
        const byte Packet_SenderDht     = 0x10;
        const byte Packet_SenderClient  = 0x20;
        const byte Packet_TargetDht     = 0x30;
        const byte Packet_TargetClient  = 0x40;
        const byte Packet_Type          = 0x50;
        const byte Packet_ID            = 0x60;
        const byte Packet_Seq           = 0x70;
        const byte Packet_To            = 0x80;
        const byte Packet_From          = 0x90;
        const byte Packet_Ident         = 0xA0;


        internal ulong SenderID;
        internal ushort SenderClient;

        internal ulong TargetID;
        internal ushort TargetClient;

        internal byte PacketType;

		internal ushort PeerID;
		internal byte   Sequence;
		internal byte[] Payload;
        internal uint   Ident;

		internal DhtAddress ToEndPoint;
        internal DhtAddress FromEndPoint;


        internal RudpPacket()
		{
		}

		internal override byte[] Encode(G2Protocol protocol)
		{
            lock (protocol.WriteSection)
            {
                G2Frame bdy = protocol.WritePacket(null, RootPacket.Comm, Payload);

                protocol.WritePacket(bdy, Packet_SenderDht, BitConverter.GetBytes(SenderID));
                protocol.WritePacket(bdy, Packet_SenderClient, BitConverter.GetBytes(SenderClient));
                protocol.WritePacket(bdy, Packet_TargetDht, BitConverter.GetBytes(TargetID));
                protocol.WritePacket(bdy, Packet_TargetClient, BitConverter.GetBytes(TargetClient));
                protocol.WritePacket(bdy, Packet_Type,   BitConverter.GetBytes(PacketType));
                protocol.WritePacket(bdy, Packet_ID,     BitConverter.GetBytes(PeerID));
                protocol.WritePacket(bdy, Packet_Seq,    BitConverter.GetBytes(Sequence));
                
                if(Ident != 0)
                    protocol.WritePacket(bdy, Packet_Ident, BitConverter.GetBytes(Ident));

                if (ToEndPoint != null)
                    ToEndPoint.WritePacket(protocol, bdy, Packet_To);

                if (FromEndPoint != null)
                    FromEndPoint.WritePacket(protocol, bdy, Packet_From);

                return protocol.WriteFinish();
            }
		}

        internal static RudpPacket Decode(G2ReceivedPacket packet)
        {
            return Decode(packet.Root);
        }

        internal static RudpPacket Decode(G2Header root)
		{
            RudpPacket gc = new RudpPacket();

			if( G2Protocol.ReadPayload(root) )
				gc.Payload = Utilities.ExtractBytes(root.Data, root.PayloadPos, root.PayloadSize);

			G2Protocol.ResetPacket(root);


			G2Header child = new G2Header(root.Data);

			while( G2Protocol.ReadNextChild(root, child) == G2ReadResult.PACKET_GOOD )
			{
                if (!G2Protocol.ReadPayload(child))
                    continue;

                switch (child.Name)
                {
                    case Packet_SenderDht:
                        gc.SenderID = BitConverter.ToUInt64(child.Data, child.PayloadPos);
                        break;

                    case Packet_SenderClient:
                        gc.SenderClient = BitConverter.ToUInt16(child.Data, child.PayloadPos);
                        break;

                    case Packet_TargetDht:
                        gc.TargetID = BitConverter.ToUInt64(child.Data, child.PayloadPos);
                        break;

                    case Packet_TargetClient:
                        gc.TargetClient = BitConverter.ToUInt16(child.Data, child.PayloadPos);
                        break;

                    case Packet_Type:
                        gc.PacketType = child.Data[child.PayloadPos];
                        break;

                    case Packet_ID:
                        gc.PeerID = BitConverter.ToUInt16(child.Data, child.PayloadPos);
                        break;

                    case Packet_Seq:
                        gc.Sequence = (byte)child.Data[child.PayloadPos];
                        break;

                    case Packet_To:
                        gc.ToEndPoint = DhtAddress.ReadPacket(child);
                        break;

                    case Packet_From:
                        gc.FromEndPoint = DhtAddress.ReadPacket(child);
                        break;

                    case Packet_Ident:
                        gc.Ident = BitConverter.ToUInt32(child.Data, child.PayloadPos);
                        break;
                }																  
			}

			return gc;
		}
	}

	internal class RudpSyn
	{
        const int BYTE_SIZE = 14; // 2 + 8 + 2 + 2

		internal ushort Version;
		internal UInt64 SenderID;
		internal ushort ClientID; 
		internal ushort ConnID;


		internal RudpSyn(byte[] bytes)
		{
            if (bytes.Length < BYTE_SIZE)
				return;

            Version  = BitConverter.ToUInt16(bytes, 0);
            SenderID = BitConverter.ToUInt64(bytes, 2);
            ClientID = BitConverter.ToUInt16(bytes, 10);
            ConnID   = BitConverter.ToUInt16(bytes, 12);
		}

		internal static byte[] Encode(ushort version, UInt64 senderID, ushort clientID, ushort connID)
		{
            byte[] payload = new byte[BYTE_SIZE];

            BitConverter.GetBytes(version).CopyTo(payload, 0);
            BitConverter.GetBytes(senderID).CopyTo(payload, 2);
            BitConverter.GetBytes(clientID).CopyTo(payload, 10);
            BitConverter.GetBytes(connID).CopyTo(payload, 12);

            return payload;
		}	
	}

    internal class RudpLight
    {
        const int MIN_SIZE = 4 + 4;

        internal uint   Service;
        internal uint   Type;
        internal byte[] Data;


        internal RudpLight(byte[] bytes)
        {
            if (bytes.Length < MIN_SIZE)
                return;

            Service = BitConverter.ToUInt32(bytes, 0);
            Type = BitConverter.ToUInt32(bytes, 4);
            Data = Utilities.ExtractBytes(bytes, 8, bytes.Length - 8);
        }

        internal static byte[] Encode(uint service, int type, byte[] data)
        {
            byte[] payload = new byte[MIN_SIZE + data.Length];

            BitConverter.GetBytes(service).CopyTo(payload, 0);
            BitConverter.GetBytes(type).CopyTo(payload, 4);
            data.CopyTo(payload, 8);

            return payload;
        }
    }


	internal class RudpAck
	{
		internal byte Start;
		internal byte Space;


		internal RudpAck(byte[] bytes)
		{
			if(bytes.Length < 2)
				return;

			Start = bytes[0];
			Space = bytes[1];
		}

		internal static byte[] Encode(byte start, byte space)
		{
			byte[] payload = new byte[2];

            payload[0] = start;
            payload[1] = space;

            return payload;
        }	
	}

	internal class SessionRequest : G2Packet
	{
        const byte Packet_Key = 0x10;


        internal byte[] EncryptedKey;
        

		internal SessionRequest()
		{
		}

		internal override byte[] Encode(G2Protocol protocol)
		{
            lock (protocol.WriteSection)
            {
                G2Frame sr = protocol.WritePacket(null, CommPacket.SessionRequest, null);

                protocol.WritePacket(sr, Packet_Key, EncryptedKey);

                return protocol.WriteFinish();
            }
		}

		internal static SessionRequest Decode(G2ReceivedPacket packet)
		{
			SessionRequest sr = new SessionRequest();

			G2Header child = new G2Header(packet.Root.Data);

			while( G2Protocol.ReadNextChild(packet.Root, child) == G2ReadResult.PACKET_GOOD )
			{
                if (child.Name == Packet_Key)
                    if (G2Protocol.ReadPayload(child))
                        sr.EncryptedKey = Utilities.ExtractBytes(child.Data, child.PayloadPos, child.PayloadSize);
			}

			return sr;
		}
	}

	internal class SessionAck : G2Packet
	{
        const byte Packet_Name = 0x10;


        // we can trust this because name is encoded in outbound key that could only be aquired by
        // decrypting the outound key in the session request with the local private key
        internal string Name;	


		internal SessionAck()
		{
		}

		internal override byte[] Encode(G2Protocol protocol)
		{
            lock (protocol.WriteSection)
            {
                G2Frame sa = protocol.WritePacket(null, CommPacket.SessionAck, null);

                protocol.WritePacket(sa, Packet_Name, UTF8Encoding.UTF8.GetBytes(Name));

                return protocol.WriteFinish();
            }
		}

		internal static SessionAck Decode(G2ReceivedPacket packet)
		{
			SessionAck sa = new SessionAck();

			G2Header child = new G2Header(packet.Root.Data);

			while( G2Protocol.ReadNextChild(packet.Root, child) == G2ReadResult.PACKET_GOOD )
			{
                if (child.Name == Packet_Name)
                    sa.Name = UTF8Encoding.UTF8.GetString(child.Data, child.PayloadPos, child.PayloadSize);
			}

			return sa;
		}
	}

    internal class EncryptionUpdate : G2Packet
	{
        internal bool Start;
        internal byte[] Padding;


        internal EncryptionUpdate(bool start)
		{
            Start = start;
		}

		internal override byte[] Encode(G2Protocol protocol)
		{
            lock (protocol.WriteSection)
            {
                // signals start of encryption
                if (Start)
                    protocol.WritePacket(null, CommPacket.CryptStart, null);

                // padding for encrypted block
                else
                    protocol.WritePacket(null, CommPacket.CryptPadding, Padding);

                return protocol.WriteFinish();
            }
        }

        internal static EncryptionUpdate Decode(G2ReceivedPacket packet)
		{
            // not decrypted
            EncryptionUpdate eu = new EncryptionUpdate(false);
		    return eu;
        }
    }

	internal class KeyRequest : G2Packet
	{
        const byte Packet_Encryption = 0x10;
        const byte Packet_Key = 0x20;
        const byte Packet_IV = 0x30;


        internal string Encryption;
        internal byte[] Key;
        internal byte[] IV;


		internal KeyRequest()
		{
		}

		internal override byte[] Encode(G2Protocol protocol)
		{
            lock (protocol.WriteSection)
            {
                G2Frame kr = protocol.WritePacket(null, CommPacket.KeyRequest, null);

                if (Encryption != null)
                {
                    protocol.WritePacket(kr, Packet_Encryption, UTF8Encoding.UTF8.GetBytes(Encryption));
                    protocol.WritePacket(kr, Packet_Key, Key);
                    protocol.WritePacket(kr, Packet_IV, IV);
                }

                return protocol.WriteFinish();
            }
		}

		internal static KeyRequest Decode(G2ReceivedPacket packet)
		{
			KeyRequest kr = new KeyRequest();

			G2Header child = new G2Header(packet.Root.Data);

			while( G2Protocol.ReadNextChild(packet.Root, child) == G2ReadResult.PACKET_GOOD )
			{
                if (child.Name == Packet_Encryption)
                    if (G2Protocol.ReadPayload(child))
                        kr.Encryption = UTF8Encoding.UTF8.GetString(child.Data, child.PayloadPos, child.PayloadSize);

                if (child.Name == Packet_Key)
                    if (G2Protocol.ReadPayload(child))
                        kr.Key = Utilities.ExtractBytes(child.Data, child.PayloadPos, child.PayloadSize);

                if (child.Name == Packet_IV)
                    if (G2Protocol.ReadPayload(child))
                        kr.IV = Utilities.ExtractBytes(child.Data, child.PayloadPos, child.PayloadSize);
			}

			return kr;
		}
	}

	internal class KeyAck : G2Packet
	{
        const byte Packet_Key = 0x10;


        internal byte[] PublicKey;


		internal KeyAck()
		{
		}

		internal override byte[] Encode(G2Protocol protocol)
		{
            lock (protocol.WriteSection)
            {
                G2Frame ka = protocol.WritePacket(null, CommPacket.KeyAck, null);

                protocol.WritePacket(ka, Packet_Key, PublicKey);

                return protocol.WriteFinish();
            }
		}

		internal static KeyAck Decode(G2ReceivedPacket packet)
		{
			KeyAck ka = new KeyAck();

			G2Header child = new G2Header(packet.Root.Data);

            while (G2Protocol.ReadNextChild(packet.Root, child) == G2ReadResult.PACKET_GOOD)
            {
                if (!G2Protocol.ReadPayload(child))
                    continue ;

                if (child.Name == Packet_Key)
                    ka.PublicKey = Utilities.ExtractBytes(child.Data, child.PayloadPos, child.PayloadSize);
            }

			return ka;
		}
	}

    internal class CommData : G2Packet
    {
        const byte Packet_Service = 0x10;
        const byte Packet_DataType = 0x20;
        const byte Packet_Data = 0x30;

        internal uint Service;
        internal uint DataType;
        internal byte[] Data;


        internal CommData()
        {
        }

        internal CommData(uint id, uint type, byte[] data)
        {
            Service = id;
            DataType = type;
            Data = data;
        }

        internal override byte[] Encode(G2Protocol protocol)
        {
            lock (protocol.WriteSection)
            {
                G2Frame packet = protocol.WritePacket(null, CommPacket.Data, null);

                protocol.WritePacket(packet, Packet_Service, CompactNum.GetBytes(Service));
                protocol.WritePacket(packet, Packet_DataType, CompactNum.GetBytes(DataType)); 
                protocol.WritePacket(packet, Packet_Data, Data);

                return protocol.WriteFinish();
            }
        }

        internal static CommData Decode(G2ReceivedPacket packet)
        {
            return Decode(packet.Root);
        }

        internal static CommData Decode(G2Header root)
        {
            CommData data = new CommData();

            G2Header child = new G2Header(root.Data);

            while (G2Protocol.ReadNextChild(root, child) == G2ReadResult.PACKET_GOOD)
            {
                if (!G2Protocol.ReadPayload(child))
                    continue;

                switch (child.Name)
                {
                    case Packet_Service:
                        data.Service = CompactNum.ToUInt32(child.Data, child.PayloadPos, child.PayloadSize);
                        break;

                    case Packet_DataType:
                        data.DataType = CompactNum.ToUInt32(child.Data, child.PayloadPos, child.PayloadSize);
                        break;

                    case Packet_Data:
                        data.Data = Utilities.ExtractBytes(child.Data, child.PayloadPos, child.PayloadSize);
                        break;
                }
            }

            return data;
        }
    }

    internal class ProxyUpdate : G2Packet
    {
        const byte Packet_Proxy = 0x10;

        internal DhtAddress Proxy;


        internal ProxyUpdate()
        {
        }

        internal override byte[] Encode(G2Protocol protocol)
        {
            lock (protocol.WriteSection)
            {
                G2Frame update = protocol.WritePacket(null, CommPacket.ProxyUpdate, null);

                Proxy.WritePacket(protocol, update, Packet_Proxy);

                return protocol.WriteFinish();
            }
        }

        internal static ProxyUpdate Decode(G2ReceivedPacket packet)
        {
            ProxyUpdate update = new ProxyUpdate();

            G2Header child = new G2Header(packet.Root.Data);

            while (G2Protocol.ReadNextChild(packet.Root, child) == G2ReadResult.PACKET_GOOD)
            {
                if (!G2Protocol.ReadPayload(child))
                    continue;

                switch (child.Name)
                {
                    case Packet_Proxy:
                        update.Proxy = DhtAddress.ReadPacket(child);
                        break;
                }
            }

            return update;
        }
    }

	internal class CommClose : G2Packet
	{
        const byte Packet_Message = 0x10;

		internal string Reason;
	
		internal CommClose()
		{
		}

		internal override byte[] Encode(G2Protocol protocol)
		{
            lock (protocol.WriteSection)
            {
                G2Frame close = protocol.WritePacket(null, CommPacket.Close, null);

                protocol.WritePacket(close, Packet_Message, UTF8Encoding.UTF8.GetBytes(Reason));

                return protocol.WriteFinish();
            }
		}

		internal static CommClose Decode(G2ReceivedPacket packet)
		{
			CommClose close = new CommClose();
			
			G2Header child = new G2Header(packet.Root.Data);

			while( G2Protocol.ReadNextChild(packet.Root, child) == G2ReadResult.PACKET_GOOD )
			{
                if (child.Name == Packet_Message)
					if( G2Protocol.ReadPayload(child) )
                        close.Reason = UTF8Encoding.UTF8.GetString(child.Data, child.PayloadPos, child.PayloadSize);
			}

			return close;
		}
	}
}
