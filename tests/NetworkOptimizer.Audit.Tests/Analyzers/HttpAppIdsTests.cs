using FluentAssertions;
using NetworkOptimizer.Audit.Analyzers;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

public class HttpAppIdsTests
{
    #region IsHttpApp Tests

    [Theory]
    [InlineData(852190, true)]   // HTTP
    [InlineData(1245278, true)]  // HTTPS
    [InlineData(852723, true)]   // HTTP/3
    [InlineData(12345, false)]   // Random non-HTTP app
    [InlineData(0, false)]       // Zero
    [InlineData(-1, false)]      // Negative
    public void IsHttpApp_ReturnsExpectedResult(int appId, bool expected)
    {
        var result = HttpAppIds.IsHttpApp(appId);
        result.Should().Be(expected);
    }

    #endregion

    #region IsWebCategory Tests

    [Theory]
    [InlineData(13, true)]   // Web Services category
    [InlineData(1, false)]   // Other category
    [InlineData(0, false)]   // Zero
    [InlineData(-1, false)]  // Negative
    public void IsWebCategory_ReturnsExpectedResult(int categoryId, bool expected)
    {
        var result = HttpAppIds.IsWebCategory(categoryId);
        result.Should().Be(expected);
    }

    #endregion

    #region Constants Tests

    [Fact]
    public void Http_HasExpectedValue()
    {
        HttpAppIds.Http.Should().Be(852190);
    }

    [Fact]
    public void Https_HasExpectedValue()
    {
        HttpAppIds.Https.Should().Be(1245278);
    }

    [Fact]
    public void Http3_HasExpectedValue()
    {
        HttpAppIds.Http3.Should().Be(852723);
    }

    [Fact]
    public void WebServicesCategory_HasExpectedValue()
    {
        HttpAppIds.WebServicesCategory.Should().Be(13);
    }

    [Fact]
    public void AllHttpAppIds_ContainsAllHttpTypes()
    {
        HttpAppIds.AllHttpAppIds.Should().Contain(HttpAppIds.Http);
        HttpAppIds.AllHttpAppIds.Should().Contain(HttpAppIds.Https);
        HttpAppIds.AllHttpAppIds.Should().Contain(HttpAppIds.Http3);
        HttpAppIds.AllHttpAppIds.Should().HaveCount(3);
    }

    [Fact]
    public void AllWebCategoryIds_ContainsWebServicesCategory()
    {
        HttpAppIds.AllWebCategoryIds.Should().Contain(HttpAppIds.WebServicesCategory);
        HttpAppIds.AllWebCategoryIds.Should().HaveCount(1);
    }

    #endregion
}
