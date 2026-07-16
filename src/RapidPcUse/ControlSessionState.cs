namespace RapidPcUse;

internal enum ControlEndReason
{
    Stop,
    IdleTimeout,
    UserTakeover,
    Dispose,
}

internal readonly record struct ControlOperationLease(long Generation, DateTimeOffset DeadlineUtc);

internal sealed class ControlSessionState(TimeProvider? timeProvider = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private long _generation;
    private DateTimeOffset _lastActivityUtc;
    private bool _active;
    private bool _interrupted;

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

    internal bool Start()
    {
        lock (_gate)
        {
            _lastActivityUtc = _timeProvider.GetUtcNow();
            if (_active)
            {
                return false;
            }

            _generation++;
            _active = true;
            _interrupted = false;
            return true;
        }
    }

    internal void Touch()
    {
        lock (_gate)
        {
            if (_active)
            {
                _lastActivityUtc = _timeProvider.GetUtcNow();
            }
        }
    }

    internal ControlOperationLease BeginOperation(TimeSpan maximumDuration)
    {
        lock (_gate)
        {
            ThrowIfInactive();
            _lastActivityUtc = _timeProvider.GetUtcNow();
            return new ControlOperationLease(_generation, _lastActivityUtc + maximumDuration);
        }
    }

    internal void ThrowIfCannotContinue(ControlOperationLease operation)
    {
        lock (_gate)
        {
            if (_interrupted)
            {
                throw new UserTakeoverException();
            }

            if (!_active || operation.Generation != _generation)
            {
                throw new ControlSessionEndedException();
            }

            if (_timeProvider.GetUtcNow() > operation.DeadlineUtc)
            {
                throw new TimeoutException("The desktop action exceeded the 30-second safety budget.");
            }
        }
    }

    internal bool End(ControlEndReason reason)
    {
        lock (_gate)
        {
            if (!_active)
            {
                return false;
            }

            _active = false;
            _interrupted = reason == ControlEndReason.UserTakeover;
            _generation++;
            return true;
        }
    }

    internal bool TryExpireIdle(TimeSpan idleTimeout)
    {
        lock (_gate)
        {
            if (!_active || _timeProvider.GetUtcNow() - _lastActivityUtc <= idleTimeout)
            {
                return false;
            }

            _active = false;
            _interrupted = false;
            _generation++;
            return true;
        }
    }

    private void ThrowIfInactive()
    {
        if (_interrupted)
        {
            throw new UserTakeoverException();
        }

        if (!_active)
        {
            throw new ControlSessionEndedException();
        }
    }
}
