using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RapidPcUse;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            DriverLog.Error(
                "driver.unhandled_exception",
                "An exception escaped the driver boundary.",
                eventArgs.ExceptionObject as Exception ?? new InvalidOperationException(eventArgs.ExceptionObject?.ToString()));

        try
        {
            DriverLog.Info(
                "driver.started",
                "Rapid PC Use started and is waiting for MCP requests.",
                data: new
                {
                    process_session_id = Process.GetCurrentProcess().SessionId,
                    os = RuntimeInformation.OSDescription,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    executable = Environment.ProcessPath,
                    user_interactive = Environment.UserInteractive,
                    privacy = "Screenshots, typed text, and literal key values are never written to this log.",
                });
            NativeMethods.TryEnablePerMonitorV2DpiAwareness();
            using var host = new RapidPcHost();
            host.Run();
            DriverLog.Info("driver.stopped", "The MCP input stream closed and Rapid PC Use stopped normally.");
        }
        catch (Exception exception)
        {
            DriverLog.Error("driver.terminated", "Rapid PC Use terminated unexpectedly.", exception);
            Console.Error.WriteLine($"Rapid PC Use terminated: {exception.Message}");
            Environment.ExitCode = 1;
        }
        finally
        {
            DriverLog.FlushAndStop(TimeSpan.FromSeconds(2));
        }
    }
}
