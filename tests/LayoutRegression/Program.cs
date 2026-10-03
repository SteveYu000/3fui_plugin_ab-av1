using System.Reflection;
using System.Drawing;
using System.Windows.Forms;
using FFmpegFreeUI.AbAv1;
using LakeUI;

// 仅检查控件几何布局，不改变 Windows 显示设置，不创建或启动编码任务。
internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static void Main()
    {
        try { Run(); }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    }

    private static void Run()
    {
        // 两个独立测试进程分别使用 100% 和实际显示器 DPI，不修改 Windows 设置。
        var hundredPercent = Environment.GetCommandLineArgs().Contains("--100");
        Application.SetHighDpiMode(hundredPercent ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
        foreach (var size in new[] {new Size(980, 660), new Size(1280, 900), new Size(2040, 1300), new Size(780, 740), new Size(880, 620)})
        {
            using var page = new MainPanel();
            var sample = Field<UserControl>(page, "_sampleEncodePanel");
            var dpi = page.DeviceDpi;
            var scale = dpi / 96f;
            page.Size = new Size((int)Math.Round(size.Width * scale), (int)Math.Round(size.Height * scale));
            var tabs = Field<ModernTabControl>(page, "_tabControl");
            CheckPage(page, page, scale, dpi, size);
            // 同一实例反复跨过换行断点，确认不会累积缩放边距和按钮尺寸。
            for (var repeat = 0; repeat < 3; repeat++)
            {
                foreach (var alternate in new[] {new Size(780, 740), new Size(1280, 900), size})
                {
                    page.Size = new Size((int)Math.Round(alternate.Width * scale), (int)Math.Round(alternate.Height * scale));
                    CheckPage(page, page, scale, dpi, alternate);
                    tabs.SelectedIndex = 1;
                    CheckPage(page, sample, scale, dpi, alternate);
                    tabs.SelectedIndex = 0;
                }
            }
            tabs.SelectedIndex = 1;
            Layout(page);
            CheckPage(page, sample, scale, dpi, size);
            var metric = Field<ModernComboBox>(sample, "_scoreMetric");
            metric.SelectedIndex = 1;
            Layout(page);
            metric.SelectedIndex = 0;
            Layout(page);
            CheckPage(page, sample, scale, dpi, size);
            tabs.SelectedIndex = 0;
            var crfMetric = Field<ModernComboBox>(page, "_scoreMetric");
            crfMetric.SelectedIndex = 1;
            Layout(page);
            crfMetric.SelectedIndex = 0;
            Layout(page);
            CheckPage(page, page, scale, dpi, size);
            Console.WriteLine($"PASS DPI={dpi} logical={size.Width}x{size.Height}: 两页布局与评分指标切换");
        }
    }

    private static IEnumerable<Control> Walk(Control control)
    {
        yield return control;
        foreach (Control child in control.Controls)
        foreach (var nested in Walk(child)) yield return nested;
    }

    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, PrivateInstance)!.GetValue(value)!;
    private static void Layout(Control root)
    {
        for (var pass = 0; pass < 4; pass++)
        foreach (var control in Walk(root).ToArray()) control.PerformLayout();
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckPage(MainPanel root, UserControl page, float scale, int dpi, Size size)
    {
        Layout(root);
        var button = Field<ModernButton>(page, "_addFilesButton");
        var metric = Field<ModernComboBox>(page, "_scoreMetric");
        var model = Field<ModernComboBox>(page, "_vmafModel");
        var start = Field<ModernButton>(page, "_startButton");
        var list = Field<UltraDetailListView>(page, "_fileList");
        var editor = Field<ModernTextBox>(page, page is MainPanel ? "_targetScore" : "_crf");
        Require(Math.Abs(button.Height / scale - 32) <= 2, $"任务按钮高度异常: {button.Size} DPI={dpi}");
        Require(Math.Abs(button.Width / scale - 100) <= 2, $"任务按钮宽度异常: {button.Size} DPI={dpi}");
        Require(Math.Abs(editor.Height / scale - 32) <= 2, $"输入框高度异常: {editor.Size} DPI={dpi}");
        Require(Math.Abs(metric.Height / scale - 32) <= 2, $"下拉框高度异常: {metric.Size} DPI={dpi}");
        Require(Math.Abs(model.Height / scale - 32) <= 2, $"模型框高度异常: {model.Size} DPI={dpi}");
        Require(Math.Abs(start.Height / scale - 36) <= 2, $"开始按钮高度异常: {start.Size} DPI={dpi}");
        Require(list.Height / scale >= 64, $"任务列表可用高度不足: {list.Size} DPI={dpi} logical={size}");
        foreach (var control in new Control[] {button, metric, editor, model, start, list})
        {
            for (var current = control; current.Parent != null && current != page; current = current.Parent)
            {
                Require(current.Bounds.Right <= current.Parent.ClientSize.Width + 2, $"控件越过父容器右边界: {control.GetType().Name} {current.Bounds} parent={current.Parent.Size} DPI={dpi}");
                Require(current.Bounds.Bottom <= current.Parent.ClientSize.Height + 2, $"控件越过父容器底边界: {control.GetType().Name} {current.Bounds} parent={current.Parent.Size} DPI={dpi}");
            }
        }
        Require(list.Columns.Sum(column => column.Width) <= list.Width, $"列表列宽溢出: DPI={dpi}");
        if (page is MainPanel)
        {
            var parameters = Field<Control>(page, "_searchParametersLayout");
            var rowCount = (int)parameters.GetType().GetProperty("RowCount")!.GetValue(parameters)!;
            Require(rowCount == (parameters.Width / scale < 840 ? 2 : 1), $"参数换行断点不一致: DPI={dpi}");
        }
    }
}
