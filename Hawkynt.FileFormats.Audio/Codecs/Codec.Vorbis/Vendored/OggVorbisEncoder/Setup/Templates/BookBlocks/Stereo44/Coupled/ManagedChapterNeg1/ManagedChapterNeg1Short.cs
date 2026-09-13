namespace OggVorbisEncoder.Setup.Templates.BookBlocks.Stereo44.Coupled.ManagedChapterNeg1;

/// <summary>
/// Represents a managed chapter neg 1 short.
/// </summary>
public class ManagedChapterNeg1Short : IStaticCodeBook
{
    /// <summary>
    /// Gets the dimensions.
    /// </summary>
    public int Dimensions { get; } = 2;

    /// <summary>
    /// Gets the length list.
    /// </summary>
    public byte[] LengthList { get; } = {
         5, 6,12,14,12,14,16,17,18, 4, 2, 5,11, 7,10,12,
        14,15, 9, 4, 5,11, 7,10,13,15,18,15, 6, 7, 5, 6,
         8,11,13,16,11, 5, 6, 5, 5, 6, 9,13,15,12, 5, 7,
         6, 5, 6, 9,12,14,12, 6, 7, 8, 6, 7, 9,12,13,14,
         8, 8, 7, 5, 5, 8,10,12,16, 9, 9, 8, 6, 6, 7, 9,
         9,
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
