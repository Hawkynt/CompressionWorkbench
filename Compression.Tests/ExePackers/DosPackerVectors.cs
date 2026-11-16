using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace Compression.Tests.ExePackers;

/// <summary>
/// The embedded DOS packer vectors (see ExePackers/DosVectors/README.md) and the MZ view the
/// tests compare restored programs through.
/// </summary>
internal static class DosPackerVectors {
  public static byte[] Load(string name) {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"DosPackerVectors.{name}")
      ?? throw new FileNotFoundException(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>The parts of an MZ file the loader acts on.</summary>
  internal sealed record MzView(
    ushort MinAlloc, ushort MaxAlloc, ushort SS, ushort SP, ushort IP, ushort CS,
    (ushort Offset, ushort Segment)[] Relocs, byte[] Image, byte[] Overlay) {

    public int[] LinearRelocs => [.. this.Relocs.Select(r => r.Segment * 16 + r.Offset)];

    public static MzView Parse(byte[] b) {
      ushort U(int o) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
      var size = U(4) * 512 - (U(2) == 0 ? 0 : 512 - U(2));
      var start = U(8) * 16;
      var relocs = Enumerable.Range(0, U(6)).Select(i => (U(U(24) + 4 * i), U(U(24) + 4 * i + 2))).ToArray();
      return new(U(10), U(12), U(14), U(16), U(20), U(22), relocs, b[start..size], b[size..]);
    }
  }

  /// <summary>
  /// Asserts that <paramref name="restored"/> loads exactly like <paramref name="original"/>:
  /// same image, same relocation addresses in the same order, same entry and stack registers,
  /// same memory requirements and overlay.
  /// </summary>
  public static void AssertLoadsLike(byte[] original, byte[] restored, string what) {
    var o = MzView.Parse(original);
    var r = MzView.Parse(restored);
    Assert.Multiple(() => {
      Assert.That(r.Image, Is.EqualTo(o.Image), $"{what}: load image");
      Assert.That(r.LinearRelocs, Is.EqualTo(o.LinearRelocs), $"{what}: relocation addresses");
      Assert.That((r.CS, r.IP, r.SS, r.SP), Is.EqualTo((o.CS, o.IP, o.SS, o.SP)), $"{what}: CS:IP SS:SP");
      Assert.That((r.MinAlloc, r.MaxAlloc), Is.EqualTo((o.MinAlloc, o.MaxAlloc)), $"{what}: min/max allocation");
      Assert.That(r.Overlay, Is.EqualTo(o.Overlay), $"{what}: overlay");
    });
  }

  // ── live oracle (DOSBox) ────────────────────────────────────────────

  public static string? DosBox => Environment.GetEnvironmentVariable("CWB_DOSBOX") is { Length: > 0 } p && File.Exists(p) ? p : null;

  /// <summary>A DOS tool named by <c>CWB_TOOL_&lt;name&gt;</c>.</summary>
  public static string? Tool(string name) =>
    Environment.GetEnvironmentVariable("CWB_TOOL_" + name) is { Length: > 0 } p && File.Exists(p) ? p : null;

  public static void RequireDosTools(params string[] tools) {
    if (DosBox == null) Assert.Ignore("Set CWB_DOSBOX to a dosbox-x or dosbox executable.");
    foreach (var t in tools)
      if (Tool(t) == null) Assert.Ignore($"Set CWB_TOOL_{t} to the DOS executable.");
  }

  /// <summary>Runs <paramref name="lines"/> as a batch file in DOSBox with <paramref name="drive"/> as C:.</summary>
  public static void RunDos(string drive, IEnumerable<string> lines) {
    var bat = "@echo off\r\n" + string.Join("\r\n", lines) + "\r\n";
    File.WriteAllBytes(Path.Combine(drive, "GO.BAT"), Encoding.ASCII.GetBytes(bat));
    var psi = new ProcessStartInfo(DosBox!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var a in new[] { "-silent", "-nolog", "-fastlaunch", "-defaultconf", "-set", "cpu cycles=max", "-c", $"mount c \"{drive}\"", "-c", "c:", "-c", "call GO.BAT", "-c", "exit" })
      psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    if (!p.WaitForExit(300_000)) {
      try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
      Assert.Fail("DOSBox did not finish within 5 minutes.");
    }
    _ = stdout.Result + stderr.Result;
  }

  public static string TempDrive(string tag) {
    var d = Path.Combine(Path.GetTempPath(), $"cwb_{tag}_{Guid.NewGuid():N}"[..24]);
    Directory.CreateDirectory(d);
    return d;
  }

  public static string ReadText(string path) => File.Exists(path) ? File.ReadAllText(path, Encoding.Latin1) : "";
}
