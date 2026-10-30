using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FileFormat.Par2;

/// <summary>
/// Writes a PAR 2.0 recovery set as one <c>.par2</c> file: the main packet, a file-description and
/// an input-file-slice-checksum packet per protected file, the requested recovery slices and a
/// creator packet.
/// </summary>
/// <remarks>
/// Implemented from the Parity Volume Set Specification 2.0 (Nahas, Clements, Nettle, Gallagher,
/// 2003): packets are <c>PAR2\0PKT</c>, length, MD5 of everything from the recovery set id to the end
/// of the body, recovery set id, type, body; the recovery set id is the MD5 of the main packet body;
/// a file id is the MD5 of the file's 16 KiB-MD5, length and name; recovery data is a Reed–Solomon code
/// over GF(2^16) with generator 0x1100B, input slice <c>i</c> (ordered by the main packet's sorted file
/// ids, then by position in the file) carrying the constant 2^n for the i-th exponent n that is not a
/// multiple of 3, 5, 17 or 257, and recovery slice <c>e</c> holding Σ cᵢ^e · sliceᵢ on little-endian
/// 16-bit words.
/// </remarks>
public static class Par2Writer {

  /// <summary>Default number of source slices aimed at when no slice size is given (par2cmdline's default).</summary>
  public const int DefaultSliceCount = 2000;
  /// <summary>Maximum number of input slices the 16-bit code supports.</summary>
  public const int MaxInputSlices = 32768;

  private static readonly byte[] Magic = "PAR2\0PKT"u8.ToArray();
  internal static readonly byte[] TypeMain = "PAR 2.0\0Main\0\0\0\0"u8.ToArray();
  internal static readonly byte[] TypeFileDesc = "PAR 2.0\0FileDesc"u8.ToArray();
  internal static readonly byte[] TypeIfsc = "PAR 2.0\0IFSC\0\0\0\0"u8.ToArray();
  internal static readonly byte[] TypeRecvSlic = "PAR 2.0\0RecvSlic"u8.ToArray();
  internal static readonly byte[] TypeCreator = "PAR 2.0\0Creator\0"u8.ToArray();

  /// <summary>Creator text written into every set.</summary>
  public const string Creator = "CompressionWorkbench https://github.com/Hawkynt/CompressionWorkbench";

  /// <summary>One protected file.</summary>
  public sealed record SourceFile(string Name, byte[] Data);

  /// <summary>
  /// Writes the recovery set for <paramref name="files"/>.
  /// </summary>
  /// <param name="output">Destination.</param>
  /// <param name="files">Protected files; names use '/' between directories and must be unique.</param>
  /// <param name="sliceSize">Slice size in bytes (multiple of 4), or 0 to choose one.</param>
  /// <param name="recoverySlices">Number of recovery slices (exponents 0..n-1), or -1 for 5 % of the input slices (at least one).</param>
  public static void Write(Stream output, IReadOnlyList<SourceFile> files, long sliceSize = 0, int recoverySlices = -1) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(files);
    if (files.Count == 0)
      throw new ArgumentException("PAR2: a recovery set needs at least one file.", nameof(files));
    if (files.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != files.Count)
      throw new ArgumentException("PAR2: file names must be unique.", nameof(files));
    foreach (var f in files)
      if (string.IsNullOrEmpty(f.Name) || f.Name.StartsWith('/') || f.Name.Contains('\\'))
        throw new ArgumentException($"PAR2: '{f.Name}' is not a relative '/'-separated name.", nameof(files));


    if (sliceSize == 0)
      sliceSize = ChooseSliceSize(files);
    if (sliceSize <= 0 || sliceSize % 4 != 0 || sliceSize > int.MaxValue)
      throw new ArgumentException($"PAR2: slice size {sliceSize} must be a positive multiple of 4.", nameof(sliceSize));
    var slice = (int)sliceSize;

    // Per-file description, in the main packet's order: file ids sorted as 16-byte little-endian integers.
    var described = files.Select(f => Describe(f)).ToList();
    described.Sort((a, b) => CompareLittleEndian(a.FileId, b.FileId));
    var inputSlices = described.Sum(d => SliceCount(d.File.Data.Length, slice));
    if (inputSlices > MaxInputSlices)
      throw new ArgumentException($"PAR2: {inputSlices} input slices exceed the {MaxInputSlices} the 16-bit code supports; use a larger slice size.", nameof(sliceSize));
    if (recoverySlices < 0)
      recoverySlices = Math.Max(1, (int)Math.Ceiling(inputSlices * 0.05));
    if (recoverySlices > 65535)
      throw new ArgumentException("PAR2: at most 65535 recovery slices (exponents) exist.", nameof(recoverySlices));


    var mainBody = new MemoryStream();
    WriteU64(mainBody, (ulong)slice);
    WriteU32(mainBody, (uint)described.Count);
    foreach (var d in described) mainBody.Write(d.FileId);
    var setId = MD5.HashData(mainBody.ToArray());

    output.Write(Packet(setId, TypeMain, mainBody.ToArray()));
    foreach (var d in described) {
      output.Write(Packet(setId, TypeFileDesc, FileDescBody(d)));
      output.Write(Packet(setId, TypeIfsc, IfscBody(d, slice)));
    }

    if (recoverySlices > 0 && inputSlices > 0) {
      var recovery = ComputeRecovery(described.Select(d => d.File.Data).ToList(), slice, recoverySlices);
      for (var e = 0; e < recoverySlices; ++e) {
        var body = new byte[4 + slice];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)e);
        recovery[e].CopyTo(body, 4);
        output.Write(Packet(setId, TypeRecvSlic, body));
      }
    }
    output.Write(Packet(setId, TypeCreator, Pad4(Encoding.ASCII.GetBytes(Creator))));
  }

  /// <summary>
  /// Recovery data for exponents 0..<paramref name="count"/>-1 over the files' slices (in the given
  /// order, each zero-padded to whole slices).
  /// </summary>
  public static byte[][] ComputeRecovery(IReadOnlyList<byte[]> filesInOrder, int sliceSize, int count) {
    var words = sliceSize / 2;
    var recovery = new byte[count][];
    var acc = new ushort[count][];
    for (var e = 0; e < count; ++e) acc[e] = new ushort[words];


    var exponent = 0;
    var input = new ushort[words];
    foreach (var data in filesInOrder) {
      for (long offset = 0; offset < data.Length; offset += sliceSize) {
        exponent = NextConstantExponent(exponent);
        Array.Clear(input);
        var n = (int)Math.Min(sliceSize, data.Length - offset);
        var src = data.AsSpan((int)offset, n);
        for (var w = 0; w < n / 2; ++w) input[w] = BinaryPrimitives.ReadUInt16LittleEndian(src[(2 * w)..]);
        if ((n & 1) != 0) input[n / 2] = src[n - 1];

        for (var e = 0; e < count; ++e) {
          // factor = constant^e = 2^(exponent*e)
          var logFactor = (int)((long)exponent * e % 65535);
          var target = acc[e];
          for (var w = 0; w < words; ++w) {
            var v = input[w];
            if (v != 0) target[w] ^= Gf16.Exp[Gf16.Log[v] + logFactor];
          }
        }

      }
    }

    for (var e = 0; e < count; ++e) {
      recovery[e] = new byte[sliceSize];
      for (var w = 0; w < words; ++w) BinaryPrimitives.WriteUInt16LittleEndian(recovery[e].AsSpan(2 * w), acc[e][w]);
    }
    return recovery;
  }

  /// <summary>The next exponent n (after <paramref name="previous"/>) with 2^n of order 65535.</summary>
  public static int NextConstantExponent(int previous) {
    var n = previous + 1;
    while (n % 3 == 0 || n % 5 == 0 || n % 17 == 0 || n % 257 == 0) ++n;
    return n;
  }

  private sealed record Description(SourceFile File, byte[] FileId, byte[] Md5, byte[] Md5First16K, byte[] NameBytes);

  private static Description Describe(SourceFile f) {
    var name = Encoding.UTF8.GetBytes(f.Name);
    var md5 = MD5.HashData(f.Data);
    var md5First = MD5.HashData(f.Data.AsSpan(0, Math.Min(16384, f.Data.Length)));
    var idInput = new byte[16 + 8 + name.Length];
    md5First.CopyTo(idInput, 0);
    BinaryPrimitives.WriteUInt64LittleEndian(idInput.AsSpan(16), (ulong)f.Data.LongLength);
    name.CopyTo(idInput, 24);
    return new Description(f, MD5.HashData(idInput), md5, md5First, name);
  }

  private static byte[] FileDescBody(Description d) {
    var ms = new MemoryStream();
    ms.Write(d.FileId);
    ms.Write(d.Md5);
    ms.Write(d.Md5First16K);
    WriteU64(ms, (ulong)d.File.Data.LongLength);
    ms.Write(Pad4(d.NameBytes));
    return ms.ToArray();
  }

  private static byte[] IfscBody(Description d, int slice) {
    var ms = new MemoryStream();
    ms.Write(d.FileId);
    var buffer = new byte[slice];
    for (long offset = 0; offset < d.File.Data.Length; offset += slice) {
      Array.Clear(buffer);
      var n = (int)Math.Min(slice, d.File.Data.Length - offset);
      d.File.Data.AsSpan((int)offset, n).CopyTo(buffer);
      ms.Write(MD5.HashData(buffer));
      WriteU32(ms, Compression.Core.Checksums.Crc32.Compute(buffer));
    }
    return ms.ToArray();
  }

  private static byte[] Packet(byte[] setId, byte[] type, byte[] body) {
    if (body.Length % 4 != 0) throw new InvalidOperationException("PAR2 packet body must be 4-byte aligned.");
    var packet = new byte[64 + body.Length];
    Magic.CopyTo(packet, 0);
    BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(8), (ulong)packet.Length);
    setId.CopyTo(packet, 32);
    type.CopyTo(packet, 48);
    body.CopyTo(packet, 64);
    MD5.HashData(packet.AsSpan(32)).CopyTo(packet, 16);
    return packet;
  }

  internal static long ChooseSliceSize(IReadOnlyList<SourceFile> files) {
    // Smallest multiple of 4 that keeps the input near DefaultSliceCount slices, never more than MaxInputSlices.

    var total = files.Sum(f => (long)f.Data.Length);
    var size = Math.Max(4, (total + DefaultSliceCount - 1) / DefaultSliceCount);
    size = (size + 3) & ~3L;
    while (files.Sum(f => SliceCount(f.Data.Length, size)) > MaxInputSlices) size += 4;
    return size;
  }

  private static int SliceCount(long length, long slice) => (int)((length + slice - 1) / slice);

  internal static int CompareLittleEndian(byte[] a, byte[] b) {
    for (var i = 15; i >= 0; --i)
      if (a[i] != b[i]) return a[i].CompareTo(b[i]);
    return 0;
  }

  private static byte[] Pad4(byte[] b) {
    var padded = new byte[(b.Length + 3) & ~3];
    b.CopyTo(padded, 0);
    return padded;
  }

  private static void WriteU32(Stream s, uint v) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(b, v);
    s.Write(b);
  }

  private static void WriteU64(Stream s, ulong v) {
    Span<byte> b = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(b, v);
    s.Write(b);
  }

  /// <summary>GF(2^16) with generator polynomial 0x1100B, as the specification fixes it.</summary>
  internal static class Gf16 {
    /// <summary>Antilogarithms, doubled so a sum of two logarithms needs no modulo.</summary>
    public static readonly ushort[] Exp = BuildExp();
    public static readonly int[] Log = BuildLog();

    private static ushort[] BuildExp() {
      var exp = new ushort[2 * 65535];
      var x = 1;
      for (var i = 0; i < 65535; ++i) {
        exp[i] = exp[i + 65535] = (ushort)x;
        x <<= 1;
        if ((x & 0x10000) != 0) x ^= 0x1100B;
      }
      return exp;
    }

    private static int[] BuildLog() {
      var log = new int[65536];
      for (var i = 0; i < 65535; ++i) log[Exp[i]] = i;
      return log;
    }

    public static ushort Multiply(ushort a, ushort b) => a == 0 || b == 0 ? (ushort)0 : Exp[Log[a] + Log[b]];
  }
}
