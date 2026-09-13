using Anaphora.Core;

namespace Anaphora.Analysis;

/// <summary>One ROI's pixels: where they come from in the capture texture, and where they land in the atlas.</summary>
public readonly record struct AtlasRegion(string RoiId, PixelRect Source, int AtlasX, int AtlasY)
{
    public PixelRect Destination => new(AtlasX, AtlasY, Source.Width, Source.Height);
}

/// <summary>
/// Packs every ROI of a profile into one small image, so a frame costs one
/// readback of a few hundred kilobytes instead of 33 MB.
///
/// The packing is a plain copy at native resolution -- no scaling, no filtering
/// -- which on the GPU is one CopySubresourceRegion per ROI straight into a
/// staging texture. The readers never see the atlas as an atlas: each ROI is
/// read through a <see cref="FrameView"/> reframed so that its client-area
/// coordinates land on its own slot, which makes an atlas reading bit-for-bit
/// identical to reading the whole frame.
///
/// A layout is only valid for the texture size and client area it was built
/// for. Rebuild it when either changes.
/// </summary>
public sealed class RoiAtlas
{
    /// <summary>Gutter between slots. Nothing bleeds without filtering; this only keeps debug dumps legible.</summary>
    private const int Padding = 1;

    private readonly Dictionary<string, AtlasRegion> byRoi;

    private RoiAtlas(
        GameProfile profile,
        int textureWidth,
        int textureHeight,
        PixelRect clientArea,
        int width,
        int height,
        AtlasRegion[] regions)
    {
        Profile = profile;
        TextureWidth = textureWidth;
        TextureHeight = textureHeight;
        ClientArea = clientArea;
        Width = width;
        Height = height;
        Regions = regions;
        byRoi = regions.ToDictionary(r => r.RoiId, StringComparer.Ordinal);
    }

    public GameProfile Profile { get; }

    public int TextureWidth { get; }

    public int TextureHeight { get; }

    /// <summary>The game's client area inside the capture texture.</summary>
    public PixelRect ClientArea { get; }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyList<AtlasRegion> Regions { get; }

    /// <param name="clientArea">
    /// Where the client area sits in the capture texture. For a borderless game
    /// it is the whole texture; a windowed one is captured with its chrome.
    /// </param>
    public static RoiAtlas Create(GameProfile profile, int textureWidth, int textureHeight, PixelRect clientArea)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(textureWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(textureHeight);

        PixelRect client = ClampTo(clientArea, textureWidth, textureHeight);
        if (client.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clientArea), $"{clientArea} has no overlap with a {textureWidth}x{textureHeight} texture.");
        }

        // Source rectangles in texture coordinates. Every rectangle a reader
        // derives -- segments, tails, insets, the inner disc -- lies inside its
        // ROI's rounded bounds, so copying those bounds is enough.
        var sources = new List<(string Id, PixelRect Rect)>();
        foreach (RoiDefinition roi in profile.Rois)
        {
            PixelRect local = roi.Bounds.ToPixels(client.Width, client.Height);
            PixelRect source = ClampTo(
                new PixelRect(client.X + local.X, client.Y + local.Y, local.Width, local.Height),
                textureWidth,
                textureHeight);

            if (!source.IsEmpty)
            {
                sources.Add((roi.Id, source));
            }
        }

        // Shelf packing, tallest first. Near enough to optimal for a dozen
        // rectangles, and the result is deterministic, which keeps GPU dumps
        // comparable between runs.
        int area = sources.Sum(s => (s.Rect.Width + Padding) * (s.Rect.Height + Padding));
        int widest = sources.Count == 0 ? 1 : sources.Max(s => s.Rect.Width);
        int width = Math.Max(widest, (int)Math.Ceiling(Math.Sqrt(area * 1.25)));

        var regions = new List<AtlasRegion>(sources.Count);
        int x = 0;
        int y = 0;
        int shelf = 0;

        foreach ((string id, PixelRect rect) in sources
            .OrderByDescending(s => s.Rect.Height)
            .ThenByDescending(s => s.Rect.Width)
            .ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            if (x > 0 && x + rect.Width > width)
            {
                x = 0;
                y += shelf + Padding;
                shelf = 0;
            }

            regions.Add(new AtlasRegion(id, rect, x, y));
            x += rect.Width + Padding;
            shelf = Math.Max(shelf, rect.Height);
        }

        int height = Math.Max(1, y + shelf);
        return new RoiAtlas(profile, textureWidth, textureHeight, client, width, height, [.. regions]);
    }

    /// <summary>
    /// The view a reader should use for one ROI: client-area coordinates, backed
    /// by that ROI's slot in <paramref name="atlas"/>. False when nothing was
    /// copied for it because its bounds fell outside the texture; reading it
    /// anyway would walk off the end of the atlas.
    /// </summary>
    public bool TryViewFor(in FrameView atlas, RoiDefinition roi, out FrameView view)
    {
        ArgumentNullException.ThrowIfNull(roi);

        if (!byRoi.TryGetValue(roi.Id, out AtlasRegion region))
        {
            view = default;
            return false;
        }

        // Client (x, y) is texture (x + client.X, y + client.Y), which sits at
        // atlas (texture - source + slot).
        view = atlas.Reframe(
            ClientArea.Width,
            ClientArea.Height,
            ClientArea.X - region.Source.X + region.AtlasX,
            ClientArea.Y - region.Source.Y + region.AtlasY);
        return true;
    }

    /// <summary>
    /// Builds the atlas on the CPU from a whole texture. The GPU path does the
    /// same with CopySubresourceRegion; this one exists so the layout can be
    /// tested against captured frames on a machine without the game.
    /// </summary>
    public void CopyFrom(ReadOnlySpan<byte> texture, int textureStride, Span<byte> atlas)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(atlas.Length, Width * Height * 4);

        foreach (AtlasRegion region in Regions)
        {
            for (int row = 0; row < region.Source.Height; row++)
            {
                texture
                    .Slice(((region.Source.Y + row) * textureStride) + (region.Source.X * 4), region.Source.Width * 4)
                    .CopyTo(atlas.Slice(((region.AtlasY + row) * Width * 4) + (region.AtlasX * 4)));
            }
        }
    }

    private static PixelRect ClampTo(PixelRect rect, int width, int height)
    {
        int left = Math.Clamp(rect.X, 0, width);
        int top = Math.Clamp(rect.Y, 0, height);
        int right = Math.Clamp(rect.Right, 0, width);
        int bottom = Math.Clamp(rect.Bottom, 0, height);
        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
