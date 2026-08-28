using LambdaBoss.UI;
using Xunit;

namespace LambdaBoss.Tests;

/// <summary>
///     Unit tests for <see cref="DebouncedAction" />, the coalescer behind
///     the Gather dialog's LET preview.
///
///     The tick path itself needs a pumping dispatcher and isn't exercised
///     here; what these tests pin is the part the dialog's correctness
///     rests on — that a deferred callback is never lost and never runs
///     twice. <see cref="DebouncedAction.Flush" /> after any number of
///     triggers runs the callback exactly once; a second flush with
///     nothing pending does nothing (so a Save right after a LostFocus
///     flush doesn't recompute again); and
///     <see cref="DebouncedAction.Cancel" /> drops the pending run, which
///     is what stops a superseded preview from re-running behind a full
///     recompute or ticking into a closed window.
/// </summary>
public sealed class DebouncedActionTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);

    [Fact]
    public void NothingPendingUntilTriggered()
    {
        var runs = 0;
        var d = new DebouncedAction(Delay, () => runs++);

        Assert.False(d.IsPending);
        d.Flush();

        Assert.Equal(0, runs);
    }

    [Fact]
    public void TriggerDefersTheCallback()
    {
        var runs = 0;
        var d = new DebouncedAction(Delay, () => runs++);

        d.Trigger();

        Assert.True(d.IsPending);
        Assert.Equal(0, runs);
    }

    [Fact]
    public void FlushRunsAPendingCallbackOnce()
    {
        var runs = 0;
        var d = new DebouncedAction(Delay, () => runs++);

        d.Trigger();
        d.Flush();

        Assert.Equal(1, runs);
        Assert.False(d.IsPending);
    }

    [Fact]
    public void ABurstOfTriggersCollapsesToOneRun()
    {
        var runs = 0;
        var d = new DebouncedAction(Delay, () => runs++);

        // Stands in for the per-keystroke Name edits.
        for (var i = 0; i < 10; i++)
            d.Trigger();
        d.Flush();

        Assert.Equal(1, runs);
    }

    [Fact]
    public void FlushIsIdempotent()
    {
        var runs = 0;
        var d = new DebouncedAction(Delay, () => runs++);

        d.Trigger();
        d.Flush();
        d.Flush();

        Assert.Equal(1, runs);
    }

    [Fact]
    public void CancelDropsThePendingCallback()
    {
        var runs = 0;
        var d = new DebouncedAction(Delay, () => runs++);

        d.Trigger();
        d.Cancel();

        Assert.False(d.IsPending);

        d.Flush();

        Assert.Equal(0, runs);
    }

    [Fact]
    public void TriggerAfterFlushSchedulesAgain()
    {
        var runs = 0;
        var d = new DebouncedAction(Delay, () => runs++);

        d.Trigger();
        d.Flush();
        d.Trigger();
        d.Flush();

        Assert.Equal(2, runs);
    }

    [Fact]
    public void NullActionIsRejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DebouncedAction(Delay, null!));
    }
}
