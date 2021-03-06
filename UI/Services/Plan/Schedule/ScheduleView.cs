using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;


using DeOps.Implementation;

using DeOps.Interface;
using DeOps.Interface.TLVex;
using DeOps.Interface.Views;

using DeOps.Services.Assist;
using DeOps.Services.Trust;


namespace DeOps.Services.Plan
{
    public partial class ScheduleView : ViewShell
    {
        public CoreUI UI;
        public OpCore Core;
        public PlanService Plans;
        TrustService Trust;

        public ulong UserID;
        public uint  ProjectID;

        public DateTime StartTime;
        public DateTime EndTime;

        public Dictionary<ulong, PlanNode> NodeMap = new Dictionary<ulong, PlanNode>();
        public List<ulong> Uplinks = new List<ulong>();

        public PlanBlock SelectedBlock;
        public int SelectedGoalID;

        ToolTip  HoverTip = new ToolTip();
        Point    HoverPos = new Point();
        BlockRow HoverBlock;
        int      HoverTicks;
        string   HoverText;

        public int LoadGoal;
        public int LoadGoalBranch;

        StringBuilder Details = new StringBuilder(4096);
        const string DefaultPage = @"<html>
                                    <head>
                                    <style>
                                        body { font-family:tahoma; font-size:12px;margin-top:3px;}
                                        td { font-size:10px;vertical-align: middle; }
                                    </style>
                                    </head>

                                    <body bgcolor=#f5f5f5>

                                        

                                    </body>
                                    </html>";

        const string BlockPage = @"<html>
                                    <head>
                                    <style>
                                        body { font-family:tahoma; font-size:12px;margin-top:3px;}
                                        td { font-size:10px;vertical-align: middle; }
                                    </style>

                                    <script>
                                        function SetElement(id, text)
                                        {
                                            document.getElementById(id).innerHTML = text;
                                        }
                                    </script>
                                    
                                    </head>

                                    <body bgcolor=#f5f5f5>

                                        <br>
                                        <b><u><span id='title'><?=title?></span></u></b><br>
                                        <br>
                                        <b>Start</b><br>
                                        <span id='start'><?=start?></span><br>
                                        <br>
                                        <b>Finish</b><br>
                                        <span id='finish'><?=finish?></span><br>
                                        <br>
                                        <b>Notes</b><br>
                                        <span id='notes'><?=notes?></span>

                                    </body>
                                    </html>";

        public ScheduleView(CoreUI ui, PlanService plans, ulong id, uint project)
        {
            InitializeComponent();

            UI = ui;
            Core = ui.Core;
            Plans = plans;
            Trust = Core.Trust;

            UserID = id;
            ProjectID = project;

            StartTime = Core.TimeNow;
            EndTime   = Core.TimeNow.AddMonths(3);

            GuiUtils.SetupToolstrip(TopStrip, new OpusColorTable());

            MainSplit.Panel2Collapsed = true;

            Core.KeepDataGui += new KeepDataHandler(Core_KeepData);

            PlanStructure.NodeExpanding += new EventHandler(PlanStructure_NodeExpanding);
            PlanStructure.NodeCollapsed += new EventHandler(PlanStructure_NodeCollapsed);

            // set last block so that setdetails shows correctly
            LastBlock = new PlanBlock();
            SetDetails(null);
        }

        public override string GetTitle(bool small)
        {
            if (small)
                return "Schedule";

            string title = "";

            if (UserID == Core.UserID)
                title += "My ";
            else
                title += Core.GetName(UserID) + "'s ";

            if (ProjectID != 0)
                title += Trust.GetProjectName(ProjectID) + " ";

            title += "Schedule";

            return title;
        }

        public override void Init()
        {
            if (UserID != Core.UserID)
                NewButton.Visible = false;

            PlanStructure.Columns[1].WidthResized += new EventHandler(PlanStructure_Resized);

            ScheduleSlider.Init(this);

            DateRange.Value = 40;
            UpdateRange();

            GotoTime(Core.TimeNow);

            // guilty of a hack, this sets the last column to the correct length, 
            // firing the event to set the slider to the same size as the column
            PlanStructure.GenerateColumnRects();
            PlanStructure.Invalidate();


            // load links
            RefreshUplinks();
            RefreshStructure();


            // events
            Trust.GuiUpdate += new LinkGuiUpdateHandler(Trust_Update);
            Plans.PlanUpdate += new PlanUpdateHandler(Plans_Update);
        }

        private void ScheduleView_Load(object sender, EventArgs e)
        {
            RefreshGoalCombo();

            foreach(GoalComboItem item in GoalCombo.Items)
                if (item.ID == LoadGoal)
                {
                    GoalCombo.SelectedItem = item;
                    break;
                }
        }

        private void GotoTime(DateTime time)
        {
            long ticks = ScheduleSlider.Width * ScheduleSlider.TicksperPixel;

            StartTime = time.AddTicks(-ticks * 1/4);
            EndTime   = time.AddTicks(ticks * 3/4);

            ScheduleSlider.RefreshSlider();
        }

        public override bool Fin()
        {
            bool save = false;

            if (SaveButton.Visible)
            {
                DialogResult result = MessageBox.Show(this, "Save Chages to Schedule?", "DeOps", MessageBoxButtons.YesNoCancel);

                if (result == DialogResult.Yes)
                    save = true;
                if (result == DialogResult.Cancel)
                    return false;
            }

            Trust.GuiUpdate -= new LinkGuiUpdateHandler(Trust_Update);
            Plans.PlanUpdate -= new PlanUpdateHandler(Plans_Update);

            Core.KeepDataGui -= new KeepDataHandler(Core_KeepData);

            HoverTimer.Enabled = false;

            if(save)
                Plans.SaveLocal();

            return true;
        }

        public override Size GetDefaultSize()
        {
            return new Size(475, 325);
        }

        public override Icon GetIcon()
        {
            return PlanRes.Schedule;
        }

        void PlanStructure_Resized(object sender, EventArgs args)
        {
            LabelPlus.Location = new Point(PlanStructure.Columns[0].Width - 30, LabelPlus.Location.Y);

            DateRange.Width = LabelPlus.Location.X + 1 - DateRange.Location.X;
            
            ScheduleSlider.Location = new Point( MainSplit.Panel1.Width - PlanStructure.Columns[1].Width, ScheduleSlider.Location.Y);
            ScheduleSlider.Width = PlanStructure.Columns[1].Width;

            ExtendedLabel.Location = new Point(ScheduleSlider.Location.X, ExtendedLabel.Location.Y);
            ExtendedLabel.Width = ScheduleSlider.Width;

            ExtendedLabel.Update();
            LabelPlus.Update();
        }

        private void DateRange_Scroll(object sender, EventArgs e)
        {
            UpdateRange();
        }

        private void UpdateRange()
        {
            /*
                            tick	hours
            quarter day	    1	    6
            day	            2	    24
            week	        3	    168
            month	        4	    672
            quarter year	5	    2016
            year	        6	    8064
            5 years	        7	    40320
         
            exponential fit,  tick = 1.592 * e ^ (1.4485 * hours)
            */

            double x = DateRange.Maximum - DateRange.Value;
            x /= 20;

            double hours = 1.592 * Math.Exp(1.4485 * x);

            //EndTime = StartTime.AddHours(hours);

            DateTime fourthTime = new DateTime(StartTime.Ticks + (EndTime.Ticks-StartTime.Ticks) / 4);

            StartTime = fourthTime.AddHours(-hours * 1 / 4);
            EndTime = fourthTime.AddHours(hours * 3 / 4);
            
            ScheduleSlider.RefreshSlider();
        }

        private void RefreshStructure()
        {
            PlanStructure.BeginUpdate();


            // save selected
            PlanNode selected = GetSelected();

            // save visible while unloading
            List<ulong> visible = new List<ulong>();
            foreach (TreeListNode node in PlanStructure.Nodes)
                if (node.GetType() == typeof(PlanNode))
                    UnloadNode((PlanNode)node, visible);


            NodeMap.Clear();
            PlanStructure.Nodes.Clear();

     
            // nodes
            ThreadedList<OpLink> roots = null;
            if (Trust.ProjectRoots.SafeTryGetValue(ProjectID, out roots))
                roots.LockReading(delegate()
                {
                    foreach (OpLink root in roots)
                    {
                        if (Uplinks.Contains(root.UserID))
                        {
                            PlanNode node = CreateNode(root);

                            Plans.Research(root.UserID);

                            LoadNode(node);

                            GuiUtils.InsertSubNode(PlanStructure.virtualParent, node);

                            ExpandPath(node, Uplinks);
                        }

                        if (root.IsLoopRoot &&
                            root.Downlinks.Count > 0 &&
                            Uplinks.Contains(root.Downlinks[0].UserID))
                        {
                            foreach (OpLink downlink in root.Downlinks)
                                if (!root.IsLoopedTo(downlink))
                                {
                                    PlanNode node = CreateNode(downlink);

                                    Plans.Research(downlink.UserID);

                                    LoadNode(node);

                                    GuiUtils.InsertSubNode(PlanStructure.virtualParent, node);

                                    ExpandPath(node, Uplinks);
                                }
                        }
                    }
                });

            // restore visible
            foreach (ulong id in visible)
                foreach (TreeListNode node in PlanStructure.Nodes)
                    if (node.GetType() == typeof(PlanNode))
                    {
                        List<ulong> uplinks = Trust.GetUnconfirmedUplinkIDs(id, ProjectID);
                        uplinks.Add(id);
                        VisiblePath((PlanNode)node, uplinks);
                    }

            // restore selected
            if (selected != null)
                if (NodeMap.ContainsKey(selected.Link.UserID))
                    PlanStructure.Select(NodeMap[selected.Link.UserID]);


            PlanStructure.EndUpdate();
        }

        private void LoadNode(PlanNode node)
        {
            // check if already loaded
            if (node.AddSubs)
                return;


            node.AddSubs = true;

            // go through downlinks

            OpLink[] downlinks = (from link in node.Link.Downlinks
                                  where node.Link.Confirmed.Contains(link.UserID) ||
                                        Uplinks.Contains(link.UserID)
                                  select link).ToArray();

            foreach (OpLink link in downlinks)
            {
                // if doesnt exist search for it
                if (!link.Trust.Loaded)
                {
                    Trust.Research(link.UserID, ProjectID, false);
                    continue;
                }

                Plans.Research(link.UserID);

                GuiUtils.InsertSubNode(node, CreateNode(link));
            }
        }

        private PlanNode CreateNode(OpLink link)
        {
            PlanNode node = new PlanNode(this, link, link.UserID == UserID);

            NodeMap[link.UserID] = node;

            return node;
        }

        private void ExpandPath(PlanNode node, List<ulong> path)
        {
            if (!path.Contains(node.Link.UserID))
                return;

            // expand triggers even loading nodes two levels down, one level shown, the other hidden
            node.Expand();

            foreach (PlanNode sub in node.Nodes)
                ExpandPath(sub, path);
        }

        void Trust_Update(ulong key)
        {
            // copied from linkTree's source
            OpLink link = Trust.GetLink(key, ProjectID);

            if (link == null)
            {
                if (NodeMap.ContainsKey(key))
                    RemoveNode(NodeMap[key]);

                return;
            }

            /*above should do this now
             * if (!link.Projects.Contains(ProjectID) && !link.Downlinks.ContainsKey(ProjectID))
            {
                if (NodeMap.ContainsKey(key))
                    RemoveNode(NodeMap[key]);

                return;
            }*/

            PlanNode node = null;

            if (NodeMap.ContainsKey(key))
                node = NodeMap[key];

            TreeListNode parent = null;
            OpLink uplink = GetTreeHigher(link);

            if (uplink == null)
                parent = PlanStructure.virtualParent;

            else if (NodeMap.ContainsKey(uplink.UserID))
                parent = NodeMap[uplink.UserID];

            else if (uplink.IsLoopRoot)
                parent = new TreeListNode(); // ensures that tree is refreshed

            // if nodes status unchanged
            if (node != null && parent != null && node.Parent == parent)
            {
                node.UpdateName();
                Invalidate();
                return;
            }

            // only if parent is visible
            if (parent != null)
            {
                RefreshUplinks();
                RefreshStructure();
            }
            
            ////////////////////////////////////////////////////////////////////
            /*
            // update uplinks
            if (key == DhtID || Uplinks.Contains(key))
                RefreshUplinks();


            // create a node item, or get the current one
            PlanNode node = null;

            if (NodeMap.ContainsKey(key))
                node = NodeMap[key];
            else
                node = new PlanNode(this, link, key == DhtID);


            // get the right parent node for this item
            TreeListNode parent = null;

            OpLink parentLink = link.GetHigher(ProjectID, false);

            if (parentLink == null) // dont combine below, causes next if to fail
                parent = Uplinks.Contains(key) ? PlanStructure.virtualParent : null;

            else if (NodeMap.ContainsKey(parentLink.DhtID))
                parent = NodeMap[parentLink.DhtID];

            else
                parent = null; // branch this link is apart of is not visible in current display


            // remember settings
            bool selected = node.Selected;


            if (node.Parent != parent)
            {
                List<ulong> visible = new List<ulong>();

                // remove previous instance of node
                if (node.Parent != null)
                {
                    if (node.IsVisible())
                        visible.Add(link.DhtID);

                    UnloadNode(node, visible);
                    NodeMap.Remove(link.DhtID);
                    node.Remove();
                }


                // if node changes to be sub of another root 3 levels down, whole branch must be reloaded
                if (parent == null || parent == PlanStructure.virtualParent)
                {
                    if(Uplinks.Contains(key))
                        RefreshStructure();
                    
                    return;
                }

                // if new parent is hidden, dont bother adding till user expands
                PlanNode newParent = parent as PlanNode; // null if root

                if (newParent != null && newParent.AddSubs == false)
                    return;


                // copy node to start fresh
                PlanNode newNode = CreateNode(node.Link);


                // check if parent should be moved to project header
                if (newParent != null)
                {
                    Utilities.InsertSubNode(newParent, newNode);

                    // if we are a visible child, must load hidden sub nodes
                    if (newParent.IsVisible() && newParent.IsExpanded)
                        LoadNode(newNode);
                }

                // if node itself is the root
                else
                {
                    LoadNode(newNode);

                    // remove previous
                    foreach (PlanNode old in PlanStructure.Nodes)
                        UnloadNode(old, visible);

                    PlanStructure.Nodes.Clear();
                    PlanStructure.Nodes.Add(newNode);
                }

                node = newNode;


                // recurse to each previously visible node
                foreach (ulong id in visible)
                {
                    List<ulong> uplinks = Links.GetUplinkIDs(id, ProjectID);

                    foreach (PlanNode root in PlanStructure.Nodes) // should only be one root
                        VisiblePath(root, uplinks);
                }
            }

            node.UpdateName();


            node.Selected = selected;*/
        }

        private OpLink GetTreeHigher(OpLink link)
        {
            if (link.LoopRoot != null)
                return link.LoopRoot;

            return link.GetHigher(false);
        }


        private void RefreshUplinks()
        {
            Uplinks.Clear();
            Uplinks.Add(UserID);
            Uplinks.AddRange(Trust.GetUnconfirmedUplinkIDs(UserID, ProjectID));

            // we show unconfirmed highers, but not unconfirmed lowers
        }

        private void VisiblePath(PlanNode node, List<ulong> path)
        {
            bool found = false;

            foreach (PlanNode sub in node.Nodes)
                if (path.Contains(sub.Link.UserID))
                    found = true;

            if (found)
            {
                node.Expand();

                foreach (PlanNode sub in node.Nodes)
                    VisiblePath(sub, path);
            }
        }

        private void RemoveNode(PlanNode node)
        {
            UnloadNode(node, null); // unload subs
            NodeMap.Remove(node.Link.UserID); // remove from map
            node.Remove(); // remove from tree
        }

        void Core_KeepData()
        {
            foreach (PlanNode node in PlanStructure.Nodes)
                RecurseFocus(node);
        }

        void RecurseFocus(PlanNode node)
        {
            // add parent to focus list
            Core.KeepData.SafeAdd(node.Link.UserID, true);
            
            // iterate through sub items
            foreach (PlanNode sub in node.Nodes)
                RecurseFocus(sub);
        }

        void Plans_Update(OpPlan plan)
        {
            // if node not tracked
            if(!NodeMap.ContainsKey(plan.UserID))
                return;

            // update this node, and all subs      (visible below)
            TreeListNode node = (TreeListNode) NodeMap[plan.UserID];
            
            bool done = false;

            while (node != null && !done)
            {
                ((PlanNode)node).UpdateBlock();

                done = PlanStructure.GetNextNode(ref node);
            }

            RefreshGoalCombo();
        }

        public void RefreshRows()
        {
            TreeListNode node = (TreeListNode) PlanStructure.virtualParent.FirstChild();

            bool done = false;

            while (node != null && !done)
            {
                ((PlanNode)node).UpdateBlock();

                done = PlanStructure.GetNextNode(ref node);
            }

            SetDetails(LastBlock);
        }

        private void PlanStructure_SelectedItemChanged(object sender, EventArgs e)
        {
            RefreshRows(); // updates selection box graphics

            // children searched on expand

            PlanNode node = GetSelected();

            if (node == null)
            {
                SetDetails(null);
                return;
            }
            // link research
            Trust.Research(node.Link.UserID, ProjectID, false);

            // plan research
            Plans.Research(node.Link.UserID);
        }

        PlanNode GetSelected()
        {
            if (PlanStructure.SelectedNodes.Count == 0)
                return null;

            PlanNode node = (PlanNode) PlanStructure.SelectedNodes[0];

            return node;
        }

        private void PlanStructure_Enter(object sender, EventArgs e)
        {
            RefreshRows(); // updates selection box graphics
        }

        private void PlanStructure_Leave(object sender, EventArgs e)
        {
            RefreshRows(); // updates selection box graphics
        }

        void PlanStructure_NodeExpanding(object sender, EventArgs e)
        {
            PlanNode node = sender as PlanNode;

            if (node == null)
                return;

            Debug.Assert(node.AddSubs);

            // node now expanded, get next level below children
            foreach (PlanNode child in node.Nodes)
                LoadNode(child);
        }


        void PlanStructure_NodeCollapsed(object sender, EventArgs e)
        {
            PlanNode node = sender as PlanNode;

            if (node == null)
                return;

            if (!node.AddSubs) // this node is already collapsed
                return;

            // remove nodes 2 levels down
            foreach (PlanNode child in node.Nodes)
                UnloadNode(child, null);

            Debug.Assert(node.AddSubs); // this is the top level, children hidden underneath
        }

        private void UnloadNode(PlanNode node, List<ulong> visible)
        {
            node.AddSubs = false;

            if (visible != null && node.IsVisible())
                visible.Add(node.Link.UserID);

            // for each child, call unload node, then clear
            foreach (PlanNode child in node.Nodes)
            {
                if (NodeMap.ContainsKey(child.Link.UserID))
                    NodeMap.Remove(child.Link.UserID);

                UnloadNode(child, visible);
            }

            // unloads children of node, not the node itself
            node.Nodes.Clear();
            node.Collapse();
        }

        private void HoverTimer_Tick(object sender, EventArgs e)
        {
            // if mouse in same block at same position for 5 ticks (2,5 seconds) display tool tip, else hide tool tip

            if ( !Cursor.Position.Equals(HoverPos) )
            {
                HoverTicks = 0;
                HoverPos   = new Point(0, 0);
                HoverBlock = null;
                HoverText  = null;
                HoverTip.Hide(this);
                return;
            }

            if (HoverTicks < 1)
            {
                HoverTicks++;
                return;
            }

            if (HoverText == null && HoverBlock != null)
            {
                HoverText = HoverBlock.GetHoverText();

                HoverTip.Show(HoverText, this, PointToClient(Cursor.Position));
            }
        }

        public void CursorUpdate(BlockRow block)
        {
            // test block because control can scroll without mouse moving
            if (Cursor.Position.Equals(HoverPos) && block == HoverBlock) 
                return;

            HoverTicks = 0;
            HoverPos   = Cursor.Position;
            HoverBlock = block;
            HoverText  = null;
            HoverTip.Hide(this);
        }

        private void SaveButton_Click(object sender, EventArgs e)
        {
            SaveButton.Visible = false;
            DiscardButton.Visible = false;

            PlanStructure.Height = MainSplit.Panel1.Height - PlanStructure.Top;

            Plans.SaveLocal();
        }

        private void DiscardButton_Click(object sender, EventArgs e)
        {
            SaveButton.Visible = false;
            DiscardButton.Visible = false;

            PlanStructure.Height = MainSplit.Panel1.Height - PlanStructure.Top;

            Plans.LoadPlan(Core.UserID);
            Plans_Update(Plans.LocalPlan);
        }

        public void ChangesMade()
        {
            SaveButton.Visible = true;
            DiscardButton.Visible = true;

            int height = MainSplit.Panel1.Height;
            PlanStructure.Height = height - PlanStructure.Top - SaveButton.Height - 8;


            if (GuiUtils.IsRunningOnMono())
            {
                // buttons aren't positioned when they aren't visible
                SaveButton.Location = new Point(Width - 156, height - 22);
                DiscardButton.Location = new Point(Width - 86, height - 22);
            }
          
            SaveButton.BringToFront();
            DiscardButton.BringToFront();
        }


        private void RefreshGoalCombo()
        {
            GoalComboItem prevItem = GoalCombo.SelectedItem as GoalComboItem;

            int prevSelectedID = 0;
            if (prevItem != null)
                prevSelectedID = prevItem.ID;

            GoalCombo.Items.Clear();

            GoalCombo.Items.Add(new GoalComboItem("None", 0));

            // go up the chain looking for goals which have been assigned to this person
            // at root goal is the title of the goal


            List<PlanGoal> rootList = new List<PlanGoal>();
            List<int> assigned = new List<int>();

            // foreach self & higher
            List<ulong> ids = Trust.GetUplinkIDs(UserID, ProjectID);
            ids.Add(UserID);

            foreach (ulong id in ids)
            {
                OpPlan plan = Plans.GetPlan(id, true);

                if (plan == null)
                    continue;

                // goals we have been assigned to
                foreach (List<PlanGoal> list in plan.GoalMap.Values)
                    foreach (PlanGoal goal in list)
                    {
                        if (goal.Project != ProjectID)
                            break;

                        if (goal.Person == UserID && !assigned.Contains(goal.Ident))
                            assigned.Add(goal.Ident);

                        if (goal.BranchDown == 0)
                            if (!goal.Archived)
                                rootList.Add(goal);
                    }
            }

            // update combo
            GoalComboItem prevSelected = null;

            foreach (PlanGoal goal in rootList)
                if (assigned.Contains(goal.Ident))
                {
                    GoalComboItem item = new GoalComboItem(goal.Title, goal.Ident);

                    if (goal.Ident == prevSelectedID)
                        prevSelected = item;

                    GoalCombo.Items.Add(item);
                }

            if (prevSelected != null)
                GoalCombo.SelectedItem = prevSelected;
            else
                GoalCombo.SelectedIndex = 0;
        }

        private void NewButton_Click(object sender, EventArgs e)
        {
            EditBlock form = new EditBlock(BlockViewMode.New, this, null);

            if (form.ShowDialog(this) == DialogResult.OK)
            {
                ChangesMade();
                Plans_Update(Plans.LocalPlan);
            }
        }

        private void NowButton_Click(object sender, EventArgs e)
        {
            GotoTime(Core.TimeNow);
        }

        private void GoalCombo_SelectedIndexChanged(object sender, EventArgs e)
        {
            GoalComboItem item = GoalCombo.SelectedItem as GoalComboItem;

            if (item == null)
                return;

            SelectedGoalID = item.ID;

            RefreshRows();
        }

        private void DetailsButton_Click(object sender, EventArgs e)
        {
            MainSplit.Panel2Collapsed = !DetailsButton.Checked;

            if (DetailsButton.Checked)
                DetailsButton.Image = PlanRes.details2;
            else
                DetailsButton.Image = PlanRes.details1;
        }


        PlanBlock LastBlock;

        public void SetDetails(PlanBlock block)
        {
            List<string[]> tuples = new List<string[]>();

            string notes = null;
 
            // get inof that needs to be set
            if (block != null)
            {
                tuples.Add(new string[] { "title",  block.Title });
                tuples.Add(new string[] { "start",  block.StartTime.ToLocalTime().ToString("D") });
                tuples.Add(new string[] { "finish", block.EndTime.ToLocalTime().ToString("D") });
                tuples.Add(new string[] { "notes", block.Description.Replace("\r\n", "<br>") });

                notes = block.Description;
            }

            // set details button
            DetailsButton.ForeColor = Color.Black;

            if (MainSplit.Panel2Collapsed && notes != null && notes != "")
                DetailsButton.ForeColor = Color.Red;


            if (LastBlock != block)
            {
                Details.Length = 0;

                if (block != null)
                    Details.Append(BlockPage);
                else
                    Details.Append(DefaultPage);

                foreach (string[] tuple in tuples)
                    Details.Replace("<?=" + tuple[0] + "?>", tuple[1]);

                SetDisplay(Details.ToString());
            }
            else
            {
                foreach (string[] tuple in tuples)
                    DetailsBrowser.SafeInvokeScript("SetElement", new String[] { tuple[0], tuple[1] });
            }

            LastBlock = block;
        }

        private void SetDisplay(string html)
        {
            Debug.Assert(!html.Contains("<?"));

            //if (!DisplayActivated)
            //    return;

            // watch transfers runs per second, dont update unless we need to 
            if (html.CompareTo(DetailsBrowser.DocumentText) == 0)
                return;

            // prevents clicking sound when browser navigates
            DetailsBrowser.SetDocNoClick( html);
        }

        // call is a MarshalByRefObject so cant access value types directly
        public DateTime GetStartTime()
        {
            return StartTime;
        }

        public DateTime GetEndTime()
        {
            return EndTime;
        }


    }


    public class GoalComboItem
    {
        public string Name;
        public int ID;

        public GoalComboItem(string name, int id)
        {
            Name = name;
            ID = id;
        }

        public override string ToString()
        {
            return Name;
        }
    }


    public class PlanNode : TreeListNode
    {
        public ScheduleView View;
        public OpLink Link;
        public bool AddSubs;

        public PlanNode( ScheduleView view, OpLink link, bool local)
        {
            View = view;
            Link = link;

            if (local)
                Font = new System.Drawing.Font("Tahoma", 8.25F, System.Drawing.FontStyle.Bold);

            SubItems.Add(new BlockRow(this));

            UpdateName();
            UpdateBlock();
        }

        public void UpdateName()
        {
            ulong id = Link.UserID;

            if (Link.Uplink != null && Link.Uplink.Titles.ContainsKey(id))
                Text = View.Core.GetName(id) + "\n" + Link.Uplink.Titles[id];
            else
                Text = View.Core.GetName(id);
        }

        public void UpdateBlock()
        {
            ((BlockRow)SubItems[0].ItemControl).UpdateRow(true);
        }

        public override string ToString()
        {
            return Text;
        }
    }

}