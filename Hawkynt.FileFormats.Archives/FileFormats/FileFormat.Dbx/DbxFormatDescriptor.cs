#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dbx;

/// <summary>
/// Outlook Express 5/6 mailbox (<c>.dbx</c>), read-only. A mail or news folder lists its messages as
/// <c>message_NNNN.eml</c> (the stored RFC 822 text, exactly as undbx saves it); <c>Folders.dbx</c>
/// lists its folder tree as <c>folders.ini</c>.
///
/// References:
/// <list type="bullet">
///   <item><description>Arne Schloh, "Outlook Express DBX file format" — the reverse-engineered layout</description></item>
///   <item><description><c>https://github.com/jamesjohnmcguire/DbxOutlookExpress</c> — DbxOutlookExpress (MIT), whose reader this follows</description></item>
///   <item><description><c>https://sourceforge.net/projects/undbx/</c> — undbx, the extraction oracle</description></item>
/// </list>
/// </summary>
public sealed class DbxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Dbx";
  public string DisplayName => "Outlook Express DBX";
  public FormatCategory Category => FormatCategory.Archive;
  // Read-only: no Outlook Express is available to prove that a mailbox we would write opens.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dbx";
  public IReadOnlyList<string> Extensions => [".dbx"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xCF, 0xAD, 0x12, 0xFE, 0x00, 0xFD, 0x74, 0x6F], Confidence: 0.9, Mask: [0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0xFF, 0xFF, 0xFF]),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("dbx", "Outlook Express")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Outlook Express 5/6 mailbox: messages as .eml, Folders.dbx as folders.ini (read-only)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => [.. Entries(new DbxReader(ReadAll(stream))).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.Length, e.Data.Length, "stored", false, false, null))];

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var (name, data) in Entries(new DbxReader(ReadAll(stream))))
      if (files == null || MatchesFilter(name, files))
        WriteFile(outputDir, name, data);
  }

  private static IEnumerable<(string Name, byte[] Data)> Entries(DbxReader reader) {
    foreach (var m in reader.ReadMessages())
      yield return ($"message_{m.Index:D4}.eml", m.Data);
    var folders = reader.ReadFolders();
    if (folders.Count == 0) yield break;
    var ini = new StringBuilder();
    foreach (var f in folders)
      ini.Append(CultureInfo.InvariantCulture, $"[Folder{f.Id}]\nname={f.Name}\nparent={f.ParentId}\nfile={f.FileName}\n\n");
    yield return ("folders.ini", Encoding.UTF8.GetBytes(ini.ToString()));
  }

  private static byte[] ReadAll(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
