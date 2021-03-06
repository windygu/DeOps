using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;

using DeOps.Services.Location;
using DeOps.Implementation.Dht;
using DeOps.Implementation.Protocol;
using DeOps.Implementation.Protocol.Comm;
using DeOps.Implementation.Protocol.Net;


namespace DeOps.Implementation.Transport
{
    public enum SessionStatus { Connecting, Active, Closed };

    public class RudpSession : DhtClient
    {
        public OpCore Core;
        public DhtNetwork Network;
        public RudpHandler RudpControl;
        public RudpSocket Comm;

        public SessionStatus Status = SessionStatus.Connecting;

		// extra info
        public string CloseMsg;

		// negotiation
		public RijndaelManaged InboundEnc;
		public RijndaelManaged OutboundEnc;

        bool RequestReceived;
        bool ConnectAckSent;

        public DateTime NegotiateTimeout;

        public const int BUFF_SIZE = 10 * 1024;
		
        // sending
        int SendBlockSize = 16;

        public byte[] SendBuffer;
        public int SendBuffSize = 0;

        public byte[] EncryptBuffer;
        public int EncryptBuffSize;

        ICryptoTransform SendEncryptor;
        
        // receiving
        int RecvBlockSize = 16;
        
        byte[] ReceiveBuffer;
        int    RecvBuffSize = 0; 
        
        byte[] DecryptBuffer;
        int    DecryptBuffSize;

        ICryptoTransform RecvDecryptor;

		// active
        public DateTime  Startup;

        public int Lingering;


        public RudpSession(RudpHandler control, ulong dhtID, ushort clientID, bool inbound)
        {
			Core     = control.Network.Core;
            Network = control.Network;
            RudpControl = control;

            UserID = dhtID;
            ClientID = clientID;

            Comm = new RudpSocket(this, inbound);

            NegotiateTimeout = Core.TimeNow.AddSeconds(10);
            Startup = Core.TimeNow;

            //DebugWriter = new FileStream("Log " + Network.Profile.ScreenName + "-" + Buddy.ScreenName + "-" + Comm.PeerID.ToString() + ".txt", FileMode.CreateNew, FileAccess.Write);
		}

        public void Connect()
        {
            if (Status != SessionStatus.Connecting)
                return;

            Comm.Connect();
        }

		public void UpdateStatus(SessionStatus status)
		{
            if (Status == status)
                return;

			Status = status;

			Log("Session - " + status.ToString());

            if (RudpControl.SessionUpdate != null)
                RudpControl.SessionUpdate.Invoke(this);
		}

        Queue<Tuple<int, int>> LastSends = new Queue<Tuple<int, int>>();

		public bool SendPacket(G2Packet packet)
		{
            if (Core.InvokeRequired)
                Debug.Assert(false);

            byte[] final = packet.Encode(Network.Protocol);

            if (Comm.State != RudpState.Connected)
                return false;

            PacketLogEntry logEntry = new PacketLogEntry(Core.TimeNow, TransportProtocol.Rudp, DirectionType.Out, Comm.PrimaryAddress.Address, final);
            Core.Network.LogPacket(logEntry);

            // dont worry about buffers, cause initial comm buffer is large enough to fit all negotiating packets
            if (SendEncryptor == null)
            {
                int length = final.Length;
                Comm.Send(final, ref length);
                return true;
            }

            // goal - dont fill encrypt buffer because it will block stuff like pings during transfers
            // use as temp, return failed if no room

            if (SendBuffer == null)
                SendBuffer = new byte[BUFF_SIZE];

            if (EncryptBuffer == null)
                EncryptBuffer = new byte[BUFF_SIZE];

            LastSends.Enqueue(new Tuple<int, int>(EncryptBuffSize, final.Length));
            if (LastSends.Count > 100)
                LastSends.Dequeue();

            // ensure enough space in encrypt buff for packet and expedite packets
            if (BUFF_SIZE - EncryptBuffSize < final.Length + 128)
                throw new Exception("Packet Dropped");

            // encode put into send buff
            lock (SendBuffer)
            {
                final.CopyTo(SendBuffer, SendBuffSize);
                SendBuffSize += final.Length;
            }

            return FlushSend(); // return true if room in comm buffer
		}

        public bool FlushSend()
        {
            if (SendEncryptor == null)
                return false;

            lock (SendBuffer)
            {
                // add padding to send buff to ensure all packets sent
                int remainder = SendBuffSize % SendBlockSize;

                if (remainder > 0 && SendBuffSize < BUFF_SIZE - 32)
                {
                    int paddingNeeded = SendBlockSize - remainder;

                    if (paddingNeeded == 3)
                        paddingNeeded = 4;

                    // packet empty is 2 bytes, 1 byte extra if there is size info, cant pad 3 bytes :(
                    EncryptionUpdate eu = new EncryptionUpdate(false);
                    if(paddingNeeded > 3)
                        eu.Padding = new byte[paddingNeeded - 3];
                    byte[] final = eu.Encode(Network.Protocol);

                    final.CopyTo(SendBuffer, SendBuffSize);
                    SendBuffSize += final.Length;
                }

                // move data from send buff to encrypt buff
                int transformSize = SendBuffSize - (SendBuffSize % SendBlockSize);
                if (transformSize > 0 && transformSize < BUFF_SIZE - EncryptBuffSize)
                {
                    int transformed = SendEncryptor.TransformBlock(SendBuffer, 0, transformSize, EncryptBuffer, EncryptBuffSize);
                    Debug.Assert(transformSize == transformed);

                    EncryptBuffSize += transformed;
                    SendBuffSize -= transformed;

                    Buffer.BlockCopy(SendBuffer, transformed, SendBuffer, 0, SendBuffSize);
                }

                // send encrypt buff
                if(EncryptBuffSize > 0)
                {
                    Comm.Send(EncryptBuffer, ref EncryptBuffSize);
                }
            }

            // return false if still data to be sent
            return EncryptBuffSize == 0;
        }

        bool RecvStarted;

		public void ReceivePacket(G2ReceivedPacket packet)
		{
			if(packet.Root.Name == CommPacket.Close)
			{
				Receive_Close(packet);
				return;
			}

            else if (packet.Root.Name == CommPacket.CryptPadding)
            { 
                // just padding 
            }

			else if(Status == SessionStatus.Connecting)
			{
                if (packet.Root.Name == CommPacket.SessionRequest)
					Receive_SessionRequest(packet);

                else if (packet.Root.Name == CommPacket.SessionAck)
					Receive_SessionAck(packet);

                else if (packet.Root.Name == CommPacket.KeyRequest)
					Receive_KeyRequest(packet);

                else if (packet.Root.Name == CommPacket.KeyAck)
					Receive_KeyAck(packet);

                else if (packet.Root.Name == CommPacket.CryptStart)
                {
                    Debug.Assert(!RecvStarted);
                    RecvStarted = true;

                    InboundEnc.Padding = PaddingMode.None;
                    RecvDecryptor = InboundEnc.CreateDecryptor();
                }

				return;
			}
			
			else if(Status == SessionStatus.Active)
			{
                if (packet.Root.Name == CommPacket.Data)
                    ReceiveData(packet);

                else if (packet.Root.Name == CommPacket.ProxyUpdate)
                    Receive_ProxyUpdate(packet);

				return;
			}
							
		}

		public void SecondTimer()
		{
			Comm.SecondTimer();

            if(Status == SessionStatus.Connecting)
			{
                if (Core.TimeNow > NegotiateTimeout)
                    Send_Close("Timeout");
			}
			
            if(Status == SessionStatus.Active)
			{
                //crit - still need this?
                if (FlushSend())
                    Core.Transfers.Send_Data(this); // a hack for stalled transfers
			}
		}

		public void Send_KeyRequest()
		{   
            // A connecting to B
            // if A doesnt know B's key, the connection starts with a key request
            // if B doesnt know A's key, once request received from A, B sends a key req

            KeyRequest keyRequest = new KeyRequest();
            
            if (RequestReceived)
            {
                // generate inbound key
                Debug.Assert(InboundEnc == null);
                InboundEnc = new RijndaelManaged();
                InboundEnc.GenerateKey();
                InboundEnc.GenerateIV();

                // make packet
                keyRequest.Encryption = Utilities.CryptType(InboundEnc);
                keyRequest.Key = InboundEnc.Key;
                keyRequest.IV = InboundEnc.IV;
            }

            Log("Key Request Sent");

			SendPacket(keyRequest);
		}

		public void Receive_KeyRequest(G2ReceivedPacket embeddedPacket)
		{
			KeyRequest request = KeyRequest.Decode(embeddedPacket);

            if (request.Key != null)
            {
                OutboundEnc = new RijndaelManaged();
                OutboundEnc.Key = request.Key;
                OutboundEnc.IV = request.IV;

                StartEncryption();
            }

			Send_KeyAck();
		}

		public void Send_KeyAck()
		{
			KeyAck keyAck    = new KeyAck();
            keyAck.PublicKey = Core.User.Settings.KeyPublic;

			Log("Key Ack Sent");

			SendPacket(keyAck);
		}

		public void Receive_KeyAck(G2ReceivedPacket embeddedPacket)
		{
			KeyAck keyAck = KeyAck.Decode(embeddedPacket);
  
            Log("Key Ack Received");

            Core.IndexKey(UserID, ref keyAck.PublicKey);

            // send session request with encrypted current key
            Send_SessionRequest();

            if (RequestReceived)
            {
                Send_SessionAck();
                ConnectAckSent = true;
            }

            // receiving session gets, verifies sender can encrypt with public key and goes alriiight alriight
		}

		public void Send_SessionRequest()
		{
			// build session request, call send packet
			SessionRequest request = new SessionRequest();

            // generate inbound key, inbound known if keyack completing
            if (InboundEnc == null)
            {
                InboundEnc = new RijndaelManaged();
                InboundEnc.GenerateKey();
                InboundEnc.GenerateIV();
            }

            // encode session key with remote hosts public key (should be 48 bytes, 16 + 32)
            byte[] sessionKey = new byte[InboundEnc.Key.Length + InboundEnc.IV.Length];
            InboundEnc.Key.CopyTo(sessionKey, 0);
            InboundEnc.IV.CopyTo(sessionKey, InboundEnc.Key.Length);
            request.EncryptedKey = Utilities.KeytoRsa(Core.KeyMap[UserID]).Encrypt(sessionKey, false);

            Log("Session Request Sent");

            SendPacket(request);
		}

		public void Receive_SessionRequest(G2ReceivedPacket embeddedPacket)
		{
			SessionRequest request = SessionRequest.Decode(embeddedPacket);

            Log("Session Request Received");
            RequestReceived = true;

            byte[] sessionKey = Core.User.Settings.KeyPair.Decrypt(request.EncryptedKey, false);

            // new connection
            if (OutboundEnc == null)
            {
                OutboundEnc = new RijndaelManaged();
                OutboundEnc.Key = Utilities.ExtractBytes(sessionKey, 0, 32);
                OutboundEnc.IV = Utilities.ExtractBytes(sessionKey, 32, 16);
            }

            // if key request
            else
            {
                if(Utilities.MemCompare(OutboundEnc.Key, Utilities.ExtractBytes(sessionKey, 0, 32)) == false ||
                    Utilities.MemCompare(OutboundEnc.IV, Utilities.ExtractBytes(sessionKey, 32, 16)) == false)
                {
                    Send_Close("Verification after key request failed");
                    return;
                }

                Send_SessionAck();
                ConnectAckSent = true;
                return;
            }

			// if public key null
			if(!Core.KeyMap.ContainsKey(UserID))
			{
                StartEncryption();
				Send_KeyRequest();
				return;
			}

            if (Comm.Listening)
                Send_SessionRequest();

            StartEncryption();
            Send_SessionAck();

            ConnectAckSent = true;
		}

        bool EncryptionStarted = false;

        private void StartEncryption()
        {
            Debug.Assert(!EncryptionStarted);
            EncryptionStarted = true;
            
            SendPacket( new EncryptionUpdate(true) ); // dont expedite because very next packet is expedited

            OutboundEnc.Padding = PaddingMode.None;
            SendEncryptor = OutboundEnc.CreateEncryptor();

            Log("Encryption Started");
        }

		public void Send_SessionAck()
		{
			SessionAck ack = new SessionAck();
            ack.Name = Core.User.Settings.UserName;

            Debug.Assert(ack.Name != "");

            Log("Session Ack Sent");

			SendPacket(ack);
		}

		public void Receive_SessionAck(G2ReceivedPacket embeddedPacket)
		{
			SessionAck ack = SessionAck.Decode(embeddedPacket);

            Core.IndexName(UserID, ack.Name);

            Log("Session Ack Received");

            if( AlreadyActive() )
            {
                Send_Close("Already Active");
                return;
            }

            if( !ConnectAckSent )
            {
                Send_Close("Ack not Received");
                return;
            }

            UpdateStatus(SessionStatus.Active);
		}

        public bool SendData(uint service, uint datatype, G2Packet packet)
        {
            CommData data = new CommData(service, datatype, packet.Encode(Network.Protocol));

            Core.ServiceBandwidth[service].OutPerSec += data.Data.Length;

            return SendPacket(data);
        }

        public void ReceiveData(G2ReceivedPacket embeddedPacket)
        {
            // 0 is network packet?

            CommData data = CommData.Decode(embeddedPacket);

            if (data == null)
                return;

            if (Core.ServiceBandwidth.ContainsKey(data.Service))
                Core.ServiceBandwidth[data.Service].InPerSec += data.Data.Length;

            if (RudpControl.SessionData.Contains(data.Service, data.DataType))
                RudpControl.SessionData[data.Service, data.DataType].Invoke(this, data.Data);
        }

		public void Send_Close(string reason)
		{
            if (Status == SessionStatus.Closed)
            {
                Debug.Assert(false);
                return;
            }

            CloseMsg = reason;

			Log("Sending Close (" + reason + ")");

			CommClose close = new CommClose();
			close.Reason    = reason;

            SendPacket(close);
            Comm.Close(); 

			UpdateStatus(SessionStatus.Closed);
            // socket not closed until fin received
		}
		
		public void Receive_Close(G2ReceivedPacket embeddedPacket)
		{
			CommClose close = CommClose.Decode(embeddedPacket);

            CloseMsg = close.Reason;

			Log("Received Close (" + close.Reason + ")");

			UpdateStatus(SessionStatus.Closed);
		}

        public void Send_ProxyUpdate(TcpConnect tcp)
        {
            //crit handle special if tcp is global

            ProxyUpdate update = new ProxyUpdate();
            update.Proxy = new DhtAddress(tcp.RemoteIP, tcp);

            Log("Sent Proxy Update (" + update.Proxy + ")");

            SendPacket(update);
        }

        public void Receive_ProxyUpdate(G2ReceivedPacket embeddedPacket)
        {
            ProxyUpdate update = ProxyUpdate.Decode(embeddedPacket);

            Comm.AddAddress(new RudpAddress(update.Proxy));

            if (embeddedPacket.ReceivedTcp)
                Comm.AddAddress(new RudpAddress(update.Proxy, embeddedPacket.Tcp));

            Comm.CheckRoutes();

            Log("Received Proxy Update (" + update.Proxy + ")");
        }

        public bool AlreadyActive()
        {
            int activeCount = RudpControl.SessionMap.Values.Where( s =>
                                    s != this &&
                                    s.UserID == UserID &&
                                    s.ClientID == ClientID &&
                                    s.Status == SessionStatus.Active).Count();

            return activeCount > 0;
        }

		public void Log(string entry)
		{
            //PeerID 10:23:250 : 
            string prefix = Comm.PeerID.ToString() + " ";
            prefix += Core.TimeNow.Minute.ToString() + ":" + Core.TimeNow.Second.ToString();

            if (Core.TimeNow.Millisecond == 0)
                prefix += ":00";
            else
                prefix += ":" + Core.TimeNow.Millisecond.ToString().Substring(0, 2);

            Core.Network.UpdateLog("RUDP " + Core.GetName(UserID), prefix + ": " + entry);

            //byte[] data = strEnc.GetBytes(Comm.PeerID.ToString() + ": " + entry + "\r\n");
            //DebugWriter.Write(data, 0, data.Length);
		}

		public void OnConnect()
		{
            Log("OnConnect");

            // it can take a while to get the rudp session up
            // especially between two blocked hosts
            NegotiateTimeout = Core.TimeNow.AddSeconds(10);

            if (Core.KeyMap.ContainsKey(UserID))
                Send_SessionRequest();
            else
                Send_KeyRequest();
		}

		public void OnAccept()
        {
            Log("OnAccept");

			//wait for remote host to send session request
            NegotiateTimeout = Core.TimeNow.AddSeconds(10);
		}
        
		public void OnReceive()
		{
            if(ReceiveBuffer == null)
                ReceiveBuffer = new byte[BUFF_SIZE];

            int recvd = Comm.Receive(ReceiveBuffer, RecvBuffSize, BUFF_SIZE - RecvBuffSize);

            if (recvd <= 0)
                return;

            int start = 0;
            RecvBuffSize += recvd;

            // get next packet
            G2ReadResult streamStatus = G2ReadResult.PACKET_GOOD;

            while (streamStatus == G2ReadResult.PACKET_GOOD)
            {
                G2ReceivedPacket packet = new G2ReceivedPacket();

                start = 0;

                // if encryption off
                if (RecvDecryptor == null)
                {
                    packet.Root = new G2Header(ReceiveBuffer);
                    streamStatus = G2Protocol.ReadNextPacket(packet.Root, ref start, ref RecvBuffSize);

                    if (streamStatus != G2ReadResult.PACKET_GOOD)
                        break;

                    PacketLogEntry logEntry = new PacketLogEntry(Core.TimeNow, TransportProtocol.Rudp, DirectionType.In, Comm.PrimaryAddress.Address, Utilities.ExtractBytes(packet.Root.Data, packet.Root.PacketPos, packet.Root.PacketSize));
                    Core.Network.LogPacket(logEntry);

                    ReceivePacket(packet);

                    if (start > 0 && RecvBuffSize > 0)
                        Buffer.BlockCopy(ReceiveBuffer, start, ReceiveBuffer, 0, RecvBuffSize);
                }

                // else if encryption on
                else 
                {
                    // if data needs to be decrypted from receive buffer
                    if (RecvBuffSize >= RecvBlockSize)
                    {
                        int transLength = RecvBuffSize - (RecvBuffSize % RecvBlockSize);
                        int spaceAvail  = BUFF_SIZE - DecryptBuffSize;

                        if(spaceAvail < transLength)
                            transLength = spaceAvail - (spaceAvail % RecvBlockSize);

                        if (transLength >= RecvBlockSize)
                        {
                            if (DecryptBuffer == null)
                                DecryptBuffer = new byte[BUFF_SIZE];

                            int transformed = RecvDecryptor.TransformBlock(ReceiveBuffer, 0, transLength, DecryptBuffer, DecryptBuffSize); 
                            Debug.Assert(transformed == transLength);

                            DecryptBuffSize += transformed;
                            RecvBuffSize -= transLength;

                            Buffer.BlockCopy(ReceiveBuffer, transLength, ReceiveBuffer, 0, RecvBuffSize);
                        }
                    }

                    // read packets from decrypt buffer
                    packet.Root = new G2Header(DecryptBuffer);
                    
                    //crit - delete
                    //int lastStart = start;
                    //int lastBuffSize = DecryptBuffSize;
                    //byte[] lastBuffer = Utilities.ExtractBytes(DecryptBuffer, 0, DecryptBuffSize);
              
                    streamStatus = G2Protocol.ReadNextPacket(packet.Root, ref start, ref DecryptBuffSize);

                    if (streamStatus == G2ReadResult.PACKET_ERROR)
                    {
                        //crit - debug this
                        Send_Close("Session Packet Error");
                        break ;
                    }

                    if (streamStatus != G2ReadResult.PACKET_GOOD)
                        break;

                    PacketLogEntry logEntry = new PacketLogEntry(Core.TimeNow, TransportProtocol.Rudp, DirectionType.In, Comm.PrimaryAddress.Address, Utilities.ExtractBytes(packet.Root.Data, packet.Root.PacketPos, packet.Root.PacketSize));
                    Core.Network.LogPacket(logEntry);

                    ReceivePacket(packet);

                    if (start > 0 && DecryptBuffSize > 0)
                        Buffer.BlockCopy(DecryptBuffer, start, DecryptBuffer, 0, DecryptBuffSize);
                }
            }
		}

		public void OnSend()
		{
            // try to flush remaining data
            if (!FlushSend())
                return;

            Core.Transfers.Send_Data(this);
		}

		public void OnClose()
		{
            Log("OnClose");

            UpdateStatus(SessionStatus.Closed);
		}

        public bool SendBuffLow()
        {
            if (Comm != null)
                return Comm.SendBuffLow();

            return true;
        }

        public void SendUnreliable(uint service, uint type, G2Packet packet)
        {
            // fast, secure, out-of-band method of sending data
            // useful for things like VOIP during a file transfer with host
            // data has got to go out asap, no matter what

            // check rudp socket is connected
            if (Status != SessionStatus.Active)
                return;

            // add to special rudp packet
            RudpPacket rudp = new RudpPacket();
            rudp.SenderID = Network.Local.UserID;
            rudp.SenderClient = Network.Local.ClientID;
            rudp.TargetID = UserID;
            rudp.TargetClient = ClientID;
            rudp.PeerID = Comm.RemotePeerID;
            rudp.PacketType = RudpPacketType.Unreliable;
          
            CommData data = new CommData(service, type, packet.Encode(Network.Protocol));
            rudp.Payload = Utilities.EncryptBytes(data.Encode(Network.Protocol), OutboundEnc.Key);

            // send
            Comm.SendPacket(rudp, Comm.PrimaryAddress);

            // stats
            Core.ServiceBandwidth[service].OutPerSec += data.Data.Length;

            PacketLogEntry logEntry = new PacketLogEntry(Core.TimeNow, TransportProtocol.Rudp, DirectionType.Out, Comm.PrimaryAddress.Address, rudp.Payload);
            Core.Network.LogPacket(logEntry);
        }

        public void UnreliableReceive(byte[] data)
        {
            byte[] decrypted = Utilities.DecryptBytes(data, data.Length, InboundEnc.Key);

            G2ReceivedPacket packet = new G2ReceivedPacket();
            packet.Root = new G2Header(decrypted);

            if (G2Protocol.ReadPacket(packet.Root))
            {
                PacketLogEntry logEntry = new PacketLogEntry(Core.TimeNow, TransportProtocol.Rudp, DirectionType.In, Comm.PrimaryAddress.Address, decrypted);
                Core.Network.LogPacket(logEntry);

                ReceivePacket(packet);
            }
        }
    }
}
