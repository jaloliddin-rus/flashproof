# The two research reports, compared and fact-checked

*11 November 2026*

- **Report A:** `flash-health-research.md`
- **Report B:** `Flash Drive Diagnostic Tool Research.md`

Both reports are kept private; this comparison and its source links are what's published.

**How this was done:**
- Three agents compared the reports section by section: failure modes and health data, existing tools, and method, architecture and safety.
- Four more agents checked about 45 disputed or design-critical claims against primary sources:
  - Microsoft Learn, and Microsoft's open-source storage driver samples
  - Apple documentation, and the XNU and IOStorageFamily source
  - Linux kernel and UDisks source
  - the SD Association
  - each tool's official page or GitHub repository
- Evidence from forums is labelled as such.

## Bottom line

- **Report A is the one to trust for what the tool should claim and how it should behave.** It cites official sources, says how sure it is of each claim, and was right in almost every dispute that was checked.
- **Report B is more concrete and more useful for implementation details.** It gives exact API sequences, a sector format, a quick-probe design, and macOS and Linux specifics. **But it overstates certainty and contains a number of factual errors.** Many of its sources are forums, download mirrors, vendor blogs, and a data-recovery company's website. Use its details only after checking them.

## Where the reports agree (solid ground)

- Ordinary thumb drives and cards have no SMART health data. Windows' "Healthy" label says nothing about the flash itself.
- Only writing every byte with position-unique data and reading it back proves the storage works. Quick checks are screening tools. Nothing can predict remaining life, so the tool shouldn't show a 0–100 health score.
- Filling a fake drive can destroy existing files, so users must back up first.
- Fast fake detection needs raw-device access, and the tool must write all test markers before reading any back.
- How to bypass the cache: NO_BUFFERING + WRITE_THROUGH on Windows, O_DIRECT on Linux, and F_NOCACHE or the raw `/dev/rdiskN` device on macOS. See the device-cache caveat below.
- Disk I/O should run in a separate worker process, and the GUI should never run as admin.
- The tool should accept SD and MMC bus types (built-in card slots) but exclude system, boot and eMMC disks.
- A browser-only tool isn't possible, and Android can wait.
- The default stack suggestion is a Rust core with a Tauri GUI; Electron is a poor fit. Report A also calls C#/.NET reasonable for a Windows-first product.
- Sign the releases, publish the source, and never tell users to disable antivirus.

## Disputes, settled by checking the sources

| Topic | Report A | Report B | What the sources say |
|---|---|---|---|
| Does write-through force the drive to save data? | Bypasses Windows' cache, but not necessarily the drive's own | Tells the drive's controller to flush | **A is right.** Windows only sends a flush or FUA command when the drive says it has a write cache. Many flash drives don't, so nothing forces their internal buffer out to storage. Only reading the data back proves it was stored. |
| When to call a drive "hung" | Warn after 10–15 s, escalate after 30–60 s | 5 s timeout, 3 s after cancelling, then tell the user to unplug "to prevent a blue screen" | **A is right.** Windows itself gives each request a long timeout plus several retries. It waits up to 5 minutes before giving up on a cancelled request. A process can't exit while it has I/O pending. Unplugging is a recovery step; no source says it prevents 0x9F crashes. |
| How certain is a quick fake probe? | Limited; test it against real fakes first | Catches wraparound "with 100% mathematical certainty" | **A is right.** f3's own docs still call f3probe experimental. B is right that f3probe v10 no longer resets the drive and reads back in random order. |
| Is ValiDrive safe for your data? | Designed to restore your data; loss is possible but untested | "Can corrupt data on wraparound fakes" | **Closer to A.** GRC says ValiDrive always rewrites the original data. GRC staff say (in a forum) that the restore can fail on a drive that doesn't store writes reliably. B's claim is its own inference. |
| Is f3probe safe for your data? | A restoring mode exists, but isn't guaranteed | Contradicts itself (says both "restores" and "destructive") | The default mode keeps a backup in RAM and writes it back at the end, but the backup is lost if f3probe crashes. `--destructive` skips the restore. It runs on Linux only. |
| Checking speed class | A benchmark can screen a card but not certify it | Grade the card by its "worst 10-second window" | **A is right.** The SD Association measures the minimum speed over 4 MB windows, under conditions a USB reader can't reproduce. The 10-second rule is one blog's own test method. |
| A1/A2 card classes | — | A1 and A2 came with SD 6.0 | **B is wrong.** A1 came with SD 5.1 (2016) and A2 with SD 6.0 (2017). B's IOPS figures are right. Reaching A2 speeds needs command queuing in the host, which USB readers don't do. |
| A card ID (CID) that doesn't match | A lead, not proof | "Confirmed fake" | **A is right.** Genuine budget cards use unlisted manufacturer IDs, and some Realtek readers return an all-zero CID. |
| Endurance and retention numbers | No universal figure; cites measured card tests | TLC 300–3,000 cycles; retention "52→13 weeks" and "404→8 weeks" | **A is right.** Kingston lists TLC as up to 3,000 cycles and QLC up to 1,000, with no low end of 300. The JEDEC figures are end-of-life requirements for SSDs, not SD card data, and "13 weeks" is the recovery firm's own extrapolation. A's measured microSD figures (Murray, EOSS 2024) check out, though they come from conference slides that tested one card per pattern. |
| Windows event 129 | Context only | A sign of a USB reset | **A is right.** Event 129 is logged by storport drivers when a request times out. Ordinary flash drives (Bulk-Only Transport) use the usbstor driver, which never logs it; only UAS drives can. Events 153 (retried I/O) and 157 (surprise removal) cover both kinds. |
| Your 0x9F crash | Needs the dump and the device layout | "Classic IRP starvation", stated as fact | **Neither proves it.** The dump shows the webcam's video driver stuck on a power-down request. Microsoft documents that hubs wait for their child devices, but no source shows one failing device stalling another device's power request. The Lexar link rests on the timing and on the PC not booting with it plugged in. |

### Smaller errors in Report B

- `badblocks -v` means verbose, not read-only. Read-only is the default.
- H2testw's latest version is 1.4 (2008), not 1.44.
- SD Memory Card Formatter has an official Linux command-line version, and it's for SD cards only.
- macOS notarization doesn't grant access to raw disks. Opening `/dev/rdiskN` needs root plus the user's Removable Volumes or Full Disk Access consent.
- The macOS F_NOCACHE limit for writes under 16 KB is real, but it dates back to OS X 10.6, not "Darwin 20".
- EV code-signing certificates stopped giving instant SmartScreen reputation in 2024.
- Health data (CMD56) through "ordinary USB readers" only works for Swissbit's S-4x/S-5x cards with specific tested readers. ATP's only works in native card slots.

### Report B claims that checked out and are worth using

- Built-in Mac SD readers report cards as internal (DiskArbitration source, plus a blog), so the tool shouldn't filter on "external".
- UDisks2 OpenDevice accepts O_DIRECT and O_EXCL. Expect one admin password prompt.
- WebUSB blocks the mass-storage device class. Chrome's "usb-unrestricted" exception only applies to apps installed by an administrator policy.
- Microsoft Artifact Signing costs $9.99/month (Basic), but individuals are only eligible in the US and Canada.
- The Microsoft Store avoids SmartScreen warnings, but an app that needs admin rights for raw disk access requires Microsoft's approval.
- Sending CMD56 to a card that doesn't support it can leave the card read-only until it's power-cycled (forum report).

## What each report does best

**Report A**
- Labels every claim by type of evidence (Established, Vendor claim, Inference, and so on).
- Uses honest verdict wording: "Full test passed" means the drive worked correctly today, and quick passes are marked "limited coverage".
- Has a read-only "inspect" mode.
- Explains GB vs GiB, so a 64 GB drive showing 59.6 GiB isn't mistaken for a fake.
- Keeps the random seed in a journal on the computer, not on the drive, and keeps test files after a failure as evidence.
- Plans validation with fault injection and real fake drives before release, and covers licensing obligations.

**Report B**
- Gives a concrete per-sector format (the address XORed with the seed, plus a checksum). It can tell apart sectors that are OK, changed, overwritten (wraparound) or corrupted.
- Uses a binary search to find a fake's real capacity, and aborts a full test early if the start of the drive gets overwritten.
- Gives the exact Windows sequence for locking and dismounting a volume before raw writes, plus detailed macOS and Linux steps.
- Detects a USB 2.0 reader that caps the speed results, and suggests a "refund proof" report for people who bought a fake.

## Recommendations for your tool

1. **Use Report A's wording and limits.**
   - Verdicts: full test passed / quick check passed (limited coverage) / fake capacity / data errors / not detected (dead, or a reader problem) / interrupted.
   - Don't say "Healthy & Genuine", show a remaining-life percentage, or certify speed classes. Report "worst sustained write speed through this reader" instead.
2. **Add Report B's quick fake probe, but only once it's proven.** It writes test data spread over the whole drive, writes everything before reading any of it back, and reads in random order. Release it only after it passes tests against real fake drives. On Windows it needs admin rights.
3. **Handle hangs carefully.**
   - Run I/O in a separate worker process.
   - Show a "slow" warning early, but say "not responding" only after 60 seconds or more.
   - Present unplugging as a recovery step.
   - Keep the PC awake during tests.
4. **Don't trust flags to save the data.** For full tests, offer "unplug and replug before verifying", so the read-back can't come from the drive's own buffer.
5. **Pick targets safely.** Never decide by bus type alone. Exclude any disk that holds Windows, the boot partition or the page file. Accept USB, SD and MMC disks only if they're removable or the user confirms them.
6. **Treat card extras as optional.** Read the CID when it's available; all zeros means unavailable, not fake. Send CMD56 only to card models known to support it.
7. **Licensing.**
   - Reusing code from f3, MediaTester or CapacityTester makes your tool GPL-3.0.
   - MIT code (the CrystalDisk tools) and BSD code (agnostics) is fine as long as you keep the notices.
   - Link to H2testw, ValiDrive, RMPrepUSB and SD Memory Card Formatter instead of bundling them.
8. **Distribution.**
   - Start with a signed direct download.
   - Check whether you're eligible for Artifact Signing (individuals: US and Canada only). If not, buy an OV certificate.
   - Expect SmartScreen warnings for the first weeks either way.

## What this means for Test-UsbDrive.ps1

Both reports would change these things:
- **Target check:** replace "BusType is USB" with the safer rule in recommendation 5.
- **Hangs:** move disk I/O out of the PowerShell process and make it cancellable. Right now a hung drive freezes the whole script.
- **Block format:** add an address header and a checksum to each block, so a wraparound fake can be told apart from corruption.
- **`-MaxGB`:** it only tests the start of the drive, which is exactly where a fake's real storage is, so it can't catch fakes. Rename it or replace it with a probe spread across the drive.
- **Existing files:** read them unbuffered. The current read can be served from RAM.
- **Speed:** report speed over time, not one average.

Report A also suggests:
- keeping test files after a failure
- reporting the 64 MB margin that goes untested
- showing both GB and GiB
- hiding serial numbers in the CSV

## Still unverified: needs your own testing

- What bus type Windows reports for Realtek laptop card readers (USB- or PCIe-attached) and for soldered eMMC storage.
- How well an f3probe-style quick probe catches real fake drives.
- Whether a cheap Realtek USB reader exposes card registers on Linux.
- macOS behaviour: F_FULLFSYNC on exFAT, and the F_NOCACHE limits.
- Whether O_DIRECT works through Android's storage layer on real phones.

## Key primary sources used in the checks

- Microsoft, file buffering: https://learn.microsoft.com/en-us/windows/win32/fileio/file-buffering
- Microsoft, canceling pending I/O: https://learn.microsoft.com/en-us/windows/win32/fileio/canceling-pending-i-o-operations
- Microsoft, TerminateProcess: https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-terminateprocess
- Microsoft, bug check 0x9F: https://learn.microsoft.com/windows-hardware/drivers/debugger/bug-check-0x9f--driver-power-state-failure
- Microsoft, STORAGE_BUS_TYPE: https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddstor/ne-ntddstor-storage_bus_type
- Microsoft, storage timeouts and event 129: https://learn.microsoft.com/en-us/archive/blogs/ntdebugging/understanding-storage-timeouts-and-event-129-errors
- Microsoft, storage class driver samples (classpnp, disk): https://github.com/microsoft/Windows-driver-samples
- Microsoft, SmartScreen reputation: https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation
- Microsoft, Artifact Signing: https://azure.microsoft.com/en-us/products/artifact-signing
- GRC, ValiDrive FAQ: https://www.grc.com/validrive/faq.htm
- f3 repository and changelog: https://github.com/AltraMayor/f3
- badblocks man page: https://man7.org/linux/man-pages/man8/badblocks.8.html
- SD Association, speed class: https://www.sdcard.org/developers/sd-standard-overview/speed-class/
- SD Association, application performance class: https://www.sdcard.org/developers/sd-standard-overview/application-performance-class/
- Linux MMC sysfs attributes: https://www.kernel.org/doc/html/latest/driver-api/mmc/mmc-dev-attrs.html
- sdmon (list of cards that support CMD56): https://github.com/Ognian/sdmon
- JEDEC SSD endurance presentation (Cox): https://www.jedec.org/sites/default/files/Alvin_Cox%20[Compatibility%20Mode]_0.pdf
- Murray, EOSS 2024 slides: https://hosted-files.sched.co/eoss24/d0/eoss2024-murray-sdcards.pdf
- HeatWatch (HPCA 2018): https://people.inf.ethz.ch/omutlu/pub/heatwatch-3D-nand-errors-and-self-recovery_hpca18.pdf
- Apple XNU source: https://github.com/apple-oss-distributions/xnu
- UDisks2 Block interface: https://storaged.org/doc/udisks2-api/latest/gdbus-org.freedesktop.UDisks2.Block.html
- WebUSB spec: https://wicg.github.io/webusb/
- Android, accessing documents and other files: https://developer.android.com/training/data-storage/shared/documents-files
