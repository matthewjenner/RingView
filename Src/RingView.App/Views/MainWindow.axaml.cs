using Avalonia.Controls;
using Avalonia.Input;
using RingView.App.ViewModels;

namespace RingView.App.Views;

public sealed partial class MainWindow : Window
{
    private WindowState _beforeFullscreen = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.FullscreenRequested += ToggleFullscreen;
        };
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.Escape();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    public void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _beforeFullscreen;
        }
        else
        {
            _beforeFullscreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    private void OnSettingsBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.IsSettingsOpen = false;
    }

    private void OnAboutBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is { } vm)
            vm.IsAboutOpen = false;
    }
}
