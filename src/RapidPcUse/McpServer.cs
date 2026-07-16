using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RapidPcUse;

internal sealed class McpServer(DesktopController desktop, TextReader input, TextWriter output)
{
    private const string ServerName = "rapid-pc-use";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    internal void Run()
    {
        while (true)
        {
            string? line;
            try
            {
                line = ReadBoundedLine(input);
            }
            catch (ProtocolLimitException exception)
            {
                DriverLog.Warning("mcp.message_rejected", "The driver rejected an oversized JSON-RPC message.", exception: exception);
                WriteProtocolError(null, -32600, "The JSON-RPC message exceeded the server limit.");
                continue;
            }

            if (line is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var request = JsonDocument.Parse(line);
                HandleMessage(request.RootElement);
            }
            catch (Exception exception)
            {
                DriverLog.Error("mcp.invalid_message", "The driver received an invalid JSON-RPC message.", exception);
                WriteProtocolError(null, -32700, "Invalid JSON-RPC message.");
            }
        }
    }

    private void HandleMessage(JsonElement request)
    {
        var hasId = request.TryGetProperty("id", out var id);
        if (hasId && !IsValidRequestId(id))
        {
            WriteProtocolError(null, -32600, "The JSON-RPC request ID is invalid or too long.");
            return;
        }

        var method = request.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String
            ? methodElement.GetString()
            : null;

        if (method is null)
        {
            if (hasId)
            {
                WriteProtocolError(id, -32600, "JSON-RPC method is required.");
            }

            return;
        }

        if (!hasId)
        {
            return;
        }

        try
        {
            var parameters = request.TryGetProperty("params", out var paramsElement) ? paramsElement : default;
            object result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new Dictionary<string, object?>(),
                "tools/list" => new Dictionary<string, object?> { ["tools"] = ToolDefinitions() },
                "tools/call" => CallTool(parameters),
                _ => throw new MethodNotFoundException(method),
            };
            WriteResult(id, result);
        }
        catch (MethodNotFoundException exception)
        {
            DriverLog.Warning("mcp.method_not_found", "The MCP client requested an unsupported method.", exception: exception);
            WriteProtocolError(id, -32601, "Method not found.");
        }
        catch (Exception exception)
        {
            DriverLog.Error("mcp.request_failed", "An MCP request failed outside the PC tool boundary.", exception);
            WriteProtocolError(id, -32603, "Internal JSON-RPC error.");
        }
    }

    private static Dictionary<string, object?> Initialize(JsonElement parameters)
    {
        var protocolVersion = parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("protocolVersion", out var version) &&
            version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : "2025-11-25";

        return new Dictionary<string, object?>
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["tools"] = new Dictionary<string, object?> { ["listChanged"] = false },
            },
            ["serverInfo"] = new Dictionary<string, object?>
            {
                ["name"] = ServerName,
                ["version"] = BuildInfo.Version,
                ["description"] = "Fast native Windows screenshot, mouse, keyboard, and takeover control.",
            },
            ["instructions"] = "Start with pc_observe(begin_control=true); capture without the visible control cue is not permitted. Use monitor-local normalized screenshot coordinates 0..1000. Each frame is valid for 30 seconds and one action batch. Native tool calls require user approval unless the user explicitly installed fast mode. Batch only deterministic actions. RAPID_PC_USE_FAILURE is terminal for the task: make no more PC or target-app actions, perform only the single bounded log read it requests, then give the user a brief plain-language summary without investigating. Physical Escape means the user took over; end the model turn immediately without reading logs.",
        };
    }

    private object CallTool(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("tools/call requires a tool name.");
        }

        var name = nameElement.GetString()!;
        if (name is not ("pc_observe" or "pc_act" or "pc_stop"))
        {
            throw new MethodNotFoundException("Unknown tool.");
        }

        var arguments = parameters.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Object
            ? args
            : default;
        var operationId = DriverLog.NewOperationId("op");
        var requestSummary = SafeRequestSummary(name, arguments);
        var stopwatch = Stopwatch.StartNew();
        DriverLog.Info(
            "tool.started",
            $"{name} started.",
            operationId: operationId,
            tool: name,
            data: requestSummary);

        try
        {
            var outcome = name switch
            {
                "pc_observe" => Observe(arguments),
                "pc_act" => Act(arguments),
                "pc_stop" => Stop(),
                _ => throw new MethodNotFoundException("Unknown tool."),
            };
            stopwatch.Stop();
            DriverLog.Info(
                "tool.completed",
                $"{name} completed successfully.",
                operationId: operationId,
                tool: name,
                data: new { elapsed_ms = stopwatch.ElapsedMilliseconds, request = requestSummary, result = outcome.LogData });
            return outcome.Result;
        }
        catch (UserTakeoverException)
        {
            stopwatch.Stop();
            DriverLog.Info(
                "tool.user_takeover",
                $"{name} ended because the user pressed physical Escape.",
                operationId: operationId,
                tool: name,
                data: new { elapsed_ms = stopwatch.ElapsedMilliseconds });
            return ToolText(
                "USER_TAKEOVER: The user is now operating the PC. Stop immediately, make no more PC-use calls in this turn, and end the turn.",
                isError: false);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            var failureId = DriverLog.NewOperationId("failure");
            var simpleSummary = PlainLanguageSummary(exception);
            DriverLog.Error(
                "tool.failed",
                $"{name} failed. {simpleSummary}",
                exception,
                operationId: operationId,
                tool: name,
                failureId: failureId,
                data: new { elapsed_ms = stopwatch.ElapsedMilliseconds, request = requestSummary });

            var released = true;
            try
            {
                desktop.Stop();
                DriverLog.Info(
                    "tool.failure_control_released",
                    "PC control was released after the failed tool call.",
                    operationId: operationId,
                    tool: name,
                    failureId: failureId);
            }
            catch (Exception stopException)
            {
                released = false;
                DriverLog.Error(
                    "tool.failure_cleanup_failed",
                    "The driver encountered another error while releasing PC control after a failure.",
                    stopException,
                    operationId: operationId,
                    tool: name,
                    failureId: failureId);
            }

            var controlState = released
                ? "The driver released mouse and keyboard control."
                : "The driver attempted to release mouse and keyboard control; cleanup also reported an error in the same log.";
            return ToolText(
                $"RAPID_PC_USE_FAILURE\n{simpleSummary} {controlState}\nFailure ID: {failureId}\nLog: {DriverLog.FilePath}\nREQUIRED: Stop this PC task now. Make no further PC-use, target-app, shell-automation, browser, CLI, or workaround calls. Use exactly one bounded read-only command to read the log entry matching this failure ID, then tell the user in 1-3 plain sentences what failed and what remains unconfirmed. Do not retry, investigate, search the web, inspect processes/source code, or attempt a fix.",
                isError: true);
        }
    }

    private ToolOutcome Observe(JsonElement arguments)
    {
        var beginControl = OptionalBoolean(arguments, "begin_control", true);
        var observation = desktop.Observe(beginControl);
        return new ToolOutcome(ObservationResult(observation), ObservationLogData(observation));
    }

    private ToolOutcome Act(JsonElement arguments)
    {
        var frameId = RequiredInt64(arguments, "frame_id");
        if (!arguments.TryGetProperty("actions", out var actions))
        {
            throw new ArgumentException("pc_act requires actions.");
        }

        var settleMilliseconds = OptionalInteger(arguments, "settle_ms", 35);
        var observeAfter = OptionalBoolean(arguments, "observe_after", true);
        var observation = desktop.Act(frameId, actions, settleMilliseconds, observeAfter);
        return observation is null
            ? new ToolOutcome(
                ToolText("Actions completed without observation. Use pc_observe before any coordinate-dependent action.", isError: false),
                new { returned_frame = false })
            : new ToolOutcome(ObservationResult(observation), ObservationLogData(observation));
    }

    private ToolOutcome Stop()
    {
        desktop.Stop();
        return new ToolOutcome(
            ToolText("PC control ended. The native mouse and keyboard are released.", isError: false),
            new { control_active = false });
    }

    private static object ObservationLogData(Observation observation) => new
    {
        frame_id = observation.FrameId,
        control_active = observation.ControlActive,
        display_count = observation.Frames.Count,
        total_capture_ms = observation.TotalMilliseconds,
        encoded_bytes = observation.Frames.Sum(frame => frame.Bytes.Length),
    };

    private static Dictionary<string, object?> ObservationResult(Observation observation)
    {
        var content = new List<object>();
        var manifest = new
        {
            frame_id = observation.FrameId,
            coordinate_space = "monitor-local normalized integers: x and y each range from 0 to 1000",
            control_active = observation.ControlActive,
            total_capture_ms = observation.TotalMilliseconds,
            displays = observation.Frames.Select(frame => new
            {
                display_id = frame.Monitor.Id,
                device = frame.Monitor.DeviceName,
                primary = frame.Monitor.IsPrimary,
                virtual_origin_px = new[] { frame.Monitor.Left, frame.Monitor.Top },
                native_size_px = new[] { frame.Monitor.Width, frame.Monitor.Height },
                encoded_size_px = new[] { frame.EncodedWidth, frame.EncodedHeight },
                capture_ms = frame.CaptureMilliseconds,
            }).ToArray(),
        };

        content.Add(new Dictionary<string, object?>
        {
            ["type"] = "text",
            ["text"] = "RAPID_PC_FRAME " + JsonSerializer.Serialize(manifest, SerializerOptions),
        });

        foreach (var frame in observation.Frames)
        {
            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = $"{frame.Monitor.Id} ({frame.EncodedWidth}x{frame.EncodedHeight} encoded; {frame.Monitor.Width}x{frame.Monitor.Height} native). Use x/y 0..1000 relative to this image.",
            });
            content.Add(new Dictionary<string, object?>
            {
                ["type"] = "image",
                ["data"] = Convert.ToBase64String(frame.Bytes),
                ["mimeType"] = frame.MimeType,
                ["_meta"] = new Dictionary<string, object?> { ["codex/imageDetail"] = "original" },
            });
        }

        return new Dictionary<string, object?>
        {
            ["content"] = content,
            ["isError"] = false,
        };
    }

    private static IReadOnlyList<object> ToolDefinitions() =>
    [
        new Dictionary<string, object?>
        {
            ["name"] = "pc_observe",
            ["description"] = "Capture every Windows display as separate images while showing the user an Esc takeover cue. begin_control must be true. Returns a single-use frame_id that expires after 30 seconds and exact display metadata. Coordinates for pc_act are normalized monitor-local integers from 0 to 1000.",
            ["inputSchema"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["begin_control"] = new Dictionary<string, object?>
                    {
                        ["type"] = "boolean",
                        ["const"] = true,
                        ["description"] = "Required. Acquire PC control and show the control overlay.",
                    },
                },
                ["required"] = new[] { "begin_control" },
                ["additionalProperties"] = false,
            },
            ["annotations"] = new Dictionary<string, object?>
            {
                ["title"] = "Observe Windows displays",
                ["readOnlyHint"] = false,
                ["destructiveHint"] = false,
                ["idempotentHint"] = false,
                ["openWorldHint"] = true,
            },
        },
        new Dictionary<string, object?>
        {
            ["name"] = "pc_act",
            ["description"] = "Execute one bounded batch of real native Windows input, consuming the latest unexpired frame_id, then optionally return fresh screenshots. The client prompts for approval unless fast mode was explicitly enabled. Physical Escape immediately cancels and releases held inputs.",
            ["inputSchema"] = ActSchema(),
            ["annotations"] = new Dictionary<string, object?>
            {
                ["title"] = "Act on Windows and observe",
                ["readOnlyHint"] = false,
                ["destructiveHint"] = true,
                ["idempotentHint"] = false,
                ["openWorldHint"] = true,
            },
        },
        new Dictionary<string, object?>
        {
            ["name"] = "pc_stop",
            ["description"] = "End the active PC control session, hide the visual cue, and release every driver-held key or mouse button. Always call when the PC-use task finishes.",
            ["inputSchema"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>(),
                ["additionalProperties"] = false,
            },
            ["annotations"] = new Dictionary<string, object?>
            {
                ["title"] = "End Windows control",
                ["readOnlyHint"] = false,
                ["destructiveHint"] = false,
                ["idempotentHint"] = true,
                ["openWorldHint"] = false,
            },
        },
    ];

    private static Dictionary<string, object?> ActSchema()
    {
        var actionProperties = new Dictionary<string, object?>
        {
            ["type"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["enum"] = new[] { "move", "relative_move", "click", "mouse_down", "mouse_up", "drag", "scroll", "type", "key", "key_down", "key_up", "wait" },
            },
            ["display_id"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "Display ID from the latest observation." },
            ["x"] = CoordinateSchema("X coordinate 0..1000 in the selected display image, or relative delta for relative_move."),
            ["y"] = CoordinateSchema("Y coordinate 0..1000 in the selected display image, or relative delta for relative_move."),
            ["to_x"] = CoordinateSchema("Drag destination X, 0..1000."),
            ["to_y"] = CoordinateSchema("Drag destination Y, 0..1000."),
            ["button"] = new Dictionary<string, object?> { ["type"] = "string", ["enum"] = new[] { "left", "right", "middle", "x1", "x2" } },
            ["count"] = new Dictionary<string, object?> { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 3 },
            ["duration_ms"] = new Dictionary<string, object?> { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = SecurityLimits.MaxDragMilliseconds },
            ["scroll_y"] = new Dictionary<string, object?> { ["type"] = "integer", ["minimum"] = -100, ["maximum"] = 100, ["description"] = "Wheel ticks; positive scrolls down, negative up." },
            ["scroll_x"] = new Dictionary<string, object?> { ["type"] = "integer", ["minimum"] = -100, ["maximum"] = 100, ["description"] = "Wheel ticks; positive scrolls right, negative left." },
            ["text"] = new Dictionary<string, object?> { ["type"] = "string", ["maxLength"] = SecurityLimits.MaxTypedCodeUnitsPerAction, ["description"] = "Literal text typed as Unicode keystrokes, never clipboard paste." },
            ["interval_ms"] = new Dictionary<string, object?> { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = SecurityLimits.MaxTypeIntervalMilliseconds, ["description"] = "Delay per typed UTF-16 code unit; default 2 ms." },
            ["keys"] = new Dictionary<string, object?> { ["type"] = "string", ["maxLength"] = SecurityLimits.MaxKeyChordCharacters, ["description"] = "Key or '+'-joined chord, e.g. CTRL+L, ENTER, ALT+F4." },
            ["ms"] = new Dictionary<string, object?> { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = SecurityLimits.MaxWaitMilliseconds },
        };

        return new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["frame_id"] = new Dictionary<string, object?> { ["type"] = "integer", ["minimum"] = 1, ["description"] = "Latest frame_id returned by pc_observe or pc_act." },
                ["actions"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["maxItems"] = SecurityLimits.MaxActionsPerBatch,
                    ["items"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = actionProperties,
                        ["required"] = new[] { "type" },
                        ["additionalProperties"] = false,
                    },
                },
                ["settle_ms"] = new Dictionary<string, object?> { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = SecurityLimits.MaxWaitMilliseconds, ["description"] = "Short repaint delay after the batch; default 35 ms." },
                ["observe_after"] = new Dictionary<string, object?> { ["type"] = "boolean", ["description"] = "Return fresh screenshots in this same call; default true." },
            },
            ["required"] = new[] { "frame_id", "actions" },
            ["additionalProperties"] = false,
        };
    }

    private static Dictionary<string, object?> CoordinateSchema(string description) => new()
    {
        ["type"] = "integer",
        ["minimum"] = -32768,
        ["maximum"] = 32767,
        ["description"] = description,
    };

    private static object SafeRequestSummary(string tool, JsonElement arguments)
    {
        if (tool == "pc_observe")
        {
            return new { begin_control = OptionalBoolean(arguments, "begin_control", true) };
        }

        if (tool != "pc_act")
        {
            return new { };
        }

        var frameId = arguments.ValueKind == JsonValueKind.Object &&
                      arguments.TryGetProperty("frame_id", out var frame) &&
                      frame.TryGetInt64(out var parsedFrame)
            ? parsedFrame
            : (long?)null;
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("actions", out var actions) ||
            actions.ValueKind != JsonValueKind.Array)
        {
            return new { frame_id = frameId, action_count = 0, action_types = Array.Empty<string>() };
        }

        var actionTypes = new List<string>();
        var typedCodeUnits = 0;
        foreach (var action in actions.EnumerateArray().Take(SecurityLimits.MaxActionsPerBatch))
        {
            if (action.ValueKind == JsonValueKind.Object &&
                action.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String)
            {
                var rawType = type.GetString();
                actionTypes.Add(rawType is not null && rawType.Length <= 32 && KnownActionTypes.Contains(rawType)
                    ? rawType
                    : "unknown");
            }
            else
            {
                actionTypes.Add("unknown");
            }

            if (action.ValueKind == JsonValueKind.Object &&
                action.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                typedCodeUnits = Math.Min(
                    SecurityLimits.MaxActionsPerBatch * SecurityLimits.MaxTypedCodeUnitsPerAction,
                    typedCodeUnits + (text.GetString()?.Length ?? 0));
            }
        }

        return new
        {
            frame_id = frameId,
            action_count = actions.GetArrayLength(),
            action_types = actionTypes,
            typed_code_units = typedCodeUnits,
            privacy = "Literal typed text and key values omitted.",
        };
    }

    private static string PlainLanguageSummary(Exception exception)
    {
        var chain = ExceptionChain(exception).ToArray();
        if (chain.Any(item => item is StaleFrameException))
        {
            return "The visible screen or display layout changed before the requested input could be applied.";
        }

        if (chain.Any(item => item is ArgumentOutOfRangeException))
        {
            return "A requested numeric value was outside the configured safety limits.";
        }

        var win32 = chain.OfType<Win32Exception>().FirstOrDefault();
        if (win32 is not null)
        {
            if (win32.Message.Contains("cursor", StringComparison.OrdinalIgnoreCase))
            {
                return "Windows rejected a native cursor movement, so the requested pointer action was not completed.";
            }

            if (win32.Message.Contains("input events", StringComparison.OrdinalIgnoreCase))
            {
                return "Windows rejected one or more native mouse or keyboard events, so the requested action was not completed.";
            }

            return "Windows rejected a desktop operation required by Rapid PC Use.";
        }

        if (chain.Any(item => item is ArgumentException))
        {
            return "Rapid PC Use received an invalid action request and did not apply it.";
        }

        return "Rapid PC Use could not complete the requested desktop operation.";
    }

    private static IEnumerable<Exception> ExceptionChain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static Dictionary<string, object?> ToolText(string text, bool isError) => new()
    {
        ["content"] = new object[]
        {
            new Dictionary<string, object?> { ["type"] = "text", ["text"] = text },
        },
        ["isError"] = isError,
    };

    private static bool OptionalBoolean(JsonElement arguments, string property, bool fallback)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static bool IsValidRequestId(JsonElement id)
        => id.ValueKind switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.Number => id.GetRawText().Length <= 64,
            JsonValueKind.String => id.GetString()?.Length <= 128,
            _ => false,
        };

    private static int OptionalInteger(JsonElement arguments, string property, int fallback)
        => arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(property, out var value) && value.TryGetInt32(out var result)
            ? result
            : fallback;

    private static long RequiredInt64(JsonElement arguments, string property)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(property, out var value) ||
            !value.TryGetInt64(out var result))
        {
            throw new ArgumentException($"'{property}' must be an integer.");
        }

        return result;
    }

    internal static string? ReadBoundedLine(
        TextReader reader,
        int maximumCharacters = SecurityLimits.MaxRequestLineCharacters)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 4096));
        while (true)
        {
            var value = reader.Read();
            if (value == -1)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            if (value == '\n')
            {
                return builder.ToString();
            }

            if (value == '\r')
            {
                continue;
            }

            if (builder.Length >= maximumCharacters)
            {
                while ((value = reader.Read()) is not (-1 or '\n'))
                {
                }

                throw new ProtocolLimitException();
            }

            builder.Append((char)value);
        }
    }

    private void WriteResult(JsonElement id, object result)
    {
        Write(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonSerializer.Deserialize<object>(id.GetRawText()),
            ["result"] = result,
        });
    }

    private void WriteProtocolError(JsonElement? id, int code, string message)
    {
        Write(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.HasValue ? JsonSerializer.Deserialize<object>(id.Value.GetRawText()) : null,
            ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
        });
    }

    private void Write(object message)
    {
        output.WriteLine(JsonSerializer.Serialize(message, SerializerOptions));
        output.Flush();
    }

    private sealed class MethodNotFoundException(string message) : Exception(message);

    private sealed class ProtocolLimitException()
        : Exception("The JSON-RPC message exceeded the configured character limit.");

    private sealed record ToolOutcome(object Result, object? LogData);

    private static readonly HashSet<string> KnownActionTypes = new(StringComparer.Ordinal)
    {
        "move",
        "relative_move",
        "click",
        "mouse_down",
        "mouse_up",
        "drag",
        "scroll",
        "type",
        "key",
        "key_down",
        "key_up",
        "wait",
    };
}
