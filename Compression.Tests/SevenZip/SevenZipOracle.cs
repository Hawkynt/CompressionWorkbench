using System.Diagnostics;
using System.Text;

namespace Compression.Tests.SevenZip;

/// <summary>
/// Drives the real 7-Zip command line for the 7z conformance harness: builds a deterministic
/// source tree, runs <c>7z a/t/x</c> with an argument vector (no shell quoting), and compares
/// trees byte for byte.
/// </summary>
internal static class SevenZipOracle {
  /// <summary>The 7-Zip executable the harness drives, or <see langword="null"/> when none is installed.</summary>
  public static string? ToolPath => FsInteropToolbox.SevenZipPath;

  /// <summary>Skips the calling test when 7-Zip is not installed.</summary>
  public static string Require() {
    FsInteropToolbox.Require7z();
    return ToolPath!;
  }

  /// <summary>Runs 7-Zip with <paramref name="args"/> in <paramref name="workingDirectory"/>.</summary>
  public static (int ExitCode, string Output) Run(string workingDirectory, params string[] args) {
    var psi = new ProcessStartInfo(Require()) {
      WorkingDirectory = workingDirectory,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      RedirectStandardInput = true,
      UseShellExecute = false,
      CreateNoWindow = true,
      StandardOutputEncoding = Encoding.UTF8,
      StandardErrorEncoding = Encoding.UTF8,
    };
    // -sccUTF-8 keeps unicode names intact in the listing; -bso1 -bse1 merge the streams.
    psi.ArgumentList.Add(args[0]);
    psi.ArgumentList.Add("-sccUTF-8");
    for (var i = 1; i < args.Length; ++i)
      psi.ArgumentList.Add(args[i]);

    using var process = Process.Start(psi) ?? throw new InvalidOperationException("7-Zip did not start.");
    process.StandardInput.Close();
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(600_000)) {
      try { process.Kill(); } catch (InvalidOperationException) { }
      Assert.Fail($"7-Zip timed out: {string.Join(' ', args)}");
    }
    return (process.ExitCode, stdout.Result + stderr.Result);
  }

  /// <summary>Runs 7-Zip and fails the test unless it exits 0.</summary>
  public static string RunOk(string workingDirectory, params string[] args) {
    var (exit, output) = Run(workingDirectory, args);
    Assert.That(exit, Is.EqualTo(0), $"7z {string.Join(' ', args)} exited {exit}:\n{output}");
    return output;
  }

  /// <summary>Text with plenty of repetition: the case every coder compresses.</summary>
  public static byte[] Text(int size, int seed) {
    var words = new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "lorem", "ipsum", "dolor", "sit", "amet", "\n" };
    var random = new Random(seed);
    var sb = new StringBuilder(size + 16);
    while (sb.Length < size)
      sb.Append(words[random.Next(words.Length)]).Append(' ');
    return Encoding.ASCII.GetBytes(sb.ToString(0, size));
  }

  /// <summary>Incompressible bytes.</summary>
  public static byte[] Noise(int size, int seed) {
    var data = new byte[size];
    new Random(seed).NextBytes(data);
    return data;
  }

  /// <summary>
  /// Bytes shaped like x86 code — a CALL/JMP with a small relative operand every few bytes, the
  /// pattern BCJ and BCJ2 convert — so the branch filters do real work on it.
  /// </summary>
  public static byte[] X86Like(int size, int seed) {
    var random = new Random(seed);
    var data = new byte[size];
    for (var i = 0; i < size;) {
      if (random.Next(4) == 0 && i + 5 <= size) {
        data[i] = random.Next(2) == 0 ? (byte)0xE8 : (byte)0xE9;
        var rel = random.Next(-40000, 40000);
        BitConverter.TryWriteBytes(data.AsSpan(i + 1), rel);
        i += 5;
      } else
        data[i++] = (byte)random.Next(0x40, 0x60);
    }
    return data;
  }

  /// <summary>
  /// Writes the standard conformance tree into <paramref name="root"/>: text, noise, x86-shaped
  /// code, 4-byte-aligned "RISC" words, an empty file, an empty directory, nested directories
  /// and a non-ASCII name. Returns relative path (forward slashes) to content; directories map
  /// to <see langword="null"/>.
  /// </summary>
  public static SortedDictionary<string, byte[]?> WriteStandardTree(string root) {
    var tree = new SortedDictionary<string, byte[]?>(StringComparer.Ordinal) {
      ["text.txt"] = Text(300_000, 1),
      ["noise.bin"] = Noise(70_000, 2),
      ["code/x86.bin"] = X86Like(400_000, 3),
      ["code/risc.bin"] = RiscLike(120_000, 4),
      ["empty.txt"] = [],
      ["emptydir"] = null,
      ["code"] = null,
      ["deep"] = null,
      ["deep/er"] = null,
      ["deep/er/still.txt"] = Text(5_000, 5),
      ["deep/small.txt"] = "small"u8.ToArray(),
      ["ünïcødé_日本語.txt"] = Text(9_000, 6),
    };
    Materialize(root, tree);
    return tree;
  }

  /// <summary>Big-endian and little-endian 32-bit words with branch-like opcodes in them.</summary>
  public static byte[] RiscLike(int size, int seed) {
    var random = new Random(seed);
    var data = new byte[size & ~15];
    for (var i = 0; i + 4 <= data.Length; i += 4) {
      var kind = random.Next(6);
      var word = kind switch {
        0 => 0xEB000000u | (uint)random.Next(0, 0x1000),          // ARM BL (LE)
        1 => 0x94000000u | (uint)random.Next(0, 0x1000),          // ARM64 BL
        2 => 0x0000006Fu | ((uint)random.Next(0, 0x100) << 12),   // RISC-V JAL
        3 => 0x48000001u | ((uint)random.Next(0, 0x1000) << 2),   // PowerPC bl (BE written LE here)
        _ => (uint)random.Next(),
      };
      BitConverter.TryWriteBytes(data.AsSpan(i), word);
    }
    return data;
  }

  /// <summary>Creates <paramref name="tree"/> below <paramref name="root"/>.</summary>
  public static void Materialize(string root, IReadOnlyDictionary<string, byte[]?> tree) {
    Directory.CreateDirectory(root);
    foreach (var (path, data) in tree) {
      var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
      if (data == null)
        Directory.CreateDirectory(full);
      else {
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
      }
    }
  }

  /// <summary>Reads a directory back into the same shape <see cref="WriteStandardTree"/> returns.</summary>
  public static SortedDictionary<string, byte[]?> ReadTree(string root) {
    var tree = new SortedDictionary<string, byte[]?>(StringComparer.Ordinal);
    foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
      tree[Path.GetRelativePath(root, dir).Replace('\\', '/')] = null;
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
      tree[Path.GetRelativePath(root, file).Replace('\\', '/')] = File.ReadAllBytes(file);
    return tree;
  }

  /// <summary>Asserts two trees hold the same names, kinds and bytes.</summary>
  public static void AssertSameTree(IReadOnlyDictionary<string, byte[]?> expected, IReadOnlyDictionary<string, byte[]?> actual, string context) {
    Assert.That(actual.Keys.Order(StringComparer.Ordinal), Is.EqualTo(expected.Keys.Order(StringComparer.Ordinal)), $"{context}: names differ");
    foreach (var (name, data) in expected) {
      var other = actual[name];
      if (data == null) {
        Assert.That(other, Is.Null, $"{context}: '{name}' should be a directory");
        continue;
      }
      Assert.That(other, Is.Not.Null, $"{context}: '{name}' should be a file");
      Assert.That(other!.Length, Is.EqualTo(data.Length), $"{context}: '{name}' size");
      Assert.That(other.AsSpan().SequenceEqual(data), Is.True, $"{context}: '{name}' bytes differ");
    }
  }
}
