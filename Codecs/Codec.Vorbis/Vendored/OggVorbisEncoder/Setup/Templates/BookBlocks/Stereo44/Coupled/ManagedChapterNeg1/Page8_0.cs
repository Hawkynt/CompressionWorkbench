namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.ManagedChapterNeg1;

/// <summary>
/// Represents a page 8 0.
/// </summary>
public class Page8_0 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         1, 4, 4,12,11,13,13,14,14, 4, 7, 7,11,13,14,14,
        14,14, 3, 8, 3,14,14,14,14,14,14,14,10,12,14,14,
        14,14,14,14,14,14, 5,14, 8,14,14,14,14,14,12,14,
        13,14,14,14,14,14,14,14,13,14,10,14,14,14,14,14,
        14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,14,
        14,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
    public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
    public int QuantMin { get; } = -516186112;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
    public int QuantDelta { get; } = 1627103232;
    /// <summary>
    /// Gets the quant.
    /// </summary>
    public int Quant { get; } = 4;
    /// <summary>
    /// Gets the quant sequence p.
    /// </summary>
    public int QuantSequenceP { get; } = 0;

    /// <summary>
    /// Gets the quant list.
    /// </summary>
    public int[] QuantList { get; } = {
        4,
        3,
        5,
        2,
        6,
        1,
        7,
        0,
        8,
    };
}
