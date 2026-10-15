# AdbSync v0.1.3

**English** · [Italiano](README.it.md)

**Incremental** copy of files from an Android phone to a Windows PC over **ADB** instead of the slow MTP protocol.
It compares the two folders the way FreeFileSync does and copies **only new or changed files**, with verification, automatic retries and a detailed live status.

Mode of this version: **Update, phone → PC**. New and changed files are copied; **nothing is ever deleted**, neither on the PC nor on the phone.

> **Language:** the program speaks **Italian** (built in) and **English** (`lang\en.json`). Choose it from the **🌐 Language** menu; it applies at the next start. Translations are plain JSON files anyone can add: see [Translations](#10-translations). Below, UI names are given in English with the Italian label in brackets.

---

## 1. Setup (once)

1. **On the phone**: Settings → About phone → tap "Build number" 7 times to unlock Developer options.
   Then Settings → Developer options → enable **USB debugging**.
2. Connect the phone with a USB cable. The phone asks "Allow USB debugging?": tick *Always allow from this computer* and tap **Allow**.
3. **On the PC**: extract the zip into any folder and run `AdbSync.exe`. If adb is not available, the program offers to download Google's official *platform-tools* (about 7 MB) into its `platform-tools` subfolder. You can also point it to an existing `adb.exe`.

**Portable program**: settings, logs, reports and platform-tools all live in the program folder:

```
AdbSync\
  AdbSync.exe, adbsync-cli.exe, README.md (EN), README.it.md (IT), CHANGELOG.md
  adbsync.ini      links used by the Help menu (project page, online help, issues)
  lang\en.json     English translation (add more languages here)
  data\settings.json
  logs\            logs, CSV reports, nomi_convertiti_* lists, diagnostica_* folders
  platform-tools\  adb.exe and DLLs (downloaded from Google)
```

If the folder is not writable (e.g. `C:\Program Files`) the program falls back to `%LOCALAPPDATA%\AdbSync` and says so in the log.
The adb authorization key stays in its standard Windows location (`%USERPROFILE%\.android`).
To move or delete the program folder while adb is running: menu *adb… → Arresta il server adb* (stop the adb server).

> The `AdbSync_v0.1.2_win-x64.zip` package requires the **.NET 10 Desktop Runtime x64**, available at https://dotnet.microsoft.com/download/dotnet/10.0

## 2. Usage

1. Choose the **phone**, the **phone folder** (e.g. `/sdcard/DCIM`; *Sfoglia…* / Browse navigates the phone's folders) and the **PC folder**.
2. Press **🔍 Compare** (*Confronta*). Within seconds you get the file list grouped by category:

   | Category (UI label) | Meaning | Action |
   |---|---|---|
   | New (*Nuovo*) | exists only on the phone | copied |
   | Changed (*Modificato*) | the phone has a newer version | copied (overwrites) |
   | Conflict (*Conflitto*) | the PC has a newer **and different** version | left untouched, unless *Overwrite conflicts* is enabled |
   | Date only (*Solo data diversa*) | same name and size, different date (typical after old MTP copies) | left untouched, unless *Align same-size dates* is enabled (see below) |
   | Identical (*Identico*) | same size and date | none |
   | PC only (*Solo sul PC*) | not on the phone | none (never deleted) |
   | Name collision (*Collisione nome*) | two phone files that would get the same name on Windows | skipped and reported |
   | Excluded (*Escluso*) | matched by an exclusion rule (e.g. `.thumbnails/`), on the phone or only on the PC | never touched; the Note column shows the rule |

   Right-click the list to exclude or include single files: the choice stays valid when the plan is recalculated, until the next Compare. Double-click opens the file in Explorer.

   Above the list, a summary shows **file and folder** counts for phone and PC separately, for a direct comparison with FreeFileSync (which counts folders as items).

   **Acquired list, no rescans.** After Compare, the phone and PC listings stay in memory. Changing an option (compare mode, conflicts, exclusions, date alignment) recalculates the plan from that listing in seconds, without rescanning. A rescan happens only when you change phone or folders, or press Compare again. With 1,000,000 files the listing takes about 400 MB of RAM and a recalculation takes a few seconds.
3. Press **▶ Sync** (*Sincronizza*). The *Stato* (Status) panel shows:
   - percentage and bytes copied out of the total;
   - speed and estimated time remaining;
   - files copied, permanent errors and files waiting for another attempt;
   - for each active transfer: file, percentage, speed and attempt number;
   - waiting messages, e.g. "phone not reachable: waiting for reconnection…".
4. At the end you get an **explicit outcome**:
   - **COMPLETATO** (completed) only if *all* planned files were copied and verified;
   - otherwise **TERMINATO CON ERRORI** (finished with errors), **INTERROTTO** (aborted) or **ANNULLATO** (cancelled), with the missing files and the reason in the *Errori* (Errors) tab.

   After the copy, the in-memory listing is updated with what the program wrote, and a **"further operations on the acquired list"** dialog offers:
   - **retry the N files not copied**: starts right away, no rescan;
   - **align dates** of "date only" files, with the chosen verification level, for that operation only;
   - **close**.

   Alternatively, repeat **Compare → Sync**: only the missing files are copied.

## 3. How it works (why it is reliable)

- **Fast, verified scan**: a single command on the phone returns the whole tree, files and folders, with size and nanosecond date. Data arrives in batches of at most 500 paths (64 KB), so Android's argument-length limit can never be exceeded.
  - Every batch declares how many entries it contains, and the program checks that the received total matches.
  - Any scan error, or a count that does not add up, **stops the compare** with an explicit message: a partial listing never becomes a copy plan.
  - The only exceptions are "permission denied" entries, typical of `Android/data` and `Android/obb`. They are listed in the Errors tab, and the final summary says "COMPLETATO CON AVVISI" (completed with warnings) instead of "COMPLETATO".
  - The number of entries found is counted independently and compared with those received, so even a batch killed silently on the phone is detected.
- **PC changes are protected**: right before writing, the program checks that the PC file is still as it was at compare time. If it appeared or changed in the meantime, it is not touched and is reported.
- **Links on the PC** (junctions/symlinks): never followed and never written into, so writes never leave the destination folder.
- **Transfer**: uses ADB's *sync* protocol directly through the AdvancedSharpAdbClient library. No `adb.exe` process is spawned per file.
- **Atomic copy**: each file is written as `name.adbsync-part` and becomes the final file only after the checks:
  - the received size matches the expected one;
  - with *MD5 verification* enabled, the MD5 computed on the phone matches the one computed on the PC.

  Then the dates are set. A file interrupted halfway never replaces the good one.
- **Faithful dates**:
  - files: the phone's modification date, including fractions of a second; the creation date is set equal to the modification date, because the phone's real creation date is not readable;
  - folders: the phone's modification date (and creation set equal), applied at the end of the copy;
  - identical files already on the PC: dates are realigned without copying the content again.
- **Empty folders** are recreated, so the structure matches the phone's.
- **Stalls**: if no byte arrives for *stall timeout* seconds (minimum 1, default 30), the transfer is closed. The file **goes back to the end of the queue, without waiting**, and is retried after the others.
  - A low timeout with many retries usually performs better: a "stuck" file never blocks a transfer slot for long.
  - After many consecutive stalls with no file completed (at least 10, or 4 × parallel transfers), the program checks that the phone responds. If the problem persists for 5 checks in a row, the job stops with a clear message instead of looping forever.
  - The final summary and the log report: number of stalls, time lost, files that succeeded after several attempts, and files that **always stalled** (candidates for exclusion).
- **Retries**: up to *1 + extra retries* attempts per file (default 6). After a connection error the wait grows (2, 5, 10, 20, 30 s); after a stall there is no wait. Errors that retrying cannot fix, such as a file deleted from the phone or permission denied, are reported immediately.
- **Date alignment for "date only" files** (same name, byte-identical size). Levels:
  - *No*: untouched (default);
  - *Size only*: dates aligned without reading the content;
  - *Quick check*: MD5 of the first and last 64 KB, computed on phone and PC; exact for files up to 128 KB;
  - *Full MD5 check*: reads the whole file, slower.

  On the phone, hashes are computed **in batches**: one command per up to 200 files, not one per file. If the content differs, the file is not touched, it is flagged in orange in the Errors tab and from then on shows as *Conflict*. If verification fails (unreadable file, phone not responding), dates stay as they were and the file is reported.
- **Cable unplugged or phone locked**: the copy waits and resumes by itself when the phone is back (maximum wait 5 minutes, then a clean stop).
- **Destination disk unplugged**: the copy pauses with the message "destination disk not available". It resumes by itself if the disk returns with the same drive letter; otherwise it stops cleanly after 5 minutes, without using up the files' attempts.
- **Disk space** is checked before starting. If it runs out during the copy, the job stops with a clear message.
- The **PC does not go to sleep** during the copy.
- **Names not valid on Windows** (`: ? * " < > |`, trailing dots or spaces, `CON`, `AUX`…) are saved with equivalent characters, e.g. `：` and `？`. The mapping is stable, so incremental copies keep working.
- **Default exclusions**:
  - `.thumbnails/` (thumbnails generated by Android);
  - `.trashed-*` (files in the Android 11+ trash);
  - `.pending-*` (files being written);
  - the program's own temporary files.

  They can be changed with the *Esclusioni…* (Exclusions) button. Rules apply **relative to the chosen phone folder**: choosing `/sdcard/Pictures` excludes `.thumbnails/` inside it, choosing `/sdcard/Pictures/.thumbnails` itself does not. Excluded files are counted in the compare summary and shown with the *Excluded* filter, so a "nothing to copy" result is never a mystery.
- **Converted names list**: every sync with converted names writes `logs\nomi_convertiti_<date>.txt` with type, original phone path, PC path, outcome and the substitutions spelled out, e.g. `"?" → "？" (U+FF1F)` (folders included), so you can always trace a file back to its original name.

The *Stato* (Status) panel can be resized by dragging the splitter above it, and its size is remembered. The active-transfers list always shows all parallel transfers.

## 4. Options

| Option (UI label) | Default | Notes |
|---|---|---|
| Compare size only (*Confronta solo la dimensione*) | no | useful if the PC files were copied in the past without keeping dates |
| Ignore 1-hour differences (*Ignora differenze di 1 ora*) | yes | FAT/exFAT disks and daylight-saving changes |
| Overwrite conflicts (*Sovrascrivi i conflitti*) | no | the phone "wins" even if the PC file is newer |
| MD5 verification (*Verifica MD5*) | no | maximum integrity guarantee, but slower |
| Parallel transfers (*Trasferimenti paralleli*) | 2 | 2 to 4 helps with many small files (e.g. WhatsApp) |
| Stall timeout (*Timeout stallo*) | 30 s | minimum 1 s; the stuck file goes back to the end of the queue |
| Extra retries (*Tentativi extra*) | 5 | |
| Align same-size dates (*Allinea date stessa dimensione*) | No | No / Size only / Quick check / Full MD5 check |
| Diagnostic mode (*Modalità diagnostica*) | no | on every compare, saves to `logs\diagnostica_<date>\` the full phone listing, the PC listing, the plan with the reason for every decision, and the raw phone messages |

## 5. Command line

`adbsync-cli.exe` uses the same engine and suits scripts and scheduled tasks:

```
adbsync-cli devices
adbsync-cli compare -r /sdcard/DCIM -l "D:\Backup\Phone\DCIM" --list
adbsync-cli sync    -r /sdcard/DCIM -l "D:\Backup\Phone\DCIM" --yes --verify
adbsync-cli sync    -r /sdcard -l "D:\Backup\Phone" --parallel 4 --stall 5 --retries 20 --align-dates quick
adbsync-cli --help
```

Exit codes: `0` everything done · `1` errors on some files · `2` aborted or incomplete scan · `3` usage error. `--diagnostic` writes the full listings; `--align-dates none|size|quick|full` sets date alignment.

## 6. Logs and reports

- Full log of every session: `logs\adbsync_YYYYMMDD_hhmmss.log` in the program folder.
- CSV report of every sync, opens in Excel: `report_YYYYMMDD_hhmmss.csv`, with the outcome of each file.
- Settings: `data\settings.json` in the program folder.
- Diagnostic mode: `logs\diagnostica_YYYYMMDD_hhmmss\` with `1_telefono.tsv` (phone), `2_pc.tsv`, `3_piano.tsv` (plan) and `4_messaggi_telefono.txt` (phone messages).

## 7. Known limitations of this version

- The acquired listing lives in memory while the program is open: after a restart a new Compare is needed.
- The *Quick check* cannot see differences located only in the middle of a file larger than 128 KB. For maximum assurance use *Full MD5 check*.
- Files that are smaller but newer on the PC (e.g. interrupted MTP copies) show as *Conflict* and are not copied unless *Overwrite conflicts* is enabled. Use the Conflict filter to find them.
- Phone → PC direction only, *Update* mode only (no *Mirror*, no deletions).
- Since Android 11, `Android/data` and `Android/obb` cannot be read over ADB without root: they are reported as warnings in the log.
- Without *MD5 verification*, corruption that leaves the file size unchanged cannot be detected. This is very rare, because ADB already uses a reliable protocol over USB.
- With several phones connected, you pick the one to use from the list.
- English plurals are not handled ("1 files").
- The phone's original file creation date is not readable over ADB: it is set equal to the modification date.
- If the destination disk comes back with a different drive letter, the copy cannot find it: reconnect it with the same letter, or simply redo Compare → Sync with the new path.

## 8. Code structure

```
src/AdbSync.Core   engine: scan, compare, robust copy (cross-platform)
src/AdbSync.Gui    Windows Forms user interface
src/AdbSync.Cli    command line
lang/              translation files (en.json = reference)
tests/             103 tests, including a simulated phone with faults (stalls, disconnects, truncated copies, ...)
```

Build: .NET 10 SDK → `dotnet build`. Tests: `dotnet test`.

Publish:

```
dotnet publish src/AdbSync.Gui -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## 9. License

Released under the [MIT](LICENSE) license. Third-party components: see [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## 10. Translations

Every text of the program goes through `lang\<code>.json` files next to the exe. To add a language:

1. Copy `lang\en.json` to `lang\<code>.json` (e.g. `de.json`).
2. Set `"code"`, `"language"` (name shown in the menu), `"culture"` (number/date format, e.g. `de-DE`) and `"translators"`.
3. Translate the **values** in `"strings"`. Keys are the original Italian texts and must not be changed. Keep placeholders such as `{0}` or `{1:0.0}` exactly: a translation that changes them is ignored and reported in the log.
4. Restart AdbSync and pick the language. Untranslated texts stay in Italian, and the log tells how many are missing.

Pull requests with new languages are welcome.

## 11. Credits

Developed by [Gate-SPM-Array](https://github.com/Gate-SPM-Array) with [Claude](https://claude.com) (Anthropic), which wrote the code based on the maintainer's requirements, design decisions and testing on real phones.

AdbSync is an independent project, not affiliated with FreeFileSync or AdbFileManager: the former is the functional reference for the compare logic, the latter inspired the use of ADB instead of MTP.
