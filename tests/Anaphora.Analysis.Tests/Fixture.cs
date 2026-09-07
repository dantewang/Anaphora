using Anaphora.CaptureProbe;
using Anaphora.Core;

namespace Anaphora.Analysis.Tests;

/// <summary>
/// The captured frames, with everything outside the HUD blacked out so they fit
/// in the repository. They keep the original 3840x2160 canvas, so the shipped
/// profile's normalised bounds apply to them unchanged.
/// </summary>
internal static class Fixture
{
    public static readonly GameProfile Profile =
        ProfileStore.Load(Path.Combine(AppContext.BaseDirectory, "profiles", "endfield.json"));

    public static byte[] Load(string frame, out int width, out int height) =>
        Png.ReadBgra(Path.Combine(AppContext.BaseDirectory, "fixtures", $"frame-{frame}.png"), out width, out height);

    public static HudSnapshot Read(string frame)
    {
        byte[] pixels = Load(frame, out int width, out int height);
        return new HudReader(Profile).Read(new FrameView(pixels, width, height));
    }
}
