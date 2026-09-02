using OggVorbisEncoder.Lookups;
using OggVorbisEncoder.Setup;

namespace OggVorbisEncoder;

/// <summary>
/// Represents a lookup collection.
/// </summary>
public class LookupCollection
{
    private LookupCollection(
        EnvelopeLookup envelopeLookup,
        MdctLookup[] transformLookup,
        PsyGlobalLookup psyGlobalLookup,
        PsyLookup[] psyLookup,
        DrftLookup[] fftLookup,
        FloorLookup[] floorLookup,
        ResidueLookup[] residueLookup)
    {
        EnvelopeLookup = envelopeLookup;
        TransformLookup = transformLookup;
        PsyGlobalLookup = psyGlobalLookup;
        PsyLookup = psyLookup;
        FftLookup = fftLookup;
        FloorLookup = floorLookup;
        ResidueLookup = residueLookup;
    }

        /// <summary>
    /// Gets the envelope lookup.
    /// </summary>
public EnvelopeLookup EnvelopeLookup { get; }
        /// <summary>
    /// Gets the transform lookup.
    /// </summary>
public MdctLookup[] TransformLookup { get; }
        /// <summary>
    /// Gets the psy global lookup.
    /// </summary>
public PsyGlobalLookup PsyGlobalLookup { get; }
        /// <summary>
    /// Gets the psy lookup.
    /// </summary>
public PsyLookup[] PsyLookup { get; }
        /// <summary>
    /// Gets the fft lookup.
    /// </summary>
public DrftLookup[] FftLookup { get; }
        /// <summary>
    /// Gets the floor lookup.
    /// </summary>
public FloorLookup[] FloorLookup { get; }
        /// <summary>
    /// Gets the residue lookup.
    /// </summary>
public ResidueLookup[] ResidueLookup { get; }

        /// <summary>
    /// Performs the create operation.
    /// </summary>
public static LookupCollection Create(VorbisInfo info)
    {
        var codecSetup = info.CodecSetup;

        var psyGlobal = new PsyGlobalLookup(codecSetup.PsyGlobalParam);
        var envelope = new EnvelopeLookup(codecSetup.PsyGlobalParam, info);

        // MDCT is tranform 0
        var transform = new MdctLookup[2];
        transform[0] = new MdctLookup(codecSetup.BlockSizes[0]);
        transform[1] = new MdctLookup(codecSetup.BlockSizes[1]);

        // analysis always needs an fft
        var fftLookup = new DrftLookup[2];
        fftLookup[0] = new DrftLookup(codecSetup.BlockSizes[0]);
        fftLookup[1] = new DrftLookup(codecSetup.BlockSizes[1]);

        // finish the codebooks 
        if (codecSetup.FullBooks == null)
        {
            codecSetup.FullBooks = new CodeBook[codecSetup.BookParams.Count];
            for (var i = 0; i < codecSetup.BookParams.Count; i++)
                codecSetup.FullBooks[i] = CodeBook.InitEncode(codecSetup.BookParams[i]);
        }

        var psyLookup = new PsyLookup[codecSetup.PsyParams.Count];
        for (var i = 0; i < psyLookup.Length; i++)
            psyLookup[i] = new PsyLookup(
                codecSetup.PsyParams[i],
                codecSetup.PsyGlobalParam,
                codecSetup.BlockSizes[codecSetup.PsyParams[i].BlockFlag] / 2,
                info.SampleRate);

        // initialize all the backend lookups 
        var floor = new FloorLookup[codecSetup.FloorParams.Count];
        for (var i = 0; i < floor.Length; i++)
            floor[i] = new FloorLookup(codecSetup.FloorParams[i]);

        var residue = new ResidueLookup[codecSetup.ResidueParams.Count];
        for (var i = 0; i < residue.Length; i++)
            residue[i] = new ResidueLookup(codecSetup.ResidueParams[i], codecSetup.FullBooks);

        return new LookupCollection(
            envelope,
            transform,
            psyGlobal,
            psyLookup,
            fftLookup,
            floor,
            residue);
    }
}
