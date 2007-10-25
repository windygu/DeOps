using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

using DeOps.Implementation;
using DeOps.Implementation.Protocol;
using DeOps.Implementation.Transport;
using DeOps.Components.Link;
using DeOps.Components.Location;

namespace DeOps.Components.Chat
{
    internal delegate void CreateRoomHandler(ChatRoom room);
    internal delegate void RemoveRoomHandler(ChatRoom room);

    internal class ChatControl : OpComponent
    {
        internal OpCore Core;
        internal LinkControl Links;

        internal List<ChatRoom> Rooms = new List<ChatRoom>();
        Dictionary<ulong, List<ushort>> ConnectedClients = new Dictionary<ulong, List<ushort>>();

        internal CreateRoomHandler  CreateRoomEvent;
        internal RemoveRoomHandler  RemoveRoomEvent;
 

        internal ChatControl(OpCore core)
        {
            Core = core;
            Links = core.Links;

            Core.RudpControl.SessionUpdate += new SessionUpdateHandler(Session_Update);
            Core.RudpControl.SessionData[ComponentID.Chat] = new SessionDataHandler(Session_Data);

            Core.LoadEvent += new LoadHandler(Core_Load);
            Core.ExitEvent += new ExitHandler(Core_Exit);
        }

        void Core_Load()
        {
            Links.LinkUpdate += new LinkUpdateHandler(Link_Update);
            Core.Locations.LocationUpdate += new LocationUpdateHandler(Location_Update);

            Link_Update(Links.LocalLink);
        }

        void Core_Exit()
        {
            if (CreateRoomEvent != null || RemoveRoomEvent != null)
                throw new Exception("Chat Events not fin'd");
        }

        internal override List<MenuItemInfo> GetMenuInfo(InterfaceMenuType menuType, ulong key, uint proj)
        {
            if (key != Core.LocalDhtID)
                return null;

            List<MenuItemInfo> menus = new List<MenuItemInfo>();

            if(menuType == InterfaceMenuType.Internal)
                menus.Add(new MenuItemInfo("Comm/Chat", ChatRes.Icon, new EventHandler(Menu_View)));

            if(menuType == InterfaceMenuType.External)
                menus.Add(new MenuItemInfo("Chat", ChatRes.Icon, new EventHandler(Menu_View)));


            return menus;
        }

        void Menu_View(object sender, EventArgs args)
        {
            IViewParams node = sender as IViewParams;

            if (node == null)
                return;

            if (node.GetKey() != Core.LocalDhtID)
                return;

            // gui creates viewshell, component just passes view object
            ChatView view = new ChatView(this, node.GetProject());

            Core.InvokeView(node.IsExternal(), view);
        }

        internal void Link_Update(OpLink link)
        {
            foreach (uint project in Links.ProjectRoots.Keys)
            {
                OpLink uplink = link.GetHigher(project, true);
                List<OpLink> downlinks = link.GetLowers(project, true);

                // if us
                if (link == Links.LocalLink)
                {
                    // if uplink exists, refresh high room, else remove it
                    if (uplink != null)
                        RefreshRoom(RoomKind.Command_High, project);
                    else
                        RemoveRoom(RoomKind.Command_High, project);

                    // if downlinks exist
                    if (downlinks.Count > 0)
                        RefreshRoom(RoomKind.Command_Low, project);
                    else
                        RemoveRoom(RoomKind.Command_Low, project);
                }

                // if not us
                else
                {
                    // remove link from whatever room
                    ChatRoom currentRoom = FindRoom(link.DhtID, project);
                    
                    if(currentRoom != null)
                        RefreshRoom(currentRoom.Kind, project);

                    // find where node belongs
                    if (uplink != null && (link == uplink || Links.IsLower(uplink.DhtID, link.DhtID, project)))
                        RefreshRoom(RoomKind.Command_High, project);

                    else if (Links.IsLowerDirect(link.DhtID, project))
                        RefreshRoom(RoomKind.Command_Low, project);
                }
            }
        }

        private void RefreshRoom(RoomKind kind, uint project)
        {
            OpLink highNode = null;

            if (kind == RoomKind.Command_High)
            {
                highNode = Links.LocalLink.GetHigher(project, true) ;

                if(highNode == null)
                {
                    RemoveRoom(kind, project);
                    return;
                }
            }

            if (kind == RoomKind.Command_Low)
                highNode = Links.LocalLink;

            // ensure top room exists
            ChatRoom room = FindRoom(kind, project);
            bool newRoom = false;

            if (room == null)
            {
                string name = "error";
                if (project == 0)
                    name = Core.User.Settings.Operation;
                else if (Links.ProjectNames.ContainsKey(project))
                    name = Links.ProjectNames[project];

                room = new ChatRoom(kind, project, name);
                Rooms.Add(room);

                newRoom = true;
            }

            // remove members
            room.Members.Clear();

            // add members
            room.Members.Add(highNode);

            foreach (OpLink downlink in highNode.GetLowers(project, true))
                room.Members.Add(downlink);

            if(room.Members.Count == 1)
            {
                RemoveRoom(kind, project);
                return;
            }

            if(newRoom)
                Core.InvokeInterface(CreateRoomEvent, room);

            Core.InvokeInterface(room.MembersUpdate, true);
        }
        
        private void RemoveRoom(RoomKind kind, uint id)
        {
            ChatRoom room = FindRoom(kind, id);

            if (room == null)
                return;

            Rooms.Remove(room);

            Core.InvokeInterface(RemoveRoomEvent, room);
        }

        private ChatRoom FindRoom(RoomKind kind, uint project)
        {
            foreach (ChatRoom room in Rooms)
                if (kind == room.Kind && project == room.ProjectID)
                    return room;

            return null;
        }

        private List<ChatRoom> FindRoom(ulong key)
        {
            List<ChatRoom> results = new List<ChatRoom>();

            foreach (ChatRoom room in Rooms)
                foreach (OpLink member in room.Members)
                    if (member.DhtID == key)
                    {
                        results.Add(room);
                        break;
                    }

            return results;
        }

        private ChatRoom FindRoom(ulong key, uint project)
        {
            foreach (ChatRoom room in Rooms)
                if (room.ProjectID == project)
                    foreach (OpLink member in room.Members)
                        if (member.DhtID == key)
                            return room;

            return null;
        }

        internal void Location_Update(LocationData location)
        {
            // return if node not part of any rooms
            List<ChatRoom> rooms = FindRoom(location.KeyID);

            if (rooms.Count > 0)
                Core.RudpControl.Connect(location); // func checks if already connected
        }

        internal void Session_Update(RudpSession session)
        {
            ulong key = session.DhtID;

            // if node a member of a room
            List<ChatRoom> rooms = FindRoom(key);

            if (rooms.Count == 0)
                return;

            // getstatus message
            string name = "unknown ";
            if (Links.LinkMap.ContainsKey(key) && Links.LinkMap[key].Name != null)
                name = Links.LinkMap[key].Name + " ";

            string location = "";
            if (Core.Locations.ClientCount(session.DhtID ) > 1)
                location = " @" + Core.Locations.GetLocationName(session.DhtID, session.ClientID);


            string message = null;

            if (session.Status == SessionStatus.Active &&
                (!ConnectedClients.ContainsKey(session.DhtID) || !ConnectedClients[session.DhtID].Contains(session.ClientID)))
            {
                message = "Connected to " + name + location;

                if (!ConnectedClients.ContainsKey(session.DhtID))
                    ConnectedClients[session.DhtID] = new List<ushort>();
                    
                ConnectedClients[session.DhtID].Add(session.ClientID);
            }

            if (session.Status == SessionStatus.Closed &&
                ConnectedClients.ContainsKey(session.DhtID) && ConnectedClients[session.DhtID].Contains(session.ClientID))
            {
                message = "Disconnected from " + name + location;

                ConnectedClients[session.DhtID].Remove(session.ClientID);

                if (ConnectedClients[session.DhtID].Count == 0)
                    ConnectedClients.Remove(session.DhtID);
            }

            // update interface
            if(message != null)
                foreach (ChatRoom room in rooms)
                {
                    ProcessMessage(room, new ChatMessage(Core, message, true));

                    Core.InvokeInterface(room.MembersUpdate, false );
                }
        }

        internal void SendMessage(ChatRoom Room, string text)
        {
            ProcessMessage(Room, new ChatMessage(Core, text, false));

            bool sent = false;

            ChatData packet = new ChatData(ChatPacketType.Message);
            packet.ChatID = Room.ProjectID;
            packet.Text   = text;
            packet.Custom = (Room.Kind == RoomKind.Custom);

            lock(Core.RudpControl.SessionMap)
                foreach (OpLink link in Room.Members)             
                    if (Core.RudpControl.SessionMap.ContainsKey(link.DhtID))
                        foreach (RudpSession session in Core.RudpControl.SessionMap[link.DhtID])
                            if (session.Status == SessionStatus.Active)
                            {
                                sent = true;
                                session.SendData(ComponentID.Chat, packet, true);
                            }

            if(!sent)
                ProcessMessage(Room, new ChatMessage(Core, "Message not sent (no one connected to room)", true));
        }

        void Session_Data(RudpSession session, byte[] data)
        {
            G2Header root = new G2Header(data);

            if (Core.Protocol.ReadPacket(root))
            {
                switch (root.Name)
                {
                    case ChatPacket.Data:

                        ChatData packet = ChatData.Decode(Core.Protocol, root);

                        if (packet.Type != ChatPacketType.Message || packet.Custom)
                            return;

                        ChatRoom room = FindRoom(session.DhtID, packet.ChatID);

                        if (room == null)
                            return;
                        
                        ProcessMessage(room, new ChatMessage(Core, session, packet.Text));
        
                        break;
                }
            }
        }

        private void ProcessMessage(ChatRoom Room, ChatMessage message)
        {
            Room.Log.Add(message);

            // ask user here if invite to room

            Core.InvokeInterface(Room.ChatUpdate, message );
        }

        internal override void GetActiveSessions(ref ActiveSessions active)
        {
            foreach (ChatRoom room in Rooms)
                foreach (OpLink member in room.Members)
                    active.Add(member.DhtID);
        }
    }


    internal enum RoomKind { None, Command_High, Command_Low, Custom };

    internal delegate void MembersUpdateHandler(bool refresh);
    internal delegate void ChatUpdateHandler(ChatMessage message);

    internal class ChatRoom
    {
        internal uint     ProjectID;
        internal string   Name;
        internal RoomKind Kind;

        internal List<ChatMessage> Log = new List<ChatMessage>();

        internal List<OpLink> Members = new List<OpLink>();

        internal MembersUpdateHandler MembersUpdate;
        internal ChatUpdateHandler    ChatUpdate;


        internal ChatRoom(RoomKind kind, uint project, string name)
        {
            Kind = kind;
            ProjectID   = project;
            Name = name;
        }
    }

    internal class ChatMessage
    {
        internal bool       System;
        internal ulong      Source;
        internal ushort     ClientID;
        internal DateTime   TimeStamp;
        internal string     Text;


        internal ChatMessage(OpCore core, string text, bool system)
        {
            Source = core.LocalDhtID;
            ClientID = core.ClientID;
            TimeStamp = core.TimeNow;
            Text = text;
            System = system;
        }

        internal ChatMessage(OpCore core, RudpSession session, string text)
        {
            Source = session.DhtID;
            ClientID = session.ClientID;
            TimeStamp = core.TimeNow;
            Text = text;
        }
    }
}