using Anaphora.Core;
using Anaphora.Overlay;

namespace Anaphora.App.Tests;

/// <summary>
/// Dragging the overlay. The anchor is the only record of where it sits, so a
/// drag is arithmetic on fractions of the game's client area: normalised, so
/// that changing resolution or going windowed does not move it.
/// </summary>
public class OverlayAnchorTests
{
    private static readonly PixelRect Client = new(0, 0, 1920, 1080);

    [Fact]
    public void ADragMovesByItsShareOfTheClientArea()
    {
        OverlayAnchor moved = new OverlayAnchor(0.3, 0.6).Nudge(192, 108, Client, 200, 100);

        Assert.Equal(0.4, moved.X, 6);
        Assert.Equal(0.7, moved.Y, 6);
    }

    [Fact]
    public void XSnapsToTheCentreLineWhenItLandsNear()
    {
        // 374px of 1920 lands on .4948, within the 1% tolerance. The default is
        // centred, so getting back to it by hand has to be exact.
        OverlayAnchor moved = new OverlayAnchor(0.3, 0.6).Nudge(374, 0, Client, 200, 100).Snapped();

        Assert.Equal(0.5, moved.X);
        Assert.True(moved.IsCentred);
    }

    [Fact]
    public void XDoesNotSnapFromFurtherOut()
    {
        // .4823 is 1.8% out, so it stays where the drag put it.
        OverlayAnchor moved = new OverlayAnchor(0.3, 0.6).Nudge(350, 0, Client, 200, 100).Snapped();

        Assert.NotEqual(0.5, moved.X);
        Assert.False(moved.IsCentred);
    }

    [Fact]
    public void ASlowDragIsNotPinnedToTheCentreLine()
    {
        // A drag arrives as small deltas. Each 30px step is inside the snap
        // tolerance, so snapping every step would undo every step and the
        // overlay could never leave the middle. Nudge must not snap.
        var anchor = new OverlayAnchor(0.5, 0.67);
        for (int i = 0; i < 10; i++)
        {
            anchor = anchor.Nudge(-30, 0, Client, 400, 300);
        }

        Assert.Equal(0.5 - (300.0 / Client.Width), anchor.X, 6);
        Assert.Equal(anchor, anchor.Snapped());
    }

    [Theory]
    [InlineData(-5000.0, 0.0)]
    [InlineData(5000.0, 0.0)]
    public void TheWholeOverlayStaysInsideTheClientArea(double dx, double dy)
    {
        const int width = 400;
        OverlayAnchor moved = new OverlayAnchor(0.5, 0.67).Nudge(dx, dy, Client, width, 300);

        double half = width / 2.0 / Client.Width;
        Assert.InRange(moved.X, half, 1 - half);
    }

    [Fact]
    public void DraggingOffTheTopStopsAtTheOverlaysOwnHeight()
    {
        // y is the bottom edge, so the overlay's height is as high as it goes.
        OverlayAnchor moved = new OverlayAnchor(0.5, 0.67).Nudge(0, -5000, Client, 400, 300);

        Assert.Equal(300.0 / Client.Height, moved.Y, 6);
    }

    [Fact]
    public void DraggingOffTheBottomStopsAtTheEdge()
    {
        OverlayAnchor moved = new OverlayAnchor(0.5, 0.67).Nudge(0, 5000, Client, 400, 300);

        Assert.Equal(1, moved.Y);
    }

    [Fact]
    public void AnOverlayBiggerThanTheGameIsLeftWhereItIs()
    {
        OverlayAnchor moved = new OverlayAnchor(0.5, 0.67).Nudge(300, 0, Client, 4000, 3000);

        Assert.Equal(0.5, moved.X);
        Assert.Equal(1, moved.Y);
    }

    [Fact]
    public void AnEmptyClientAreaChangesNothing()
    {
        var start = new OverlayAnchor(0.42, 0.61);

        Assert.Equal(start, start.Nudge(100, 100, new PixelRect(0, 0, 0, 0), 200, 100));
    }

    [Fact]
    public void TheReadoutSaysWhereItIsAndWhetherItSnapped()
    {
        Assert.Equal("水平 50.0%（已吸附中线） · 底边 67.0%", OverlayAnchor.AboveBottomHud.Describe());
        Assert.Equal("水平 32.0% · 底边 48.0%", new OverlayAnchor(0.32, 0.48).Describe());
    }
}
