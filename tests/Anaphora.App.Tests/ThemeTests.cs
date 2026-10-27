using System.Globalization;
using System.Xml.Linq;
using Anaphora.App.Editor;
using Avalonia.Data;

namespace Anaphora.App.Tests;

/// <summary>
/// The palette lives in two theme dictionaries that are chosen between at
/// runtime, so a key added to one and forgotten in the other only shows up as
/// a magenta brush in the graph, or as nothing at all in XAML. Compare the two
/// sets instead of waiting to see it.
/// </summary>
public class ThemeTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void BothThemesDefineTheSameColours()
    {
        Dictionary<string, List<string>> keys = Variants();

        Assert.Equal(["Default", "Light"], keys.Keys.Order());
        Assert.Equal(keys["Default"].Order(), keys["Light"].Order());
    }

    [Fact]
    public void NoThemeNamesTheSameColourTwice()
    {
        foreach ((string variant, List<string> keys) in Variants())
        {
            Assert.Equal(keys.Count, keys.Distinct().Count());
            Assert.NotEmpty(variant);
        }
    }

    [Fact]
    public void YellowIsTheOneTheUserAskedFor()
    {
        // #FFFA00 is a fill colour in both themes. Where it would be a hairline
        // or a label, AccentInk takes over, and on a light ground that is black.
        XDocument app = Load();
        Assert.Equal(["#FFFA00", "#FFFA00"], Colours(app, "Accent"));
        Assert.Equal(["#111111", "#111111"], Colours(app, "AccentOn"));
        Assert.Equal(["#FFFA00", "#111111"], Colours(app, "AccentInk"));
    }

    [Theory]
    [InlineData("System", "System", true)]
    [InlineData("System", "Dark", false)]
    [InlineData("Light", "Light", true)]
    public void TheThemeButtonsReadTheSetting(string setting, string button, bool lit)
    {
        object value = Enum.Parse(typeof(AppTheme), setting);

        Assert.Equal(lit, new EnumEqualsConverter().Convert(value, typeof(bool), button, Culture));
    }

    [Fact]
    public void CheckingAThemeButtonWritesItBack()
    {
        var converter = new EnumEqualsConverter();

        Assert.Equal(AppTheme.Dark, converter.ConvertBack(true, typeof(AppTheme), "Dark", Culture));

        // Unchecking is the other button's business: it must not write anything.
        Assert.Equal(BindingOperations.DoNothing, converter.ConvertBack(false, typeof(AppTheme), "Dark", Culture));
        Assert.Equal(BindingOperations.DoNothing, converter.ConvertBack(true, typeof(AppTheme), "Sepia", Culture));
    }

    private static Dictionary<string, List<string>> Variants() =>
        Load().Descendants(Avalonia + "ResourceDictionary")
            .Where(d => d.Attribute(Xaml + "Key") is not null)
            .ToDictionary(
                d => (string)d.Attribute(Xaml + "Key")!,
                d => d.Elements(Avalonia + "SolidColorBrush")
                    .Select(b => (string)b.Attribute(Xaml + "Key")!)
                    .ToList());

    /// <summary>One key's colour in each variant, dark first.</summary>
    private static List<string> Colours(XDocument app, string key) =>
        [.. app.Descendants(Avalonia + "SolidColorBrush")
            .Where(b => (string?)b.Attribute(Xaml + "Key") == key)
            .Select(b => (string)b.Attribute("Color")!)];

    private static XDocument Load()
    {
        return XDocument.Load(Path.Combine(AppContext.BaseDirectory, "App.axaml"));
    }
}
