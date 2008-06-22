using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

using RiseOp.Interface;

namespace RiseOp.Services.Trust
{
    internal partial class RemoveLinks : CustomIconForm
    {
        List<ulong> PersonIDs;

        internal List<ulong> RemoveIDs = new List<ulong>();

        internal RemoveLinks(TrustService links, List<ulong> ids)
        {
            InitializeComponent();

            PersonIDs = ids;

            foreach (ulong id in PersonIDs)
                PeopleList.Items.Add(new RemoveItem(id, links.GetName(id)));
        }

        private void TheCancelButton_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void RemoveButton_Click(object sender, EventArgs e)
        {
            foreach (RemoveItem item in PeopleList.SelectedItems)
                RemoveIDs.Add(item.ID);

            DialogResult = DialogResult.OK;

            Close();
        }
    }

    internal class RemoveItem
    {
        internal ulong ID;
        internal string Name;

        internal RemoveItem(ulong id, string name)
        {
            ID = id;
            Name = name;
        }

        public override string ToString()
        {

            return Name;
        }
    }
}