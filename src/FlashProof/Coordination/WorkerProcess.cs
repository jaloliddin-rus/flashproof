using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace FlashProof.Coordination;

/// <summary>One running worker, as the coordinator sees it: lines in, lines out, and a way to end it.</summary>
public interface IWorkerConnection : IDisposable
{
    /// <summary>The worker's next line; null once its output closed, which means it exited.</summary>
    Task<string?> ReadLineAsync();

    /// <summary>Sends a line without waiting for the worker to take it.</summary>
    void Send(string line);

    /// <summary>Ends the worker without waiting. A worker stuck in I/O may still not exit (design §5.4).</summary>
    void Kill();
}

/// <summary>
/// <c>FlashProof.exe --worker</c> as a child process with redirected stdin and stdout (design §4). Disposing it closes
/// the worker's stdin; the worker then stops any running test as "app closed" and exits by itself.
/// </summary>
public sealed class WorkerProcess : IWorkerConnection
{
    readonly Process process;
    readonly StreamReader output;
    readonly Channel<string> outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    WorkerProcess(Process process)
    {
        this.process = process;
        output = process.StandardOutput;
        _ = WriteAllAsync(process.StandardInput);
    }

    /// <param name="exePath">This app's own exe, which runs the worker when given <c>--worker</c>.</param>
    public static WorkerProcess Start(string exePath)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false); // a BOM would break the first command
        var info = new ProcessStartInfo(exePath, "--worker")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
        };
        return new WorkerProcess(Process.Start(info) ?? throw new Win32Exception("The worker didn't start."));
    }

    public async Task<string?> ReadLineAsync()
    {
        string? line = await output.ReadLineAsync().ConfigureAwait(false);
        if (line is null)
            output.Dispose();
        return line;
    }

    public void Send(string line) => outgoing.Writer.TryWrite(line);

    public void Kill()
    {
        try
        {
            process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // it has exited already, or is exiting
        }
    }

    public void Dispose() => outgoing.Writer.TryComplete();

    /// <summary>Writes on a thread of its own, so the UI never waits on a pipe the worker isn't reading.</summary>
    async Task WriteAllAsync(StreamWriter input)
    {
        try
        {
            await foreach (string line in outgoing.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await input.WriteLineAsync(line).ConfigureAwait(false);
                await input.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // the worker is gone
        }
        try
        {
            input.Dispose(); // closes the worker's stdin
        }
        catch (IOException)
        {
            // the worker is gone
        }
        process.Dispose(); // only the process handle: the output reader is still in use
    }
}
