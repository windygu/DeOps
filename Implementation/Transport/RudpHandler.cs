using System;
using System.Collections.Generic;
using System.Text;

using RiseOp.Services;
using RiseOp.Services.Location;
using RiseOp.Implementation.Dht;
using RiseOp.Implementation.Protocol.Net;

namespace RiseOp.Implementation.Transport
{
    internal delegate void SessionUpdateHandler(RudpSession session);
    internal delegate void SessionDataHandler(RudpSession session, byte[] data);
    internal delegate void KeepActiveHandler(Dictionary<ulong, bool> active);


    internal class RudpHandler
    {
        // maintain connections to those needed by components (timer)
        // keep up to date with location changes / additions (locationdata index)
        // notify components on connection changes (event)
        // remove connections when components no longer interested in node (timer)

        internal DhtNetwork Network;

        internal Dictionary<ulong, List<RudpSession>> SessionMap = new Dictionary<ulong, List<RudpSession>>();
        internal Dictionary<ushort, RudpSocket> SocketMap = new Dictionary<ushort, RudpSocket>();

        internal SessionUpdateHandler SessionUpdate;
        internal ServiceEvent<SessionDataHandler> SessionData = new ServiceEvent<SessionDataHandler>();
        internal KeepActiveHandler KeepActive;

        internal RudpHandler(DhtNetwork network)
        {
            Network = network;
        }

        internal void SecondTimer()
        {
            List<ulong> removeKeys = new List<ulong>();
            List<RudpSession> removeSessions = new List<RudpSession>();

            // remove dead sessions
            foreach (List<RudpSession> list in SessionMap.Values)
            {
                removeSessions.Clear();

                foreach (RudpSession session in list)
                    if (session.Status != SessionStatus.Closed)
                        session.SecondTimer();
                    else
                        removeSessions.Add(session);


                foreach (RudpSession session in removeSessions)
                    list.Remove(session);

                if (list.Count == 0)
                    removeKeys.Add(removeSessions[0].UserID);
            }

            foreach (ulong key in removeKeys)
                SessionMap.Remove(key);

            // every 10 secs check for sessions no longer in use
            if (Network.Core.TimeNow.Second % 10 != 0)
                return;

            Dictionary<ulong, bool> active = new Dictionary<ulong, bool>();

            if (KeepActive != null)
                foreach (KeepActiveHandler handler in KeepActive.GetInvocationList())
                    handler.Invoke(active);

            foreach(ulong key in SessionMap.Keys)
                if(!active.ContainsKey(key))
                    foreach (RudpSession session in SessionMap[key])
                        if(session.Status == SessionStatus.Active)
                            session.Send_Close("Not Active");

        }

        internal void Connect(LocationData location)
        {
            if (location.UserID == Network.LocalUserID && location.Source.ClientID == Network.ClientID)
                return;

            if (IsConnected(location))
                return;

            if (!SessionMap.ContainsKey(location.UserID))
                SessionMap[location.UserID] = new List<RudpSession>();

            RudpSession session = new RudpSession(this, location.UserID, location.Source.ClientID, false);
            SessionMap[location.UserID].Add(session);

            session.Comm.AddAddress(new RudpAddress(new DhtAddress(location.IP, location.Source)));

            foreach (DhtAddress address in location.Proxies)
                session.Comm.AddAddress(new RudpAddress(address));

            session.Connect(); 
        }

        internal RudpSession GetActiveSession(LocationData location)
        {
            return GetActiveSession(location.UserID, location.Source.ClientID);
        }

        internal RudpSession GetActiveSession(ulong key, ushort client)
        {
            foreach (RudpSession session in GetActiveSessions(key))
                if (session.ClientID == client)
                    return session;

            return null;
        }
        internal List<RudpSession> GetActiveSessions(ulong key)
        {
            List<RudpSession> sessions = new List<RudpSession>();

            if (SessionMap.ContainsKey(key))
                foreach (RudpSession session in SessionMap[key])
                    if (session.Status == SessionStatus.Active)
                        sessions.Add(session);

            return sessions;
        }

        internal bool IsConnected(LocationData location)
        {
            return ( GetActiveSession(location) != null );
        }

        internal bool IsConnected(ulong id)
        {
            return (GetActiveSessions(id).Count > 0);
        }

        internal void AnnounceProxy(TcpConnect tcp)
        {
            foreach (List<RudpSession> sessions in SessionMap.Values)
                foreach(RudpSession session in sessions)
                    if (session.Status == SessionStatus.Active)
                        session.Send_ProxyUpdate(tcp);
        }
    }

    internal class ActiveSessions
    {
        List<ulong> AllSessions = new List<ulong>();
        Dictionary<ulong, List<ushort>> Sessions = new Dictionary<ulong, List<ushort>>();

        internal ActiveSessions()
        {
        }

        internal void Add(RudpSession rudp)
        {
            Add(rudp.UserID, rudp.ClientID);
        }

        internal void Add(ulong key, ushort id)
        {
            if (!Sessions.ContainsKey(key))
                Sessions[key] = new List<ushort>();

            if (!Sessions[key].Contains(id))
                Sessions[key].Add(id);
        }

        internal void Add(ulong key)
        {
            if (AllSessions.Contains(key))
                return;

            AllSessions.Add(key);
        }

        internal bool Contains(RudpSession rudp)
        {
            if (AllSessions.Contains(rudp.UserID))
                return true;

            if (!Sessions.ContainsKey(rudp.UserID))
                return false;

            if (Sessions[rudp.UserID].Contains(rudp.ClientID))
                return true;

            return false;
        }
    }
}
