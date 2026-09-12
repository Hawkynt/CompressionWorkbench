/*-------------------------------------------------------------------*
 *                         UPD_TAR.C                 *
 *-------------------------------------------------------------------*
 * Update the target vector for codebook search.             *
 *-------------------------------------------------------------------*/

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    private unsafe void Updt_tar(
         short* x,                           /* (i) Q0  : old target (for pitch search)     */
         short* x2,                          /* (o) Q0  : new target (for codebook search)  */
         short* y,                           /* (i) Q0  : filtered adaptive codebook vector */
         short gain,                          /* (i) Q14 : adaptive codebook gain            */
         short L                              /* (i)     : subframe size                     */
    )
    {
        short i;
        int L_tmp;

        for (i = 0; i < L; i++)
        {
            L_tmp = L_mult(x[i], 16384);
            L_tmp = L_msu(L_tmp, y[i], gain);
            x2[i] = extract_h(L_shl(L_tmp, 1));
        }

        return;
    }
}
