namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.Chapter1;

/// <summary>
/// Represents a chapter 1 short.
/// </summary>
public class Chapter1Short : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         6, 8,13,12,13,14,15,16,16, 4, 2, 4, 7, 6, 8,11,
        13,15,10, 4, 4, 8, 6, 8,11,14,17,11, 5, 6, 5, 6,
         8,12,14,17,11, 5, 5, 6, 5, 7,10,13,16,12, 6, 7,
         8, 7, 8,10,13,15,13, 8, 8, 7, 7, 8,10,12,15,15,
         7, 7, 5, 5, 7, 9,12,14,15, 8, 8, 6, 6, 7, 8,10,
        11,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
    public CodeBookMapType MapType { get; } = (CodeBookMapType)0;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
    public int QuantMin { get; } = 0;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
    public int QuantDelta { get; } = 0;
    /// <summary>
    /// Gets the quant.
    /// </summary>
    public int Quant { get; } = 0;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
    public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
    public int[] QuantList { get; } = null;
}
