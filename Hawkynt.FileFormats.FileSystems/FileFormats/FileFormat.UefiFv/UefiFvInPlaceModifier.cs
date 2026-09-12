#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.UefiFv;

/// <summary>Transactional offline editor for standard PI FFS2/FFS3 firmware volumes.</summary>
internal static class UefiFvInPlaceModifier {
  public static void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    var state = Open(archive);
    foreach (var (name, data) in FilesOnly(inputs)) {
      var identity = UefiFvWriter.IdentityFromName(name);
      foreach (var old in UefiFvParser.LiveSlots(state.Volume, includePad: false)
                 .Where(slot => slot.Name == identity.Guid).ToList())
        Erase(state, old.Offset, old.Footprint);
      Refresh(state);

      var encoded = UefiFvWriter.BuildFfsFile(identity.Guid, identity.Type, data,
        state.Volume.FileSystemGuid, state.Volume.ErasePolarity);
      var footprint = UefiFvWriter.Align8(encoded.Length);
      var offset = FindErasedRun(state.Image, state.Volume.DataStart, state.Volume.End,
        footprint, state.Volume.EraseByte);
      if (offset < 0)
        throw new IOException($"UEFI FV has no erased run large enough for '{name}' ({footprint} bytes).");
      encoded.CopyTo(state.Image, offset);
      Refresh(state);
    }
    Finish(state);
  }

  public static void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    var state = Open(archive);
    foreach (var name in entryNames) {
      var slot = UefiFvParser.LiveSlots(state.Volume, includePad: false)
        .FirstOrDefault(s => string.Equals(UefiFvWriter.EntryName(s.Name, s.Type), name, StringComparison.OrdinalIgnoreCase));
      if (slot == null) continue;
      Erase(state, slot.Offset, slot.Footprint);
      Refresh(state);
    }
    Finish(state);
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
    var start = UefiFvReader.FindFirst(image) ?? throw new InvalidDataException("UEFI FV header was not found.");
    var volume = UefiFvParser.Parse(image, start);
    EnsureMutable(volume);
    return new EditorState(archive, image, start, volume);
  }

  private static void EnsureMutable(UefiFvLayout volume) {
    if (volume.FileSystemGuid != UefiFvConstants.Ffs2Guid && volume.FileSystemGuid != UefiFvConstants.Ffs3Guid)
      throw new NotSupportedException($"UEFI FV uses unsupported file-system GUID {volume.FileSystemGuid:D}.");
    if (volume.IsSigned)
      throw new NotSupportedException("Signed UEFI firmware volumes cannot be modified without re-signing the complete volume.");
  }

  private static void Refresh(EditorState state)
    => state.Volume = UefiFvParser.Parse(state.Image, state.Start);

  private static void Finish(EditorState state) {
    var usedEnd = UefiFvParser.LiveSlots(state.Volume).Select(slot => slot.DataEnd)
      .DefaultIfEmpty(state.Volume.DataStart).Max();
    UefiFvParser.WriteUsedSize(state.Image, state.Volume, usedEnd);
    state.Archive.Position = 0;
    state.Archive.Write(state.Image);
    state.Archive.SetLength(state.Image.Length);
  }

  private static int FindErasedRun(byte[] image, int start, int end, int needed, byte eraseByte) {
    for (var pos = UefiFvConstants.AlignOffset(start, start); pos + needed <= end; pos += UefiFvWriter.Alignment)
      if (UefiFvParser.IsErased(image.AsSpan(pos, needed), eraseByte)) return pos;
    return -1;
  }

  private static void Erase(EditorState state, int offset, int length)
    => state.Image.AsSpan(offset, length).Fill(state.Volume.EraseByte);

  private sealed class EditorState(Stream archive, byte[] image, int start, UefiFvLayout volume) {
    public Stream Archive { get; } = archive;
    public byte[] Image { get; } = image;
    public int Start { get; } = start;
    public UefiFvLayout Volume { get; set; } = volume;
  }
}
