#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>One entry of the ASPack stub's region table.</summary>
/// <param name="Rva">Address the restored bytes belong at, in the packed image's address space.</param>
/// <param name="OriginalSize">Size of the region before packing.</param>
/// <param name="Characteristics">Section characteristics the region had in the original file.</param>
/// <param name="IsStored">True when the region was left uncompressed (the stub's -0x10E sentinel).</param>
internal readonly record struct AsPackRegion(uint Rva, uint OriginalSize, uint Characteristics, bool IsStored);

/// <summary>Everything the ASPack stub tells us about how the image was packed.</summary>
internal sealed record AsPackLayout(
  int StubFileOffset,
  uint StubRva,
  int RegionTableFileOffset,
  IReadOnlyList<AsPackRegion> Regions,
  bool CallFilterEnabled,
  bool CallFilterWide,
  byte CallFilterMarker,
  uint? OriginalEntryPointRva
);

/// <summary>
/// Reads the ASPack 2.x container: the stub's region table, its call-filter
/// configuration and the original entry point, and restores each packed region.
/// </summary>
/// <remarks>
/// <para>
/// ASPack keeps the original section layout. Every section's raw bytes are
/// replaced in place by an <see cref="AsPackLzDecoder"/> stream, the section
/// headers keep their virtual addresses, and a <c>.aspack</c>/<c>.adata</c> pair
/// carrying the stub is appended. The stub walks a table of
/// <c>{rva, original size, characteristics}</c> triples terminated by a zero
/// record; a size of <c>-0x10E</c> marks a region that was stored rather than
/// compressed.
/// </para>
/// <para>
/// Before compressing, ASPack rewrites near <c>E8</c>/<c>E9</c> operands from
/// relative to absolute so that repeated calls to the same target compress; the
/// stub reverses this per region. Two variants exist, distinguished by a patched
/// jump inside the stub's filter loop: the common one only converts an operand
/// whose first byte equals a per-file marker byte and stores the absolute address
/// in the remaining three, while the other converts every operand as a full
/// 32-bit value. A patched flag byte disables the filter entirely.
/// </para>
/// <para>
/// Layout knowledge here comes from reading the ASPack 2.12 stub's own code, not
/// from any third-party unpacker.
/// </para>
/// </remarks>
internal static class AsPackImage {

  /// <summary>Region size sentinel meaning "stored, not compressed" (-0x10E).</summary>
  private const uint StoredRegionSentinel = 0xFFFFFEF2;

  private const int MaximumRegions = 96;

  /// <summary>
  /// <c>mov eax,[esi] / jmp $+2 / cmp byte [esi],marker / jne / and al,0 /
  /// rol eax,24 / sub eax,ebx / mov [esi],eax</c> — the tail of the stub's
  /// call-filter loop. The jump displacement and the marker byte are patched per
  /// file, so both are wildcards.
  /// </summary>
  private static ReadOnlySpan<byte> CallFilterAnchor =>
    [0x8B, 0x06, 0xEB, 0x00, 0x80, 0x3E, 0x00, 0x75, 0xF3, 0x24, 0x00, 0xC1, 0xC0, 0x18, 0x2B, 0xC3, 0x89, 0x06];

  private static ReadOnlySpan<byte> CallFilterAnchorMask =>
    [1, 1, 1, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1];

  /// <summary><c>mov bl,flag / cmp bl,0 / jne</c> — skips the call filter when the flag is non-zero.</summary>
  private static ReadOnlySpan<byte> CallFilterFlagAnchor => [0xB3, 0x00, 0x80, 0xFB, 0x00, 0x75];

  private static ReadOnlySpan<byte> CallFilterFlagAnchorMask => [1, 0, 1, 1, 1, 1];

  /// <summary>
  /// <c>mov eax,oep / push eax / add eax,[ebp+delta] / pop ecx / or ecx,ecx /
  /// mov [ebp+delta],eax</c> — the stub patching its own tail jump with the
  /// original entry point.
  /// </summary>
  private static ReadOnlySpan<byte> EntryPointAnchor =>
    [0xB8, 0, 0, 0, 0, 0x50, 0x03, 0x85, 0, 0, 0, 0, 0x59, 0x0B, 0xC9, 0x89, 0x85];

  private static ReadOnlySpan<byte> EntryPointAnchorMask =>
    [1, 0, 0, 0, 0, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 1];

  /// <summary>
  /// Reads the stub's region table and configuration. Returns false when the
  /// image is not a PE, carries no entry-point section, or holds no readable
  /// region table — all of which mean "an ASPack build we do not model".
  /// </summary>
  public static bool TryRead(byte[] image, ExecutableImageInfo? info, out AsPackLayout? layout) {
    layout = null;
    if (image is null || info is null || info.Container != ExecutableContainerKind.Pe || info.Regions.Count == 0)
      return false;

    var stub = FindEntryPointRegion(info);
    if (stub is null || stub.FileSize == 0 || stub.FileOffset > (ulong)image.Length)
      return false;

    var stubOffset = (int)stub.FileOffset;
    var stubLength = (int)Math.Min(stub.FileSize, (ulong)(image.Length - stubOffset));
    var stubBytes = image.AsSpan(stubOffset, stubLength);
    var firstSectionRva = info.Regions.Min(r => r.VirtualAddress);

    var tableOffset = FindRegionTable(image, stubOffset, stubLength, firstSectionRva, info, out var regions);
    if (tableOffset < 0 || regions.Count == 0)
      return false;

    var filterEnabled = true;
    var filterWide = false;
    byte filterMarker = 0;
    var filterAt = IndexOfMasked(stubBytes, CallFilterAnchor, CallFilterAnchorMask);
    if (filterAt >= 0) {
      // A displacement of zero falls through to the marker test; the patched
      // variant jumps straight to the subtraction and converts every operand.
      filterWide = stubBytes[filterAt + 3] != 0;
      filterMarker = stubBytes[filterAt + 6];
    }

    var flagAt = IndexOfMasked(stubBytes, CallFilterFlagAnchor, CallFilterFlagAnchorMask);
    if (flagAt >= 0)
      filterEnabled = stubBytes[flagAt + 1] == 0;
    else if (filterAt < 0)
      filterEnabled = false;

    uint? originalEntryPoint = null;
    var entryAt = IndexOfMasked(stubBytes, EntryPointAnchor, EntryPointAnchorMask);
    if (entryAt >= 0) {
      var candidate = BinaryPrimitives.ReadUInt32LittleEndian(stubBytes[(entryAt + 1)..]);
      if (candidate != 0 && candidate < stub.VirtualAddress)
        originalEntryPoint = candidate;
    }

    layout = new(stubOffset, (uint)stub.VirtualAddress, tableOffset, regions,
      filterEnabled, filterWide, filterMarker, originalEntryPoint);
    return true;
  }

  /// <summary>
  /// Restores one region: decodes its stream and reverses the call filter.
  /// Stored regions and regions whose data is not present in the file return null.
  /// </summary>
  public static byte[]? Restore(byte[] image, ExecutableImageInfo info, AsPackLayout layout, AsPackRegion region, long maximumSize) {
    if (region.IsStored || region.OriginalSize == 0 || region.OriginalSize > maximumSize)
      return null;

    var offset = ResolveRva(info, region.Rva);
    if (offset < 0)
      return null;

    var (buffer, produced) = AsPackLzDecoder.Decompress(image, offset, (int)region.OriginalSize);
    if (layout.CallFilterEnabled)
      ReverseCallFilter(buffer.AsSpan(0, produced), layout.CallFilterMarker, layout.CallFilterWide);

    return buffer.AsSpan(0, (int)region.OriginalSize).ToArray();
  }

  /// <summary>
  /// Turns the packer's absolute <c>E8</c>/<c>E9</c> operands back into
  /// displacements, exactly as the stub does: scan for a near call/jump opcode,
  /// read the operand that follows, subtract the opcode's own offset and write
  /// the result back, then resume scanning after the instruction.
  /// </summary>
  public static void ReverseCallFilter(Span<byte> buffer, byte marker, bool wide) {
    var remaining = buffer.Length - 5;
    var position = 0;
    while (remaining > 0) {
      var opcode = buffer[position];
      ++position;
      if ((opcode == 0xE8 || opcode == 0xE9) && position + 4 <= buffer.Length) {
        var opcodeOffset = (uint)(position - 1);
        uint absolute;
        if (wide)
          absolute = BinaryPrimitives.ReadUInt32LittleEndian(buffer[position..]);
        else if (buffer[position] == marker)
          absolute = (uint)(buffer[position + 1] | (buffer[position + 2] << 8) | (buffer[position + 3] << 16));
        else {
          --remaining;
          continue;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(buffer[position..], absolute - opcodeOffset);
        position += 4;
        remaining -= 5;
      } else
        --remaining;
    }
  }

  /// <summary>Maps an RVA to a file offset, or -1 when the RVA has no bytes in the file.</summary>
  public static int ResolveRva(ExecutableImageInfo info, uint rva) {
    foreach (var region in info.Regions) {
      if (rva < region.VirtualAddress) continue;
      var delta = rva - region.VirtualAddress;
      if (delta >= region.FileSize) continue;
      var offset = region.FileOffset + delta;
      if (offset > int.MaxValue) continue;
      return (int)offset;
    }

    return -1;
  }

  /// <summary>Name of the section an RVA falls into, for artifact naming.</summary>
  public static string DescribeRva(ExecutableImageInfo info, uint rva) {
    foreach (var region in info.Regions) {
      var size = Math.Max(region.VirtualSize, region.FileSize);
      if (rva >= region.VirtualAddress && rva < region.VirtualAddress + size)
        return region.Name;
    }

    return "region";
  }

  private static ExecutableRegion? FindEntryPointRegion(ExecutableImageInfo info) {
    foreach (var region in info.Regions) {
      var size = Math.Max(region.VirtualSize, region.FileSize);
      if (info.EntryPoint >= region.VirtualAddress && info.EntryPoint < region.VirtualAddress + size)
        return region;
    }

    return null;
  }

  private static int FindRegionTable(
      byte[] image, int stubOffset, int stubLength, ulong firstSectionRva,
      ExecutableImageInfo info, out IReadOnlyList<AsPackRegion> regions) {
    regions = [];
    var end = stubOffset + stubLength;
    for (var offset = stubOffset; offset + 12 <= end; offset += 4) {
      if (BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset)) != firstSectionRva)
        continue;
      if (TryReadRegionTable(image, offset, end, info, out var candidate)) {
        regions = candidate;
        return offset;
      }
    }

    return -1;
  }

  private static bool TryReadRegionTable(
      byte[] image, int offset, int end, ExecutableImageInfo info, out List<AsPackRegion> regions) {
    regions = [];
    var previousRva = 0u;
    for (var cursor = offset; cursor + 12 <= end; cursor += 12) {
      var rva = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(cursor));
      if (rva == 0)
        return regions.Count > 0;

      var size = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(cursor + 4));
      var characteristics = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(cursor + 8));
      var stored = size == StoredRegionSentinel;
      if (rva <= previousRva && regions.Count > 0) return false;
      if ((characteristics & 0xF0000000u) == 0) return false;
      if (!stored && (size == 0 || size > 0x08000000u)) return false;
      if (!info.Regions.Any(r => rva >= r.VirtualAddress && rva < r.VirtualAddress + Math.Max(r.VirtualSize, r.FileSize)))
        return false;
      if (regions.Count >= MaximumRegions) return false;

      regions.Add(new(rva, stored ? 0 : size, characteristics, stored));
      previousRva = rva;
    }

    return false;
  }

  private static int IndexOfMasked(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, ReadOnlySpan<byte> mask) {
    for (var start = 0; start + needle.Length <= haystack.Length; ++start) {
      var matched = true;
      for (var i = 0; i < needle.Length; ++i)
        if (mask[i] != 0 && haystack[start + i] != needle[i]) {
          matched = false;
          break;
        }

      if (matched) return start;
    }

    return -1;
  }
}
