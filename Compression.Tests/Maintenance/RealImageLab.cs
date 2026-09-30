#pragma warning disable CS1591
using System.Text;

namespace Compression.Tests.Maintenance;

/// <summary>
/// Builds reference volumes with the real Linux tools and reads back everything a
/// volume says about its files, so a maintenance verb can be judged against what
/// the reference tooling produced rather than against what our own writer emits.
/// </summary>
/// <remarks>
/// <para>A self-round-trip cannot see metadata loss: our writer never produced
/// the label, the owners, the symlinks or the hidden flags in the first place, so
/// a verb that drops them reads back exactly what went in. These volumes are made
/// by <c>mkfs.*</c> and filled through a kernel driver (libguestfs' appliance), so
/// they carry what a user's volume carries.</para>
///
/// <para>The manifest is read through the same kernel driver: names, types, modes,
/// owners, sizes, modification times, link targets, link counts, extended
/// attributes, content digests, and the volume's label and UUID. FAT's DOS
/// attribute bits come from <c>mattrib</c>.</para>
/// </remarks>
internal static class RealImageLab {

  /// <summary>The seconds-since-epoch stamp every seeded entry carries (2001-02-03 04:05:06 UTC).</summary>
  public const long SeedTime = 981173106;

  public const string Label = "CWBLABEL";

  /// <summary>What a filesystem can be asked to store beyond names and bytes.</summary>
  [Flags]
  public enum Features {
    None = 0,
    Posix = 1,        // chmod / chown
    Symlink = 2,
    HardLink = 4,
    Xattr = 8,
    DosAttributes = 16,
  }

  public sealed record FsKind(string Name, string MkfsCommand, long Size, Features Features, string CheckCommand,
    string? RequiredTool = null);

  /// <summary>The filesystems the lab can build. <c>{img}</c> is substituted with the image path.</summary>
  public static readonly IReadOnlyDictionary<string, FsKind> Kinds = new Dictionary<string, FsKind>(StringComparer.OrdinalIgnoreCase) {
    ["ext4"] = new("ext4", $"mkfs.ext4 -q -F -L {Label} -U 11111111-2222-3333-4444-555555555555 {{img}}", 64L << 20,
      Features.Posix | Features.Symlink | Features.HardLink | Features.Xattr, "e2fsck -fn {img}", "mkfs.ext4"),
    ["ext2"] = new("ext2", $"mkfs.ext2 -q -F -L {Label} -U 11111111-2222-3333-4444-555555555555 {{img}}", 32L << 20,
      Features.Posix | Features.Symlink | Features.HardLink | Features.Xattr, "e2fsck -fn {img}", "mkfs.ext2"),
    ["fat16"] = new("fat16", $"mkfs.vfat -F 16 -n {Label} -i 1234ABCD {{img}}", 32L << 20,
      Features.DosAttributes, "fsck.vfat -n {img}", "mkfs.vfat"),
    ["fat32"] = new("fat32", $"mkfs.vfat -F 32 -n {Label} -i 1234ABCD {{img}}", 64L << 20,
      Features.DosAttributes, "fsck.vfat -n {img}", "mkfs.vfat"),
    ["exfat"] = new("exfat", $"mkfs.exfat -L {Label} {{img}}", 64L << 20,
      Features.None, "fsck.exfat -n {img}", "mkfs.exfat"),
    ["ntfs"] = new("ntfs", $"mkfs.ntfs -q -F -f -L {Label} {{img}}", 64L << 20,
      Features.Symlink | Features.HardLink, "ntfsfix -n {img}", "mkfs.ntfs"),
    ["xfs"] = new("xfs", $"mkfs.xfs -q -f -L {Label} -m uuid=11111111-2222-3333-4444-555555555555 {{img}}", 320L << 20,
      Features.Posix | Features.Symlink | Features.HardLink | Features.Xattr, "xfs_repair -n {img}", "mkfs.xfs"),
    ["btrfs"] = new("btrfs", $"mkfs.btrfs -q -f -L {Label} -U 11111111-2222-3333-4444-555555555555 {{img}}", 128L << 20,
      Features.Posix | Features.Symlink | Features.HardLink | Features.Xattr, "btrfs check --readonly {img}", "mkfs.btrfs"),
    ["hfsplus"] = new("hfsplus", $"mkfs.hfsplus -v {Label} {{img}}", 32L << 20,
      Features.Posix | Features.Symlink, "fsck.hfsplus -fn {img}", "mkfs.hfsplus"),
  };

  public static bool Available(FsKind kind, out string why) {
    why = "";
    if (!FsInteropToolbox.WslAvailable) { why = "No Linux shell / WSL available."; return false; }
    if (!FsInteropToolbox.WslHasTool("guestfish")) { why = "guestfish (libguestfs-tools) is not installed."; return false; }
    if (kind.RequiredTool != null && !FsInteropToolbox.WslHasTool(kind.RequiredTool)) {
      why = $"'{kind.RequiredTool}' is not installed.";
      return false;
    }
    return true;
  }

  private static string Wsl(string path) => FsInteropToolbox.WinToWsl(path).Trim('\'');

  /// <summary>
  /// Formats and fills a reference volume of <paramref name="kind" /> in
  /// <paramref name="dir" /> and returns its path.
  /// </summary>
  /// <remarks>
  /// Contents: <c>top.txt</c>, <c>docs/readme.txt</c>, <c>docs/deep/nested.bin</c>
  /// (200 000 bytes), <c>big.bin</c> (3 000 000 bytes), an empty <c>empty/</c>
  /// folder, and — where the filesystem stores them — a symlink <c>link</c>, a hard
  /// link <c>hard</c> to <c>top.txt</c>, modes, owners, an extended attribute and
  /// DOS hidden/system/read-only bits. Every seeded entry carries <see cref="SeedTime" />.
  /// </remarks>
  public static string BuildReference(FsKind kind, string dir) {
    Directory.CreateDirectory(dir);
    var img = Path.Combine(dir, kind.Name + ".img");
    var rnd = new Random(20260930);
    var top = Path.Combine(dir, "seed_top.txt");
    File.WriteAllText(top, "hello top\n");
    var readme = Path.Combine(dir, "seed_readme.txt");
    File.WriteAllText(readme, "readme body\n");
    var nested = Path.Combine(dir, "seed_nested.bin");
    var nb = new byte[200_000]; rnd.NextBytes(nb); File.WriteAllBytes(nested, nb);
    var big = Path.Combine(dir, "seed_big.bin");
    var bb = new byte[3_000_000]; rnd.NextBytes(bb); File.WriteAllBytes(big, bb);
    var hidden = Path.Combine(dir, "seed_hidden.txt");
    File.WriteAllText(hidden, "hidden\n");

    var mk = FsInteropToolbox.RunWsl(
      $"rm -f '{Wsl(img)}' && truncate -s {kind.Size} '{Wsl(img)}' && {kind.MkfsCommand.Replace("{img}", $"'{Wsl(img)}'")}");
    Assert.That(mk.ExitCode, Is.EqualTo(0), $"{kind.Name}: mkfs failed\n{mk.StdOut}\n{mk.StdErr}");

    var script = new StringBuilder();
    script.AppendLine("run");
    script.AppendLine("mount /dev/sda /");
    script.AppendLine("mkdir-p /docs/deep");
    script.AppendLine("mkdir /empty");
    script.AppendLine($"upload {Wsl(top)} /top.txt");
    script.AppendLine($"upload {Wsl(readme)} /docs/readme.txt");
    script.AppendLine($"upload {Wsl(nested)} /docs/deep/nested.bin");
    script.AppendLine($"upload {Wsl(big)} /big.bin");
    if (kind.Features.HasFlag(Features.DosAttributes)) script.AppendLine($"upload {Wsl(hidden)} /hidden.txt");
    if (kind.Features.HasFlag(Features.Symlink)) script.AppendLine("ln-s docs/readme.txt /link");
    if (kind.Features.HasFlag(Features.HardLink)) script.AppendLine("ln /top.txt /hard");
    if (kind.Features.HasFlag(Features.Posix)) {
      script.AppendLine("chmod 0640 /top.txt");
      script.AppendLine("chmod 0750 /docs");
      script.AppendLine("chmod 0700 /empty");
      script.AppendLine("chown 1234 5678 /top.txt");
      script.AppendLine("chown 1234 5678 /docs/readme.txt");
      if (kind.Features.HasFlag(Features.Symlink)) script.AppendLine("lchown 42 43 /link");
    }
    if (kind.Features.HasFlag(Features.Xattr)) script.AppendLine("setxattr user.cwb probe 5 /top.txt");
    foreach (var path in new[] { "/top.txt", "/docs/readme.txt", "/docs/deep/nested.bin", "/big.bin", "/docs/deep", "/docs", "/empty" })
      script.AppendLine($"utimens {path} {SeedTime} 0 {SeedTime} 0");
    if (kind.Features.HasFlag(Features.DosAttributes)) script.AppendLine($"utimens /hidden.txt {SeedTime} 0 {SeedTime} 0");
    script.AppendLine("umount /");
    var scriptPath = Path.Combine(dir, "populate.gf");
    File.WriteAllText(scriptPath, script.ToString().Replace("\r\n", "\n"));

    var fill = FsInteropToolbox.RunWsl($"guestfish --rw -a '{Wsl(img)}' -f '{Wsl(scriptPath)}' 2>&1 | grep -v -i kvm");
    Assert.That(fill.StdOut + fill.StdErr, Does.Not.Contain("libguestfs: error"), $"{kind.Name}: populate failed\n{fill.StdOut}\n{fill.StdErr}");

    if (kind.Features.HasFlag(Features.DosAttributes)) {
      var attr = FsInteropToolbox.RunWsl(
        $"export MTOOLS_SKIP_CHECK=1; mattrib -i '{Wsl(img)}' +r ::top.txt && mattrib -i '{Wsl(img)}' +h +s ::hidden.txt");
      Assert.That(attr.ExitCode, Is.EqualTo(0), $"{kind.Name}: mattrib failed\n{attr.StdOut}\n{attr.StdErr}");
    }
    return img;
  }

  /// <summary>
  /// Everything the volume says about itself and its files, one fact per line,
  /// in a stable order.
  /// </summary>
  public static List<string> Manifest(FsKind kind, string img) {
    // The listing runs inside the appliance; it travels there base64-encoded so no
    // layer of shell or process-argument quoting can mangle it.
    const string inner = """
      cd /sysroot
      find . -mindepth 1 \( -name lost+found -prune \) -o -printf 'entry %P|%y|%m|%U|%G|%s|%T@|%l|%n\n' | LC_ALL=C sort
      find . -type f -not -path './lost+found/*' -exec sha256sum {} + | sed 's/^/sha /' | LC_ALL=C sort -k3
      getfattr -h -d -m - -R . 2>/dev/null | grep -v '^$' | grep -v selinux | sed 's/^/xattr /'
      """;
    var b64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(inner.Replace("\r\n", "\n") + "\n"));
    var scriptPath = Path.Combine(Path.GetDirectoryName(img)!, "manifest_" + Guid.NewGuid().ToString("N")[..8] + ".sh");
    File.WriteAllText(scriptPath,
      $"guestfish --ro -a '{Wsl(img)}' run : vfs-label /dev/sda : vfs-uuid /dev/sda : mount-ro /dev/sda / : "
      + $"debug sh \"echo {b64} | base64 -d | sh\" 2>&1 | grep -v -i kvm\n");
    var cmd = $"bash '{Wsl(scriptPath)}'";
    var r = FsInteropToolbox.RunWsl(cmd);
    Assert.That(r.StdOut + r.StdErr, Does.Not.Contain("libguestfs: error"),
      $"{kind.Name}: the kernel driver could not mount the volume\n{r.StdOut}\n{r.StdErr}");
    var lines = r.StdOut.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    if (lines.Count >= 2) {
      lines[0] = "label " + lines[0];
      lines[1] = "uuid " + lines[1];
    }
    // A directory's size is the length of its own index; it moves with any edit.
    lines = lines.Select(l => l.StartsWith("entry ") && l.Split('|') is { Length: 9 } f && f[1] == "d"
      ? string.Join('|', f[0], f[1], f[2], f[3], f[4], "-", f[6], f[7], f[8])
      : l).ToList();
    if (kind.Features.HasFlag(Features.DosAttributes)) {
      var a = FsInteropToolbox.RunWsl($"export MTOOLS_SKIP_CHECK=1; mattrib -i '{Wsl(img)}' -/ ::");
      lines.AddRange(a.StdOut.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => "dosattr " + System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"\s+", " ")).OrderBy(l => l, StringComparer.Ordinal));
    }
    return lines;
  }

  /// <summary>The reference tool's own consistency check; the verdict and its output.</summary>
  public static (bool Clean, string Output) Check(FsKind kind, string img) {
    var r = FsInteropToolbox.RunWsl(kind.CheckCommand.Replace("{img}", $"'{Wsl(img)}'") + " 2>&1");
    var output = r.StdOut + r.StdErr;
    var clean = kind.Name switch {
      "ntfs" => output.Contains("processed successfully", StringComparison.OrdinalIgnoreCase)
                && !output.Contains("corrupt", StringComparison.OrdinalIgnoreCase),
      "btrfs" => r.ExitCode == 0 && !output.Contains("error", StringComparison.OrdinalIgnoreCase),
      "hfsplus" => output.Contains("appears to be OK", StringComparison.OrdinalIgnoreCase),
      _ => r.ExitCode == 0,
    };
    return (clean, output);
  }

  /// <summary>
  /// Lines present on one side only, excluding the ones the operation was meant to
  /// change (<paramref name="expectedChanges" /> are path prefixes of entry,
  /// digest and xattr lines).
  /// </summary>
  public static List<string> UnexpectedDifferences(List<string> before, List<string> after,
      IReadOnlyCollection<string> expectedChanges) {
    bool Expected(string line) {
      var path = PathOf(line);
      return path != null && expectedChanges.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal));
    }
    var removed = before.Except(after).Where(l => !Expected(l)).Select(l => "- " + l);
    var added = after.Except(before).Where(l => !Expected(l)).Select(l => "+ " + l);
    return removed.Concat(added).ToList();
  }

  private static string? PathOf(string line) {
    if (line.StartsWith("entry ")) return line[6..].Split('|')[0];
    if (line.StartsWith("sha ")) {
      var p = line.Split("  ", 2);
      return p.Length == 2 ? p[1].TrimStart('.', '/') : null;
    }
    if (line.StartsWith("dosattr ")) {
      var i = line.IndexOf("::/", StringComparison.Ordinal);
      return i < 0 ? null : line[(i + 3)..];
    }
    return null;
  }
}
