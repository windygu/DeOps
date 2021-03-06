using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

using DeOps.Services.Trust;
using DeOps.Implementation;
using DeOps.Implementation.Protocol;
using DeOps.Interface;


namespace DeOps.Services.Profile
{
    public partial class ProfileView : ViewShell
    {
        OpCore Core;
        ProfileService Profiles;
        TrustService Trust;

        ulong UserID;
        public uint ProjectID;

        public Dictionary<string, string> TextFields = new Dictionary<string, string>();
        public Dictionary<string, string> FileFields = new Dictionary<string, string>();



        public ProfileView(ProfileService profile, ulong id, uint project)
        {
            InitializeComponent();

            Profiles = profile;
            Core = profile.Core;
            Trust = Core.Trust;

            UserID = id;
            ProjectID = project;
        }

        public override void Init()
        {
            Profiles.ProfileUpdate += new ProfileUpdateHandler(Profile_Update);
            Trust.GuiUpdate += new LinkGuiUpdateHandler(Trust_Update);
            Core.KeepDataGui += new KeepDataHandler(Core_KeepData);
            
            OpProfile profile = Profiles.GetProfile(UserID);

            if (profile == null)
                DisplayLoading();
            else
                Profile_Update(profile);

            // have to set twice (init document?) for page to show up
            if (GuiUtils.IsRunningOnMono())
            {
                if (profile == null)
                    DisplayLoading();
                else
                    Profile_Update(profile);
            }

            List<ulong> ids = new List<ulong>();
            ids.AddRange(Trust.GetUplinkIDs(UserID, ProjectID));

            foreach (ulong id in ids)
                Profiles.Research(id);
        }

        public override bool Fin()
        {
            Profiles.ProfileUpdate -= new ProfileUpdateHandler(Profile_Update);
            Trust.GuiUpdate -= new LinkGuiUpdateHandler(Trust_Update);
            Core.KeepDataGui -= new KeepDataHandler(Core_KeepData);

            return true;
        }

        void Core_KeepData()
        {
            Core.KeepData.SafeAdd(UserID, true);
        }

        private void DisplayLoading()
        {
           string html = @"<html>
                            <body>
                                <table width=""100%"" height=""100%"">
                                    <tr valign=""middle"">
                                        <td align=""center"">
                                        <b>Loading...</b>
                                        </td>
                                    </tr>
                                </table>
                            </body>
                        </html>";

           Browser.SetDocNoClick(html);
        }

        public override string GetTitle(bool small)
        {
            if (small)
                return "Profile";

            if (UserID == Profiles.Core.UserID)
                return "My Profile";

            return Profiles.Core.GetName(UserID) + "'s Profile";
        }

        public override Size GetDefaultSize()
        {
            return new Size(500, 625);
        }

        public override Icon GetIcon()
        {
            return ProfileRes.IconX;
        }

        void Trust_Update(ulong key)
        {
            // if updated node is local, or higher, refresh so motd updates
            if (key != UserID && !Core.Trust.IsHigher(UserID, key, ProjectID))
                return;

            OpProfile profile = Profiles.GetProfile(UserID);

            if (profile == null)
                return;

            Profile_Update(profile);
        }

        void Profile_Update(OpProfile profile)
        {
            // if self or in uplink chain, update profile
            List<ulong> uplinks = new List<ulong>();
            uplinks.Add(UserID);
            uplinks.AddRange(Core.Trust.GetUplinkIDs(UserID, ProjectID));

            if (!uplinks.Contains(profile.UserID))
                return;

            // get fields from profile

            // if temp/id dir exists use it
            // clear temp/id dir
            // extract files to temp dir

            // get html
            // insert fields into html

            // display

            string tempPath = Profiles.ExtractPath;


            // create if needed, clear of pre-existing data
            if (!Directory.Exists(tempPath))
                Directory.CreateDirectory(tempPath);

            // get the profile for this display
            profile = Profiles.GetProfile(UserID);

            if (profile == null)
                return;

            // not secure
            else
            {
                string[] files = Directory.GetFiles(tempPath);

                foreach (string path in files)
                    File.Delete(path);
            }

            string template = LoadProfile(Profiles, profile, tempPath, TextFields, FileFields);
            
            if (template == null)
            {
                template = @"<html>
                            <body>
                                <table width=""100%"" height=""100%"">
                                    <tr valign=""middle"">
                                        <td align=""center"">
                                        <b>Unable to Load</b>
                                        </td>
                                    </tr>
                                </table>
                            </body>
                        </html>";
            }

            string html = FleshTemplate(Profiles, profile.UserID, ProjectID, template, TextFields, FileFields);
           
            Browser.SetDocNoClick(html);
        }

        private static string LoadProfile(ProfileService service, OpProfile profile, string tempPath, Dictionary<string, string> textFields, Dictionary<string, string> fileFields)
        {
            string template = null;

            textFields.Clear();
            textFields["local_help"] = (profile.UserID == service.Core.UserID) ? "<font size=2>Right-click or click <a href='http://edit'>here</a> to Edit</font>" : "";

            if(fileFields != null)
                fileFields.Clear();
           
            if (!profile.Loaded)
                service.LoadProfile(profile.UserID);
            
            try
            {
                using (TaggedStream stream = new TaggedStream(service.GetFilePath(profile), service.Core.GuiProtocol))
                using (IVCryptoStream crypto = IVCryptoStream.Load(stream, profile.File.Header.FileKey))
                {
                    int buffSize = 4096;
                    byte[] buffer = new byte[4096];
                    long bytesLeft = profile.EmbeddedStart;
                    while (bytesLeft > 0)
                    {
                        int readSize = (bytesLeft > (long)buffSize) ? buffSize : (int)bytesLeft;
                        int read = crypto.Read(buffer, 0, readSize);
                        bytesLeft -= (long)read;
                    }

                    // load file
                    foreach (ProfileAttachment attached in profile.Attached)
                    {
                        if (attached.Name.StartsWith("template"))
                        {
                            byte[] html = new byte[attached.Size];
                            crypto.Read(html, 0, (int)attached.Size);

                            UTF8Encoding utf = new UTF8Encoding();
                            template = utf.GetString(html);
                        }

                        else if (attached.Name.StartsWith("fields"))
                        {
                            byte[] data = new byte[attached.Size];
                            crypto.Read(data, 0, (int)attached.Size);

                            int start = 0, length = data.Length;
                            G2ReadResult streamStatus = G2ReadResult.PACKET_GOOD;

                            while (streamStatus == G2ReadResult.PACKET_GOOD)
                            {
                                G2ReceivedPacket packet = new G2ReceivedPacket();
                                packet.Root = new G2Header(data);

                                streamStatus = G2Protocol.ReadNextPacket(packet.Root, ref start, ref length);

                                if (streamStatus != G2ReadResult.PACKET_GOOD)
                                    break;

                                if (packet.Root.Name == ProfilePacket.Field)
                                {
                                    ProfileField field = ProfileField.Decode(packet.Root);

                                    if (field.Value == null)
                                        continue;

                                    if (field.FieldType == ProfileFieldType.Text)
                                        textFields[field.Name] = UTF8Encoding.UTF8.GetString(field.Value);
                                    else if (field.FieldType == ProfileFieldType.File && fileFields != null)
                                        fileFields[field.Name] = UTF8Encoding.UTF8.GetString(field.Value);
                                }
                            }
                        }

                        else if (attached.Name.StartsWith("file=") && fileFields != null)
                        {
                            string name = attached.Name.Substring(5);

                            try
                            {
                                string fileKey = null;
                                foreach (string key in fileFields.Keys)
                                    if (name == fileFields[key])
                                    {
                                        fileKey = key;
                                        break;
                                    }

                                fileFields[fileKey] = tempPath + Path.DirectorySeparatorChar + name;

                                using (FileStream extract = new FileStream(fileFields[fileKey], FileMode.CreateNew, FileAccess.Write))
                                {
                                    long remaining = attached.Size;
                                    byte[] buff = new byte[2096];

                                    while (remaining > 0)
                                    {
                                        int read = (remaining > 2096) ? 2096 : (int)remaining;
                                        remaining -= read;

                                        crypto.Read(buff, 0, read);
                                        extract.Write(buff, 0, read);
                                    }
                                }
                            }
                            catch
                            { }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            return template;
        }

        public static string FleshTemplate(ProfileService service, ulong id, uint project, string template, Dictionary<string, string> textFields, Dictionary<string, string> fileFields)
        {
            string final = template;

            // get link
            OpLink link = service.Core.Trust.GetLink(id, project);

            if (link == null)
                return "";


            // replace fields
            while (final.Contains("<?"))
            {
                // get full tag name
                int start = final.IndexOf("<?");
                int end = final.IndexOf("?>");

                if (end == -1)
                    break;

                string fulltag = final.Substring(start, end + 2 - start);
                string tag = fulltag.Substring(2, fulltag.Length - 4);

                string[] parts = tag.Split(new char[] { ':' });

                bool tagfilled = false;

                if (parts.Length == 2)
                {

                    if (parts[0] == "text" && textFields.ContainsKey(parts[1]))
                    {
                        final = final.Replace(fulltag, textFields[parts[1]]);
                        tagfilled = true;
                    }

                    else if (parts[0] == "file" && fileFields != null && fileFields.ContainsKey(parts[1]) )
                    {
                        string path = fileFields[parts[1]];

                        if (File.Exists(path))
                        {
                            path = "file:///" + path;
                            path = path.Replace(Path.DirectorySeparatorChar, '/');
                            final = final.Replace(fulltag, path);
                            tagfilled = true;
                        }
                    }
                   
                    // load default photo if none in file
                    else if (parts[0] == "file" && fileFields != null && parts[1] == "Photo")
                    {
                        string path = service.ExtractPath;

                        // create if needed, clear of pre-existing data
                        if (!Directory.Exists(path))
                            Directory.CreateDirectory(path);

                        path += Path.DirectorySeparatorChar + "DefaultPhoto.jpg";
                        ProfileRes.DefaultPhoto.Save(path);

                        if (File.Exists(path))
                        {
                            path = "file:///" + path;
                            path = path.Replace(Path.DirectorySeparatorChar, '/');
                            final = final.Replace(fulltag, path);
                            tagfilled = true;
                        }
                    }

                    else if (parts[0] == "link" && link != null)
                    {
                        tagfilled = true;

                        if (parts[1] == "name")
                            final = final.Replace(fulltag, service.Core.GetName(id));

                        else if (parts[1] == "title")
                        {
                            if (link.Uplink != null && link.Uplink.Titles.ContainsKey(id))
                                final = final.Replace(fulltag, link.Uplink.Titles[id]);
                            else
                                final = final.Replace(fulltag, "");
                        }
                        else
                            tagfilled = false;
                    }

                    else if (parts[0] == "motd")
                    {
                        if (parts[1] == "start")
                        {
                            string motd = FleshMotd(service, template, link.UserID, project);

                            int startMotd = final.IndexOf("<?motd:start?>");
                            int endMotd = final.IndexOf("<?motd:end?>");

                            if (endMotd > startMotd)
                            {
                                endMotd += "<?motd:end?>".Length;

                                final = final.Remove(startMotd, endMotd - startMotd);

                                final = final.Insert(startMotd, motd);
                            }
                        }

                        if (parts[1] == "next")
                            return final;
                    }
                }

                if (!tagfilled)
                    final = final.Replace(fulltag, "");
            }

            return final;
        }

        private static string FleshMotd(ProfileService service, string template, ulong id, uint project)
        {
            // extract motd template
            string startTag = "<?motd:start?>";
            string nextTag = "<?motd:next?>";

            int start = template.IndexOf(startTag) + startTag.Length;
            int end = template.IndexOf("<?motd:end?>");

            if (end < start)
                return "";
             
            string motdTemplate = template.Substring(start, end - start);

            // get links in chain up
            List<ulong> uplinks = new List<ulong>();
            uplinks.Add(id);
            uplinks.AddRange(service.Core.Trust.GetUplinkIDs(id, project));     
            uplinks.Reverse();

            // build cascading motds
            string finalMotd = "";

            foreach (ulong uplink in uplinks)
            {
                OpProfile upperProfile = service.GetProfile(uplink);

                if (upperProfile != null)
                {
                    Dictionary<string, string> textFields = new Dictionary<string, string>();

                    LoadProfile(service, upperProfile, null, textFields, null);

                    string motdTag = "MOTD-" + project.ToString();
                    if(!textFields.ContainsKey(motdTag))
                        textFields[motdTag] = "No announcements";

                    textFields["MOTD"] = textFields[motdTag];

                    string currentMotd = motdTemplate;
                    currentMotd = FleshTemplate(service, uplink, project, currentMotd, textFields, null);

                    if (finalMotd == "")
                        finalMotd = currentMotd;
                    
                    else if(finalMotd.IndexOf(nextTag) != -1)
                        finalMotd = finalMotd.Replace(nextTag, currentMotd);
                }
            }

            finalMotd = finalMotd.Replace(nextTag, "");

            return finalMotd;
        }

        private void RightClickMenu_Opening(object sender, CancelEventArgs e)
        {
            if (UserID != Profiles.Core.UserID)
            {
                e.Cancel = true;
                return;
            }           
        }

        private void EditMenu_Click(object sender, EventArgs e)
        {
            EditProfile edit = new EditProfile(Profiles, this);
            edit.ShowDialog(this);
        }


        public uint GetProjectID()
        {
            return ProjectID; // call is a MarshalByRefObject so cant access value types directly
        }

        private void Browser_Navigating(object sender, WebBrowserNavigatingEventArgs e)
        {
            string url = e.Url.OriginalString;

            if (GuiUtils.IsRunningOnMono() && url.StartsWith("wyciwyg"))
                return;

            if (url.StartsWith("about:blank"))
                return;

            url = url.Replace("http://", "");
            url = url.TrimEnd('/');

            string[] command = url.Split('/');

            if (command.Length > 0)
                if (command[0] == "edit")
                    EditMenu.PerformClick();

            e.Cancel = true;
        }
    }
}
