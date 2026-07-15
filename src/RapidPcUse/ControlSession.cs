using System.IO;

namespace RapidPcUse;

internal sealed class ControlSession : IDisposable
{
    private readonly object _gate = new();
    private readonly InputController _input;
    private readonly ControlOverlay _overlay;
    private readonly Timer _idleTimer;
    private FileStream? _lease;
    private DateTime _lastActivityUtc;
    private bool _active;
    private bool _interrupted;
    private bool _disposed;

    internal ControlSession(InputController input, ControlOverlay overlay)
    {
        _input = input;
        _overlay = overlay;
        _idleTimer = new Timer(_ => StopIfIdle(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    internal bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    internal bool WasInterrupted
    {
        get
        {
            lock (_gate)
            {
                return _interrupted;
            }
        }
    }

    internal void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active)
            {
                _lastActivityUtc = DateTime.UtcNow;
                return;
            }

            var leasePath = Path.Combine(Path.GetTempPath(), "rapid-pc-use.control.lock");
            try
            {
                _lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException("Another Rapid PC Use session already controls this Windows desktop.", exception);
            }

            try
            {
                _overlay.Show();
            }
            catch
            {
                _lease.Dispose();
                _lease = null;
                throw;
            }

            _interrupted = false;
            _active = true;
            _lastActivityUtc = DateTime.UtcNow;
            DriverLog.Info("control.acquired", "Native mouse and keyboard control was acquired and the user takeover cue is visible.");
        }
    }

    internal void Touch()
    {
        lock (_gate)
        {
            if (_active)
            {
                _lastActivityUtc = DateTime.UtcNow;
            }
        }
    }

    internal void EnsureCanAct()
    {
        lock (_gate)
        {
            if (_interrupted)
            {
                throw new UserTakeoverException();
            }

            if (!_active)
            {
                throw new InvalidOperationException("No active PC control session. Call pc_observe with begin_control=true first.");
            }

            _lastActivityUtc = DateTime.UtcNow;
        }
    }

    internal void OnPhysicalEscape()
    {
        var shouldStop = false;
        lock (_gate)
        {
            if (_active)
            {
                _active = false;
                _interrupted = true;
                shouldStop = true;
            }
        }

        if (!shouldStop)
        {
            return;
        }

        DriverLog.Info("control.user_takeover", "The physical Escape key returned control to the user.");

        ThreadPool.QueueUserWorkItem(_ =>
        {
            _input.ReleaseAll();
            _overlay.Hide();
            ReleaseLease();
        });
    }

    internal void Stop()
    {
        bool wasActive;
        lock (_gate)
        {
            wasActive = _active || _lease is not null;
            _active = false;
            _interrupted = false;
        }

        _input.ReleaseAll();
        try
        {
            _overlay.Hide();
        }
        finally
        {
            ReleaseLease();
        }

        if (wasActive)
        {
            DriverLog.Info("control.released", "Native mouse and keyboard control was released and the takeover cue was hidden.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _idleTimer.Dispose();
        Stop();
    }

    private void StopIfIdle()
    {
        var idle = false;
        lock (_gate)
        {
            idle = _active && DateTime.UtcNow - _lastActivityUtc > TimeSpan.FromMinutes(3);
        }

        if (idle)
        {
            DriverLog.Warning("control.idle_timeout", "The control session was idle for three minutes and will be released automatically.");
            Stop();
        }
    }

    private void ReleaseLease()
    {
        FileStream? lease;
        lock (_gate)
        {
            lease = _lease;
            _lease = null;
        }

        lease?.Dispose();
    }
}
