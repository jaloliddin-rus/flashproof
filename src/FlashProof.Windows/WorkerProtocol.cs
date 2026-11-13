using System.Text.Json;
using System.Text.Json.Serialization;
using FlashProof.Core;

namespace FlashProof.Windows;

/// <summary>A command from the app to the worker: one JSON object per line on the worker's stdin (design §7).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ScanCommand), "scan")]
[JsonDerivedType(typeof(StartCommand), "start")]
[JsonDerivedType(typeof(ReadFilesCommand), "readFiles")]
[JsonDerivedType(typeof(CancelCommand), "cancel")]
[JsonDerivedType(typeof(EjectCommand), "eject")]
public abstract record WorkerCommand;

/// <summary>Find the USB and SD disks, inspect each, and list USB devices that failed.</summary>
public sealed record ScanCommand : WorkerCommand;

/// <summary>Start a full test the user confirmed.</summary>
public sealed record StartCommand(StartSpec Spec) : WorkerCommand;

/// <summary>Inspect's optional read-back of every existing file on one volume of a scanned disk (design §6.3).</summary>
public sealed record ReadFilesCommand(string DeviceInstanceId, string VolumeGuidPath) : WorkerCommand;

/// <summary>Stop the running full test or read-back.</summary>
public sealed record CancelCommand : WorkerCommand;

/// <summary>Do Windows' "Safely remove" for the drive a test is waiting to have replugged (design §6.2 step 7).</summary>
public sealed record EjectCommand : WorkerCommand;

/// <summary>A full test the user confirmed (design §5.3).</summary>
/// <param name="SessionId">Letters, digits and '-' only, at most 64: it names a folder on the drive and one on this PC.</param>
/// <param name="VolumeGuidPath">The disk's test volume, as the scan reported it.</param>
/// <param name="ConfirmedIdentity">The device as the user confirmed it. Nothing is written to any other (design §5.2).</param>
/// <param name="MaxTestBytes">For hardware tests only: test at most this much. Null tests all the free space.</param>
/// <param name="ReplugBeforeVerify">"Unplug and replug before verifying" (design §6.2 step 7): the user unplugs the drive between writing and reading back.</param>
public sealed record StartSpec(string SessionId, string DeviceInstanceId, string VolumeGuidPath, VolumeIdentity ConfirmedIdentity,
    bool KeepTestFiles = false, long? MaxTestBytes = null, bool ReplugBeforeVerify = false);

/// <summary>A message from the worker to the app: one JSON object per line on the worker's stdout (design §7).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(HeartbeatMessage), "heartbeat")]
[JsonDerivedType(typeof(DevicesMessage), "devices")]
[JsonDerivedType(typeof(ProgressMessage), "progress")]
[JsonDerivedType(typeof(SampleMessage), "sample")]
[JsonDerivedType(typeof(WarningMessage), "warning")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(ResultMessage), "result")]
[JsonDerivedType(typeof(FilesMessage), "files")]
[JsonDerivedType(typeof(ReplugMessage), "replug")]
public abstract record WorkerMessage;

/// <summary>The first message: the protocol version this worker speaks.</summary>
public sealed record HelloMessage(int Protocol, string Version) : WorkerMessage;

/// <summary>Sent every second from a thread of its own (design §5.4); shows the worker is alive, not that the drive is.</summary>
public sealed record HeartbeatMessage : WorkerMessage;

/// <summary>The scan's result.</summary>
public sealed record DevicesMessage(IReadOnlyList<ScannedDevice> Devices, IReadOnlyList<ProblemDevice> ProblemDevices) : WorkerMessage;

/// <param name="Done">Bytes, except for the early fake check, which counts records.</param>
/// <param name="BytesPerSecond">Over the time since the previous progress message; null when not measured.</param>
public sealed record ProgressMessage(WorkPhase Phase, long Done, long Total, double? BytesPerSecond) : WorkerMessage;

/// <summary>A speed slice of writing or verifying (design §6.4), for the speed chart.</summary>
public sealed record SampleMessage(TestPhase Phase, long Offset, long Bytes, double Seconds) : WorkerMessage;

public sealed record WarningMessage(string Code, string Text) : WorkerMessage;

/// <summary>A command was refused or failed. <see cref="Code"/> is one of <see cref="WorkerErrors"/>.</summary>
public sealed record ErrorMessage(string Code, string Text, int Win32 = 0) : WorkerMessage;

/// <summary>A full test ended; its journal is at <see cref="JournalPath"/>.</summary>
/// <param name="StayedPluggedIn">
/// The test lost the drive (errors of kind <see cref="IoErrorKind.DeviceRemoved"/>), yet it never left Windows' device
/// list and kept its plug-in time: its file system dropped the volume while it stayed plugged in. The app then doesn't
/// ask "Did you unplug it?".
/// </param>
public sealed record ResultMessage(TestResult Result, string JournalPath, bool StayedPluggedIn = false) : WorkerMessage;

/// <summary>A read-back ended.</summary>
public sealed record FilesMessage(FileReadResult Result) : WorkerMessage;

/// <summary>Where a test waiting for "Unplug and replug before verifying" stands (design §6.2 step 7). Verify progress follows once the drive is accepted.</summary>
/// <param name="Blocker">For <see cref="ReplugStep.EjectFailed"/>: the program or service using the drive, if Windows named one.</param>
public sealed record ReplugMessage(ReplugStep Step, string? Blocker = null) : WorkerMessage;

public enum ReplugStep
{
    /// <summary>Writing and flushing ended and the worker let go of the drive: eject it, then unplug it.</summary>
    ReadyToEject,

    /// <summary>Windows ejected it: unplug it, wait, plug it back in.</summary>
    Ejected,

    /// <summary>A program or service kept Windows from ejecting it.</summary>
    EjectFailed,

    /// <summary>Windows didn't let FlashProof eject it (as in a Remote Desktop session): use Windows' own "Safely remove", or just unplug it.</summary>
    EjectNotAllowed,

    /// <summary>The tested drive is back in Windows' device list; checking it's the same one.</summary>
    Checking,

    /// <summary>Another drive or card was plugged in. It isn't written to.</summary>
    WrongDrive,

    /// <summary>A drive like the tested one came back without this test's files, or they couldn't be read.</summary>
    NotThisTest,
}

/// <summary>A USB or SD disk in Windows' device list, and when it was last plugged in (null if Windows doesn't say).</summary>
public sealed record PluggedDisk(string InstanceId, DateTimeOffset? LastArrival);

public enum WorkPhase
{
    Write,
    EarlyCheck,
    Verify,
    ReadFiles,
}

/// <summary>A disk the scan found: its Inspect report, or why it couldn't be read.</summary>
/// <param name="Status">Null for Ready; otherwise the verdict without a test.</param>
/// <param name="Report">Null if the disk failed or didn't answer in time; then the status is Detected but not working.</param>
/// <param name="Identity">For a Ready disk, its test volume's identity as read now: what the user confirms, and what <see cref="StartSpec.ConfirmedIdentity"/> sends back (design §5.2).</param>
public sealed record ScannedDevice(DiskCandidate Device, VerdictResult? Status, InspectReport? Report, VolumeIdentity? Identity, string? Failure);

/// <summary>The codes of <see cref="ErrorMessage"/>.</summary>
public static class WorkerErrors
{
    public const string BadCommand = "badCommand";
    public const string Busy = "busy";
    public const string BadSessionId = "badSessionId";
    public const string SessionExists = "sessionExists";
    public const string NotACandidate = "notACandidate";
    public const string NotReady = "notReady";
    public const string NotTestVolume = "notTestVolume";
    public const string NotOnDevice = "notOnDevice";
    public const string IoFailed = "ioFailed";
    public const string Internal = "internal";
    public const string NothingToCancel = "nothingToCancel";

    /// <summary>An eject when no test is waiting for a replug, or while the worker has the drive open.</summary>
    public const string NothingToEject = "nothingToEject";

    /// <summary>A read-back was stopped, or a test was stopped before it began.</summary>
    public const string Cancelled = "cancelled";
}

public static class WorkerProtocol
{
    /// <summary>Raised whenever a message changes incompatibly.</summary>
    public const int Version = 1;

    public static string Serialize(WorkerMessage message) => JsonSerializer.Serialize(message, WorkerJson.Default.WorkerMessage);

    public static string Serialize(WorkerCommand command) => JsonSerializer.Serialize(command, WorkerJson.Default.WorkerCommand);

    /// <exception cref="JsonException">Not a message.</exception>
    public static WorkerMessage ParseMessage(string line) =>
        JsonSerializer.Deserialize(line, WorkerJson.Default.WorkerMessage) ?? throw new JsonException("Empty message.");

    /// <exception cref="JsonException">Not a command.</exception>
    public static WorkerCommand ParseCommand(string line) =>
        JsonSerializer.Deserialize(line, WorkerJson.Default.WorkerCommand) ?? throw new JsonException("Empty command.");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(WorkerCommand))]
[JsonSerializable(typeof(WorkerMessage))]
internal sealed partial class WorkerJson : JsonSerializerContext;
