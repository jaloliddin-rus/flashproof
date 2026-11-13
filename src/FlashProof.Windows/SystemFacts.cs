using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FlashProof.Windows;

/// <summary>Windows policies that block writing to removable drives (design §3, "Writing blocked by Windows").</summary>
/// <param name="RemovableDisksDenyWrite">Group Policy "Removable Disks: Deny write access", or "All Removable Storage classes: Deny all access", for the computer or the user.</param>
/// <param name="StorageWriteProtect">StorageDevicePolicies\WriteProtect.</param>
/// <param name="BitLockerDenyWrite">"Deny write access to removable drives not protected by BitLocker": such drives are mounted read-only.</param>
public sealed record WritePolicy(bool RemovableDisksDenyWrite, bool StorageWriteProtect, bool BitLockerDenyWrite)
{
    public static readonly WritePolicy None = new(false, false, false);

    public bool BlocksAllRemovableWrites => RemovableDisksDenyWrite || StorageWriteProtect;
}

/// <summary>
/// What the running system needs, so that a disk holding any of it is never tested (design §5.1 rule 3), and the write
/// policies. Read from the registry and the environment only: no disk is opened. WMI is not used, as it can hang and
/// it queries every disk.
/// </summary>
/// <param name="SystemPartitionDevice">Such as <c>\Device\HarddiskVolume1</c>, from HKLM\SYSTEM\Setup\SystemPartition.</param>
/// <param name="ProtectedLetters">Drive letters of Windows, page files, the app and its journals, such as "C:".</param>
public sealed record SystemFacts(string? SystemPartitionDevice, IReadOnlySet<string> ProtectedLetters, WritePolicy Policy)
{
    const string RemovableStorage = @"Software\Policies\Microsoft\Windows\RemovableStorageDevices";
    const string RemovableDisks = RemovableStorage + @"\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}";

    public static SystemFacts Read()
    {
        using var local = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

        var paths = new List<string?>
        {
            Environment.GetEnvironmentVariable("SystemDrive"),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppContext.BaseDirectory,
        };
        if (Value(local, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "ExistingPageFiles") is string[] pageFiles)
            paths.AddRange(pageFiles);

        var policy = new WritePolicy(
            RemovableDisksDenyWrite: IsOne(local, RemovableDisks, "Deny_Write") || IsOne(user, RemovableDisks, "Deny_Write")
                || IsOne(local, RemovableStorage, "Deny_All") || IsOne(user, RemovableStorage, "Deny_All"),
            StorageWriteProtect: IsOne(local, @"SYSTEM\CurrentControlSet\Control\StorageDevicePolicies", "WriteProtect"),
            BitLockerDenyWrite: IsOne(local, @"SYSTEM\CurrentControlSet\Policies\Microsoft\FVE", "RDVDenyWriteAccess"));

        return new SystemFacts(Value(local, @"SYSTEM\Setup", "SystemPartition") as string, LettersOf(paths), policy);
    }

    /// <summary>The drive letters in paths such as <c>C:\Windows</c> or <c>\??\C:\pagefile.sys</c>, as "C:".</summary>
    internal static IReadOnlySet<string> LettersOf(IEnumerable<string?> paths)
    {
        var letters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in paths)
        {
            if (path is not null && Regex.Match(path, @"^(?:\\\?\?\\|\\\\\?\\)?([A-Za-z]):") is { Success: true } m)
                letters.Add(m.Groups[1].Value.ToUpperInvariant() + ":");
        }
        return letters;
    }

    static object? Value(RegistryKey hive, string key, string name)
    {
        using var k = hive.OpenSubKey(key);
        return k?.GetValue(name);
    }

    static bool IsOne(RegistryKey hive, string key, string name) => Value(hive, key, name) is int value && value == 1;
}
