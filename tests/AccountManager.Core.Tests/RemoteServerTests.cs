using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AccountManager.Core.Remote;

namespace AccountManager.Core.Tests;

public sealed class RemoteServerTests : IDisposable
{
    private readonly FakeController _controller = new();
    private readonly string _token = RemoteServer.GenerateToken();
    private readonly RemoteServer _server;
    private readonly HttpClient _http = new();

    public RemoteServerTests()
    {
        _server = new RemoteServer(_controller, _token);
        _server.Start(0, allowLan: false);
        _http.BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/");
    }

    [Fact]
    public async Task Serves_panel_without_auth()
    {
        HttpResponseMessage response = await _http.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Account manager", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Status_requires_token()
    {
        HttpResponseMessage response = await _http.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Status_with_token()
    {
        HttpResponseMessage response = await Authorized(HttpMethod.Get, "/api/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        RemoteStatus? status = await response.Content.ReadFromJsonAsync<RemoteStatus>();
        Assert.Equal(2, status!.Running);
        Assert.Equal("alpha", status.Accounts[0].Name);
    }

    [Fact]
    public async Task Account_actions_and_shutdown_route_to_controller()
    {
        Assert.Equal(HttpStatusCode.OK, (await Authorized(HttpMethod.Post, "/api/accounts/7/launch")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Authorized(HttpMethod.Post, "/api/accounts/7/stop")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Authorized(HttpMethod.Post, "/api/accounts/999/stop")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Authorized(HttpMethod.Post, "/api/shutdown", new { minutes = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Authorized(HttpMethod.Post, "/api/shutdown", new { minutes = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Authorized(HttpMethod.Post, "/api/shutdown", new { cancel = true })).StatusCode);

        Assert.Equal(["launch:7", "stop:7", "shutdown:30", "cancel"], _controller.Calls);
    }

    [Fact]
    public async Task Wrong_token_is_rejected_and_throttled()
    {
        for (int i = 0; i < 10; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
            Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(request)).StatusCode);
        }

        // Even the right token is refused while throttled.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Authorized(HttpMethod.Get, "/api/status")).StatusCode);
    }

    [Fact]
    public async Task Rejects_dns_names_in_host_header()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Host = "attacker.example";
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        Assert.Equal((HttpStatusCode)421, (await _http.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1:5280", true)]
    [InlineData("localhost:5280", true)]
    [InlineData("192.168.1.20:5280", true)]
    [InlineData("[::1]:5280", true)]
    [InlineData("evil.com:5280", false)]
    [InlineData("localhost.evil.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Host_check(string? host, bool allowed)
    {
        Assert.Equal(allowed, RemoteServer.IsAllowedHost(host));
    }

    [Fact]
    public void Short_tokens_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new RemoteServer(_controller, "short"));
    }

    private Task<HttpResponseMessage> Authorized(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _http.SendAsync(request);
    }

    public void Dispose()
    {
        _http.Dispose();
        _server.Dispose();
    }

    private sealed class FakeController : IRemoteController
    {
        public List<string> Calls { get; } = [];

        public RemoteStatus GetStatus() => new(10, 12.5, 8, 16, 2, 1500, null, null,
            [new RemoteAccount(7, "alpha", "Running", "", 3, 700, 60), new RemoteAccount(8, "beta", "Offline", "", 0, 0, 0)]);

        public bool Launch(long accountId) => Record($"launch:{accountId}", accountId == 7);

        public bool Stop(long accountId) => Record($"stop:{accountId}", accountId == 7);

        public bool Restart(long accountId) => Record($"restart:{accountId}", accountId == 7);

        public int CloseAll()
        {
            Calls.Add("closeall");
            return 2;
        }

        public void ScheduleShutdown(int minutes) => Calls.Add($"shutdown:{minutes}");

        public void CancelSchedule() => Calls.Add("cancel");

        private bool Record(string call, bool result)
        {
            if (result)
            {
                Calls.Add(call);
            }

            return result;
        }
    }
}
