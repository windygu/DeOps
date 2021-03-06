using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using DeOps.Implementation.Protocol;
using DeOps.Implementation.Protocol.Net;
using DeOps.Implementation.Dht;


namespace DeOps.Implementation.Transport
{
	/// <summary>
	/// Summary description for TcpHandler.
	/// </summary>
	public class TcpHandler
	{
        // super-classes
		public OpCore Core; 
        public DhtNetwork Network;
		
		Socket ListenSocket;
		
		public ushort ListenPort;

		public List<TcpConnect> SocketList = new List<TcpConnect>();
        public List<TcpConnect> ProxyServers = new List<TcpConnect>();
        public List<TcpConnect> ProxyClients = new List<TcpConnect>();
        public Dictionary<ulong, TcpConnect> ProxyMap = new Dictionary<ulong, TcpConnect>();


		DateTime LastProxyCheck = new DateTime(0);

		int MaxProxyServers = 2;
		int MaxProxyNATs    = 12;
		int MaxProxyBlocked = 6;


        public TcpHandler(DhtNetwork network)
		{
            Network = network;
            Core = Network.Core;

            Initialize();
        }

        public void Initialize()
        {
            ListenPort = Network.IsLookup ? Network.Lookup.Ports.Tcp : Core.User.Settings.TcpPort;

            if (Core.Sim != null)
                return;

			ListenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

			bool bound    = false;
			int  attempts = 0;
			while( !bound && attempts < 5)
			{
				try
				{
					ListenSocket.Bind( new IPEndPoint( System.Net.IPAddress.Any, ListenPort) );
					bound = true;
					
					ListenSocket.Listen(10);
					ListenSocket.BeginAccept(new AsyncCallback(ListenSocket_Accept), ListenSocket);
					
					Network.UpdateLog("Core", "Listening for TCP on port " + ListenPort.ToString());
				}
				catch(Exception ex)
				{ 
					Network.UpdateLog("Exception", "TcpHandler::TcpHandler: " + ex.Message);

					attempts++; 
					ListenPort++;
				}
			}
		}

		public void Shutdown()
		{
            if (Core.Sim != null)
                return;

			try
			{
				Socket oldSocket = ListenSocket; // do this to prevent listen exception
				ListenSocket = null;
				
				if(oldSocket != null)
					oldSocket.Close();

				lock(SocketList)
					foreach(TcpConnect connection in SocketList)
						connection.CleanClose("Client shutting down");
			}
			catch(Exception ex)
			{
				Network.UpdateLog("Exception", "TcpHandler::Shudown: " + ex.Message);
			}
		}

		public void SecondTimer()
		{
			// if firewalled find closer proxy
			if(Core.Firewall != FirewallType.Open)
				if(NeedProxies(ProxyType.Server) || Core.TimeNow > LastProxyCheck.AddSeconds(30))
				{
					ConnectProxy();
                    LastProxyCheck = Core.TimeNow;
				}

			// Run through socket connections
			ArrayList deadSockets = new ArrayList();

			lock(SocketList)
				foreach(TcpConnect socket in SocketList)
				{
					socket.SecondTimer();
					
					// only let socket linger in connecting state for 10 secs
					if( socket.State == TcpState.Closed )
						deadSockets.Add(socket);
				}

            foreach (TcpConnect socket in deadSockets)
            {
                string message = "Connection to " + socket.ToString() + " Removed";
                if (socket.ByeMessage != null)
                    message += ", Reason: " + socket.ByeMessage;

                Network.UpdateLog("Tcp", message);

                // socket.TcpSocket = null; causing endrecv to fail on disconnect


                lock (SocketList)
                    SocketList.Remove(socket);

                if (ProxyServers.Contains(socket))
                    ProxyServers.Remove(socket);

                if (ProxyClients.Contains(socket))
                    ProxyClients.Remove(socket);

                if (ProxyMap.ContainsKey(socket.RoutingID))
                    ProxyMap.Remove(socket.RoutingID);

                ArrayList removeList = new ArrayList();

                // iterate through searches
                lock (Network.Searches.Active)
                    foreach (DhtSearch search in Network.Searches.Active)
                        // if proxytcp == connection
                        if (search.ProxyTcp != null && search.ProxyTcp == socket)
                        {
                            // if proxytcp == client blocked kill search
                            if (search.ProxyTcp.Proxy == ProxyType.ClientBlocked)
                                search.FinishSearch("Proxied client disconnected");

                            // else if proxy type is server add back to pending proxy list
                            if (search.ProxyTcp.Proxy == ProxyType.Server)
                            {
                                removeList.Add(search);
                                Network.Searches.Pending.Add(search);
                                search.Log("Back to Pending, TCP Disconnected");
                            }

                            search.ProxyTcp = null;
                        }

                lock (Network.Searches.Active)
                    foreach (DhtSearch search in removeList)
                        Network.Searches.Active.Remove(search);
            }
		}

		void ListenSocket_Accept(IAsyncResult asyncResult)
		{
			if(ListenSocket == null)
				return;

			try
			{
				Socket tempSocket = ListenSocket.EndAccept(asyncResult); // do first to catch

                OnAccept(tempSocket, (IPEndPoint) tempSocket.RemoteEndPoint);
			}
			catch(Exception ex)
			{
				Network.UpdateLog("Exception", "TcpHandler::ListenSocket_Accept:1: " + ex.Message);
			}

			// exception handling not combined because endreceive can fail legit, still need begin receive to run
			try
			{
				ListenSocket.BeginAccept(new AsyncCallback(ListenSocket_Accept), ListenSocket);
			}
			catch(Exception ex)
			{
				Network.UpdateLog("Exception", "TcpHandler::ListenSocket_Accept:2: " + ex.Message);
			}
		}

        public TcpConnect OnAccept(Socket socket, IPEndPoint source)
        {
            TcpConnect inbound = new TcpConnect(this);

            inbound.TcpSocket = socket;
            inbound.RemoteIP = source.Address;
            inbound.TcpPort = (ushort)source.Port;  // zero if internet, actual value if sim
            inbound.SetConnected();

            // it's not until the host sends us traffic that we can send traffic back because we don't know
            // connecting node's dhtID (and hence encryption key) until ping is sent

            lock (SocketList) 
                SocketList.Add(inbound);

            Network.UpdateLog("Tcp", "Accepted Connection from " + inbound.ToString());

            return inbound;
        }

		public void MakeOutbound( DhtAddress address, ushort tcpPort, string reason)
		{
			try
			{
                int connecting = 0;

                // check if already connected
                lock(SocketList)
                    foreach (TcpConnect socket in SocketList)
                    {
                        if (socket.State == TcpState.Connecting)
                            connecting++;

                        if (socket.State != TcpState.Closed && 
                            address.IP.Equals(socket.RemoteIP) && 
                            tcpPort == socket.TcpPort && 
                            socket.Outbound) // allows check firewall to work
                            return;
                    }

                if (connecting > 6)
                {
                    Debug.Assert(true);
                    return;
                }

                TcpConnect outbound = new TcpConnect(this, address, tcpPort);
				Network.UpdateLog("Tcp", "Attempting Connection to " + address.ToString() + ":" + tcpPort.ToString() + " (" + reason + ")");
				
                lock(SocketList)
                    SocketList.Add(outbound);
			}
			catch(Exception ex)
			{
				Network.UpdateLog("Exception", "TcpHandler::MakeOutbound: " + ex.Message);
			}
		}

		void ConnectProxy()
		{
			// Get cloest contacts and sort by distance to us
			DhtContact attempt = null;
			
			// no Dht contacts, use ip cache will be used to connect tcp/udp in DoBootstrap

			// find if any contacts in list are worth trying (will be skipped if set already)
            // get closest contact that is not already connected
            foreach (DhtContact contact in Network.Routing.NearXor.Contacts)
                // if havent tried in 10 minutes
                if (Core.TimeNow > contact.NextTryProxy && contact.TunnelClient == null)
                {
                    bool connected = false;

                    lock (SocketList)
                        foreach (TcpConnect socket in SocketList)
                            if (contact.UserID == socket.UserID && contact.ClientID == socket.ClientID)
                            {
                                connected = true;
                                break;
                            }

                    if(connected)
                        continue;

                    if (attempt == null || (contact.UserID ^ Network.Local.UserID) < (attempt.UserID ^ Network.Local.UserID))
                        attempt = contact;
                }
            

			if(attempt != null)
			{
				// take into account when making proxy request, disconnct furthest
				if(Core.Firewall == FirewallType.Blocked)
				{
					// continue attempted to test nat with pings which are small
                    Network.Send_Ping(attempt);
					MakeOutbound( attempt, attempt.TcpPort, "try proxy");
				}

				// if natted do udp proxy request first before connect
				else if(Core.Firewall == FirewallType.NAT)
				{
					ProxyReq request = new ProxyReq();
                    request.SenderID = Network.Local.UserID;
					request.Type     = ProxyType.ClientNAT;
					Network.UdpControl.SendTo( attempt, request);
				}

                attempt.NextTryProxy = Core.TimeNow.AddMinutes(10);
			}
		}

		public bool NeedProxies(ProxyType type)
		{
			int count = 0;

			lock(SocketList)
				foreach(TcpConnect connection in SocketList)
					if(connection.Proxy == type)
						count++;

			// count of proxy servers connected to, (we are firewalled)
			if(type == ProxyType.Server && count < MaxProxyServers)
				return true;

			// count of proxy clients connected to, (we are open)
			if(type == ProxyType.ClientBlocked && count < MaxProxyBlocked)
					return true;

			if(type == ProxyType.ClientNAT && count < MaxProxyNATs)
				return true;

		
			return false;
		}

		public bool ProxiesMaxed()
		{
			// we're not firewalled
			if( Core.Firewall == FirewallType.Open)
			{
				if( NeedProxies(ProxyType.ClientBlocked) )
					return false;

				if( NeedProxies(ProxyType.ClientNAT) )
					return false;
			}

			// we are firewalled
			else
			{
				if( NeedProxies(ProxyType.Server) )
					return false;
			}	

			return true;
		}

		public bool AcceptProxy(ProxyType type, UInt64 targetID)
		{
			if( NeedProxies(type) )
				return true;

			// else go through proxies, determine if targetid is closer than proxies already hosted
			lock(SocketList)
				foreach(TcpConnect connection in SocketList)
					if(connection.Proxy == type)
						// if closer than at least 1 contact
                        if ((targetID ^ Network.Local.UserID) < (connection.UserID ^ Network.Local.UserID) || targetID == connection.UserID)
						{
							return true;
						}

			return false;
		}

		public void CheckProxies()
		{
			CheckProxies(ProxyType.Server,        MaxProxyServers);
			CheckProxies(ProxyType.ClientNAT,     MaxProxyNATs);
			CheckProxies(ProxyType.ClientBlocked, MaxProxyBlocked);
		}

		void CheckProxies(ProxyType type, int max)
		{
            /*if (type == ProxyType.Server && Core.Sim.RealFirewall != FirewallType.Open)
            {
                int x = 0;
                // count number of connected servers, if too many break
                foreach (TcpConnect connection in SocketList)
                    if (connection.Proxy == type)
                        x++;
                if (x > 2)
                {
                    int y = 0;
                    y++;
                }
            }*/

			TcpConnect furthest = null;
			UInt64     distance = 0;
			int        count    = 0;

			lock(SocketList)
				foreach(TcpConnect connection in SocketList)
                    if (connection.State == TcpState.Connected && connection.Proxy == type)
                    {
                        count++;

                        if ((connection.UserID ^ Network.Local.UserID) > distance)
                        {
                            distance = connection.UserID ^ Network.Local.UserID;
                            furthest = connection;
                        }
                    }

			// greater than max, disconnect furthest
			if(count > max && furthest != null)
				furthest.CleanClose("Too many proxies, disconnecting furthest");
		}

		public void Receive_Bye(G2ReceivedPacket packet)
		{
			Bye bye = Bye.Decode(packet);

			foreach(DhtContact contact in bye.ContactList)
                Network.Routing.Add(contact);

			string message = (bye.Message != null) ? bye.Message : "";
			
			packet.Tcp.ByeMessage = "Remote: " + message;

			Network.UpdateLog("Tcp", "Bye Received from " + packet.Tcp.ToString() + " " + message);
			
			// close connection
			packet.Tcp.Disconnect();

            // reconnect
            if (bye.Reconnect && NeedProxies(ProxyType.Server))
                MakeOutbound(packet.Source, packet.Tcp.TcpPort, "Reconnecting");
		}

        public TcpConnect GetRandomProxy()
        {
            if (ProxyServers.Count == 0)
                return null;

            return ProxyServers[Core.RndGen.Next(ProxyServers.Count)];
        }

        public int SendRandomProxy(G2Packet packet)
        {
            TcpConnect socket = GetRandomProxy();

            return (socket != null) ? socket.SendPacket(packet) : 0;
        }

        public void AddProxy(TcpConnect socket)
        {
            if (ProxyMap.ContainsKey(socket.RoutingID))
                return;

            ProxyMap[socket.RoutingID] = socket;

            if (socket.Proxy == ProxyType.Server)
                ProxyServers.Add(socket);
            else
                ProxyClients.Add(socket);
        }

        public TcpConnect GetProxy(DhtClient client)
        {
            if (client == null)
                return null;

            return GetProxy(client.UserID, client.ClientID);
        }

        public TcpConnect GetProxy(ulong user, ushort client)
        {
            TcpConnect proxy;

            if(ProxyMap.TryGetValue(user ^ client, out proxy))
                if(proxy.State == TcpState.Connected && proxy.Proxy != ProxyType.Unset)
                    return proxy;

            return null;
        }

        public TcpConnect GetProxyServer(IPAddress ip)
        {
            if (ip == null)
                return null;

            foreach (TcpConnect server in ProxyServers)
                if (server.RemoteIP.Equals(ip))
                    return server;

            return null;
        }
    }
}
