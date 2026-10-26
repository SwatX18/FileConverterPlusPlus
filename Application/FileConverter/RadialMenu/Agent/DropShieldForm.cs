// <copyright file="DropShieldForm.cs" company="AAllard">License: http://www.gnu.org/licenses/gpl.html GPL version 3.</copyright>

namespace FileConverter.RadialMenu.Agent
{
    using System;
    using System.Drawing;
    using System.Windows.Forms;

    /// <summary>
    /// Near transparent topmost window covering one screen while a Ctrl+drag is in progress. It is an OLE drop target that always
    /// answers "no drop": the files can't be dropped on anything below, and its DragEnter tells us a file drag is in progress.
    /// </summary>
    internal sealed class DropShieldForm : Form
    {
        public DropShieldForm(Rectangle screenBounds, Action<IDataObject> onDragEnter, Action onStaleClick)
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.Opacity = 0.004;
            this.AllowDrop = true;
            this.StartPosition = FormStartPosition.Manual;
            this.Bounds = screenBounds;
            this.BackColor = Color.Black;

            this.DragEnter += (sender, e) =>
            {
                e.Effect = DragDropEffects.None;
                onDragEnter(e.Data);
            };
            this.DragOver += (sender, e) => e.Effect = DragDropEffects.None;

            // During a real drag (or a rubber band selection) Explorer holds the mouse capture: a click reaching a shield means
            // the button-up of the drag was missed and the shield is stuck.
            this.MouseDown += (sender, e) => onStaleClick();
            this.MouseUp += (sender, e) => onStaleClick();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams createParams = base.CreateParams;
                createParams.ExStyle |= AgentNativeMethods.WS_EX_NOACTIVATE | AgentNativeMethods.WS_EX_TOOLWINDOW;
                return createParams;
            }
        }
    }
}
