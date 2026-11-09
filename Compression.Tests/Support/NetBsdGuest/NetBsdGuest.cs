#pragma warning disable CS1591
using System.Text.RegularExpressions;

namespace Compression.Tests.Support;

/// <summary>
/// Runs commands in a NetBSD guest (official live image under qemu, see
/// <c>netbsd-guest.py</c>) for the file systems only NetBSD's own tools write and read.
/// Opt-in: set <c>CWB_NETBSD_LIVE</c> to the Linux path of an unpacked
/// <c>NetBSD-*-amd64-live.img</c>; tests <see cref="Assert.Ignore(string)"/> otherwise.
/// </summary>
internal static class NetBsdGuest {
  public static string? LiveImage => Environment.GetEnvironmentVariable("CWB_NETBSD_LIVE");

  public static void Require() {
    if (string.IsNullOrEmpty(LiveImage)) Assert.Ignore("CWB_NETBSD_LIVE (path to a NetBSD amd64 live image inside WSL/Linux) is not set.");
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("qemu-system-x86_64") || !FsInteropToolbox.WslHasTool("python3"))
      Assert.Ignore("qemu-system-x86_64 and python3 are needed to boot the NetBSD guest.");
  }

  /// <summary>
  /// Boots the guest with a zeroed <paramref name="dataMegabytes"/> MiB second disk (or the
  /// given bytes) at <c>/dev/rwd1d</c>, runs <paramref name="commands"/> and returns the
  /// console output and the data disk's final bytes.
  /// </summary>
  public static (string Output, byte[] Disk) Run(IEnumerable<string> commands, int dataMegabytes, byte[]? initialDisk = null) {
    Require();
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_nbsd_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var data = Path.Combine(dir, "data.img");
      var disk = new byte[dataMegabytes * 1024L * 1024L];
      initialDisk?.CopyTo(disk, 0);
      File.WriteAllBytes(data, disk);
      var cmd = Path.Combine(dir, "cmd.txt");
      File.WriteAllText(cmd, string.Join('\n', commands) + "\n");
      var script = Path.Combine(TestContext.CurrentContext.TestDirectory, "Support", "NetBsdGuest", "netbsd-guest.py");
      var r = FsInteropToolbox.RunWsl($"timeout 1500 python3 {Q(script)} {Q2(LiveImage!)} {Q(data)} {Q(cmd)} 2>&1");
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
