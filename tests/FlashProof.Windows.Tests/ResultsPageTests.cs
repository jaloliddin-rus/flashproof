using FlashProof.Core;

namespace FlashProof.Windows.Tests;

// M4 docs: docs/results.md explains every result in the app's own words, so each fixed text in Text.cs that a user
// sees as a result, a status or an Inspect finding must appear there word for word. Change one, change the other.
public sealed class ResultsPageTests
{
    static readonly string Page = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "results.md"));

    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FlashProof.slnx")))
                return dir.FullName;
        throw new DirectoryNotFoundException("The repository root (FlashProof.slnx) wasn't found.");
    }

    static IEnumerable<string> AppTexts()
    {
        foreach (var verdict in Enum.GetValues<Verdict>())
        {
            // These two show on the drive list as the statuses "No card" and "Not working" (checked below).
            if (verdict is not (Verdict.NoCardInReader or Verdict.DetectedButNotWorking))
                yield return Text.Title(verdict);
            if (Text.Explanation(verdict) is { Length: > 0 } explanation)
                yield return explanation;
        }
        yield return Text.Explanation(new VerdictResult(Verdict.PassedCapacityConfirmed, CapacityResult.Confirmed, 1), new TestEvidence());
        foreach (var reason in Enum.GetValues<CantTestReason>())
            yield return Text.CantTestText(reason);
        foreach (var reason in Enum.GetValues<InterruptReason>())
            yield return Text.InterruptText(reason);
        yield return Text.InterruptText(null);

        var lost = new TestEvidence();
        lost.Errors.Add(new IoError(TestPhase.Write, 0, IoErrorKind.DeviceRemoved, 1006, "lost"));
        yield return Text.ConnectionLost(lost, stayedPluggedIn: true)!;
        yield return Text.ConnectionLost(lost, stayedPluggedIn: false)!;
        yield return Text.LayoutChanged;
        yield return Text.DidYouUnplugIt;
        yield return Text.FilesKept("*session*");
        yield return Text.ConfirmReplugHint;
        yield return Text.Replug(new ReplugMessage(ReplugStep.NotThisTest), "*the drive*");

        yield return Text.StatusNoCard;
        yield return Text.StatusNotWorking;
        yield return Text.StatusNotSupported;
        yield return Text.StatusCantTest;

        yield return Text.NoProblemsFound;
        yield return Text.NotAHealthPass;
        foreach (var problem in Enum.GetValues<InspectProblem>())
            yield return Text.Problem(problem);
        yield return Text.RecentDiskErrorsUncertain;
        foreach (var kind in Enum.GetValues<DiskEventKind>())
            yield return Text.EventKind(kind);
        var now = DateTimeOffset.UnixEpoch;
        yield return Text.EventCaveat(new DiskEvent(DiskEventKind.ControllerError, now, 2, DiskEventMatch.ThisPlugIn, DuringEjectedTest: true));
        yield return Text.EventCaveat(new DiskEvent(DiskEventKind.RetriedIo, now, 2, DiskEventMatch.EarlierSameDiskNumber));
        yield return Text.EventCaveat(new DiskEvent(DiskEventKind.UasReset, now, null, DiskEventMatch.UasDriver));
        yield return Text.ReadAllFilesHint;
        yield return Text.HoldsFilesAdvice;

        yield return Text.UnitsExplained;
        yield return Text.Usb2Note(new SpeedSummary { AverageRead = 30e6 })!;
    }

    [Fact]
    public void Every_result_text_is_on_the_page_word_for_word()
    {
        var missing = AppTexts().Distinct().Where(text => !Page.Contains(text, StringComparison.Ordinal)).ToList();
        Assert.Empty(missing);
    }
}
