using FlashProof.Coordination;

namespace FlashProof.Windows.Tests;

// Design §4 and §5.4: the real FlashProof.exe --worker, started the way the app starts it. Nothing here touches a
// device: the only command sent is a cancel with nothing running.
public sealed class WorkerProcessTests
{
    static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "FlashProof.exe");

    static async Task<WorkerMessage> NextAsync(WorkerProcess worker, CancellationToken ct) =>
        WorkerProtocol.ParseMessage(await worker.ReadLineAsync().WaitAsync(ct) ?? throw new EndOfStreamException("The worker exited."));

    /// <summary>Reads until the worker's output closes, which means it exited.</summary>
    static async Task UntilExitAsync(WorkerProcess worker, CancellationToken ct)
    {
        while (await worker.ReadLineAsync().WaitAsync(ct) is not null)
        {
        }
    }

    [Fact]
    public async Task The_worker_answers_its_first_command_and_exits_when_its_stdin_closes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var worker = WorkerProcess.Start(Exe);

        var hello = Assert.IsType<HelloMessage>(await NextAsync(worker, cts.Token));
        worker.Send(WorkerProtocol.Serialize(new CancelCommand())); // the first command parses: no BOM on stdin
        WorkerMessage reply;
        do
            reply = await NextAsync(worker, cts.Token);
        while (reply is HeartbeatMessage);
        worker.Dispose();
        await UntilExitAsync(worker, cts.Token);

        Assert.Equal(WorkerProtocol.Version, hello.Protocol);
        Assert.Equal(WorkerErrors.NothingToCancel, Assert.IsType<ErrorMessage>(reply).Code);
    }

    [Fact]
    public async Task Kill_ends_the_worker()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var worker = WorkerProcess.Start(Exe);
        Assert.IsType<HelloMessage>(await NextAsync(worker, cts.Token));

        worker.Kill(); // its stdin stays open, so only the kill can end it

        await UntilExitAsync(worker, cts.Token);
    }
}
