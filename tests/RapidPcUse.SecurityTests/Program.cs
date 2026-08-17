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
    ("action validation reports recoverable numeric correction data", ActionValidationReportsCorrectionData),
    ("capture dimensions enforce resource limits", CaptureResourcesAreBounded),
    ("capture resolution maps common aspect ratios to short-edge tiers", CaptureResolutionMapsAspectRatios),
    ("capture resolution preserves uncommon ratios and avoids upscaling", CaptureResolutionPreservesUncommonRatios),
    ("image context patch estimates are bounded and deterministic", ImagePatchEstimatesAreDeterministic),
    ("image context telemetry recognizes exact repeats without exposing hashes", ImageContextTelemetryRecognizesRepeats),
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

    var displays = new[] { new MonitorDescriptor("display-1", "device", 0, 0, 1920, 1080, true) };
    using var unknownDisplay = JsonDocument.Parse("[{\"type\":\"click\",\"display_id\":\"display-2\",\"x\":500,\"y\":500}]");
    Expect<PcActionPlanValidationException>(() => DesktopController.ValidateActionPlan(unknownDisplay.RootElement, 0, displays));
    using var unknownKey = JsonDocument.Parse("[{\"type\":\"key\",\"keys\":\"NOT_A_WINDOWS_KEY\"}]");
    Expect<PcActionPlanValidationException>(() => DesktopController.ValidateActionPlan(unknownKey.RootElement, 0, displays));
}

static void ActionValidationReportsCorrectionData()
{
    using var invalid = JsonDocument.Parse("[{\"type\":\"scroll\",\"scroll_y\":10001}]");
    try
    {
        DesktopController.ValidateActionPlan(invalid.RootElement, 35);
    }
    catch (PcActionPlanValidationException exception)
    {
        Assert(exception.Code == "numeric_out_of_range", "validation code is wrong");
        Assert(exception.ActionIndex == 1 && exception.ActionType == "scroll", "action location was lost");
        Assert(exception.Field == "scroll_y" && exception.SuppliedValue == 10001, "offending field/value was lost");
        Assert(
            exception.AllowedMinimum == SecurityLimits.MinScrollDeltaPerAction &&
            exception.AllowedMaximum == SecurityLimits.MaxScrollDeltaPerAction,
            "allowed scroll range was lost");
        Assert(DesktopController.ScrollTicks(591) == 6, "model-native scroll delta was not normalized");
        return;
    }

    throw new InvalidOperationException("Expected a recoverable action validation rejection.");
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

static void CaptureResolutionMapsAspectRatios()
{
    AssertResolution(1920, 1080, CaptureTier.Tier900, 1600, 900, "16:9");
    AssertResolution(2560, 1600, CaptureTier.Tier900, 1440, 900, "16:10");
    AssertResolution(1366, 768, CaptureTier.Tier900, 1280, 720, "16:9");
    AssertResolution(3440, 1440, CaptureTier.Tier900, 2150, 900, "43:18");
    AssertResolution(5120, 1440, CaptureTier.Tier900, 3200, 900, "32:9");
    AssertResolution(1080, 1920, CaptureTier.Tier900, 900, 1600, "16:9");
    AssertResolution(1600, 2560, CaptureTier.Tier900, 900, 1440, "16:10");
}

static void CaptureResolutionPreservesUncommonRatios()
{
    var uncommon = CaptureResolutionPolicy.Select(2000, 1000, CaptureTier.Tier900);
    Assert(uncommon.Width == 1800 && uncommon.Height == 900, "custom 2:1 display should preserve its aspect");
    Assert(uncommon.AspectClass.StartsWith("custom-", StringComparison.Ordinal), "custom ratio should be identified");

    var small = CaptureResolutionPolicy.Select(1024, 600, CaptureTier.Tier900);
    Assert(small.Width == 1024 && small.Height == 600 && !small.Resized, "sub-720 displays must not be upscaled");

    var native = CaptureResolutionPolicy.Select(3840, 2160, CaptureTier.Native);
    Assert(native.Width == 3840 && native.Height == 2160 && !native.Resized, "native tier should not resize");
}

static void ImagePatchEstimatesAreDeterministic()
{
    Assert(ContextTelemetry.Estimate32PixelPatches(1600, 900) == 1450, "1600x900 patch estimate is wrong");
    Assert(ContextTelemetry.Estimate32PixelPatches(1440, 900) == 1305, "1440x900 patch estimate is wrong");
    Assert(ContextTelemetry.Estimate32PixelPatches(1280, 720) == 920, "1280x720 patch estimate is wrong");
}

static void ImageContextTelemetryRecognizesRepeats()
{
    var telemetry = new ContextTelemetry();
    var monitor = new MonitorDescriptor("display-1", "device", 0, 0, 1920, 1080, true);
    var resolution = new CaptureResolution(1600, 900, 900, "16:9", true);
    var timings = new CaptureStageTimings(1, 2, 3, 4, 5, 6, 21);

    Observation ObservationWithBytes(long frameId, byte[] bytes) => new(
        frameId,
        "topology",
        [new ScreenFrame(frameId, monitor, 1600, 900, "image/jpeg", bytes, 1, resolution, timings)],
        1,
        true);

    var first = telemetry.Record(ObservationWithBytes(1, [1, 2, 3]));
    var repeat = telemetry.Record(ObservationWithBytes(2, [1, 2, 3]));
    var changed = telemetry.Record(ObservationWithBytes(3, [1, 2, 4]));

    Assert(!first.Images[0].ExactRepeatOfPrevious, "first image cannot be a repeat");
    Assert(repeat.Images[0].ExactRepeatOfPrevious && repeat.ExactRepeatImages == 1, "identical image was not recognized");
    Assert(!changed.Images[0].ExactRepeatOfPrevious && changed.ExactRepeatImages == 1, "changed image was marked as a repeat");
}

static void AssertResolution(
    int nativeWidth,
    int nativeHeight,
    CaptureTier tier,
    int expectedWidth,
    int expectedHeight,
    string expectedAspectClass)
{
    var result = CaptureResolutionPolicy.Select(nativeWidth, nativeHeight, tier);
    Assert(result.Width == expectedWidth && result.Height == expectedHeight,
        $"{nativeWidth}x{nativeHeight} mapped to {result.Width}x{result.Height}");
    Assert(result.AspectClass == expectedAspectClass,
        $"{nativeWidth}x{nativeHeight} mapped to {result.AspectClass}");
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
