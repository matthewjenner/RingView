using Avalonia.Controls;

namespace RingView.App.Tests;

public sealed class SignInUiTests
{
    [Fact]
    public Task SignInWithTwoFactorLandsOnTheWall() => TestApp.Run(() =>
    {
        using UiHarness ui = UiHarness.Start(signedOut: true);
        ui.WaitUntil(() => ui.ViewModel.ShowLogin, "the sign-in screen");
        Button signIn = ui.ButtonWithText("Sign in");
        signIn.IsEffectivelyEnabled.Should().BeFalse("nothing is entered yet");

        List<TextBox> boxes = [.. ui.FindAll<TextBox>(_ => true)];
        ui.Type(boxes[0], "someone@example.com");
        ui.Type(boxes[1], "the-password");
        ui.Click(signIn);

        ui.WaitUntil(() => ui.ViewModel.Login.NeedsTwoFactor, "the 2FA step");
        ui.ViewModel.Login.Password.Should().BeEmpty("the password leaves the form once sent");
        ui.Find<TextBlock>(t => t.Text == "Please enter the code sent to xxx-xxx-0123 via sms", "Ring's prompt").Should().NotBeNull();

        TextBox code = ui.Find<TextBox>(t => t.Name == "CodeBox", "code box");
        ui.Type(code, "123456");
        ui.Click(ui.ButtonWithText("Verify"));

        ui.WaitUntil(() => ui.ViewModel.Wall is { Tiles.Count: 5 }, "the wall");
    });
}
