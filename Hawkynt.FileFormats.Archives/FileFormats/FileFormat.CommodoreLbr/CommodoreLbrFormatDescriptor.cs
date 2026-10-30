#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.CommodoreLbr;

/// <summary>One member of a Commodore LBR container.</summary>
/// <param name="Name">The PETSCII file name (read as Latin-1).</param>
/// <param name="FileType">The CBM DOS file type letter: P (PRG), S (SEQ), U (USR).</param>
/// <param name="Offset">Offset of the member's data.</param>
/// <param name="Length">Length of the member's data.</param>
public sealed record CommodoreLbrEntry(string Name, char FileType, long Offset, long Length);

/// <summary>
/// Reads and writes the Commodore 64 LBR container of Mike Swanson's "Library" (DWB signature):
/// a CR-terminated ASCII directory (count, then name, type and size per member) followed by the
/// members' bytes back to back, uncompressed.
/// </summary>
public static class CommodoreLbr {
  private static readonly Encoding Text = Encoding.Latin1;

  /// <summary>Parses the directory; data offsets follow from the sizes.</summary>
  /// <exception cref="InvalidDataException">The header or directory is malformed, or a member runs past the end.</exception>
  public static List<CommodoreLbrEntry> Read(ReadOnlySpan<byte> data) {
    if (data.Length < 4 || !data[..3].SequenceEqual("DWB"u8))
      throw new InvalidDataException("Not a Commodore LBR container (no DWB signature).");

    var position = 3;
    var count = Number(data, ref position);
    if (count < 0 || count > 10000)
      throw new InvalidDataException("Commodore LBR directory count is out of range.");

    var directory = new List<(string Name, char Type, long Size)>(count);
    for (var i = 0; i < count; ++i) {
      var name = Line(data, ref position);
      var type = Line(data, ref position);
      if (type.Length != 1)
        throw new InvalidDataException($"Commodore LBR entry '{name}' has file type '{type}'.");
      var size = Number(data, ref position);
      if (size < 0)
        throw new InvalidDataException($"Commodore LBR entry '{name}' has a negative size.");
      directory.Add((name, type[0], size));
    }

    var entries = new List<CommodoreLbrEntry>(count);
    long offset = position;
    foreach (var (name, type, size) in directory) {
      if (size > data.Length - offset)
        throw new InvalidDataException($"Commodore LBR entry '{name}' runs past the end of the container.");
      entries.Add(new(name, type, offset, size));
      offset += size;
    }

    return entries;
  }

  private static string Line(ReadOnlySpan<byte> data, ref int position) {
    var end = data[position..].IndexOf((byte)'\r');
    if (end < 0)
      throw new InvalidDataException("Commodore LBR directory line is not CR-terminated.");
    var text = Text.GetString(data.Slice(position, end));
    position += end + 1;
    return text;
  }

  private static int Number(ReadOnlySpan<byte> data, ref int position) {
    var text = Line(data, ref position).Trim(' ');
    return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
      ? value
      : throw new InvalidDataException($"Commodore LBR number '{text}' is not a decimal number.");
  }

  /// <summary>Writes a container the way Library writes one.</summary>
  public static byte[] Write(IReadOnlyList<(string Name, char FileType, byte[] Data)> members) {
    using var ms = new MemoryStream();
    void Put(string s) => ms.Write(Text.GetBytes(s));
    Put($"DWB {members.Count} \r");
    foreach (var (name, type, data) in members) {
      if (name.Contains('\r'))
        throw new ArgumentException("A Commodore file name cannot contain CR.", nameof(members));
      if (type is not ('P' or 'S' or 'U'))
        throw new ArgumentException($"Commodore LBR stores PRG, SEQ and USR files, not '{type}'.", nameof(members));
      Put($"{name}\r{type}\r {data.Length.ToString(CultureInfo.InvariantCulture)} \r");
    }

    foreach (var (_, _, data) in members)
      ms.Write(data);
    return ms.ToArray();
  }
}

/// <summary>
/// Commodore 64 LBR library container (Library 7.0–10.0, "DWB" signature). The extension .lbr is the
/// CP/M library's, so this one is recognised by its signature.
///
/// References:
/// <list type="bullet">
///   <item><description><c>http://unusedino.de/ec64/technical/formats/lbr.html</c> — Peter Schepers' LBR.TXT (rev 1.3, samples by Joe Forster/STA)</description></item>
///   <item><description><c>http://fileformats.archiveteam.org/wiki/LBR_(Commodore)</c> — format overview and the sample corpus</description></item>
/// </list>
/// </summary>
public sealed class CommodoreLbrFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "CommodoreLbr";
  public string DisplayName => "LBR (Commodore)";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: the writer reproduces real Library containers byte for byte; edits keep every member's
  // name, type and bytes.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".lbr";
  // .lbr belongs to the CP/M library descriptor; this one is found by its signature.
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("DWB "u8.ToArray(), Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Commodore 64 LBR library container (Library 7.0-10.0): stored PRG/SEQ/USR files";

  private static byte[] ReadAll(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var data = ReadAll(stream);
    return CommodoreLbr.Read(data).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Length, e.Length,
      $"Stored/{e.FileType}", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    foreach (var e in CommodoreLbr.Read(data)) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, data.AsSpan((int)e.Offset, (int)e.Length).ToArray());
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var members = FlatFiles(inputs).Select(f => (f.Name, 'P', f.Data)).ToList();
    output.Write(CommodoreLbr.Write(members));
  }

  /// <summary>Adds (or replaces by name) PRG members; every other member keeps its name, type and bytes.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var added = FilesOnly(inputs).ToList();
    var names = added.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
    Rewrite(archive, e => !names.Contains(e.Name), added.Select(f => (f.Name, 'P', f.Data)));
  }

  /// <summary>Removes members by name; every other member keeps its name, type and bytes.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    var names = entryNames.ToHashSet(StringComparer.Ordinal);
    Rewrite(archive, e => !names.Contains(e.Name), []);
  }

  private static void Rewrite(Stream archive, Func<CommodoreLbrEntry, bool> keep, IEnumerable<(string, char, byte[])> appended) {
    var data = ReadAll(archive);
    var members = CommodoreLbr.Read(data).Where(keep)
      .Select(e => (e.Name, e.FileType, data.AsSpan((int)e.Offset, (int)e.Length).ToArray())).ToList();
    members.AddRange(appended);
    var bytes = CommodoreLbr.Write(members);
    archive.Position = 0;
    archive.Write(bytes);
    archive.SetLength(bytes.Length);
    archive.Flush();
  }
}
