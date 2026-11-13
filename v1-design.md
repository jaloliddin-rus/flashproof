# FlashProof v1 — design

*Draft 2, 11 November 2026: revised after an independent review (changes listed in §12). Based on [research-comparison.md](research-comparison.md), the two research reports, and hands-on testing with `Test-UsbDrive.ps1`.*

**Decisions already made:**
- C# / .NET 10 with an Avalonia UI.
- Licensed GPL-3.0, so code can be ported from f3.
- Windows first; macOS and Linux later.
- v1 is Inspect + Full test; the quick fake check comes in v1.1 (decided 12 November 2026).

## 1. What v1 is

FlashProof is a free Windows app that tells ordinary people whether a USB flash drive, SD card or microSD card can be trusted. In plain language, it answers four questions:
1. Does it store data correctly?
2. Is its capacity real?
3. How fast is it in practice?
4. If it isn't working, what should I do?

### Goals
- **Safety first.**
  - The app can never write to the wrong disk.
  - It never surprises the user by destroying data.
  - It stays responsive when a drive hangs.
- **Honest results.** Every verdict states exactly what was tested and what wasn't. There's no health score, no "remaining life", and no "genuine" certificate.
- **Usable by non-experts.** Plain words, real time estimates, and a clear next step for every outcome.
- **No admin rights needed.** v1 never asks for elevation.

### Non-goals for v1
- **Quick fake check (raw probe):** moved to v1.1. It needs admin rights, volume locking and a crash-safe undo log, and the review showed it carries most of the risk. Its design notes are kept in §11.
- **Other platforms:** macOS, Linux and Android. The core library stays portable.
- **Later features:** a raw full-device test with reformat, CMD56 health readouts, SD card ID (CID) reading, speed-class certification and retention re-checks.
- **Out of scope entirely:** repair, low-level formatting and firmware tools.

## 2. User-facing modes (v1)

| Mode | What it does | Writes? | Typical time | Best result it can give |
|---|---|---|---|---|
| **Inspect** | Identifies the device. Checks capacity (shown in GB and GiB), write protection and the reason for it, media presence, recent Windows disk errors, and USB devices that failed to start. Can optionally read every existing file. | No | Seconds; minutes if files are read | "No problems found in these checks" (not a health pass) |
| **Full test** | Fills the free space with test files, flushes, then reads back and checks every byte. Keeps existing files. | Yes (free space only) | Long: about 2 h for a slow 64 GB drive | Storage: "All X GB tested worked correctly today". Capacity: confirmed only if about 95% or more of the drive was tested. |

**Default flows:**
- **New drive or card:** Inspect → Full test. A new drive is empty, so the capacity gets checked too.
- **Drive that holds files:** Inspect. The app then advises copying the files off before the Full test, because writing to a fake can damage them, and because only an empty drive gets its capacity checked.

## 3. Verdicts

The verdict engine follows fixed rules; no AI. A full test always reports **two results**: one for storage (does the tested space work?) and one for capacity (is the size real?).

| Verdict | Rule (minimum evidence) | What the user is told |
|---|---|---|
| **Passed: capacity confirmed** | All planned records written and verified OK, no I/O errors, and test coverage ≥ 95% of the **disk** (not just the volume) | "The drive worked correctly today and its full capacity is real. Keep backups." |
| **Passed: capacity not checked** | Same, but coverage < 95% | "The X GB tested worked correctly today. Only Y% of the drive was tested, so its real capacity wasn't checked. To check it, copy your files off, delete them, and test again." |
| **Fake capacity** | MISPLACED records with a consistent wrap size (§6.1), or BLANK/STALE records from a clear boundary onward | "Only about X GB of the claimed Y GB actually stores data. Return it if you can. Don't trust a smaller partition as a fix." |
| **Not storing data** | BLANK/STALE records from (near) the start of the test | "This drive isn't saving what's written to it. Copy off anything important and stop using it." |
| **Data errors** | BIT_ERRORS, GARBAGE, unreadable sectors, file-system errors during the fill, or MISPLACED records with no consistent pattern | "This drive is losing or mixing up data. Copy off anything important and stop using it." |
| **Connection problem suspected** | Retried-I/O events (153) or timeouts, or the device disappeared and the user says they didn't unplug it | "Try another USB port or card reader, then test again." |
| **Write protected** | The device refuses writes (ERROR_WRITE_PROTECT, or IOCTL_DISK_IS_WRITABLE says no) and no Windows policy explains it | "Check the lock switch on SD cards and adapters. If it's unlocked and still protected, the card has probably locked itself after failing. Copy off your files." |
| **Writing blocked by Windows** | A policy blocks writes: StorageDevicePolicies\WriteProtect, removable-storage Group Policy, or BitLocker "deny write access" | "Windows on this PC is set to block writing to removable drives. This isn't a fault in the drive." |
| **No card in reader** | Reader present, but no media | "Insert or reseat the card." Never "failed". |
| **Detected but not working** | USB device has a problem code (for example 43), or USB storage never produced a disk | "Windows sees something plugged in, but it isn't working. Try another port. If it still fails, it's probably dead. Unplug it: a drive in this state can stop some PCs from starting up." (Learned from the Lexar.) |
| **Can't test** | No file system or RAW; BitLocker-locked; less than 256 MiB free; or a device type v1 doesn't support (§5.1) | States the specific reason and what to do (unlock, copy files off, and so on). |
| **Interrupted** | Cancelled, unplugged (user confirms), the drive or card changed, the PC slept, or the app crashed | "No result. Don't rely on this run." Keep the evidence and offer Inspect. |

**If the device disappears mid-test,** the app asks "Did you unplug it?" Yes → Interrupted. No → Connection problem suspected.
- **Only if it really left** (added 13 Nov 2026, after an ADATA FAT32 stick on this PC): Windows can drop a drive's volume while the drive stays plugged in (a retried I/O, then error 1006 on the test file). The worker then reads the device list for about 3 s; if the drive is still there with the same plug-in time, the app doesn't ask, and the verdict is Connection problem suspected.
- **Unless the drive changed itself** (added 13 Nov 2026, after the same ADATA's retest): if the drive stayed plugged in but afterwards its partition table or its volume serial differs from the confirmed identity, it overwrote its own start (a fake wrapping round, or a failing controller). That's **Data errors**, said with "While writing, the drive damaged its own file system: its partition table or volume ID changed, though it stayed plugged in. Fake and failing drives do this." The worker re-reads only that drive, with the scan's 10 s limit; if it can't tell, the verdict stays Connection problem suspected. Error 34 (ERROR_WRONG_DISK) counts as a lost volume, like 1006.
- **Connection problem suspected says what happened before its advice:** "Windows lost contact with the drive while writing, though it stayed plugged in." or "The drive disappeared from Windows while writing, though it wasn't unplugged.", then "This can be the USB port, a hub or cable, or the drive itself." The user read the bare title as "not connected".

Every result screen also shows:
- capacity in GB and GiB, with a one-line explanation of why a 64 GB drive shows about 59.6 GiB
- speed: average and worst sustained write and read, plus a chart over the test
- the tested range, the coverage percentage, and the untested margin

## 4. Architecture

```
┌──────────────── FlashProof.exe (Avalonia UI, runs as the user) ────────────────┐
│  Views  ←→  Coordinator: state machine, watchdog, journal, verdict engine, export │
└───────────────────────────┬─────────────────────────────────────────────────────┘
                            │ child process, stdin/stdout JSON lines
                 FlashProof.exe --worker   (same user, no elevation)
                 – device scan, Inspect, Full test; one session at a time
```

**Projects in the solution:**

| Project | Contents |
|---|---|
| `FlashProof.Core` | Record format, generator, classifier, wrap analysis, verdict rules, journal and result schema, `IBlockDevice` / `ITestVolume` abstractions. Nothing platform-specific. |
| `FlashProof.Windows` | Enumeration, eligibility, identity, unbuffered overlapped I/O, file-extent lookup, device and power notifications, event-log reader, PnP problem scan, policy checks. |
| `FlashProof` (exe) | Avalonia UI plus coordinator. The same exe runs headless with `--worker`. |
| `FlashProof.Tests` | Unit tests, the `SimulatedDevice` emulator, and emulator-driven tests. |

**Why a worker process:**
- A hung USB request can't be forced to finish, and a process can't exit while its I/O is pending.
- The UI never touches the device and never waits on the worker synchronously.
- Device scanning also runs in a worker, with a timeout per device, because enumerating a dead device can hang (the Lexar case).
- Killing the worker contains the problem inside the app. It doesn't fix the host.

**Why stdin/stdout:** with no elevation in v1, a plain child process with redirected stdio is the simplest secure channel. There's no named pipe and no ACL to get wrong. The elevated worker in v1.1 will need a named pipe (§11).

## 5. Safety rules

### 5.1 Which devices are shown, and which can be written
The device list shows every USB, SD and MMC disk, plus USB devices with a problem code. Each one gets a status: **Ready**, **No card**, **Not working**, **Not supported (reason)** or **Can't test (reason)**.

A device is **Ready** (writable) only if **all** of these hold:
1. **Bus type is USB, SD or MMC.** Read through `IOCTL_STORAGE_QUERY_PROPERTY` (StorageDeviceProperty) on `\\.\PhysicalDriveN` opened with zero access, which works without admin; this was tested on your PC.
2. **`RemovableMedia` is true.** This excludes external SSDs and hard drives, which report fixed media; they show as "Not supported in v1: external SSD or hard drive".
   - **Known limitation:** the few flash drives that report themselves as fixed disks are also excluded.
   - **Deliberately stricter than the research:** non-removable SD/MMC disks are treated as embedded eMMC and rejected, with no override in v1.
3. **The disk holds nothing the running system needs.** It must not hold:
   - the running Windows volume
   - the system partition (registry `HKLM\SYSTEM\Setup\SystemPartition`)
   - any page file (`…\Memory Management\ExistingPageFiles`)
   - the app's own files

   These are read from the registry, because WMI can hang. `MSFT_Partition.IsSystem` / `IsBoot` is a secondary cross-check run with a timeout. An EFI partition on its own is fine, because Ventoy-style drives have one (your SanDisk does, and it reports IsSystem = False).
4. **The volume maps to this one disk only** (`IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`) and lies within the disk's size. A volume that runs past the end of the disk is flagged as a sign of a fake or a bad format.
5. **The volume is usable:** a supported file system (FAT32, exFAT or NTFS), mounted and not BitLocker-locked.
6. **Media is present** (`IOCTL_STORAGE_CHECK_VERIFY2`, which works without admin; tested).
7. **No Windows policy blocks writes** (checked before blaming the device).

There is no "show all disks and force it" override.

### 5.2 Identity and re-checking
- **The identity record combines:**
  - PnP instance ID, vendor/product and serial (these belong to the reader when it's a card)
  - disk size and sector sizes
  - `DEVPKEY_Device_LastArrivalDate`
  - the media-change count from `IOCTL_STORAGE_CHECK_VERIFY2`
  - the partition table ID (MBR signature or GPT disk GUID)
  - the volume GUID path and volume serial number

  The last three are what reveal a **card swapped in the same reader**. Disk numbers and drive letters are never identity.
- **Re-checked:** before writing starts, before each new test file is created, and every 30 s during the test. All checks go through the worker's own open handles. Any change stops the test as **Interrupted: the drive or card changed**.
- **Paths:** file mode writes through `\\?\Volume{GUID}\FlashProof-<session>\`, never through `X:\`, so a letter reassigned to another device can't redirect writes.
- **Verify after replug:** verification may continue read-only if the stable fields match (vendor/product/serial, size, partition ID and volume serial) and the first test file carries this session's tag. Writing never resumes after a reconnect.
- **Needs hardware testing:** whether card readers actually increase the media-change count when a card is swapped (§8).

### 5.3 Protecting data
- **Before the full test,** a confirmation screen names the device ("SanDisk 3.2Gen1, 61.5 GB, E: Ventoy") and states what will be written and how much of the drive it covers. The user must confirm their files are backed up. The screen explains that writing to a fake drive can damage existing files.
- **Session folder:** the full test writes only inside `FlashProof-<session>\`. It deletes only the files its journal lists, never by wildcard.
- **Cleanup:** after a pass, the test files are deleted. After a failure or interruption, they're kept as evidence.
  - The "keep for a later re-check" box was removed from the confirmation screen on 13 Nov 2026, after the first usability session: the tester ticked it, taking it for a required step, and the pass left 61.4 GB of test files on the drive. v1 has no re-check, so kept files only fill the drive. The engine and protocol keep the option (`KeepTestFiles`) for the retention re-checks listed under later features.
- **Never automatic:** formatting, chkdsk repair, resuming writes, or deleting anything uncertain.

### 5.4 Protecting the host
- **I/O pacing:** one I/O in flight, in aligned 4 MiB chunks.
- **Watchdog:**
  - Worker heartbeat every 1 s, on its own thread.
  - No progress for 15 s → a "Slow" notice.
  - No progress for 60 s, or no heartbeat for 10 s → "Not responding", with **Keep waiting** and **Stop** options.
- **Stop:**
  1. Cancel through the CancellationToken, which issues CancelIoEx.
  2. After 10 s, kill the worker asynchronously.
  3. If it hasn't exited after 30 s, show: "The drive *SanDisk 3.2Gen1* is stuck. Unplug it; pending writes may be lost."
  - Unplugging is presented as recovery, never as crash prevention.
- **Worker self-exit:** the worker exits on its own if its stdin closes, which happens when the app dies.
- **Power:** keep the PC awake with `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)`. The worker watches for suspend and resume. A suspend marks the session Interrupted, and errors in the seconds around a resume aren't counted against the drive.
- **During a test:** no polling of other devices or other slots of a multi-slot reader.
- **Errors:** after a serious error, stop issuing new I/O. No retry storms, no bus resets, no driver or registry changes.
- **Off the suspect device:** journals and logs are always on the system disk.
- **Device-removal notifications** (`CM_Register_Notification` on the open handle):
  - On query-remove, close the handles.
  - Never unregister inside the callback, because it can deadlock.

## 6. Test engine

### 6.1 Record format and classification
Every 512-byte record written has this layout. Fields are little-endian, and the layout is versioned.

| Bytes | Field |
|---|---|
| 0–7 | Session tag: the first 8 bytes of SHA-256(seed) |
| 8–15 | Record index: global position in the test, in 512-byte units, continuous across all test files |
| 16–503 | Pseudorandom fill: SplitMix64 in counter mode, keyed by (seed, record index) |
| 504–511 | XxHash64 of bytes 0–503, **unkeyed**, so records from older sessions can still be recognised |

**Classifying a record on read-back** (compared with the regenerated expected record):

| Class | Condition | Meaning |
|---|---|---|
| OK | Identical | Stored correctly |
| BIT_ERRORS | Hamming distance to the expected record ≤ 64 bits, in any bytes | The flash is degrading |
| MISPLACED | Checksum valid, this session's tag, index ≠ expected | Data from another position |
| STALE | Checksum valid, another session's tag | The write never happened |
| BLANK | All 0x00 or all 0xFF | Dropped write, or empty flash |
| GARBAGE | Anything else | Corruption |

Read errors are recorded separately, with their offset and Win32 error code.

**Working out the wrap size:**
- After creating each test file, the worker records the file's on-disk extents (`FSCTL_GET_RETRIEVAL_POINTERS`, plus the partition offset). That maps every record index to a **disk position**.
- For each MISPLACED record, the difference in disk position between where it was found and where it was written gives a wrap-size candidate.
- If at least 90% of the MISPLACED records agree on one wrap size, the verdict is **Fake capacity**, with real capacity ≈ that size. Otherwise it's **Data errors**, because genuine but failing drives can also misdirect writes.

### 6.2 Full test (file mode)
1. **Preflight.**
   - Check eligibility (§5.1) and that free space is at least 256 MiB.
   - Compute the coverage: (free space − reserve) ÷ disk size.
   - Show the scope: "Will test 52.4 GB, which is 85% of the drive. The capacity check needs about 95%: copy files off to include it." Also show a time range.
   - Confirm.
2. **Journal.** Create the host-side journal at `%LOCALAPPDATA%\FlashProof\sessions\<id>\journal.json`, containing the seed, generator version, identity, plan, file extents and checkpoints.
3. **Write.**
   - Files are 1 GiB each, which fits FAT32's 4 GiB limit, and are created through the volume GUID path.
   - Opened with `FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH | FILE_FLAG_OVERLAPPED`.
   - Written through .NET `RandomAccess.WriteAsync` with a CancellationToken; cancellation issues CancelIoEx for that I/O.
   - Buffers come from `NativeMemory.AlignedAlloc` in a `MemoryManager<byte>` and are never freed while an I/O is still pending.
   - The byte count of every I/O is checked, since a handle created outside .NET gets no device-length fallback.
   - A checkpoint is recorded in the journal after each file.
4. **Early fake check.**
   - After each GiB, read back about 64 randomly chosen records from all the data written so far, unbuffered and as whole sectors. MISPLACED, BLANK or STALE results stop the fill with the matching verdict.
   - This check only fires once more data has been written than the drive really holds. For a fake "2 TB" drive with 32 GB of real flash at 10 MB/s, that means about 55 minutes instead of an 80-hour fill.
5. **End of fill.**
   - ERROR_DISK_FULL ends the fill normally; the actual size is recorded.
   - File-system errors such as ERROR_FILE_CORRUPT (1392) or ERROR_DISK_CORRUPT (1393) stop the test, and count as evidence of corruption or a fake.
6. **Flush.** `FlushFileBuffers` on each file, with errors checked. This is not proof that the data reached the flash.
7. **Optional "Unplug and replug before verifying."** Off by default. It gives stronger proof that the read-back isn't coming from the drive's own buffer. Verification then continues read-only under the rules in §5.2.
8. **Verify.** Read back everything unbuffered and classify every record. Speed is recorded per 256 MiB slice.
9. **Verdicts and report** (both results), then cleanup as described in §5.3.

**Sector sizes:** on drives with 4096-byte sectors, the 512-byte records still work, but every read and write must cover whole sectors. NO_BUFFERING constrains offsets, lengths and buffer alignment, not the file size.

### 6.3 Inspect
- **Identity and capacity:** device identity, then capacity in GB and GiB, partition layout and file system, and a check that each volume lies within the disk.
- **Media state:** media presence, then write protection, distinguishing device protection from Windows policy.
- **Recent disk errors:** Windows disk events from the last 24 h (System log: disk 7, 11 and 153, plus storport 129 for UAS drives), matched to the disk, with a caveat that disk numbers change.
  - Event 157 is logged on every unplug without ejecting, so it is shown but never counted as an error.
  - **Ejecting the drive during a FlashProof test** makes Windows log a burst of event 11 ("controller error"). It's Windows' own "is the drive ready?" check (`IOCTL_STORAGE_CHECK_VERIFY2`) failing with no status, retried every 0.5 s, in the seconds before the worker is told to let go. Seen twice on the SanDisk, 0.6–5.1 s before the test ended. Stopping a test and ejecting an idle drive logged nothing.
    - So event 11s on the same drive, from 30 s before to 5 s after the end of one of its FlashProof tests, are shown but not counted, with that explanation. This applies only to tests that ended because the drive was removed and saw no I/O errors.
    - The test's journal gives the times.
    - Other event kinds still count, and so do event 11s outside that window.
- **Failed USB devices:** a scan for USB devices with problem codes, and for USB storage that never produced a disk.
- **Optional:** read every existing file unbuffered (the prototype read could come from RAM) and list unreadable files. A successful read proves the file is readable, not that its contents are correct.

### 6.4 Speed reporting
- **Shown:** average and worst sustained write and read speed (worst 256 MiB slice), plus a chart over the test.
- **USB 2.0 note:** if reads stay at or below about 40 MB/s, a note says they may be capped by a USB 2.0 port or reader.
- **No speed-class certification.** The wording is "worst sustained write on this PC: X MB/s" (the user chose it on 12 Nov over "through this reader", which reads oddly for a USB stick; the speed depends on the port, any card reader and the PC).

### 6.5 Time estimate
- The estimate is `remaining bytes ÷ write speed + total bytes ÷ read speed`.
- The write speed comes from a **rolling window over the last 2 GiB**, not the first GiB, because the drive's fast cache makes the start look too fast.
- The read speed is unknown until verification starts, so the estimate is shown as a range and refined once verification begins.
- It shows "stalled" when progress stops.

## 7. Worker protocol (stdin/stdout, JSON lines)
- **App → worker:** `scan`, `start {spec}` and `cancel`.
- **Worker → app:** `hello {version}`, `heartbeat`, `devices {list}`, `progress {phase, done, total, mbps}`, `sample {offset, ms}`, `warning {code, text}`, `error {code, win32, offset}`, `result {schema}`.
- **Format:** System.Text.Json with source generation; versioned schemas.

## 8. Testing and validation

**Emulator.** `SimulatedDevice` implements `IBlockDevice` and `ITestVolume`, and can behave as:
- a genuine drive
- a wraparound fake (any modulus, including non-power-of-two)
- a limbo fake (drops writes after N)
- a RAM-cache fake (holds the last M MB)
- a fake whose real flash is already filled by files
- a drive with bit flips or with unreadable ranges
- a slow drive
- a drive that hangs at offset X, either ignoring or honouring cancellation
- a drive that disconnects mid-test
- a card that gets swapped
- a drive that lies about its size or alignment

**Required results on the emulator:**
- the correct verdict and capacity result for every behaviour
- existing files byte-identical after a test on a genuine device
- no "capacity confirmed" when coverage is under 95%

**Unit tests.** Generator determinism, the classifier (including Hamming-distance edge cases), wrap-size analysis, the verdict rules, journal crash consistency, and identity checks.

**Safety tests** (on a test machine):
- The system disk, an external SSD, a VHD, a USB boot disk and a BitLocker-locked drive must all be rejected.
- Swapping the device or the card between confirmation and writing must block the write.
- Killing the worker at every state, and unplugging during write, flush and cleanup, must never cause an automatic resume or a wrong deletion.

**Hardware matrix:**
- your SanDisk 3.2Gen1 (genuine, slow, Ventoy layout)
- 3–5 cheap marketplace "1–2 TB" fakes (USB and microSD)
- one USB 2.0 card reader, and one USB 3 reader with several slots
- a laptop with a built-in Realtek reader, to learn its bus type
- the dead Lexar, **only on a spare PC**, since it blocks booting

**Spikes to run in M2, before the details above are fixed:**
1. Does `FSCTL_GET_RETRIEVAL_POINTERS` work without admin on FAT32, exFAT and NTFS?
2. Does the media-change count increase when a card is swapped, on real readers?
3. Which bus type and removable flag do built-in readers report?
4. A possible fast fake screen in file mode, with no admin rights (an idea from the review, unproven):
   - pre-allocate all the test files
   - write 4 KiB at the start of each one, from the highest disk address down
   - read them all back

   If it proves reliable on the emulator and on real fakes, it could become the v1.1 "quick check" without admin.

## 9. Distribution
- **Source and license:** GitHub, GPL-3.0, with release hashes and a plain-language page explaining every verdict.
- **Build:** a single self-contained exe for win-x64 and win-arm64.
- **No elevation:** v1 runs entirely without admin rights, which also removes the main obstacle to a later Microsoft Store release.
- **Signing:** Microsoft Artifact Signing if you're eligible (individuals: US and Canada only); otherwise an OV certificate. Expect SmartScreen warnings for the first weeks.
- **Privacy:** no network access and no telemetry. Serials are redacted in exports by default.

## 10. Milestones

| # | Milestone | Done when |
|---|---|---|
| M0 | Repo, solution skeleton, LICENSE, CI (build + tests) | CI is green |
| M1 | Core: record format, classifier, wrap analysis, verdict engine, journal, `SimulatedDevice` | The emulator suite passes |
| M2 | Windows layer and worker: scan, eligibility, identity, unbuffered overlapped I/O, file extents, watchdog, removal and power handling, plus the spikes | A full test runs end-to-end on the SanDisk from `--worker`, and the safety tests pass on the emulator and in unit tests. The safety tests on real hardware moved to M4 (decided 12 November 2026). |
| M3 | Avalonia UI: device list, Inspect, Full test flow, progress and ETA, both verdicts, export | A non-technical tester completes both modes unaided |
| M4 | Release: signing, docs, full validation run (including the §8 safety tests on real hardware), beta | v1.0 shipped |

**v1.1:** the quick fake check (§11), or the file-mode fast screen if spike 4 works out.

**Later:**
- a raw full test (erase, test the whole device, reformat)
- reading the SD card's ID (CID) on built-in SD-host readers, where an all-zero CID means unavailable
- CMD56 health data, only for allowlisted card models
- retention re-checks after 7 and 30 days
- an A1/A2 random-I/O profile and USB link-speed detection
- macOS and Linux
- localization

## 11. Design notes kept for the v1.1 quick fake check
- **Algorithm:**
  - A port of f3probe v10 under GPL-3.0, credited: it doesn't reset the drive and reads back in random order.
  - Lock and dismount every volume on the disk (`FSCTL_LOCK_VOLUME` + `FSCTL_DISMOUNT_VOLUME`) and run on `\\.\PhysicalDriveN`.
  - Refuse to run if locking fails, or if a volume is BitLocker-encrypted.
- **Crash-safe undo log** (fixes a data-loss bug in a naive backup-and-restore):
  - Each original block is saved and **flushed to the host before** the matching device write.
  - Blocks are restored **in reverse order**, because on a wraparound fake a later backup can contain an earlier marker. Every restored block is then read back to verify it.
  - Everything is restored **before** unlocking.
  - If the app disappears, the worker restores on its own.
  - After an interruption and a replug, the app only rewrites blocks that still hold this session's markers.
  - The user is told to cancel any "Format disk?" prompt from Windows.
- **Elevation:**
  - Standard users, and Windows "Administrator protection", run the elevated worker under a different account. So:
    - The named pipe's ACL allows the user and Administrators.
    - The app checks `GetNamedPipeClientProcessId` against the PID it launched, uses a one-time secret, and sets `PIPE_REJECT_REMOTE_CLIENTS`.
  - The backup lives in `%ProgramData%\FlashProof\<session>` with an admin-only ACL.
  - The worker has a `restore` mode that takes no path argument, and exits on its own on timeout or when the pipe is lost.
  - The app can't be assumed able to kill an elevated worker.
- **Release gate:** the emulator suite plus a real-fake matrix, with existing data byte-identical afterwards.

## 12. Changes after the review (draft 1 → draft 2)
1. **The quick fake check moved to v1.1.** v1 needs no admin rights. The restore-order bug and the elevation problems the review found are fixed in §11.
2. **Two results per test (storage and capacity).** "Capacity confirmed" requires about 95% or more coverage of the disk. Previously, "Full test passed" could approve a fake whose real flash was already full of files.
3. **Card swaps are now detected.** The identity record adds the arrival date, the media-change count, the partition ID and the volume serial; it's re-checked often; and writes go through the volume GUID path.
4. **Eligibility rules fixed:**
   - Ventoy-style EFI partitions are allowed.
   - Removable media is required, which excludes external SSDs.
   - The device list is kept separate from the writable list, so the "No card" and "Not working" verdicts can be reached.
   - System checks use the registry instead of WMI.
5. **Classifier fixed:** an unkeyed checksum (so STALE can be detected), BIT_ERRORS by Hamming distance, a pattern test before calling something a fake, and a new "Not storing data" verdict.
6. **Early fake check corrected:** wrap sizes come from file extents, it samples 64 records per GiB, and its timing claim is honest (about 55 minutes, not "minutes").
7. **New cases:**
   - Can't test (with the reason), and Writing blocked by Windows policy.
   - Disk full is the normal end of a fill; file-system errors count as evidence.
   - Suspend and resume handling, and no polling of other slots.
   - Event 157 isn't an error.
   - The device scan runs in the worker with timeouts.
8. **Simpler:** one exe with `--worker`, stdin/stdout instead of a named pipe, one test at a time.
9. **Minor:** a rolling-window ETA, "Did you unplug it?", consistent cleanup, read-only verification after a replug, and Lexar wording based on the boot problem.

## 13. Open questions for you
1. **v1 scope:** decided on 12 November 2026. v1 is Inspect + Full test; the quick fake check comes in v1.1.
2. **Name:** decided. *14 November 2026: the app is **FlashProof**, renamed from the placeholder "FlashCheck" after web name checks. The official trademark registers (USPTO, EU, UK, WIPO) were not searched.*
3. **Signing:** which country will you publish from? This decides whether Artifact Signing is available.
4. **Test hardware:** are you OK buying a few cheap fake drives and cards (roughly $5–15 each)?
5. **UI languages:** decided on 12 November 2026: English only for v1, with all on-screen text kept in one file so translation can come later.
6. **Supported Windows versions:** decided on 12 November 2026: Windows 10 22H2 and 11, x64 and ARM64. Only Windows 11 x64 can be tested on the development PC; the rest is tested in M4.
7. **Layout:** decided on 12 November 2026: one screen at a time. The home screen lists the drives as cards, each with Inspect and Full test buttons; each step then fills the window, with a Back button.
