#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Hdf4;

/// <summary>
/// Reader for the NCSA HDF4 container (predecessor of HDF5). Walks the Data
/// Descriptor (DD) linked list starting at the file prefix and collects each
/// object as a <see cref="DataDescriptor"/> — tag + reference + file offset +
/// length. Byte order is big-endian throughout.
/// </summary>
/// <remarks>
/// Format anatomy:
///   bytes 0..3     magic = <c>0x0E 0x03 0x13 0x01</c> (HDF4 signature)
///   bytes 4..      first Data Descriptor Header (DDH):
///                    <c>num_dds : u16 BE</c>
///                    <c>next_offset : u32 BE</c>   (0 = end of chain)
///                  followed by <c>num_dds × 12-byte DD</c> records:
///                    <c>tag : u16 BE</c>
///                    <c>ref : u16 BE</c>
///                    <c>offset : u32 BE</c>
///                    <c>length : u32 BE</c>
/// Older variants begin with <c>HDF\0\0\0\x0E\x02</c>; this reader accepts
/// both magics and threads a bounded linked-list walk (<see cref="MaxBlocks"/>
/// to guard against cycles).
/// </remarks>
public sealed class Hdf4Reader {

  /// <summary>Standard 4-byte HDF4 file signature.</summary>
  public static ReadOnlySpan<byte> Magic => [0x0E, 0x03, 0x13, 0x01];

  /// <summary>Older HDF 8-byte ASCII-like signature ("HDF\0\0\0\x0E\x02").</summary>
  public static ReadOnlySpan<byte> LegacyMagic =>
    [(byte)'H', (byte)'D', (byte)'F', 0x00, 0x00, 0x00, 0x0E, 0x02];

  /// <summary>One entry of a Data Descriptor block.</summary>
  public sealed record DataDescriptor(ushort Tag, ushort Reference, uint Offset, uint Length);

  /// <summary>Parsed HDF4 file — all DD entries walked from the DD linked list.</summary>
  public sealed record Hdf4File(
    string MagicKind,                // "HDF4" or "HDF-legacy"
    IReadOnlyList<DataDescriptor> DataDescriptors,
    IReadOnlyDictionary<ushort, int> TagHistogram
  );

  /// <summary>Maximum number of DDH blocks the walker will traverse before giving up.</summary>
  public const int MaxBlocks = 1024;

  /// <summary>Parses an HDF4 file from an in-memory span.</summary>
  public static Hdf4File Read(ReadOnlySpan<byte> data) {
    string magicKind;
    int firstDdhOffset;

    if (data.Length >= 4 && data[..4].SequenceEqual(Magic)) {
      magicKind = "HDF4";
      firstDdhOffset = 4;
    } else if (data.Length >= 8 && data[..8].SequenceEqual(LegacyMagic)) {
      magicKind = "HDF-legacy";
      firstDdhOffset = 8;
    } else {
      throw new InvalidDataException("hdf4: unrecognized magic (expected 0E 03 13 01 or 'HDF' legacy).");
    }

    var descriptors = new List<DataDescriptor>();
    var histogram = new Dictionary<ushort, int>();

    var offset = (uint)firstDdhOffset;
    var visited = new HashSet<uint>();
    var blocks = 0;

    while (offset != 0 && offset + 6 <= data.Length && blocks < MaxBlocks) {
      if (!visited.Add(offset)) break;                         // guard against cycles
      blocks++;

      var numDds = BinaryPrimitives.ReadUInt16BigEndian(data[(int)offset..]);
      var nextOffset = BinaryPrimitives.ReadUInt32BigEndian(data[((int)offset + 2)..]);

      var dsStart = (int)offset + 6;
      for (var i = 0; i < numDds; i++) {
        var rec = dsStart + i * 12;
        if (rec + 12 > data.Length) break;
        var tag = BinaryPrimitives.ReadUInt16BigEndian(data[rec..]);
        var refn = BinaryPrimitives.ReadUInt16BigEndian(data[(rec + 2)..]);
        var off = BinaryPrimitives.ReadUInt32BigEndian(data[(rec + 4)..]);
        var len = BinaryPrimitives.ReadUInt32BigEndian(data[(rec + 8)..]);

        // Tag 0 (NONE) is the "free" / empty marker and shouldn't be surfaced.
        if (tag == 0) continue;
        descriptors.Add(new DataDescriptor(tag, refn, off, len));
        histogram[tag] = histogram.TryGetValue(tag, out var c) ? c + 1 : 1;
      }
      offset = nextOffset;
    }

    return new Hdf4File(magicKind, descriptors, histogram);
  }

  /// <summary>Short human-readable name for well-known HDF4 tags.</summary>
  public static string TagName(ushort tag) => tag switch {
    1 => "NONE",
    20 => "VERSION",
    30 => "COMPRESSED",
    40 => "LINKED",
    100 => "FID",
    101 => "FD",
    102 => "TID",
    103 => "TD",
    104 => "DIL",
    105 => "DIA",
    106 => "NT",
    107 => "MT",
    108 => "FREE",
    200 => "ID8",
    201 => "IP8",
    202 => "RI8",
    203 => "CI8",
    204 => "II8",
    300 => "ID",
    301 => "LUT",
    302 => "RI",
    303 => "CI",
    304 => "NRI",
    306 => "RIG",
    307 => "LD",
    308 => "MD",
    309 => "MA",
    310 => "CCN",
    311 => "CFM",
    312 => "AR",
    400 => "DRAW",
    401 => "RUN",
    500 => "XYP",
    501 => "MTO",
    602 => "T14",
    603 => "T105",
    700 => "SDG",
    701 => "SDD",
    702 => "SD",
    703 => "SDS",
    704 => "SDL",
    705 => "SDU",
    706 => "SDF",
    707 => "SDM",
    708 => "SDC",
    709 => "SDT",
    710 => "SDLNK",
    720 => "NDG",
    721 => "RESERVED",
    731 => "DFR8",
    732 => "DFCI",
    1020 => "VG",
    1962 => "VH",
    1963 => "VS",
    _ => $"TAG_{tag}",
  };
}
