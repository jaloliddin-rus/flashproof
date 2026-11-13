#Requires -Version 7
<#
  Test-UsbDrive.ps1 - health test for a USB flash drive (works like H2testw).

  What it does:
    1. Shows what the drive reports: model, serial, capacity, file system.
    2. Reads every existing file on the drive to check it is still readable.
    3. Fills the free space with test files, reads them back and checks every byte,
       then deletes the test files. This catches worn-out flash and fake-capacity drives.
       Reads and writes bypass the Windows cache, so the data really comes from the drive.

  Your existing files are not changed. Exception: on a fake-capacity drive, filling it
  can overwrite existing data, so copy off anything important first.

  Usage:
    .\Test-UsbDrive.ps1              # picks the USB drive automatically if only one is plugged in
    .\Test-UsbDrive.ps1 -Drive F     # choose a drive letter
    .\Test-UsbDrive.ps1 -MaxGB 4     # quick test: write/verify only 4 GB
    .\Test-UsbDrive.ps1 -ReadOnly    # skip the write test

  Each run adds a row to usb-test-results.csv next to this script.
#>
param(
    [string]$Drive,
    [double]$MaxGB = 0,
    [switch]$ReadOnly
)
$ErrorActionPreference = 'Stop'

$source = @'
using System;
using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public class UsbPhaseResult {
    public long Bytes;               // bytes written / read successfully
    public long BadSectors;          // 512-byte blocks that came back different (verify only)
    public long FirstBadOffset = -1; // position of the first bad block, in bytes
    public double MBps;
    public string Error;             // I/O error that stopped the phase, if any
}

public static class UsbTester {
    public const int Chunk = 4 << 20;   // bytes per I/O (4 MiB)
    const int Words = Chunk / 8;
    const int SectorWords = 512 / 8;
    const uint NoBuffering = 0x20000000, WriteThrough = 0x80000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int count, out int done, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int count, out int done, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualFree(IntPtr addr, UIntPtr size, uint type);

    static IOException Err(string what, int code) {
        string msg = code == 0 ? "transferred fewer bytes than requested" : new Win32Exception(code).Message + " (Win32 error " + code + ")";
        return new IOException(what + ": " + msg);
    }

    static string Gb(long bytes) { return (bytes / 1e9).ToString("F2") + " GB"; }

    static string FileName(string dir, int i) { return Path.Combine(dir, "fill_" + (i + 1).ToString("D4") + ".bin"); }

    // Unbuffered handle: data goes straight to / comes straight from the drive, not the RAM cache.
    static SafeFileHandle Open(string path, bool write) {
        SafeFileHandle h = write
            ? CreateFileW(path, 0x40000000, 0, IntPtr.Zero, 2, NoBuffering | WriteThrough, IntPtr.Zero)
            : CreateFileW(path, 0x80000000, 1, IntPtr.Zero, 3, NoBuffering, IntPtr.Zero);
        if (h.IsInvalid) throw Err("open " + Path.GetFileName(path), Marshal.GetLastWin32Error());
        return h;
    }

    // Unbuffered I/O needs a sector-aligned buffer; VirtualAlloc gives a page-aligned one.
    static IntPtr Alloc() {
        IntPtr p = VirtualAlloc(IntPtr.Zero, (UIntPtr)(uint)Chunk, 0x3000, 0x04);
        if (p == IntPtr.Zero) throw new OutOfMemoryException();
        return p;
    }

    // Test pattern: each 8-byte word depends on the run's random seed and its position,
    // so data that ends up in the wrong place (fake capacity) or is stale is caught.
    static void Fill(long[] w, ulong seed, long firstWord) {
        for (int i = 0; i < w.Length; i++) {
            ulong z = seed + (ulong)(firstWord + i) * 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            w[i] = (long)(z ^ (z >> 31));
        }
    }

    static void Progress(string phase, long done, long total, Stopwatch sw) {
        double mbps = done / 1048576.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        Console.WriteLine("[{0}] {1,6:F1} / {2:F1} GB  {3,3}%  {4,6:F1} MB/s", phase, done / 1e9, total / 1e9, done * 100 / total, mbps);
    }

    public static UsbPhaseResult Write(string dir, long chunks, int chunksPerFile, ulong seed) {
        var r = new UsbPhaseResult(); var w = new long[Words]; IntPtr buf = Alloc();
        long total = chunks * Chunk, c = 0; var sw = Stopwatch.StartNew();
        try {
            for (int f = 0; c < chunks; f++) {
                using (SafeFileHandle h = Open(FileName(dir, f), true)) {
                    for (int k = 0; k < chunksPerFile && c < chunks; k++, c++) {
                        Fill(w, seed, c * Words);
                        Marshal.Copy(w, 0, buf, Words);
                        int done;
                        bool ok = WriteFile(h, buf, Chunk, out done, IntPtr.Zero);
                        int code = ok ? 0 : Marshal.GetLastWin32Error();
                        if (!ok || done != Chunk) throw Err("write failed at " + Gb(c * (long)Chunk), code);
                        r.Bytes += Chunk;
                        if (r.Bytes % (1L << 30) == 0 || c == chunks - 1) Progress("Write ", r.Bytes, total, sw);
                    }
                }
            }
        } catch (Exception e) { r.Error = e.Message; }
        finally { VirtualFree(buf, UIntPtr.Zero, 0x8000); }
        r.MBps = r.Bytes / 1048576.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        return r;
    }

    public static UsbPhaseResult Verify(string dir, long chunks, int chunksPerFile, ulong seed) {
        var r = new UsbPhaseResult(); var want = new long[Words]; var got = new long[Words]; IntPtr buf = Alloc();
        long total = chunks * Chunk, c = 0; var sw = Stopwatch.StartNew();
        try {
            for (int f = 0; c < chunks; f++) {
                using (SafeFileHandle h = Open(FileName(dir, f), false)) {
                    for (int k = 0; k < chunksPerFile && c < chunks; k++, c++) {
                        int done;
                        bool ok = ReadFile(h, buf, Chunk, out done, IntPtr.Zero);
                        int code = ok ? 0 : Marshal.GetLastWin32Error();
                        if (!ok || done != Chunk) throw Err("read failed at " + Gb(c * (long)Chunk), code);
                        Marshal.Copy(buf, got, 0, Words);
                        Fill(want, seed, c * Words);
                        if (!new ReadOnlySpan<long>(want).SequenceEqual(new ReadOnlySpan<long>(got))) {
                            for (int s = 0; s < Words; s += SectorWords) {
                                if (!new ReadOnlySpan<long>(want, s, SectorWords).SequenceEqual(new ReadOnlySpan<long>(got, s, SectorWords))) {
                                    if (r.BadSectors++ == 0) r.FirstBadOffset = c * (long)Chunk + s * 8L;
                                }
                            }
                        }
                        r.Bytes += Chunk;
                        if (r.Bytes % (1L << 30) == 0 || c == chunks - 1) Progress("Verify", r.Bytes, total, sw);
                    }
                }
            }
        } catch (Exception e) { r.Error = e.Message; }
        finally { VirtualFree(buf, UIntPtr.Zero, 0x8000); }
        r.MBps = r.Bytes / 1048576.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        return r;
    }

    static byte[] readBuf = new byte[Chunk];

    // Reads a whole existing file; throws if any part of it can't be read.
    public static long ReadAll(string path) {
        long n = 0; int k;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan))
            while ((k = fs.Read(readBuf, 0, readBuf.Length)) > 0) n += k;
        return n;
    }
}
'@
if (-not ('UsbTester' -as [type])) { Add-Type -TypeDefinition $source }

function Format-GB([double]$bytes) { [math]::Round($bytes / 1e9, 1) }

# --- 1. Find the USB drive (refuses anything that isn't on the USB bus) -------
$usbVolumes = @(Get-Disk | Where-Object BusType -eq 'USB' | Get-Partition |
    Where-Object { "$($_.DriveLetter)" -match '^[A-Z]$' } | Get-Volume)
$found = ($usbVolumes | ForEach-Object { "$($_.DriveLetter):" }) -join ', '
if ($Drive) {
    $vol = $usbVolumes | Where-Object { "$($_.DriveLetter)" -eq $Drive.TrimEnd(':', '\').ToUpper() }
    if (-not $vol) { throw "$Drive is not a plugged-in USB drive. USB drives found: $(if ($found) { $found } else { 'none' })" }
} elseif ($usbVolumes.Count -eq 0) {
    throw 'No USB drive with a drive letter found. Plug one in and check it shows up in File Explorer.'
} elseif ($usbVolumes.Count -gt 1) {
    throw "More than one USB drive found ($found). Pick one with -Drive <letter>."
} else {
    $vol = $usbVolumes[0]
}
$letter = "$($vol.DriveLetter)"
$root = "$($letter):\"
$disk = Get-Partition -DriveLetter $letter | Get-Disk

Write-Host "Drive      : $($letter): '$($vol.FileSystemLabel)' ($($vol.FileSystem))"
Write-Host "Device     : $($disk.FriendlyName)   serial $($disk.SerialNumber)"
Write-Host "Capacity   : $(Format-GB $disk.Size) GB reported (should be a bit under the size on the label)"
Write-Host "Free space : $(Format-GB $vol.SizeRemaining) GB"
if ($disk.IsReadOnly) { Write-Host 'WARNING    : the drive reports itself as READ-ONLY (often a sign of failed flash)' }

# --- 2. Read every existing file ----------------------------------------------
$files = @(Get-ChildItem $root -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\System Volume Information\\' })
Write-Host "`nReading $($files.Count) existing file(s)..."
$unreadable = @()
$sw = [Diagnostics.Stopwatch]::StartNew(); $readBytes = 0
foreach ($f in $files) {
    try { $readBytes += [UsbTester]::ReadAll($f.FullName) }
    catch { $unreadable += $f.FullName; Write-Host "  UNREADABLE: $($f.FullName) - $($_.Exception.GetBaseException().Message)" }
}
Write-Host "  Read $(Format-GB $readBytes) GB, $($unreadable.Count) unreadable file(s)."

# --- 3. Write + verify test on the free space ---------------------------------
$w = $null; $v = $null; $plannedBytes = 0; $testNote = 'read-only'
if (-not $ReadOnly) {
    $chunk = [UsbTester]::Chunk
    $bytes = $vol.SizeRemaining - 64MB              # leave a little room for the file system
    if ($MaxGB -gt 0) { $bytes = [math]::Min($bytes, [long]($MaxGB * 1e9)) }
    $chunks = [long][math]::Floor($bytes / $chunk)
    if ($chunks -le 0) {
        $testNote = 'no free space for write test'
    } else {
        $plannedBytes = $chunks * $chunk
        $testNote = if ($MaxGB -gt 0) { "quick ($(Format-GB $plannedBytes) GB)" } else { 'full' }
        $seed = [BitConverter]::ToUInt64([Security.Cryptography.RandomNumberGenerator]::GetBytes(8), 0)
        $testDir = Join-Path $root ('_usbtest_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
        New-Item -ItemType Directory -Path $testDir | Out-Null
        try {
            Write-Host "`nWrite test: filling $(Format-GB $plannedBytes) GB with test data..."
            $w = [UsbTester]::Write($testDir, $chunks, 256, $seed)        # 256 x 4 MiB = 1 GiB per file
            Write-Host "`nVerify: reading it back and checking every byte..."
            $v = [UsbTester]::Verify($testDir, [long]($w.Bytes / $chunk), 256, $seed)
        } finally {
            Remove-Item $testDir -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path $testDir) { Write-Host "NOTE: could not delete $testDir - delete it by hand." }
        }
    }
}

# --- 4. Verdict ----------------------------------------------------------------
$problems = @()
if ($disk.IsReadOnly) { $problems += 'drive is read-only' }
if ($unreadable.Count) { $problems += "$($unreadable.Count) existing file(s) unreadable" }
if ($w.Error) { $problems += $w.Error }
if ($v.Error) { $problems += $v.Error }
if ($v.BadSectors) { $problems += "$([math]::Round($v.BadSectors * 512 / 1MB, 2)) MB came back corrupted (first at $(Format-GB $v.FirstBadOffset) GB into the test)" }
if ($w -and -not $w.Error -and -not $v.Error -and $v.Bytes -ne $plannedBytes) { $problems += 'verified less data than was written' }
$verdict = if ($problems) { 'FAIL' } else { 'PASS' }

$row = [pscustomobject]@{
    Date            = Get-Date -Format 'yyyy-MM-dd HH:mm'
    Device          = $disk.FriendlyName
    Serial          = $disk.SerialNumber
    Label           = $vol.FileSystemLabel
    SizeGB          = Format-GB $disk.Size
    FileSystem      = $vol.FileSystem
    ExistingFiles   = $files.Count
    UnreadableFiles = $unreadable.Count
    Test            = $testNote
    TestedGB        = Format-GB ([long]$v.Bytes)
    WriteMBps       = if ($w) { [math]::Round($w.MBps, 1) } else { '' }
    ReadMBps        = if ($v) { [math]::Round($v.MBps, 1) } else { '' }
    CorruptMB       = if ($v) { [math]::Round($v.BadSectors * 512 / 1MB, 2) } else { '' }
    Verdict         = $verdict
    Problems        = $problems -join '; '
}
$row | Export-Csv (Join-Path $PSScriptRoot 'usb-test-results.csv') -Append -NoTypeInformation
Write-Host "`n===== RESULT: $verdict ====="
$row | Format-List | Out-String | Write-Host
