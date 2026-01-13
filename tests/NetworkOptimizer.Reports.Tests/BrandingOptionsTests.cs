using FluentAssertions;
using NetworkOptimizer.Reports;
using Xunit;

namespace NetworkOptimizer.Reports.Tests;

public class BrandingOptionsTests
{
    #region Default Values Tests

    [Fact]
    public void BrandingOptions_DefaultValues_AreCorrect()
    {
        // Act
        var options = new BrandingOptions();

        // Assert
        options.CompanyName.Should().Be("Ozark Connect");
        options.LogoPath.Should().BeNull();
        options.ShowProductAttribution.Should().BeTrue();
        options.ProductName.Should().Be("Network Optimizer");
        options.CustomFooter.Should().BeNull();
        options.Colors.Should().NotBeNull();
    }

    #endregion

    #region Factory Method Tests

    [Fact]
    public void OzarkConnect_Factory_ReturnsCorrectOptions()
    {
        // Act
        var options = BrandingOptions.OzarkConnect();

        // Assert
        options.CompanyName.Should().Be("Ozark Connect");
        options.ShowProductAttribution.Should().BeTrue();
        options.ProductName.Should().Be("Network Optimizer");
        options.Colors.Primary.Should().Be("#2E6B7D");
    }

    [Fact]
    public void Generic_Factory_ReturnsCorrectOptions()
    {
        // Act
        var options = BrandingOptions.Generic();

        // Assert
        options.CompanyName.Should().Be("Network Report");
        options.ShowProductAttribution.Should().BeFalse();
        options.Colors.Primary.Should().Be("#1F4788");
    }

    #endregion

    #region Property Setting Tests

    [Fact]
    public void BrandingOptions_CanSetAllProperties()
    {
        // Arrange & Act
        var options = new BrandingOptions
        {
            CompanyName = "Custom Company",
            LogoPath = "/path/to/logo.png",
            ShowProductAttribution = false,
            ProductName = "Custom Product",
            CustomFooter = "Custom Footer Text",
            Colors = ColorScheme.HighContrast()
        };

        // Assert
        options.CompanyName.Should().Be("Custom Company");
        options.LogoPath.Should().Be("/path/to/logo.png");
        options.ShowProductAttribution.Should().BeFalse();
        options.ProductName.Should().Be("Custom Product");
        options.CustomFooter.Should().Be("Custom Footer Text");
        options.Colors.Primary.Should().Be("#000080");
    }

    #endregion
}

public class ColorSchemeTests
{
    #region Default Values Tests

    [Fact]
    public void ColorScheme_DefaultValues_AreCorrect()
    {
        // Act
        var colors = new ColorScheme();

        // Assert
        colors.Primary.Should().Be("#2E6B7D");
        colors.Secondary.Should().Be("#E87D33");
        colors.Tertiary.Should().Be("#215999");
        colors.Success.Should().Be("#389E3C");
        colors.Warning.Should().Be("#D9A621");
        colors.Critical.Should().Be("#CC3333");
        colors.LightGray.Should().Be("#F5F5F5");
        colors.Text.Should().Be("#000000");
        colors.TextSecondary.Should().Be("#666666");
    }

    #endregion

    #region Factory Method Tests

    [Fact]
    public void OzarkConnect_Factory_ReturnsCorrectColors()
    {
        // Act
        var colors = ColorScheme.OzarkConnect();

        // Assert
        colors.Primary.Should().Be("#2E6B7D");
        colors.Secondary.Should().Be("#E87D33");
        colors.Success.Should().Be("#389E3C");
    }

    [Fact]
    public void Generic_Factory_ReturnsCorrectColors()
    {
        // Act
        var colors = ColorScheme.Generic();

        // Assert
        colors.Primary.Should().Be("#1F4788");
        colors.Secondary.Should().Be("#5C7A99");
        colors.Success.Should().Be("#27AE60");
    }

    [Fact]
    public void HighContrast_Factory_ReturnsCorrectColors()
    {
        // Act
        var colors = ColorScheme.HighContrast();

        // Assert
        colors.Primary.Should().Be("#000080");
        colors.Secondary.Should().Be("#4169E1");
        colors.Success.Should().Be("#006400");
    }

    #endregion

    #region HexToRgb Tests

    [Theory]
    [InlineData("#FFFFFF", 1f, 1f, 1f)]
    [InlineData("#000000", 0f, 0f, 0f)]
    [InlineData("#FF0000", 1f, 0f, 0f)]
    [InlineData("#00FF00", 0f, 1f, 0f)]
    [InlineData("#0000FF", 0f, 0f, 1f)]
    [InlineData("FFFFFF", 1f, 1f, 1f)] // Without hash
    public void HexToRgb_ValidHex_ReturnsCorrectRgb(string hex, float expectedR, float expectedG, float expectedB)
    {
        // Act
        var (r, g, b) = ColorScheme.HexToRgb(hex);

        // Assert
        r.Should().BeApproximately(expectedR, 0.01f);
        g.Should().BeApproximately(expectedG, 0.01f);
        b.Should().BeApproximately(expectedB, 0.01f);
    }

    [Theory]
    [InlineData("#2E6B7D", 0.18f, 0.42f, 0.49f)] // Brand teal
    [InlineData("#E87D33", 0.91f, 0.49f, 0.2f)]  // Brand orange
    [InlineData("#389E3C", 0.22f, 0.62f, 0.24f)] // Brand green
    public void HexToRgb_BrandColors_ReturnsCorrectRgb(string hex, float expectedR, float expectedG, float expectedB)
    {
        // Act
        var (r, g, b) = ColorScheme.HexToRgb(hex);

        // Assert
        r.Should().BeApproximately(expectedR, 0.01f);
        g.Should().BeApproximately(expectedG, 0.01f);
        b.Should().BeApproximately(expectedB, 0.01f);
    }

    [Theory]
    [InlineData("#FFF")]
    [InlineData("#FFFFF")]
    [InlineData("#FFFFFFF")]
    [InlineData("invalid")]
    [InlineData("")]
    public void HexToRgb_InvalidHex_ThrowsException(string hex)
    {
        // Act
        var act = () => ColorScheme.HexToRgb(hex);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("*6 characters*");
    }

    #endregion

    #region GetRgb Helper Methods Tests

    [Fact]
    public void GetPrimaryRgb_ReturnsCorrectValues()
    {
        // Arrange
        var colors = ColorScheme.OzarkConnect();

        // Act
        var (r, g, b) = colors.GetPrimaryRgb();

        // Assert
        r.Should().BeApproximately(0.18f, 0.01f);
        g.Should().BeApproximately(0.42f, 0.01f);
        b.Should().BeApproximately(0.49f, 0.01f);
    }

    [Fact]
    public void GetSecondaryRgb_ReturnsCorrectValues()
    {
        // Arrange
        var colors = ColorScheme.OzarkConnect();

        // Act
        var (r, g, b) = colors.GetSecondaryRgb();

        // Assert
        r.Should().BeApproximately(0.91f, 0.01f);
        g.Should().BeApproximately(0.49f, 0.01f);
        b.Should().BeApproximately(0.2f, 0.01f);
    }

    [Fact]
    public void GetSuccessRgb_ReturnsCorrectValues()
    {
        // Arrange
        var colors = ColorScheme.OzarkConnect();

        // Act
        var (r, g, b) = colors.GetSuccessRgb();

        // Assert
        r.Should().BeApproximately(0.22f, 0.01f);
        g.Should().BeApproximately(0.62f, 0.01f);
        b.Should().BeApproximately(0.24f, 0.01f);
    }

    [Fact]
    public void GetWarningRgb_ReturnsCorrectValues()
    {
        // Arrange
        var colors = ColorScheme.OzarkConnect();

        // Act
        var (r, g, b) = colors.GetWarningRgb();

        // Assert
        r.Should().BeApproximately(0.85f, 0.01f);
        g.Should().BeApproximately(0.65f, 0.01f);
        b.Should().BeApproximately(0.13f, 0.01f);
    }

    [Fact]
    public void GetCriticalRgb_ReturnsCorrectValues()
    {
        // Arrange
        var colors = ColorScheme.OzarkConnect();

        // Act
        var (r, g, b) = colors.GetCriticalRgb();

        // Assert
        r.Should().BeApproximately(0.8f, 0.01f);
        g.Should().BeApproximately(0.2f, 0.01f);
        b.Should().BeApproximately(0.2f, 0.01f);
    }

    #endregion
}
