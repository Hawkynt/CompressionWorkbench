using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Xml;
using FileFormat.Cab;

namespace FileFormat.WixBurn;

/// <summary>A payload of a WiX Burn bundle.</summary>
/// <param name="FileName">"UX/..." for the bootstrapper application, "AttachedContainer/..." for the chained packages and their payloads, as WiX's dark.exe extracts them.</param>
/// <param name="Container">Index of the attached container holding it (0 = the UX container).</param>
/// <param name="SourcePath">The payload's name inside its container's cabinet ("u0", "a3", ...).</param>
/// <param name="Size">Its size as the manifest records it, or -1 when the manifest gives none.</param>
/// <param name="Hash">The manifest's hash of it (SHA-1 in WiX 3, SHA-512 later), or null.</param>
public sealed record WixBurnEntry(string FileName, int Container, string SourcePath, long Size, byte[]? Hash);

/// <summary>
/// Reads WiX Burn bundles: the engine exe's ".wixburn" section names the containers appended to
/// it; container 0 is the bootstrapper-application cabinet, whose entry "0" is the Burn
/// manifest; the others hold the chained packages. The manifest maps every cabinet entry to the
/// payload's real path and hash.
/// </summary>
/// <remarks>
/// Layout written down in docs/WIX-BURN-ON-DISK.md from WiX bundles built by candle/light 3.14
/// and checked against what dark.exe -x extracts; every payload is verified against the hash
/// the manifest records for it.
/// </remarks>
public sealed class WixBurnReader {
  /// <summary>BURN_SECTION_MAGIC.</summary>
  public const uint SectionMagic = 0x00F14300;

  private readonly Stream _stream;
  private readonly long[] _containerOffsets;
  private readonly long[] _containerSizes;
  private readonly Dictionary<int, CabReader> _cabinets = [];
  private readonly List<WixBurnEntry> _entries = [];

  /// <summary>Gets the section format version (2 for WiX 3 and later).</summary>
  public uint SectionVersion { get; }

  /// <summary>Gets the bundle id from the section header.</summary>
  public Guid BundleId { get; }

  /// <summary>Gets the size of the engine stub the containers follow.</summary>
  public long StubSize { get; }

  /// <summary>Gets the raw Burn manifest XML.</summary>
  public byte[] Manifest { get; }

  /// <summary>Gets the payloads, UX first.</summary>
  public IReadOnlyList<WixBurnEntry> Entries => this._entries;

  /// <summary>Gets the number of containers (UX included).</summary>
  public int ContainerCount => this._containerOffsets.Length;

  /// <summary>Opens a bundle.</summary>
  /// <exception cref="InvalidDataException">No ".wixburn" section, or one that does not parse.</exception>
  public WixBurnReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._stream = stream;
    var section = FindSection(stream) ?? throw new InvalidDataException("No .wixburn section.");
    if (section.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(section) != SectionMagic)
      throw new InvalidDataException("The .wixburn section does not start with the Burn magic.");
    this.SectionVersion = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(4));
    this.BundleId = new Guid(section.AsSpan(8, 16));
    this.StubSize = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(24));
    var format = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(40));
    var count = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(44));
    if (format != 1)
      throw new NotSupportedException($"Burn container format {format} is not supported (1 = cabinet).");
    if (count == 0 || count > 1024 || 48 + 4 * count > section.Length)
      throw new InvalidDataException("Implausible Burn container count.");
    this._containerOffsets = new long[count];
    this._containerSizes = new long[count];
    var at = this.StubSize;
    for (var i = 0; i < count; ++i) {
      var size = BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(48 + 4 * i));
      if (at + size > stream.Length)
        throw new InvalidDataException("A Burn container runs past the end of the file.");
      this._containerOffsets[i] = at;
      this._containerSizes[i] = size;
      at += size;
    }

    var ux = this.Cabinet(0);
    var manifestEntry = ux.Entries.FirstOrDefault(e => e.FileName == "0")
      ?? throw new InvalidDataException("The UX container has no Burn manifest (entry \"0\").");
    this.Manifest = ux.ExtractEntry(manifestEntry);
    this._entries.Add(new WixBurnEntry("UX/manifest.xml", 0, "0", this.Manifest.Length, null));
    this.ReadManifest();
  }

  private void ReadManifest() {
    var doc = new XmlDocument { XmlResolver = null };
    using (var ms = new MemoryStream(this.Manifest))
    using (var xr = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
      doc.Load(xr);

    // Attached containers: Container elements with AttachedIndex (1 = the first after the UX cab).
    var containerIndex = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (XmlElement c in doc.GetElementsByTagName("*").OfType<XmlElement>().Where(e => e.LocalName == "Container")) {
      if (c.GetAttribute("Attached") is "yes" && int.TryParse(c.GetAttribute("AttachedIndex"), out var index))
        containerIndex[c.GetAttribute("Id")] = index;
    }

    var seen = new HashSet<(int, string)>();
    foreach (XmlElement p in doc.GetElementsByTagName("*").OfType<XmlElement>().Where(e => e.LocalName == "Payload")) {
      if (p.GetAttribute("Packaging") is not "embedded")
        continue; // downloaded at install time, not in the bundle
      var source = p.GetAttribute("SourcePath");
      var filePath = p.GetAttribute("FilePath").Replace('\\', '/');
      var inUx = p.ParentNode is XmlElement { LocalName: "UX" };
      int container;
      if (inUx)
        container = 0;
      else if (!containerIndex.TryGetValue(p.GetAttribute("Container"), out container))
        continue; // a detached container ships beside the bundle
      if (!seen.Add((container, source)))
        continue;
      var size = long.TryParse(p.GetAttribute("FileSize"), out var s) ? s : -1;
      var hash = p.GetAttribute("Hash") is { Length: > 0 } h && h.Length % 2 == 0 ? Convert.FromHexString(h) : null;
      var name = (inUx ? "UX/" : "AttachedContainer/") + string.Join('/', filePath.Split('/').Where(seg => seg is not ("" or "." or "..")));
      this._entries.Add(new WixBurnEntry(name, container, source, size, hash));
    }
  }

  /// <summary>Returns a payload's bytes, checked against the manifest's hash.</summary>
  public byte[] Extract(WixBurnEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Container == 0 && entry.SourcePath == "0")
      return this.Manifest;
    var cab = this.Cabinet(entry.Container);
    var item = cab.Entries.FirstOrDefault(e => e.FileName == entry.SourcePath)
      ?? throw new InvalidDataException($"Container {entry.Container} has no entry '{entry.SourcePath}' for {entry.FileName}.");
    var data = cab.ExtractEntry(item);
    if (entry.Hash != null) {
      byte[] actual = entry.Hash.Length switch {
        20 => SHA1.HashData(data),
        32 => SHA256.HashData(data),
        64 => SHA512.HashData(data),
        _ => entry.Hash,
      };
      if (!actual.AsSpan().SequenceEqual(entry.Hash))
        throw new InvalidDataException($"Burn payload {entry.FileName}: hash mismatch.");
    }
    return data;
  }

  private CabReader Cabinet(int index) {
    if (this._cabinets.TryGetValue(index, out var cab))
      return cab;
    if (index < 0 || index >= this._containerOffsets.Length)
      throw new InvalidDataException($"Burn container {index} does not exist.");
    var bytes = new byte[this._containerSizes[index]];
    this._stream.Position = this._containerOffsets[index];
    this._stream.ReadExactly(bytes);
    cab = new CabReader(new MemoryStream(bytes, writable: false));
    this._cabinets[index] = cab;
    return cab;
  }

  /// <summary>Returns the raw bytes of the ".wixburn" PE section, or null.</summary>
  public static byte[]? FindSection(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    try {
      if (stream.Length < 0x40)
        return null;
      var head = new byte[4096];
      stream.Position = 0;
      var n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
      if (head[0] != 'M' || head[1] != 'Z')
        return null;
      var pe = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(0x3C));
      if (pe < 0 || pe + 24 > n || BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(pe)) != 0x00004550)
        return null;
      var sections = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(pe + 6));
      var table = pe + 24 + BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(pe + 20));
      for (var i = 0; i < sections && table + 40 * i + 40 <= n; ++i) {
        var s = head.AsSpan(table + 40 * i);
        if (!s[..8].SequenceEqual(".wixburn"u8))
          continue;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(s[16..]);
        var ptr = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]);
        if (size > 1 << 20 || ptr + (long)size > stream.Length)
          return null;
        var data = new byte[size];
        stream.Position = ptr;
        stream.ReadExactly(data);
        return data;
      }
      return null;
    } catch (Exception ex) when (ex is IOException or ArgumentException) {
      return null;
    }
  }
}
