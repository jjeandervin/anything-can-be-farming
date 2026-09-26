using AnythingCanBeFarming.Data;

namespace AnythingCanBeFarming.Api.Tests;

public sealed class ReferenceTextTests
{
    [Theory]
    [InlineData("  Japanese  Maple ", "japanese maple")]
    [InlineData("Japanese maple", "japanese maple")]
    [InlineData("JAPANESE MAPLE", "japanese maple")]
    [InlineData("Black-eyed Susan", "black-eyed susan")]
    [InlineData("Queen Anne’s lace", "queen anne's lace")]
    [InlineData("‘Moonglow’ juniper", "'moonglow' juniper")]
    [InlineData("\tred\r\n maple ", "red maple")]
    [InlineData("Bégonia", "begonia")]
    [InlineData("Bégonia", "begonia")]
    [InlineData("Érable du Japon", "erable du japon")]
    [InlineData("Ａｃｅｒ", "acer")]
    [InlineData("ﬁg", "fig")]
    [InlineData("Straße", "straße")]
    [InlineData("İris", "iris")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Normalizes_names(string input, string expected) =>
        Assert.Equal(expected, NameNormalizer.Normalize(input));

    [Fact]
    public void Normalization_is_idempotent_and_culture_independent()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            var once = NameNormalizer.Normalize("  IRIS  Érable ");
            Assert.Equal("iris erable", once);
            Assert.Equal(once, NameNormalizer.Normalize(once));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("Q1", true)]
    [InlineData("Q159657", true)]
    [InlineData("Q0", false)]
    [InlineData("Q012", false)]
    [InlineData("q159657", false)]
    [InlineData("P225", false)]
    [InlineData("Q", false)]
    [InlineData("Q159657\n", false)]
    [InlineData(" Q159657", false)]
    [InlineData("Q١٢", false)]
    [InlineData("http://www.wikidata.org/entity/Q159657", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Validates_qids(string? value, bool expected) =>
        Assert.Equal(expected, WikidataIdentifier.IsQid(value));

    [Theory]
    [InlineData("wfo-0000514950", true)]
    [InlineData("wfo-4000000718", true)]
    [InlineData("wfo-000051495", false)]
    [InlineData("wfo-00005149500", false)]
    [InlineData("WFO-0000514950", false)]
    [InlineData("wfo-0000514950\n", false)]
    [InlineData(" wfo-0000514950", false)]
    [InlineData("wfo-00005149a0", false)]
    [InlineData("0000514950", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Validates_wfo_ids(string? value, bool expected) =>
        Assert.Equal(expected, WfoIdentifier.IsWellFormed(value));
}
