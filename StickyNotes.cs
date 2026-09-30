using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

class StickyNotes : ApplicationContext
{
    public class NoteData
    {
        public string Id, Text;
        public int X, Y, Width, Height, Color;
        public bool TopMost;
    }

    public static readonly Color[] Colors =
    {
        Color.FromArgb(255, 242, 157), Color.FromArgb(197, 235, 190), Color.FromArgb(190, 222, 250),
        Color.FromArgb(255, 204, 229), Color.FromArgb(221, 205, 255), Color.FromArgb(235, 235, 235)
    };

    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StickyNotes");
    static readonly string FilePath = Path.Combine(Folder, "notes.json");

    readonly List<NoteForm> notes = new List<NoteForm>();
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer saveTimer;

    StickyNotes()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Новая заметка", null, (s, e) => NewNote());
        menu.Items.Add("Показать все", null, (s, e) => { foreach (var n in notes) { n.Show(); n.Activate(); } });
        menu.Items.Add("Открыть папку с заметками", null, (s, e) => System.Diagnostics.Process.Start("explorer.exe", Folder));
        var autostart = new ToolStripMenuItem("Запускать вместе с Windows") { Checked = Autostart };
        autostart.Click += (s, e) => { Autostart = !Autostart; autostart.Checked = Autostart; };
        menu.Items.Add(autostart);
        menu.Items.Add("Выход", null, (s, e) => { SaveNow(); ExitThread(); });
        Microsoft.Win32.SystemEvents.SessionEnding += (s, e) => SaveNow();
        tray = new NotifyIcon { Icon = SystemIcons.Application, Visible = true, ContextMenuStrip = menu, Text = "StickyNotes: двойной клик — новая заметка" };
        tray.DoubleClick += (s, e) => NewNote();

        saveTimer = new System.Windows.Forms.Timer { Interval = 800 };
        saveTimer.Tick += (s, e) => { saveTimer.Stop(); Save(); };

        foreach (var d in Load()) Open(d);
        if (notes.Count == 0)
            Open(new NoteData
            {
                Text = "Привет! Это заметка.\r\n\r\n＋ — новая заметка\r\n● — сменить цвет\r\n📌 — поверх всех окон\r\n× — удалить\r\n\r\nВсё сохраняется само.",
                X = Screen.PrimaryScreen.WorkingArea.Right - 300, Y = 60, Width = 260, Height = 260
            });
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static bool Autostart
    {
        get { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("StickyNotes") != null; }
        set
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                if (value) k.SetValue("StickyNotes", "\"" + Application.ExecutablePath + "\""); else k.DeleteValue("StickyNotes", false);
        }
    }

    public void SaveNow()
    {
        saveTimer.Stop();
        Save();
    }

    public void Changed()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    public NoteForm NewNote(NoteForm near = null)
    {
        var area = Screen.PrimaryScreen.WorkingArea;
        var d = new NoteData { Text = "", Width = 260, Height = 240, Color = near != null ? near.Data.Color : 0 };
        if (near != null) { d.X = near.Left + 30; d.Y = near.Top + 30; }
        else { d.X = area.Left + area.Width / 2 - 130 + notes.Count * 20 % 200; d.Y = area.Top + 120 + notes.Count * 20 % 200; }
        var form = Open(d);
        form.Activate();
        form.FocusText();
        Changed();
        return form;
    }

    NoteForm Open(NoteData d)
    {
        if (d.Id == null) d.Id = Guid.NewGuid().ToString("N");
        var form = new NoteForm(this, d);
        form.FormClosed += (s, e) => { notes.Remove(form); if (form.Deleted) Changed(); };
        notes.Add(form);
        form.Show();
        return form;
    }

    static List<NoteData> Load()
    {
        try
        {
            if (File.Exists(FilePath)) return new JavaScriptSerializer().Deserialize<List<NoteData>>(File.ReadAllText(FilePath));
        }
        catch
        {
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
        }
        return new List<NoteData>();
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var data = notes.Where(n => !n.Deleted).Select(n => n.Snapshot()).ToList();
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(data));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
        }
        catch { }
    }

    protected override void ExitThreadCore()
    {
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }

    public class NoteForm : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public readonly NoteData Data;
        public bool Deleted;
        readonly StickyNotes app;
        readonly TextBox text;
        readonly Panel header;
        readonly Label pin;

        public NoteForm(StickyNotes app, NoteData data)
        {
            this.app = app;
            Data = data;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            MinimumSize = new Size(160, 120);
            Bounds = new Rectangle(data.X, data.Y, Math.Max(160, data.Width), Math.Max(120, data.Height));
            TopMost = data.TopMost;
            Padding = new Padding(1);
            KeepOnScreen();

            header = new Panel { Dock = DockStyle.Top, Height = 28 };
            header.MouseDown += (s, e) => Drag(e);
            Controls.Add(header);

            header.Controls.Add(HeaderButton("●", "Сменить цвет", DockStyle.Right, (s, e) => { Data.Color = (Data.Color + 1) % Colors.Length; UpdateColors(); app.Changed(); }));
            pin = HeaderButton("📌", "Поверх всех окон", DockStyle.Right, (s, e) => { TopMost = !TopMost; Data.TopMost = TopMost; UpdateColors(); app.Changed(); });
            header.Controls.Add(pin);
            header.Controls.Add(HeaderButton("×", "Удалить", DockStyle.Right, (s, e) => Delete()));
            header.Controls.Add(HeaderButton("＋", "Новая заметка", DockStyle.Left, (s, e) => app.NewNote(this)));

            text = new TextBox
            {
                Multiline = true, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 11), Text = data.Text ?? "",
                ScrollBars = ScrollBars.None, AcceptsTab = true, Location = new Point(12, 34)
            };
            text.TextChanged += (s, e) => app.Changed();
            text.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.A) { text.SelectAll(); e.SuppressKeyPress = true; }
                if (e.Control && e.KeyCode == Keys.N) { app.NewNote(this); e.SuppressKeyPress = true; }
            };
            Controls.Add(text);
            text.BringToFront();

            Resize += (s, e) => { LayoutText(); Invalidate(); };
            Move += (s, e) => app.Changed();
            ResizeEnd += (s, e) => app.Changed();
            Shown += (s, e) => text.Select(text.TextLength, 0);
            LayoutText();
            UpdateColors();
        }

        public void FocusText() { text.Focus(); }

        void KeepOnScreen()
        {
            if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(Left, Top, Width, 30))))
                Location = new Point(Screen.PrimaryScreen.WorkingArea.Left + 100, Screen.PrimaryScreen.WorkingArea.Top + 100);
        }

        Label HeaderButton(string symbol, string tip, DockStyle dock, EventHandler click)
        {
            var b = new Label { Text = symbol, Dock = dock, Width = 30, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI Symbol", 11), Cursor = Cursors.Hand };
            new ToolTip().SetToolTip(b, tip);
            b.Click += click;
            b.MouseEnter += (s, e) => b.BackColor = ControlPaint.Dark(BackColor, 0.05f);
            b.MouseLeave += (s, e) => b.BackColor = Color.Transparent;
            return b;
        }

        void LayoutText()
        {
            if (text == null) return;
            text.Bounds = new Rectangle(12, header.Bottom + 4, ClientSize.Width - 24, ClientSize.Height - header.Bottom - 18);
        }

        void UpdateColors()
        {
            var c = Colors[Data.Color % Colors.Length];
            BackColor = c;
            text.BackColor = c;
            header.BackColor = ControlPaint.Dark(c, 0.02f);
            foreach (Control b in header.Controls) b.ForeColor = Color.FromArgb(90, 80, 60);
            pin.ForeColor = TopMost ? Color.Firebrick : Color.FromArgb(150, 140, 120);
            Invalidate();
        }

        void Drag(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
        }

        void Delete()
        {
            if (text.Text.Trim().Length > 0 &&
                MessageBox.Show(this, "Удалить заметку?", "StickyNotes", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Deleted = true;
            Close();
        }

        public NoteData Snapshot()
        {
            Data.Text = text.Text;
            Data.X = Left; Data.Y = Top; Data.Width = Width; Data.Height = Height;
            return Data;
        }

        // Resize by dragging the edges of a borderless window.
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x84 || (int)m.Result != 1) return; // WM_NCHITTEST, HTCLIENT
            var p = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
            const int grip = 8;
            bool left = p.X < grip, right = p.X >= ClientSize.Width - grip, top = p.Y < 4, bottom = p.Y >= ClientSize.Height - grip;
            if (bottom && right) m.Result = (IntPtr)17;
            else if (bottom && left) m.Result = (IntPtr)16;
            else if (top && right) m.Result = (IntPtr)14;
            else if (top && left) m.Result = (IntPtr)13;
            else if (right) m.Result = (IntPtr)11;
            else if (left) m.Result = (IntPtr)10;
            else if (bottom) m.Result = (IntPtr)15;
            else if (top) m.Result = (IntPtr)12;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(ControlPaint.Dark(BackColor, 0.1f))) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using (var pen = new Pen(ControlPaint.Dark(BackColor, 0.15f)))
                for (int i = 4; i <= 12; i += 4) e.Graphics.DrawLine(pen, ClientSize.Width - i, ClientSize.Height - 2, ClientSize.Width - 2, ClientSize.Height - i);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Alt+F4 only hides a note; it is deleted only with the × button.
            if (!Deleted && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); app.Changed(); return; }
            if (!Deleted) app.SaveNow();
            base.OnFormClosing(e);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } // CS_DROPSHADOW
        }
    }

    [STAThread]
    static void Main()
    {
        bool first;
        using (new Mutex(true, "StickyNotes-single-instance", out first))
        {
            if (!first) { MessageBox.Show("StickyNotes уже запущен: иконка в трее.", "StickyNotes"); return; }
            Application.EnableVisualStyles();
            Application.Run(new StickyNotes());
        }
    }
}
