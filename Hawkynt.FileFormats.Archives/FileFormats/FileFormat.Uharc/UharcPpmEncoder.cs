namespace FileFormat.Uharc;

/// <summary>
/// The encoder side of the PPM stream (docs/UHARC-ON-DISK.md, "PPM" and "Writing"): it runs the
/// decoder's <see cref="UharcPpmModel{TCoder}"/> over the model symbols of the input
/// (<see cref="UharcPpmSymbols"/>), which then answers every coding decision from the symbol that
/// comes next — a predicted copy is taken when it reproduces the symbols past its length floor,
/// every literal stage codes the symbol or escapes. Leading text files make a text stream with the
/// word transform, the rest is binary; each block gets its multimedia class
/// (<see cref="UharcMediaClass"/>).
/// </summary>
internal static class UharcPpmEncoder {

  /// <summary>Packs <paramref name="data"/> as PPM stream <paramref name="method"/> (24..39).</summary>
  /// <param name="data">The stream.</param>
  /// <param name="method">The PPM method.</param>
  /// <param name="fileEnds">Where each file ends in the stream; leading text files become a text stream when that is smaller.</param>
  public static byte[] Encode(byte[] data, int method, IReadOnlyList<long> fileEnds)
    => UharcPpmSymbols.EncodeSmaller(data, fileEnds, (d, ends, plan) => Encode(d, method, ends, plan));

  /// <summary>Packs <paramref name="data"/> from the given symbols (a text or a binary stream).</summary>
  internal static byte[] Encode(byte[] data, int method, IReadOnlyList<long> fileEnds, UharcPpmSymbols plan) {
    if (method is < UharcMethods.PpmFirst or > UharcMethods.PpmLast)
      throw new ArgumentOutOfRangeException(nameof(method));
    if (data.Length == 0)
      return [];

    var coder = new UharcRangeEncoder();
    var model = new UharcPpmModel<UharcEncoding>(new(coder), method) { Source = data, Plan = plan };
    var output = new byte[data.Length];
    model.Run(output, fileEnds);
    if (!output.AsSpan().SequenceEqual(data))
      throw new InvalidOperationException("UHARC: PPM encoder lost track of its model.");
    return coder.Finish();
  }
}
