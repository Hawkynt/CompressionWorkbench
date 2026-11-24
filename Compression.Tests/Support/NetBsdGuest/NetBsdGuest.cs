#pragma warning disable CS1591
using System.Text.RegularExpressions;

namespace Compression.Tests.Support;

/// <summary>
/// Runs commands in a NetBSD guest (official live image under qemu, see
/// <c>netbsd-guest.py</c>) for the file systems only NetBSD's own tools write and read.
/// Opt-in: set <c>CWB_NETBSD_LIVE</c> to the Linux path of an unpacked
/// <c>NetBSD-*-amd64-live.img</c>; tests <see cref="Assert.Ignore(string)"/> otherwise.
/// Tests that need NetBSD 9 (<c>release9</c>) take <c>CWB_NETBSD9_IMAGE</c> instead, the Linux
/// path of an unpacked <c>NetBSD-9.5-amd64-bios-install.img</c>; it boots single-user, with
/// <c>/</c> made writable and a tmpfs on <c>/tmp</c> before the commands run.
/// </summary>
internal static class NetBsdGuest {
  public static string? LiveImage => Environment.GetEnvironmentVariable("CWB_NETBSD_LIVE");

  public static string? Release9Image => Environment.GetEnvironmentVariable("CWB_NETBSD9_IMAGE");

  public static void Require(bool release9 = false) {
    if (release9 && string.IsNullOrEmpty(Release9Image))
      Assert.Ignore("CWB_NETBSD9_IMAGE (path to an unpacked NetBSD-9.5-amd64-bios-install.img inside WSL/Linux) is not set.");
    if (!release9 && string.IsNullOrEmpty(LiveImage)) Assert.Ignore("CWB_NETBSD_LIVE (path to a NetBSD amd64 live image inside WSL/Linux) is not set.");
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("qemu-system-x86_64") || !FsInteropToolbox.WslHasTool("python3"))
      Assert.Ignore("qemu-system-x86_64 and python3 are needed to boot the NetBSD guest.");
  }

  /// <summary>
  /// Boots the guest with a zeroed <paramref name="dataMegabytes"/> MiB second disk (or the
  /// given bytes) at <c>/dev/rwd1d</c>, runs <paramref name="commands"/> and returns the
  /// console output and the data disk's final bytes.
  /// </summary>
  public static (string Output, byte[] Disk) Run(IEnumerable<string> commands, int dataMegabytes, byte[]? initialDisk = null, bool release9 = false) {
    Require(release9);
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_nbsd_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var data = Path.Combine(dir, "data.img");
      var disk = new byte[dataMegabytes * 1024L * 1024L];
      initialDisk?.CopyTo(disk, 0);
      File.WriteAllBytes(data, disk);
      var cmd = Path.Combine(dir, "cmd.txt");
      // the install image has no login and starts with a read-only root
      string[] prelude = release9 ? ["mount -uw / ; mount_tmpfs tmpfs /tmp"] : [];
      File.WriteAllText(cmd, string.Join('\n', prelude.Concat(commands)) + "\n");
      var script = Path.Combine(TestContext.CurrentContext.TestDirectory, "Support", "NetBsdGuest", "netbsd-guest.py");
      // its boot menu has two entries, "Drop to boot prompt" being the second
      var env = release9 ? "NB_MENU=2 NB_SINGLE=1 " : "";
      var r = FsInteropToolbox.RunWsl($"{env}timeout 1500 python3 {Q(script)} {Q2(release9 ? Release9Image! : LiveImage!)} {Q(data)} {Q(cmd)} 2>&1");
      return (r.StdOut + r.StdErr, File.ReadAllBytes(data));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  /// <summary>The integer the guest printed as <c>NAME=value</c>.</summary>
  public static long Value(string output, string name) {
    var m = Regex.Match(output, $@"(?m)^{Regex.Escape(name)}=(\d+)");
    Assert.That(m.Success, Is.True, $"guest did not report {name}:\n{output}");
    return long.Parse(m.Groups[1].Value);
  }

  private static string Q(string winPath) => FsInteropToolbox.WinToWsl(winPath);
  private static string Q2(string linuxPath) => "'" + linuxPath.Replace("'", "'\\''") + "'";
}
