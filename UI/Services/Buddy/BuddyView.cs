﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

using DeOps.Implementation;

using DeOps.Interface;
using DeOps.Interface.Views;
using DeOps.Interface.TLVex;

using DeOps.Services.IM;
using DeOps.Services.Location;
using DeOps.Services.Trust;


namespace DeOps.Services.Buddy
{
    public class BuddyView : ContainerListViewEx
    {
        CoreUI UI;
        OpCore Core;
        BuddyService Buddies;
        LocationService Locations;

        public Font OnlineFont = new Font("Tahoma", 8.25F);
        public Font LabelFont = new Font("Tahoma", 8.25F, System.Drawing.FontStyle.Bold);
        public Font OfflineFont = new Font("Tahoma", 8.25F, System.Drawing.FontStyle.Italic);

        bool Dragging;
        Point DragStart = Point.Empty;
        string[] DragBuddies = null;

        bool Interactive;
        public bool FirstLineBlank = true;
        StatusPanel SelectionInfo;


        public BuddyView()
        {
            DisableHScroll = true;
            HeaderStyle = System.Windows.Forms.ColumnHeaderStyle.None;
            AllowDrop = true;
            MultiSelect = true;
        }

        public void Init(CoreUI ui, BuddyService buddies, StatusPanel status, bool interactive)
        {
            UI = ui;
            Buddies = buddies;
            Core = buddies.Core;
            Locations = Core.Locations;

            Interactive = interactive;
            SelectionInfo = status;

            Buddies.GuiUpdate += new BuddyGuiUpdateHandler(Buddy_Update);
            Locations.GuiUpdate += new LocationGuiUpdateHandler(Location_Update);

            MouseClick += new MouseEventHandler(BuddyList_MouseClick);
            MouseDoubleClick += new MouseEventHandler(BuddyList_MouseDoubleClick);

            MouseDown += new MouseEventHandler(BuddyView_MouseDown);
            MouseMove += new MouseEventHandler(BuddyView_MouseMove);
            MouseUp += new MouseEventHandler(BuddyView_MouseUp);
            DragOver += new DragEventHandler(BuddyView_DragOver);
            DragDrop += new DragEventHandler(BuddyView_DragDrop);

            Columns.Add("", 100, System.Windows.Forms.HorizontalAlignment.Left, ColumnScaleStyle.Spring);

            SmallImageList = new List<Image>(); // itit here, cause main can re-init
            SmallImageList.Add(new Bitmap(16, 16));
            SmallImageList.Add(BuddyRes.away);
            SmallImageList.Add(BuddyRes.blocked);

            SelectedIndexChanged += new EventHandler(BuddyView_SelectedIndexChanged);

            RefreshView();
        }

        void BuddyView_MouseDown(object sender, MouseEventArgs e)
        {
            if (!Interactive)
                return;

            Dragging = false;
            DragStart = Point.Empty;

            BuddyItem clicked = GetItemAt(e.Location) as BuddyItem;

            if (DragStart == Point.Empty && clicked != null && clicked.User != 0)
            {
                DragBuddies = (from b in SelectedItems.Cast<BuddyItem>()
                               where b.User != 0
                               select Core.GetIdentity(b.User)).ToArray();

                DragStart = e.Location;
            }
        }

        void BuddyView_MouseMove(object sender, MouseEventArgs e)
        {
            if (DragStart != Point.Empty && !Dragging && GuiUtils.GetDistance(DragStart, e.Location) > 4)
            {
                Dragging = true;

                DataObject data = new DataObject(DataFormats.Text, DragBuddies);
                DoDragDrop(data, DragDropEffects.Copy);
            }
        }

        void BuddyView_MouseUp(object sender, MouseEventArgs e)
        {
            Dragging = false;
            DragStart = Point.Empty; 
        }

        void BuddyView_DragOver(object sender, DragEventArgs e)
        {
            e.Effect = DragDropEffects.None;

            if (!e.Data.GetDataPresent(DataFormats.Text))
                return;

            BuddyItem overItem = GetItemAt(PointToClient(new Point(e.X, e.Y))) as BuddyItem;

            // must be dragging over a group label
            if (overItem == null || overItem.User != 0 || overItem.Text == "")
                return;
            
            e.Effect = DragDropEffects.All;
        }

        void BuddyView_DragDrop(object sender, DragEventArgs e)
        {
            Dragging = false;

            // Handle only FileDrop data.
            if (!e.Data.GetDataPresent(DataFormats.Text))
                return;

            // get destination of drop
            BuddyItem overItem = GetItemAt(PointToClient(new Point(e.X, e.Y))) as BuddyItem;

            // must be dragging over a group label
            if (overItem == null || overItem.User != 0 || overItem.Text == "")
                return;

            string groupname = overItem.GroupLabel ? overItem.Text : null;

            try
            {
                string[] links = (string[])e.Data.GetData(DataFormats.Text);

                foreach (string link in links)
                {
                    OpBuddy buddy = Buddies.AddBuddy(link);

                    if (buddy != null)
                        Buddies.AddtoGroup(buddy.ID, groupname);
                }

                RefreshView();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }
        

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Buddies.GuiUpdate -= new BuddyGuiUpdateHandler(Buddy_Update);
                Locations.GuiUpdate -= new LocationGuiUpdateHandler(Location_Update);

                MouseClick -= new MouseEventHandler(BuddyList_MouseClick);
            }

            base.Dispose(disposing);
        }


        void Buddy_Update()
        {
            RefreshView();
        }

        void Location_Update(ulong user)
        {
            if (Buddies.BuddyList.SafeContainsKey(user))
                RefreshView();           
        }

        private void RefreshView()
        {  
            List<BuddyItem> Online = new List<BuddyItem>();
            List<BuddyItem> Offline = new List<BuddyItem>();
            Dictionary<string, List<BuddyItem>> Groups = new Dictionary<string, List<BuddyItem>>();


            Buddies.BuddyList.LockReading(delegate()
            {
                foreach (OpBuddy buddy in Buddies.BuddyList.Values)
               {
                    string name = Core.GetName(buddy.ID);

                    BuddyItem item = new BuddyItem(name, buddy.ID);


                    bool online = false;
                    bool away = false;

                    foreach (ClientInfo info in Core.Locations.GetClients(buddy.ID))
                    {
                        online = true;

                        if (info.Data.Away)
                            away = true;
                    }

                    // set color / icon
                    if(!online || (item.User == Core.UserID && Core.User.Settings.Invisible))
                    {
                        item.Font = OfflineFont;
                        item.ForeColor = Color.Gray;
                    }
                    else
                        item.Font = OnlineFont;


                    if (Buddies.IgnoreList.SafeContainsKey(item.User))
                        item.ImageIndex = 2;
                    else if (away)
                        item.ImageIndex = 1;


                    // put in group
                    if (buddy.Group != null)
                    {
                        if (!Groups.ContainsKey(buddy.Group))
                            Groups[buddy.Group] = new List<BuddyItem>();

                        Groups[buddy.Group].Add(item);
                    }
                    else if (online)
                        Online.Add(item);
                    else
                        Offline.Add(item);
                }
            });

            BeginUpdate();

            // save selected
            List<ulong> selected = (from i in SelectedItems.Cast<BuddyItem>()
                                    where i.User != 0
                                    select i.User).ToList();

            Items.Clear();

            if(FirstLineBlank)
                Items.Add(new BuddyItem());
                
            AddSection("Buddies", Online, false);

            foreach (string title in Groups.Keys)
                AddSection(title, Groups[title], true);

            AddSection("Offline", Offline, false);

            // re-select
            foreach (BuddyItem item in SelectedItems.Cast<BuddyItem>().Where(i => selected.Contains(i.User)))
                item.Selected = true;

            EndUpdate();

            Update(); // invalidate takes too long?
        }

        private void AddSection(string title, List<BuddyItem> people, bool group)
        {
            if (people.Count == 0)
                return;

            Items.Add(new BuddyItem(title, LabelFont, group));

            foreach (BuddyItem item in people.OrderBy(p => p.Text))
                Items.Add(item);

            Items.Add(new BuddyItem());
        }


        void BuddyList_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            if (!Interactive)
                return;

            // this gets right click to select item
            BuddyItem clicked = GetItemAt(e.Location) as BuddyItem;

            if (clicked == null || clicked.User == 0)
                return;


            if (Core.Locations.ActiveClientCount(clicked.User) > 0)
            {
                if(SelectionInfo.IM != null)
                    SelectionInfo.IM.OpenIMWindow(clicked.User);
            }
        }

        private void BuddyList_MouseClick(object sender, MouseEventArgs e)
        {
            if (!Interactive)
                return;

            // this gets right click to select item
            BuddyItem clicked = GetItemAt(e.Location) as BuddyItem;


            if (clicked != null && clicked.User != 0)
                Core.Locations.Research(clicked.User);


            // right click menu
            if (e.Button != MouseButtons.Right)
                return;

            // menu
            ContextMenuStripEx treeMenu = new ContextMenuStripEx();

            if (clicked == null || clicked.User == 0)
            {
                if (clicked == null || !clicked.GroupLabel) // blank space clicked, or a buddy/offline label
                    treeMenu.Items.Add(new ToolStripMenuItem("Add Buddy", BuddyRes.buddy_add, Menu_AddBuddy));
                else
                    treeMenu.Items.Add(new ToolStripMenuItem("Remove Group", BuddyRes.group_remove, Menu_RemoveGroup));

                treeMenu.Show(this, e.Location);
                return;
            }

            uint project = 0;

            // views
            List<MenuItemInfo> quickMenus = new List<MenuItemInfo>();
            List<MenuItemInfo> extMenus = new List<MenuItemInfo>();

            foreach (var service in UI.Services.Values)
            {
                if (service is TrustService || service is BuddyService)
                    continue;

                service.GetMenuInfo(InterfaceMenuType.Quick, quickMenus, clicked.User, project);

                service.GetMenuInfo(InterfaceMenuType.External, extMenus, clicked.User, project);
            }

            foreach (MenuItemInfo info in quickMenus)
                treeMenu.Items.Add(new OpMenuItem(clicked.User, project, info.Path, info));


            if (extMenus.Count > 0)
            {
                ToolStripMenuItem viewItem = new ToolStripMenuItem("Views", InterfaceRes.views);

                foreach (MenuItemInfo info in extMenus)
                    viewItem.DropDownItems.SortedAdd(new OpMenuItem(clicked.User, project, info.Path, info));

                //crit add project specific views

                treeMenu.Items.Add(viewItem);
            }

            if (treeMenu.Items.Count > 0)
                treeMenu.Items.Add("-");

            treeMenu.Items.Add(new ToolStripMenuItem("Add to Group", BuddyRes.group_add, Menu_AddGroup));

            if (clicked.User != Core.UserID) // if not self
                treeMenu.Items.Add(new ToolStripMenuItem("Remove Buddy", BuddyRes.buddy_remove, Menu_RemoveBuddy));

            // add trust options at bottom
            quickMenus.Clear();

            UI.Services[ServiceIDs.Buddy].GetMenuInfo(InterfaceMenuType.Quick, quickMenus, clicked.User, project);

            if (UI.Services.ContainsKey(ServiceIDs.Trust))
                UI.Services[ServiceIDs.Trust].GetMenuInfo(InterfaceMenuType.Quick, quickMenus, clicked.User, project);

            foreach (MenuItemInfo info in quickMenus)
                treeMenu.Items.Add(new OpMenuItem(clicked.User, project, info.Path, info));

            // show
            if (treeMenu.Items.Count > 0)
                treeMenu.Show(this, e.Location);
        }


        void Menu_AddGroup(object sender, EventArgs e)
        {
            GetTextDialog add = new GetTextDialog("Add to Group", "Enter the name of the group to add to", "");
            
            if (add.ShowDialog() == DialogResult.OK && add.ResultBox.Text != "" )
            { 
                foreach (BuddyItem item in SelectedItems)
                    if (item.User != 0)
                        Buddies.AddtoGroup(item.User, add.ResultBox.Text);
            }
        }

        void Menu_RemoveGroup(object sender, EventArgs e)
        {
            foreach (BuddyItem item in SelectedItems)
                Buddies.RemoveGroup(item.Text);
        }


        void Menu_RemoveBuddy(object sender, EventArgs e)
        {
            foreach (BuddyItem item in SelectedItems)
                if (item.User != 0)
                    Core.Buddies.RemoveBuddy(item.User);
        }

        void Menu_AddBuddy(object sender, EventArgs e)
        {
            AddBuddyDialog(Core, "");
        }

        private void BuddyView_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (SelectionInfo == null)
                return;

            if (SelectedItems.Count == 0)
            {
                SelectionInfo.ShowNetwork();
                return;
            }

            BuddyItem item = SelectedItems[0] as BuddyItem;

            if (item == null || item.Blank)
                SelectionInfo.ShowNetwork();

            else if (item.User != 0)
                SelectionInfo.ShowUser(item.User, 0);

            else
                SelectionInfo.ShowGroup(item.GroupLabel ? item.Text : null);
        }

        public static void AddBuddyDialog(OpCore core, string link)
        {
            GetTextDialog add = new GetTextDialog("Add Buddy", "Enter a buddy link", link);
            add.BigResultBox();

            if (add.ShowDialog() == DialogResult.OK)
                try
                {
                    core.Buddies.AddBuddy(add.ResultBox.Text);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                }
        }

        public List<ulong> GetSelectedIDs()
        {
            List<ulong> selected = new List<ulong>();

            foreach (BuddyItem item in SelectedItems)
                if (item.User != 0)
                    selected.Add(item.User);

            return selected;
        }
    }


    public class BuddyItem : ContainerListViewItem, IViewParams
    {
        public ulong User;
        public bool GroupLabel;
        public bool Blank;

        public BuddyItem()
        {
            Text = "";
            ImageIndex = 0;
            Blank = true;
        }

        public BuddyItem(string text, Font font, bool group)
        {
            Text = text;
            Font = font;
            ImageIndex = 0;
            GroupLabel = group;
        }

        public BuddyItem(string text, ulong id)
        {
            Text = text;
            User = id;
            ImageIndex = 0;
        }

        public ulong GetUser()
        {
            return User;
        }

        public uint GetProject()
        {
            return 0;
        }

        public bool IsExternal()
        {
            return true;
        }
    }
}
