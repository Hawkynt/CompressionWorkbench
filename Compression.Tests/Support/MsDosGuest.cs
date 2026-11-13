using System.Text;

namespace Compression.Tests.Support;

/// <summary>
/// Boots a real MS-DOS from the owner's own install media under <c>qemu-system-i386</c> (inside WSL on
/// Windows, natively on Linux) with a FAT16 host hard disk C: that carries a compressed volume file as
/// <c>C:\DRVSPACE.000</c>, runs a batch file in the guest, and hands back what it wrote: <c>LOG.TXT</c>,
/// the files it copied to <c>A:\OUT</c>, the text screen at the end, and the volume files as the
/// guest left them. Nothing is downloaded — every image is opt-in through environment variables (see
/// <c>MsDosImageStaging.md</c>); a test skips cleanly when they are missing.
/// </summary>
internal static class MsDosGuest {

  /// <summary>MS-DOS 6.22 install disk 1 (boots, holds <c>DRVSPACE.BIN</c> and <c>SCANDISK.EXE</c>); disk 2
  /// (<c>Disk2.img</c> beside it) provides <c>DRVSPACE.EXE</c>.</summary>
  public const string Dos622Var = "CWB_MSDOS_DRVSPACE_BOOT_IMG";

  /// <summary>Older name for <see cref="Dos622Var"/>.</summary>
  public const string Dos622LegacyVar = "CWB_MSDOS622_BOOT_IMG";

  /// <summary>A bootable MS-DOS 7.1 floppy image (the 7.1 installer's boot disk serves).</summary>
  public const string Dos71Var = "CWB_MSDOS71_BOOT_IMG";

  /// <summary>The MS-DOS 7.1 <c>DRVSPACE.BIN</c>, extracted from the same distribution.</summary>
  public const string Dos71DriverVar = "CWB_MSDOS71_DRVSPACE_BIN";

  /// <summary>What a guest run left behind.</summary>
  public sealed record Run(string Log, string Screen, string OutDir, string ResultDir) {
    /// <summary>A file the batch copied to <c>A:\OUT</c>, or null.</summary>
    public byte[]? Out(string name) {
      var p = Path.Combine(this.OutDir, name);
      return File.Exists(p) ? File.ReadAllBytes(p) : null;
    }

    /// <summary>A volume file as the guest left it on the host disk (<c>DRVSPACE.000</c>, <c>DRVSPACE.001</c>), or null.</summary>
    public byte[]? Volume(string name) {
      var p = Path.Combine(this.ResultDir, name);
      return File.Exists(p) ? File.ReadAllBytes(p) : null;
    }
  }

  /// <summary>Skips unless the MS-DOS 6.22 disks and the guest tooling are present; returns disk 1 and disk 2.</summary>
  public static (string Disk1, string Disk2) Require622() {
    var disk1 = Env(Dos622Var) ?? Env(Dos622LegacyVar);
    if (disk1 is null)
      Assert.Ignore($"Set {Dos622Var} to MS-DOS 6.22 install disk 1 (Disk1.img, with Disk2.img beside it). See Compression.Tests/Support/MsDosImageStaging.md.");
    var disk2 = Path.Combine(Path.GetDirectoryName(disk1)!, "Disk2.img");
    if (!File.Exists(disk2))
      Assert.Ignore($"MS-DOS 6.22 disk 2 (DRVSPACE.EXE) not found at {disk2}.");
    RequireTools();
    return (disk1, disk2);
  }

  /// <summary>Skips unless an MS-DOS 7.1 boot floppy, its <c>DRVSPACE.BIN</c> and the guest tooling are present.</summary>
  public static (string Boot, string Driver) Require71() {
    var boot = Env(Dos71Var);
    var driver = Env(Dos71DriverVar);
    if (boot is null || driver is null)
      Assert.Ignore($"Set {Dos71Var} to an MS-DOS 7.1 boot floppy image and {Dos71DriverVar} to its DRVSPACE.BIN. See Compression.Tests/Support/MsDosImageStaging.md.");
    RequireTools();
    return (boot, driver);
  }

  /// <summary>
  /// Boots MS-DOS 6.22 with <paramref name="volume"/> (or none) as <c>C:\DRVSPACE.000</c> on a host disk of
  /// <paramref name="hostCylinders"/> × 16 heads × 63 sectors, the payload files in <c>A:\IN</c>, and runs
  /// <paramref name="batch"/> as AUTOEXEC.BAT. The batch must end by appending <c>DONE</c> to
  /// <c>A:\LOG.TXT</c>; the guest is stopped after <paramref name="maxWaitSeconds"/> otherwise.
  /// </summary>
  public static Run Boot622(string workDir, byte[]? volume, string batch,
      IReadOnlyList<(string Name, byte[] Data)>? payload = null, int hostCylinders = 33, int maxWaitSeconds = 240) {
    var (disk1, disk2) = Require622();
    return Boot("622", disk1, disk2, workDir, volume, batch, payload, hostCylinders, maxWaitSeconds);
  }

  /// <summary>
  /// Boots MS-DOS 7.1 with <paramref name="volume"/> as <c>C:\DRVSPACE.000</c>; its DRVSPACE.BIN mounts the
  /// volume at boot as C: (the host disk moves to E:). The batch runs as AUTOEXEC.BAT, as for
  /// <see cref="Boot622"/>.
  /// </summary>
  public static Run Boot71(string workDir, byte[] volume, string batch, int maxWaitSeconds = 120) {
    var (boot, driver) = Require71();
    return Boot("71", boot, driver, workDir, volume, batch, null, 33, maxWaitSeconds);
  }

  private static Run Boot(string version, string boot, string extra, string workDir, byte[]? volume, string batch,
      IReadOnlyList<(string Name, byte[] Data)>? payload, int hostCylinders, int maxWaitSeconds) {
    Directory.CreateDirectory(workDir);
    var script = Path.Combine(workDir, "msdos-guest.sh");
    File.WriteAllText(script, GuestScript.Replace("\r\n", "\n"));
    var batchFile = Path.Combine(workDir, "AUTOEXEC.BAT");
    File.WriteAllText(batchFile, batch.Replace("\r\n", "\n").TrimEnd('\n').Replace("\n", "\r\n") + "\r\n", Encoding.ASCII);
    var cvf = "-";
    if (volume is not null) {
      cvf = Path.Combine(workDir, "volume.cvf");
      File.WriteAllBytes(cvf, volume);
    }
    var pay = "-";
    if (payload is { Count: > 0 }) {
      pay = Path.Combine(workDir, "in");
      Directory.CreateDirectory(pay);
      foreach (var (name, data) in payload) File.WriteAllBytes(Path.Combine(pay, name), data);
    }
    var result = Path.Combine(workDir, "result");
    var r = FsInteropToolbox.RunWsl(
      $"bash {Q(script)} {version} {Q(boot)} {Q(extra)} {(cvf == "-" ? "-" : Q(cvf))} {Q(result)} {Q(batchFile)} "
      + $"{(pay == "-" ? "-" : Q(pay))} {hostCylinders} {maxWaitSeconds} 2>&1");
    var log = ReadText(Path.Combine(result, "log.txt"));
    var screen = ReadText(Path.Combine(result, "screen.txt"));
    if (log.Length == 0)
      Assert.Ignore($"The MS-DOS guest wrote no log (boot image unusable?). Harness output: {r.StdOut.Trim()} {r.StdErr.Trim()}");
    return new Run(log, screen, Path.Combine(result, "OUT"), result);
  }

  private static string ReadText(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.Latin1).Replace("\r", "") : "";

  private static string Q(string path) => OperatingSystem.IsWindows() ? FsInteropToolbox.WinToWsl(path) : $"'{Path.GetFullPath(path)}'";

  private static string? Env(string name) {
    var v = Environment.GetEnvironmentVariable(name);
    return !string.IsNullOrWhiteSpace(v) && File.Exists(v) ? v : null;
  }

  private static void RequireTools() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("The MS-DOS guest needs Linux or WSL.");
    foreach (var (tool, package) in new[] { ("qemu-system-i386", "qemu-system-x86"), ("mcopy", "mtools"), ("sfdisk", "fdisk"), ("python3", "python3") })
      if (!FsInteropToolbox.WslHasTool(tool))
        Assert.Ignore($"{tool} not found (apt install {package}).");
  }

  // The guest harness. Arguments: <622|71> <boot floppy> <622: disk 2 image | 71: DRVSPACE.BIN> <volume or ->
  // <result dir> <AUTOEXEC.BAT> <payload dir or -> <host cylinders> <max wait seconds>.
  private const string GuestScript = """
    #!/bin/bash
    set -u
    VER="$1"; BOOT="$2"; EXTRA="$3"; CVF="$4"; RES="$5"; AUTO="$6"; PAY="$7"; CYL="$8"; MAXWAIT="$9"
    W=$(mktemp -d /tmp/cwbdos.XXXXXX)
    trap 'kill $QP 2>/dev/null; rm -rf "$W"' EXIT
    QP=0
    cp "$BOOT" "$W/boot.img"
    # Room on the floppy: drop installer-only files neither boot nor the drivers need.
    for f in SETUP.EXE SETUP.MSG DOSSETUP.INI COUNTRY.TX_ EGA.CP_ EGA2.CP_ EGA3.CP_ ISO.CP_ KEYBRD2.SY_ EMM386.EX_ \
             MEM.EX_ MSCDEX.EXE QBASIC.EXE README.TXT REPLACE.EX_ RESTORE.EX_ XCOPY.EX_ DEFRAG.EXE DEFRAG.HL_ \
             NETWORKS.TXT PACKING.LST FDISK.EXE DEBUG.EXE EDIT.COM KEYB.COM KEYBOARD.SYS NLSFUNC.EXE COUNTRY.SYS \
             GDISK.EXE PKUNZIP.EXE MESSAGES.EXE SUSCR.EXE BOOTPART.EXE SETUP.BAT SETUP.INF CHANGE.EXE MCD.EXE \
             XMSDSK.EXE DOSKEY.COM CTMOUSE.EXE VIDE-CDD.SYS ELTORITO.SYS LICENSE.TXT FINDCD.EXE WBAT.COM HIMEM.SYS \
             KILLER.EXE INIFILE.COM; do
      mdel -i "$W/boot.img" "::$f" 2>/dev/null
    done
    if [ "$VER" = 622 ]; then
      mcopy -n -i "$EXTRA" ::DRVSPACE.EXE "$W/DRVSPACE.EXE" || exit 3
      mcopy -o -i "$W/boot.img" "$W/DRVSPACE.EXE" ::DRVSPACE.EXE
      printf 'MaxRemovableDrives=2\r\nFirstDrive=D\r\nLastDrive=H\r\nMaxFileFragments=117\r\n' > "$W/DRVSPACE.INI"
      MEM=16
    else
      mcopy -o -i "$W/boot.img" "$EXTRA" ::DRVSPACE.BIN
      printf 'MaxRemovableDrives=2\r\nFirstDrive=D\r\nLastDrive=H\r\nMaxFileFragments=117\r\nActivateDrive=E,C0\r\n' > "$W/DRVSPACE.INI"
      printf '[Paths]\r\nWinDir=C:\\WINDOWS\r\nWinBootDir=C:\\WINDOWS\r\nHostWinBootDrv=C\r\n[Options]\r\nBootGUI=0\r\nLogo=0\r\nDrvSpace=1\r\nDblSpace=1\r\nAutoScan=0\r\n' > "$W/MSDOS.SYS"
      mattrib -i "$W/boot.img" -r -s -h ::MSDOS.SYS
      mcopy -o -i "$W/boot.img" "$W/MSDOS.SYS" ::MSDOS.SYS
      mattrib -i "$W/boot.img" +r +s +h ::MSDOS.SYS
      MEM=32
    fi
    mcopy -o -i "$W/boot.img" "$W/DRVSPACE.INI" ::DRVSPACE.INI
    printf 'FILES=30\r\nBUFFERS=20\r\nLASTDRIVE=Z\r\n' > "$W/CONFIG.SYS"
    mcopy -o -i "$W/boot.img" "$W/CONFIG.SYS" ::CONFIG.SYS
    printf '[ENVIRONMENT]\r\n   Display = Mono\r\n   Mouse = Off\r\n   ScanTimeOut = Off\r\n   NumPasses = 1\r\n   LabelCheck = Off\r\n[CUSTOM]\r\n   DriveSummary = Off\r\n   AllSummary = Off\r\n   Surface = Never\r\n   CheckHost = Never\r\n   SaveLog = Overwrite\r\n   Undo = Never\r\n' > "$W/SCANDISK.INI"
    for k in DS_Header FAT_Media Okay_Entries Bad_Chain Crosslinks Boot_Sector Invalid_MDFAT DS_Crosslinks DS_LostClust DS_Signatures Mismatch_FAT Bad_Clusters; do
      printf '   %s = Fix\r\n' $k >> "$W/SCANDISK.INI"
    done
    printf '   Bad_Entries = Delete\r\n   LostClust = Save\r\n' >> "$W/SCANDISK.INI"
    mcopy -o -i "$W/boot.img" "$W/SCANDISK.INI" ::SCANDISK.INI
    mcopy -o -i "$W/boot.img" "$AUTO" ::AUTOEXEC.BAT
    if [ "$PAY" != "-" ]; then mmd -i "$W/boot.img" ::IN; mcopy -i "$W/boot.img" "$PAY"/* ::IN/; fi
    # Host hard disk: CYL x 16 heads x 63 sectors, one FAT16 partition from sector 63.
    truncate -s $((CYL*16*63*512)) "$W/hdd.img"
    echo '63,,6,*' | sfdisk -q "$W/hdd.img" > /dev/null 2>&1
    mformat -i "$W/hdd.img@@32256" -T $((CYL*16*63-63)) -h 16 -s 63 -H 63 -v HOST ::
    if [ "$CVF" != "-" ]; then
      mcopy -i "$W/hdd.img@@32256" "$CVF" ::DRVSPACE.000
      mattrib -i "$W/hdd.img@@32256" +h +s +r ::DRVSPACE.000
    fi
    qemu-system-i386 -m $MEM -display none -no-reboot \
      -drive file="$W/boot.img",format=raw,if=floppy,index=0,cache=writethrough \
      -drive file="$W/hdd.img",format=raw,if=ide,index=0,cache=writethrough \
      -monitor unix:$W/mon,server,nowait -boot a > "$W/qemu.log" 2>&1 &
    QP=$!
    for i in $(seq 1 $((MAXWAIT/2))); do
      sleep 2
      if mtype -i "$W/boot.img" ::LOG.TXT 2>/dev/null | grep -q DONE; then sleep 2; break; fi
    done
    mkdir -p "$RES"
    # The text screen at the end (what an interactive prompt would be waiting on).
    python3 - "$W/mon" "$RES/screen.txt" <<'PY'
    import os, socket, sys, time
    s = socket.socket(socket.AF_UNIX); s.connect(sys.argv[1]); time.sleep(0.5)
    tmp = sys.argv[2] + '.bin'
    s.send(('pmemsave 0xb8000 4000 "' + tmp + '"\r\n').encode())
    for _ in range(20):
        time.sleep(0.5)
        if os.path.exists(tmp) and os.path.getsize(tmp) == 4000: break
    b = open(tmp, 'rb').read() if os.path.exists(tmp) else bytes(4000)
    rows = [''.join(chr(b[(r * 80 + c) * 2]) if 32 <= b[(r * 80 + c) * 2] < 127 else ' ' for c in range(80)).rstrip() for r in range(25)]
    open(sys.argv[2], 'w').write('\n'.join(rows) + '\n')
    PY
    kill $QP 2>/dev/null; wait $QP 2>/dev/null
    mtype -i "$W/boot.img" ::LOG.TXT > "$RES/log.txt" 2>/dev/null
    mcopy -n -s -i "$W/boot.img" ::OUT "$RES/" 2>/dev/null
    mcopy -n -i "$W/hdd.img@@32256" ::DRVSPACE.000 "$RES/DRVSPACE.000" 2>/dev/null
    mcopy -n -i "$W/hdd.img@@32256" ::DRVSPACE.001 "$RES/DRVSPACE.001" 2>/dev/null
    exit 0
    """;
}
