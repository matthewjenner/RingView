using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using RingView.Core.Ring;
using RingView.Core.Streaming;

namespace RingView.Ring.Tests;

public sealed class DevicesAndSnapshotsTests
{
    [Fact]
    public void DeviceFactsMatchTheRecordedLibrary()
    {
        JsonArray cameras = Protocol.Events("cameras").Single()["cameras"]!.AsArray();
        cameras.Should().NotBeEmpty();
        foreach (JsonObject camera in cameras.Select(c => c!.AsObject()))
        {
            RingDevice device = RingDevices.ParseDevice(camera["data"]!.AsObject());
            string because = $"camera {camera["id"]}";
            device.Info.Id.Should().Be((long)camera["id"]!, because);
            device.Info.HasBattery.Should().Be((bool)camera["hasBattery"]!, because);
            device.Info.BatteryLevel.Should().Be((int?)camera["batteryLevel"], because);
            device.Info.OperatingOnBattery.Should().Be((bool)camera["operatingOnBattery"]!, because);
            device.Info.IsOffline.Should().Be((bool)camera["isOffline"]!, because);
            device.SnapshotsAreBlocked.Should().Be((bool)camera["snapshotsAreBlocked"]!, because);
            device.SnapshotLifetime.TotalMilliseconds.Should().Be((double)camera["snapshotLifeTimeMs"]!, because);
        }
    }

    [Fact]
    public void TheDeviceListKeepsTheLibrarysOrder()
    {
        JsonObject response = Protocol.Http(u => u.EndsWith("/clients_api/ring_devices", StringComparison.Ordinal))["response"]!["body"]!.AsObject();
        IEnumerable<long> expected = Protocol.Events("cameras").Single()["cameras"]!.AsArray().Select(c => (long)c!["id"]!);

        RingDevices.Parse(response).Select(d => d.Info.Id).Should().Equal(expected);
    }

    [Fact]
    public async Task SnapshotsUseTheRecordedRequestAndRingsAge()
    {
        JsonObject recorded = Protocol.Http(u => u.Contains("app-snaps", StringComparison.Ordinal))["request"]!.AsObject();
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
        var http = new FakeHttp(r => r.Uri.Host == "oauth.ring.com" ? FakeHttp.Tokens() : Jpeg(time, ageMs: 4_000));
        var snapshots = Snapshots(http, time, Wired());

        Snapshot shot = await snapshots.GetSnapshotAsync(1001, CancellationToken.None);

        SentRequest sent = http.Requests.Last();
        sent.Uri.GetLeftPart(UriPartial.Path).Should().Be(((string)recorded["url"]!).Split('?')[0]);
        sent.Uri.Query.Should().Be("?after-ms=0&extras=force");
        sent.Headers["Accept"].Should().Be("image/jpeg");
        shot.AgeMs.Should().Be(4_000);
        shot.Jpeg.Should().Equal(0xFF, 0xD8);
    }

    [Theory]
    [InlineData(false, 9_000, 1)]
    [InlineData(false, 11_000, 2)]
    [InlineData(true, 9 * 60_000, 1)]
    [InlineData(true, 11 * 60_000, 2)]
    public async Task ASnapshotIsReusedWithinItsLifetime(bool battery, int laterMs, int expectedFetches)
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
        var http = new FakeHttp(r => r.Uri.Host == "oauth.ring.com" ? FakeHttp.Tokens() : Jpeg(time, ageMs: 0));
        var snapshots = Snapshots(http, time, battery ? Battery() : Wired());

        await snapshots.GetSnapshotAsync(1001, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(laterMs));
        Snapshot second = await snapshots.GetSnapshotAsync(1001, CancellationToken.None);

        http.Requests.Count(r => r.Uri.Host == "app-snaps.ring.com").Should().Be(expectedFetches);
        if (expectedFetches == 1)
            second.AgeMs.Should().Be(laterMs, "a reused frame reports its true, growing age");
    }

    [Fact]
    public async Task OfflineAndBlockedCamerasExplainThemselves()
    {
        var time = new FakeTimeProvider();
        var http = new FakeHttp(_ => FakeHttp.Tokens());
        RingDevice offline = RingDevices.ParseDevice(JsonNode.Parse("""{"id":1,"description":"Yard","battery_life":null,"alerts":{"connection":"offline"},"settings":{"power_mode":"wired"}}""")!.AsObject());
        RingDevice blocked = RingDevices.ParseDevice(JsonNode.Parse("""{"id":2,"description":"Gate","battery_life":null,"alerts":{"connection":"online"},"settings":{"motion_detection_enabled":false}}""")!.AsObject());

        await FluentActions.Awaiting(() => Snapshots(http, time, offline).GetSnapshotAsync(1, CancellationToken.None))
            .Should().ThrowAsync<RingException>().WithMessage("*Yard because it is offline");
        await FluentActions.Awaiting(() => Snapshots(http, time, blocked).GetSnapshotAsync(2, CancellationToken.None))
            .Should().ThrowAsync<RingException>().WithMessage("Motion detection is disabled for Gate*");
    }

    private static RingSnapshots Snapshots(FakeHttp http, TimeProvider time, RingDevice device) =>
        new(new RingRestClient(http.Client(), RingToken.Create("rt", "hid").Wrap(), time), _ => Task.FromResult(device), time);

    private static RingDevice Wired() => RingDevices.ParseDevice(JsonNode.Parse(
        """{"id":1001,"description":"Wired","battery_life":null,"alerts":{"connection":"online"},"settings":{"power_mode":"wired","motion_detection_enabled":true}}""")!.AsObject());

    private static RingDevice Battery() => RingDevices.ParseDevice(JsonNode.Parse(
        """{"id":1001,"description":"Battery","battery_life":"87","alerts":{"connection":"online"},"health":{"battery_percentage":87,"battery_present":true},"settings":{"power_mode":"battery","motion_detection_enabled":true}}""")!.AsObject());

    private static HttpResponseMessage Jpeg(TimeProvider time, long ageMs)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xD8]) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        response.Headers.Date = time.GetUtcNow();
        response.Headers.TryAddWithoutValidation("x-time-millis",
            (time.GetUtcNow().ToUnixTimeMilliseconds() - ageMs).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }
}
