using RingView.Core.Diagnostics;
using RingView.Core.Ring;

namespace RingView.Ring;

/// <summary>
/// One-time sign-in: email and password, then a 2FA code if Ring asks. The password and code
/// live in memory only for the exchange, are registered with the log redactor while held, and are
/// dropped as soon as a refresh token arrives or the flow is abandoned. Nothing touches disk; the
/// controller stores the resulting token.
/// </summary>
public sealed class RingSignIn(HttpClient http, SecretRedactor redactor) : IRingSignIn
{
    private readonly RingOAuth _oauth = new(http);
    private readonly List<string> _held = [];
    private (string Email, string Password, string HardwareId)? _pending;

    public async Task<SignInOutcome> StartAsync(string email, string password, CancellationToken cancellationToken)
    {
        Reset();
        Hold(password);
        // A new client gets a new hardware id; it then travels inside the stored token.
        _pending = (email, password, Guid.NewGuid().ToString());
        return await AttemptAsync(code: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SignInOutcome> SubmitCodeAsync(string code, CancellationToken cancellationToken)
    {
        if (_pending is null)
            return new SignInOutcome.Failed("No login in progress. Please start again.");
        Hold(code);
        return await AttemptAsync(code, cancellationToken).ConfigureAwait(false);
    }

    public void Reset()
    {
        foreach (string secret in _held)
            redactor.Unregister(secret);
        _held.Clear();
        _pending = null;
    }

    private async Task<SignInOutcome> AttemptAsync(string? code, CancellationToken cancellationToken)
    {
        (string email, string password, string hardwareId) = _pending!.Value;
        SignInStep step;
        try
        {
            step = await _oauth.SignInAsync(email, password, code, hardwareId, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            Reset();
            return new SignInOutcome.Failed("Could not reach Ring: " + ex.Message);
        }

        switch (step)
        {
            case SignInStep.Done done:
                Reset();
                return new SignInOutcome.Authenticated(RingToken.Create(done.Tokens.RefreshToken, hardwareId).Wrap());
            case SignInStep.CodeNeeded needed:
                return new SignInOutcome.NeedsTwoFactor(needed.Prompt);
            default:
                Reset();
                return new SignInOutcome.Failed(((SignInStep.Failed)step).Message);
        }
    }

    private void Hold(string secret)
    {
        redactor.Register(secret);
        _held.Add(secret);
    }
}
