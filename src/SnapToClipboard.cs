// SnapCopy —— 一键截图，截完直接进剪贴板，无预览窗口。
//
// 常驻形态：桌面上一枚置顶悬浮按钮，点击（或按 Ctrl+Alt+S）进入框选；右键可隐藏/退出。
// 交互：拖动框选；单击 = 全屏；Esc / 右键 = 取消。
// 命令行（供自检）：
//   --capture-all            整屏入剪贴板
//   --region x y w h         裁剪指定区域入剪贴板
//   --toast-test             预览提示条
//   --selftest               框选逻辑回归测试（退出码 0 通过）
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SnapCopy
{
    internal static class Native
    {
        [DllImport("user32.dll")]
        internal static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        internal const uint MOD_ALT = 0x0001;
        internal const uint MOD_CONTROL = 0x0002;
        internal const uint MOD_SHIFT = 0x0004;
        internal const int WM_HOTKEY = 0x0312;
    }

    internal static class Log
    {
        internal static void Write(string message)
        {
            string path = Environment.GetEnvironmentVariable("SNAPCOPY_LOG");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " [pid " +
                    System.Diagnostics.Process.GetCurrentProcess().Id + "] " + message + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;

            try { Native.SetProcessDPIAware(); }
            catch (Exception) { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Log.Write("start mode=" + mode);

            if (mode == "--selftest")
            {
                Environment.ExitCode = SelfTest.Run();
                return;
            }
            if (mode == "--toast-test")
            {
                CaptureService.PreviewToast();
                return;
            }
            if (mode == "--capture-all")
            {
                CaptureService.ClipboardFullScreen();
                return;
            }
            if (mode == "--region")
            {
                int x, y, w, h;
                if (args.Length >= 5
                    && int.TryParse(args[1], out x) && int.TryParse(args[2], out y)
                    && int.TryParse(args[3], out w) && int.TryParse(args[4], out h))
                {
                    CaptureService.ClipboardRegion(new Rectangle(x, y, w, h));
                    return;
                }
                Environment.ExitCode = 2;
                return;
            }

            // 常驻：单实例。已在运行时，第二次启动只当作"再截一张"的信号。
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\SnapCopy.Widget", out created))
            {
                if (!created)
                {
                    SignalRunningInstance();
                    return;
                }
                try
                {
                    Application.Run(new WidgetForm());
                }
                finally
                {
                    try { mutex.ReleaseMutex(); }
                    catch (Exception) { }
                }
            }
        }

        private static void SignalRunningInstance()
        {
            try
            {
                using (EventWaitHandle trigger = EventWaitHandle.OpenExisting("Local\\SnapCopy.Trigger"))
                {
                    trigger.Set();
                }
                Log.Write("signalled running instance");
            }
            catch (Exception ex)
            {
                Log.Write("signal failed: " + ex.Message);
            }
        }
    }

    internal static class CaptureService
    {
        internal static Bitmap Capture(Rectangle virtualScreen)
        {
            Bitmap bmp = new Bitmap(virtualScreen.Width, virtualScreen.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(virtualScreen.Left, virtualScreen.Top, 0, 0, virtualScreen.Size, CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }

        internal static void CopyToClipboard(Bitmap source, Rectangle region)
        {
            using (Bitmap cropped = source.Clone(region, PixelFormat.Format32bppArgb))
            {
                DataObject data = new DataObject();
                data.SetData(DataFormats.Bitmap, true, cropped);
                using (MemoryStream png = new MemoryStream())
                {
                    cropped.Save(png, ImageFormat.Png);
                    data.SetData("PNG", false, png.ToArray());
                }
                // copy:true 让数据在进程退出后仍留在剪贴板；重试应对剪贴板被别的程序占用。
                Clipboard.SetDataObject(data, true, 12, 60);
            }
        }

        internal static void ClipboardFullScreen()
        {
            Rectangle vs = SystemInformation.VirtualScreen;
            using (Bitmap snapshot = Capture(vs))
            {
                CopyToClipboard(snapshot, new Rectangle(0, 0, snapshot.Width, snapshot.Height));
            }
        }

        internal static void ClipboardRegion(Rectangle region)
        {
            Rectangle vs = SystemInformation.VirtualScreen;
            using (Bitmap snapshot = Capture(vs))
            {
                Rectangle r = Rectangle.Intersect(new Rectangle(0, 0, snapshot.Width, snapshot.Height), region);
                if (r.Width <= 0 || r.Height <= 0) { Environment.ExitCode = 3; return; }
                CopyToClipboard(snapshot, r);
            }
        }

        internal static void Interactive()
        {
            Rectangle vs = SystemInformation.VirtualScreen;
            using (Bitmap snapshot = Capture(vs))
            using (OverlayForm overlay = new OverlayForm(snapshot, vs))
            {
                overlay.ShowDialog();
                Rectangle picked = overlay.Result;
                Log.Write("picked " + picked);
                if (picked.Width <= 0 || picked.Height <= 0) return;
                CopyToClipboard(snapshot, picked);
                ToastForm.Pop(picked);
            }
        }

        internal static void PreviewToast()
        {
            ToastForm form = new ToastForm(new Rectangle(0, 0, 1280, 720), 2600);
            Application.Run(form);
        }
    }

    internal sealed class OverlayForm : Form
    {
        private const int MinDrag = 4;
        private const int ArmDelayMs = 180;

        private readonly Bitmap _snapshot;
        private Point _start;
        private Point _current;
        private bool _dragging;
        private bool _armed;

        internal Rectangle Result = Rectangle.Empty;

        internal OverlayForm(Bitmap snapshot, Rectangle screenBounds)
        {
            _snapshot = snapshot;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = screenBounds;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            UpdateStyles();

            // 刚弹出的一瞬间不接收点击：避免触发它的那次点击/双击漏进来被当成"单击=全屏"。
            System.Windows.Forms.Timer arm = new System.Windows.Forms.Timer();
            arm.Interval = ArmDelayMs;
            arm.Tick += delegate(object sender, EventArgs e)
            {
                arm.Stop();
                _armed = true;
                arm.Dispose();
            };
            arm.Start();
        }

        internal Rectangle Evaluate(Point from, Point to)
        {
            int w = ClientSize.Width > 0 ? ClientSize.Width : Bounds.Width;
            int h = ClientSize.Height > 0 ? ClientSize.Height : Bounds.Height;
            Point a = Clamp(from, w, h);
            Point b = Clamp(to, w, h);
            int left = Math.Min(a.X, b.X);
            int top = Math.Min(a.Y, b.Y);
            int right = Math.Max(a.X, b.X);
            int bottom = Math.Max(a.Y, b.Y);
            Rectangle sel = new Rectangle(left, top, right - left, bottom - top);
            if (sel.Width < MinDrag || sel.Height < MinDrag)
            {
                sel = new Rectangle(0, 0, w, h);
            }
            return sel;
        }

        private static Point Clamp(Point p, int width, int height)
        {
            int x = Math.Min(Math.Max(p.X, 0), width);
            int y = Math.Min(Math.Max(p.Y, 0), height);
            return new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.DrawImageUnscaled(_snapshot, 0, 0);

            using (SolidBrush dim = new SolidBrush(Color.FromArgb(118, 0, 0, 0)))
            {
                g.FillRectangle(dim, ClientRectangle);
            }

            Rectangle sel = Rectangle.Empty;
            if (_dragging)
            {
                sel = Evaluate(_start, _current);
            }

            if (_dragging && sel.Width > 0 && sel.Height > 0)
            {
                g.DrawImage(_snapshot, sel, sel, GraphicsUnit.Pixel);
                using (Pen pen = new Pen(Color.FromArgb(0, 174, 255), 2f))
                {
                    g.DrawRectangle(pen, sel.X, sel.Y, Math.Max(0, sel.Width - 1), Math.Max(0, sel.Height - 1));
                }
                if (sel.Width < ClientSize.Width || sel.Height < ClientSize.Height)
                {
                    DrawSizeLabel(g, sel, sel.Width + " x " + sel.Height);
                }
            }
            else if (!_dragging)
            {
                DrawHint(g);
            }
        }

        private void DrawHint(Graphics g)
        {
            const string text = "拖动框选区域　·　单击截取全屏　·　Esc / 右键 取消";
            using (Font font = new Font("Microsoft YaHei UI", 12f, FontStyle.Regular, GraphicsUnit.Point))
            {
                SizeF size = g.MeasureString(text, font);
                float x = (ClientSize.Width - size.Width) / 2f;
                float y = ClientSize.Height * 0.12f;
                RectangleF box = new RectangleF(x - 18f, y - 12f, size.Width + 36f, size.Height + 24f);
                using (GraphicsPath path = RoundedRect(box, 10f))
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(205, 18, 18, 18)))
                using (SolidBrush fg = new SolidBrush(Color.FromArgb(240, 240, 240)))
                {
                    g.FillPath(bg, path);
                    g.DrawString(text, font, fg, x, y);
                }
            }
        }

        private void DrawSizeLabel(Graphics g, Rectangle sel, string text)
        {
            using (Font font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point))
            {
                SizeF size = g.MeasureString(text, font);
                float x = sel.Right - size.Width - 14f;
                float y = sel.Bottom + 6f;
                if (y + size.Height + 10f > ClientSize.Height) y = sel.Top - size.Height - 14f;
                if (y < 0f) y = sel.Top + 6f;
                if (x < 0f) x = sel.Left + 6f;

                RectangleF box = new RectangleF(x - 6f, y - 3f, size.Width + 12f, size.Height + 6f);
                using (GraphicsPath path = RoundedRect(box, 4f))
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(200, 18, 18, 18)))
                using (SolidBrush fg = new SolidBrush(Color.White))
                {
                    g.FillPath(bg, path);
                    g.DrawString(text, font, fg, x, y);
                }
            }
        }

        internal static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            float d = radius * 2f;
            GraphicsPath path = new GraphicsPath();
            if (d <= 0.5f)
            {
                path.AddRectangle(bounds);
                return path;
            }
            path.AddArc(bounds.Left, bounds.Top, d, d, 180f, 90f);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270f, 90f);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0f, 90f);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        private Point Clamp(Point p)
        {
            int w = ClientSize.Width > 0 ? ClientSize.Width : Bounds.Width;
            int h = ClientSize.Height > 0 ? ClientSize.Height : Bounds.Height;
            return Clamp(p, w, h);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                Cancel();
                return;
            }
            if (e.Button == MouseButtons.Left && _armed)
            {
                _dragging = true;
                _start = Clamp(e.Location);
                _current = _start;
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_dragging) return;
            _current = Clamp(e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!_dragging) return;
            _current = Clamp(e.Location);
            Rectangle sel = Evaluate(_start, _current);
            _dragging = false;
            Result = sel;
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                Cancel();
                return;
            }
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                Result = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
                Close();
                return;
            }
            base.OnKeyDown(e);
        }

        private void Cancel()
        {
            Result = Rectangle.Empty;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            // 快照归 CaptureService 所有，这里不释放。
            base.Dispose(disposing);
        }
    }

    internal sealed class ToastForm : Form
    {
        private readonly string _text;
        private readonly System.Windows.Forms.Timer _timer;

        internal ToastForm(Rectangle region, int lifetimeMs)
        {
            _text = "已复制到剪贴板　" + region.Width + " x " + region.Height;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(24, 24, 24);
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

            using (Font font = new Font("Microsoft YaHei UI", 11f))
            {
                Size size = TextRenderer.MeasureText(_text, font);
                int w = size.Width + 44;
                int h = size.Height + 22;
                Rectangle screen = Screen.PrimaryScreen.Bounds;
                Location = new Point(screen.Left + (screen.Width - w) / 2, screen.Bottom - h - 90);
                Size = new Size(w, h);
            }
            Opacity = 0.94;
            Region = new Region(OverlayForm.RoundedRect(new RectangleF(0, 0, Width, Height), 12f));

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = lifetimeMs;
            _timer.Tick += delegate(object sender, EventArgs e)
            {
                _timer.Stop();
                Close();
            };
            _timer.Start();
        }

        internal static void Pop(Rectangle region)
        {
            ToastForm form = new ToastForm(region, 900);
            form.Show();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_NOACTIVATE = 0x08000000;
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_TRANSPARENT = 0x00000020;
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush accent = new SolidBrush(Color.FromArgb(0, 174, 255)))
            {
                g.FillEllipse(accent, 16f, Height / 2f - 4f, 8f, 8f);
            }
            using (Font font = new Font("Microsoft YaHei UI", 11f))
            {
                TextRenderer.DrawText(g, _text, font, new Point(34, (Height - font.Height) / 2), Color.White, TextFormatFlags.NoPadding);
            }
        }
    }

    internal sealed class WidgetForm : Form
    {
        private const string StateFolderName = "SnapCopy";
        private const string StateFileName = "widget.ini";

        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _captureItem;
        private readonly ToolStripMenuItem _toggleItem;
        private readonly NotifyIcon _tray;
        private readonly ToolTip _tip;

        private EventWaitHandle _trigger;
        private volatile bool _closing;

        private bool _dragging;
        private bool _moved;
        private Point _dragOrigin;
        private Point _formOrigin;
        private bool _hover;
        private bool _busy;
        private float _scale = 1f;
        private int _hotkeyId;
        private string _hotkeyText = "热键被占用";

        internal WidgetForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            Cursor = Cursors.Hand;
            DoubleBuffered = true;
            Text = "一键截图";
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

            _captureItem = new ToolStripMenuItem("截图", null, delegate { BeginCapture(); });
            _toggleItem = new ToolStripMenuItem("隐藏悬浮按钮", null, delegate { ToggleWidget(); });
            ToolStripMenuItem quitItem = new ToolStripMenuItem("退出", null, delegate { Quit(); });

            _menu = new ContextMenuStrip();
            _menu.Items.Add(_captureItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_toggleItem);
            _menu.Items.Add(quitItem);

            Icon trayIcon;
            try { trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { trayIcon = null; }
            if (trayIcon == null) trayIcon = SystemIcons.Application;

            _tray = new NotifyIcon();
            _tray.Icon = trayIcon;
            _tray.Text = "一键截图";
            _tray.ContextMenuStrip = _menu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { BeginCapture(); };

            _tip = new ToolTip();
            _tip.SetToolTip(this, "左键截图　·　拖动移动　·　右键菜单");
            _tip.InitialDelay = 400;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            using (Graphics g = CreateGraphics())
            {
                _scale = g.DpiX / 96f;
            }
            if (_scale < 1f) _scale = 1f;

            Rectangle saved = LoadState();
            ApplySize();
            if (saved != Rectangle.Empty) Location = ClampToScreen(saved.Location);
            else
            {
                Rectangle screen = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(screen.Right - Width - 24, screen.Bottom - Height - 90);
            }

            RegisterHotkey();
            UpdateToggleText();
            StartTriggerListener();
        }

        private void StartTriggerListener()
        {
            try
            {
                _trigger = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\SnapCopy.Trigger");
            }
            catch (Exception ex)
            {
                Log.Write("trigger create failed: " + ex.Message);
                return;
            }

            Thread worker = new Thread(delegate()
            {
                while (!_closing)
                {
                    try
                    {
                        if (!_trigger.WaitOne(400)) continue;
                    }
                    catch (Exception) { return; }
                    if (_closing) return;
                    try { BeginInvoke(new Action(BeginCapture)); }
                    catch (Exception) { return; }
                }
            });
            worker.IsBackground = true;
            worker.Name = "SnapCopy.trigger";
            worker.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 测试钩子：设置 SNAPCOPY_AUTOCAPTURE=1 时自动进入一次框选，供无输入注入的环境验收。
            if (Environment.GetEnvironmentVariable("SNAPCOPY_AUTOCAPTURE") == "1")
            {
                System.Windows.Forms.Timer auto = new System.Windows.Forms.Timer();
                auto.Interval = 1200;
                auto.Tick += delegate(object sender, EventArgs args)
                {
                    auto.Stop();
                    auto.Dispose();
                    BeginCapture();
                };
                auto.Start();
            }
        }

        private void ApplySize()
        {
            using (Font font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point))
            {
                Size textSize = TextRenderer.MeasureText("截图", font);
                int pad = (int)Math.Round(13 * _scale);
                int iconSize = (int)Math.Round(17 * _scale);
                int gap = (int)Math.Round(7 * _scale);
                int width = pad + iconSize + gap + textSize.Width + pad;
                int height = textSize.Height + (int)Math.Round(15 * _scale);
                Size = new Size(width, height);
            }
            using (GraphicsPath path = OverlayForm.RoundedRect(new RectangleF(0, 0, Width, Height), 9f * _scale))
            {
                Region = new Region(path);
            }
        }

        private void RegisterHotkey()
        {
            uint[][] combos = new uint[][]
            {
                new uint[] { Native.MOD_CONTROL | Native.MOD_ALT, 0x53, 0 },  // Ctrl+Alt+S
                new uint[] { Native.MOD_CONTROL | Native.MOD_SHIFT, 0x53, 1 }, // Ctrl+Shift+S
                new uint[] { Native.MOD_CONTROL | Native.MOD_ALT, 0x41, 2 }   // Ctrl+Alt+A
            };
            string[] labels = new string[] { "Ctrl+Alt+S", "Ctrl+Shift+S", "Ctrl+Alt+A" };

            for (int i = 0; i < combos.Length; i++)
            {
                if (Native.RegisterHotKey(Handle, 0xB001, combos[i][0], combos[i][1]))
                {
                    _hotkeyId = 0xB001;
                    _hotkeyText = labels[(int)combos[i][2]];
                    _captureItem.ShortcutKeyDisplayString = _hotkeyText;
                    _tray.Text = "一键截图 " + _hotkeyText;
                    Log.Write("hotkey=" + _hotkeyText);
                    return;
                }
            }
            Log.Write("hotkey registration failed");
            _captureItem.ShortcutKeyDisplayString = "";
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && (int)m.WParam == 0xB001)
            {
                BeginCapture();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            RectangleF rect = new RectangleF(0, 0, Width, Height);
            using (GraphicsPath path = OverlayForm.RoundedRect(rect, 9f * _scale))
            {
                Color top = _hover ? Color.FromArgb(255, 62, 146, 255) : Color.FromArgb(255, 43, 127, 255);
                Color bottom = _hover ? Color.FromArgb(255, 22, 96, 216) : Color.FromArgb(255, 12, 78, 190);
                using (LinearGradientBrush brush = new LinearGradientBrush(rect, top, bottom, 90f))
                {
                    g.FillPath(brush, path);
                }
            }

            float pad = 13f * _scale;
            float iconSize = 17f * _scale;
            RectangleF iconRect = new RectangleF(pad, (Height - iconSize) / 2f, iconSize, iconSize);
            DrawViewfinder(g, iconRect, Math.Max(1.4f, 1.8f * _scale));

            using (Font font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point))
            {
                float textX = pad + iconSize + 7f * _scale;
                TextRenderer.DrawText(g, "截图", font, new Point((int)textX, (Height - font.Height) / 2),
                    Color.White, TextFormatFlags.NoPadding);
            }
        }

        private static void DrawViewfinder(Graphics g, RectangleF r, float weight)
        {
            float len = r.Width * 0.42f;
            using (Pen pen = new Pen(Color.White, weight))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;

                g.DrawLine(pen, r.Left, r.Top + len, r.Left, r.Top);
                g.DrawLine(pen, r.Left, r.Top, r.Left + len, r.Top);

                g.DrawLine(pen, r.Right - len, r.Top, r.Right, r.Top);
                g.DrawLine(pen, r.Right, r.Top, r.Right, r.Top + len);

                g.DrawLine(pen, r.Left, r.Bottom - len, r.Left, r.Bottom);
                g.DrawLine(pen, r.Left, r.Bottom, r.Left + len, r.Bottom);

                g.DrawLine(pen, r.Right - len, r.Bottom, r.Right, r.Bottom);
                g.DrawLine(pen, r.Right, r.Bottom, r.Right, r.Bottom - len);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _moved = false;
                _dragOrigin = Cursor.Position;
                _formOrigin = Location;
            }
            else if (e.Button == MouseButtons.Right)
            {
                _menu.Show(this, e.Location);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                Point now = Cursor.Position;
                int dx = now.X - _dragOrigin.X;
                int dy = now.Y - _dragOrigin.Y;
                if (!_moved && (Math.Abs(dx) + Math.Abs(dy)) > 3) _moved = true;
                if (_moved)
                {
                    Location = ClampToScreen(new Point(_formOrigin.X + dx, _formOrigin.Y + dy));
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && _dragging)
            {
                _dragging = false;
                if (_moved) SaveState();
                else BeginCapture();
            }
            base.OnMouseUp(e);
        }

        private static Point ClampToScreen(Point p)
        {
            Rectangle vs = SystemInformation.VirtualScreen;
            int x = Math.Min(Math.Max(p.X, vs.Left), vs.Right - 40);
            int y = Math.Min(Math.Max(p.Y, vs.Top), vs.Bottom - 20);
            return new Point(x, y);
        }

        private void BeginCapture()
        {
            if (_busy) return;
            _busy = true;
            bool wasVisible = Visible;

            try
            {
                if (wasVisible)
                {
                    Hide();
                    Application.DoEvents();
                    Thread.Sleep(150);
                    Application.DoEvents();
                }

                CaptureService.Interactive();
            }
            catch (Exception ex)
            {
                Log.Write("capture error: " + ex);
                _tray.ShowBalloonTip(2500, "一键截图", "截图失败：" + ex.Message, ToolTipIcon.Error);
            }
            finally
            {
                if (wasVisible && !IsDisposed)
                {
                    Show();
                }
                _busy = false;
            }
        }

        private void ToggleWidget()
        {
            if (Visible)
            {
                Hide();
            }
            else
            {
                Show();
                BringToFront();
            }
            UpdateToggleText();
            SaveState();
        }

        private void UpdateToggleText()
        {
            _toggleItem.Text = Visible ? "隐藏悬浮按钮" : "显示悬浮按钮";
        }

        private void Quit()
        {
            SaveState();
            _closing = true;
            if (_trigger != null)
            {
                try { _trigger.Set(); }
                catch (Exception) { }
            }
            _tray.Visible = false;
            _tray.Dispose();
            if (_hotkeyId != 0)
            {
                Native.UnregisterHotKey(Handle, _hotkeyId);
                _hotkeyId = 0;
            }
            Close();
            Application.Exit();
        }

        private static string StatePath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StateFolderName);
            return Path.Combine(dir, StateFileName);
        }

        private Rectangle LoadState()
        {
            try
            {
                string path = StatePath();
                if (!File.Exists(path)) return Rectangle.Empty;
                string[] parts = File.ReadAllText(path).Split(',');
                if (parts.Length >= 2)
                {
                    int x, y;
                    if (int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y))
                    {
                        return new Rectangle(x, y, Width, Height);
                    }
                }
            }
            catch (Exception) { }
            return Rectangle.Empty;
        }

        private void SaveState()
        {
            try
            {
                string path = StatePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Location.X + "," + Location.Y + "," + (Visible ? "1" : "0"));
            }
            catch (Exception) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closing = true;
                if (_trigger != null)
                {
                    try { _trigger.Set(); }
                    catch (Exception) { }
                    try { _trigger.Dispose(); }
                    catch (Exception) { }
                    _trigger = null;
                }
                if (_hotkeyId != 0)
                {
                    Native.UnregisterHotKey(Handle, _hotkeyId);
                    _hotkeyId = 0;
                }
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
                if (_menu != null) _menu.Dispose();
                if (_tip != null) _tip.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class SelfTest
    {
        internal static int Run()
        {
            int failures = 0;
            using (Bitmap bmp = new Bitmap(2560, 1440))
            using (OverlayForm form = new OverlayForm(bmp, new Rectangle(0, 0, 2560, 1440)))
            {
                Rectangle normal = form.Evaluate(new Point(100, 100), new Point(400, 300));
                Rectangle reversed = form.Evaluate(new Point(400, 300), new Point(100, 100));
                Rectangle click = form.Evaluate(new Point(500, 500), new Point(501, 501));

                if (normal.Width != 300 || normal.Height != 200) failures++;
                if (reversed.Width != 300 || reversed.Height != 200) failures++;
                if (click.Width != 2560 || click.Height != 1440) failures++;

                Log.Write("selftest normal=" + normal + " reversed=" + reversed + " click=" + click +
                          " failures=" + failures);
            }
            return failures == 0 ? 0 : 1;
        }
    }
}
