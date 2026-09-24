using MisaAutomation.Core;

namespace MisaAutomation.Inspector;

internal sealed class ControlHighlight : IDisposable
{
    private sealed class Edge : Form
    {
        public Edge() { FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; BackColor = Color.OrangeRed; }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        { get { var value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x00000020 | 0x00080000; return value; } }
    }
    private readonly Edge[] edges = [new(), new(), new(), new()];
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1200 };
    public ControlHighlight() { timer.Tick += (_, _) => { timer.Stop(); foreach (var edge in edges) edge.Hide(); }; }
    public void Show(Bounds bounds)
    {
        var rectangle = Rectangle.Round(new RectangleF((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height));
        if (rectangle.Width <= 0 || rectangle.Height <= 0) return;
        edges[0].Bounds = new Rectangle(rectangle.Left, rectangle.Top, rectangle.Width, 2);
        edges[1].Bounds = new Rectangle(rectangle.Left, rectangle.Bottom - 2, rectangle.Width, 2);
        edges[2].Bounds = new Rectangle(rectangle.Left, rectangle.Top, 2, rectangle.Height);
        edges[3].Bounds = new Rectangle(rectangle.Right - 2, rectangle.Top, 2, rectangle.Height);
        foreach (var edge in edges) edge.Show();
        timer.Stop(); timer.Start();
    }
    public void Dispose() { timer.Dispose(); foreach (var edge in edges) edge.Dispose(); }
}
