using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using RingView.Core.Diagnostics;
using RingView.Core.Ring;

namespace RingView.Ring.Tests;

public sealed class AuthAndRestTests
{
    private static readonly string Stored = RingToken.Create("stored-rt", "hid-1234").Wrap();

    [Fact]
    public void TheTokenKeepsEveryFieldAcrossRotation()
    {
        string wrapped = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("""{"rt":"a","hid":"h","pnc":{"x":1}}"""));
        RingToken rotated = RingToken.Parse(wrapped).WithRefreshToken("b", "h");
        RingToken back = RingToken.Parse(rotated.Wrap());

        back.RefreshToken.Should().Be("b");
        back.HardwareId.Should().Be("h");
        back.FieldNames.Should().BeEquivalentTo("rt", "hid", "pnc");
        RingToken.Parse("bare-refresh-token").RefreshToken.Should().Be("bare-refresh-token");
    }

    [Fact]
    public async Task TheRefreshRequestMatchesTheRecording()
    {
        JsonObject recorded = Protocol.Http(u => u.Contains("oauth.ring.com", StringComparison.Ordinal))["request"]!.AsObject();
        var http = new FakeHttp(_ => FakeHttp.Tokens());

        await new RingOAuth(http.Client()).RefreshAsync("stored-rt", "hid-1234", CancellationToken.None);

        SentRequest sent = http.Requests.Single();
        sent.Uri.ToString().Should().Be((string)recorded["url"]!);
        sent.Method.Method.Should().Be((string)recorded["method"]!);
        foreach ((string name, JsonNode? value) in recorded["headers"]!.AsObject())
        {
            sent.Headers.Should().ContainKey(name);
            string expected = (string)value!;
            if (!expected.StartsWith('<'))
                sent.Headers[name].Should().StartWith(expected, name);
        }
        sent.Headers["hardware_id"].Should().Be("hid-1234");
        JsonObject body = sent.Json!;
        foreach ((string name, JsonNode? value) in recorded["body"]!.AsObject())
        {
            body.Should().ContainKey(name);
            if (!((string)value!).StartsWith('<'))
                ((string)body[name]!).Should().Be((string)value!, name);
        }
        ((string)body["refresh_token"]!).Should().Be("stored-rt");
    }

    [Fact]
    public async Task ARejectedRefreshMeansSignInAgain()
    {
        var http = new FakeHttp(_ => FakeHttp.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"token is invalid or does not exists"}"""));

        Func<Task> refresh = () => new RingOAuth(http.Client()).RefreshAsync("dead-rt", "hid", CancellationToken.None);

        RingException error = (await refresh.Should().ThrowAsync<RingException>()).Which;
        RingErrors.Classify(error).Should().Be(RingErrorKind.InvalidToken);
    }

    [Fact]
    public async Task TwoFactorPromptsReadLikeTheElectronApp()
    {
        var steps = new Queue<HttpResponseMessage>([
            FakeHttp.Json(HttpStatusCode.PreconditionFailed, """{"tsv_state":"sms","phone":"xxx-xxx-1234"}"""),
            FakeHttp.Json(HttpStatusCode.BadRequest, """{"error":"Verification Code is invalid or expired"}"""),
            FakeHttp.Tokens(refresh: "new-rt"),
        ]);
        var http = new FakeHttp(_ => steps.Dequeue());
        var redactor = new SecretRedactor();
        var signIn = new RingSignIn(http.Client(), redactor);

        SignInOutcome first = await signIn.StartAsync("someone@example.com", "the-password", CancellationToken.None);
        redactor.Redact("the-password").Should().Be(SecretRedactor.Mask, "the password is masked while it is held");
        SignInOutcome second = await signIn.SubmitCodeAsync("111111", CancellationToken.None);
        SignInOutcome third = await signIn.SubmitCodeAsync("222222", CancellationToken.None);

        first.Should().Be(new SignInOutcome.NeedsTwoFactor("Please enter the code sent to xxx-xxx-1234 via sms"));
        second.Should().Be(new SignInOutcome.NeedsTwoFactor(RingOAuth.InvalidCodePrompt));
        RingToken token = RingToken.Parse(third.Should().BeOfType<SignInOutcome.Authenticated>().Which.RefreshToken);
        token.RefreshToken.Should().Be("new-rt");
        token.HardwareId.Should().NotBeNullOrEmpty();
        http.Requests[2].Headers["2fa-code"].Should().Be("222222");
        ((string)http.Requests[2].Json!["grant_type"]!).Should().Be("password");
        redactor.Redact("the-password").Should().Be("the-password", "the password is dropped once signed in");
    }

    [Fact]
    public async Task EveryRefreshRotatesTheTokenAndAccessIsReusedUntilItExpires()
    {
        var time = new FakeTimeProvider();
        int refreshes = 0;
        var http = new FakeHttp(r => r.Uri.Host == "oauth.ring.com"
            ? FakeHttp.Tokens(access: $"access-{++refreshes}", refresh: $"rt-{refreshes}", expiresIn: 3600)
            : FakeHttp.Json(HttpStatusCode.OK, "{}"));
        var rest = new RingRestClient(http.Client(), Stored, time);
        var rotations = new List<string>();
        rest.TokenRotated += t => rotations.Add(RingToken.Parse(t).RefreshToken);

        await rest.GetJsonAsync(RingEndpoints.Devices, CancellationToken.None);
        await rest.GetJsonAsync(RingEndpoints.Devices, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(59.5)); // inside the last minute: refresh early
        await rest.GetJsonAsync(RingEndpoints.Devices, CancellationToken.None);

        rotations.Should().Equal("rt-1", "rt-2");
        SentRequest api = http.Requests.First(r => r.Uri.Host == "api.ring.com");
        api.Headers["Authorization"].Should().Be("Bearer access-1");
        api.Headers["hardware_id"].Should().Be("hid-1234");
        api.Headers["User-Agent"].Should().Be("android:com.ringapp");
        ((string)http.Requests[^1].Headers["Authorization"]).Should().Be("Bearer access-2");
    }

    [Fact]
    public async Task ARejectedAccessTokenIsRefreshedOnceAndRetried()
    {
        int calls = 0, refreshes = 0;
        var http = new FakeHttp(r => r.Uri.Host == "oauth.ring.com"
            ? FakeHttp.Tokens(access: $"access-{++refreshes}")
            : ++calls == 1 ? FakeHttp.Json(HttpStatusCode.Unauthorized, "{}") : FakeHttp.Json(HttpStatusCode.OK, """{"ok":true}"""));
        var rest = new RingRestClient(http.Client(), Stored, new FakeTimeProvider());

        JsonObject result = await rest.GetJsonAsync(RingEndpoints.Devices, CancellationToken.None);

        ((bool)result["ok"]!).Should().BeTrue();
        http.Requests.Count(r => r.Uri.Host == "oauth.ring.com").Should().Be(2);
    }

}
