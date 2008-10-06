using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;


using RiseOp.Implementation.Protocol;
using RiseOp.Implementation.Protocol.Comm;
using RiseOp.Implementation.Protocol.Net;
using RiseOp.Implementation.Protocol.Special;
using RiseOp.Implementation.Transport;

using RiseOp.Services;
using RiseOp.Services.Location;
using RiseOp.Services.Transfer;

using RiseOp.Interface.Tools;


namespace RiseOp.Implementation.Dht
{
    internal delegate void StatusChange();
    
    internal class DhtNetwork
    {
        // super-class
        internal OpCore Core;

        // sub-class
        internal G2Protocol Protocol;
        internal TcpHandler TcpControl;
        internal UdpHandler UdpControl;
        internal LanHandler LanControl;
        internal RudpHandler RudpControl;
        internal LightCommHandler LightComm;

        internal OpCache Cache;
        internal DhtRouting Routing;
        internal DhtStore Store;
        internal DhtSearchControl Searches;


        internal bool IsLookup;
        internal LookupSettings LookupConfig;
        internal bool LanMode = true;

        internal DhtClient Local;
        internal ulong OpID;

        internal bool Responsive; // able to talk to dht
        internal bool Established; // situated on dht, ready to publish to adjacents
        
        internal int FireStatusChange; // timeout until established is called
        internal StatusChange StatusChange; // operation only

        byte[] LookupKey = new byte[]  {0x33,0xf6,0x89,0xf3,0xd2,0xf5,0xae,0xc2,
                                        0x49,0x59,0xe6,0xbb,0xe2,0xc6,0x3c,0xc8,
                                        0x5e,0x63,0x0c,0x7a,0xb9,0x08,0x18,0xd4,
                                        0xf9,0x73,0x9f,0x52,0xd6,0xf4,0x34,0x0e};

        // lookup IM is a secret network, not public because it is not published on the lookup network
        internal static byte[] GlobalIMKey = new byte[]  {  0x23,0x1d,0x25,0xe5,0xca,0x0b,0x65,0xb3,
                                            0x65,0x04,0x2b,0x1c,0x61,0x1d,0x20,0x94 ,
                                            0x18,0xf3,0x08,0x3d,0x01,0xf6,0x97,0x8a ,
                                            0x6c,0x76,0xda,0x4b,0x70,0x88,0x00,0xaa};

        internal RijndaelManaged OpCrypt;
        internal RijndaelManaged AugmentedCrypt;
        internal byte[] AugmentBytes = new byte[8];

        // log
        internal Queue<G2ReceivedPacket> IncomingPackets = new Queue<G2ReceivedPacket>();
        internal Queue<PacketLogEntry> LoggedPackets = new Queue<PacketLogEntry>();
        internal Dictionary<string, Queue<string>> LogTable = new Dictionary<string, Queue<string>>();

        // gui
        internal PacketsForm  GuiPackets;
        internal CrawlerForm  GuiCrawler;
        internal GraphForm    GuiGraph;
        internal TransferView GuiTransfers;


        internal DhtNetwork(OpCore core, bool lookup)
        {
            Core = core;
            IsLookup = lookup;

            Cache = new OpCache(this); // lookup config loads cache entries

            if (IsLookup)
                LookupConfig = LookupSettings.Load(this);

            Local = new DhtClient();
            Local.UserID = IsLookup ? LookupConfig.UserID : Utilities.KeytoID(Core.User.Settings.KeyPublic);
            Local.ClientID = (ushort)Core.RndGen.Next(1, ushort.MaxValue);

            OpID = Utilities.KeytoID(IsLookup ? LookupKey : Core.User.Settings.OpKey);

            OpCrypt = new RijndaelManaged();

            // load encryption
            if (IsLookup)
                OpCrypt.Key = LookupKey;
            else
                OpCrypt.Key = Core.User.Settings.OpKey;

            AugmentedCrypt = new RijndaelManaged();
            AugmentedCrypt.Key = (byte[])OpCrypt.Key.Clone();

            Protocol = new G2Protocol();
            TcpControl = new TcpHandler(this);
            UdpControl = new UdpHandler(this);
            LanControl = new LanHandler(this);
            RudpControl = new RudpHandler(this);
            LightComm = new LightCommHandler(this);
            
            Routing = new DhtRouting(this);
            Store = new DhtStore(this);
            Searches = new DhtSearchControl(this);
        }

        internal void SecondTimer()
        {
            // timers
            Cache.SecondTimer();
            TcpControl.SecondTimer();
            UdpControl.SecondTimer();
            RudpControl.SecondTimer();
            LightComm.SecondTimer();
            Routing.SecondTimer();
            Searches.SecondTimer();


            if (!IsLookup)
                CheckLookupProxyMode();


            CheckConnectionStatus();


            // established in dht
            if (FireStatusChange > 0)
            {
                FireStatusChange--;

                if (FireStatusChange == 0)
                {
                    Established = true;

                    if (StatusChange != null)
                        StatusChange.Invoke();
                }
            }
        }

        internal void CheckConnectionStatus()
        {
            // dht responsiveness is only reliable if we can accept incoming connections, other wise we might be 
            // behind a NAT and in that case won't be able to receive traffic from anyone who has not sent us stuff
            bool connected = (Routing.DhtEnabled && Routing.DhtResponsive) ||
                            TcpControl.ProxyServers.Count > 0 || TcpControl.ProxyClients.Count > 0;


            if (connected == Responsive)
                return;

            string name = IsLookup ? "Lookup" : Core.User.Settings.Operation;

            // else set new value
            Responsive = connected;

            if (Responsive)
            {
                // done to fill up routing table down to self

                Searches.Start(Routing.LocalRoutingID + 1, "Self", Core.DhtServiceID, 0, null, new EndSearchHandler(EndSelfSearch));
                Routing.NextSelfSearch = Core.TimeNow.AddHours(1);

                // at end of self search, status change count down triggered

                UpdateLog("general", name + " network connected");
            }

            // network dead
            else
            {
                Established = false;

                SetLanMode(true);

                Cache.Reset();

                if (StatusChange != null)
                    StatusChange.Invoke();

                UpdateLog("general", name + " network disconnected");
            }
        }

        internal void EndSelfSearch(DhtSearch search)
        {
            // if not already established (an hourly self re-search)
            if (!Established)
            {
                // a little buffer time for local nodes to send patch files
                // so we dont start sending our own huge patch files
                FireStatusChange = 10;
            }
        }

        internal void FirewallChangedtoOpen()
        {
            if (Core.InvokeRequired)
            {
                Core.RunInCoreAsync(delegate() { FirewallChangedtoOpen(); });
                return;
            }

            //close proxy connects
            lock (TcpControl.SocketList)
                foreach (TcpConnect connection in TcpControl.SocketList)
                    if (connection.State == TcpState.Connected && connection.Proxy == ProxyType.Server) //crit ?? close everything, even unset proxies
                        connection.CleanClose("Firewall changed to Open", true);
        }

        internal void FirewallChangedtoNAT()
        {
            if (Core.InvokeRequired)
            {
                Core.RunInCoreAsync(delegate() { FirewallChangedtoNAT(); });
                return;
            }

            //update proxy connects
            lock (TcpControl.SocketList)
                foreach (TcpConnect connection in TcpControl.SocketList)
                    if (connection.Proxy == ProxyType.Server)
                    {
                        ProxyReq request = new ProxyReq();
                        request.SenderID = Local.UserID;
                        request.Type = ProxyType.ClientNAT;
                        connection.SendPacket(request);
                    }
        }

        internal DhtSource GetLocalSource()
        {
            DhtSource source = new DhtSource();

            source.UserID = Local.UserID;
            source.ClientID = Local.ClientID;
            source.TcpPort = TcpControl.ListenPort;
            source.UdpPort = UdpControl.ListenPort;
            source.Firewall = Core.Firewall;

            return source;
        }

        internal DhtContact GetLocalContact()
        {
            return new DhtContact(Local.UserID, Local.ClientID, Core.LocalIP, TcpControl.ListenPort, UdpControl.ListenPort);
        }

        internal void IncomingPacket(G2ReceivedPacket packet)
        {
            if (Core.Sim == null || Core.Sim.Internet.TestCoreThread)
            {
                lock (IncomingPackets)
                    if (IncomingPackets.Count < 100)
                        IncomingPackets.Enqueue(packet);

                Core.ProcessEvent.Set();
            }
            else
            {
                try
                {
                    ReceivePacket(packet);
                }
                catch (Exception ex)
                {
                    UpdateLog("Exception", "DhtNetwork::IncomingPacket: " + ex.Message);
                }
            }
        }

        internal void ReceivePacket(G2ReceivedPacket packet)
        {
            // Network packet
            if (packet.Root.Name == RootPacket.Network)
            {
                NetworkPacket netPacket = NetworkPacket.Decode(packet.Root);

                G2ReceivedPacket embedded = new G2ReceivedPacket();
                embedded.Tcp = packet.Tcp;
                embedded.Source = packet.Source;
                embedded.Source.UserID = netPacket.SourceID;
                embedded.Source.ClientID = netPacket.ClientID;
                embedded.Root = new G2Header(netPacket.InternalData);

                // from - received from proxy server
                if (netPacket.FromAddress != null)
                {
                    if (packet.ReceivedUdp)
                        throw new Exception("From tag set on packet received udp");
                    if (packet.Tcp.Proxy != ProxyType.Server)
                        throw new Exception("From tag (" + netPacket.FromAddress.ToString() + ") set on packet not received from server (" + packet.Tcp.ToString() + ")");

                    embedded.Source = new DhtContact(netPacket.FromAddress);
                }

                // to - received from proxied node, and not for us
                if (netPacket.ToAddress != null &&
                    !(netPacket.ToAddress.UserID == Local.UserID && netPacket.ToAddress.ClientID == Local.ClientID))
                {
                    if (packet.ReceivedUdp)
                        throw new Exception("To tag set on packet received udp");
                    if (packet.Tcp.Proxy == ProxyType.Server || packet.Tcp.Proxy == ProxyType.Unset)
                        throw new Exception("To tag set on packet received from server");

                    DhtAddress address = netPacket.ToAddress;
                    netPacket.ToAddress = null;

                    TcpConnect direct = TcpControl.GetProxy(address);

                    if (direct != null)
                        direct.SendPacket(netPacket);
                    else
                        UdpControl.SendTo(address, netPacket);

                    return;
                }

                // process
                if (G2Protocol.ReadPacket(embedded.Root))
                    ReceiveNetworkPacket(embedded);
            }

            // Tunnel Packet
            else if (packet.Root.Name == RootPacket.Tunnel)
            {
                // can only tunnel over lookup network
                if (!IsLookup)
                    return;

                PacketLogEntry logEntry = new PacketLogEntry(Core.TimeNow, TransportProtocol.Tunnel, DirectionType.In, packet.Source, packet.Root.Data);
                LogPacket(logEntry);

                TunnelPacket tunnel = TunnelPacket.Decode(packet.Root);

                // handle locally
                if (tunnel.Target.Equals(Local))
                {
                    Core.Context.Cores.LockReading(delegate()
                    {
                        foreach (OpCore core in Core.Context.Cores)
                            if (core.TunnelID == tunnel.Target.TunnelID)
                                core.Network.ReceiveTunnelPacket(packet, tunnel); 
                    });

                }
                else if (tunnel.TargetServer != null)
                {
                    TcpConnect direct = TcpControl.GetProxy(tunnel.Target);

                    // if directly connected add from and forwared
                    if (direct != null)
                        direct.SendPacket(tunnel);

                    // only forward udp if received over tcp from a proxied host
                    else if (tunnel.TargetServer != null && packet.ReceivedTcp && packet.Tcp.Proxy != ProxyType.Server)
                        UdpControl.SendTo(tunnel.TargetServer, tunnel);
                }
            }

            // Communication Packet
            else if (packet.Root.Name == RootPacket.Comm)
            {
                RudpPacket commPacket = RudpPacket.Decode(packet);

                packet.Source.UserID = commPacket.SenderID;
                packet.Source.ClientID = commPacket.SenderClient;

                // For local host
                if (commPacket.TargetID == Local.UserID && commPacket.TargetClient == Local.ClientID)
                {
                    if (packet.ReceivedTcp && commPacket.FromEndPoint != null)
                        packet.Source = new DhtContact(commPacket.FromEndPoint);

                    ReceiveCommPacket(packet, commPacket);
                    return;
                }

                // Also Forward to appropriate node
                TcpConnect socket = TcpControl.GetProxy(commPacket.TargetID, commPacket.TargetClient);

                if (socket != null)
                {
                    // strip TO flag, add from address
                    commPacket.ToEndPoint = null;
                    commPacket.FromEndPoint = packet.Source;

                    commPacket.SenderID = Local.UserID;
                    commPacket.SenderClient = Local.ClientID;
                    socket.SendPacket(commPacket);
                    return;
                }

                // forward udp if TO flag marked
                if (packet.ReceivedTcp && commPacket.ToEndPoint != null)
                {
                    DhtAddress address = commPacket.ToEndPoint;

                    commPacket.ToEndPoint = null; // strip TO flag

                    commPacket.SenderID = Local.UserID;
                    commPacket.SenderClient = Local.ClientID;
                    UdpControl.SendTo(address, commPacket);
                }
            }
        }

        internal void ReceiveCommPacket(G2ReceivedPacket raw, RudpPacket packet)
        {
            try
            {
                if (packet.PacketType == RudpPacketType.Light ||
                    packet.PacketType == RudpPacketType.LightAck)
                {
                    LightComm.ReceivePacket(raw, packet);
                    return;
                }

                // if a socket already set up
                lock (RudpControl.SocketMap)
                    if (RudpControl.SocketMap.ContainsKey(packet.PeerID))
                    {
                        RudpControl.SocketMap[packet.PeerID].RudpReceive(raw, packet, IsLookup);
                        return;
                    }

                // if starting new session
                if (packet.PacketType != RudpPacketType.Syn)
                    return;

                RudpSyn syn = new RudpSyn(packet.Payload);

                // prevent connection from self
                if (syn.SenderID == Local.UserID && syn.ClientID == Local.ClientID)
                    return;


                // find connecting session with same or unknown client id
                ulong id = syn.SenderID ^ syn.ClientID;

                if (RudpControl.SessionMap.ContainsKey(id))
                {
                    RudpSession session = RudpControl.SessionMap[id];

                    // if session id zero or matches forward
                    if ((session.Comm.State == RudpState.Connecting && session.Comm.RemotePeerID == 0) ||
                        (session.Comm.State != RudpState.Closed && session.Comm.RemotePeerID == syn.ConnID)) // duplicate syn
                    {
                        session.Comm.RudpReceive(raw, packet, IsLookup);
                    }
                    else
                        session.Log("Session request denied (already active)");

                    return;
                }

                //crit check if this is the peer id of a failed connection attempt
                /*if (buddy.LastPeerIDs.Contains(syn.ConnID))
                {
                    buddy.Log("Session denied due to recent peer id");
                    return;
                }*/


                // if clientid not in session, create new session
                RudpSession newSession = new RudpSession(RudpControl, syn.SenderID, syn.ClientID, true);

                RudpControl.SessionMap[id] = newSession;

                // send ack before sending our own syn (connect)
                // ack tells remote which address is good so that our syn's ack comes back quickly
                newSession.Comm.RudpReceive(raw, packet, IsLookup);

                newSession.Connect();


                UpdateLog("RUDP", "Inbound session accepted to ClientID " + syn.ClientID.ToString());
            }
            catch (Exception ex)
            {
                UpdateLog("Exception", "KimCore::ReceiveCommPacket: " + ex.Message);
            }
        }

        internal void ReceiveNetworkPacket(G2ReceivedPacket packet)
        {
            // Search request
            if (packet.Root.Name == NetworkPacket.SearchRequest)
                Searches.ReceiveRequest(packet);

            // Search ack
            else if (packet.Root.Name == NetworkPacket.SearchAck)
                Searches.ReceiveAck(packet);

            // Ping
            else if (packet.Root.Name == NetworkPacket.Ping)
                Receive_Ping(packet);

            // Pong
            else if (packet.Root.Name == NetworkPacket.Pong)
                Receive_Pong(packet);

            // Store
            else if (packet.Root.Name == NetworkPacket.StoreRequest)
                Store.Receive_StoreReq(packet);

            // Proxy request
            else if (packet.Root.Name == NetworkPacket.ProxyRequest)
                Receive_ProxyRequest(packet);

            // Proxy ack
            else if (packet.Root.Name == NetworkPacket.ProxyAck)
                Receive_ProxyAck(packet);

            // Bye
            else if (packet.Root.Name == NetworkPacket.Bye && packet.ReceivedTcp)
                TcpControl.Receive_Bye(packet);

            // Crawl Request
            else if (packet.Root.Name == NetworkPacket.CrawlRequest)
                Receive_CrawlRequest(packet);

            // Crawl Ack
            else if (packet.Root.Name == NetworkPacket.CrawlAck)
                Receive_CrawlAck(packet);

            // unknown packet
            else
            {
                UpdateLog("Exception", "Uknown packet type " + packet.Root.Name.ToString());
            }
        }

        // nodes in lookup proxy mode are psuedo-open, instead of udp they send tunneled packets
        // tunnel packets include routing information to the lookup target as well as
        // the encrytped operation packet embedded in the payload
        internal int SendTunnelPacket(DhtAddress contact, G2Packet embed)
        {
            Debug.Assert(contact.TunnelClient != null && contact.TunnelServer != null);
            Debug.Assert(Core.Context.Lookup != null);
            Debug.Assert(!IsLookup);
            Debug.Assert(Core.User.Settings.OpAccess != AccessType.Secret);

            if (IsLookup ||
                Core.Context.Lookup == null ||
                Core.User.Settings.OpAccess == AccessType.Secret)
                return 0;

            OpCore lookup = Core.Context.Lookup;

            // tunnel packet through lookup network
            byte[] encoded = embed.Encode(Protocol);

            PacketLogEntry logEntry = new PacketLogEntry(Core.TimeNow, TransportProtocol.Tunnel, DirectionType.Out, contact, encoded);
            LogPacket(logEntry);

            TunnelPacket packet = new TunnelPacket();

            // encrypt, turn off encryption during simulation
            if (Core.Sim == null || Core.Sim.Internet.TestEncryption)
            {
                lock (AugmentedCrypt)
                {
                    SetAugmentedKey(contact.UserID);
                    packet.Payload = Utilities.EncryptBytes(encoded, AugmentedCrypt.Key);
                }
            }
            else
                packet.Payload = encoded;

            packet.Source = new TunnelAddress(lookup.Network.Local, Core.TunnelID);
            packet.Target = contact.TunnelClient;

            int bytesSent = 0;

            // if we are the tunnel server (our lookup net is open, but op is blocked)
            if (lookup.Network.Local.Equals(contact.TunnelServer)) // use dhtclient compare
            {
                lookup.RunInCoreAsync(delegate()
                {
                    TcpConnect direct = lookup.Network.TcpControl.GetProxy(packet.Target);

                    if (direct != null)
                    {
                        packet.SourceServer = new DhtAddress(Core.LocalIP, lookup.Network.GetLocalSource());
                        bytesSent = direct.SendPacket(packet);
                    }
                });

                return bytesSent;
            }

            // if not open send proxied through local lookup proxy
            // NAT as well because receiver would need to send all responses through same local lookup proxy
            // for NATd host to get replies
            if (Core.Firewall != FirewallType.Open)
            {
                packet.TargetServer = contact.TunnelServer;

                lookup.RunInCoreAsync(delegate()
                {
                    TcpConnect server = lookup.Network.TcpControl.GetProxy(packet.TargetServer) ?? // direct path
                                        lookup.Network.TcpControl.GetProxyServer(contact.IP) ?? // reRoute through same server
                                        lookup.Network.TcpControl.GetRandomProxy(); // random proxy

                    if (server != null)
                    {
                        packet.SourceServer = new DhtAddress(server.RemoteIP, server);
                        bytesSent = server.SendPacket(packet);
                    }
                });
            }
            // else we are open, send op ip address in the souce server
            else
            {
                packet.SourceServer = new DhtAddress(Core.LocalIP, lookup.Network.GetLocalSource());

                lookup.RunInCoreAsync(delegate()
                {
                    bytesSent = lookup.Network.UdpControl.SendTo(contact.TunnelServer, packet);
                });
            }

            return bytesSent;
        }

        internal void ReceiveTunnelPacket(G2ReceivedPacket raw, TunnelPacket tunnel)
        {
            if (Core.InvokeRequired) // called from  lookup core's thread
            {
                Core.RunInCoreAsync(delegate() { ReceiveTunnelPacket(raw, tunnel); });
                return;
            }

            Debug.Assert(!IsLookup);

            if (IsLookup)
                return;

            // decrypt internal packet
            if (Core.Sim == null || Core.Sim.Internet.TestEncryption) // turn off encryption during simulation
            {
                if (tunnel.Payload.Length < AugmentedCrypt.IV.Length)
                    throw new Exception("Not enough data received for IV");

                lock (AugmentedCrypt)
                {
                    SetAugmentedKey(Local.UserID);
                    tunnel.Payload = Utilities.DecryptBytes(tunnel.Payload, tunnel.Payload.Length, AugmentedCrypt.Key);
                }
            }

            G2ReceivedPacket opPacket = new G2ReceivedPacket();
            opPacket.Root = new G2Header(tunnel.Payload);

            // set source information
            if (G2Protocol.ReadPacket(opPacket.Root))
            {
                opPacket.Source = new DhtAddress();

                // used to add direct op contact if source firewall is open
                // or re-routing through same lookup proxy
                opPacket.Source.IP = raw.Source.IP;

                // op user/client set by net/comm processing

                opPacket.Source.TunnelClient = tunnel.Source;
                opPacket.Source.TunnelServer = tunnel.SourceServer;

                PacketLogEntry logEntry = new PacketLogEntry(Core.TimeNow, TransportProtocol.Tunnel, DirectionType.In, opPacket.Source, opPacket.Root.Data);
                LogPacket(logEntry);

                IncomingPacket(opPacket);
            }
        }

        internal void Send_Ping(DhtContact contact)
        {
            Ping ping = new Ping();
            ping.Source = GetLocalSource();
            ping.RemoteIP = contact.IP;
            ping.Ident = contact.Ident = (ushort)Core.RndGen.Next(ushort.MaxValue);

            // always send ping udp, tcp pings are sent manually
            int sentBytes = SendPacket(contact, ping);

            Core.ServiceBandwidth[Core.DhtServiceID].OutPerSec += sentBytes;
        }

        internal int SendPacket(DhtAddress contact, G2Packet packet)
        {
            if (contact.TunnelServer != null)
                return SendTunnelPacket(contact, packet);
            else
                return UdpControl.SendTo(contact, packet);
        }

        void Receive_Ping(G2ReceivedPacket packet)
        {
            Core.ServiceBandwidth[Core.DhtServiceID].InPerSec += packet.Root.Data.Length;

            Ping ping = Ping.Decode(packet);

            bool lanIP = Utilities.IsLocalIP(packet.Source.IP);
            bool validSource = (!lanIP || LanMode && lanIP);

            // set local IP
            SetLocalIP(ping.RemoteIP, packet);


            // check loop back
            if (ping.Source != null && Local.Equals(ping.Source))
            {
                if (packet.ReceivedTcp)
                    packet.Tcp.CleanClose("Loopback connection");

                return;
            }

            // dont send back pong if received tunneled and no longer need to use lookup proxies
            // remote would only send tunneled ping if UseGlobalProxies published info on network
            // let our lookup address expire from remote's routing table
            if (packet.Tunneled && !UseLookupProxies)
                return;

            // setup pong reply
            Pong pong = new Pong();

            if (ping.Source != null)
                pong.Source = GetLocalSource();

            if (ping.RemoteIP != null)
                pong.RemoteIP = packet.Source.IP;

            int sentBytes = 0;

            // received tcp
            if (packet.ReceivedTcp)
            {
                if (ping.Source == null)
                {
                    packet.Tcp.SendPacket(pong);
                    return;
                }

                if (validSource)
                {
                    if (ping.Source.Firewall == FirewallType.Open)
                        Routing.Add(new DhtContact(ping.Source, packet.Source.IP));

                    // received incoming tcp means we are not firewalled
                    if (!packet.Tcp.Outbound)
                    {
                        // done here to prevent setting open for loopback tcp connection
                        Core.SetFirewallType(FirewallType.Open);
                        pong.Source.Firewall = FirewallType.Open;
                    }
                }

                // check if already connected
                if (packet.Tcp.Proxy == ProxyType.Unset && TcpControl.GetProxy(ping.Source) != null)
                {
                    packet.Tcp.CleanClose("Dupelicate Connection");
                    return;
                }

                packet.Tcp.UserID = ping.Source.UserID;
                packet.Tcp.ClientID = ping.Source.ClientID;
                packet.Tcp.TcpPort = ping.Source.TcpPort;
                packet.Tcp.UdpPort = ping.Source.UdpPort;

                // if inbound connection, to our open host, and haven't checked fw yet
                if (!packet.Tcp.Outbound &&
                    ping.Source.Firewall != FirewallType.Open &&
                    !packet.Tcp.CheckedFirewall)
                {
                    TcpControl.MakeOutbound(packet.Source, ping.Source.TcpPort, "check firewall");
                    packet.Tcp.CheckedFirewall = true;
                }

                pong.Direct = true;
                sentBytes = packet.Tcp.SendPacket(pong);

                // dont send close if proxies maxxed yet, because their id might be closer than current proxies
            }

            // ping received udp or tunneled
            else
            {
                if (validSource)
                {
                    // received udp traffic, we must be behind a NAT at least
                    if (Core.Firewall == FirewallType.Blocked && !packet.Tunneled)
                        Core.SetFirewallType(FirewallType.NAT);

                    Routing.TryAdd(packet, ping.Source);
                }

                // send pong
                sentBytes = SendPacket(packet.Source, pong);
            }

            Core.ServiceBandwidth[Core.DhtServiceID].OutPerSec += sentBytes;
        }

        void Receive_Pong(G2ReceivedPacket packet)
        {
            Core.ServiceBandwidth[Core.DhtServiceID].InPerSec += packet.Root.Data.Length;

            Pong pong = Pong.Decode(packet);

            SetLocalIP(pong.RemoteIP, packet);

            bool lanIP = Utilities.IsLocalIP(packet.Source.IP);
            bool validSource = (!lanIP || LanMode && lanIP);


            // if received tcp
            if (packet.ReceivedTcp)
            {
                // if regular interval pong 
                if (pong.Source == null)
                {
                    // keep routing entry fresh so connect state remains
                    if (validSource && packet.Tcp.Proxy == ProxyType.Server)
                        Routing.Add(new DhtContact(packet.Tcp, packet.Tcp.RemoteIP), true);
                }

                // else connect pong with source info
                else
                {
                    // usually a proxied pong from somewhere else to keep our routing fresh
                    if (validSource && pong.Source.Firewall == FirewallType.Open)
                        Routing.Add(new DhtContact(pong.Source, packet.Source.IP), true);

                    // pong's direct flag ensures that tcp connection info (especially client ID) is not set with 
                    //   information from a pong routed through the remote host, but from the host we're directly connected to
                    if (pong.Direct)
                    {
                        packet.Tcp.UserID = pong.Source.UserID;
                        packet.Tcp.ClientID = pong.Source.ClientID;
                        packet.Tcp.TcpPort = pong.Source.TcpPort;
                        packet.Tcp.UdpPort = pong.Source.UdpPort;

                        // if firewalled
                        if (packet.Tcp.Outbound && packet.Tcp.Proxy == ProxyType.Unset)
                        {
                            if (Core.Firewall != FirewallType.Open && TcpControl.AcceptProxy(ProxyType.Server, pong.Source.UserID))
                            {
                                // send proxy request
                                ProxyReq request = new ProxyReq();
                                request.SenderID = Local.UserID;
                                request.Type = (Core.Firewall == FirewallType.Blocked) ? ProxyType.ClientBlocked : ProxyType.ClientNAT;
                                packet.Tcp.SendPacket(request);
                            }

                            // else ping/pong done, end connect
                            else
                                packet.Tcp.CleanClose("Not in need of a proxy");
                        }
                    }
                }
            }

            // pong received udp or tunneled
            else
            {
                if (validSource)
                {
                    if (Core.Firewall == FirewallType.Blocked && !packet.Tunneled)
                        Core.SetFirewallType(FirewallType.NAT);

                    // add to routing
                    // on startup, especially in sim everyone starts blocked so pong source firewall is not set right, but still needs to go into routing
                    Routing.TryAdd(packet, pong.Source, true);
                }

                // send bootstrap request for nodes if network not responsive
                // do tcp connect because if 2 nodes on network then one needs to find out they're open
                if (!Responsive)
                {
                    Searches.SendRequest(packet.Source, Local.UserID, 0, Core.DhtServiceID, 0, null);

                    if (!packet.Tunneled) // ip isnt set correctly on tunneled, and if tunneled then lookup active and host tested anyways
                        TcpControl.MakeOutbound(packet.Source, pong.Source.TcpPort, "pong bootstrap");
                }

                // forward to proxied nodes, so that their routing tables are up to date, so they can publish easily
                if (Core.Firewall == FirewallType.Open)
                {
                    pong.FromAddress = packet.Source;
                    pong.RemoteIP = null;
                    pong.Direct = false;

                    foreach (TcpConnect connection in TcpControl.ProxyClients)
                        connection.SendPacket(pong);
                }
            }
        }

        private void SetLocalIP(IPAddress localIP, G2ReceivedPacket packet)
        {
            if (localIP != null && !packet.Tunneled)
            {
                bool lanIP = Utilities.IsLocalIP(localIP); // re init to what they think our ip is

                if (!lanIP || LanMode && lanIP)
                    Core.LocalIP = localIP;

                if (!lanIP && LanMode)
                    SetLanMode(false);
            }
        }

        private void SetLanMode(bool mode)
        {
            if (LanMode == mode)
                return;

            LanMode = mode;

            // ip per core, one network may be local, while another maybe on the internets
            // also prevents one networks ip setting from influencing another

            // lost connection to internet and now back to lan mode
            if (LanMode)
            {
                Cache.BroadcastTimeout = 0; //broadcast ping
                Core.SetFirewallType(FirewallType.Blocked); //set firewall blocked - dont need to disconnect tcp, already disconnected
            }

            // found our external IP address - reset firewall, will quickly be set to nat/open after this function
            else
            {
                Core.SetFirewallType(FirewallType.Blocked);
            }
        }

        internal void Receive_ProxyRequest(G2ReceivedPacket packet)
        {
            ProxyReq request = ProxyReq.Decode(packet);

            ProxyAck ack = new ProxyAck();
            ack.Source = GetLocalSource();

            // check if there is space for type required
            if (Core.Firewall == FirewallType.Open && TcpControl.AcceptProxy(request.Type, ack.Source.UserID))
            {
                ack.Accept = true;
            }
            else if (packet.ReceivedTcp)
            {
                packet.Tcp.CleanClose("Couldn't accept proxy request");
                return;
            }



            // always send some contacts along so node can find closer proxy
            ack.ContactList = Routing.Find(request.SenderID, 8);


            // received request tcp
            if (packet.ReceivedUdp)
                UdpControl.SendTo(packet.Source, ack);

            // received request tcp
            else
            {
                packet.Tcp.Proxy = request.Type;
                packet.Tcp.SendPacket(ack);

                TcpControl.AddProxy(packet.Tcp);

                // check if a proxy needs to be disconnected now because overflow
                TcpControl.CheckProxies();
            }
        }

        internal void Receive_ProxyAck(G2ReceivedPacket packet)
        {
            ProxyAck ack = ProxyAck.Decode(packet);

            // update routing
            if (packet.ReceivedUdp && ack.Source.Firewall == FirewallType.Open)
                Routing.Add(new DhtContact(ack.Source, packet.Source.IP));

            foreach (DhtContact contact in ack.ContactList)
                Routing.Add(contact);


            // dont do proxy if we're not firewalled or remote host didnt accept
            if (Core.Firewall == FirewallType.Open || !ack.Accept)
            {
                if (packet.ReceivedTcp)
                    packet.Tcp.CleanClose("Proxy request rejected");

                return;
            }

            // received ack udp
            if (packet.ReceivedUdp)
            {
                if (!TcpControl.ProxyMap.ContainsKey(ack.Source.UserID))
                    TcpControl.MakeOutbound(packet.Source, ack.Source.TcpPort, "proxy ack recv");
            }

            // received ack tcp
            else
            {
                packet.Tcp.Proxy = ProxyType.Server;

                TcpControl.AddProxy(packet.Tcp);

                TcpControl.CheckProxies();

                // location and rudp connections updated after 20 seconds
            }
        }

        internal void Send_CrawlRequest(DhtAddress address, DhtClient target)
        {
            CrawlRequest request = new CrawlRequest();

            request.Target = target;

            SendPacket(address, request);
        }

        internal void Receive_CrawlRequest(G2ReceivedPacket packet)
        {
            CrawlRequest request = CrawlRequest.Decode(packet);


            if (Local.Equals(request.Target))
            {
                Send_CrawlAck(request, packet);
            }

            // Forward to appropriate node
            else
            {
                TcpConnect client = TcpControl.GetProxy(request.Target);

                if (client != null)
                {
                    request.FromAddress = packet.Source; // add so receiving host knows where to send response too

                    client.SendPacket(request);
                }
            }
        }

        internal void Send_CrawlAck(CrawlRequest req, G2ReceivedPacket packet)
        {
            CrawlAck ack = new CrawlAck();

            ack.Source = GetLocalSource();
            ack.Version = System.Windows.Forms.Application.ProductVersion;
            ack.Uptime = (Core.TimeNow - Core.StartTime).Seconds;


            foreach (TcpConnect connection in TcpControl.ProxyServers)
                ack.ProxyServers.Add(new DhtContact(connection, connection.RemoteIP));

            foreach (TcpConnect connection in TcpControl.ProxyClients)
                ack.ProxyClients.Add(new DhtContact(connection, connection.RemoteIP));


            if (packet.ReceivedTcp)
            {
                ack.ToAddress = packet.Source;
                packet.Tcp.SendPacket(ack);
            }
            else
                SendPacket(packet.Source, ack);
        }

        internal void Receive_CrawlAck(G2ReceivedPacket packet)
        {
            CrawlAck ack = CrawlAck.Decode(packet);

            if (GuiCrawler != null)
                GuiCrawler.BeginInvoke(GuiCrawler.CrawlAck, ack, packet);

        }

        internal Queue<Tuple<DateTime, string>> GeneralLog = new Queue<Tuple<DateTime, string>>();

        internal void UpdateLog(string type, string message)
        {
            if (Core.Sim != null && !Core.Sim.Internet.Logging)
                return;

            if (type == "general")
                lock (GeneralLog)
                {
                    GeneralLog.Enqueue(new Tuple<DateTime, string>(Core.TimeNow, Core.TimeNow.ToString("mm:ss:") + message));

                    while (GeneralLog.Count > 50)
                        GeneralLog.Dequeue();
                }

            lock (LogTable)
            {
                Queue<string> targetLog = null;

                if (LogTable.ContainsKey(type))
                    targetLog = LogTable[type];
                else
                    LogTable[type] = targetLog = new Queue<string>();

                targetLog.Enqueue(Core.TimeNow.ToString("HH:mm:ss:ff - ") + message);

                int logsize = (Core.Sim == null) ? 500 : 100;

                while (targetLog.Count > logsize)
                    targetLog.Dequeue();
            }
        }

        internal void LogPacket(PacketLogEntry logEntry)
        {
            if (Core.Sim != null && !Core.Sim.Internet.Logging)
                return;

            if (Core.PauseLog)
                return;

            lock (LoggedPackets)
            {
                LoggedPackets.Enqueue(logEntry);

                while (LoggedPackets.Count > 50)
                    LoggedPackets.Dequeue();
            }

            if (GuiPackets != null)
                GuiPackets.BeginInvoke(GuiPackets.UpdateLog, logEntry);
        }

        internal bool UseLookupProxies;

        internal void CheckLookupProxyMode()
        {
            Debug.Assert(!IsLookup);

            // if blocked/NATed connected to lookup but not the op, then we are in lookup proxy mode
            // lookup proxy mode removed once connection to op is established
            // lookup proxies are published with location data so that communication can be tunneled
            // hosts in lookup proxy mode are psuedo-open meaning they act similarly to open hosts in that 
            // they are added to the routing table and they conduct search/store like an open node

            OpCore lookup = Core.Context.Lookup;

            bool useProxies = (Core.TimeNow > Core.StartTime.AddSeconds(15) &&
                                // op not secret
                                Core.User.Settings.OpAccess != AccessType.Secret &&
                                // op core blocked
                                lookup != null && Core.Firewall != FirewallType.Open &&
                                // either we're connected to an open lookup, or we are open lookup (lookup port open, op port closed)
                                (lookup.Network.TcpControl.ProxyServers.Count > 0 || lookup.Firewall == FirewallType.Open) &&
                                // not connected to any op proxy servers
                                TcpControl.ProxyServers.Count == 0);


            // if no state change return
            if (useProxies == UseLookupProxies)
                return;

            UseLookupProxies = useProxies;

            if (UseLookupProxies)
            {
                // socket will handle publishing after 15 secs, location timer handles re-publishing
            }
            else
            {
                // lookup proxies should remove themselves from routing by timing out
            }
        }

        internal string GetLabel()
        {
            string label = "";

            if (IsLookup)
            {
                label += "Lookup ";

                Core.Context.Cores.LockReading(delegate()
                {
                    foreach (OpCore core in Core.Context.Cores)
                        label += core.User.Settings.UserName + ", ";
                });

                label = label.TrimEnd(',', ' ');
            }
            else
                label += Core.User.Settings.UserName;

            return label;
        }

        internal void ChangePorts(ushort tcp, ushort udp)
        {
            if (Core.InvokeRequired)
            {
                Core.RunInCoreAsync(delegate() { ChangePorts(tcp, udp); });
                return;
            }

            if (IsLookup)
            {
                LookupConfig.TcpPort = tcp;
                LookupConfig.UdpPort = udp;
            }
            else
            {
                Core.User.Settings.TcpPort = tcp;
                Core.User.Settings.UdpPort = udp;
            }

            // re-initialize sockets
            TcpControl.Shutdown();
            UdpControl.Shutdown();

            TcpControl.Initialize();
            UdpControl.Initialize();

            // save profile
            Core.User.Save();

            // save lookup config
            if (IsLookup)
                LookupConfig.Save(Core);
        }



        internal void SetAugmentedKey(ulong user)
        {
            // augmented key is original key with first 8 bytes xored with user id

            BitConverter.GetBytes(user).CopyTo(AugmentedCrypt.Key, 0);

            for(int i = 0; i < 8; i++)
                AugmentedCrypt.Key[i] ^= OpCrypt.Key[i];
        }
    }
}
