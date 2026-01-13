using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Agents;
using NetworkOptimizer.Agents.Models;
using Xunit;

namespace NetworkOptimizer.Agents.Tests;

public class ScriptRendererTests : IDisposable
{
    private readonly Mock<ILogger<ScriptRenderer>> _loggerMock;
    private readonly string _tempTemplatesDir;
    private readonly ScriptRenderer _renderer;

    public ScriptRendererTests()
    {
        _loggerMock = new Mock<ILogger<ScriptRenderer>>();
        _tempTemplatesDir = Path.Combine(Path.GetTempPath(), $"templates_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempTemplatesDir);
        _renderer = new ScriptRenderer(_loggerMock.Object, _tempTemplatesDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempTemplatesDir))
        {
            try { Directory.Delete(_tempTemplatesDir, true); } catch { }
        }
    }

    private static AgentConfiguration CreateTestConfig(AgentType agentType = AgentType.UDM)
    {
        return new AgentConfiguration
        {
            AgentId = "test-agent-001",
            DeviceName = "Test Device",
            AgentType = agentType,
            InfluxDbUrl = "http://influxdb:8086",
            InfluxDbOrg = "test-org",
            InfluxDbBucket = "test-bucket",
            InfluxDbToken = "test-token-secret",
            CollectionIntervalSeconds = 30,
            SpeedtestIntervalMinutes = 60,
            EnableDockerMetrics = true,
            Tags = new Dictionary<string, string>
            {
                { "site", "main" },
                { "environment", "test" }
            },
            SshCredentials = new SshCredentials
            {
                Host = "192.168.1.1",
                Username = "root",
                Password = "password"
            }
        };
    }

    #region RenderTemplateStringAsync Tests

    [Fact]
    public async Task RenderTemplateStringAsync_SimpleVariable_RendersCorrectly()
    {
        // Arrange
        var template = "Agent ID: {{ agent_id }}, Device: {{ device_name }}";
        var config = CreateTestConfig();

        // Act
        var result = await _renderer.RenderTemplateStringAsync(template, config);

        // Assert
        result.Should().Be("Agent ID: test-agent-001, Device: Test Device");
    }

    [Fact]
    public async Task RenderTemplateStringAsync_AllBasicVariables_RendersCorrectly()
    {
        // Arrange
        var template = @"agent_id={{ agent_id }}
device_name={{ device_name }}
agent_type={{ agent_type }}
influxdb_url={{ influxdb_url }}
influxdb_org={{ influxdb_org }}
influxdb_bucket={{ influxdb_bucket }}
influxdb_token={{ influxdb_token }}
collection_interval={{ collection_interval }}
speedtest_interval={{ speedtest_interval }}
enable_docker={{ enable_docker }}";
        var config = CreateTestConfig();

        // Act
        var result = await _renderer.RenderTemplateStringAsync(template, config);

        // Assert
        result.Should().Contain("agent_id=test-agent-001");
        result.Should().Contain("device_name=Test Device");
        result.Should().Contain("agent_type=udm");
        result.Should().Contain("influxdb_url=http://influxdb:8086");
        result.Should().Contain("influxdb_org=test-org");
        result.Should().Contain("influxdb_bucket=test-bucket");
        result.Should().Contain("influxdb_token=test-token-secret");
        result.Should().Contain("collection_interval=30");
        result.Should().Contain("speedtest_interval=60");
        result.Should().Contain("enable_docker=true");
    }

    [Fact]
    public async Task RenderTemplateStringAsync_ConditionalIsUdm_RendersCorrectly()
    {
        // Arrange
        var template = @"{{ if is_udm }}UDM DEVICE{{ else }}NOT UDM{{ end }}";
        var config = CreateTestConfig(AgentType.UDM);

        // Act
        var result = await _renderer.RenderTemplateStringAsync(template, config);

        // Assert
        result.Should().Be("UDM DEVICE");
    }

    [Fact]
    public async Task RenderTemplateStringAsync_ConditionalIsUcg_RendersCorrectly()
    {
        // Arrange
        var template = @"{{ if is_ucg }}UCG DEVICE{{ else }}NOT UCG{{ end }}";
        var config = CreateTestConfig(AgentType.UCG);

        // Act
        var result = await _renderer.RenderTemplateStringAsync(template, config);

        // Assert
        result.Should().Be("UCG DEVICE");
    }

    [Fact]
    public async Task RenderTemplateStringAsync_ConditionalIsLinux_RendersCorrectly()
    {
        // Arrange
        var template = @"{{ if is_linux }}LINUX DEVICE{{ else }}NOT LINUX{{ end }}";
        var config = CreateTestConfig(AgentType.Linux);

        // Act
        var result = await _renderer.RenderTemplateStringAsync(template, config);

        // Assert
        result.Should().Be("LINUX DEVICE");
    }

    [Fact]
    public async Task RenderTemplateStringAsync_ConditionalIsUnifi_UDM_RendersCorrectly()
    {
        // Arrange
        var template = @"{{ if is_unifi }}UNIFI DEVICE{{ else }}NOT UNIFI{{ end }}";
        var config = CreateTestConfig(AgentType.UDM);

        // Act
        var result = await _renderer.RenderTemplateStringAsync(template, config);

        // Assert
        result.Should().Be("UNIFI DEVICE");
    }

    [Fact]
    public async Task RenderTemplateStringAsync_ConditionalIsUnifi_UCG_RendersCorrectly()
    {
        // Arrange
        var template = @"{{ if is_unifi }}UNIFI DEVICE{{ else }}NOT UNIFI{{ end }}";
        var config = CreateTestConfig(AgentType.UCG);

        // Act
        var result = await _renderer.RenderTemplateStringAsync(template, config);

        // Assert
        result.Should().Be("UNIFI DEVICE");
    }

    [Fact]
    public async Task RenderTemplateStringAsync_ConditionalIsUnifi_Linux_RendersCorrectly()
    {
        // Arrange
        var template = @"{{ if is_unifi }}UNIFI DEVICE{{ else }}NOT UNIFI{{ end }}";
        var config = CreateTestConfig(AgentType.Linux);

        // Act
        var result = await _renderer.RenderTemplateStringAsync(template, config);

        // Assert
        result.Should().Be("NOT UNIFI");
    }

    [Fact]
    public async Task RenderTemplateStringAsync_Tags_RendersCorrectly()
    {
        // Arrange
        var template = @"Site: {{ tags.site }}, Environment: {{ tags.environment }}";
        var config = CreateTestConfig();

        // Act
        var result = await _renderer.RenderTemplateStringAsync(template, config);

        // Assert
        result.Should().Be("Site: main, Environment: test");
    }

    [Fact]
    public async Task RenderTemplateStringAsync_InvalidTemplate_ThrowsException()
    {
        // Arrange
        var template = "{{ if unclosed }";
        var config = CreateTestConfig();

        // Act
        var act = async () => await _renderer.RenderTemplateStringAsync(template, config);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Template parsing errors*");
    }

    [Fact]
    public async Task RenderTemplateStringAsync_EmptyTemplate_ReturnsEmpty()
    {
        // Arrange
        var template = "";
        var config = CreateTestConfig();

        // Act
        var result = await _renderer.RenderTemplateStringAsync(template, config);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task RenderTemplateStringAsync_NoVariables_ReturnsAsIs()
    {
        // Arrange
        var template = "#!/bin/bash\necho \"Hello World\"";
        var config = CreateTestConfig();

        // Act
        var result = await _renderer.RenderTemplateStringAsync(template, config);

        // Assert
        result.Should().Be("#!/bin/bash\necho \"Hello World\"");
    }

    #endregion

    #region RenderTemplateAsync Tests

    [Fact]
    public async Task RenderTemplateAsync_ValidTemplateFile_RendersCorrectly()
    {
        // Arrange
        var templateContent = "Agent: {{ agent_id }}, Device: {{ device_name }}";
        var templatePath = Path.Combine(_tempTemplatesDir, "test.template");
        await File.WriteAllTextAsync(templatePath, templateContent);
        var config = CreateTestConfig();

        // Act
        var result = await _renderer.RenderTemplateAsync("test.template", config);

        // Assert
        result.Should().Be("Agent: test-agent-001, Device: Test Device");
    }

    [Fact]
    public async Task RenderTemplateAsync_FileNotFound_ThrowsException()
    {
        // Arrange
        var config = CreateTestConfig();

        // Act
        var act = async () => await _renderer.RenderTemplateAsync("nonexistent.template", config);

        // Assert
        await act.Should().ThrowAsync<FileNotFoundException>()
            .WithMessage("*Template not found*");
    }

    [Fact]
    public async Task RenderTemplateAsync_InvalidTemplateContent_ThrowsException()
    {
        // Arrange
        var templateContent = "{{ invalid syntax {{}";
        var templatePath = Path.Combine(_tempTemplatesDir, "invalid.template");
        await File.WriteAllTextAsync(templatePath, templateContent);
        var config = CreateTestConfig();

        // Act
        var act = async () => await _renderer.RenderTemplateAsync("invalid.template", config);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Template parsing errors*");
    }

    #endregion

    #region GetTemplatesForAgent Tests

    [Fact]
    public void GetTemplatesForAgent_UDM_ReturnsCorrectTemplates()
    {
        // Act
        var templates = _renderer.GetTemplatesForAgent(AgentType.UDM);

        // Assert
        templates.Should().HaveCount(3);
        templates.Should().Contain("udm-agent-boot.sh.template");
        templates.Should().Contain("udm-metrics-collector.sh.template");
        templates.Should().Contain("install-udm.sh.template");
    }

    [Fact]
    public void GetTemplatesForAgent_UCG_ReturnsCorrectTemplates()
    {
        // Act
        var templates = _renderer.GetTemplatesForAgent(AgentType.UCG);

        // Assert
        templates.Should().HaveCount(3);
        templates.Should().Contain("udm-agent-boot.sh.template");
        templates.Should().Contain("udm-metrics-collector.sh.template");
        templates.Should().Contain("install-udm.sh.template");
    }

    [Fact]
    public void GetTemplatesForAgent_Linux_ReturnsCorrectTemplates()
    {
        // Act
        var templates = _renderer.GetTemplatesForAgent(AgentType.Linux);

        // Assert
        templates.Should().HaveCount(3);
        templates.Should().Contain("linux-agent.sh.template");
        templates.Should().Contain("linux-agent.service.template");
        templates.Should().Contain("install-linux.sh.template");
    }

    [Fact]
    public void GetTemplatesForAgent_UnknownType_ThrowsException()
    {
        // Arrange
        var invalidType = (AgentType)999;

        // Act
        var act = () => _renderer.GetTemplatesForAgent(invalidType);

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithMessage("Unknown agent type: 999");
    }

    #endregion

    #region ValidateTemplates Tests

    [Fact]
    public void ValidateTemplates_AllTemplatesExist_ReturnsTrue()
    {
        // Arrange - Create all UDM templates
        File.WriteAllText(Path.Combine(_tempTemplatesDir, "udm-agent-boot.sh.template"), "content");
        File.WriteAllText(Path.Combine(_tempTemplatesDir, "udm-metrics-collector.sh.template"), "content");
        File.WriteAllText(Path.Combine(_tempTemplatesDir, "install-udm.sh.template"), "content");

        // Act
        var isValid = _renderer.ValidateTemplates(AgentType.UDM, out var missingTemplates);

        // Assert
        isValid.Should().BeTrue();
        missingTemplates.Should().BeEmpty();
    }

    [Fact]
    public void ValidateTemplates_SomeTemplatesMissing_ReturnsFalse()
    {
        // Arrange - Create only one template
        File.WriteAllText(Path.Combine(_tempTemplatesDir, "udm-agent-boot.sh.template"), "content");

        // Act
        var isValid = _renderer.ValidateTemplates(AgentType.UDM, out var missingTemplates);

        // Assert
        isValid.Should().BeFalse();
        missingTemplates.Should().HaveCount(2);
        missingTemplates.Should().Contain("udm-metrics-collector.sh.template");
        missingTemplates.Should().Contain("install-udm.sh.template");
    }

    [Fact]
    public void ValidateTemplates_NoTemplatesExist_ReturnsFalse()
    {
        // Act
        var isValid = _renderer.ValidateTemplates(AgentType.Linux, out var missingTemplates);

        // Assert
        isValid.Should().BeFalse();
        missingTemplates.Should().HaveCount(3);
    }

    #endregion

    #region ListAvailableTemplates Tests

    [Fact]
    public void ListAvailableTemplates_WithTemplates_ReturnsFileNames()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempTemplatesDir, "first.template"), "content");
        File.WriteAllText(Path.Combine(_tempTemplatesDir, "second.template"), "content");

        // Act
        var templates = _renderer.ListAvailableTemplates();

        // Assert
        templates.Should().HaveCount(2);
        templates.Should().Contain("first.template");
        templates.Should().Contain("second.template");
    }

    [Fact]
    public void ListAvailableTemplates_NoTemplates_ReturnsEmpty()
    {
        // Act
        var templates = _renderer.ListAvailableTemplates();

        // Assert
        templates.Should().BeEmpty();
    }

    [Fact]
    public void ListAvailableTemplates_DirectoryNotExists_ReturnsEmpty()
    {
        // Arrange
        Directory.Delete(_tempTemplatesDir, true);

        // Act
        var templates = _renderer.ListAvailableTemplates();

        // Assert
        templates.Should().BeEmpty();
    }

    [Fact]
    public void ListAvailableTemplates_OnlyTemplateExtension_FiltersCorrectly()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_tempTemplatesDir, "valid.template"), "content");
        File.WriteAllText(Path.Combine(_tempTemplatesDir, "invalid.txt"), "content");
        File.WriteAllText(Path.Combine(_tempTemplatesDir, "readme.md"), "content");

        // Act
        var templates = _renderer.ListAvailableTemplates();

        // Assert
        templates.Should().HaveCount(1);
        templates.Should().Contain("valid.template");
    }

    #endregion
}
