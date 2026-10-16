using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using FanPilot.Control;
using H.NotifyIcon;

namespace FanPilot.UI;

/// <summary>Notification-area icon: live tooltip, mode and preset menu, Open and Exit.</summary>
internal sealed class TrayIcon : IDisposable
{
    readonly TaskbarIcon _icon;
    readonly MainViewModel _vm;
    readonly MenuItem _auto, _manual, _curve, _presets;

    public TrayIcon(MainViewModel vm, Action open, Action exit)
    {
        _vm = vm;

        var menu = new ContextMenu();
        menu.Items.Add(Item("Open FanPilot", open));
        menu.Items.Add(new Separator());

        _auto = Item("ASUS Auto", () => vm.Mode = FanMode.AsusAuto);
        _manual = Item("Manual", () => vm.Mode = FanMode.Manual);
        _curve = Item("Curve", () => vm.Mode = FanMode.Curve);
        foreach (var item in new[] { _auto, _manual, _curve }) menu.Items.Add(item);

        _presets = new MenuItem { Header = "Presets" };
        BuildPresets();
        // Presets change once fan setup knows this laptop's Silent speed.
        vm.Presets.CollectionChanged += (_, _) => BuildPresets();
        menu.Items.Add(_presets);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", exit));

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/fan.ico")),
            ToolTipText = vm.TrayText,
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => open();
        _icon.ForceCreate(enablesEfficiencyMode: false);

        vm.PropertyChanged += OnViewModelChanged;
        UpdateChecks();
    }

    void BuildPresets()
    {
        _presets.Items.Clear();
        foreach (var preset in _vm.Presets)
            _presets.Items.Add(Item($"{preset.Name} ({preset.Percent}%)", () => _vm.PresetCommand.Execute(preset.Percent)));
    }

    static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TrayText)) _icon.ToolTipText = _vm.TrayText;
        else if (e.PropertyName is nameof(MainViewModel.Mode) or nameof(MainViewModel.IsControlEnabled)) UpdateChecks();
    }

    void UpdateChecks()
    {
        foreach (var item in new[] { _auto, _manual, _curve, _presets }) item.IsEnabled = _vm.IsControlEnabled;
        _auto.IsChecked = _vm.Mode == FanMode.AsusAuto;
        _manual.IsChecked = _vm.Mode == FanMode.Manual;
        _curve.IsChecked = _vm.Mode == FanMode.Curve;
    }

    public void Dispose()
    {
        _vm.PropertyChanged -= OnViewModelChanged;
        _icon.Dispose();
    }
}
