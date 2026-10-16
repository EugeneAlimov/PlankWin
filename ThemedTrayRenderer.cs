using System.Drawing;
using System.Windows.Forms;

namespace PlankWin
{
    // Оформление меню трея (WinForms) в тон системной теме: тёмная/светлая, акцент на галочках,
    // скругления Win11 (через DWM), прямые углы на Win10.
    public sealed class ThemedTrayRenderer : ToolStripProfessionalRenderer
    {
        private readonly Palette _p;

        private sealed class Palette : ProfessionalColorTable
        {
            public Color Bg, Hover, Border, Text, TextDisabled, Sep, Accent;
            public override Color ToolStripDropDownBackground => Bg;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Hover;
            public override Color MenuItemSelected => Hover;
            public override Color MenuItemSelectedGradientBegin => Hover;
            public override Color MenuItemSelectedGradientEnd => Hover;
            public override Color MenuItemPressedGradientBegin => Hover;
            public override Color MenuItemPressedGradientEnd => Hover;
            public override Color SeparatorDark => Sep;
            public override Color SeparatorLight => Sep;
            public override Color CheckBackground => Hover;
            public override Color CheckSelectedBackground => Hover;
            public override Color CheckPressedBackground => Hover;
        }

        public ThemedTrayRenderer(bool dark, Color accent) : base(MakePalette(dark, accent))
        {
            _p = (Palette)ColorTable;
            RoundedEdges = false;
        }

        private static Palette MakePalette(bool dark, Color accent) => dark
            ? new Palette
            {
                Bg = Color.FromArgb(43, 43, 43),
                Hover = Color.FromArgb(65, 65, 65),
                Border = Color.FromArgb(70, 70, 70),
                Text = Color.White,
                TextDisabled = Color.FromArgb(140, 140, 140),
                Sep = Color.FromArgb(70, 70, 70),
                Accent = accent
            }
            : new Palette
            {
                Bg = Color.FromArgb(249, 249, 249),
                Hover = Color.FromArgb(234, 234, 234),
                Border = Color.FromArgb(220, 220, 220),
                Text = Color.Black,
                TextDisabled = Color.FromArgb(130, 130, 130),
                Sep = Color.FromArgb(220, 220, 220),
                Accent = accent
            };

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? _p.Text : _p.TextDisabled;
            base.OnRenderItemText(e);
        }

        // Подсветка пункта рисуется сами — базовый рендерер местами берёт системный синий.
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rc = new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2);
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            int d = Win32Helper.IsWin11 ? 8 : 0;
            if (d > 0)
            {
                path.AddArc(rc.X, rc.Y, d, d, 180, 90);
                path.AddArc(rc.Right - d, rc.Y, d, d, 270, 90);
                path.AddArc(rc.Right - d, rc.Bottom - d, d, d, 0, 90);
                path.AddArc(rc.X, rc.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
            }
            else path.AddRectangle(rc);
            using var b = new SolidBrush(_p.Hover);
            g.FillPath(b, path);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = _p.Text;
            base.OnRenderArrow(e);
        }

        // Галочка: акцентная «таблетка» с белой отметкой вместо стандартной рамки.
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            r.Inflate(-1, -1);
            using (var b = new SolidBrush(_p.Accent)) g.FillEllipse(b, r);
            using var pen = new Pen(Color.White, 1.6f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            g.DrawLines(pen, new[] { new PointF(x + w * 0.28f, y + h * 0.52f), new PointF(x + w * 0.44f, y + h * 0.68f), new PointF(x + w * 0.73f, y + h * 0.36f) });
        }

        // Скругление выпадающих окон меню на Win11 (на Win10 атрибут игнорируется).
        public static void RoundCorners(ToolStripDropDown dd)
        {
            if (!Win32Helper.IsWin11 || !dd.IsHandleCreated) return;
            int pref = 3; // DWMWCP_ROUNDSMALL
            try { Win32Helper.DwmSetWindowAttribute(dd.Handle, 33, ref pref, sizeof(int)); } catch { }
        }

        // Применить тему к меню и всем подменю (вызывать перед открытием).
        public static void Apply(ContextMenuStrip menu, bool dark, Color accent)
        {
            var r = new ThemedTrayRenderer(dark, accent);
            menu.Renderer = r;
            menu.Opened -= OnDropOpened; menu.Opened += OnDropOpened;
            foreach (ToolStripItem it in menu.Items) ApplyTo(it, r);
        }

        private static void ApplyTo(ToolStripItem item, ToolStripRenderer r)
        {
            if (item is ToolStripMenuItem mi && mi.HasDropDownItems)
            {
                mi.DropDown.Renderer = r;
                mi.DropDown.Opened -= OnDropOpened; mi.DropDown.Opened += OnDropOpened;
                foreach (ToolStripItem c in mi.DropDownItems) ApplyTo(c, r);
            }
        }

        private static void OnDropOpened(object? sender, System.EventArgs e)
        {
            if (sender is ToolStripDropDown dd) RoundCorners(dd);
        }
    }
}