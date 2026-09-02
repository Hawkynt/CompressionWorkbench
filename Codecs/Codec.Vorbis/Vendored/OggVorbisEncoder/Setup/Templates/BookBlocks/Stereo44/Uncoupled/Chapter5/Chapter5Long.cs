namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Uncoupled.Chapter5;

/// <summary>
/// Represents a chapter 5 long.
/// </summary>
public class Chapter5Long : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         3, 8,13,12,14,12,16,11,13,14, 5, 4, 5, 6, 7, 8,
        10, 9,12,15,10, 5, 5, 5, 6, 8, 9, 9,13,15,10, 5,
         5, 6, 6, 7, 8, 8,11,13,12, 7, 5, 6, 4, 6, 7, 7,
        11,14,11, 7, 7, 6, 6, 6, 7, 6,10,14,14, 9, 8, 8,
         6, 7, 7, 7,11,16,11, 8, 8, 7, 6, 6, 7, 4, 7,12,
        10,10,12,10,10, 9,10, 5, 6, 9,10,12,15,13,14,14,
        14, 8, 7, 8,
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
