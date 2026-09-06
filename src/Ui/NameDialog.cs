using System;
using System.Drawing;
using System.Windows.Forms;

namespace NostalgiaPlus.Ui
{
    /// <summary>
    /// Minimal single-line prompt for naming a preset.
    ///
    /// Written by hand rather than pulling in Microsoft.VisualBasic just for InputBox:
    /// the plugin otherwise has no dependencies beyond the framework assemblies it
    /// already needs, and that is worth keeping.
    /// </summary>
    public static class NameDialog
    {
        /// <summary>Returns the entered text, or null if cancelled or left empty.</summary>
        public static string Ask(IWin32Window owner, string title, string prompt, string initial)
        {
            using (var form = new Form())
            using (var label = new Label())
            using (var box = new TextBox())
            using (var ok = new Button())
            using (var cancel = new Button())
            {
                form.Text = title;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterParent;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ShowInTaskbar = false;
                form.ClientSize = new Size(340, 116);

                label.SetBounds(12, 12, 316, 18);
                label.Text = prompt;

                box.SetBounds(12, 34, 316, 24);
                box.Text = initial ?? "";
                box.SelectAll();

                ok.SetBounds(160, 74, 80, 28);
                ok.Text = "Save";
                ok.DialogResult = DialogResult.OK;

                cancel.SetBounds(248, 74, 80, 28);
                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;

                form.Controls.Add(label);
                form.Controls.Add(box);
                form.Controls.Add(ok);
                form.Controls.Add(cancel);
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                if (form.ShowDialog(owner) != DialogResult.OK) return null;
                string text = box.Text.Trim();
                return text.Length == 0 ? null : text;
            }
        }
    }
}
