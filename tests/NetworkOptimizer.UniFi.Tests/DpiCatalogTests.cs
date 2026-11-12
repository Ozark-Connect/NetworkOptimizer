using NetworkOptimizer.UniFi;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class DpiCatalogTests
{
    [Fact]
    public void Long_protocol_names_read_as_the_plain_one()
    {
        Assert.Equal("HTTP", DpiCatalog.AppName(18, 63));
        Assert.Equal("HTTPS", DpiCatalog.AppName(19, 94));
        Assert.Equal("HTTP/3", DpiCatalog.AppName(13, 755));
        Assert.Equal("fa-solid fa-chart-line", DpiCatalog.IconsForName("SNMP").Class);
    }

    [Fact]
    public void Names_resolve_from_the_packed_key()
    {
        Assert.Equal("Netflix", DpiCatalog.AppName(4, 132));
        Assert.Equal("Media streaming services", DpiCatalog.CategoryName(4));
        Assert.Equal("netflix.com", DpiCatalog.IconDomain(4, 132));
        Assert.Null(DpiCatalog.AppName(255, 12345));
    }

    [Theory]
    [InlineData(4, 112, "YouTube")]          // UniFi Network's catalog: "Youtube"
    [InlineData(4, 303, "YouTube Kids")]
    [InlineData(6, 4, "FaceTime")]           // "Facetime"
    [InlineData(8, 62, "Xbox")]              // "XBOX"
    [InlineData(8, 22, "WoW Game")]          // "WOW Game", World of Warcraft: the Battle.net mark
    public void Brands_take_their_owners_spelling(int category, int application, string expected)
    {
        Assert.Equal(expected, DpiCatalog.AppName(category, application));
        Assert.NotNull(DpiCatalog.IconsForName(expected).Class);
    }

    [Theory]
    [InlineData(13, 84, "Apple")]           // "Apple.com": Live Traffic names Apple by its domains
    [InlineData(13, 126, "Akamai")]         // "Akamai.net"
    [InlineData(13, 209, "Adobe")]          // "Adobe.com", the catalog's own duplicate of "Adobe"
    [InlineData(13, 69, "Microsoft.com")]   // no bare "Microsoft" to fold into
    public void A_dot_com_name_folds_into_its_bare_name(int category, int application, string expected) =>
        Assert.Equal(expected, DpiCatalog.AppName(category, application));

    [Fact]
    public void Brand_casing_fixes_whole_words_only() =>
        Assert.Equal("Wowtv.co.kr", DpiCatalog.AppName(4, 175));

    [Fact]
    public void Icon_falls_through_catalog_mark_name_brand_word_and_category()
    {
        Assert.Equal("fa-brands fa-spotify", DpiCatalog.IconClass(4, 130));    // the catalog's own mark
        Assert.Equal("fa-solid fa-lock", DpiCatalog.IconClass(20, 185));       // SSL/TLS, by name
        Assert.Equal("SSH", DpiCatalog.AppName(10, 21));                        // "Secure Shell (SSH)", renamed
        Assert.Equal("fa-solid fa-terminal", DpiCatalog.IconClass(10, 21));   // our mark, as port 22's
        Assert.Equal("fa-solid fa-terminal", DpiCatalog.IconsForName("SSH").Class);
        Assert.Equal("fa-brands fa-ebay", DpiCatalog.IconClass(13, 17));       // eBay, by name
        Assert.Equal("fa-brands fa-microsoft", DpiCatalog.IconClass(13, 69));  // Microsoft.com, by brand word
        Assert.Equal("fa-solid fa-globe", DpiCatalog.IconClass(13, 60000));    // unknown app, Web services category
        Assert.Equal("fa-solid fa-question", DpiCatalog.IconClass(255, 0));    // unknown app, Unknown category
        Assert.Equal("fa-solid fa-question", DpiCatalog.IconClass(21, 3));     // a category the catalog lacks
    }

    [Fact]
    public void Only_catalog_domains_are_icon_domains()
    {
        Assert.True(DpiCatalog.IsIconDomain("netflix.com"));
        Assert.False(DpiCatalog.IsIconDomain("example.org"));
    }
}
