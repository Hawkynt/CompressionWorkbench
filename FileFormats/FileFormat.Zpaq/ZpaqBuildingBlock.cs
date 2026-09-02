using System.Buffers.Binary;
using Compression.Registry;

namespace FileFormat.Zpaq;

/// <summary>
/// Exposes the ZPAQ context-mixing codec — the compression stage that the ZPAQ
/// journaling archiver wraps — as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// </summary>
/// <remarks>
/// <para>
/// ZPAQ (Matt Mahoney, "The ZPAQ Open Standard Format for Highly Compressed Data")
/// codes one bit at a time: an array of prediction components supplies a probability
/// for the next bit, a binary arithmetic coder codes the bit against that probability,
/// and a ZPAQL bytecode program (HCOMP) recomputes the context hashes in H[] after
/// every whole byte. <see cref="ZpaqlVm"/> runs the program and the components,
/// <see cref="ZpaqRangeEncoder"/> and <see cref="ZpaqRangeDecoder"/> do the coding,
/// and this block supplies one concrete configuration and drives the loop.
/// </para>
/// <para>
/// The configuration is four direct context models over hashed orders 1 to 4, built
/// by the HCOMP program in <see cref="Hcomp"/>. Per the format, the component context
/// is refined with the bits of the partly coded byte before each bit is coded; that
/// refinement belongs to the predictor rather than to HCOMP, so it is applied here.
/// </para>
/// <para>
/// The wire format is a 4-byte little-endian uncompressed length followed by the
/// coded bytes. An empty message is the header alone. The decoder learns how many
/// bytes to produce from the header, so the coded stream carries no end marker.
/// </para>
/// <para>
/// This is one configuration among the many the format allows, and it combines the
/// component predictions the way <see cref="ZpaqlVm.Predict"/> does, by averaging
/// rather than through a mixer chain. It is therefore a ZPAQ-model codec, not a
/// bit-exact producer of any particular reference archive's streams.
/// </para>
/// <para>
/// This block is the codec only. It is deliberately not the archive container:
/// <see cref="ZpaqWriter"/> and <see cref="ZpaqReader"/> model the journaling archive
/// (transactions, filenames, timestamps, SHA-1 index) and store their payloads
/// verbatim, so they carry no compression stage of their own to expose. Deduplication
/// and versioning, which the real container layers on top of those, are likewise not
/// implemented.
/// </para>
/// </remarks>
public sealed class ZpaqBuildingBlock : IBuildingBlock {

  /// <summary>Number of context models, one per hashed order 1..4.</summary>
  private const int ComponentCount = 4;

  /// <summary>Log2 of the counter table size of each context model.</summary>
  private const int ContextModelBits = 16;

  /// <summary>Counter adaptation rate of each context model.</summary>
  private const int ContextModelRate = 4;

  /// <summary>Log2 of the H[] context array size. Must cover <see cref="ComponentCount"/>.</summary>
  private const int HashArrayBits = 3;

  /// <summary>Log2 of the M[] history buffer size used by the HCOMP program.</summary>
  private const int HistoryBufferBits = 16;

  /// <summary>Size of the uncompressed-length header, in bytes.</summary>
  private const int SizeHeaderBytes = 4;

  /// <summary>Odd multiplier that spreads the partly coded byte across the low context bits.</summary>
  private const uint PartialByteMultiplier = 0x9E3779B1;

  /// <summary>
  /// HCOMP program: hashes orders 1 to 4 of the byte history into H[0..3].
  /// </summary>
  /// <remarks>
  /// On entry the ZPAQL A register holds the byte just coded, and R[0] is the write
  /// cursor into the M[] history buffer. Each order reaches one byte further back and
  /// folds it into the previous order's hash with the HASH instruction
  /// (A = (A + M[B] + 512) * 773), so order n hashes the last n bytes.
  /// <code>
  ///   R1=A  A=R0 B=A  A=R1 *B=A  A=R0 A++ R0=A   ; M[pos++] = byte
  ///   A=R0 A-=1 B=A  A=0   HASH  H0=A            ; order 1
  ///   A=R0 A-=2 B=A  A=H0  HASH  H1=A            ; order 2
  ///   A=R0 A-=3 B=A  A=H1  HASH  H2=A            ; order 3
  ///   A=R0 A-=4 B=A  A=H2  HASH  H3=A            ; order 4
  ///   HALT
  /// </code>
  /// </remarks>
  private static ReadOnlySpan<byte> Hcomp => [
    // Append the byte to the history buffer and advance the cursor.
    48, 1,           // R[1] = A
    49, 0,           // A = R[0]
    6,               // B = A
    49, 1,           // A = R[1]
    9,               // M[B] = A
    49, 0,           // A = R[0]
    2,               // A++
    48, 0,           // R[0] = A

    // Order 1: hash of M[pos-1] alone.
    49, 0, 3, 6,     // A = R[0]; A -= 1; B = A
    5,               // A = 0
    225,             // HASH
    82, 0,           // H[0] = A

    // Order 2: the order-1 hash folded with M[pos-2].
    49, 0, 3, 3, 6,  // A = R[0]; A -= 2; B = A
    83, 0,           // A = H[0]
    225,             // HASH
    82, 1,           // H[1] = A

    // Order 3: the order-2 hash folded with M[pos-3].
    49, 0, 3, 3, 3, 6,
    83, 1,           // A = H[1]
    225,             // HASH
    82, 2,           // H[2] = A

    // Order 4: the order-3 hash folded with M[pos-4].
    49, 0, 3, 3, 3, 3, 6,
    83, 2,           // A = H[2]
    225,             // HASH
    82, 3,           // H[3] = A

    0,               // HALT
  ];

  /// <inheritdoc/>
    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Zpaq";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "ZPAQ";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "ZPAQ context-mixing model over hashed orders 1-4, contexts built by the ZPAQL virtual machine, coded bit by bit by a carry-propagating binary range coder";
  /// <inheritdoc/>
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
    /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) {
    var output = new MemoryStream();
    Span<byte> header = stackalloc byte[SizeHeaderBytes];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    output.Write(header);

    if (data.Length == 0)
      return output.ToArray();

    var vm = CreateVm();
    var contexts = new uint[ComponentCount];
    var coder = new ZpaqRangeEncoder(output);

    foreach (var value in data) {
      var partial = 1;
      for (var shift = 7; shift >= 0; --shift) {
        var bit = (value >> shift) & 1;
        ApplyContexts(vm, contexts, partial);
        coder.EncodeBit(bit, vm.Predict());
        vm.Update(bit);
        partial = (partial << 1) | bit;
      }

      vm.RunHcomp(value);
      CaptureContexts(vm, contexts);
    }

    coder.Flush();
    return output.ToArray();
  }

  /// <inheritdoc/>
    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (originalSize == 0)
      return [];

    var payload = data[SizeHeaderBytes..].ToArray();
    var coder = new ZpaqRangeDecoder(payload, 0);

    var vm = CreateVm();
    var contexts = new uint[ComponentCount];
    var result = new byte[originalSize];

    for (var index = 0; index < result.Length; ++index) {
      var partial = 1;
      for (var shift = 7; shift >= 0; --shift) {
        ApplyContexts(vm, contexts, partial);
        var bit = coder.DecodeBit(vm.Predict());
        vm.Update(bit);
        partial = (partial << 1) | bit;
      }

      var value = (byte)partial;
      result[index] = value;
      vm.RunHcomp(value);
      CaptureContexts(vm, contexts);
    }

    return result;
  }

  /// <summary>
  /// Builds the VM for one message: four context models fed by <see cref="Hcomp"/>.
  /// </summary>
  private static ZpaqlVm CreateVm() {
    var components = new Component[ComponentCount];
    for (var i = 0; i < components.Length; ++i)
      components[i] = new Component {
        Type = ComponentType.Cm,
        Param1 = ContextModelBits,
        Param2 = ContextModelRate,
      };

    return new ZpaqlVm(Hcomp.ToArray(), [], HashArrayBits, HistoryBufferBits, components);
  }

  /// <summary>
  /// Records the per-byte context hashes the HCOMP program has just written, so
  /// they can be re-derived for each bit of the next byte.
  /// </summary>
  private static void CaptureContexts(ZpaqlVm vm, uint[] contexts) {
    for (var i = 0; i < contexts.Length; ++i)
      contexts[i] = vm.H[i];
  }

  /// <summary>
  /// Refines each component's context with the bits of the partly coded byte.
  /// </summary>
  /// <remarks>
  /// The multiplier is odd, so within one model no two prefixes of the same byte
  /// can land on the same counter. The component then indexes its table by the low
  /// <see cref="ContextModelBits"/> bits of the offset hash; those bits are the same
  /// whether or not the sum is first reduced modulo 2^32, because the table size
  /// divides 2^32.
  /// </remarks>
  /// <param name="vm">The VM whose H[] array supplies the component contexts.</param>
  /// <param name="contexts">The per-byte hashes captured by <see cref="CaptureContexts"/>.</param>
  /// <param name="partial">The bits coded so far in the current byte, preceded by a leading 1.</param>
  private static void ApplyContexts(ZpaqlVm vm, uint[] contexts, int partial) {
    for (var i = 0; i < contexts.Length; ++i)
      vm.H[i] = contexts[i] + (uint)partial * PartialByteMultiplier;
  }
}
