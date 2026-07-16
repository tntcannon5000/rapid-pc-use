using System.Text.Json;
using RapidPcUse;

var tests = new (string Name, Action Run)[]
{
    ("stop invalidates an in-flight operation", StopInvalidatesOperation),
    ("physical takeover has a distinct terminal signal", TakeoverInvalidatesOperation),
    ("operation deadline is enforced", OperationDeadlineIsEnforced),
    ("idle sessions expire", IdleSessionExpires),
    ("protocol reader enforces a hard line limit", ProtocolReaderIsBounded),
    ("action batches enforce resource limits", ActionBatchesAreBounded),
    ("capture dimensions enforce resource limits", CaptureResourcesAreBounded),
    ("diagnostics redact exception-controlled data", DiagnosticsAreRedacted),
    ("bounded parser fuzz is deterministic", BoundedParserFuzz),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.GetType().Name}: {exception.Message}");
    }
}

foreach (var failure in failures)
{
    Console.Error.WriteLine(failure);
}

return failures.Count == 0 ? 0 : 1;

static void StopInvalidatesOperation()
{
    var clock = new ManualTimeProvider();
    var state = new ControlSessionState(clock);
    Assert(state.Start(), "session should start");
    var lease = state.BeginOperation(TimeSpan.FromSeconds(30));
    Assert(state.End(ControlEndReason.Stop), "session should stop");
    Expect<ControlSessionEndedException>(() => state.ThrowIfCannotContinue(lease));
}

static void TakeoverInvalidatesOperation()
{
    var state = new ControlSessionState();
    _ = state.Start();
    var lease = state.BeginOperation(TimeSpan.FromSeconds(30));
    _ = state.End(ControlEndReason.UserTakeover);
    Expect<UserTakeoverException>(() => state.ThrowIfCannotContinue(lease));
}

static void OperationDeadlineIsEnforced()
{
    var clock = new ManualTimeProvider();
    var state = new ControlSessionState(clock);
    _ = state.Start();
    var lease = state.BeginOperation(TimeSpan.FromSeconds(30));
    clock.Advance(TimeSpan.FromSeconds(31));
    Expect<TimeoutException>(() => state.ThrowIfCannotContinue(lease));
}

static void IdleSessionExpires()
{
    var clock = new ManualTimeProvider();
    var state = new ControlSessionState(clock);
    _ = state.Start();
    clock.Advance(TimeSpan.FromMinutes(4));
    Assert(state.TryExpireIdle(TimeSpan.FromMinutes(3)), "idle session should expire");
    Expect<ControlSessionEndedException>(() => state.BeginOperation(TimeSpan.FromSeconds(1)));
}

static void ProtocolReaderIsBounded()
{
    Assert(McpServer.ReadBoundedLine(new StringReader("abc\n"), 3) == "abc", "exact limit should pass");
    Assert(McpServer.ReadBoundedLine(new StringReader("\uFEFFabc\n"), 3) == "abc", "initial UTF-8 BOM should be ignored");
    Expect<Exception>(() => McpServer.ReadBoundedLine(new StringReader("abcd\nnext\n"), 3));
    Assert(McpServer.ReadBoundedLine(new StringReader(string.Empty), 3) is null, "EOF should return null");
}

static void ActionBatchesAreBounded()
{
    using var valid = JsonDocument.Parse("""
        [{"type":"type","text":"hello","interval_ms":2},{"type":"wait","ms":20}]
        """);
    DesktopController.ValidateActionPlan(valid.RootElement, 35);

    using var tooLong = JsonDocument.Parse($"[{{\"type\":\"type\",\"text\":\"{new string('x', SecurityLimits.MaxTypedCodeUnitsPerAction + 1)}\"}}]");
    Expect<ArgumentException>(() => DesktopController.ValidateActionPlan(tooLong.RootElement, 0));

    using var tooSlow = JsonDocument.Parse("[{\"type\":\"wait\",\"ms\":10000},{\"type\":\"wait\",\"ms\":10000},{\"type\":\"wait\",\"ms\":10000},{\"type\":\"wait\",\"ms\":1}]");
    Expect<ArgumentException>(() => DesktopController.ValidateActionPlan(tooSlow.RootElement, 0));

    using var tooMany = JsonDocument.Parse("[" + string.Join(',', Enumerable.Repeat("{\"type\":\"wait\",\"ms\":0}", SecurityLimits.MaxActionsPerBatch + 1)) + "]");
    Expect<ArgumentException>(() => DesktopController.ValidateActionPlan(tooMany.RootElement, 0));
}

static void CaptureResourcesAreBounded()
{
    var valid = new[] { new MonitorDescriptor("display-1", "device", 0, 0, 1920, 1080, true) };
    GdiScreenCaptureBackend.ValidateCaptureResources(valid);

    var excessive = new[] { new MonitorDescriptor("display-1", "device", 0, 0, 100_000, 100_000, true) };
    Expect<InvalidOperationException>(() => GdiScreenCaptureBackend.ValidateCaptureResources(excessive));

    var tooMany = Enumerable.Range(0, SecurityLimits.MaxDisplays + 1)
        .Select(index => new MonitorDescriptor($"display-{index}", "device", 0, 0, 1, 1, index == 0))
        .ToArray();
    Expect<InvalidOperationException>(() => GdiScreenCaptureBackend.ValidateCaptureResources(tooMany));
}

static void DiagnosticsAreRedacted()
{
    const string secret = "super-secret-request-value";
    var exception = new InvalidOperationException(secret);
    exception.Data["attacker_key"] = secret;
    exception.Data["action_index"] = 2;
    var serialized = JsonSerializer.Serialize(DriverLog.ExceptionDetails(exception));
    Assert(!serialized.Contains(secret, StringComparison.Ordinal), "secret exception content leaked");
    Assert(!serialized.Contains("attacker_key", StringComparison.Ordinal), "arbitrary exception data leaked");
    Assert(serialized.Contains("action_index", StringComparison.Ordinal), "safe numeric metadata was lost");
    Assert(serialized.Contains("details_redacted", StringComparison.Ordinal), "redaction marker is missing");
}

static void BoundedParserFuzz()
{
    var random = new Random(4271);
    for (var iteration = 0; iteration < 500; iteration++)
    {
        var length = random.Next(0, 128);
        var input = new string(Enumerable.Range(0, length).Select(_ => (char)random.Next(32, 127)).ToArray()) + "\n";
        var limit = random.Next(1, 64);
        try
        {
            var result = McpServer.ReadBoundedLine(new StringReader(input), limit);
            Assert(result is not null && result.Length <= limit, "reader returned an oversized line");
        }
        catch (Exception)
        {
            Assert(length > limit, "reader rejected an in-limit line");
        }
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Expect<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 7, 16, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    internal void Advance(TimeSpan duration) => _utcNow += duration;
}
