#pragma warning disable CS1591
using FileFormat.Arc;

namespace FileFormat.Pak;

/// <summary>
/// Random-access in-place modifier for Quake PAK archives. PAK shares the
/// ARC binary layout (chain of entry blocks terminated by a 2-byte
/// 0x1A 0x00 end-of-archive marker), so this wrapper delegates straight to
/// <see cref="ArcModifier"/>. Add overwrites the old EOA marker with a new
/// Stored entry plus a fresh EOA — bytes before the old EOA are untouched.
/// Remove walks the entry chain, locates the target, and shifts trailing
/// bytes forward to compact (no central directory).
/// </summary>
public static class PakInPlaceModifier {

  /// <summary>
  /// Appends a Stored entry to a PAK archive. Bytes before the old
  /// end-of-archive marker are not modified.
  /// </summary>
  public static void AddFile(Stream pak, string name, byte[] data)
    => ArcModifier.AddFile(pak, name, data);

  /// <summary>
  /// Removes the named entry. Returns true if found.
  /// </summary>
  public static bool RemoveFile(Stream pak, string name, bool wipeData = true)
    => ArcModifier.RemoveFile(pak, name, wipeData);
}
