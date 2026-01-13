using FluentAssertions;
using NetworkOptimizer.Monitoring;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

public class SnmpConfigurationTests
{
    #region Default Values Tests

    [Fact]
    public void SnmpConfiguration_DefaultValues_AreCorrect()
    {
        // Act
        var config = new SnmpConfiguration();

        // Assert
        config.Port.Should().Be(161);
        config.Timeout.Should().Be(2000);
        config.RetryCount.Should().Be(2);
        config.Version.Should().Be(SnmpVersion.V3);
        config.Community.Should().Be("public");
        config.Username.Should().BeEmpty();
        config.AuthenticationPassword.Should().BeEmpty();
        config.PrivacyPassword.Should().BeEmpty();
        config.AuthProtocol.Should().Be(AuthenticationProtocol.SHA1);
        config.PrivProtocol.Should().Be(PrivacyProtocol.AES);
        config.ContextName.Should().BeEmpty();
        config.EngineId.Should().BeEmpty();
        config.PollingIntervalSeconds.Should().Be(60);
        config.UseHighCapacityCounters.Should().BeTrue();
        config.HighCapacityThresholdMbps.Should().Be(1000);
        config.EnableDebugLogging.Should().BeFalse();
        config.MaxConcurrentRequests.Should().Be(10);
        config.ExcludeInterfacePatterns.Should().HaveCount(9);
    }

    [Fact]
    public void SnmpConfiguration_ExcludeInterfacePatterns_HasExpectedPatterns()
    {
        // Act
        var config = new SnmpConfiguration();

        // Assert
        config.ExcludeInterfacePatterns.Should().Contain("^lo$");
        config.ExcludeInterfacePatterns.Should().Contain("^br-");
        config.ExcludeInterfacePatterns.Should().Contain("^docker");
        config.ExcludeInterfacePatterns.Should().Contain("^veth");
        config.ExcludeInterfacePatterns.Should().Contain("^ifb");
        config.ExcludeInterfacePatterns.Should().Contain("^virbr");
        config.ExcludeInterfacePatterns.Should().Contain("^tun");
        config.ExcludeInterfacePatterns.Should().Contain("^tap");
    }

    #endregion

    #region Port Validation Tests

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-161)]
    public void Validate_InvalidPort_LessThanOne_ThrowsException(int port)
    {
        // Arrange
        var config = CreateValidV2cConfig();
        config.Port = port;

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("Port")
            .WithMessage("*Port must be between 1 and 65535*");
    }

    [Theory]
    [InlineData(65536)]
    [InlineData(70000)]
    [InlineData(int.MaxValue)]
    public void Validate_InvalidPort_GreaterThan65535_ThrowsException(int port)
    {
        // Arrange
        var config = CreateValidV2cConfig();
        config.Port = port;

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("Port")
            .WithMessage("*Port must be between 1 and 65535*");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(161)]
    [InlineData(65535)]
    public void Validate_ValidPort_DoesNotThrow(int port)
    {
        // Arrange
        var config = CreateValidV2cConfig();
        config.Port = port;

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().NotThrow();
    }

    #endregion

    #region Timeout Validation Tests

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-1000)]
    public void Validate_InvalidTimeout_ThrowsException(int timeout)
    {
        // Arrange
        var config = CreateValidV2cConfig();
        config.Timeout = timeout;

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("Timeout")
            .WithMessage("*Timeout must be greater than 0*");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2000)]
    [InlineData(30000)]
    public void Validate_ValidTimeout_DoesNotThrow(int timeout)
    {
        // Arrange
        var config = CreateValidV2cConfig();
        config.Timeout = timeout;

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().NotThrow();
    }

    #endregion

    #region RetryCount Validation Tests

    [Theory]
    [InlineData(-1)]
    [InlineData(-10)]
    public void Validate_NegativeRetryCount_ThrowsException(int retryCount)
    {
        // Arrange
        var config = CreateValidV2cConfig();
        config.RetryCount = retryCount;

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("RetryCount")
            .WithMessage("*RetryCount cannot be negative*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void Validate_ValidRetryCount_DoesNotThrow(int retryCount)
    {
        // Arrange
        var config = CreateValidV2cConfig();
        config.RetryCount = retryCount;

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().NotThrow();
    }

    #endregion

    #region PollingInterval Validation Tests

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-60)]
    public void Validate_InvalidPollingInterval_ThrowsException(int interval)
    {
        // Arrange
        var config = CreateValidV2cConfig();
        config.PollingIntervalSeconds = interval;

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("PollingIntervalSeconds")
            .WithMessage("*PollingIntervalSeconds must be greater than 0*");
    }

    #endregion

    #region SNMP v1/v2c Validation Tests

    [Theory]
    [InlineData(SnmpVersion.V1)]
    [InlineData(SnmpVersion.V2c)]
    public void Validate_V1V2c_WithEmptyCommunity_ThrowsException(SnmpVersion version)
    {
        // Arrange
        var config = new SnmpConfiguration
        {
            Version = version,
            Community = ""
        };

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("Community")
            .WithMessage("*Community string is required for SNMP v1/v2c*");
    }

    [Theory]
    [InlineData(SnmpVersion.V1)]
    [InlineData(SnmpVersion.V2c)]
    public void Validate_V1V2c_WithWhitespaceCommunity_ThrowsException(SnmpVersion version)
    {
        // Arrange
        var config = new SnmpConfiguration
        {
            Version = version,
            Community = "   "
        };

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("Community")
            .WithMessage("*Community string is required for SNMP v1/v2c*");
    }

    [Theory]
    [InlineData(SnmpVersion.V1, "public")]
    [InlineData(SnmpVersion.V2c, "private")]
    public void Validate_V1V2c_WithValidCommunity_DoesNotThrow(SnmpVersion version, string community)
    {
        // Arrange
        var config = new SnmpConfiguration
        {
            Version = version,
            Community = community
        };

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().NotThrow();
    }

    #endregion

    #region SNMP v3 Validation Tests

    [Fact]
    public void Validate_V3_WithEmptyUsername_ThrowsException()
    {
        // Arrange
        var config = new SnmpConfiguration
        {
            Version = SnmpVersion.V3,
            Username = ""
        };

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("Username")
            .WithMessage("*Username is required for SNMP v3*");
    }

    [Fact]
    public void Validate_V3_WithWhitespaceUsername_ThrowsException()
    {
        // Arrange
        var config = new SnmpConfiguration
        {
            Version = SnmpVersion.V3,
            Username = "   "
        };

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("Username")
            .WithMessage("*Username is required for SNMP v3*");
    }

    [Fact]
    public void Validate_V3_WithAuthProtocolAndNoPassword_ThrowsException()
    {
        // Arrange
        var config = new SnmpConfiguration
        {
            Version = SnmpVersion.V3,
            Username = "testuser",
            AuthProtocol = AuthenticationProtocol.SHA1,
            AuthenticationPassword = ""
        };

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("AuthenticationPassword")
            .WithMessage("*AuthenticationPassword is required when using authentication*");
    }

    [Fact]
    public void Validate_V3_WithPrivProtocolAndNoPassword_ThrowsException()
    {
        // Arrange
        var config = new SnmpConfiguration
        {
            Version = SnmpVersion.V3,
            Username = "testuser",
            AuthProtocol = AuthenticationProtocol.SHA1,
            AuthenticationPassword = "authpass",
            PrivProtocol = PrivacyProtocol.AES,
            PrivacyPassword = ""
        };

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("PrivacyPassword")
            .WithMessage("*PrivacyPassword is required when using privacy*");
    }

    [Fact]
    public void Validate_V3_WithNoAuthProtocol_DoesNotRequireAuthPassword()
    {
        // Arrange
        var config = new SnmpConfiguration
        {
            Version = SnmpVersion.V3,
            Username = "testuser",
            AuthProtocol = AuthenticationProtocol.None,
            AuthenticationPassword = "",
            PrivProtocol = PrivacyProtocol.None, // No privacy either
            PrivacyPassword = ""
        };

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_V3_WithNoPrivProtocol_DoesNotRequirePrivPassword()
    {
        // Arrange
        var config = new SnmpConfiguration
        {
            Version = SnmpVersion.V3,
            Username = "testuser",
            AuthProtocol = AuthenticationProtocol.SHA1,
            AuthenticationPassword = "authpass",
            PrivProtocol = PrivacyProtocol.None,
            PrivacyPassword = ""
        };

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_V3_FullyConfigured_DoesNotThrow()
    {
        // Arrange
        var config = CreateValidV3Config();

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(AuthenticationProtocol.MD5)]
    [InlineData(AuthenticationProtocol.SHA1)]
    [InlineData(AuthenticationProtocol.SHA256)]
    [InlineData(AuthenticationProtocol.SHA384)]
    [InlineData(AuthenticationProtocol.SHA512)]
    public void Validate_V3_AllAuthProtocols_WithPassword_DoesNotThrow(AuthenticationProtocol protocol)
    {
        // Arrange
        var config = new SnmpConfiguration
        {
            Version = SnmpVersion.V3,
            Username = "testuser",
            AuthProtocol = protocol,
            AuthenticationPassword = "authpassword",
            PrivProtocol = PrivacyProtocol.None
        };

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(PrivacyProtocol.DES)]
    [InlineData(PrivacyProtocol.AES)]
    [InlineData(PrivacyProtocol.AES192)]
    [InlineData(PrivacyProtocol.AES256)]
    public void Validate_V3_AllPrivProtocols_WithPassword_DoesNotThrow(PrivacyProtocol protocol)
    {
        // Arrange
        var config = new SnmpConfiguration
        {
            Version = SnmpVersion.V3,
            Username = "testuser",
            AuthProtocol = AuthenticationProtocol.SHA1,
            AuthenticationPassword = "authpassword",
            PrivProtocol = protocol,
            PrivacyPassword = "privpassword"
        };

        // Act
        var act = () => config.Validate();

        // Assert
        act.Should().NotThrow();
    }

    #endregion

    #region Clone Tests

    [Fact]
    public void Clone_CreatesIndependentCopy()
    {
        // Arrange
        var original = CreateFullyConfiguredConfig();

        // Act
        var clone = original.Clone();

        // Modify original
        original.Port = 9999;
        original.Username = "modified";
        original.ExcludeInterfacePatterns.Add("^newpattern");

        // Assert - clone should be unchanged
        clone.Port.Should().Be(161);
        clone.Username.Should().Be("testuser");
        clone.ExcludeInterfacePatterns.Should().NotContain("^newpattern");
    }

    [Fact]
    public void Clone_CopiesAllProperties()
    {
        // Arrange
        var original = new SnmpConfiguration
        {
            Port = 162,
            Timeout = 5000,
            RetryCount = 3,
            Version = SnmpVersion.V3,
            Community = "custom",
            Username = "admin",
            AuthenticationPassword = "auth123",
            PrivacyPassword = "priv456",
            AuthProtocol = AuthenticationProtocol.SHA256,
            PrivProtocol = PrivacyProtocol.AES256,
            ContextName = "context1",
            EngineId = "engine1",
            PollingIntervalSeconds = 30,
            UseHighCapacityCounters = false,
            HighCapacityThresholdMbps = 500,
            EnableDebugLogging = true,
            MaxConcurrentRequests = 20,
            ExcludeInterfacePatterns = new List<string> { "^test" }
        };

        // Act
        var clone = original.Clone();

        // Assert
        clone.Port.Should().Be(162);
        clone.Timeout.Should().Be(5000);
        clone.RetryCount.Should().Be(3);
        clone.Version.Should().Be(SnmpVersion.V3);
        clone.Community.Should().Be("custom");
        clone.Username.Should().Be("admin");
        clone.AuthenticationPassword.Should().Be("auth123");
        clone.PrivacyPassword.Should().Be("priv456");
        clone.AuthProtocol.Should().Be(AuthenticationProtocol.SHA256);
        clone.PrivProtocol.Should().Be(PrivacyProtocol.AES256);
        clone.ContextName.Should().Be("context1");
        clone.EngineId.Should().Be("engine1");
        clone.PollingIntervalSeconds.Should().Be(30);
        clone.UseHighCapacityCounters.Should().BeFalse();
        clone.HighCapacityThresholdMbps.Should().Be(500);
        clone.EnableDebugLogging.Should().BeTrue();
        clone.MaxConcurrentRequests.Should().Be(20);
        clone.ExcludeInterfacePatterns.Should().ContainSingle("^test");
    }

    [Fact]
    public void Clone_ExcludePatternsList_IsIndependent()
    {
        // Arrange
        var original = CreateValidV2cConfig();
        original.ExcludeInterfacePatterns = new List<string> { "^lo$", "^eth0$" };

        // Act
        var clone = original.Clone();
        clone.ExcludeInterfacePatterns.Add("^eth1$");

        // Assert
        original.ExcludeInterfacePatterns.Should().HaveCount(2);
        clone.ExcludeInterfacePatterns.Should().HaveCount(3);
    }

    #endregion

    #region Enum Tests

    [Fact]
    public void SnmpVersion_HasCorrectValues()
    {
        ((int)SnmpVersion.V1).Should().Be(0);
        ((int)SnmpVersion.V2c).Should().Be(1);
        ((int)SnmpVersion.V3).Should().Be(3);
    }

    [Fact]
    public void AuthenticationProtocol_HasCorrectValues()
    {
        ((int)AuthenticationProtocol.None).Should().Be(0);
        ((int)AuthenticationProtocol.MD5).Should().Be(1);
        ((int)AuthenticationProtocol.SHA1).Should().Be(2);
        ((int)AuthenticationProtocol.SHA256).Should().Be(3);
        ((int)AuthenticationProtocol.SHA384).Should().Be(4);
        ((int)AuthenticationProtocol.SHA512).Should().Be(5);
    }

    [Fact]
    public void PrivacyProtocol_HasCorrectValues()
    {
        ((int)PrivacyProtocol.None).Should().Be(0);
        ((int)PrivacyProtocol.DES).Should().Be(1);
        ((int)PrivacyProtocol.AES).Should().Be(2);
        ((int)PrivacyProtocol.AES192).Should().Be(3);
        ((int)PrivacyProtocol.AES256).Should().Be(4);
    }

    #endregion

    #region Helper Methods

    private static SnmpConfiguration CreateValidV2cConfig()
    {
        return new SnmpConfiguration
        {
            Version = SnmpVersion.V2c,
            Community = "public"
        };
    }

    private static SnmpConfiguration CreateValidV3Config()
    {
        return new SnmpConfiguration
        {
            Version = SnmpVersion.V3,
            Username = "testuser",
            AuthProtocol = AuthenticationProtocol.SHA1,
            AuthenticationPassword = "authpassword",
            PrivProtocol = PrivacyProtocol.AES,
            PrivacyPassword = "privpassword"
        };
    }

    private static SnmpConfiguration CreateFullyConfiguredConfig()
    {
        return new SnmpConfiguration
        {
            Port = 161,
            Timeout = 2000,
            RetryCount = 2,
            Version = SnmpVersion.V3,
            Community = "public",
            Username = "testuser",
            AuthenticationPassword = "authpass",
            PrivacyPassword = "privpass",
            AuthProtocol = AuthenticationProtocol.SHA1,
            PrivProtocol = PrivacyProtocol.AES,
            ContextName = "context",
            EngineId = "engine",
            PollingIntervalSeconds = 60,
            UseHighCapacityCounters = true,
            HighCapacityThresholdMbps = 1000,
            EnableDebugLogging = false,
            MaxConcurrentRequests = 10,
            ExcludeInterfacePatterns = new List<string> { "^lo$" }
        };
    }

    #endregion
}
