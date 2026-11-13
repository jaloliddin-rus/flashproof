namespace FlashProof.Windows.Tests;

// Found on the real PC (12 Nov): the app could be opened twice, and each copy could start a Full test on the same drive.
public sealed class SingleInstanceTests
{
    [Fact]
    public void Only_one_copy_holds_the_lock_until_it_exits()
    {
        string name = $"FlashProofTest-{Guid.NewGuid():N}";
        var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(name));

        first.Dispose();
        using var next = SingleInstance.TryAcquire(name);
        Assert.NotNull(next);
    }
}
