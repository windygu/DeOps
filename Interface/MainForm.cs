using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Diagnostics;
using System.Threading;
using System.Runtime.InteropServices;

using DeOps.Implementation;
using DeOps.Implementation.Dht;

using DeOps.Components;
using DeOps.Components.Link;
using DeOps.Components.IM;
using DeOps.Components.Location;

using DeOps.Interface.TLVex;
using DeOps.Interface.Views;

namespace DeOps.Interface
{
    internal delegate void ShowExternalHandler(ViewShell view);
    internal delegate void ShowInternalHandler(ViewShell view);


    internal partial class MainForm : Form
    {
        internal OpCore Core;
        internal LinkControl Links;

        internal ShowExternalHandler ShowExternal;
        internal ShowInternalHandler ShowInternal;

        internal uint SelectedProject;

        ToolStripButton ProjectButton;
        uint ProjectButtonID;

        internal ViewShell InternalView;
        internal List<ExternalView> ExternalViews = new List<ExternalView>();

        Font BoldFont = new System.Drawing.Font("Tahoma", 8.25F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));

        int NewsSequence;
        Queue<NewsItem> NewsPending = new Queue<NewsItem>();
        Queue<NewsItem> NewsRecent = new Queue<NewsItem>();
        SolidBrush NewsBrush = new SolidBrush(Color.FromArgb(0, Color.White));
        Rectangle NewsArea;
        bool NewsHideUpdates;

        internal MainForm(OpCore core)
        {
            InitializeComponent();
            
            Core = core;
            Links = Core.Links;

            ShowExternal += new ShowExternalHandler(OnShowExternal);
            ShowInternal += new ShowInternalHandler(OnShowInternal);

            Core.NewsUpdate += new NewsUpdateHandler(Core_NewsUpdate);
            Links.GuiUpdate  += new LinkGuiUpdateHandler(Links_Update);

            CommandTree.SelectedLink = Core.LocalDhtID;

            TopToolStrip.Renderer = new ToolStripProfessionalRenderer(new OpusColorTable());
            NavStrip.Renderer = new ToolStripProfessionalRenderer(new NavColorTable());
            SideToolStrip.Renderer = new ToolStripProfessionalRenderer(new OpusColorTable());
        }

        internal void InitSideMode()
        {
            MainSplit.Panel1Collapsed = false;
            MainSplit.Panel2Collapsed = true;

            Width = 200;

            SideMode = true;
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            Text = Core.User.Settings.Operation + " - " + Core.User.Settings.ScreenName;

            //crit bar CurrentViewLabel.Text = "";

            CommandTree.Init(Links);
            CommandTree.ShowProject(0);

            //crit
            OnSelectChange(Core.LocalDhtID, CommandTree.Project);
            UpdateCommandPanel();

            if(SideMode)
                Left = Screen.PrimaryScreen.WorkingArea.Width - Width;

        }


        private void InviteMenuItem_Click(object sender, EventArgs e)
        {
            InviteForm form = new InviteForm(Core);
            form.ShowDialog(this);
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            Trace.WriteLine("Main Closing " + Thread.CurrentThread.ManagedThreadId.ToString());

            CommandTree.Fin();

            while (ExternalViews.Count > 0)
                if( !ExternalViews[0].SafeClose())
                {
                    e.Cancel = true;
                    return;
                }

            if (!CleanInternal())
            {
                e.Cancel = true;
                return;
            }

            ShowExternal -= new ShowExternalHandler(OnShowExternal);
            ShowInternal -= new ShowInternalHandler(OnShowInternal);

            Core.NewsUpdate -= new NewsUpdateHandler(Core_NewsUpdate);
            Links.GuiUpdate -= new LinkGuiUpdateHandler(Links_Update);

            foreach (OpComponent component in Core.Components.Values)
                component.GuiClosing();

            Core.GuiMain = null;

            if(LockForm)
            {
                LockForm = false;
                return;
            }

            if (Core.Sim == null)
                Application.Exit();
        }

        bool LockForm;
        

        private void LockButton_Click(object sender, EventArgs e)
        {
            LockForm = true;

            Close();

            Core.GuiTray = new TrayLock(Core, SideMode);
        }

        private bool CleanInternal()
        {
            if (InternalView != null)
            {
                if (!InternalView.Fin())
                    return false;

                InternalView.Dispose();
            }

            InternalPanel.Controls.Clear();
            
            return true;
        }


        void OnShowExternal(ViewShell view)
        {
            ExternalView external = new ExternalView(this, view);

            ExternalViews.Add(external);

            external.Show();
        }

        void OnShowInternal(ViewShell view)
        {
            if (!CleanInternal())
                return;

            view.Dock = DockStyle.Fill;

            InternalPanel.Visible = false;
            InternalPanel.Controls.Add(view);
            InternalView = view;

            UpdateNavBar();

            InternalView.Init();
            InternalPanel.Visible = true;
        }


        void RecurseFocus(TreeListNode parent, List<ulong> focused)
        {
            // add parent to focus list
            if (parent.GetType() == typeof(LinkNode))
                focused.Add(((LinkNode)parent).Link.DhtID);

            // iterate through sub items
            foreach (TreeListNode subitem in parent.Nodes)
                if (parent.GetType() == typeof(LinkNode))
                    RecurseFocus(subitem, focused);
        }

        void Links_Update(ulong key)
        {

            // check if removed
            if (!Links.LinkMap.ContainsKey(key))
                return;

            // update
            OpLink link = Links.LinkMap[key];

            if (!link.Loaded)
                return;

            if (!Links.ProjectRoots.ContainsKey(CommandTree.Project))
            {
                if (ProjectButton.Checked)
                    OperationButton.Checked = true;

                SideToolStrip.Items.Remove(ProjectButton);
                ProjectButton = null;
            }

            UpdateNavBar();
        }

        LinkNode GetSelected()
        {
            if (CommandTree.SelectedNodes.Count == 0)
                return null;

            TreeListNode node = CommandTree.SelectedNodes[0];

            if (node.GetType() != typeof(LinkNode))
                return null;

            return (LinkNode)node;
        }


        void UpdateCommandPanel()
        {
            if (GetSelected() == null)
                ShowNetworkStatus();

            else
                ShowNodeStatus();
        }

        private void ShowNetworkStatus()
        {
            string GlobalStatus = "";
            string OpStatus = "";

            if (Core.GlobalNet == null)
                GlobalStatus = "Disconnected";
            else if (Core.GlobalNet.Routing.Responsive())
                GlobalStatus = "Connected";
            else
                GlobalStatus = "Connecting";


            if (Core.OperationNet.Routing.Responsive())
                OpStatus = "Connected";
            else
                OpStatus = "Connecting";

            
            string html = 
             
                @"<html>
                <head>
	                <style type=""text/css"">
	                <!--
	                    body { margin: 0; }
	                    p    { font-size: 8.25pt; font-family: Tahoma }
	                -->
	                </style>
                </head>
                <body bgcolor=WhiteSmoke>
	                <table width=100% cellpadding=4>
	                    <tr><td bgcolor=green><p><b><font color=#ffffff>Network Status</font></b></p></td></tr>
	                </table>
                    <table callpadding=3>    
                        <tr><td><p><b>Global:</b></p></td><td><p>" + GlobalStatus + @"</p></td></tr>
	                    <tr><td><p><b>Network:</b></p></td><td><p>" + OpStatus + @"</p></td></tr>
	                    <tr><td><p><b>Firewall:</b></p></td><td><p>" + Core.Firewall.ToString() + @"</p></td></tr>
                    </table>
                </body>
                </html>
                ";

            // prevents clicking sound when browser navigates
            if (!StatusBrowser.DocumentText.Equals(html))
            {
                StatusBrowser.Hide();
                StatusBrowser.DocumentText = html;
                StatusBrowser.Show();
            }
        }

        private void ShowNodeStatus()
        {
            LinkNode node = GetSelected();

            if (node == null)
            {
                ShowNetworkStatus();
                return;
            }

            OpLink link = node.Link;

            string name = link.Name;
            
            string title = "None";
            if (link.Title.ContainsKey(CommandTree.Project))
                if (link.Title[CommandTree.Project] != "")
                    title = link.Title[CommandTree.Project];

            string projects = "";
            foreach (uint id in link.Projects)
                if(id != 0)
                    projects += "<a href='project:" + id.ToString() + "'>" + Links.ProjectNames[id] +"</a>, ";
            projects = projects.TrimEnd(new char[] { ' ', ',' });


            string locations = "";
            if (Core.OperationNet.Routing.Responsive())
            {
                if (Core.Locations.LocationMap.ContainsKey(link.DhtID))
                    foreach (LocInfo info in Core.Locations.LocationMap[link.DhtID].Values)
                        if (info.Location.Location == "")
                            locations += "Unknown, ";
                        else
                            locations += info.Location.Location + ", ";

                locations = locations.TrimEnd(new char[] { ' ', ',' });
            }

            string html =
                @"<html>
                <head>
	                <style type=""text/css"">
	                <!--
	                    body { margin: 0 }
	                    p    { font-size: 8.25pt; font-family: Tahoma }
                        A:link {text-decoration: none; color: black}
                        A:visited {text-decoration: none; color: black}
                        A:active {text-decoration: none; color: black}
                        A:hover {text-decoration: underline; color: black}
	                -->
	                </style>
                </head>
                <body bgcolor=WhiteSmoke>
	                <table width=100% cellpadding=4>
	                    <tr><td bgcolor=MediumSlateBlue><p><b><font color=#ffffff>" + link.Name + @"</font></b></p></td></tr>
	                </table>
                    <table callpadding=3>  
                        <tr><td><p><b>Title:</b></p></td><td><p>" + title + @"</p></td></tr>
	                    <tr><td><p><b>Projects:</b></p></td><td><p>" + projects + @"</p></td></tr>";

            if (locations != "")
                html += @"<tr><td><p><b>Locations:</b></p></td><td><p>" + locations + @"</p></td></tr>";
                            
            html += 
                        @"<tr><td><p><b>Last Seen:</b></p></td><td><p></p></td></tr>
                    </table>
                </body>
                </html>";

            //crit show locations local time

            // prevents clicking sound when browser navigates
            if (!StatusBrowser.DocumentText.Equals(html))
            {
                StatusBrowser.Hide();
                StatusBrowser.DocumentText = html;
                StatusBrowser.Show();
            }
        }


        private void CommandTree_MouseClick(object sender, MouseEventArgs e)
        {
            // this gets right click to select item
            TreeListNode clicked = CommandTree.GetNodeAt(e.Location) as TreeListNode;

            if (clicked == null)
                return;

            // project menu
            if (clicked == CommandTree.ProjectNode && e.Button == MouseButtons.Right)
            {
                ContextMenu treeMenu = new ContextMenu();

                treeMenu.MenuItems.Add(new MenuItem("Properties", new EventHandler(OnProjectProperties)));

                if (CommandTree.Project != 0)
                {
                    if (Links.LocalLink.Projects.Contains(CommandTree.Project))
                        treeMenu.MenuItems.Add(new MenuItem("Leave", new EventHandler(OnProjectLeave)));
                    else
                        treeMenu.MenuItems.Add(new MenuItem("Join", new EventHandler(OnProjectJoin)));
                }

                treeMenu.Show(CommandTree, e.Location);

                return;
            }


            if (clicked.GetType() != typeof(LinkNode))
                return;

            LinkNode item = clicked as LinkNode;



            // right click menu
            if (e.Button == MouseButtons.Right)
            {
                // menu
                ContextMenuStripEx treeMenu = new ContextMenuStripEx();

                // select
                treeMenu.Items.Add("Select", InterfaceRes.star, TreeMenu_Select);

                // views
                List<ToolStripMenuItem> quickMenus = new List<ToolStripMenuItem>();
                List<ToolStripMenuItem> extMenus = new List<ToolStripMenuItem>();

                foreach (OpComponent component in Core.Components.Values)
                {
                    if (component is LinkControl)
                        continue;

                    // quick
                    List<MenuItemInfo> menuList = component.GetMenuInfo(InterfaceMenuType.Quick, item.Link.DhtID, CommandTree.Project);

                    if (menuList != null && menuList.Count > 0)
                        foreach (MenuItemInfo info in menuList)
                            quickMenus.Add(new OpMenuItem(item.Link.DhtID, CommandTree.Project, info.Path, info));

                    // external
                    menuList = component.GetMenuInfo(InterfaceMenuType.External, item.Link.DhtID, CommandTree.Project);

                    if (menuList != null && menuList.Count > 0)
                        foreach (MenuItemInfo info in menuList)
                            extMenus.Add(new OpMenuItem(item.Link.DhtID, CommandTree.Project, info.Path, info));
                }

                if (quickMenus.Count > 0 || extMenus.Count > 0)
                {
                    treeMenu.Items.Add("-");

                    foreach (ToolStripMenuItem menu in quickMenus)
                        treeMenu.Items.Add(menu);
                }

                if (extMenus.Count > 0)
                {
                    ToolStripMenuItem viewItem = new ToolStripMenuItem("Views", InterfaceRes.views);

                    foreach (ToolStripMenuItem menu in extMenus)
                        viewItem.DropDownItems.Add(menu);

                    treeMenu.Items.Add(viewItem);
                }

                // link
                if (CommandTree.TreeMode == CommandTreeMode.Operation)
                {
                    List<ToolStripMenuItem> linkMenus = new List<ToolStripMenuItem>();

                    List<MenuItemInfo> menuList = Links.GetMenuInfo(InterfaceMenuType.Quick, item.Link.DhtID, CommandTree.Project);

                    if (menuList != null && menuList.Count > 0)
                        foreach (MenuItemInfo info in menuList)
                            linkMenus.Add(new OpMenuItem(item.Link.DhtID, CommandTree.Project, info.Path, info));

                    if (linkMenus.Count > 0)
                    {
                        treeMenu.Items.Add("-");

                        foreach (ToolStripMenuItem menu in linkMenus)
                            treeMenu.Items.Add(menu);
                    }
                }

                // show
                if (treeMenu.Items.Count > 0)
                    treeMenu.Show(CommandTree, e.Location);
            }
        }

        void TreeMenu_Select(object sender, EventArgs e)
        {
            SelectCurrentItem();
        }

        private void CommandTree_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            SelectCurrentItem();
        }

        void SelectCurrentItem()
        {
            LinkNode item = GetSelected();

            if (item == null)
                return;

            if (SideMode)
            {
                OpMenuItem info = new OpMenuItem(item.Link.DhtID, 0);

                if (Core.Locations.LocationMap.ContainsKey(info.DhtID))
                    ((IMControl)Core.Components[ComponentID.IM]).QuickMenu_View(info, null);
                else
                    Core.Mail.QuickMenu_View(info, null);
            }
            else
                OnSelectChange(item.Link.DhtID, CommandTree.Project);
        }


        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd,int wMsg, bool wParam, int lParam);


        void SetRedraw(Control ctl, bool lfDraw)
        {
            int WM_SETREDRAW  = 0x000B;

            SendMessage(ctl.Handle, WM_SETREDRAW, lfDraw, 0);
            if (lfDraw)
            {
                ctl.Invalidate();
                ctl.Refresh();
            }
        }

        void OnSelectChange(ulong id, uint project)
        {
            SuspendLayout();

            if (!Links.LinkMap.ContainsKey(id))
                id = Core.LocalDhtID;

            OpLink link = Links.LinkMap[id];

            if (!link.Projects.Contains(project))
                project = 0;

            // bold new and set
            SelectedProject = project;

            CommandTree.SelectLink(id);


            // setup toolbar with menu items for user
            HomeButton.Visible = id != Core.LocalDhtID;
            HomeSparator.Visible = HomeButton.Visible;

            PlanButton.DropDownItems.Clear();
            CommButton.DropDownItems.Clear();
            DataButton.DropDownItems.Clear();

            foreach (OpComponent component in Core.Components.Values)
            {
                List<MenuItemInfo> menuList = component.GetMenuInfo(InterfaceMenuType.Internal, id, project);

                if (menuList == null || menuList.Count == 0)
                    continue;

                foreach (MenuItemInfo info in menuList)
                {
                    string[] parts = info.Path.Split(new char[] { '/' });

                    if (parts.Length < 2)
                        continue;
                    
                    if (parts[0] == PlanButton.Text)
                        PlanButton.DropDownItems.Add(new OpStripItem(id, project, parts[1], info));

                    else if (parts[0] == CommButton.Text)
                        CommButton.DropDownItems.Add(new OpStripItem(id, project, parts[1], info));

                    else if (parts[0] == DataButton.Text)
                        DataButton.DropDownItems.Add(new OpStripItem(id, project, parts[1], info));
                }
            }

            // setup nav bar - add components
            UpdateNavBar();


            // find previous component in drop down, activate click on it
            string previous = InternalView != null? InternalView.GetTitle(true) : "Profile";

            if (!SelectComponent(previous))
                SelectComponent("Profile");

            ResumeLayout();
        }

        private bool SelectComponent(string component)
        {
            foreach (ToolStripMenuItem item in ComponentNavButton.DropDownItems)
                if (item.Text == component)
                {
                    item.PerformClick();
                    return true;
                }

            return false;
        }

        private void UpdateNavBar()
        {
            PersonNavButton.DropDownItems.Clear();
            ProjectNavButton.DropDownItems.Clear();
            ComponentNavButton.DropDownItems.Clear();

            OpLink link = null;

            if (Links.LinkMap.ContainsKey(CommandTree.SelectedLink))
            {
                link = Links.LinkMap[CommandTree.SelectedLink];

                if (link.DhtID == Core.LocalDhtID)
                    PersonNavButton.Text = "My";
                else
                    PersonNavButton.Text = link.Name + "'s";

                PersonNavItem self = null;
                
                // add higher and subs of higher
                OpLink higher = link.GetHigher(SelectedProject);
                if (higher != null)
                {
                    PersonNavButton.DropDownItems.Add(new PersonNavItem(higher.Name, higher.DhtID, this, PersonNav_Clicked));

                    List<ulong> adjacentIDs = Links.GetDownlinkIDs(higher.DhtID, SelectedProject, 1);
                    foreach (ulong id in adjacentIDs)
                    {
                        PersonNavItem item = new PersonNavItem("   " + Links.GetName(id), id, this, PersonNav_Clicked);
                        if (id == CommandTree.SelectedLink)
                        {
                            item.Font = BoldFont;
                            self = item;
                        }

                        PersonNavButton.DropDownItems.Add(item);
                    }
                }

                string childspacing = (self == null) ? "   " : "      ";

                // if self not added yet, add
                if (self == null)
                {
                    PersonNavItem item = new PersonNavItem(link.Name, link.DhtID, this, PersonNav_Clicked);
                    item.Font = BoldFont;
                    self = item;
                    PersonNavButton.DropDownItems.Add(item);
                }

                // add downlinks of self
                List<ulong> downlinkIDs = Links.GetDownlinkIDs(CommandTree.SelectedLink, SelectedProject, 1);
                foreach (ulong id in downlinkIDs)
                {
                    PersonNavItem item = new PersonNavItem(childspacing + Links.GetName(id), id, this, PersonNav_Clicked);

                    int index = PersonNavButton.DropDownItems.IndexOf(self);
                    PersonNavButton.DropDownItems.Insert(index+1, item);
                }
            }
            else
            {
                PersonNavButton.Text = "Unknown";
            }

            PersonNavButton.DropDownItems.Add("-");
            PersonNavButton.DropDownItems.Add("Browse...");


            // set person's projects
            if (Links.ProjectNames.ContainsKey(SelectedProject))
                ProjectNavButton.Text = Links.ProjectNames[SelectedProject];
            else
                ProjectNavButton.Text = "Unknown";

            if (link != null)
                foreach (uint project in link.Projects)
                {
                    string name = "Unknown";
                    if (Links.ProjectNames.ContainsKey(project))
                        name = Links.ProjectNames[project];

                    string spacing = (project == 0) ? "" : "   ";

                    ProjectNavButton.DropDownItems.Add(new ProjectNavItem(spacing + name, project, ProjectNav_Clicked));
                }


            // set person's components
            if (InternalView != null)
                ComponentNavButton.Text = InternalView.GetTitle(true);

            foreach (OpComponent component in Core.Components.Values)
            {
                List<MenuItemInfo> menuList = component.GetMenuInfo(InterfaceMenuType.Internal, CommandTree.SelectedLink, SelectedProject);

                if (menuList == null || menuList.Count == 0)
                    continue;

                foreach (MenuItemInfo info in menuList)
                    ComponentNavButton.DropDownItems.Add(new ComponentNavItem(info, CommandTree.SelectedLink, SelectedProject, info.ClickEvent));
            }
            
        }

        private void PersonNav_Clicked(object sender, EventArgs e)
        {
            PersonNavItem item = sender as PersonNavItem;

            if (item == null)
                return;

            OnSelectChange(item.DhtID, SelectedProject);
        }

        private void ProjectNav_Clicked(object sender, EventArgs e)
        {
            ProjectNavItem item = sender as ProjectNavItem;

            if (item == null)
                return;

            OnSelectChange(CommandTree.SelectedLink, item.ProjectID);
        }
        
        private void OperationButton_CheckedChanged(object sender, EventArgs e)
        {
            // if checked, uncheck other and display
            if (OperationButton.Checked)
            {
                OnlineButton.Checked = false;

                if (ProjectButton != null)
                    ProjectButton.Checked = false;

                MainSplit.Panel1Collapsed = false;

                CommandTree.ShowProject(0);
            }

            // if not check, check if online checked, if not hide
            else
            {
                if (!OnlineButton.Checked)
                    if (ProjectButton == null || !ProjectButton.Checked)
                    {
                        if (SideMode)
                            OperationButton.Checked = true;
                        else
                            MainSplit.Panel1Collapsed = true;
                    }
            }
        }

        private void OnlineButton_CheckedChanged(object sender, EventArgs e)
        {
            // if checked, uncheck other and display
            if (OnlineButton.Checked)
            {
                OperationButton.Checked = false;

                if (ProjectButton != null)
                    ProjectButton.Checked = false;

                MainSplit.Panel1Collapsed = false;

                CommandTree.ShowOnline();
            }

            // if not check, check if online checked, if not hide
            else
            {
                if (!OperationButton.Checked)
                    if (ProjectButton == null || !ProjectButton.Checked)
                    {
                        if (SideMode)
                            OnlineButton.Checked = true;
                        else
                            MainSplit.Panel1Collapsed = true;
                    }
            }
        }

        private void HomeButton_Click(object sender, EventArgs e)
        {
            OnSelectChange(Core.LocalDhtID, SelectedProject);
        }

        private void ProjectsButton_DropDownOpening(object sender, EventArgs e)
        {
            ProjectsButton.DropDownItems.Clear();

            ProjectsButton.DropDownItems.Add(new ToolStripMenuItem("New...", null, new EventHandler(ProjectMenu_New)));

            foreach (uint id in Links.ProjectNames.Keys)
                if (id != 0)
                    ProjectsButton.DropDownItems.Add(new ProjectItem(Links.ProjectNames[id], id, new EventHandler(ProjectMenu_Click)));
        }

        private void ProjectMenu_New(object sender, EventArgs e)
        {
            NewProjectForm form = new NewProjectForm(Core);

            if (form.ShowDialog(this) == DialogResult.OK)
            {
                ProjectItem item = new ProjectItem("", form.ProjectID, null);
                ProjectMenu_Click(item, e);
            }
        }

        private void ProjectMenu_Click(object sender, EventArgs e)
        {
            ProjectItem item = sender as ProjectItem;

            if (item == null)
                return;

            UpdateProjectButton(item.ProjectID);
        }

        private void UpdateProjectButton(uint id)
        {
            ProjectButtonID = id;

            // destroy any current project button
            if (ProjectButton != null)
                SideToolStrip.Items.Remove(ProjectButton);

            // create button for project
            ProjectButton = new ToolStripButton(Links.ProjectNames[ProjectButtonID], null, new EventHandler(ShowProject));
            ProjectButton.TextDirection = ToolStripTextDirection.Vertical90;
            ProjectButton.CheckOnClick = true;
            ProjectButton.Checked = true;
            SideToolStrip.Items.Add(ProjectButton);

            // click button
            ShowProject(ProjectButton, null);
        }


        private void ShowProject(object sender, EventArgs e)
        {
            ToolStripButton button = sender as ToolStripButton;

            if (sender == null)
                return;

            // check project exists
            if (!Links.ProjectRoots.ContainsKey(ProjectButtonID))
            {
                OperationButton.Checked = true;
                SideToolStrip.Items.Remove(ProjectButton);
                ProjectButton = null;
            }

            // if checked, uncheck other and display
            if (button.Checked)
            {
                OperationButton.Checked = false;
                OnlineButton.Checked = false;
                MainSplit.Panel1Collapsed = false;

                CommandTree.ShowProject(ProjectButtonID);
            }

            // if not check, check if online checked, if not hide
            else
            {
                if (!OperationButton.Checked && !OnlineButton.Checked)
                {
                    if (SideMode)
                        ProjectButton.Checked = true;
                    else
                        MainSplit.Panel1Collapsed = true;
                }
            }
        }


        bool SideMode;
        int Panel2Width;

        private void SideButton_CheckedChanged(object sender, EventArgs e)
        {
            if (SideButton.Checked)
            {
                Panel2Width = MainSplit.Panel2.Width;

                MainSplit.Panel1Collapsed = false;
                MainSplit.Panel2Collapsed = true;

                Width -= Panel2Width;
                Left += Panel2Width;

                SideMode = true;

                OnSelectChange(Core.LocalDhtID, 0);
            }

            else
            {
                Left -= Panel2Width;

                Width += Panel2Width;

                MainSplit.Panel2Collapsed = false;

                SideMode = false;
            }
        }

        private void OnProjectProperties(object sender, EventArgs e)
        {

        }

        private void OnProjectLeave(object sender, EventArgs e)
        {
            if (CommandTree.Project != 0)
                Links.LeaveProject(CommandTree.Project);

            // if no roots, remove button change projectid to 0
            if (!Links.ProjectRoots.ContainsKey(CommandTree.Project))
            {
                SideToolStrip.Items.Remove(ProjectButton);
                ProjectButton = null;
                OperationButton.Checked = true;
            }
        }

        private void OnProjectJoin(object sender, EventArgs e)
        {
            if (CommandTree.Project != 0)
                Links.JoinProject(CommandTree.Project);
        }

        private void StatusBrowser_Navigating(object sender, WebBrowserNavigatingEventArgs e)
        {
            string url = e.Url.OriginalString;

            string[] parts = url.Split(new char[] {':'});

            if(parts.Length < 2)
                return;

            if (parts[0] == "project")
            {
                UpdateProjectButton(uint.Parse(parts[1]));

                e.Cancel = true;
            }
        }

        private void RightClickMenu_Opening(object sender, CancelEventArgs e)
        {
            LinkNode item = GetSelected();

            if (item == null)
            {
                e.Cancel = true;
                return;
            }

            if (item.Link.DhtID != Core.LocalDhtID)
            {
                e.Cancel = true;
                return;
            }
        }

        private void EditMenu_Click(object sender, EventArgs e)
        {
            EditLink edit = new EditLink(Core, CommandTree.Project);
            edit.ShowDialog(this);
        }

        private void CommandTree_SelectedItemChanged(object sender, EventArgs e)
        {
            UpdateCommandPanel();
        }

        private void PopoutButton_Click(object sender, EventArgs e)
        {
            SuspendLayout();
            InternalPanel.Controls.Clear();

            OnShowExternal(InternalView);
            InternalView = null;

            OnSelectChange(CommandTree.SelectedLink, SelectedProject);
            ResumeLayout();
        }

        private void NewsTimer_Tick(object sender, EventArgs e)
        {
            if (NewsPending.Count == 0)
                return;

            // sequence = 1/10s
            Color color = NewsPending.Peek().DisplayColor;
            int alpha = NewsBrush.Color.A;

            // 1/4s fade in
            if (NewsSequence < 3)
                alpha += 255 / 4;

            // 2s show
            else if (NewsSequence < 23)
                alpha = 255;

            // 1/4s fad out
            else if (NewsSequence < 26)
                alpha -= 255 / 4;

            // 1/2s hide
            else if (NewsSequence < 31)
            {
                alpha = 0;
            }
            else
            {
                NewsSequence = 0;
                NewsRecent.Enqueue(NewsPending.Dequeue());

                while (NewsRecent.Count > 15)
                    NewsRecent.Dequeue();
            }

            
            if (NewsBrush.Color.A != alpha || NewsBrush.Color != color)
                NewsBrush = new SolidBrush(Color.FromArgb(alpha, color));

            NewsSequence++;
            NavStrip.Invalidate();
        }

        private void NavStrip_Paint(object sender, PaintEventArgs e)
        {
            NewsArea = new Rectangle();

            if (NewsPending.Count == 0)
                return;

            // get bounds where we can put news text
            int x = ComponentNavButton.Bounds.X + ComponentNavButton.Bounds.Width + 4;
            int width = NewsButton.Bounds.X - 4 - x;

            if (width < 0)
            {
                NewsButton.Image = InterfaceRes.news_hot;
                return;
            }

            // determine size of text
            int reqWidth = (int) e.Graphics.MeasureString(NewsPending.Peek().Info.Message, BoldFont).Width;

            if (width < reqWidth)
            {
                NewsButton.Image = InterfaceRes.news_hot;
                return;
            }

            // draw text
            x = x + width / 2 - reqWidth / 2;
            e.Graphics.DrawString(NewsPending.Peek().Info.Message, BoldFont, NewsBrush, x, 5);

            NewsArea = new Rectangle(x, 5, reqWidth, 9);
        }

        void Core_NewsUpdate(NewsItemInfo info)
        {
            NewsItem item = new NewsItem(info, SideMode, Core.LocalDhtID); // pop out external view if in messenger mode
            item.Text = Core.TimeNow.ToString("h:mm ") + info.Message;

            // set color
            if (Links.IsLowerDirect(info.DhtID, info.ProjectID))
                item.DisplayColor = Color.LightBlue;
            else if (Links.IsHigher(info.DhtID, info.ProjectID))
                item.DisplayColor = Color.Coral;
            else
                item.DisplayColor = Color.White;


            Queue<NewsItem> queue = NewsHideUpdates ? NewsRecent : NewsPending;

            queue.Enqueue(item);//Links.IsHigher(info.DhtID, info.ProjectID)));

            while (queue.Count > 15)
                queue.Dequeue();

            if(NewsHideUpdates)
                NewsButton.Image = InterfaceRes.news_hot;
        }

        private void NewsButton_DropDownOpening(object sender, EventArgs e)
        {
            NewsButton.DropDown.Items.Clear();

            foreach (NewsItem item in NewsPending)
                NewsButton.DropDown.Items.Add(item);

            if (NewsPending.Count > 0 && NewsRecent.Count > 0)
                NewsButton.DropDown.Items.Add("-");

            foreach (NewsItem item in NewsRecent)
                NewsButton.DropDown.Items.Add(item);

            if (NewsRecent.Count > 0)
                NewsButton.DropDown.Items.Add("-");

            ToolStripMenuItem hide = new ToolStripMenuItem("Hide News Updates", null, new EventHandler(NewsButton_HideUpdates));
            hide.Checked = NewsHideUpdates;
            NewsButton.DropDown.Items.Add(hide);

            NewsButton.Image = InterfaceRes.news;
        }

        private void NavStrip_MouseMove(object sender, MouseEventArgs e)
        {
            if (NewsArea.Contains(e.Location) && NewsPending.Count > 0 && NewsPending.Peek().Info.ClickEvent != null)
                Cursor.Current = Cursors.Hand;
            else
                Cursor.Current = Cursors.Arrow;
        }

        private void NavStrip_MouseClick(object sender, MouseEventArgs e)
        {
            if (NewsArea.Contains(e.Location) && NewsPending.Peek() != null)
                NewsPending.Peek().Info.ClickEvent.Invoke(NewsPending.Peek(), null);
        }

        private void NewsButton_HideUpdates(object sender, EventArgs e)
        {
            NewsHideUpdates = !NewsHideUpdates;

            if (NewsHideUpdates && NewsPending.Count > 0)
                while (NewsPending.Count != 0)
                    NewsRecent.Enqueue(NewsPending.Dequeue());

            NavStrip.Invalidate();
        }
    }

    class NewsItem : ToolStripMenuItem, IViewParams
    {
        internal NewsItemInfo Info;
        internal Color DisplayColor;
        internal bool External;
        ulong LocalID;

        internal NewsItem(NewsItemInfo info, bool external, ulong localid)
            : base(info.Message, info.Symbol != null ? info.Symbol.ToBitmap() : null, info.ClickEvent)
        {
            Info = info;
            External = external;
            LocalID = localid;
        }

        public ulong GetKey()
        {
            return Info.ShowRemote ? Info.DhtID : LocalID;
        }

        public uint GetProject()
        {
            return Info.ProjectID;
        }

        public bool IsExternal()
        {
            return External;
        }
    }

    class OpStripItem : ToolStripMenuItem, IViewParams
    {
        internal ulong DhtID;
        internal uint ProjectID;
        internal MenuItemInfo Info;

        internal OpStripItem(ulong key, uint id, string text, MenuItemInfo info)
            : base(text, null, info.ClickEvent )
        {
            DhtID = key;
            ProjectID = id;
            Info = info;

            Image = Info.Symbol.ToBitmap();
        }

        public ulong GetKey()
        {
            return DhtID;
        }

        public uint GetProject()
        {
            return ProjectID;
        }

        public bool IsExternal()
        {
            return false;
        }
    }

    class ProjectItem : ToolStripMenuItem
    {
        internal uint ProjectID;

        internal ProjectItem(string text, uint id, EventHandler onClick)
            : base(text, null, onClick)
        {
            ProjectID = id;
        }
    }

    class OpMenuItem : ToolStripMenuItem, IViewParams
    {
        internal ulong DhtID;
        internal uint ProjectID;
        internal MenuItemInfo Info;

        internal OpMenuItem(ulong key, uint id)
        {
            DhtID = key;
            ProjectID = id;
        }

        internal OpMenuItem(ulong key, uint id, string text, MenuItemInfo info)
            : base(text, null, info.ClickEvent)
        {
            DhtID = key;
            ProjectID = id;
            Info = info;

            if(info.Symbol != null)
                Image = info.Symbol.ToBitmap();
        }

        public ulong GetKey()
        {
            return DhtID;
        }

        public uint GetProject()
        {
            return ProjectID;
        }

        public bool IsExternal()
        {
            return true;
        }
    }


    class PersonNavItem : ToolStripMenuItem
    {
        internal ulong DhtID;

        internal PersonNavItem(string name, ulong dhtid, MainForm form, EventHandler onClick)
            : base(name, null, onClick)
        {
            DhtID = dhtid;

            Font = new System.Drawing.Font("Tahoma", 8.25F);

            if (DhtID == form.Core.LocalDhtID)
                Image = InterfaceRes.star;
        }
    }

    class ProjectNavItem : ToolStripMenuItem
    {
        internal uint ProjectID;

        internal ProjectNavItem(string name, uint project, EventHandler onClick)
            : base(name, null, onClick)
        {
            ProjectID = project;

            Font = new System.Drawing.Font("Tahoma", 8.25F);
        }
    }

    class ComponentNavItem : ToolStripMenuItem, IViewParams
    {
        ulong DhtID;
        uint ProjectID;

        internal ComponentNavItem(MenuItemInfo info, ulong id, uint project, EventHandler onClick)
            : base("", null, onClick)
        {

            DhtID = id;
            ProjectID = project;

            string[] parts = info.Path.Split(new char[] { '/' });

            if (parts.Length == 2)
                Text = parts[1];

            Font = new System.Drawing.Font("Tahoma", 8.25F);

            if (info.Symbol != null)
                Image = info.Symbol.ToBitmap();
        }

        public ulong GetKey()
        {
            return DhtID;
        }

        public uint GetProject()
        {
            return ProjectID;
        }

        public bool IsExternal()
        {
            return false;
        }
    }
}
