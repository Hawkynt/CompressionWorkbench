#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.UefiFv;

/// <summary>
/// Offline random-access editor for ordinary FFS2 records in a firmware volume.
/// It reuses erased 0xFF ranges and never relocates unrelated FFS files.
/// </summary>
internal static class UefiFvInPlaceModifier {
  private sealed record Slot(int Offset, int Length, Guid Guid, byte Type) {
    public string Name => UefiFvWriter.EntryName(Guid, Type);
  }

  public static void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    var state = Open(archive);
    foreach (var (name, data) in FilesOnly(inputs)) {
      var identity = UefiFvWriter.IdentityFromName(name);
      var existing = ScanSlots(state.Image, state.FvStart, state.FvEnd)
        .FirstOrDefault(s => s.Guid == identity.Guid);
      if (existing != null)
        Erase(state, existing.Offset, existing.Length);

      var encoded = UefiFvWriter.BuildFfsFile(identity.Guid, identity.Type, data);
      var footprint = UefiFvWriter.Align8(encoded.Length);
      var offset = FindErasedRun(state.Image, state.DataStart, state.FvEnd, footprint);
      if (offset < 0)
        throw new IOException($"UEFI FV has no erased run large enough for '{name}' ({footprint} bytes).");

      Write(state, offset, encoded);
      if (footprint > encoded.Length)
        Erase(state, offset + encoded.Length, footprint - encoded.Length);
    }
  }

  public static void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    var state = Open(archive);
    foreach (var name in entryNames) {
      var slot = ScanSlots(state.Image, state.FvStart, state.FvEnd)
        .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
      if (slot != null)
        Erase(state, slot.Offset, slot.Length);
    }
  }

  private static EditorState Open(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new ArgumentException("UEFI FV mutation requires a seekable read/write stream.", nameof(archive));
    if (archive.Length > int.MaxValue)
      throw new NotSupportedException("The in-memory FV editor supports images up to 2 GiB.");

    archive.Position = 0;
    var image = new byte[checked((int)archive.Length)];
    archive.ReadExactly(image);
    var fvStart = UefiFvReader.FindFirst(image)
      ?? throw new InvalidDataException("UEFI FV header was not found.");
    var fv = UefiFvReader.Read(image, fvStart);
    var fvEnd = checked(fvStart + (int)fv.Header.FvLength);
    if (fvEnd > image.Length) throw new InvalidDataException("UEFI FV extends past the image.");
    var dataStart = Align8(checked(fvStart + fv.Header.HeaderLength));
    return new EditorState(archive, image, fvStart, dataStart, fvEnd);
  }

  private static List<Slot> ScanSlots(byte[] image, int fvStart, int fvEnd) {
    var fv = UefiFvReader.Read(image, fvStart);
    var pos = Align8(checked(fvStart + fv.Header.HeaderLength));
    var result = new List<Slot>();
    while (pos + UefiFvWriter.FfsHeaderLength <= fvEnd) {
      // One quantum at a time, and only the quantum is tested — see the same
      // walk in UefiFvReader: testing a whole header straddles the end of an
      // erased gap and reads the next file's GUID as a length.
      if (IsErased(image.AsSpan(pos, UefiFvWriter.Alignment))) {
        pos += UefiFvWriter.Alignment;
        continue;
      }

      var size = image[pos + 20] | (image[pos + 21] << 8) | (image[pos + 22] << 16);
      if (size < UefiFvWriter.FfsHeaderLength || pos + size > fvEnd)
        throw new InvalidDataException($"Invalid FFS file header at FV offset 0x{pos - fvStart:X}.");
      var guid = new Guid(image.AsSpan(pos, 16));
      var type = image[pos + 18];
      var footprint = UefiFvWriter.Align8(size);
      result.Add(new Slot(pos, footprint, guid, type));
      pos += footprint;
    }
    return result;
  }

  private static int FindErasedRun(byte[] image, int start, int end, int needed) {
    for (var pos = Align8(start); pos + needed <= end; pos += UefiFvWriter.Alignment) {
      if (IsErased(image.AsSpan(pos, needed))) return pos;
    }
    return -1;
  }

  private static bool IsErased(ReadOnlySpan<byte> bytes) {
    foreach (var b in bytes)
      if (b != 0xFF) return false;
    return true;
  }

  private static void Write(EditorState state, int offset, ReadOnlySpan<byte> bytes) {
    bytes.CopyTo(state.Image.AsSpan(offset, bytes.Length));
    state.Archive.Position = offset;
    state.Archive.Write(bytes);
  }

  private static void Erase(EditorState state, int offset, int length) {
    state.Image.AsSpan(offset, length).Fill(0xFF);
    state.Archive.Position = offset;
    Span<byte> erased = stackalloc byte[1024];
    erased.Fill(0xFF);
    var remaining = length;
    while (remaining > 0) {
      var take = Math.Min(remaining, erased.Length);
      state.Archive.Write(erased[..take]);
      remaining -= take;
    }
  }

  private static int Align8(int value) => (value + 7) & ~7;
  private sealed record EditorState(Stream Archive, byte[] Image, int FvStart, int DataStart, int FvEnd);
}
