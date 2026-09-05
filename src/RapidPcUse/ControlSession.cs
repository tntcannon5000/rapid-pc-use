using System.IO;

namespace RapidPcUse;

internal sealed class ControlSession : IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);
    private readonly object _leaseGate = new();
    private readonly object _cleanupGate = new();
    private readonly object _cancellationGate = new();
    private readonly InputController _input;
    private readonly ControlOverlay _overlay;
    private readonly ControlSessionState _state = new();
    private readonly Timer _idleTimer;
    private FileStream? _lease;
    private CancellationTokenSource _controlCancellation = CreateCancelledSource();
    private bool _disposed;

    internal ControlSession(InputController input, ControlOverlay overlay)
    {
        _input = input;
        _overlay = overlay;
        _idleTimer = new Timer(_ => StopIfIdle(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    internal bool IsActive => _state.IsActive;

    internal CancellationToken ControlCancellationToken
    {
        get
        {
            lock (_cancellationGate)
            {
                return _controlCancellation.Token;
            }
        }
    }

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state.IsActive)
        {
            _state.Touch();
            return;
        }

        lock (_leaseGate)
        {
            if (_state.IsActive)
            {
                _state.Touch();
                return;
            }

            var leasePath = Path.Combine(Path.GetTempPath(), "rapid-pc-use.control.lock");
            try
            {
                _lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception)
            {
                throw new ControlSessionBusyException(exception);
            }

            try
            {
                ResetControlCancellation();
                _overlay.Show();
                _ = _state.Start();
            }
            catch
            {
                CancelControl();
                _lease.Dispose();
                _lease = null;
                throw;
            }
        }

        DriverLog.Info("control.acquired", "Native mouse and keyboard control was acquired and the user takeover cue is visible.");
    }

    internal void Touch() => _state.Touch();

    internal ControlOperationLease BeginOperation()
        => _state.BeginOperation(TimeSpan.FromMilliseconds(SecurityLimits.MaxBatchMilliseconds));

    internal void ThrowIfCannotContinue(ControlOperationLease operation)
        => _state.ThrowIfCannotContinue(operation);

    internal void ThrowIfControlLost() => _state.ThrowIfInactive();

    internal void OnPhysicalEscape()
    {
        if (!_state.End(ControlEndReason.UserTakeover))
        {
            return;
        }

        CancelControl();

        ThreadPool.QueueUserWorkItem(_ => CompleteStop(
            "control.user_takeover",
            "The physical Escape key returned control to the user.",
            throwOnError: false));
    }

    internal void Stop()
    {
        var wasActive = _state.End(ControlEndReason.Stop);
        CancelControl();
        CompleteStop(
            wasActive ? "control.released" : null,
            "Native mouse and keyboard control was released and the takeover cue was hidden.",
            throwOnError: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _idleTimer.Dispose();
        _ = _state.End(ControlEndReason.Dispose);
        CancelControl();
        CompleteStop(null, null, throwOnError: false);
        lock (_cancellationGate)
        {
            _controlCancellation.Dispose();
        }
    }

    private void StopIfIdle()
    {
        if (!_state.TryExpireIdle(IdleTimeout))
        {
            return;
        }

        DriverLog.Warning("control.idle_timeout", "The control session was idle for three minutes and was cancelled.");
        CancelControl();
        CompleteStop(
            "control.idle_released",
            "Idle desktop control was released and the takeover cue was hidden.",
            throwOnError: false);
    }

    private void CompleteStop(string? eventName, string? message, bool throwOnError)
    {
        List<Exception>? errors = null;
        lock (_cleanupGate)
        {
            try
            {
                _input.ReleaseAll();
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }

            try
            {
                _overlay.Hide();
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }
            finally
            {
                ReleaseLease();
            }
        }

        if (eventName is not null && message is not null)
        {
            DriverLog.Info(eventName, message);
        }

        if (errors is null)
        {
            return;
        }

        var aggregate = new AggregateException("Desktop control cleanup did not release every resource.", errors);
        DriverLog.Error("control.cleanup_failed", "Desktop control cleanup reported an error.", aggregate);
        if (throwOnError)
        {
            throw aggregate;
        }
    }

    private void ReleaseLease()
    {
        lock (_leaseGate)
        {
            _lease?.Dispose();
            _lease = null;
        }
    }

    private void ResetControlCancellation()
    {
        lock (_cancellationGate)
        {
            _controlCancellation.Dispose();
            _controlCancellation = new CancellationTokenSource();
        }
    }

    private void CancelControl()
    {
        lock (_cancellationGate)
        {
            try
            {
                _controlCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Disposal already completed the control lifetime.
            }
        }
    }

    private static CancellationTokenSource CreateCancelledSource()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source;
    }
}

internal sealed class ControlSessionBusyException(Exception innerException)
    : InvalidOperationException("Another Rapid PC Use session already controls this Windows desktop.", innerException);
