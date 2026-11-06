using Avalonia.Controls;
using Avalonia.Input;
using RingView.App.ViewModels;

namespace RingView.App.Views;

public sealed partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LoginViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(LoginViewModel.NeedsTwoFactor) && vm.NeedsTwoFactor)
                        CodeBox.Focus();
                };
            }
        };
    }

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is LoginViewModel vm && vm.SignInCommand.CanExecute(null))
            vm.SignInCommand.Execute(null);
    }

    private void OnCodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is LoginViewModel vm && vm.VerifyCommand.CanExecute(null))
            vm.VerifyCommand.Execute(null);
    }
}
