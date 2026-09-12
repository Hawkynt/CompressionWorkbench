using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FileFormat.Tar;

namespace FileFormat.Ova;

/// <summary>
/// Builds a spec-correct OVA (Open Virtualization Appliance): an uncompressed
/// POSIX/ustar TAR whose first entry is the <c>.ovf</c> XML descriptor, followed
/// by the disk image(s), followed by a generated <c>.mf</c> manifest carrying a
/// correct <c>SHA256(file)= &lt;hex&gt;</c> line for every other member.
/// </summary>
/// <remarks>
/// When no <c>.ovf</c> is supplied a minimal well-formed OVF envelope is
/// synthesised that references each disk via its <c>ovf:href</c>, so the
/// appliance is still importable. The TAR is written with blocking factor 1 (the
/// minimal terminated length, two zero blocks) so the output is compact and
/// deterministic.
/// </remarks>
public sealed class OvaWriter {
  /// <summary>A file to place into the OVA.</summary>
  /// <param name="Name">The member name as it appears in the archive.</param>
  /// <param name="Data">The member's raw bytes.</param>
  public sealed record OvaInput(string Name, byte[] Data);

  private readonly List<OvaInput> _ovf = [];
  private readonly List<OvaInput> _disks = [];
  private readonly List<OvaInput> _extras = [];
  private string _ovfBaseName = "appliance";

  /// <summary>The hash algorithm written into the generated manifest. Defaults to SHA256.</summary>
  public string ManifestAlgorithm { get; set; } = "SHA256";

  /// <summary>Adds a member, classifying it as the OVF, a disk, or an extra (cert, etc.).</summary>
  public OvaWriter Add(string name, byte[] data) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(data);
    var input = new OvaInput(name, data);
    if (name.EndsWith(".ovf", StringComparison.OrdinalIgnoreCase)) {
      this._ovf.Add(input);
      this._ovfBaseName = Path.GetFileNameWithoutExtension(name);
    } else if (name.EndsWith(".mf", StringComparison.OrdinalIgnoreCase)) {
      // A manifest is always regenerated from the actual member bytes; an
      // externally supplied one is dropped so we never emit a stale digest.
    } else if (OvaReader.IsDisk(name)) {
      this._disks.Add(input);
    } else {
      this._extras.Add(input);
    }
    return this;
  }

  /// <summary>
  /// Writes the OVA to <paramref name="output"/>. Order: the OVF first (supplied
  /// or synthesised), then disks, then extras, then the generated manifest.
  /// </summary>
  public void Write(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (this._disks.Count == 0 && this._ovf.Count == 0)
      throw new InvalidOperationException("An OVA needs at least one disk image or an .ovf descriptor.");

    var ovf = this._ovf.Count > 0
      ? this._ovf[0]
      : new OvaInput(this._ovfBaseName + ".ovf",
          Encoding.UTF8.GetBytes(SynthesizeOvf(this._ovfBaseName, this._disks)));

    // Members written to the TAR in canonical OVA order (manifest excluded; it
    // is generated last from these very bytes).
    var ordered = new List<OvaInput> { ovf };
    ordered.AddRange(this._disks);
    ordered.AddRange(this._extras);

    var manifestName = this._ovfBaseName + ".mf";
    var manifest = Encoding.UTF8.GetBytes(BuildManifest(this.ManifestAlgorithm, ordered));

    // Blocking factor 1: minimal valid ustar (no record padding beyond the two
    // end-of-archive zero blocks) for compact, deterministic output.
    using var w = new TarWriter(output, leaveOpen: true, format: TarHeaderFormat.Ustar, blockingFactor: 1);
    foreach (var m in ordered)
      w.AddEntry(new TarEntry { Name = m.Name, Size = m.Data.Length }, m.Data);
    w.AddEntry(new TarEntry { Name = manifestName, Size = manifest.Length }, manifest);
    w.Finish();
  }

  /// <summary>Builds an OVA into a fresh byte array.</summary>
  public byte[] ToArray() {
    using var ms = new MemoryStream();
    this.Write(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Builds the <c>.mf</c> manifest text: one <c>ALGO(name)= hex</c> line per
  /// member, in the order the members appear in the archive.
  /// </summary>
  internal static string BuildManifest(string algorithm, IReadOnlyList<OvaInput> members) {
    var sb = new StringBuilder();
    foreach (var m in members)
      sb.Append(CultureInfo.InvariantCulture,
        $"{algorithm}({m.Name})= {ComputeHex(algorithm, m.Data)}\n");
    return sb.ToString();
  }

  /// <summary>
  /// Produces a minimal but well-formed OVF 1.0 envelope referencing each disk
  /// via <c>ovf:href</c>. One <c>&lt;File&gt;</c>, <c>&lt;Disk&gt;</c> and a
  /// matching <c>StorageItem</c> are emitted per disk so importers can map
  /// each href to a virtual disk.
  /// </summary>
  internal static string SynthesizeOvf(string systemId, IReadOnlyList<OvaInput> disks) {
    var ovfNs = "http://schemas.dmtf.org/ovf/envelope/1";
    var rasdNs = "http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData";
    var vssdNs = "http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_VirtualSystemSettingData";

    var sb = new StringBuilder();
    sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
    sb.Append(CultureInfo.InvariantCulture,
      $"<Envelope xmlns=\"{ovfNs}\" xmlns:ovf=\"{ovfNs}\" xmlns:rasd=\"{rasdNs}\" xmlns:vssd=\"{vssdNs}\">\n");

    sb.Append("  <References>\n");
    for (var i = 0; i < disks.Count; ++i)
      sb.Append(CultureInfo.InvariantCulture,
        $"    <File ovf:id=\"file{i + 1}\" ovf:href=\"{XmlEscape(disks[i].Name)}\" ovf:size=\"{disks[i].Data.Length}\"/>\n");
    sb.Append("  </References>\n");

    sb.Append("  <DiskSection>\n");
    sb.Append("    <Info>Virtual disk information</Info>\n");
    for (var i = 0; i < disks.Count; ++i)
      sb.Append(CultureInfo.InvariantCulture,
        $"    <Disk ovf:diskId=\"vmdisk{i + 1}\" ovf:fileRef=\"file{i + 1}\" ovf:capacity=\"{disks[i].Data.Length}\" ovf:format=\"http://www.vmware.com/interfaces/specifications/vmdk.html#streamOptimized\"/>\n");
    sb.Append("  </DiskSection>\n");

    sb.Append(CultureInfo.InvariantCulture, $"  <VirtualSystem ovf:id=\"{XmlEscape(systemId)}\">\n");
    sb.Append(CultureInfo.InvariantCulture, $"    <Info>A virtual machine: {XmlEscape(systemId)}</Info>\n");
    sb.Append(CultureInfo.InvariantCulture, $"    <Name>{XmlEscape(systemId)}</Name>\n");
    sb.Append("    <OperatingSystemSection ovf:id=\"100\">\n");
    sb.Append("      <Info>The operating system installed</Info>\n");
    sb.Append("      <Description>Other</Description>\n");
    sb.Append("    </OperatingSystemSection>\n");
    sb.Append("    <VirtualHardwareSection>\n");
    sb.Append("      <Info>Virtual hardware requirements</Info>\n");
    for (var i = 0; i < disks.Count; ++i) {
      sb.Append("      <Item>\n");
      sb.Append(CultureInfo.InvariantCulture, $"        <rasd:ElementName>disk{i + 1}</rasd:ElementName>\n");
      sb.Append(CultureInfo.InvariantCulture, $"        <rasd:HostResource>ovf:/disk/vmdisk{i + 1}</rasd:HostResource>\n");
      sb.Append(CultureInfo.InvariantCulture, $"        <rasd:InstanceID>{i + 1}</rasd:InstanceID>\n");
      sb.Append("        <rasd:ResourceType>17</rasd:ResourceType>\n");
      sb.Append("      </Item>\n");
    }
    sb.Append("    </VirtualHardwareSection>\n");
    sb.Append("  </VirtualSystem>\n");
    sb.Append("</Envelope>\n");
    return sb.ToString();
  }

  private static string XmlEscape(string s)
    => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

  internal static string ComputeHex(string algorithm, byte[] data) {
    var hash = algorithm.Replace("-", "").ToUpperInvariant() switch {
      "SHA1" => SHA1.HashData(data),
      "SHA256" => SHA256.HashData(data),
      "SHA512" => SHA512.HashData(data),
      "MD5" => MD5.HashData(data),
      _ => SHA256.HashData(data),
    };
    return Convert.ToHexStringLower(hash);
  }
}
