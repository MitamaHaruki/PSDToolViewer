using PSDToolViewer.Core.Model;

namespace PSDToolViewer.Core.Tests;

public class LayerNamingTests
{
    [Theory]
    [InlineData("目", "目", false, false)]
    [InlineData("*通常", "通常", true, false)]
    [InlineData("!体", "体", false, true)]
    [InlineData("!*両方", "両方", false, true)]
    [InlineData("**二重", "二重", true, false)]
    [InlineData("途中に*記号", "途中に*記号", false, false)]
    public void Parse(string name, string display, bool radio, bool forced)
    {
        Assert.Equal(display, LayerNaming.ToDisplayName(name));
        Assert.Equal(radio, LayerNaming.HasRadioMark(name));
        Assert.Equal(forced, LayerNaming.HasForcedMark(name));
    }

    [Fact]
    public void ComposeNeverHasBothMarks()
    {
        Assert.Equal("!目", LayerNaming.Compose(radio: true, forced: true, "目"));
        Assert.Equal("*目", LayerNaming.Compose(radio: true, forced: false, "!目"));
        Assert.Equal("目", LayerNaming.Compose(radio: false, forced: false, "*目"));
    }
}
