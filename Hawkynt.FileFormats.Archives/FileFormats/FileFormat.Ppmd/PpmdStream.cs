using System.Buffers.Binary;
using System.Text;
using Compression.Core.Entropy.Ppmd;

namespace FileFormat.Ppmd;

/// <summary>
/// Dmitry Shkarin's PPMd file format (<c>.pmd</c>), as his PPMd var.H and var.I programs write it.
/// </summary>
/// <remarks>
/// <para>
/// A file is one or more members. Each member is a 16-byte little-endian header, the file name, and
/// a PPMd stream that ends with the model's end marker:
/// </para>
/// <list type="table">
///   <item><term>0</term><description>signature <c>0x84ACAF8F</c></description></item>
///   <item><term>4</term><description>file attributes</description></item>
///   <item><term>8</term><description>info: order − 1 (bits 0-3), memory in MiB − 1 (bits 4-11),
///   variant − 'A' (bits 12-15)</description></item>
///   <item><term>10</term><description>name length (bits 0-8); var.I keeps the model restoration
///   method in bits 14-15</description></item>
///   <item><term>12</term><description>DOS time, then DOS date</description></item>
/// </list>
/// <para>
/// Variant H is PPMd var.H (<see cref="Ppmd7"/>), variant I is var.I revision 1 (<see cref="Ppmd8"/>);
/// both use Dmitry Subbotin's carry-less range coder. Reading accepts both and writes the members'
/// data one after another. Writing produces one var.I member. Other variants and var.I's "freeze"
/// restoration method are refused.
/// </para>
/// </remarks>
public static class PpmdStream {

  /// <summary>The member signature.</summary>
  public const uint Signature = 0x84ACAF8F;
  /// <summary>Default model order for writing.</summary>
  public const int DefaultOrder = 6;
  /// <summary>Default model memory for writing, in MiB.</summary>
  public const int DefaultMemoryMb = 16;
  /// <summary>Lowest model order the header can carry.</summary>
  public const int MinOrder = 2;
  /// <summary>Highest model order the header can carry.</summary>
  public const int MaxOrder = 16;

  private const int HeaderSize = 16;
  private const int VariantH = 'H' - 'A';
  private const int VariantI = 'I' - 'A';
  private const string MemberName = "data";

  /// <summary>Writes <paramref name="input"/> as one var.I member with the default order and memory.</summary>
  public static void Compress(Stream input, Stream output) => Compress(input, output, DefaultOrder);

  /// <summary>Writes <paramref name="input"/> as one var.I member of model order <paramref name="order"/>.</summary>
  /// <exception cref="ArgumentOutOfRangeException"><paramref name="order"/> is outside 2..16.</exception>
  public static void Compress(Stream input, Stream output, int order) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentOutOfRangeException.ThrowIfLessThan(order, MinOrder);
    ArgumentOutOfRangeException.ThrowIfGreaterThan(order, MaxOrder);

    Span<byte> header = stackalloc byte[HeaderSize];
    BinaryPrimitives.WriteUInt32LittleEndian(header, Signature);
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 0x20);   // FILE_ATTRIBUTE_ARCHIVE, as PPMd writes for a plain file
    BinaryPrimitives.WriteUInt16LittleEndian(header[8..], (ushort)((order - 1) | ((DefaultMemoryMb - 1) << 4) | (VariantI << 12)));
    BinaryPrimitives.WriteUInt16LittleEndian(header[10..], (ushort)(MemberName.Length | ((int)Ppmd8RestoreMethod.Restart << 14)));
    BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 0);      // 00:00:00
    BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 0x21);   // 1980-01-01
    output.Write(header);
    output.Write(Encoding.ASCII.GetBytes(MemberName));

    var model = new Ppmd8(DefaultMemoryMb << 20, order, Ppmd8RestoreMethod.Restart);
    model.StartEncoding(output);
    var buffer = new byte[81920];
    int read;
    while ((read = input.Read(buffer)) > 0)
      foreach (var symbol in buffer.AsSpan(0, read))
        model.EncodeSymbol(symbol);
    model.EncodeSymbol(Ppmd8.EndMarker);
    model.FlushEncoder();
  }

  /// <summary>Decodes every member of a <c>.pmd</c> file, writing their data in order.</summary>
  /// <exception cref="InvalidDataException">
  /// A header is truncated or carries the wrong signature, an unsupported variant or restoration
  /// method; or a stream is corrupt or ends before its end marker.
  /// </exception>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    using var copy = new MemoryStream();
    input.CopyTo(copy);
    var data = copy.ToArray();

    if (data.Length == 0)
      throw new InvalidDataException("PPMd: empty file.");

    var position = 0;
    while (position < data.Length)
      position = DecodeMember(data, position, output);
  }

  private static int DecodeMember(byte[] data, int position, Stream output) {
    if (data.Length - position < HeaderSize)
      throw new InvalidDataException("PPMd: truncated member header.");

    var header = data.AsSpan(position, HeaderSize);
    if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Signature)
      throw new InvalidDataException("PPMd: bad member signature.");

    var info = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
    var nameField = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
    var order = (info & 0x0F) + 1;
    var memorySize = (((info >> 4) & 0xFF) + 1) << 20;
    var variant = info >> 12;
    var nameLength = nameField & 0x1FF;
    position += HeaderSize + nameLength;
    if (position > data.Length)
      throw new InvalidDataException("PPMd: truncated member name.");
    if (order < MinOrder)
      throw new InvalidDataException($"PPMd: model order {order} is below {MinOrder}.");

    return variant switch {
      VariantH => DecodeVariantH(data, position, order, memorySize, output),
      VariantI => DecodeVariantI(data, position, order, memorySize, (nameField >> 14) switch {
        0 => Ppmd8RestoreMethod.Restart,
        1 => Ppmd8RestoreMethod.CutOff,
        _ => throw new InvalidDataException("PPMd: var.I members with the 'freeze' restoration method (-r2) are not supported."),
      }, output),
      _ => throw new InvalidDataException($"PPMd: variant {(char)('A' + variant)} is not supported (only H and I)."),
    };
  }

  private static int DecodeVariantH(byte[] data, int position, int order, int memorySize, Stream output) {
    var overrun = 0;
    var rc = new RarPpmdRangeDecoder(() => {
      if (position < data.Length)
        return data[position++];
      ++overrun;
      return 0;
    });
    var model = new Ppmd7();
    model.Allocate((uint)memorySize);
    model.Initialize(order);
    var buffer = new MemoryStream();
    for (;;) {
      var symbol = model.DecodeSymbol(rc);
      if (overrun > 0)
        throw new InvalidDataException("PPMd: var.H stream is truncated.");
      if (symbol == -1)
        break;
      if (symbol < 0)
        throw new InvalidDataException("PPMd: var.H stream is corrupt.");
      buffer.WriteByte((byte)symbol);
    }
    buffer.WriteTo(output);
    return position;
  }

  private static int DecodeVariantI(byte[] data, int position, int order, int memorySize, Ppmd8RestoreMethod restore, Stream output) {
    var model = new Ppmd8(memorySize, order, restore);
    if (!model.StartDecoding(data, position))
      throw new InvalidDataException("PPMd: var.I stream does not start as an encoder starts it.");
    var buffer = new MemoryStream();
    for (;;) {
      var symbol = model.DecodeSymbol();
      if (model.InputOverrun > 0)
        throw new InvalidDataException("PPMd: var.I stream is truncated.");
      if (symbol == Ppmd8.EndMarker)
        break;
      if (symbol < 0)
        throw new InvalidDataException("PPMd: var.I stream is corrupt.");
      buffer.WriteByte((byte)symbol);
    }
    buffer.WriteTo(output);
    return model.InputPosition;
  }
}
