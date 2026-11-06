using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RingView.App.Services;
using RingView.Core;

namespace RingView.App.ViewModels;

/// <summary>
/// One-time sign-in: email and password, then a 2FA code if Ring asks. The password and code are
/// cleared from the form as soon as they are sent.
/// </summary>
public sealed partial class LoginViewModel(AppHost host) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _email = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _password = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    private string _code = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand), nameof(VerifyCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _needsTwoFactor;

    [ObservableProperty]
    private string _prompt = "Enter your verification code.";

    /// <summary>Red banner: a failed sign-in.</summary>
    [ObservableProperty]
    private string? _errorText;

    /// <summary>Amber banner: the session expired (re-auth), or encryption is unavailable.</summary>
    [ObservableProperty]
    private string? _warningText;

    public string SignInLabel => IsBusy ? "Signing in..." : "Sign in";

    public string VerifyLabel => IsBusy ? "Verifying..." : "Verify";

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(SignInLabel));
        OnPropertyChanged(nameof(VerifyLabel));
    }

    public void Update(AuthState auth)
    {
        IsBusy = auth.Status == AuthStatus.Authenticating;
        NeedsTwoFactor = auth.Status == AuthStatus.NeedsTwoFactor || (IsBusy && NeedsTwoFactor);
        if (auth.TwoFactorPrompt is { } prompt)
            Prompt = prompt;
        WarningText = !auth.EncryptionAvailable
            ? "OS-backed encryption is unavailable, so your session can't be stored securely."
            : auth.NeedsReauth ? auth.Error : null;
        ErrorText = auth.NeedsReauth ? null : auth.Error;
    }

    private bool CanSignIn() => !IsBusy && Email.Length > 0 && Password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignIn()
    {
        string email = Email.Trim(), password = Password;
        Password = ""; // drop it from the form at once
        await host.Run(c => c.SignInAsync(email, password));
    }

    private bool CanVerify() => !IsBusy && Code.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task Verify()
    {
        string code = Code.Trim();
        Code = "";
        await host.Run(c => c.SubmitCodeAsync(code));
    }
}
