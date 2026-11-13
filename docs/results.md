# What your FlashProof result means

FlashProof ends every test with a result in plain words and a next step. This page explains each result: what it means, and what to do. The quoted lines are exactly what the app shows. Where the app fills in a number, this page writes *X* or *Y*.

## In short

| Result | In short | What to do |
|---|---|---|
| [Passed: capacity confirmed](#passed-capacity-confirmed) | Every byte tested was stored correctly, and the size is real | Use it, and keep backups |
| [Passed: capacity not checked](#passed-capacity-not-checked) | The tested part works, but your files left too little room to check the size | Empty the drive and test again if you want the size checked |
| [Fake capacity](#fake-capacity) | The drive is smaller than it claims | Return it |
| [Not storing data](#not-storing-data) | The drive doesn't keep what's written to it | Copy off what you can and stop using it |
| [Data errors](#data-errors) | The drive loses or mixes up data | Copy off what you can and stop using it |
| [Connection problem suspected](#connection-problem-suspected) | The test lost contact with the drive | Try another port or card reader, then test again |
| [Interrupted](#interrupted) | The test didn't finish | Test again |

Before a test, each drive also has a status: see [Before testing](#before-testing-the-drives-status). Inspect has results of its own: see [Inspect](#inspect).

## Full test results

The Full test fills the drive's free space with test data, reads all of it back, and checks every byte. Your existing files are kept. It always gives two answers:

- **Storage:** does the space that was tested work?
- **Capacity:** is the drive's size real? This needs about 95% of the drive to be tested, so it's checked only on a drive that's empty or nearly empty.

### Passed: capacity confirmed

> The drive worked correctly today and its full capacity is real. Keep backups.

**What it means.** FlashProof tested at least 95% of the drive. Everything it wrote came back exactly right, with no errors. The drive stores data correctly, and its size is real.

**What it doesn't mean.** It doesn't tell you how long the drive will last. Flash drives and memory cards can fail without warning, whatever a test said before.

**What to do.** Use it. Keep a copy of anything important somewhere else.

### Passed: capacity not checked

> The *X* GB tested worked correctly today. Only *Y*% of the drive was tested, so its real capacity wasn't checked. To check it, copy your files off, delete them, and test again.

**What it means.** The space that was tested works. But your files take up part of the drive, and FlashProof never writes over them, so less than 95% of the drive was tested. A fake drive can store its first gigabytes correctly and fail only beyond its real size, so the size can't be confirmed from part of it.

**What to do.** If you want the size checked:
1. Copy your files from the drive to your computer.
2. Delete them from the drive.
3. Run the Full test again.
4. Copy your files back.

### Fake capacity

> Only about *X* GB of the claimed *Y* GB actually stores data. Return it if you can. Don't trust a smaller partition as a fix.

**What it means.** The drive tells Windows it's bigger than the memory inside it. Data written beyond the real size is lost, or overwrites data written earlier. This is what fake drives and cards do.

**What to do.**
- **Return it** to the seller if you can. "Copy report" or "Save report…" gives you a text report of what the test found, to show the seller.
- **Check the files that were already on it.** On a fake drive, writing to the free space can overwrite them.
- **Don't fix it with a smaller partition.** The drive still reports the false size, so formatting it again later, or using it in a camera or another device that trusts that size, can lose your data again.

### Not storing data

> This drive isn't saving what's written to it. Copy off anything important and stop using it.

**What it means.** From the start of the test, what FlashProof wrote read back as empty, or as old data that was there before. The drive accepts writes but doesn't keep them. Failing drives and some fakes do this.

**What to do.** Copy off anything important that still reads correctly, then stop using the drive.

### Data errors

> This drive is losing or mixing up data. Copy off anything important and stop using it.

**What it means.** Some of the data read back wrong: changed bits, garbage, data in the wrong place, parts that couldn't be read at all, or file-system errors while writing. Any of these means the drive can't be trusted with your files.

Sometimes the app says first what happened:

> While writing, the drive damaged its own file system: its partition table or volume ID changed, though it stayed plugged in. Fake and failing drives do this.

That means the drive overwrote its own beginning during the test, where Windows keeps the information about the drive's partitions. Windows may then show the drive as empty or ask to format it.

**What to do.**
- Copy off anything important that still reads correctly, then stop using the drive.
- If you used a card reader or a USB hub, testing once more with another reader or port can show whether the problem follows the drive.

### Connection problem suspected

> Try another USB port or card reader, then test again.

The app first says what happened, usually one of these:

> Windows lost contact with the drive while writing, though it stayed plugged in. This can be the USB port, a hub or cable, or the drive itself.

> The drive disappeared from Windows while writing, though it wasn't unplugged. This can be the USB port, a hub or cable, or the drive itself.

(Instead of "while writing" it can say "while reading back" or "during the test".)

**What it means.** The test lost contact with the drive, or Windows had to retry reads or writes. That can be the USB port, a hub or extension cable, the card reader, or the drive itself. This result says nothing about whether the drive stores data correctly.

**What to do.**
- Plug the drive straight into a port on the computer, not through a hub or extension cable. For a memory card, try another card reader if you have one.
- Run the Full test again.
- If it happens again on another port or reader, the drive itself is the likely cause.

**"Did you unplug it?"** If the drive disappears during a test, FlashProof asks:

> The drive disappeared during the test. Did you unplug it?

If you answer yes, the result is [Interrupted](#interrupted). If you answer no, it's Connection problem suspected. FlashProof doesn't ask when the drive never left Windows' list of drives.

### Interrupted

> No result. Don't rely on this run.

The app then gives the reason:

- "The test was stopped."
- "The drive or card changed during the test."
- "The drive was removed during the test."
- "The PC went to sleep during the test."
- "FlashProof was closed during the test."
- "The test stopped unexpectedly."

**What it means.** The test didn't finish, so there's no result: not good, not bad.

**What to do.** Run the Full test again when the drive can stay plugged in until the end. FlashProof keeps the PC awake during a test, but closing a laptop's lid can still put it to sleep.

**Test files.** If the test stopped before it could clean up, its files stay on the drive, and the app says so:

> The test files are still on the drive, in the folder FlashProof-*session*. Delete that folder when you no longer need it.

### Unplug and replug before verifying

This is an optional box on the screen before the Full test:

> Stronger proof: some fake drives keep the newest data in a small memory that empties when they're unplugged. When writing ends, FlashProof asks you to unplug the drive and plug it back in, so stay near the PC.

When writing is done, click "Eject now", unplug the drive, wait 5 seconds, and plug it back in. FlashProof notices by itself when it's back, checks that it's the same drive, and then reads everything back. If you plug in a different drive, FlashProof won't touch it.

If the drive comes back without the test's files, the app says:

> The drive that was plugged in doesn't have this test's files, or they can't be read. Plug in *the drive*, or press Stop. If this is the drive you tested, it may not have kept the data: press Stop, then run the Full test again without unplugging.

Run without unplugging, a drive that can't keep data gets [Not storing data](#not-storing-data).

## Before testing: the drive's status

The first screen lists your USB drives and memory cards, each with a status. The computer's internal SSDs and hard drives aren't listed.

### Ready to test

Inspect and the Full test can both run.

### No card

> Insert or reseat the card.

The card reader is there, but there's no card in it, or Windows can't see the card. This is never a fault in the card.

### Not working

> Windows sees something plugged in, but it isn't working. Try another port. If it still fails, it's probably dead. Unplug it: a drive in this state can stop some PCs from starting up.

Windows found a USB device but couldn't start it (the app may add "Windows reports problem code *N*."), or found USB storage that never showed up as a drive.

### Write protected

> Check the lock switch on SD cards and adapters. If it's unlocked and still protected, the card has probably locked itself after failing. Copy off your files.

The drive or card refuses to be written to. Full-size SD cards and microSD adapters have a small lock switch on the side: slide it away from "Lock" and try again. If it's unlocked and still refuses, you can probably still read your files, so copy them off.

### Writing blocked by Windows

> Windows on this PC is set to block writing to removable drives. This isn't a fault in the drive.

A setting on this computer stops all writing to removable drives. On a work or school computer, the IT department usually sets this. Inspect still works. To run the Full test, use another computer, or ask whoever manages this one.

### Not supported

The drive isn't the kind FlashProof tests. The app says which:

- "External SSDs and hard drives aren't supported in this version."
- "Built-in storage (eMMC) isn't supported."
- "This kind of device isn't supported."

A few genuine USB flash drives describe themselves to Windows as fixed disks, like an external hard drive. FlashProof can't tell them apart, so it doesn't test them either.

### Can't test

The drive is the right kind, but something stops a test right now. The app says what, and what to do:

- "Less than 268 MB is free. Copy some files off to make room, then test again."
- "A partition on it is bigger than the drive itself: a sign of a fake drive or a damaged format. Copy off anything important."
- "It isn't formatted as FAT32, exFAT or NTFS, or Windows can't read its format. Formatting it in Windows fixes that, but erases it."
- "It's encrypted with BitLocker and locked. Unlock it in File Explorer, then test again."
- "It holds files that Windows or FlashProof needs while running, so FlashProof won't write to it."
- "Its volume spans more than one disk, which FlashProof can't test."

## Inspect

Inspect only reads from the drive. In seconds, it checks the drive's size and partitions, whether it accepts writes, and Windows' log of disk errors from the last 24 hours. It can also read every file on the drive.

### No problems found

> No problems found in these checks.

> This isn't a health pass: a fake or failing drive can pass these checks. Only the Full test checks that data is stored correctly.

Inspect looks at what Windows can report without writing anything, and a fake drive reports what it wants. Use the Full test to know whether the drive can be trusted.

### Problems Inspect can find

- "The drive refuses writes." See [Write protected](#write-protected).
- "A partition is bigger than the drive itself: a sign of a fake drive or a damaged format." Copy off anything important, then run the Full test if the drive can be tested.
- "Windows logged disk errors for this drive in the last 24 hours." The drive, its connection or the reader had trouble recently. Try the Full test, preferably on another port.
- "Windows logged disk errors in the last 24 hours that may be from this drive." Windows can't say for sure that the errors were this drive's: each one's note below says why.
- "Some files couldn't be read." The list shows which, with Windows' error code.

### Disk errors

Each error Windows logged is listed with its time:

- **Bad block:** part of the drive couldn't be read or written.
- **Controller error:** the drive, or its connection, didn't answer Windows properly.
- **A read or write had to be retried:** it worked the second time, but something was unreliable.
- **The drive stopped responding and was reset.**
- **Unplugged without ejecting first (not an error):** shown, but never counted.

Some lines carry a note:
- "Not counted: Windows logs these itself when a drive is ejected during a FlashProof test, while it checks whether the drive is still ready."
- "Logged before this drive was last plugged in, under the same disk number, so it may have been another drive."
- "The driver that logged this can't say which drive it was, so it may have been another one."

### Read every file

> Reads every file to check that it can be read. A file that reads fine may still hold wrong data.

A file that can't be read is damaged, at least in part. A file that reads fine may still have changed, since FlashProof can't know what your files should contain. Files Windows wouldn't open (in use, or access denied) are skipped and counted separately.

If the drive holds files, Inspect also says:

> This drive holds files. Copy them off before the Full test: writing to a fake drive can damage them, and only an empty drive gets its capacity checked.

## Other things on the result screen

**Sizes.**

> Drives are sold in GB (a billion bytes). Windows counts in GiB (1,073,741,824 bytes), so a 64 GB drive shows as about 59.6 GiB.

This is why a genuine drive looks smaller in Windows than on its packaging. It isn't a sign of a fake.

**Speed.** FlashProof shows the average and the worst sustained speed for writing and for reading back, and a chart over the whole test. Its wording is "worst sustained write on this PC", because the speed depends on the port, any card reader and the computer, not only on the drive. Some drives write fast at first and slow down as they fill; the chart shows that. If reads were 40 MB/s or slower, the app adds:

> Reads were 40 MB/s or slower: they may be limited by a USB 2.0 port or card reader.

FlashProof doesn't certify speed classes (the speed ratings printed on cards).

**Tested.** How much of the drive was tested, and how much wasn't: the part used by the file system and your existing files is never written.

**The report.** "Copy report" and "Save report…" give a plain-text report you can paste or send. Serial numbers are hidden unless you tick "Include serial numbers". Its "Records" line counts the 512-byte pieces of test data that were checked:

- **OK:** came back exactly as written.
- **Bit errors:** a few bits changed. The memory is wearing out.
- **Misplaced:** data written to one place came back from another. A fake drive wraps around like this; a failing one can too.
- **Stale:** data from an earlier test came back. The write never happened.
- **Blank:** came back empty. The write was dropped, or there's no real memory there.
- **Garbage:** came back as something else entirely.

## What FlashProof never tells you

- **A health score or "remaining life".** A test shows how the drive behaved today. Most flash drives and cards don't report their wear, so no test can say when one will fail.
- **That a drive is "genuine".** A pass means it stored data correctly and had its claimed size. It doesn't say who made it.
- **Anything about drives it didn't test.** Results are about this drive, on this computer, today.
