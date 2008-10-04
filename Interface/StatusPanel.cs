﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

using RiseOp.Implementation;

using RiseOp.Services;
using RiseOp.Services.Trust;
using RiseOp.Services.Buddy;
using RiseOp.Services.IM;
using RiseOp.Services.Location;
using RiseOp.Services.Mail;


namespace RiseOp.Interface
{
    internal partial class StatusPanel : CustomDisposeControl
    {
        // bottom status panel
        StringBuilder StatusHtml = new StringBuilder(4096);

        const string ContentPage =
                @"<html>
                <head>
	                <style type='text/css'>
		                body { margin: 0; font-size: 8.25pt; font-family: Tahoma; }

		                A:link, A:visited, A:active {text-decoration: none; color: blue;}
		                A:hover {text-decoration: underline; color: blue;}

		                .header{color: white;}
		                A.header:link, A.header:visited, A.header:active {text-decoration: none; color: white;}
		                A.header:hover {text-decoration: underline; color: white;}
                		
		                .content{padding: 3px; line-height: 12pt;}
                		
	                </style>

	                <script>
		                function SetElement(id, text)
		                {
			                document.getElementById(id).innerHTML = text;
		                }
	                </script>
                </head>
                <body bgcolor=WhiteSmoke>

                    <div class='header' id='header'></div>
                    <div class='content' id='content'></div>

                </body>
                </html>";


        OpCore Core;

        enum StatusModeType { None, Network, User, Project, Group };
        
        StatusModeType CurrentMode = StatusModeType.Network;

        ulong UserID;
        uint  ProjectID;
        string BuddyGroup;

        string IMImg, MailImg, BuddyWhoImg, TrustImg, UntrustImg;


        internal StatusPanel()
        {
            InitializeComponent();

            StatusBrowser.DocumentText = ContentPage;

            
        }

        internal void Init(OpCore core)
        {
            Core = core;

            Core.Locations.GuiUpdate += new LocationGuiUpdateHandler(Location_Update);
            Core.Buddies.GuiUpdate += new BuddyGuiUpdateHandler(Buddy_Update);

            if (Core.Trust != null)
                Core.Trust.GuiUpdate += new LinkGuiUpdateHandler(Trust_Update);

            IMImg       = ExtractImage("IM",        RiseOp.Services.IM.IMRes.Icon.ToBitmap());
            MailImg     = ExtractImage("Mail",      RiseOp.Services.Mail.MailRes.Mail);
            BuddyWhoImg = ExtractImage("BuddyWho",  RiseOp.Services.Buddy.BuddyRes.buddy_who);
            TrustImg    = ExtractImage("Trust",     RiseOp.Services.Trust.LinkRes.linkup);
            UntrustImg  = ExtractImage("Untrust",   RiseOp.Services.Trust.LinkRes.unlink);

            ShowNetwork();
        }

        private string ExtractImage(string filename, Bitmap image)
        {
            if (!File.Exists(Core.User.TempPath + Path.DirectorySeparatorChar + filename + ".png"))
            {
                using (FileStream stream = new FileStream(Core.User.TempPath + Path.DirectorySeparatorChar + filename + ".png", FileMode.CreateNew, FileAccess.Write))
                    image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            }

            string path = "file:///" + Core.User.TempPath + "/" + filename + ".png";

            path = path.Replace(Path.DirectorySeparatorChar, '/');

            return path;
        }

        internal override void CustomDispose()
        {
            Core.Locations.GuiUpdate -= new LocationGuiUpdateHandler(Location_Update);
            Core.Buddies.GuiUpdate -= new BuddyGuiUpdateHandler(Buddy_Update);

            if(Core.Trust != null)
                Core.Trust.GuiUpdate -= new LinkGuiUpdateHandler(Trust_Update);
        }

        void Trust_Update(ulong user)
        {
            if (CurrentMode == StatusModeType.Project)
                ShowProject(ProjectID);

            if (CurrentMode == StatusModeType.User && user == UserID)
                ShowUser(user, ProjectID);
        }

        void Buddy_Update()
        {
            // if buddy list viewed reload
            if (CurrentMode == StatusModeType.User)
                ShowUser(UserID, ProjectID);

        }

        void Location_Update(ulong user)
        {
            if (CurrentMode == StatusModeType.User && user == UserID)
                ShowUser(user, ProjectID);
        }

        private void SecondTimer_Tick(object sender, EventArgs e)
        {
            if (DesignMode)
                return;

            if (CurrentMode == StatusModeType.Network)
                ShowNetwork();
        }

        internal void ShowNetwork()
        {
            CurrentMode = StatusModeType.Network;
            UserID = 0;

            UpdateHeader("Green", "Network Status");

            string content = "";

            content += "<div style='padding-left: 10; line-height: 14pt;'>";

            if (Core.Context.Lookup != null)
            {
                string lookup = Core.Context.Lookup.Network.Responsive ? "Connected" : "Connecting";
                content += "<b>Lookup: </b>" + lookup + "<br>";
            }

            string operation = Core.Network.Responsive ? "Connected" : "Connecting";
            content += "<b>Network: </b>" + operation + "<br>";

            content += "<b>Firewall: </b>" + Core.GetFirewallString() + "<br>";

            content += "<b><a href='http://settings'>Settings</a></b><br>";

            content += "</div>";

            UpdateContent(content);
        }

        void UpdateHeader(string color, string title)
        {
            string header = "";
            header += "<div style='padding: 3px; background: " + color + "; '>";
            header += "<b>" + title + "</b>";
            header += "</div>";

            StatusBrowser.Document.InvokeScript("SetElement", new String[] { "header", header });
        }

        private void UpdateContent(string content)
        {
            StatusBrowser.Document.InvokeScript("SetElement", new String[] { "content", content });
        }


        internal void ShowProject(uint project)
        {
            CurrentMode = StatusModeType.Project;
            ProjectID = project;

            UpdateHeader("FireBrick",  Core.Trust.GetProjectName(project));

            string content = "<div style='padding-left: 10; line-height: 14pt;'>";

            content += AddContentLink("rename", "Rename"); 

            if (project != 0)
                content += AddContentLink("leave", "Leave");

            if (project == 0 && Core.Trust.LocalTrust.Links[0].Uplink == null)
                content += AddContentLink("settings", "Settings");

            content += "</div>";

            UpdateContent(content);
        }

        string AddContentLink(string link, string name)
        {
            return "<b><a href='http://" + link + "'>" + name + "</a></b><br>";
        }

        internal void ShowGroup(string name)
        {
            CurrentMode = StatusModeType.Group;

            BuddyGroup = name;

            UpdateHeader("FireBrick", BuddyGroup == null ? "Buddies" : BuddyGroup);

            string content = "<div style='padding-left: 10;'>";

            content += AddContentLink("add_buddy", "Add Buddy");

            if (BuddyGroup != null)
            {
                content += AddContentLink("remove_group/" + name, "Remove Group");
                content += AddContentLink("rename_group/" + name, "Rename Group");
            }
            //else
            //    content += AddContentLink("add_group", "Add Group");

            content += "</div>";

            UpdateContent(content);
        }

        internal void ShowUser(ulong user, uint project)
        {
            CurrentMode = StatusModeType.User;

            UserID = user;
            ProjectID = project;

            string header = "";
            string content = "";


            // get trust info
            OpLink link = null, parent = null;
            if (Core.Trust != null)
            {
                link = Core.Trust.GetLink(user, project);

                if(link != null)
                    parent = link.GetHigher(false);
            }

            // if loop root
            if (link != null && link.IsLoopRoot)
            {
                content = "<b>Order</b><br>";

                content += "<div style='padding-left: 10;'>";

                if (link.Downlinks.Count > 0)
                {
                    foreach (OpLink downlink in link.Downlinks)
                    {
                        string entry = "";

                        if (downlink.UserID == Core.UserID)
                            entry += "<b>" + Core.GetName(downlink.UserID) + "</b> <i>trusts</i>";
                        else
                            entry += Core.GetName(downlink.UserID) + " <i>trusts</i>";

                        if (downlink.GetHigher(true) == null)
                            entry = "<font style='color: red;'>" + entry + "</font>";

                        content += entry + "<br>";
                    }

                    content += Core.GetName(link.Downlinks[0].UserID) + "<br>";
                }

                content += "</div>";

                UpdateHeader("MediumSlateBlue", "Trust Loop");
                StatusBrowser.Document.InvokeScript("SetElement", new String[] { "content", content });
                return;
            }

            // add icons on right
            content += "<div style='float: right;'>";

            Func<string, string, string> getImgLine = (url, path) => "<a href='http://" + url + "'><img style='margin:2px;' src='" + path + "' border=0></a><br>";

            if (UserID != Core.UserID && Core.GetService(ServiceID.IM) != null && Core.Locations.ActiveClientCount(UserID) > 0)
                content += getImgLine("im", IMImg);

            if (UserID != Core.UserID && Core.GetService(ServiceID.Mail) != null)
                content += getImgLine("mail", MailImg);

            content += getImgLine("buddy_who", BuddyWhoImg);

            if (UserID != Core.UserID && link != null)
            {
                OpLink local = Core.Trust.GetLink(Core.UserID, ProjectID);

                if (local.Uplink == link)
                    content += getImgLine("untrust", UntrustImg); 
                else
                    content += getImgLine("trust", TrustImg); 
            }

            content += "</div>";


            // name
            string username = Core.GetName(user);
            header = "<a class='header' href='http://rename_user'>" + username + "</a>";

 
            if (link != null)
            {
                // trust unconfirmed?
                if (parent != null && !parent.Confirmed.Contains(link.UserID))
                {
                    bool requested = parent.Requests.Any(r => r.KeyID == link.UserID);

                    string msg = requested ? "Trust Requested" : "Trust Denied";
                    msg = "<font style='text-decoration: blink; line-height: 18pt; color: red;'>" + msg + "</font>";

                    if (parent.UserID == Core.UserID)
                        msg = "<a href='http://trust_accept'>" + msg + "</a>";

                    content += "<b>" + msg + "</b><br>";
                }

                // title
                if(parent != null)
                {
                    string title = parent.Titles.ContainsKey(UserID) ? parent.Titles[UserID] : "None";
                
                    if(parent.UserID == Core.UserID)
                        title = "<a href='http://change_title/" + title + "'>" + title + "</a>";
                   
                    content += "<b>Title: </b>" + title + "<br>"; 
                }
                // projects
                string projects = "";
                foreach (uint id in link.Trust.Links.Keys)
                    if (id != 0)
                        projects += "<a href='http://project/" + id.ToString() + "'>" + Core.Trust.GetProjectName(id) + "</a>, ";
                projects = projects.TrimEnd(new char[] { ' ', ',' });

                if (projects != "")
                    content += "<b>Projects: </b>" + projects + "<br>";
            }

            //Locations:
            //    Home: Online
            //    Office: Away - At Home
            //    Mobile: Online, Local Time 2:30pm
            //    Server: Last Seen 10/2/2007

            string locations = "";

            foreach (ClientInfo info in Core.Locations.GetClients(user))
            {
                string name = Core.Locations.GetLocationName(user, info.ClientID);

                if (info.UserID == Core.UserID && info.ClientID == Core.Network.Local.ClientID)
                    name = "<a href='http://edit_location'>" + name + "</a>";

                locations += "<b>" + name + ": </b>";

                if (info.Data.Away)
                    locations += "Away " + info.Data.AwayMessage;
                else
                    locations += "Online";

                if (info.Data.GmtOffset != System.TimeZone.CurrentTimeZone.GetUtcOffset(DateTime.Now).Minutes)
                    locations += ", Local Time " + Core.TimeNow.ToUniversalTime().AddMinutes(info.Data.GmtOffset).ToString("t");

                locations += "<br>";
            }

            if (locations == "")
                content += "<b>Offline</b><br>";
            else
            {
                content += "<b>Locations</b><br>";
                content += "<div style='padding-left: 10; line-height: normal'>";
                content += locations;
                content += "</div>";
            }

            string aliases = "";
            if (Core.Trust != null)
            {
                OpTrust trust = Core.Trust.GetTrust(user);

                if (trust.Name != username)
                    aliases += "<a href='http://use_name/" + trust.Name + "'>" + trust.Name + "</a>, ";
            }

            OpBuddy buddy;
            if(Core.Buddies.BuddyList.SafeTryGetValue(user, out buddy))
                if(buddy.Name != username)
                    aliases += "<a href='http://use_name/" + buddy.Name + "'>" + buddy.Name + "</a>, ";

            if(aliases != "")
                content += "<b>Aliases: </b>" + aliases.Trim(',', ' ') + "<br>";


            UpdateHeader("MediumSlateBlue", header);
            StatusBrowser.Document.InvokeScript("SetElement", new String[] { "content", content });
        }

        string GenerateContent(List<Tuple<string, string>> tuples, List<Tuple<string, string>> locations, bool online)
        {
            string content = "<table callpadding=3>  ";

            foreach (Tuple<string, string> tuple in tuples)
                content += "<tr><td><p><b>" + tuple.First + "</b></p></td> <td><p>" + tuple.Second + "</p></td></tr>";

            if (locations == null)
                return content + "</table>";

            // locations
            string ifonline = online ? "Locations" : "Offline";

            content += "<tr><td colspan=2><p><b>" + ifonline + "</b><br>";
            foreach (Tuple<string, string> tuple in locations)
                content += "&nbsp&nbsp&nbsp <b>" + tuple.First + ":</b> " + tuple.Second + "<br>";
            content += "</p></td></tr>";

            return content + "</table>";
        }

        private void StatusBrowser_Navigating(object sender, WebBrowserNavigatingEventArgs e)
        {
            string url = e.Url.OriginalString;

            if (url.StartsWith("about:blank"))
                return;

            url = url.Replace("http://", "");
            url = url.TrimEnd('/');

            string[] command = url.Split('/');


            if (CurrentMode == StatusModeType.Project)
            {
                if (url == "rename")
                {
                    GetTextDialog rename = new GetTextDialog("Rename Project", "Enter new name for project " + Core.Trust.GetProjectName(ProjectID), Core.Trust.GetProjectName(ProjectID));

                    if (rename.ShowDialog() == DialogResult.OK)
                        Core.Trust.RenameProject(ProjectID, rename.ResultBox.Text);
                }

                else if (url == "leave")
                {
                    if (MessageBox.Show("Are you sure you want to leave " + Core.Trust.GetProjectName(ProjectID) + "?", "Leave Project", MessageBoxButtons.YesNo) == DialogResult.Yes)
                        Core.Trust.LeaveProject(ProjectID);
                }

                else if (url == "settings")
                {
                    new RiseOp.Interface.Settings.Operation(Core).ShowDialog(this);
                }
            }

            else if (CurrentMode == StatusModeType.Group)
            {
                if (url == "add_buddy")
                {
                    BuddyView.AddBuddyDialog(Core);
                }

                else if (url == "add_group")
                {
                    // not enabled yet
                }

                else if (command[0] == "rename_group")
                {
                    string name = command[1];

                    GetTextDialog rename = new GetTextDialog("Rename Group", "Enter a new name for group " + name, name);

                    if (rename.ShowDialog() == DialogResult.OK)
                        Core.Buddies.RenameGroup(name, rename.ResultBox.Text);
                }

                else if (command[0] == "remove_group")
                {
                    string name = command[1];

                    if(MessageBox.Show("Are you sure you want to remove " + name + "?", "Remove Group", MessageBoxButtons.YesNo) == DialogResult.Yes)
                        Core.Buddies.RemoveGroup(name);
                }
            }

            else if (CurrentMode == StatusModeType.User)
            {
                if (url == "rename_user")
                {
                    GetTextDialog rename = new GetTextDialog("Rename User", "New name for " + Core.GetName(UserID), Core.GetName(UserID));

                    if (rename.ShowDialog() == DialogResult.OK)
                        Core.RenameUser(UserID, rename.ResultBox.Text);
                }

                else if (url == "trust_accept")
                {
                    if (MessageBox.Show("Are you sure you want to accept trust from " + Core.GetName(UserID) + "?", "Accept Trust", MessageBoxButtons.YesNo) == DialogResult.Yes)
                        Core.Trust.AcceptTrust(UserID, ProjectID);
                }

                else if (command[0] == "change_title")
                {
                    string def = command[1];

                    GetTextDialog title = new GetTextDialog("Change Title", "Enter title for " + Core.GetName(UserID), def);

                    if (title.ShowDialog() == DialogResult.OK)
                        Core.Trust.SetTitle(UserID, ProjectID, title.ResultBox.Text);

                }

                else if (url == "edit_location")
                {
                    GetTextDialog place = new GetTextDialog("Change Location", "Where is this instance located? (Home, Work, Mobile?)", "");

                    if (place.ShowDialog() == DialogResult.OK)
                    {
                        Core.Locations.LocalClient.Data.Place = place.ResultBox.Text;
                        Core.Locations.UpdateLocation();
                    }
                }

                else if (command[0] == "project")
                {
                    uint id = uint.Parse(command[1]);

                    if (Core.GuiMain != null && Core.GuiMain.GetType() == typeof(MainForm))
                        ((MainForm)Core.GuiMain).ShowProject(id);
                }

                else if (command[0] == "use_name")
                {
                    string name = System.Web.HttpUtility.UrlDecode(command[1]);

                    if (MessageBox.Show("Change " + Core.GetName(UserID) + "'s name to " + name + "?", "Change Name", MessageBoxButtons.YesNo) == DialogResult.Yes)
                        Core.RenameUser(UserID, name);
                }

                else if (url == "im")
                {
                    IMService im = Core.GetService(ServiceID.IM) as IMService;

                    if (im != null)
                        im.OpenIMWindow(UserID);
                }

                else if (url == "mail")
                {
                    MailService mail = Core.GetService(ServiceID.Mail) as MailService;

                    if (mail != null)
                        mail.OpenComposeWindow(UserID);
                }

                else if (url == "buddy_who")
                {
                    Core.Buddies.ShowIdentity(UserID);
                }

                else if (url == "trust")
                {
                    Core.Trust.LinkupTo(UserID, ProjectID);
                }

                else if (url == "untrust")
                {
                    Core.Trust.UnlinkFrom(UserID, ProjectID);
                }
            }

            else if (CurrentMode == StatusModeType.Network)
            {
                if (url == "settings")
                {
                    new RiseOp.Interface.Settings.Connecting(Core).ShowDialog();
                }
            }

            e.Cancel = true;

        }
    }

    // control's dispose code, activates our custom dispose code which we can run in our class
    internal class CustomDisposeControl : UserControl
    {
        virtual internal void CustomDispose() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                CustomDispose();

            base.Dispose(disposing);
        }
    }

}