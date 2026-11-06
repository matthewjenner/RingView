using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RingView.App.ViewModels;
using RingView.Core.Settings;
using RingView.Core.Updates;

namespace RingView.App.Views;

/// <summary>
/// <c>--demo --screenshot &lt;folder&gt;</c>: walks the main screens with the synthetic cameras and
/// saves each as a PNG, for reviewing the look against the Electron app without real footage.
/// </summary>
internal static class Screenshots
{
    public static async Task RunAsync(Window window, MainWindowViewModel vm, string folder, Action quit)
    {
        Directory.CreateDirectory(folder);
        try
        {
            if (AppArgs.DemoSignIn)
            {
                await Task.Delay(1500);
                Save(window, folder, "signin");
                vm.Login.Email = "someone@example.com";
                vm.Login.Password = "password";
                vm.Login.SignInCommand.Execute(null);
                await Task.Delay(1500);
                Save(window, folder, "twofactor");
                return;
            }

            await Task.Delay(9000); // live sessions connect, the snapshot loads
            Save(window, folder, "wall-dark");

            WallViewModel wall = vm.Wall!;
            wall.ShowUpdate(new UpdateState(UpdateStatus.Downloaded, "0.2.1"));
            await Task.Delay(300);
            Save(window, folder, "wall-update");
            wall.ShowUpdate(new UpdateState(UpdateStatus.Error, Error: "offline"));
            await Task.Delay(300);
            Save(window, folder, "wall-update-failed");
            wall.ShowUpdate(new UpdateState(UpdateStatus.Disabled));

            TileViewModel side = wall.Tiles.First(t => t.Name == "Side");
            side.GoLiveCommand.Execute(null);
            await Task.Delay(3000);
            Save(window, folder, "wall-golive");

            vm.IsSettingsOpen = true;
            await Task.Delay(500);
            Save(window, folder, "settings");
            vm.IsSettingsOpen = false;

            AppSettings app = vm.Settings.Current;
            vm.Settings.SelectedTheme = vm.Settings.ThemeOptions.First(o => o.Value == (int)ThemeChoice.Light);
            await Task.Delay(1500);
            Save(window, folder, "wall-light");
            vm.Settings.SelectedTheme = vm.Settings.ThemeOptions.First(o => o.Value == (int)app.Theme);
            await Task.Delay(500);

            wall.ToggleSolo(wall.Tiles.First());
            await Task.Delay(1500);
            Save(window, folder, "solo");
            wall.Solo = null;
            await Task.Delay(300);
        }
        finally
        {
            Dispatcher.UIThread.Post(quit);
        }
    }

    private static void Save(Window window, string folder, string name)
    {
        double scale = window.RenderScaling;
        var size = new PixelSize((int)(window.Bounds.Width * scale), (int)(window.Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(folder, name + ".png"), new PngBitmapEncoderOptions());
    }
}
