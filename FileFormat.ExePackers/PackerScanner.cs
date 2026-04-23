#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.ExePackers;

/// <summary>
/// Shared scanning helpers used by the demoscene / historical executable packer
/// detectors (PKLITE, LZEXE, DIET, Petite, Shrinkler, …). Each detector relies
/// on a combination of byte-pattern probes, section-name lookups, and
/// MZ/PE structural checks that this class centralizes.
/// </summary>
internal static class PackerScanner {

  /// <summary>True when the file starts with an MZ DOS header (MZ or ZM variant).</summary>
  public static bool IsMzExecutable(ReadOnlySpan<byte> data) =>
    data.Length >= 2 && ((data[0] == 'M' && data[1] == 'Z') || (data[0] == 'Z' && data[1] == 'M'));

  /// <summary>True when the file is a PE (MZ + valid e_lfanew + "PE\0\0").</summary>
  public static bool IsPe(ReadOnlySpan<byte> data) {
    if (!IsMzExecutable(data) || data.Length < 0x40) return false;
    var eLfanew = BinaryPrimitives.ReadUInt32LittleEndian(data[0x3C..]);
    if (eLfanew == 0 || eLfanew + 4 > (uint)data.Length) return false;
    return data[(int)eLfanew] == 'P' && data[(int)eLfanew + 1] == 'E';
  }

  /// <summary>
  /// Returns the PE section names + their characteristics flags, or an empty
  /// list when the file isn't a valid PE.
  /// </summary>
  public static IReadOnlyList<(string Name, uint Characteristics)> GetPeSections(ReadOnlySpan<byte> data) {
    if (!IsPe(data)) return [];
    var eLfanew = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[0x3C..]);
    var coff = data[(eLfanew + 4)..];
    var numSections = BinaryPrimitives.ReadUInt16LittleEndian(coff[2..]);
    var optHdrSize = BinaryPrimitives.ReadUInt16LittleEndian(coff[16..]);
    var sectionTableOffset = eLfanew + 24 + optHdrSize;
    if (sectionTableOffset + numSections * 40 > data.Length) return [];

    var names = new List<(string, uint)>(numSections);
    for (var i = 0; i < numSections; i++) {
      var off = sectionTableOffset + i * 40;
      var nameSpan = data.Slice(off, 8);
      var terminator = nameSpan.IndexOf((byte)0);
      if (terminator < 0) terminator = 8;
      var name = Encoding.ASCII.GetString(nameSpan[..terminator]);
      var chars = BinaryPrimitives.ReadUInt32LittleEndian(data[(off + 36)..]);
      names.Add((name, chars));
    }
    return names;
  }

  /// <summary>
  /// Searches <paramref name="data"/> for the supplied byte pattern and returns
  /// the first matching offset (or -1 when not found). Keeps the search bounded
  /// to <paramref name="maxOffset"/> so we don't scan multi-megabyte payloads
  /// chasing a fingerprint that's only ever near the start.
  /// </summary>
  public static int IndexOfBounded(ReadOnlySpan<byte> data, ReadOnlySpan<byte> needle, int maxOffset) {
    var window = maxOffset >= data.Length ? data : data[..maxOffset];
    var idx = window.IndexOf(needle);
    return idx;
  }

  /// <summary>Reads a NUL-terminated ASCII string starting at the supplied offset (capped).</summary>
  public static string ReadAsciiAt(ReadOnlySpan<byte> data, int offset, int maxLength) {
    if (offset < 0 || offset >= data.Length) return "";
    var slice = data[offset..Math.Min(offset + maxLength, data.Length)];
    var nul = slice.IndexOf((byte)0);
    return Encoding.ASCII.GetString(nul < 0 ? slice : slice[..nul]);
  }
}
