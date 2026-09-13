using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FcadServe.Tests;

/// <summary>
/// HTTP-level tests against the real Program via WebApplicationFactory.
/// Configuration is injected through process environment variables scoped to
/// each test (test methods in a class run sequentially, so restoration in
/// finally keeps other classes unaffected). FreeCAD is stubbed with a slow
/// shell script ("exec" mode) so the queue can drain without a real FreeCAD.
/// </summary>
public class ApiEndpointsTests
{
    private sealed class EnvScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new();
        private readonly string[] _keys;

        public EnvScope((string Key, string? Value)[] overrides)
        {
            _keys = overrides.Select(o => o.Key).ToArray();
            foreach (var (key, value) in overrides)
            {
                _previous[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            foreach (var key in _keys)
                Environment.SetEnvironmentVariable(key, _previous.TryGetValue(key, out var v) ? v : null);
        }
    }

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "fcadserve-" + Guid.NewGuid().ToString("N"));
        public string Input => Path.Combine(Root, "in");
        public string Output => Path.Combine(Root, "out");
        public string State => Path.Combine(Root, "state");
        public string Uploads => Path.Combine(State, "uploads");
        public string Stl => Path.Combine(Input, "part.stl");
        public string WorkerScript => Path.Combine(Root, "worker-fixture.sh");
        public WebApplicationFactory<Program> Factory { get; }

        private readonly TimeSpan _jobTimeout;
        private readonly int _maxInputBytes;
        private readonly int _queueCapacity;

        public Sandbox(TimeSpan jobTimeout, int maxInputBytes = 1_000_000, bool sleep = true, int queueCapacity = 100)
        {
            _jobTimeout = jobTimeout;
            _maxInputBytes = maxInputBytes;
            _queueCapacity = queueCapacity;
            Directory.CreateDirectory(Input);
            Directory.CreateDirectory(Output);
            Directory.CreateDirectory(State);
            File.WriteAllBytes(Stl, new byte[256]);
            File.WriteAllText(WorkerScript, sleep ? "#!/bin/sh\nsleep 300\n" : "#!/bin/sh\nexit 0\n");

            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(_ => { });
        }

        public HttpClient Client => Factory.CreateClient();

        public EnvScope Scope() => new(
        [
            ("FCADSERVE_STATE_ROOT", State),
            ("FCADSERVE_ALLOWED_INPUT_ROOTS", Input),
            ("FCADSERVE_ALLOWED_OUTPUT_ROOTS", Output),
            ("FCADSERVE_FREECAD__MODE", "exec"),
            ("FCADSERVE_FREECAD__COMMAND", "/bin/sh"),
            ("FCADSERVE_WORKER_SCRIPT", WorkerScript),
            ("FCADSERVE_RENDER_SCRIPT", "/bin/true"),
            ("FCADSERVE_JOBS__TIMEOUT_SECONDS", ((int)_jobTimeout.TotalSeconds).ToString()),
            ("FCADSERVE_JOBS__MAX_INPUT_SIZE_BYTES", _maxInputBytes.ToString()),
            ("FCADSERVE_JOBS__QUEUE_CAPACITY", _queueCapacity.ToString()),
        ]);

        public void Dispose()
        {
            try
            {
                Factory.Dispose();
            }
            finally
            {
                try { Directory.Delete(Root, true); } catch { /* best effort */ }
            }
        }
    }

    private static StringContent Snake(string stl, string? output = null)
    {
        var body = JsonSerializer.Serialize(new { stl_path = stl, output_dir = output });
        return new StringContent(body, Encoding.UTF8, "application/json");
    }

    private static StringContent Camel(string stl, string? output = null)
    {
        var body = JsonSerializer.Serialize(new { stlPath = stl, outputDir = output });
        return new StringContent(body, Encoding.UTF8, "application/json");
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync());

    [Fact]
    public async Task Submit_snake_case_body_returns_202_and_queues()
    {
        using var box = new Sandbox(TimeSpan.FromMinutes(5));
        using var env = box.Scope();

        var resp = await box.Client.PostAsync("/v1/jobs", Snake(box.Stl, box.Output));
        Assert.Equal(System.Net.HttpStatusCode.Accepted, resp.StatusCode);

        using var doc = await ReadAsync(resp);
        var jobId = doc.RootElement.GetProperty("job_id").GetString()!;
        Assert.Equal("queued", doc.RootElement.GetProperty("status").GetString());
        Assert.EndsWith($"/v1/jobs/{jobId}", doc.RootElement.GetProperty("status_url").GetString());
    }

    [Fact]
    public async Task Submit_camelCase_body_is_also_accepted()
    {
        using var box = new Sandbox(TimeSpan.FromMinutes(5));
        using var env = box.Scope();

        var resp = await box.Client.PostAsync("/v1/jobs", Camel(box.Stl, box.Output));
        Assert.Equal(System.Net.HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Theory]
    [InlineData("{}", "missing_stl_path")]
    [InlineData("{\"stl_path\":\"relative.stl\"}", "relative_path")]
    [InlineData("{\"stl_path\":\"/nonexistent/x.stl\",\"output_dir\":\"/tmp\"}", "missing_file")]
    [InlineData("{\"stl_path\":\"/x/y.obj\"}", "unsupported_extension")]
    public async Task Submit_invalid_bodies_return_400_with_error_code(string body, string expectedCode)
    {
        using var box = new Sandbox(TimeSpan.FromMinutes(5));
        using var env = box.Scope();

        var resp = await box.Client.PostAsync("/v1/jobs", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);

        using var doc = await ReadAsync(resp);
        Assert.Equal(expectedCode, doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Submit_input_outside_allowed_root_is_rejected()
    {
        using var box = new Sandbox(TimeSpan.FromMinutes(5));
        var outside = Path.Combine(box.Root, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "evil.stl"), "x");
        using var env = box.Scope();

        var resp = await box.Client.PostAsync("/v1/jobs", Snake(Path.Combine(outside, "evil.stl"), box.Output));
        using var doc = await ReadAsync(resp);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("input_outside_allowed_root", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Submit_input_too_large_is_rejected()
    {
        using var box = new Sandbox(TimeSpan.FromMinutes(5), maxInputBytes: 64); // stl is 256 bytes
        using var env = box.Scope();

        var resp = await box.Client.PostAsync("/v1/jobs", Snake(box.Stl, box.Output));
        using var doc = await ReadAsync(resp);
        Assert.Equal("input_too_large", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Submit_output_outside_allowed_root_is_rejected()
    {
        using var box = new Sandbox(TimeSpan.FromMinutes(5));
        var elsewhere = Path.Combine(box.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        using var env = box.Scope();

        var resp = await box.Client.PostAsync("/v1/jobs", Snake(box.Stl, elsewhere));
        using var doc = await ReadAsync(resp);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("output_outside_allowed_root", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Submit_when_artifact_already_exists_returns_409()
    {
        using var box = new Sandbox(TimeSpan.FromMinutes(5));
        File.WriteAllText(Path.Combine(box.Output, "part.stp"), "existing");
        using var env = box.Scope();

        var resp = await box.Client.PostAsync("/v1/jobs", Snake(box.Stl, box.Output));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, resp.StatusCode);
        using var doc = await ReadAsync(resp);
        Assert.Equal("output_already_exists", doc.RootElement.GetProperty("code").GetString());
    }

    private static ByteArrayContent UploadBody(byte[] bytes) => new(bytes);

    private static readonly string SampleStl = new string('\x00', 80)
        + "\x00\x00\x00\x00\x01\x00\x00\x00"         // facet count = 1
        + "\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00" // normal
        + "\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00" // v1
        + "\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00" // v2
        + "\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00" // v3
        + "\x00\x00";

    [Fact]
    public async Task Upload_stores_stl_and_returned_path_can_be_submitted()
    {
        using var box = new Sandbox(TimeSpan.FromMinutes(5));
        using var env = box.Scope();

        var bytes = Encoding.UTF8.GetBytes(SampleStl);
        var upload = await box.Client.PostAsync("/v1/uploads?name=part.stl", UploadBody(bytes));
        Assert.Equal(System.Net.HttpStatusCode.Created, upload.StatusCode);

        using var doc = await ReadAsync(upload);
        var stlPath = doc.RootElement.GetProperty("stl_path").GetString()!;
        Assert.StartsWith(box.Uploads, stlPath);
        Assert.Equal("part.stl", doc.RootElement.GetProperty("name").GetString());
        Assert.EndsWith(".stl", stlPath);
        Assert.Equal(bytes.Length, doc.RootElement.GetProperty("size_bytes").GetInt64());
        Assert.Equal(ComputeSha256(bytes), doc.RootElement.GetProperty("sha256").GetString());
        Assert.True(File.Exists(stlPath));

        var submit = await box.Client.PostAsync("/v1/jobs", Snake(stlPath, box.Output));
        Assert.Equal(System.Net.HttpStatusCode.Accepted, submit.StatusCode);
        using var submitDoc = await ReadAsync(submit);
        Assert.Equal("queued", submitDoc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Upload_content_disposition_filename_is_used_when_no_query_name()
    {
        using var box = new Sandbox(TimeSpan.FromMinutes(5));
        using var env = box.Scope();

        var upload = await box.Client.PostAsync("/v1/uploads",
            new ByteArrayContent(Encoding.UTF8.GetBytes(SampleStl))
            {
                Headers = { { "Content-Disposition", "attachment; filename=\"from-header.stl\"" } },
            });
        Assert.Equal(System.Net.HttpStatusCode.Created, upload.StatusCode);
        using var doc = await ReadAsync(upload);
        Assert.Equal("from-header.stl", doc.RootElement.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("/v1/uploads", "missing_filename")]
    [InlineData("/v1/uploads?name=part.obj", "unsupported_extension")]
    public async Task Upload_invalid_requests_are_rejected(string url, string expectedCode)
    {
        using var box = new Sandbox(TimeSpan.FromMinutes(5));
        using var env = box.Scope();

        var resp = await box.Client.PostAsync(url, UploadBody(Encoding.UTF8.GetBytes("x")));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        using var doc = await ReadAsync(resp);
        Assert.Equal(expectedCode, doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Upload_path_traversal_name_is_sanitized_and_stored_inside_root()
    {
        using var box = new Sandbox(TimeSpan.FromMinutes(5));
        using var env = box.Scope();

        var resp = await box.Client.PostAsync("/v1/uploads?name=..%2F..%2Fevil.stl", UploadBody(Encoding.UTF8.GetBytes("x")));
        Assert.Equal(System.Net.HttpStatusCode.Created, resp.StatusCode);
        using var doc = await ReadAsync(resp);
        var stlPath = doc.RootElement.GetProperty("stl_path").GetString()!;
        Assert.Equal(".stl", Path.GetExtension(stlPath));
        Assert.StartsWith(box.Uploads, stlPath);
    }

    [Fact]
    public async Task Upload_exceeding_max_input_size_returns_413()
    {
        using var box = new Sandbox(TimeSpan.FromMinutes(5), maxInputBytes: 32);
        using var env = box.Scope();

        var resp = await box.Client.PostAsync("/v1/uploads?name=big.stl", UploadBody(new byte[64]));
        Assert.Equal(System.Net.HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        using var doc = await ReadAsync(resp);
        Assert.Equal("input_too_large", doc.RootElement.GetProperty("code").GetString());
    }

    private static string ComputeSha256(byte[] bytes)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(bytes));
    }

    [Fact]
    public async Task Unknown_job_returns_404_on_all_endpoints()
    {
        using var box = new Sandbox(TimeSpan.FromMinutes(5));
        using var env = box.Scope();

        foreach (var url in new[]
                 {
                     "/v1/jobs/j_missing",
                     "/v1/jobs/j_missing/artifacts",
                     "/v1/jobs/j_missing/artifacts/x.stp",
                 })
        {
            var resp = await box.Client.GetAsync(url);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
            using var doc = await ReadAsync(resp);
            Assert.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
        }

        var cancel = await box.Client.PostAsync("/v1/jobs/j_missing/cancel", new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, cancel.StatusCode);
    }

    [Fact]
    public async Task Job_runs_to_failed_timeout_and_job_api_observes_running_then_terminal()
    {
        using var box = new Sandbox(TimeSpan.FromSeconds(2));
        using var env = box.Scope();

        var submit = await box.Client.PostAsync("/v1/jobs", Snake(box.Stl, box.Output));
        using var submitDoc = await ReadAsync(submit);
        var jobId = submitDoc.RootElement.GetProperty("job_id").GetString()!;

        var result = await PollJobAsync(box.Client, jobId, seconds: 30);
        Assert.True(result.Terminal, "job should reach a terminal state");
        Assert.Equal("failed", result.Status);
        Assert.Equal("job_timeout", result.ErrorCode);
    }

    [Fact]
    public async Task Cancel_queued_running_job_terminates_it()
    {
        using var box = new Sandbox(TimeSpan.FromMinutes(5)); // long budget; script sleeps 300s
        using var env = box.Scope();

        var submit = await box.Client.PostAsync("/v1/jobs", Snake(box.Stl, box.Output));
        using var submitDoc = await ReadAsync(submit);
        var jobId = submitDoc.RootElement.GetProperty("job_id").GetString()!;

        var cancel = await box.Client.PostAsync($"/v1/jobs/{jobId}/cancel", new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.OK, cancel.StatusCode);
        using var cancelDoc = await ReadAsync(cancel);
        Assert.True(cancelDoc.RootElement.GetProperty("cancel_requested").GetBoolean());

        var result = await PollJobAsync(box.Client, jobId, seconds: 20);
        Assert.True(result.Terminal, "cancelled job should reach a terminal state");
        Assert.Equal("cancelled", result.Status);
        Assert.Equal("cancelled", result.ErrorCode);
    }

    [Fact]
    public async Task Submit_beyond_queue_capacity_returns_429()
    {
        using var box = new Sandbox(TimeSpan.FromMinutes(5), queueCapacity: 1); // 1 worker busy + 1 queued
        using var env = box.Scope();

        var a = await box.Client.PostAsync("/v1/jobs", Snake(box.Stl, box.Output));
        Assert.Equal(System.Net.HttpStatusCode.Accepted, a.StatusCode);
        // ensure the first job is running, then fill the single queued slot
        await Task.Delay(300);
        var b = await box.Client.PostAsync("/v1/jobs", Snake(box.Stl, Path.Combine(box.Output, "b")));
        Assert.Equal(System.Net.HttpStatusCode.Accepted, b.StatusCode);
        var c = await box.Client.PostAsync("/v1/jobs", Snake(box.Stl, Path.Combine(box.Output, "c")));
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, c.StatusCode);
        using var doc = await ReadAsync(c);
        Assert.Equal("queue_full", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Healthz_and_readyz_respond()
    {
        using var box = new Sandbox(TimeSpan.FromMinutes(5));
        using var env = box.Scope();

        var healthz = await box.Client.GetAsync("/healthz");
        Assert.Equal(System.Net.HttpStatusCode.OK, healthz.StatusCode);
        using var h = await ReadAsync(healthz);
        Assert.Equal("ok", h.RootElement.GetProperty("status").GetString());

        var readyz = await box.Client.GetAsync("/readyz");
        Assert.True(readyz.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.ServiceUnavailable);
        using var r = await ReadAsync(readyz);
        Assert.True(r.RootElement.GetProperty("ready").GetBoolean() || !r.RootElement.GetProperty("ready").GetBoolean());
        var checks = r.RootElement.GetProperty("checks");
        foreach (var name in new[] { "database", "state_root", "xvfb", "worker_script", "freecad", "input_roots", "output_policy" })
            Assert.True(checks.TryGetProperty(name, out _), $"readyz missing check: {name}");
    }

    private static async Task<JobView> PollJobAsync(HttpClient client, string jobId, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var lastStatus = "";
        while (DateTime.UtcNow < deadline)
        {
            var resp = await client.GetAsync($"/v1/jobs/{jobId}");
            var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            lastStatus = doc.RootElement.GetProperty("status").GetString()!;
            var errorCode = doc.RootElement.TryGetProperty("error_code", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (lastStatus is "succeeded" or "skipped" or "failed" or "cancelled")
            {
                return new JobView(true, lastStatus, errorCode);
            }
            doc.Dispose();
            await Task.Delay(200);
        }
        throw new Xunit.Sdk.XunitException($"job did not reach terminal state within {seconds}s (status={lastStatus})");
    }

    private sealed record JobView(bool Terminal, string Status, string? ErrorCode);
}