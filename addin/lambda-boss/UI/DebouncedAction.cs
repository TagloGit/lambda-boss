using System.Windows.Threading;

namespace LambdaBoss.UI;

/// <summary>
///     Coalesces a burst of cheap UI events into a single run of an
///     expensive callback. <see cref="Trigger" /> (re)starts a
///     <see cref="DispatcherTimer" />, so the callback fires once the
///     events stop for <c>delay</c> rather than once per event; the
///     control raising them stays fully responsive in the meantime.
///
///     Used by <see cref="GatherWindow" /> for the LET preview: the name
///     TextBoxes bind with <c>UpdateSourceTrigger=PropertyChanged</c>, so
///     every keystroke used to re-run the whole precedent walk over live
///     Excel COM — many out-of-process reads per character, which is what
///     made typing in the rename column lag.
///
///     Because the callback is deferred, anything that <em>reads</em> the
///     deferred work's output has to <see cref="Flush" /> first: a flush
///     runs a pending callback immediately (and is a no-op when nothing is
///     pending), so no caller can observe a stale result.
///     <see cref="Cancel" /> drops a pending callback for the cases where
///     the work is being superseded or thrown away.
///
///     Not thread-safe by design: every member is called from the owning
///     dispatcher thread, and the timer ticks on that same thread.
/// </summary>
internal sealed class DebouncedAction
{
    private readonly Action _action;
    private readonly DispatcherTimer _timer;

    private bool _pending;

    public DebouncedAction(TimeSpan delay, Action action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        _action = action;
        _timer = new DispatcherTimer { Interval = delay };
        _timer.Tick += OnTick;
    }

    /// <summary>
    ///     True while a triggered callback hasn't run yet — i.e. while a
    ///     <see cref="Flush" /> would have an effect.
    /// </summary>
    internal bool IsPending => _pending;

    /// <summary>
    ///     Schedules the callback, restarting the delay if one was already
    ///     scheduled. The callback runs <c>delay</c> after the <em>last</em>
    ///     trigger, so a continuous burst produces exactly one run.
    /// </summary>
    public void Trigger()
    {
        _pending = true;
        // Stop-then-start is what restarts the interval; DispatcherTimer
        // does not reset its countdown on a bare Start() while running.
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>
    ///     Runs a pending callback right now and clears the schedule.
    ///     No-op when nothing is pending, so callers can flush
    ///     unconditionally before reading whatever the callback produces.
    /// </summary>
    public void Flush()
    {
        _timer.Stop();
        if (!_pending) return;
        _pending = false;
        _action();
    }

    /// <summary>
    ///     Drops a pending callback without running it — for when the work
    ///     is superseded by something more complete, or the owner is going
    ///     away and the result would never be read.
    /// </summary>
    public void Cancel()
    {
        _timer.Stop();
        _pending = false;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Flush();
    }
}
