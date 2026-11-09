using System.Buffers.Binary;
using FileFormat.PeResources;

namespace FileFormat.GenteeInstaller;

/// <summary>One file of a Gentee (CreateInstall) installer.</summary>
/// <param name="Path">Name: <c>gentee.dll</c> and <c>script.ge</c> from the launcher, <c>temp/…</c> for the files the setup unpacks for itself, <c>setup/…</c> for the files it installs.</param>
/// <param name="Size">Unpacked size.</param>
/// <param name="CompressedSize">Packed size.</param>
/// <param name="Modified">File time, when recorded.</param>
public sealed record GenteeInstallerEntry(string Path, long Size, long CompressedSize, DateTime? Modified) {
  internal GeaArchive? Archive { get; init; }
  internal GeaEntry? ArchiveEntry { get; init; }
  internal byte[]? Content { get; init; }
}

/// <summary>
/// Installers made with Gentee's launcher (CreateInstall and other Gentee programs): a PE stub
/// whose "Gentee Launcher" header says how big the runtime and the script are; a <c>.gentee</c>
/// section holding both, LZGE packed; an RT_RCDATA resource <c>SETUP_TEMP</c> holding the files
/// the setup unpacks for itself; and GEA archives after the PE image holding the files it
/// installs.
/// </summary>
/// <remarks>
/// Converted from the <c>gentee</c> module of Observer (lazyhamster, LGPL-3.0-or-later),
/// <c>InstallerNewFile.cpp</c> and <c>InstallerDefs.h</c>, revision <c>ddbe6b6d</c>; see
/// THIRD-PARTY-NOTICE.Observer.txt. The older "Gentee installer" layout, which Observer recognises
/// but does not read, is not read here either.
/// </remarks>
public sealed class GenteeInstallerReader {
  private static ReadOnlySpan<byte> Signature => "Gentee Launcher\0"u8;
  private const int SignatureSearch = 32 * 1024;
  private const ushort RtRcData = 10;

  private GenteeInstallerReader(List<GenteeInstallerEntry> entries) => this.Entries = entries;

  /// <summary>The runtime, the script, the setup's own files and the installed files.</summary>
  public IReadOnlyList<GenteeInstallerEntry> Entries { get; }

  /// <summary>Whether a launcher header sits in the first 32 KiB and points at itself.</summary>
  public static bool IsGentee(Stream stream) {
    try {
      stream.Position = 0;
      var head = new byte[(int)Math.Min(stream.Length, SignatureSearch + 128)];
      stream.ReadExactly(head);
      return FindHeader(head) is not null;
    } catch (Exception e) when (e is IOException or EndOfStreamException) {
      return false;
    }
  }

  private sealed record Header(uint Exe, uint Minimum, int ExtensionCount, uint RuntimeSize, uint ScriptSize, long[] Extensions);

  private static Header? FindHeader(byte[] data) {
    var limit = Math.Min(data.Length, SignatureSearch);
    for (var at = data.AsSpan(0, limit).IndexOf(Signature); at >= 0; ) {
      foreach (var v2 in (bool[])[false, true]) {
        var s = v2 ? 1 : 0;
        if (at + 49 + s + 64 > data.Length)
          continue;
        var h = data.AsSpan(at);
        if (BinaryPrimitives.ReadUInt32LittleEndian(h[(44 + s)..]) != at)
          continue;
        var extensions = new long[8];
        for (var i = 0; i < 8; ++i)
          extensions[i] = BinaryPrimitives.ReadInt64LittleEndian(h[(48 + s + 8 * i)..]);
        return new Header(BinaryPrimitives.ReadUInt32LittleEndian(h[16..]), BinaryPrimitives.ReadUInt32LittleEndian(h[20..]), h[25],
          BinaryPrimitives.ReadUInt32LittleEndian(h[(28 + s)..]), BinaryPrimitives.ReadUInt32LittleEndian(h[(32 + s)..]), extensions);
      }
      var next = data.AsSpan(at + 1, limit - at - 1).IndexOf(Signature);
      at = next < 0 ? -1 : at + 1 + next;
    }
    return null;
  }

  /// <summary>Opens an installer held in memory.</summary>
  /// <exception cref="InvalidDataException">No launcher header, or a part it announces does not decode.</exception>
  /// <exception cref="NotSupportedException">A multi-volume archive.</exception>
  public static GenteeInstallerReader Open(byte[] data) {
    var header = FindHeader(data) ?? throw new InvalidDataException("no Gentee Launcher header in the first 32 KiB");
    if (data.Length < header.Minimum)
      throw new InvalidDataException("the installer is shorter than its launcher header says");
    using var stream = new MemoryStream(data, writable: false);
    var (sections, overlay) = PeLayout(stream);

    var entries = new List<GenteeInstallerEntry>();
    if (!sections.TryGetValue(".gentee", out var section))
      throw new InvalidDataException("no .gentee section");
    // The runtime and then the script, each taking the packed size the header gives; the LZGE
    // stream inside may end a byte short of it.
    if (header.RuntimeSize + header.ScriptSize > section.Size)
      throw new InvalidDataException("the .gentee section is smaller than the runtime and script the header announces");
    var runtime = Unpack(data, section.Offset, section.Offset + header.RuntimeSize, out _);
    entries.Add(new("gentee.dll", runtime.Length, header.RuntimeSize, null) { Content = runtime });
    var scriptAt = section.Offset + header.RuntimeSize;
    var script = Unpack(data, scriptAt, scriptAt + header.ScriptSize, out _);
    entries.Add(new("script.ge", script.Length, header.ScriptSize, null) { Content = script });

    if (PeResourceLocator.Find(stream, RtRcData, "SETUP_TEMP") is { } temp) {
      var resource = data.AsSpan((int)temp.Offset, temp.Size).ToArray();
      var archive = GeaArchive.Open(resource, 0);
      entries.AddRange(archive.Entries.Select(e => Member("temp/", archive, e)));
    }

    var at = overlay;
    for (var i = 0; i < header.ExtensionCount && i < 8; ++i) {
      if (header.Extensions[i] == 0)
        continue;
      if (GeaArchive.StartsAt(data, at)) {
        var archive = GeaArchive.Open(data, at);
        entries.AddRange(archive.Entries.Select(e => Member("setup/", archive, e)));
      }
      at += header.Extensions[i];
    }
    return new(entries);
  }

  private static GenteeInstallerEntry Member(string prefix, GeaArchive archive, GeaEntry e) =>
    new(prefix + e.Path, e.Size, e.CompressedSize, e.Modified) { Archive = archive, ArchiveEntry = e };

  /// <summary>Unpacks one entry.</summary>
  /// <exception cref="InvalidDataException">The entry does not decode, or misses its CRC-32.</exception>
  /// <exception cref="NotSupportedException">A password-protected file.</exception>
  public byte[] Extract(GenteeInstallerEntry entry) =>
    entry.Content is { } content ? (byte[])content.Clone() : entry.Archive!.Extract(entry.ArchiveEntry!);

  /// <summary>Unpacks every entry, decoding each archive's solid chains once.</summary>
  public IEnumerable<(GenteeInstallerEntry Entry, byte[] Data)> ExtractAll() {
    var byArchive = new Dictionary<GeaArchive, Dictionary<GeaEntry, byte[]>>();
    foreach (var entry in this.Entries) {
      if (entry.Content != null) {
        yield return (entry, (byte[])entry.Content.Clone());
        continue;
      }
      if (!byArchive.TryGetValue(entry.Archive!, out var all))
        byArchive[entry.Archive!] = all = entry.Archive!.ExtractAll().ToDictionary(p => p.Entry, p => p.Data);
      yield return (entry, all[entry.ArchiveEntry!]);
    }
  }

  /// <summary>A launcher part: the unpacked size, then an LZGE stream.</summary>
  private static byte[] Unpack(byte[] data, long at, long end, out int used) {
    if (at + 4 > end)
      throw new InvalidDataException("the .gentee section ends early");
    var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)at));
    if (size < 0 || size > 256 << 20)
      throw new InvalidDataException("launcher part size out of range");
    var output = new byte[size];
    used = Lzge.Decode(data.AsSpan((int)at + 4, (int)(end - at - 4)), output, 0, size);
    return output;
  }

  private static (Dictionary<string, (long Offset, long Size)> Sections, long Overlay) PeLayout(Stream stream) {
    Span<byte> b = stackalloc byte[64];
    stream.Position = 0;
    stream.ReadExactly(b);
    if (b[0] != 'M' || b[1] != 'Z')
      throw new InvalidDataException("not a PE image");
    long pe = BinaryPrimitives.ReadUInt32LittleEndian(b[0x3C..]);
    Span<byte> h = stackalloc byte[24];
    stream.Position = pe;
    stream.ReadExactly(h);
    if (h[0] != 'P' || h[1] != 'E' || h[2] != 0 || h[3] != 0)
      throw new InvalidDataException("not a PE image");
    int count = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
    int optional = BinaryPrimitives.ReadUInt16LittleEndian(h[20..]);
    var sections = new Dictionary<string, (long, long)>(StringComparer.Ordinal);
    long end = 0;
    Span<byte> s = stackalloc byte[40];
    for (var i = 0; i < count; ++i) {
      stream.Position = pe + 24 + optional + 40L * i;
      stream.ReadExactly(s);
      var name = System.Text.Encoding.ASCII.GetString(s[..8]).TrimEnd('\0');
      long size = BinaryPrimitives.ReadUInt32LittleEndian(s[16..]);
      long offset = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]);
      if (offset + size > stream.Length)
        throw new InvalidDataException("a PE section runs past the end of the file");
      sections.TryAdd(name, (offset, size));
      end = Math.Max(end, offset + size);
    }
    return (sections, end);
  }
}
