/*-------------------------------------------------------------------*
 *                         SCALE.C                   *
 *-------------------------------------------------------------------*
 * Scale signal to get maximum of dynamic.               *
 *-------------------------------------------------------------------*/

namespace AmrWbLib;

public partial class AmrWb
{
    void Scale_sig(
         short[] x,                           /* (i/o) : signal to scale               */
         short lg,                            /* (i)   : size of x[]                   */
         short exp                            /* (i)   : exponent: x = round(x << exp) */
    )
    {
        short i;
        int L_tmp;

        for (i = 0; i < lg; i++)
        {
            L_tmp = L_deposit_h(x[i]);
            L_tmp = L_shl(L_tmp, exp);         /* saturation can occur here */
            x[i] = round(L_tmp);
        }
    }

    // 28 Apr 25 PHR
    private unsafe void Scale_sig(
         short* x,                           /* (i/o) : signal to scale               */
         short lg,                            /* (i)   : size of x[]                   */
         short exp                            /* (i)   : exponent: x = round(x << exp) */
    )
    {
        short i;
        int L_tmp;

        for (i = 0; i < lg; i++)
        {
            L_tmp = L_deposit_h(x[i]);
            L_tmp = L_shl(L_tmp, exp);         /* saturation can occur here */
            x[i] = round(L_tmp);
        }
    }

    // 28 Apr 25 PHR
    void Scale_sig(
         ref short x,                           /* (i/o) : signal to scale               */
         short lg,                            /* (i)   : size of x[]                   */
         short exp)                            /* (i)   : exponent: x = round(x << exp) */
    {
        //short i;
        int L_tmp;

        L_tmp = L_deposit_h(x);
        L_tmp = L_shl(L_tmp, exp);         /* saturation can occur here */
        x = round(L_tmp);
    }
}
