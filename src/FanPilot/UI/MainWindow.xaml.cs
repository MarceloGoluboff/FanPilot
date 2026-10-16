using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FanPilot.Control;
using Wpf.Ui.Controls;

namespace FanPilot.UI;

public partial class MainWindow : FluentWindow
{
    readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        FitToWorkArea();

        Editor.Curve = vm.CurveForEditor();
        Editor.MinPercent = vm.Floor;
        Editor.CurveChanged += () => vm.CurveEdited(Editor.Curve);
        vm.CurveReset += () =>
        {
            Editor.MinPercent = vm.Floor;
            Editor.Curve = vm.CurveForEditor();
        };
        vm.SnapshotReceived += OnSnapshot;
        vm.FansChanged += BuildLegend;

        BuildLegend();
    }

    /// <summary>The content scrolls, so on short screens the window shrinks to fit above the taskbar.</summary>
    void FitToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        Height = Math.Min(Height, work.Height - 32);
        Width = Math.Min(Width, work.Width - 32);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    void OnSnapshot(FanSnapshot s)
    {
        Chart.Add(s);
        // The marker shows what the curve is reading: the hotter of CPU and GPU when it follows both.
        Editor.CurrentTemp = _vm.CurveUsesGpu && s.GpuTemp is int g && (s.CpuTemp is null || g > s.CpuTemp) ? g : s.CpuTemp;
    }

    void BuildLegend()
    {
        Legend.Children.Clear();
        AddLegendItem(ChartColors.Temp, "CPU");
        AddLegendItem(ChartColors.GpuTemp, "GPU");
        for (int i = 0; i < _vm.FanStats.Count; i++) AddLegendItem(ChartColors.Fan(i), _vm.FanStats[i].Label);
    }

    void AddLegendItem(Brush brush, string text)
    {
        var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        item.Children.Add(new Rectangle
        {
            Width = 10, Height = 3, Fill = brush, RadiusX = 1.5, RadiusY = 1.5,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0),
        });
        var label = new System.Windows.Controls.TextBlock { Text = text, FontSize = 12 };
        label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        item.Children.Add(label);
        Legend.Children.Add(item);
    }
}
