
namespace AmrWbLib;

/// <summary>
/// Represents an amr wb.
/// </summary>
public partial class AmrWb
{
    /*-------------------------------------------------------------------*
     *                         HP7K.C                   *
     *-------------------------------------------------------------------*
     * 15th order high pass 7kHz FIR filter.                             *
     *                                                                   *
     *-------------------------------------------------------------------*/

    private const int L_FIR_7k = 31;

    private static short[] fir_7k = new short[L_FIR_7k]
    {
        -21, 47, -89, 146, -203,
        229, -177, 0, 335, -839,
        1485, -2211, 2931, -3542, 3953,
        28682, 3953, -3542, 2931, -2211,
        1485, -839, 335, 0, -177,
        229, -203, 146, -89, 47,
        -21
    };

    private void Init_Filt_7k(short[] mem)            /* mem[30] */
    {
        Set_zero(mem, L_FIR_7k - 1);

        return;
    }


    private unsafe void Filt_7k(
         short* signal,                      /* input:  signal                  */
         short lg,                            /* input:  length of input         */
         short[] mem)                         /* in/out: memory (size=30)        */
    {
        short i, j;
        short* x = stackalloc short[L_SUBFR16k + (L_FIR_7k - 1)];
        int L_tmp;

        Copy(mem, x, L_FIR_7k - 1);

        for (i = 0; i < lg; i++)
        {
            x[i + L_FIR_7k - 1] = signal[i];
        }

        for (i = 0; i < lg; i++)
        {
            L_tmp = 0;
            for (j = 0; j < L_FIR_7k; j++)
                L_tmp = L_mac(L_tmp, x[i + j], fir_7k[j]);
            signal[i] = round(L_tmp);
        }

        Copy(x + lg, mem, L_FIR_7k - 1);

        return;
    }

}
