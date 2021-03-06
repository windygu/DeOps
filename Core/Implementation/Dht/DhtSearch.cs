using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

using DeOps.Services;
using DeOps.Implementation.Transport;
using DeOps.Implementation.Protocol.Net;

namespace DeOps.Implementation.Dht
{
	/// <summary>
	/// Summary description for DhtSearch.
	/// </summary>
	public class DhtSearch
	{

        const int LOOKUP_SIZE = 8;
        const int SEARCH_ALPHA = 3;

        // super-classes
        OpCore Core;
		public DhtNetwork Network;
        DhtSearchControl Searches;
        
		public UInt64    TargetID;
		public uint      SearchID;
        public string    Name;
        public uint      Service;
        public uint      DataType;
        public Action<DhtSearch>  DoneEvent;
        public int       TargetResults = 10;
        
        public List<DhtLookup> LookupList = new List<DhtLookup>();

		public bool   Finished;
		public string FinishReason;
        
        public bool   FoundProxy;
		public DhtContact FoundContact;
        public List<byte[]> FoundValues = new List<byte[]>();
        public Action<DhtSearch, DhtAddress, byte[]> FoundEvent;

		public TcpConnect ProxyTcp;
        public byte[] Parameters;
        public object Carry;


        public DhtSearch(DhtSearchControl control, UInt64 targetID, string name, uint service, uint datatype)
		{
            Core      = control.Core;
            Network   = control.Network ;
            Searches  = control;
			TargetID  = targetID;
			Name      = name;
            Service   = service;
            DataType  = datatype;
 
            SearchID = (uint) Core.RndGen.Next(1, int.MaxValue);
		}

		public bool Activate()
		{
			// bootstrap search from routing
			foreach(DhtContact contact in Network.Routing.Find(TargetID, 8))
				Add(contact);

            List<TcpConnect> sockets = null;

            // if open send search to proxied nodes just for good measure, probably helps on very small networks
            if (Core.Firewall == FirewallType.Open)
                sockets = Network.TcpControl.ProxyClients;

            // if natted send request to proxies for fresh nodes
            if(Core.Firewall == FirewallType.NAT)
                sockets = Network.TcpControl.ProxyServers;

            if(sockets != null)
                foreach (TcpConnect socket in sockets)
                {
                    DhtContact contact = new DhtContact(socket, socket.RemoteIP);
                    Network.Searches.SendRequest(contact, TargetID, SearchID, Service, DataType, Parameters);

                    DhtLookup host = Add(socket.GetContact());
                    if (host != null)
                        host.Status = LookupStatus.Searching;
                }					

			// if blocked send proxy search request to 1 proxy, record and wait
            if (Core.Firewall == FirewallType.Blocked && !Network.UseLookupProxies)
			{
				// pick random proxy server
                if (Network.TcpControl.ProxyServers.Count == 0)
					return false;

                ProxyTcp = Network.TcpControl.ProxyServers[Core.RndGen.Next(Network.TcpControl.ProxyServers.Count)];

                Send_ProxySearchRequest();
			}

			return true;
		}

        public void Send_ProxySearchRequest()
        {
            SearchReq request = new SearchReq();

            request.Source    = Network.GetLocalSource();
            request.SearchID  = SearchID;
            request.TargetID  = TargetID;
            request.Service   = Service;
            request.DataType  = DataType;
            request.Parameters = Parameters;

            ProxyTcp.SendPacket(request);
        }


        public DhtLookup Add(DhtContact contact)
		{
            DhtLookup added = null;

            if (contact.UserID == Network.Local.UserID && contact.ClientID == Network.Local.ClientID)
                return null;

			if(Finished) // search over
                return null;

			// go through lookup list, add if closer to target
			foreach(DhtLookup lookup in LookupList)
			{	
				if(contact.UserID == lookup.Contact.UserID && contact.ClientID == lookup.Contact.ClientID)
					return lookup;

				if((contact.UserID ^ TargetID) < (lookup.Contact.UserID ^ TargetID))
				{
                    added = new DhtLookup(contact);
                    LookupList.Insert(LookupList.IndexOf(lookup), added);
					break;
				}
			}

            if (added == null)
            {
                added = new DhtLookup(contact);
                LookupList.Add(added);
            }

            while (LookupList.Count > LOOKUP_SIZE)
				LookupList.RemoveAt(LookupList.Count - 1);
		
	
			// at end so we ensure this node is put into list and sent with proxy results
            if (Service == Core.DhtServiceID && contact.UserID == TargetID)
				Found(contact, false);

            return added;
		}

		public void SecondTimer()
		{
			if(Finished) // search over
				return;

			if(ProxyTcp != null && ProxyTcp.Proxy == ProxyType.Server) // search being handled by proxy server
				return;

            // get searching count
            int searching = 0;
            foreach (DhtLookup lookup in LookupList)
                if (lookup.Status == LookupStatus.Searching)
                    searching++;

            // iterate through lookup nodes
            bool alldone = true;

            foreach (DhtLookup lookup in LookupList)
            {
                if (lookup.Status == LookupStatus.Done)
                    continue;

                alldone = false;

                // if searching
                if (lookup.Status == LookupStatus.Searching)
                {
                    lookup.Age++;

                    // research after 3 seconds
                    if (lookup.Age == 3)
                    {
                        //Log("Sending Request to " + lookup.Contact.Address.ToString() + " (" + Utilities.IDtoBin(lookup.Contact.DhtID) + ")");
                        Network.Searches.SendRequest(lookup.Contact, TargetID, SearchID, Service, DataType, Parameters);
                    }

                    // drop after 6
                    if (lookup.Age >= 6)
                        lookup.Status = LookupStatus.Done;
                }

                // start search if room available
                if (lookup.Status == LookupStatus.None && searching < SEARCH_ALPHA)
                {
                    //Log("Sending Request to " + lookup.Contact.Address.ToString() + " (" + Utilities.IDtoBin(lookup.Contact.DhtID) + ")");
                    Network.Searches.SendRequest(lookup.Contact, TargetID, SearchID, Service, DataType, Parameters);

                    lookup.Status = LookupStatus.Searching;
                }
            }


			// set search over if nothing more
            if (alldone)
                FinishSearch(LookupList.Count.ToString() + " Search Points Exhausted");
		}


		public void FinishSearch(string reason)
		{
			Finished     = true;
			FinishReason = reason;

            if (ProxyTcp != null)
            {
                if (ProxyTcp.Proxy == ProxyType.ClientBlocked)
                {
                    SearchAck ack = new SearchAck();
                    ack.Source = Network.GetLocalSource();
                    ack.SearchID = SearchID;

                    foreach (DhtLookup lookup in LookupList)
                        ack.ContactList.Add(lookup.Contact);
                }

                SearchReq req = new SearchReq();
                req.SearchID = SearchID;
                req.EndProxySearch = true;

                ProxyTcp.SendPacket(req);
            }

            if(DoneEvent != null)
                DoneEvent.Invoke(this);
		}

		public void Found(DhtContact contact, bool proxied)
		{
			FoundContact = contact;

			if( !proxied )
				FinishSearch("Found");

			else
			{
				FoundProxy = true;
                FinishSearch("Found Proxy");
			}
		}

        public void Found(byte[] value, DhtAddress source)
        {
            if(FoundEvent != null)
                FoundEvent.Invoke(this, source, value);

            foreach (byte[] found in FoundValues)
                if(Utilities.MemCompare(found, value))
                    return;

            FoundValues.Add(value);
        }

		public void Log(string message)
		{
            int id = (int)SearchID % 1000;
            string entry = id.ToString() + ":" + Utilities.IDtoBin(TargetID);

            if(ProxyTcp != null)
                entry += "(proxied)";

            entry += ": " + message;

            Network.UpdateLog("Search - " + Name, entry); 
		}

    }

	public enum LookupStatus {None, Searching, Done};

	public class DhtLookup
	{
		public LookupStatus Status;
		public DhtContact Contact;
		public int Age;

		public DhtLookup(DhtContact contact)
		{
			Contact = contact;
			Status  = LookupStatus.None;
		}
	}
}