using Anaphora.Analysis;
using Anaphora.Core;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Anaphora.Capture;

/// <summary>
/// The per-frame GPU work: copy each ROI's rectangle out of the captured texture
/// straight into a small staging texture, map that once, hand the bytes over,
/// unmap. The whole frame never leaves the GPU.
///
/// One instance per atlas layout. A new client area or texture size means a new
/// layout, and so a new staging texture of a different size.
/// </summary>
internal sealed class AtlasReadback : IDisposable
{
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11Texture2D staging;

    private ID3D11Texture2D? verification;

    public AtlasReadback(ID3D11Device device, RoiAtlas atlas)
    {
        this.device = device;
        context = device.ImmediateContext;
        Atlas = atlas;
        staging = device.CreateTexture2D(StagingDescription(atlas.Width, atlas.Height));
    }

    public RoiAtlas Atlas { get; }

    public bool Matches(int textureWidth, int textureHeight, PixelRect clientArea) =>
        Atlas.TextureWidth == textureWidth &&
        Atlas.TextureHeight == textureHeight &&
        Atlas.ClientArea == clientArea;

    public unsafe void Process(ID3D11Texture2D source, TimeSpan timestamp, AtlasFrameHandler handler)
    {
        foreach (AtlasRegion region in Atlas.Regions)
        {
            context.CopySubresourceRegion(
                staging,
                0,
                (uint)region.AtlasX,
                (uint)region.AtlasY,
                0,
                source,
                0,
                new Box(region.Source.X, region.Source.Y, 0, region.Source.Right, region.Source.Bottom, 1));
        }

        // Map blocks until the copies above have run. They are a few hundred
        // kilobytes, so this is a short wait on the capture thread, not a stall
        // of the game's own rendering.
        MappedSubresource map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int stride = (int)map.RowPitch;
            var bytes = new ReadOnlySpan<byte>((void*)map.DataPointer, (stride * (Atlas.Height - 1)) + (Atlas.Width * 4));
            handler(new AtlasFrame(new FrameView(bytes, Atlas.Width, Atlas.Height, stride), Atlas, timestamp));
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    /// <summary>
    /// Diagnostic only, and expensive: reads the whole texture back, builds the
    /// atlas again on the CPU and compares it byte for byte with what the GPU
    /// copied. Proves the copy and the layout agree on a real driver without
    /// needing the game -- any window will do.
    /// </summary>
    public unsafe bool Verify(ID3D11Texture2D source)
    {
        Texture2DDescription description = source.Description;
        if (verification is null ||
            verification.Description.Width != description.Width ||
            verification.Description.Height != description.Height)
        {
            verification?.Dispose();
            verification = device.CreateTexture2D(StagingDescription((int)description.Width, (int)description.Height));
        }

        context.CopyResource(verification, source);

        byte[] expected = new byte[Atlas.Width * Atlas.Height * 4];
        MappedSubresource whole = context.Map(verification, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int stride = (int)whole.RowPitch;
            var bytes = new ReadOnlySpan<byte>(
                (void*)whole.DataPointer,
                (stride * ((int)description.Height - 1)) + ((int)description.Width * 4));
            Atlas.CopyFrom(bytes, stride, expected);
        }
        finally
        {
            context.Unmap(verification, 0);
        }

        // The staging atlas still holds this frame's copy; Process ran first.
        MappedSubresource packed = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int stride = (int)packed.RowPitch;
            foreach (AtlasRegion region in Atlas.Regions)
            {
                for (int row = 0; row < region.Source.Height; row++)
                {
                    var actual = new ReadOnlySpan<byte>(
                        (byte*)packed.DataPointer + ((region.AtlasY + row) * stride) + (region.AtlasX * 4),
                        region.Source.Width * 4);
                    ReadOnlySpan<byte> wanted = expected.AsSpan(
                        ((region.AtlasY + row) * Atlas.Width * 4) + (region.AtlasX * 4),
                        region.Source.Width * 4);

                    if (!actual.SequenceEqual(wanted))
                    {
                        return false;
                    }
                }
            }
        }
        finally
        {
            context.Unmap(staging, 0);
        }

        return true;
    }

    public void Dispose()
    {
        verification?.Dispose();
        staging.Dispose();
    }

    private static Texture2DDescription StagingDescription(int width, int height) => new(
        Format.B8G8R8A8_UNorm,
        (uint)width,
        (uint)height,
        arraySize: 1,
        mipLevels: 1,
        bindFlags: BindFlags.None,
        usage: ResourceUsage.Staging,
        cpuAccessFlags: CpuAccessFlags.Read);
}
