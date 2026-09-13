#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.ZipContainer;

namespace FileFormat.Crx;

/// <summary>
/// Chrome extension package (CRX3) — "Cr24" magic + version + protobuf SignedData header followed by the ZIP payload.
///
/// <para>The envelope is the only thing that separates a CRX from the rest of the ZIP-container
/// family, and it is expressed as the two hooks the base descriptor provides:
/// <see cref="PrepareRead"/> validates and skips it on the way in,
/// <see cref="WriteContainerPrefix"/> emits it on the way out.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://chromium.googlesource.com/chromium/src/+/main/components/crx_file/</c> — Chromium crx_file component — <c>crx3.proto</c> defines the header</description></item>
///   <item><description><c>https://developer.chrome.com/docs/extensions</c> — Chrome extensions documentation portal</description></item>
/// </list>
/// </summary>
public sealed class CrxFormatDescriptor : ZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "CRX";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".crx"];

  /// <inheritdoc />
  public override IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'C', (byte)'r', (byte)'2', (byte)'4'], Confidence: 0.95)
  ];

  /// <inheritdoc />
  public override string Description =>
    "Chrome extension package (CRX3 header + ZIP). The CRX3 header carries a " +
    "SignedData protobuf whose signed_header_data + ZIP body bytes are covered by " +
    "RSA/ECDSA signatures in a repeated KeyProof field; any in-place mutation of " +
    "the trailing ZIP invalidates every signature. CRX therefore advertises " +
    "CanCreate (we emit an empty SignedData — not browser-loadable, but a valid " +
    "container) but does not implement IArchiveModifiable.";

  /// <summary>
  /// Validates the "Cr24" magic and positions the stream on the ZIP payload that follows the
  /// variable-length CrxFileHeader.
  /// </summary>
  protected override Stream PrepareRead(Stream stream) {
    var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
    var magic = reader.ReadBytes(4);
    if (magic is not [(byte)'C', (byte)'r', (byte)'2', (byte)'4'])
      throw new InvalidDataException("Not a CRX file.");
    var version = reader.ReadUInt32();
    var headerLen = reader.ReadUInt32();
    stream.Position = 12 + headerLen;
    return stream;
  }

  /// <summary>
  /// Writes a minimal CRX3 envelope: "Cr24" magic, version 3, empty signed header.
  /// Roundtrips through our reader. NOTE: not browser-loadable because the
  /// CrxFileHeader protobuf is empty (no signing keys/signatures). Real signing
  /// requires a private key and is out of scope.
  /// </summary>
  protected override void WriteContainerPrefix(Stream output) {
    output.Write([(byte)'C', (byte)'r', (byte)'2', (byte)'4']);
    Span<byte> u32 = stackalloc byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(u32, 3);
    output.Write(u32);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(u32, 0);
    output.Write(u32);
  }
}
