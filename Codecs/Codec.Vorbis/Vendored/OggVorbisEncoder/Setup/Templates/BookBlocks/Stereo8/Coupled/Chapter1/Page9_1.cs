namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo8.Coupled.Chapter1;

/// <summary>
/// Represents a page 9 1.
/// </summary>
public class Page9_1 : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         1, 4, 4, 5, 5, 7, 7, 9, 9,11,11,12,12,13,13, 6,
         5, 5, 6, 6, 9, 9,10,10,12,12,12,13,15,14, 6, 5,
         5, 7, 7, 9, 9,10,10,12,12,12,13,14,13,17, 7, 7,
         8, 8,10,10,11,11,12,13,13,13,13,13,17, 7, 7, 8,
         8,10,10,11,11,13,13,13,13,14,14,17,11,11, 9, 9,
        11,11,12,12,12,13,13,14,15,13,17,12,12, 9, 9,11,
        11,12,12,13,13,13,13,14,16,17,17,17,11,12,12,12,
        13,13,13,14,15,14,15,15,17,17,17,12,12,11,11,13,
        13,14,14,15,14,15,15,17,17,17,15,15,13,13,14,14,
        15,14,15,15,16,15,17,17,17,15,15,13,13,13,14,14,
        15,15,15,15,16,17,17,17,17,16,14,15,14,14,15,14,
        14,15,15,15,17,17,17,17,17,14,14,16,14,15,15,15,
        15,15,15,17,17,17,17,17,17,16,16,15,17,15,15,14,
        17,15,17,16,17,17,17,17,16,15,14,15,15,15,15,15,
        15,
    };

    /// <summary>
    /// Gets the map type.
    /// </summary>
    public CodeBookMapType MapType { get; } = (CodeBookMapType)1;
    /// <summary>
    /// Gets the quant min.
    /// </summary>
    public int QuantMin { get; } = -520986624;
    /// <summary>
    /// Gets the quant delta.
    /// </summary>
    public int QuantDelta { get; } = 1620377600;
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
        7,
        6,
        8,
        5,
        9,
        4,
        10,
        3,
        11,
        2,
        12,
        1,
        13,
        0,
        14,
    };
}
