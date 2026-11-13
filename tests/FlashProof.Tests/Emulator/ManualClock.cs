namespace FlashProof.Tests.Emulator;

/// <summary>A clock that moves only when told to, so a slow drive can be simulated without waiting.</summary>
public sealed class ManualClock : TimeProvider
{
    static readonly DateTimeOffset Start = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    long ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => ticks;

    public override DateTimeOffset GetUtcNow() => Start.AddTicks(ticks);

    public void Advance(TimeSpan by) => ticks += by.Ticks;
}
