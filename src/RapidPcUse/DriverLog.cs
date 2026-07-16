using System.Collections;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RapidPcUse;

internal static class DriverLog
{
    private const long MaximumBytes = 4_000_000;
    private const int ArchiveCount = 3;
    private static readonly object Gate = new();
    private static readonly Mutex CrossProcessGate = new(false, @"Local\RapidPcUse.Log.v2");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    internal static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RapidPcUse",
        "rapid-pc-use.log");

    internal static string SessionId { get; } =
        $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Environment.ProcessId}-{Guid.NewGuid():N}"[..38];

    internal static string NewOperationId(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 13, prefix.Length + 33)];

    internal static void Info(
        string eventName,
        string message,
        string? operationId = null,
        string? tool = null,
        string? failureId = null,
        object? data = null)
        => Write("INFO", eventName, message, operationId, tool, failureId, data, null);

    internal static void Warning(
        string eventName,
        string message,
        string? operationId = null,
        string? tool = null,
        string? failureId = null,
        object? data = null,
        Exception? exception = null)
        => Write("WARN", eventName, message, operationId, tool, failureId, data, exception);

    internal static void Error(
        string eventName,
        string message,
        Exception exception,
        string? operationId = null,
        string? tool = null,
        string? failureId = null,
        object? data = null)
        => Write("ERROR", eventName, message, operationId, tool, failureId, data, exception);

    private static void Write(
        string level,
        string eventName,
        string message,
        string? operationId,
        string? tool,
        string? failureId,
        object? data,
        Exception? exception)
    {
        try
        {
            var entry = new Dictionary<string, object?>
            {
                ["timestamp"] = DateTimeOffset.Now.ToString("O"),
                ["level"] = level,
                ["event"] = eventName,
                ["message"] = message,
                ["version"] = BuildInfo.Version,
                ["pid"] = Environment.ProcessId,
                ["session_id"] = SessionId,
            };
            AddIfPresent(entry, "operation_id", operationId);
            AddIfPresent(entry, "tool", tool);
            AddIfPresent(entry, "failure_id", failureId);
            if (data is not null)
            {
                entry["data"] = data;
            }

            if (exception is not null)
            {
                entry["error"] = ExceptionDetails(exception);
            }

            var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
            Append(line);
        }
        catch
        {
            // Diagnostics must never interfere with desktop control.
        }
    }

    private static void Append(string line)
    {
        lock (Gate)
        {
            var ownsMutex = false;
            try
            {
                try
                {
                    ownsMutex = CrossProcessGate.WaitOne(TimeSpan.FromMilliseconds(150));
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }

                if (!ownsMutex)
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                RotateIfNeeded();
                File.AppendAllText(FilePath, line, new UTF8Encoding(false));
            }
            finally
            {
                if (ownsMutex)
                {
                    CrossProcessGate.ReleaseMutex();
                }
            }
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(FilePath) || new FileInfo(FilePath).Length < MaximumBytes)
        {
            return;
        }

        for (var index = ArchiveCount - 1; index >= 1; index--)
        {
            var source = $"{FilePath}.{index}";
            var destination = $"{FilePath}.{index + 1}";
            if (File.Exists(source))
            {
                File.Move(source, destination, true);
            }
        }

        File.Move(FilePath, $"{FilePath}.1", true);
    }

    internal static Dictionary<string, object?> ExceptionDetails(Exception exception)
    {
        var chain = new List<object>();
        for (Exception? current = exception; current is not null && chain.Count < 8; current = current.InnerException)
        {
            var item = new Dictionary<string, object?>
            {
                ["type"] = current.GetType().FullName,
                ["hresult"] = $"0x{current.HResult:X8}",
            };

            if (current is Win32Exception win32)
            {
                item["native_error_code"] = win32.NativeErrorCode;
            }

            if (current.Data.Count > 0)
            {
                var exceptionData = new Dictionary<string, object?>();
                foreach (DictionaryEntry entry in current.Data)
                {
                    if (entry.Key is string key && SafeExceptionDataKeys.Contains(key) && IsSafeScalar(entry.Value))
                    {
                        exceptionData[key] = entry.Value;
                    }
                }

                if (exceptionData.Count > 0)
                {
                    item["data"] = exceptionData;
                }
            }

            chain.Add(item);
        }

        return new Dictionary<string, object?>
        {
            ["chain"] = chain,
            ["details_redacted"] = true,
        };
    }

    private static readonly HashSet<string> SafeExceptionDataKeys = new(StringComparer.Ordinal)
    {
        "action_index",
        "action_type",
        "normalized_x",
        "normalized_y",
        "target_pixel_x",
        "target_pixel_y",
        "cursor_before_pixel_x",
        "cursor_before_pixel_y",
        "virtual_desktop_left",
        "virtual_desktop_top",
        "virtual_desktop_width",
        "virtual_desktop_height",
        "requested_input_events",
        "accepted_input_events",
    };

    private static bool IsSafeScalar(object? value)
        => value is null or bool or byte or sbyte or short or ushort or int or uint or long or ulong;

    private static void AddIfPresent(Dictionary<string, object?> entry, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            entry[name] = value;
        }
    }
}
