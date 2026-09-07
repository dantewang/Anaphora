using Anaphora.Core;

namespace Anaphora.Analysis;

/// <summary>
/// Difference hash. The patch is reduced to a 9x8 grid of grey, then each cell
/// is compared with its right-hand neighbour: 64 comparisons, 64 bits.
///
/// Comparisons rather than absolute levels is what makes it survive the thing
/// that matters here -- the same portrait rendered over a different background,
/// under a different effect, at a different brightness. It is not robust to
/// rotation or reflection, and does not need to be: HUD portraits never move.
/// </summary>
public static class PerceptualHash
{
    /// <summary>Grid columns. One more than the rows, because each row yields width-1 bits.</summary>
    public const int Columns = 9;

    public const int Rows = 8;

    public static ulong Compute(in FrameView frame, PixelRect rect)
    {
        Span<double> cells = stackalloc double[Columns * Rows];
        Reduce(frame, rect, cells);

        ulong hash = 0;
        int bit = 0;
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns - 1; column++)
            {
                if (cells[(row * Columns) + column] > cells[(row * Columns) + column + 1])
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }

    public static int Distance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);

    /// <summary>Box-averages the patch down to the grid. Empty patches reduce to zeroes.</summary>
    private static void Reduce(in FrameView frame, PixelRect rect, Span<double> cells)
    {
        cells.Clear();
        if (rect.IsEmpty)
        {
            return;
        }

        Span<int> counts = stackalloc int[Columns * Rows];
        counts.Clear();

        for (int y = 0; y < rect.Height; y++)
        {
            int row = Math.Min(y * Rows / rect.Height, Rows - 1);
            for (int x = 0; x < rect.Width; x++)
            {
                int column = Math.Min(x * Columns / rect.Width, Columns - 1);
                int index = (row * Columns) + column;
                cells[index] += frame[rect.X + x, rect.Y + y].Luma;
                counts[index]++;
            }
        }

        for (int i = 0; i < cells.Length; i++)
        {
            if (counts[i] > 0)
            {
                cells[i] /= counts[i];
            }
        }
    }
}
