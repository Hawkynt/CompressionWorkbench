/*-------------------------------------------------------------------*
 *                         LP_DEC2.C                 *
 *-------------------------------------------------------------------*
 * Decimate a vector by 2 with 2nd order fir filter.                 *
 *-------------------------------------------------------------------*/

namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    private const int lp_dec2_L_FIR = 5;    // 30 Apr 25 PHR -- changed from L_FIR due to naming conflict
    private const int L_MEM = (lp_dec2_L_FIR - 2);

    /* static float h_fir[L_FIR] = {0.13, 0.23, 0.28, 0.23, 0.13}; */
    /* fixed-point: sum of coef = 32767 to avoid overflow on DC */
    private static short[] h_fir = new short[lp_dec2_L_FIR] { 4260, 7536, 9175, 7536, 4260 };

    private unsafe void LP_Decim2(
         short* x,                           /* in/out: signal to process         */
         short l,                             /* input : size of filtering         */
         short[] mem                          /* in/out: memory (size=3)           */
)
    {
        short* p_x;
        short* x_buf = stackalloc short[L_FRAME + L_MEM];
        short i, j, k;
        int L_tmp;

        /* copy initial filter states into buffer */

        p_x = x_buf;
        for (i = 0; i < L_MEM; i++)
        {
            *p_x++ = mem[i];
        }
        for (i = 0; i < l; i++)
        {
            *p_x++ = x[i];
        }
        for (i = 0; i < L_MEM; i++)
        {
            mem[i] = x[l - L_MEM + i];
        }

        for (i = 0, j = 0; i < l; i += 2, j++)
        {
            p_x = &x_buf[i];

            L_tmp = 0;
            for (k = 0; k < lp_dec2_L_FIR; k++)
                L_tmp = L_mac(L_tmp, *p_x++, h_fir[k]);

            x[j] = round(L_tmp);
        }
    }


}
