using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Voltide.UI
{
    internal sealed class BatteryAlertForm : Form
    {
        private readonly ListBox _messages = new ListBox();
        private readonly Dictionary<string, int> _rows = new Dictionary<string, int>();

        public BatteryAlertForm()
        {
            Text = "Voltide · 低电量提醒";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(320, 100);
            _messages.Dock = DockStyle.Fill;
            _messages.HorizontalScrollbar = true;
            _messages.IntegralHeight = false;
            Controls.Add(_messages);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public void SetReading(string id, string name, int percent)
        {
            string message = name + "  ·  剩余 " + percent + "%";
            int row;
            if (_rows.TryGetValue(id, out row)) _messages.Items[row] = message;
            else _rows.Add(id, _messages.Items.Add(message));
        }

        public void ShowNearTray()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(Math.Max(area.Left, area.Right - Width - 12),
                                 Math.Max(area.Top, area.Bottom - Height - 12));
            Show();
            Location = new Point(Math.Max(area.Left, area.Right - Width - 12),
                                 Math.Max(area.Top, area.Bottom - Height - 12));
        }
    }
}
