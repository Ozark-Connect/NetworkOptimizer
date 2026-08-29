using NetworkOptimizer.UniFi;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class DpiCatalogTests
{
    [Fact]
    public void Names_resolve_from_the_packed_key()
    {
        Assert.Equal("Netflix", DpiCatalog.AppName(4, 132));
        Assert.Equal("Media streaming services", DpiCatalog.CategoryName(4));
        Assert.Equal("netflix.com", DpiCatalog.IconDomain(4, 132));
        Assert.Null(DpiCatalog.AppName(255, 12345));
    }

    [Fact]
    public void Icon_falls_through_catalog_mark_name_brand_word_and_category()
    {
        Assert.Equal("fa-brands fa-spotify", DpiCatalog.IconClass(4, 130));    // the catalog's own mark
        Assert.Equal("fa-solid fa-lock", DpiCatalog.IconClass(20, 185));       // SSL/TLS, by name
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
