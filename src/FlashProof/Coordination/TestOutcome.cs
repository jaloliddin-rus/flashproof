using FlashProof.Core;
using FlashProof.Windows;

namespace FlashProof.Coordination;

/// <summary>How a full test ended, as the app shows it.</summary>
/// <param name="Result">The worker's result; null if the worker ended without one.</param>
/// <param name="AskIfUnplugged">The drive disappeared mid-test: ask "Did you unplug it?" before showing the verdict (design §3).</param>
/// <param name="StayedPluggedIn">The test lost the drive, but it never left Windows' device list (see <see cref="ResultMessage"/>).</param>
public sealed record TestOutcome(VerdictResult Verdict, TestResult? Result, string? JournalPath, bool AskIfUnplugged, bool StayedPluggedIn = false)
{
    public static TestOutcome From(ResultMessage message)
    {
        var result = message.Result;
        // A "Safely remove" already comes back as Interrupted (UserUnplugged), and a drive that stayed plugged in can't
        // have been unplugged; only a drive that really vanished is asked about.
        bool vanished = result.Verdict.Verdict == Core.Verdict.ConnectionProblemSuspected && result.Evidence.DeviceRemoved && !message.StayedPluggedIn;
        return new(result.Verdict, result, message.JournalPath, vanished, message.StayedPluggedIn);
    }

    /// <summary>The worker ended without a result: the test was stopped, or the worker stopped unexpectedly.</summary>
    public static TestOutcome WithoutResult(InterruptReason reason) =>
        new(new VerdictResult(Core.Verdict.Interrupted, CapacityResult.NotChecked, 0, InterruptReason: reason), null, null, false);

    /// <summary>The user's answer to "Did you unplug it?": yes → Interrupted, no → Connection problem suspected (design §3).</summary>
    public TestOutcome Answer(bool unplugged)
    {
        if (!AskIfUnplugged || Result is null)
            throw new InvalidOperationException("Nothing was asked.");
        return this with { Verdict = VerdictEngine.Decide(Result.Evidence, unplugged), AskIfUnplugged = false };
    }
}
