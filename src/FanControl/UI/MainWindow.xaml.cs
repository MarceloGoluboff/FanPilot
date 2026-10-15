using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using FanControl.Control;
using Wpf.Ui.Controls;

namespace FanControl.UI;

public partial class MainWindow : FluentWindow
{
    readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();

        Editor.Curve = vm.CurveForEditor();
        Editor.MinPercent = vm.MinManualPercent;
        Editor.CurveChanged += () => vm.CurveEdited(Editor.Curve);
        vm.CurveReset += () => Editor.Curve = vm.CurveForEditor();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.MinManualPercent)) Editor.MinPercent = vm.MinManualPercent;
        };
        vm.SnapshotReceived += OnSnapshot;
        vm.FansChanged += BuildLegend;

        BuildLegend();
    }

    void OnSnapshot(FanSnapshot s)
    {
        Chart.Add(s);
        Editor.CurrentTemp = s.CpuTemp;
    }

    void BuildLegend()
    {
        Legend.Children.Clear();
        AddLegendItem(ChartColors.Temp, "Temp");
        for (int i = 1; i < _vm.Stats.Count; i++) AddLegendItem(ChartColors.Fan(i - 1), _vm.Stats[i].Label);
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
