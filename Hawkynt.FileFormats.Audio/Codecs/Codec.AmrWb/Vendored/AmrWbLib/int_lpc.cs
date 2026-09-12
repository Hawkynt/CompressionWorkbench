/*-----------------------------------------------------------------------*
 *                         int_lpc.C                   *
 *-----------------------------------------------------------------------*
 * Interpolation of the LP parameters in 4 subframes.           *
 *-----------------------------------------------------------------------*/

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    private const int MP1 = (M + 1);

    private unsafe void Int_isp(
         short[] isp_old,                     /* input : isps from past frame              */
         short[] isp_new,                     /* input : isps from present frame           */
         short[] frac,                        /* input : fraction for 3 first subfr (Q15)  */
         short* Az)                           /* output: LP coefficients in 4 subframes    */
    {
        short i, k, fac_old, fac_new;
        short[] isp = new short[M];
        int L_tmp;

        for (k = 0; k < 3; k++)
        {
            fac_new = frac[k];
            fac_old = add(sub(32767, fac_new), 1);  /* 1.0 - fac_new */

            for (i = 0; i < M; i++)
            {
                L_tmp = L_mult(isp_old[i], fac_old);
                L_tmp = L_mac(L_tmp, isp_new[i], fac_new);
                isp[i] = round(L_tmp);
            }
            Isp_Az(isp, Az, M, 0);
            Az += MP1;
        }

        /* 4th subframe: isp_new (frac=1.0) */

        Isp_Az(isp_new, Az, M, 0);
    }


}
