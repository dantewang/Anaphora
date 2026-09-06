using System.Globalization;

namespace Anaphora.CaptureProbe;

/// <summary>
/// Cuts the same rectangle out of many captured frames and tiles them into one
/// image. Marking a ROI needs to know how a HUD element behaves over a fight --
/// when a bar drains, when an icon lights up -- and flicking through ninety
/// separate 4K frames is no way to see that.
/// </summary>
internal static class MontageCommand
{
    private const int Gap = 4;

    public static int Run(string[] args)
    {
        if (args.Length < 7)
        {
            Console.Error.WriteLine("usage: montage <dir> <pattern> <x> <y> <w> <h> <out.png> [columns] [scale]");
            Console.Error.WriteLine("       e.g. montage captures/combat \"frame-*[0-9].png\" 1570 1915 700 105 sp.png 1");
            return 64;
        }

        string directory = Path.GetFullPath(args[0]);
        string[] files = Directory.GetFiles(directory, args[1]).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
        {
            Console.Error.WriteLine($"[FAIL] no files matching '{args[1]}' in {directory}");
            return 65;
        }

        int x = int.Parse(args[2], CultureInfo.InvariantCulture);
        int y = int.Parse(args[3], CultureInfo.InvariantCulture);
        int w = int.Parse(args[4], CultureInfo.InvariantCulture);
        int h = int.Parse(args[5], CultureInfo.InvariantCulture);
        string destination = Path.GetFullPath(args[6]);
        int columns = args.Length > 7 ? Math.Max(1, int.Parse(args[7], CultureInfo.InvariantCulture)) : 1;
        int scale = args.Length > 8 ? Math.Clamp(int.Parse(args[8], CultureInfo.InvariantCulture), 1, 4) : 1;

        int cellWidth = w * scale;
        int cellHeight = h * scale;
        int rows = (files.Length + columns - 1) / columns;
        int sheetWidth = (columns * cellWidth) + ((columns - 1) * Gap);
        int sheetHeight = (rows * cellHeight) + ((rows - 1) * Gap);

        byte[] sheet = new byte[sheetWidth * sheetHeight * 4];
        // Magenta gutters: nothing in a game HUD looks like this, so cell edges
        // are never mistaken for content.
        for (int i = 0; i < sheet.Length; i += 4)
        {
            sheet[i] = 255;
            sheet[i + 2] = 255;
            sheet[i + 3] = 255;
        }

        for (int index = 0; index < files.Length; index++)
        {
            byte[] pixels = Png.ReadBgra(files[index], out int fw, out int fh);
            int cx = Math.Clamp(x, 0, fw - 1);
            int cy = Math.Clamp(y, 0, fh - 1);
            int cw = Math.Clamp(w, 1, fw - cx);
            int ch = Math.Clamp(h, 1, fh - cy);

            byte[] cell = Png.Crop(pixels, fw * 4, cx, cy, cw, ch);

            int col = index % columns;
            int row = index / columns;
            int originX = col * (cellWidth + Gap);
            int originY = row * (cellHeight + Gap);

            for (int sy = 0; sy < ch * scale; sy++)
            {
                int destinationRow = ((originY + sy) * sheetWidth * 4) + (originX * 4);
                int sourceRow = (sy / scale) * cw * 4;
                for (int sx = 0; sx < cw * scale; sx++)
                {
                    Buffer.BlockCopy(cell, sourceRow + ((sx / scale) * 4), sheet, destinationRow + (sx * 4), 4);
                }
            }

            Console.WriteLine($"  [{row},{col}] {Path.GetFileName(files[index])}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Png.WriteBgra(destination, sheet, sheetWidth, sheetHeight, sheetWidth * 4);

        Console.WriteLine();
        Console.WriteLine($"tiles          : {files.Length} in {rows}x{columns} (row-major, listed above)");
        Console.WriteLine($"wrote          : {destination} ({sheetWidth}x{sheetHeight})");
        return 0;
    }
}
