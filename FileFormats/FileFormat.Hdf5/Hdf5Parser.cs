#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Hdf5;

/// <summary>
/// Partial HDF5 parser — finds the superblock, reads the offset/length sizes and
/// root object-header offset, and scans the file for object-header signatures.
/// </summary>
public sealed class Hdf5SuperblockInfo {
  public bool Found { get; set; }
  public long SuperblockOffset { get; set; }
  public int Version { get; set; } = -1;
  public int OffsetSize { get; set; }
  public int LengthSize { get; set; }
  public long RootOffset { get; set; } = -1;
}

internal static class Hdf5Parser {
  // HDF5 v2+ object-header signature
  private static readonly byte[] OhdrSignature = "OHDR"u8.ToArray();

  public static Hdf5SuperblockInfo ReadSuperblock(byte[] blob) {
    var info = new Hdf5SuperblockInfo();

    // Find the signature at 0, 512, 1024, 2048, ...
    var sigOffset = FindSuperblockOffset(blob);
    if (sigOffset < 0) return info;

    info.SuperblockOffset = sigOffset;
    var p = sigOffset + Hdf5FormatDescriptor.Hdf5Signature.Length;
    if (p >= blob.Length) return info;

    var version = blob[p];
    info.Version = version;
    p++;

    try {
      switch (version) {
        case 0:
        case 1:
          // Version 0/1: free-space version, root group symbol table entry version, reserved,
          // shared header message format version, size of offsets, size of lengths, reserved
          if (p + 6 > blob.Length) return info;
          p += 4; // free space, root sym table, reserved, shared hdr msg fmt
          info.OffsetSize = blob[p++];
          info.LengthSize = blob[p++];
          p++; // reserved
          // group leaf node K (2), group internal node K (2), file consistency flags (4)
          if (p + 8 > blob.Length) return info;
          p += 2 + 2 + 4;
          if (version == 1) {
            // indexed storage internal node K (2) + reserved (2)
            if (p + 4 > blob.Length) return info;
            p += 4;
          }
          // base addr, free-space info addr, end of file addr, driver info addr (4 x offset_size)
          if (p + 4 * info.OffsetSize > blob.Length) return info;
          p += 4 * info.OffsetSize;
          // Root group symbol table entry: link name offset (offset_size), obj header addr (offset_size),
          // cache type (4), reserved (4), scratch (16)
          if (p + info.OffsetSize > blob.Length) return info;
          p += info.OffsetSize; // link name offset
          info.RootOffset = ReadOffset(blob, p, info.OffsetSize);
          info.Found = info.OffsetSize > 0 && info.LengthSize > 0;
          return info;
        case 2:
        case 3:
          // Version 2/3: size of offsets, size of lengths, file consistency flags (1),
          // base address, superblock extension addr, end of file addr, root group obj hdr addr, checksum
          if (p + 3 > blob.Length) return info;
          info.OffsetSize = blob[p++];
          info.LengthSize = blob[p++];
          p++; // flags
          if (info.OffsetSize <= 0 || info.OffsetSize > 8) return info;
          if (p + 4 * info.OffsetSize > blob.Length) return info;
          p += info.OffsetSize; // base address
          p += info.OffsetSize; // superblock ext addr
          p += info.OffsetSize; // end-of-file addr
          info.RootOffset = ReadOffset(blob, p, info.OffsetSize);
          info.Found = true;
          return info;
        default:
          return info;
      }
    } catch {
      return info;
    }
  }

  public static IEnumerable<string> ScanForObjectHeaders(byte[] blob, Hdf5SuperblockInfo super) {
    // This is intentionally lightweight: we scan the file for the "OHDR" signature
    // used by v2+ object headers. For v0/v1 headers (no signature) we only report
    // the root offset if plausible.
    var results = new List<string>();

    if (super.Version >= 2 && super.RootOffset >= 0)
      results.Add($"/\tgroup\t{super.RootOffset}");
    else if (super.Version >= 0 && super.RootOffset >= 0)
      results.Add($"/\tgroup_v0\t{super.RootOffset}");

    var sig = OhdrSignature;
    for (var i = 0; i + sig.Length <= blob.Length; i++) {
      if (blob[i] == sig[0] &&
          blob[i + 1] == sig[1] &&
          blob[i + 2] == sig[2] &&
          blob[i + 3] == sig[3]) {
        results.Add($"ohdr@{i}\tobject\t0");
        // skip past the signature to avoid double-matches
        i += sig.Length - 1;
        if (results.Count > 4096) break;
      }
    }
    return results;
  }

  private static int FindSuperblockOffset(byte[] blob) {
    var sig = Hdf5FormatDescriptor.Hdf5Signature;
    // Check 0, 512, 1024, 2048, 4096, ...
    long offset = 0;
    while (offset + sig.Length <= blob.LongLength) {
      if (MatchesAt(blob, offset, sig))
        return (int)offset;
      offset = offset == 0 ? 512 : offset * 2;
      if (offset > int.MaxValue) break;
    }
    return -1;
  }

  private static bool MatchesAt(byte[] blob, long offset, byte[] sig) {
    if (offset + sig.Length > blob.LongLength) return false;
    for (var i = 0; i < sig.Length; i++)
      if (blob[offset + i] != sig[i]) return false;
    return true;
  }

  private static long ReadOffset(byte[] blob, int pos, int offsetSize) {
    if (pos + offsetSize > blob.Length) return -1;
    return offsetSize switch {
      1 => blob[pos],
      2 => BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pos, 2)),
      4 => BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos, 4)),
      8 => (long)BinaryPrimitives.ReadUInt64LittleEndian(blob.AsSpan(pos, 8)),
      _ => -1,
    };
  }
}
