#pragma warning disable CS1591
using System.Diagnostics;
using System.Security.Cryptography;

namespace Compression.Tests.Support;

/// <summary>
/// Reads one of our filesystem images back with software that is not ours — the
/// host kernel's driver for that filesystem, 7-Zip, or the filesystem's own
/// <c>fsck</c> — and reports whether the payload survived.
/// </summary>
/// <remarks>
/// <para>Every check is optional: when the tool or the kernel module is absent,
/// the result says so rather than failing, so the same test runs on a machine
/// without them. What is never optional is honesty about which check ran —
/// <see cref="Result.Tool" /> names it.</para>
///
/// <para>Names are not compared. A filesystem is free to fold case, truncate to
/// 8.3, or append a type suffix, and several of ours do; what must survive is
/// the content. Each expected payload therefore has to appear somewhere in what
/// the third-party reader produced.</para>
/// </remarks>
internal static class ThirdPartyFsCheck {

  /// <summary>Outcome of one third-party read-back.</summary>
  /// <param name="Ran">False when no third-party reader was available.</param>
  /// <param name="Ok">Whether every expected payload came back.</param>
  /// <param name="Tool">Which reader ran, for the test's message.</param>
  /// <param name="Detail">Diagnostics when something did not line up.</param>
  internal readonly record struct Result(bool Ran, bool Ok, string Tool, string Detail) {
    public static Result NotAvailable(string why) => new(false, false, "none", why);
  }

  /// <summary>How a given format can be read by something other than us.</summary>
  /// <param name="MountType">Value for <c>mount -t</c>, or null when the kernel has no driver.</param>
  /// <param name="MountOptions">Extra mount options the driver needs.</param>
  /// <param name="SevenZip">Whether 7-Zip understands the container.</param>
  /// <param name="Fsck">Checker executable, or null.</param>
  /// <param name="FsckArgs">Arguments for a read-only check; <c>{0}</c> is the image.</param>
  /// <param name="FsckOkExitCodes">Exit codes that count as "clean".</param>
  private readonly record struct Strategy(
    string? MountType, string MountOptions, bool SevenZip,
    string? Fsck, string FsckArgs, int[] FsckOkExitCodes);

  private static readonly Dictionary<string, Strategy> Strategies = new(StringComparer.OrdinalIgnoreCase) {
    ["Adfs"] = new("adfs", "", false, null, "", []),
    ["Apfs"] = new("apfs", "", false, null, "", []),
    // A bcachefs volume written whole carries no allocation information — the
    // trees a running filesystem keeps so it can decide where to write next. The
    // format has a feature bit for exactly that, and bcachefs's own `strip-alloc`
    // produces the same shape; the consequence is that such a volume is read with
    // `norecovery`, because without it the mount stops to rebuild what is missing
    // and cannot, being read-only. Its own checker stops at the same point, so
    // mounting and reading the files back is the whole outside opinion here.
    ["BcacheFs"] = new("bcachefs", "norecovery", false, null, "", []),
    ["Btrfs"] = new("btrfs", "", false, "btrfs", "check --readonly {0}", [0]),
    ["CramFs"] = new("cramfs", "", true, "fsck.cramfs", "{0}", [0]),
    ["Efs"] = new("efs", "", false, null, "", []),
    ["Erofs"] = new("erofs", "", false, "fsck.erofs", "{0}", [0]),
    ["ExFat"] = new("exfat", "", true, "fsck.exfat", "-n {0}", [0, 1]),
    // Our ext volumes carry extents and a journal, which is ext4 — the ext2
    // driver refuses them by feature, and asking for it read "superblock is
    // corrupt" when nothing was wrong with the volume at all.
    ["Ext"] = new("ext4", "", true, "fsck.ext4", "-fn {0}", [0]),
    // "ext1" is the original extended filesystem — magic 0x137D, not ext2's
    // 0xEF53. Linux dropped it in 2.1 and e2fsprogs never spoke it, so the only
    // outside reader that might is 7-Zip's ext handler.
    ["Ext1"] = new(null, "", true, null, "", []),
    ["F2fs"] = new("f2fs", "", false, "fsck.f2fs", "--dry-run {0}", [0]),
    ["Fat"] = new("vfat", "", true, "fsck.fat", "-n {0}", [0, 1]),
    ["FatPlus"] = new("vfat", "", true, "fsck.fat", "-n {0}", [0, 1]),
    ["Gfs2"] = new("gfs2", "lockproto=lock_nolock", false, null, "", []),
    // 7-Zip's HFS handler speaks HFS+ and refuses classic HFS, so the kernel
    // driver is the outside opinion here.
    ["Hfs"] = new("hfs", "", false, null, "", []),
    ["HfsPlus"] = new("hfsplus", "", true, "fsck.hfsplus", "-n {0}", [0]),
    ["Hpfs"] = new("hpfs", "", false, null, "", []),
    ["Iso"] = new("iso9660", "", true, null, "", []),
    ["Jfs"] = new("jfs", "", false, "fsck.jfs", "-n {0}", [0]),
    // JFS1 is the OS/2 layout, which Linux's jfs driver and fsck.jfs do not
    // read — they speak the JFS2 that shipped with AIX 5L and Linux.
    ["Jfs1"] = new(null, "", false, null, "", []),
    ["MinixFs"] = new("minix", "", false, "fsck.minix", "{0}", [0]),
    ["MinixV1"] = new("minix", "", false, "fsck.minix", "{0}", [0]),
    ["MinixV2"] = new("minix", "", false, "fsck.minix", "{0}", [0]),
    // The kernel sees only what fits a NILFS2 direct block map — six blocks —
    // so what a mount lists depends on the block size the volume was built
    // with. Mounting proves the volume is sound; the payload comparison is
    // made against our own reader instead.
    ["Nilfs2"] = new(null, "", false, null, "", []),
    ["Ntfs"] = new("ntfs3", "", true, null, "", []),
    ["Qnx4"] = new("qnx4", "", false, null, "", []),
    ["Qnx6"] = new("qnx6", "", false, null, "", []),
    ["ReiserFs"] = new("reiserfs", "", false, "reiserfsck", "--check -y -q {0}", [0]),
    ["RomFs"] = new("romfs", "", true, null, "", []),
    ["SquashFs"] = new("squashfs", "", true, null, "", []),
    ["SysV"] = new("sysv", "", false, null, "", []),
    ["TFat"] = new("vfat", "", true, "fsck.fat", "-n {0}", [0, 1]),
    ["Udf"] = new("udf", "", true, null, "", []),
    ["Ufs"] = new("ufs", "ufstype=44bsd", false, null, "", []),
    ["Vdfs"] = new(null, "", false, null, "", []),
    // The kernel's freevxfs driver is read-only and has no checker, so mounting
    // and reading the files back is the whole outside opinion — and the one
    // that matters, since it is the driver a volume has to satisfy.
    ["VxFs"] = new("vxfs", "", false, null, "", []),
    ["Xenix"] = new("sysv", "", false, null, "", []),
    ["Xfs"] = new("xfs", "", false, "xfs_repair", "-n {0}", [0]),
  };

  /// <summary>True when this format has any third-party reader configured.</summary>
  public static bool IsSupported(string formatId) => Strategies.ContainsKey(formatId);

  /// <summary>
  /// Reads <paramref name="imagePath" /> with the best third-party reader for
  /// <paramref name="formatId" /> and checks that every payload in
  /// <paramref name="expected" /> comes back.
  /// </summary>
  public static Result ReadBack(string formatId, string imagePath, IReadOnlyList<byte[]> expected) {
    ArgumentNullException.ThrowIfNull(formatId);
    ArgumentNullException.ThrowIfNull(imagePath);
    ArgumentNullException.ThrowIfNull(expected);
    if (!Strategies.TryGetValue(formatId, out var strategy))
      return Result.NotAvailable($"{formatId}: no third-party reader is configured for this format.");

    if (strategy.MountType != null && CanMount) {
      var mounted = TryMountAndCollect(strategy, imagePath);
      if (mounted.Ran)
        return Compare(mounted, expected, $"mount -t {strategy.MountType}");
    }

    if (strategy.SevenZip && SevenZip != null) {
      var extracted = TrySevenZipAndCollect(imagePath);
      if (extracted.Ran)
        return Compare(extracted, expected, "7z");
    }

    return Result.NotAvailable(
      $"{formatId}: neither the kernel driver nor 7-Zip could open the image here.");
  }

  /// <summary>
  /// Runs the format's own checker, when there is one. A missing checker is
  /// reported as "did not run" rather than as a failure.
  /// </summary>
  public static Result Fsck(string formatId, string imagePath) {
    ArgumentNullException.ThrowIfNull(formatId);
    ArgumentNullException.ThrowIfNull(imagePath);
    if (!Strategies.TryGetValue(formatId, out var strategy) || strategy.Fsck == null)
      return Result.NotAvailable($"{formatId}: no checker configured.");

    var tool = Which(strategy.Fsck);
    if (tool == null)
      return Result.NotAvailable($"{strategy.Fsck} is not installed.");

    var (stdout, stderr, exit) = Run(tool, string.Format(strategy.FsckArgs, Quote(imagePath)));
    var output = stdout + stderr;
    if (!strategy.FsckOkExitCodes.Contains(exit))
      return new Result(true, false, strategy.Fsck, $"exit {exit}: {Truncate(output)}");

    // A checker can exit zero and still say the volume is wrong. Two of ours
    // did for a long time — a FAT marker written into the unclean-unmount byte
    // had fsck.fat announce possible corruption, and an unformatted JFS
    // journal had fsck.jfs fail to replay it — and nothing noticed, because
    // nothing read past the exit code.
    var complaint = Complaints.FirstOrDefault(
      c => output.Contains(c, StringComparison.OrdinalIgnoreCase));
    return complaint == null
      ? new Result(true, true, strategy.Fsck, "")
      : new Result(true, false, strategy.Fsck,
        $"exit {exit} but said \"{complaint}\": {Truncate(output)}");
  }

  /// <summary>
  /// Things a checker says about a volume that is wrong, even when it exits
  /// zero. Each is a phrase about the image, not about the host or the run.
  /// </summary>
  private static readonly string[] Complaints = [
    "Dirty bit is set",
    "logredo failed",
    "not properly unmounted",
    "FILE SYSTEM WAS MODIFIED",
    "UNEXPECTED INCONSISTENCY",
  ];

  // Two phrases that read like complaints and are not. reiserfsck ends a clean
  // run with "No corruptions found", so the bare word "corrupt" failed every
  // volume it was happy with; and fsck.f2fs prints "[FSCK] fixing SIT types"
  // as a heading before the check runs, on a volume mkfs.f2fs made as readily
  // as on one of ours. Both were checked against a reference image before
  // being left out.

  // ── readers ───────────────────────────────────────────────────────────────

  private readonly record struct Collected(bool Ran, List<byte[]> Payloads, string Detail);

  private static Collected TryMountAndCollect(Strategy strategy, string imagePath) {
    var mountPoint = Path.Combine(Path.GetTempPath(), "cwb_tp_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(mountPoint);
    try {
      var options = "loop,ro,noatime";
      if (strategy.MountOptions.Length > 0) options += "," + strategy.MountOptions;
      // Ownership options let the invoking user read what was mounted; drivers
      // that do not know them reject the mount, so they are only added for the
      // ones that do.
      if (strategy.MountType is "adfs" or "vfat" or "exfat" or "hfs" or "hfsplus" or "iso9660"
          or "udf" or "hpfs" or "ntfs3")
        options += $",uid={Uid},gid={Gid}";

      var (_, stderr, exit) = Sudo($"mount -t {strategy.MountType} -o {options} {Quote(imagePath)} {Quote(mountPoint)}");
      if (exit != 0)
        return new Collected(false, [], $"mount failed ({exit}): {Truncate(stderr)}");

      try {
        var payloads = new List<byte[]>();
        foreach (var file in EnumerateReadable(mountPoint))
          payloads.Add(file);
        return new Collected(true, payloads, "");
      } finally {
        Sudo($"umount {Quote(mountPoint)}");
      }
    } catch (Exception ex) {
      return new Collected(false, [], ex.Message);
    } finally {
      try { Directory.Delete(mountPoint, true); } catch { /* the mount point may be gone already */ }
    }
  }

  /// <summary>
  /// Reads every regular file under a mount. Files the driver will not hand
  /// over — permissions, unsupported attributes — are skipped rather than
  /// aborting the walk, so one odd entry cannot hide the rest.
  /// </summary>
  private static IEnumerable<byte[]> EnumerateReadable(string root) {
    var pending = new Stack<string>();
    pending.Push(root);
    while (pending.Count > 0) {
      var dir = pending.Pop();
      string[] entries;
      try {
        entries = Directory.GetFileSystemEntries(dir);
      } catch {
        continue;
      }
      foreach (var entry in entries) {
        if (Directory.Exists(entry)) {
          pending.Push(entry);
          continue;
        }
        byte[] bytes;
        try {
          bytes = File.ReadAllBytes(entry);
        } catch {
          continue;
        }
        yield return bytes;
      }
    }
  }

  private static Collected TrySevenZipAndCollect(string imagePath) {
    var outDir = Path.Combine(Path.GetTempPath(), "cwb_7z_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      var (_, stderr, exit) = Run(SevenZip!, $"x -y -o{Quote(outDir)} {Quote(imagePath)}");
      if (exit != 0)
        return new Collected(false, [], $"7z exit {exit}: {Truncate(stderr)}");
      var payloads = new List<byte[]>();
      foreach (var file in EnumerateReadable(outDir))
        payloads.Add(file);
      return new Collected(true, payloads, "");
    } catch (Exception ex) {
      return new Collected(false, [], ex.Message);
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }

  private static Result Compare(Collected collected, IReadOnlyList<byte[]> expected, string tool) {
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var payload in collected.Payloads)
      seen.Add(Digest(payload));

    var missing = new List<int>();
    for (var i = 0; i < expected.Count; ++i)
      if (!seen.Contains(Digest(expected[i])))
        missing.Add(i);

    return new Result(true, missing.Count == 0, tool,
      missing.Count == 0
        ? ""
        : $"{missing.Count} of {expected.Count} payloads did not come back " +
          $"(read {collected.Payloads.Count} files of " +
          $"{string.Join("/", collected.Payloads.Select(p => p.Length))} bytes; " +
          $"missing {string.Join("/", missing.Select(i => expected[i].Length))} bytes)" +
          (collected.Detail.Length > 0 ? " — " + collected.Detail : ""));
  }

  private static string Digest(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

  // ── process plumbing ──────────────────────────────────────────────────────

  private static readonly string? SevenZip = Which("7z") ?? Which("7zz");
  private static readonly int Uid = GetId("-u");
  private static readonly int Gid = GetId("-g");

  /// <summary>
  /// Whether images can be mounted here at all: mounting needs root, and these
  /// tests never prompt for a password.
  /// </summary>
  private static readonly bool CanMount = OperatingSystem.IsLinux() && HasPasswordlessSudo();

  private static bool HasPasswordlessSudo() {
    try {
      var (_, _, exit) = Run("sudo", "-n true");
      return exit == 0;
    } catch {
      return false;
    }
  }

  private static int GetId(string flag) {
    try {
      var (stdout, _, exit) = Run("id", flag);
      return exit == 0 && int.TryParse(stdout.Trim(), out var value) ? value : 0;
    } catch {
      return 0;
    }
  }

  private static (string StdOut, string StdErr, int Exit) Sudo(string command)
    => Run("sudo", "-n " + command);

  private static (string StdOut, string StdErr, int Exit) Run(string file, string arguments) {
    var psi = new ProcessStartInfo(file, arguments) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var process = Process.Start(psi);
    if (process == null) return ("", "could not start " + file, -1);
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    if (!process.WaitForExit(120_000)) {
      try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
      return (stdout, stderr + " (timed out)", -1);
    }
    return (stdout, stderr, process.ExitCode);
  }

  private static string? Which(string tool) {
    try {
      var (stdout, _, exit) = Run("/usr/bin/which", tool);
      var path = stdout.Trim();
      return exit == 0 && path.Length > 0 ? path : null;
    } catch {
      return null;
    }
  }

  private static string Quote(string path) => path;

  private static string Truncate(string text) {
    var trimmed = text.Replace('\n', ' ').Trim();
    return trimmed.Length <= 200 ? trimmed : trimmed[..200];
  }
}
