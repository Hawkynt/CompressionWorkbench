using System.Text;
using Compression.Tests.Csc;

namespace Compression.Tests.Cvf;

/// <summary>
/// Deterministic cluster contents the checked-in CVF reference vectors were compressed from (see
/// ReferenceVectors/README.md). Only the compressed side is checked in, so these generators must never
/// change. Text, code, table and small-alphabet material is shared with <see cref="CscSamples"/>.
/// </summary>
public static class CvfSamples {
  /// <summary>Names of every sample, as used in the vector file names (<c>&lt;sample&gt;.&lt;method&gt;.bin</c>).</summary>
  public static readonly string[] Names = ["text-8k", "x86-8k", "table-8k", "alphabet-8k", "dir-8k", "text-32k"];

  /// <summary>The bytes of the named sample.</summary>
  public static byte[] Get(string name) => name switch {
    "text-8k" => CscSamples.Text(8192)[..8192],
    "x86-8k" => CscSamples.X86(8192)[..8192],
    "table-8k" => CscSamples.Table(8192)[..8192],
    "alphabet-8k" => CscSamples.SmallAlphabet(8192)[..8192],
    "dir-8k" => Directory(8192),
    "text-32k" => CscSamples.Text(32768)[..32768],
    _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
  };

  /// <summary>
  /// A FAT directory cluster: 32-byte 8.3 entries (space-padded names, attribute, time/date, first
  /// cluster, size) followed by zero-filled free entries — long zero runs and repeated short strings.
  /// </summary>
  public static byte[] Directory(int length) {
    var random = new Random(5);
    var result = new byte[length];
    var used = length / 32 / 3;
    for (var i = 0; i < used; ++i) {
      var entry = result.AsSpan(i * 32, 32);
      var stem = $"FILE{i:D4}".PadRight(8);
      var ext = (i % 3) switch { 0 => "TXT", 1 => "EXE", _ => "DAT" };
      Encoding.ASCII.GetBytes(stem + ext, entry);
      entry[11] = (byte)(i % 7 == 0 ? 0x10 : 0x20);
      BitConverter.TryWriteBytes(entry[22..], (ushort)(0x6000 + random.Next(0x800)));
      BitConverter.TryWriteBytes(entry[24..], (ushort)0x2A21);
      BitConverter.TryWriteBytes(entry[26..], (ushort)(2 + i * 3));
      BitConverter.TryWriteBytes(entry[28..], random.Next(1 << 20));
    }
    return result;
  }
}
