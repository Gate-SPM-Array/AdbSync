# AdbSync v0.1.8

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
  logs\            one folder per session + SystemLOG\ (see section 6)
  platform-tools\  adb.exe and DLLs (downloaded from Google)
```

If the folder is not writable (e.g. `C:\Program Files`) the program falls back to `%LOCALAPPDATA%\AdbSync` and says so in the log.
The adb authorization key stays in its standard Windows location (`%USERPROFILE%\.android`).
To move or delete the program folder while adb is running: menu *adb… → Arresta il server adb* (stop the adb server).

> The `AdbSync_v0.1.8_win-x64.zip` package requires the **.NET 10 Desktop Runtime x64**, available at https://dotnet.microsoft.com/download/dotnet/10.0

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
   - **verify the backup** (see below);
   - **close**.

   **Verify the backup** (also in *Strumenti → Verifica il backup*; CLI `adbsync-cli verify`): for every file present on both sides with the same name, size and date, the hash computed on the phone is compared with the PC one, using the acquired listing (no new scan). *Quick* reads the first and last 64 KB of each file; *Full* computes the MD5 of the whole file. Nothing is modified: files that differ are listed in the Errors tab and in `verifica_backup_<date>.csv` (with both hashes), and are copied again from the phone only if you confirm. The result also says whether the backup is complete: phone files missing on the PC (or with a different size or date) are counted, because they cannot be verified. A passed *quick* check only says that start and end of each file match.

   Alternatively, repeat **Compare → Sync**: only the missing files are copied.

## 3. How it works (why it is reliable)

- **Fast, verified scan**: a single command on the phone returns the whole tree, files and folders, with size and nanosecond date. Data arrives in batches of at most 500 paths (64 KB), so Android's argument-length limit can never be exceeded.
  - Every batch declares how many entries it contains, and the program checks that the received total matches.
  - Any scan error, or a count that does not add up, **stops the compare** with an explicit message: a partial listing never becomes a copy plan.
  - The only exceptions are "permission denied" entries, typical of `Android/data` and `Android/obb`. They are listed in the Errors tab, and the final summary says "COMPLETATO CON AVVISI" (completed with warnings) instead of "COMPLETATO".
  - The same applies to files that are listed but that Android does not allow to read during the copy: they are counted as *not copiable* (not as errors), are not retried, and *Retry* does not propose them again while the program stays open.
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
- **Stalls**: if the phone sends no byte for *phone stall timeout* seconds (minimum 1, default 30), the transfer is closed. The file **goes back to the end of the queue, without waiting**, and is retried after the others.
  - A low timeout with many retries usually performs better: a "stuck" file never blocks a transfer slot for long.
  - After many consecutive stalls with no file completed (at least 10, or 4 × parallel transfers), the program checks that the phone responds. If the problem persists for 5 checks in a row, the job stops with a clear message instead of looping forever.
  - The final summary and the log report: number of stalls, time lost, files that succeeded after several attempts, and files that **always stalled** (candidates for exclusion).
- **Slow destination disk** (separate timeout): time spent writing to the PC disk never counts as a phone stall. Some SSDs (e.g. DRAM-less QLC drives that are nearly full) stop writes for tens of seconds without being faulty.
  - While a write is stuck, the status shows "Slow destination disk…" and the copy waits.
  - Only a single write stuck longer than the *PC disk write timeout* (minimum 30 s, default 120 s, in *Strumenti* → *Timeout scrittura su disco (PC)*; CLI `--disk-stall`) closes the transfer; the file goes back to the queue and the phone is not blamed.
- **Resume without rewriting**: after a stall or a connection error the partial file is kept. On the next attempt the phone sends the file again from the start (the ADB protocol cannot start halfway), but the bytes already on the PC disk, minus a 1 MiB safety margin, are not written again. The partial is reused only if size and date on the phone are unchanged; a corrupted copy (MD5 mismatch) or a local disk error always restarts from zero. Partials of files that never completed are deleted at the end.
- **Diagnostic diary** in the log: every pause in the data flow over 0.5 s (`[diagnosi] pausa`) and every stall (`[diagnosi] stallo`) is logged with the time spent writing to disk and waiting for adb.
- **ADB transfer protocol v2** (the one `adb pull` uses): *Strumenti* → *Protocollo di trasferimento ADB v2*, CLI `--v2`. Falls back to v1 by itself if the phone does not support it.
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
- **Converted names list**: every sync with converted names writes `nomi_convertiti_<date>.txt` in the session folder with type, original phone path, PC path, outcome and the substitutions spelled out, e.g. `"?" → "？" (U+FF1F)` (folders included), so you can always trace a file back to its original name.

The *Stato* (Status) panel can be resized by dragging the splitter above it, and its size is remembered. The active-transfers list always shows all parallel transfers.

## 4. Options

| Option (UI label) | Default | Notes |
|---|---|---|
| Compare size only (*Confronta solo la dimensione*) | no | useful if the PC files were copied in the past without keeping dates |
| Ignore 1-hour differences (*Ignora differenze di 1 ora*) | yes | FAT/exFAT disks and daylight-saving changes |
| Overwrite conflicts (*Sovrascrivi i conflitti*) | no | the phone "wins" even if the PC file is newer |
| MD5 verification (*Verifica MD5*) | no | maximum integrity guarantee, but slower. The summary says on how many copied files the MD5 matched, and the CSV report has an MD5 column; without it, the summary says that only the size was checked |
| Parallel transfers (*Trasferimenti paralleli*) | 2 | 2 to 4 helps with many small files (e.g. WhatsApp) |
| Phone stall timeout (*Timeout stallo telefono*) | 30 s | minimum 1 s; the stuck file goes back to the end of the queue |
| PC disk write timeout (*Strumenti* menu) | 120 s | minimum 30 s; a disk write slower than this closes the transfer and requeues the file |
| Extra retries (*Tentativi extra*) | 5 | |
| Align same-size dates (*Allinea date stessa dimensione*) | No | No / Size only / Quick check / Full MD5 check |
| Diagnostic mode (*Modalità diagnostica*) | no | on every compare, saves to `diagnostica_<date>\` in the session folder the full phone listing, the PC listing, the plan with the reason for every decision, and the raw phone messages |

## 5. Command line

`adbsync-cli.exe` uses the same engine and suits scripts and scheduled tasks:

```
adbsync-cli devices
adbsync-cli compare -r /sdcard/DCIM -l "D:\Backup\Phone\DCIM" --list
adbsync-cli sync    -r /sdcard/DCIM -l "D:\Backup\Phone\DCIM" --yes --verify
adbsync-cli sync    -r /sdcard -l "D:\Backup\Phone" --parallel 4 --stall 5 --retries 20 --align-dates quick
adbsync-cli verify  -r /sdcard/DCIM -l "D:\Backup\Phone\DCIM" --quick
adbsync-cli --help
```

Exit codes: `0` everything done · `1` errors on some files (or, for `verify`, files different from the phone or missing on the PC) · `2` aborted or incomplete scan · `3` usage error. `--diagnostic` writes the full listings; `--align-dates none|size|quick|full` sets date alignment.

## 6. Logs and reports

- **One folder per session.** A session starts when the program is open and a phone is connected, and lasts while the program stays open with the same phone (a disconnection during the copy does not start a new one). Every `adbsync-cli` run is a session too.
  ```
  logs\
    2026-10-16_14-30-05_Xiaomi-2201117TG_Telefono-di-Mario\
      sessione.log                     everything that happened in the session
      report_YYYYMMDD_hhmmss.csv       one per sync, opens in Excel, outcome of each file
      nomi_convertiti_YYYYMMDD_hhmmss.txt
      diagnostica_YYYYMMDD_hhmmss\     diagnostic mode only
    SystemLOG\
      system_YYYY-MM.log               one file per month
  ```
  The folder name is date and time (so the folders sort chronologically), manufacturer and model, and the name given to the phone in Android settings (or its Bluetooth name). Characters not valid on Windows become `-`. The serial number is never used, except its last 4 characters when two phones of the same model are connected.
- **SystemLOG**: what is not part of a copy session — program start and close (version, Windows, .NET, language, migrations), adb (path, server, errors, unauthorized phone), phones detected and disconnected, which session folder was opened, unexpected errors and crashes, also before the window appears. The GUI and the command line can write to it at the same time; each line says which one (`[GUI]`, `[CLI]`).
- **Logs for an issue report**: *Aiuto → Prepara log per segnalazione* creates `logs\segnalazione_<date>.zip` with the current session log and the SystemLOG, without reports or file lists. Logs never contain the full serial number of the phone (only its last 4 characters), but they do contain names and paths of your files: check the zip before attaching it to a public issue.
- **Nothing is deleted automatically.** At startup the program shows how many sessions the log folder holds and its total size (status panel and SystemLOG); delete old sessions yourself when you want. Menu *File*: *Apri cartella log e report* (log folder) and *Apri cartella della sessione corrente* (current session).
- Logs of versions before 0.1.7 stay where they are (directly in `logs\`).
- Settings: `data\settings.json` in the program folder.
- Diagnostic mode: `diagnostica_YYYYMMDD_hhmmss\` in the session folder, with `1_telefono.tsv` (phone), `2_pc.tsv`, `3_piano.tsv` (plan) and `4_messaggi_telefono.txt` (phone messages).

## 7. Known limitations of this version

- The acquired listing lives in memory while the program is open: after a restart a new Compare is needed.
- The *Quick check* cannot see differences located only in the middle of a file larger than 128 KB. For maximum assurance use *Full MD5 check*.
- Files that are smaller but newer on the PC (e.g. interrupted MTP copies) show as *Conflict* and are not copied unless *Overwrite conflicts* is enabled. Use the Conflict filter to find them.
- Phone → PC direction only, *Update* mode only (no *Mirror*, no deletions).
- Since Android 11, `Android/data` and `Android/obb` cannot be read over ADB without root: they are reported as warnings (not copiable), not as errors.
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
tests/             139 tests, including a simulated phone with faults (stalls, disconnects, truncated copies, ...)
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
