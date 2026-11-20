using System.Text;

namespace Compression.Tests.Support;

/// <summary>
/// Boots a real Microsoft DOS from the owner's own media under <c>qemu-system-i386</c> (inside WSL on
/// Windows, natively on Linux) with a FAT16 host hard disk C: that carries a compressed volume file as
/// <c>C:\DBLSPACE.000</c> or <c>C:\DRVSPACE.000</c>, runs a batch file in the guest, and hands back
/// what it wrote: <c>LOG.TXT</c>, the files it copied to <c>A:\OUT</c>, the text screen at the end, and
/// the volume files as the guest left them. The harness itself is <c>MsDos/msdos-guest.sh</c>. Nothing is
/// downloaded — every image is opt-in through an environment variable (see <c>MsDosImageStaging.md</c>);
/// a test skips cleanly when its image is missing.
/// </summary>
internal static class MsDosGuest {

  /// <summary>A DOS the harness can boot, and the compression product it runs.</summary>
  /// <param name="Name">For messages.</param>
  /// <param name="Kind">The harness's boot recipe: <c>dos</c>, <c>dos7</c> or <c>win98</c>.</param>
  /// <param name="Product"><c>DBLSPACE</c> or <c>DRVSPACE</c>: the tool, its INI file and the volume file name.</param>
  /// <param name="Variables">Environment variables naming the media, the first one set wins.</param>
  /// <param name="DriverVariable">For <c>dos7</c>: the variable naming the DRVSPACE.BIN to load.</param>
  public sealed record DosSystem(string Name, string Kind, string Product, string[] Variables, string? DriverVariable = null) {
    public override string ToString() => this.Name;
  }

  /// <summary>MS-DOS 6.0 (English): install disk 1; DBLSPACE.EX_ is taken from the disks beside it.</summary>
  public static readonly DosSystem Dos60 = new("MS-DOS 6.0", "dos", "DBLSPACE", ["CWB_MSDOS_DBLSPACE_BOOT_IMG"]);

  /// <summary>MS-DOS 6.0 (German).</summary>
  public static readonly DosSystem Dos60De = new("MS-DOS 6.0 (German)", "dos", "DBLSPACE", ["CWB_MSDOS_DBLSPACE_DE_BOOT_IMG"]);

  /// <summary>MS-DOS 6.22 (English): install disk 1; DRVSPACE.EXE is taken from the disks beside it.</summary>
  public static readonly DosSystem Dos622 = new("MS-DOS 6.22", "dos", "DRVSPACE", ["CWB_MSDOS_DRVSPACE_BOOT_IMG", "CWB_MSDOS622_BOOT_IMG"]);

  /// <summary>MS-DOS 6.22 (German).</summary>
  public static readonly DosSystem Dos622De = new("MS-DOS 6.22 (German)", "dos", "DRVSPACE", ["CWB_MSDOS_DRVSPACE_DE_BOOT_IMG"]);

  /// <summary>MS-DOS 7.1: a bootable floppy plus its DRVSPACE.BIN, which mounts <c>C:\DRVSPACE.000</c> at boot.</summary>
  public static readonly DosSystem Dos71 = new("MS-DOS 7.1", "dos7", "DRVSPACE", ["CWB_MSDOS71_BOOT_IMG"], "CWB_MSDOS71_DRVSPACE_BIN");

  /// <summary>Windows 98 SE's real-mode DOS: the CD image; boot floppy, DRVSPACE.BIN and SCANDISK come from it.</summary>
  public static readonly DosSystem Win98 = new("Windows 98 SE", "win98", "DRVSPACE", ["CWB_WIN98_ISO"]);

  /// <summary>What a guest run left behind.</summary>
  public sealed record Run(string Log, string Screen, string OutDir, string ResultDir) {
    /// <summary>A file the batch copied to <c>A:\OUT</c>, or null.</summary>
    public byte[]? Out(string name) {
      var p = Path.Combine(this.OutDir, name);
      return File.Exists(p) ? File.ReadAllBytes(p) : null;
    }

    /// <summary>A volume file as the guest left it on the host disk (e.g. <c>DRVSPACE.000</c>, <c>DBLSPACE.001</c>), or null.</summary>
    public byte[]? Volume(string name) {
      var p = Path.Combine(this.ResultDir, name);
      return File.Exists(p) ? File.ReadAllBytes(p) : null;
    }
  }

  /// <summary>Skips unless <paramref name="system"/>'s media and the guest tooling are present.</summary>
  public static (string Boot, string ToolSource) Require(DosSystem system) {
    var boot = system.Variables.Select(Env).FirstOrDefault(v => v is not null);
    if (boot is null)
      Assert.Ignore($"Set {system.Variables[0]} for the {system.Name} gate. See Compression.Tests/Support/MsDosImageStaging.md.");
    var tool = "-";
    if (system.DriverVariable is not null)
      tool = Env(system.DriverVariable) ?? throw new IgnoreException($"Set {system.DriverVariable} to the {system.Name} DRVSPACE.BIN.");
    RequireTools(system.Kind == "win98");
    return (boot!, tool);
  }

  /// <summary>
  /// Boots <paramref name="system"/> with <paramref name="volume"/> (or none) as <c>C:\&lt;product&gt;.000</c> on
  /// a host disk of <paramref name="hostCylinders"/> × 16 heads × 63 sectors, the payload files in
  /// <c>A:\IN</c>, and runs <paramref name="batch"/> as AUTOEXEC.BAT (<c>%TOOL%</c> in it becomes the
  /// product's tool on the floppy). The batch must end by appending <c>DONE</c> to <c>A:\LOG.TXT</c>; the
  /// guest is stopped after <paramref name="maxWaitSeconds"/> otherwise.
  /// </summary>
  public static Run Boot(DosSystem system, string workDir, byte[]? volume, string batch,
      IReadOnlyList<(string Name, byte[] Data)>? payload = null, int hostCylinders = 33, int maxWaitSeconds = 240) {
    var (boot, tool) = Require(system);
    Directory.CreateDirectory(workDir);
    var script = Path.Combine(workDir, "msdos-guest.sh");
    File.WriteAllText(script, GuestScript.Value);
    var batchFile = Path.Combine(workDir, "AUTOEXEC.BAT");
    var text = batch.Replace("%TOOL%", $@"A:\{system.Product}.EXE").Replace("\r\n", "\n").TrimEnd('\n').Replace("\n", "\r\n") + "\r\n";
    File.WriteAllText(batchFile, text, Encoding.ASCII);
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
      $"bash {Q(script)} {system.Kind} {Q(boot)} {(tool == "-" ? "-" : Q(tool))} {system.Product} {(cvf == "-" ? "-" : Q(cvf))} "
      + $"{Q(result)} {Q(batchFile)} {(pay == "-" ? "-" : Q(pay))} {hostCylinders} {maxWaitSeconds} 2>&1");
    var log = ReadText(Path.Combine(result, "log.txt"));
    var screen = ReadText(Path.Combine(result, "screen.txt"));
    if (log.Length == 0)
      Assert.Ignore($"The {system.Name} guest wrote no log (media unusable?). Harness output: {r.StdOut.Trim()} {r.StdErr.Trim()}");
    return new Run(log, screen, Path.Combine(result, "OUT"), result);
  }

  private static readonly Lazy<string> GuestScript = new(() => {
    using var s = typeof(MsDosGuest).Assembly.GetManifestResourceStream("MsDos.msdos-guest.sh")
      ?? throw new InvalidOperationException("embedded msdos-guest.sh missing");
    using var reader = new StreamReader(s);
    return reader.ReadToEnd().Replace("\r\n", "\n");
  });

  private static string ReadText(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.Latin1).Replace("\r", "") : "";

  private static string Q(string path) => OperatingSystem.IsWindows() ? FsInteropToolbox.WinToWsl(path) : $"'{Path.GetFullPath(path)}'";

  private static string? Env(string name) {
    var v = Environment.GetEnvironmentVariable(name);
    return !string.IsNullOrWhiteSpace(v) && File.Exists(v) ? v : null;
  }

  private static void RequireTools(bool cd) {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("The MS-DOS guest needs Linux or WSL.");
    var tools = new List<(string, string)> { ("qemu-system-i386", "qemu-system-x86"), ("mcopy", "mtools"), ("sfdisk", "fdisk"), ("python3", "python3") };
    if (cd) tools.AddRange([("geteltorito", "genisoimage"), ("7z", "p7zip-full"), ("cabextract", "cabextract")]);
    foreach (var (tool, package) in tools)
      if (!FsInteropToolbox.WslHasTool(tool))
        Assert.Ignore($"{tool} not found (apt install {package}).");
  }
}
