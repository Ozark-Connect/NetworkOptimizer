using FluentAssertions;
using NetworkOptimizer.Agents.Models;
using Xunit;

namespace NetworkOptimizer.Agents.Tests;

public class DeploymentResultTests
{
    #region CreateSuccess Tests

    [Fact]
    public void CreateSuccess_SetsCorrectProperties()
    {
        // Arrange
        var agentId = "agent-123";
        var deviceName = "Test Device";
        var agentType = AgentType.UDM;
        var message = "Deployment successful";

        // Act
        var result = DeploymentResult.CreateSuccess(agentId, deviceName, agentType, message);

        // Assert
        result.Success.Should().BeTrue();
        result.AgentId.Should().Be(agentId);
        result.DeviceName.Should().Be(deviceName);
        result.AgentType.Should().Be(agentType);
        result.Message.Should().Be(message);
        result.DeployedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        result.Steps.Should().BeEmpty();
        result.DeployedFiles.Should().BeEmpty();
        result.Verification.Should().BeNull();
    }

    [Fact]
    public void CreateSuccess_AllAgentTypes_WorkCorrectly()
    {
        foreach (var agentType in Enum.GetValues<AgentType>())
        {
            // Act
            var result = DeploymentResult.CreateSuccess("id", "name", agentType, "msg");

            // Assert
            result.Success.Should().BeTrue();
            result.AgentType.Should().Be(agentType);
        }
    }

    #endregion

    #region CreateFailure Tests

    [Fact]
    public void CreateFailure_SetsCorrectProperties()
    {
        // Arrange
        var agentId = "agent-456";
        var deviceName = "Failed Device";
        var agentType = AgentType.Linux;
        var message = "SSH connection failed";

        // Act
        var result = DeploymentResult.CreateFailure(agentId, deviceName, agentType, message);

        // Assert
        result.Success.Should().BeFalse();
        result.AgentId.Should().Be(agentId);
        result.DeviceName.Should().Be(deviceName);
        result.AgentType.Should().Be(agentType);
        result.Message.Should().Be(message);
        result.DeployedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        result.Steps.Should().BeEmpty();
        result.DeployedFiles.Should().BeEmpty();
        result.Verification.Should().BeNull();
    }

    #endregion

    #region DeploymentStep Tests

    [Fact]
    public void DeploymentStep_DefaultValues_AreCorrect()
    {
        // Act
        var step = new DeploymentStep { Name = "Test Step" };

        // Assert
        step.Name.Should().Be("Test Step");
        step.Success.Should().BeFalse();
        step.Message.Should().BeEmpty();
        step.ExecutedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        step.DurationMs.Should().Be(0);
    }

    [Fact]
    public void DeploymentResult_CanAddSteps()
    {
        // Arrange
        var result = DeploymentResult.CreateSuccess("id", "name", AgentType.UDM, "msg");

        // Act
        result.Steps.Add(new DeploymentStep
        {
            Name = "Upload Files",
            Success = true,
            Message = "Files uploaded",
            DurationMs = 1500
        });
        result.Steps.Add(new DeploymentStep
        {
            Name = "Start Service",
            Success = true,
            Message = "Service started",
            DurationMs = 500
        });

        // Assert
        result.Steps.Should().HaveCount(2);
        result.Steps[0].Name.Should().Be("Upload Files");
        result.Steps[1].Name.Should().Be("Start Service");
    }

    #endregion

    #region VerificationResult Tests

    [Fact]
    public void VerificationResult_DefaultValues_AreCorrect()
    {
        // Act
        var verification = new VerificationResult();

        // Assert
        verification.Passed.Should().BeFalse();
        verification.VerifiedFiles.Should().BeEmpty();
        verification.ServiceStatus.Should().BeNull();
        verification.AgentRunning.Should().BeFalse();
        verification.Messages.Should().BeEmpty();
    }

    [Fact]
    public void DeploymentResult_CanSetVerification()
    {
        // Arrange
        var result = DeploymentResult.CreateSuccess("id", "name", AgentType.UDM, "msg");

        // Act
        result.Verification = new VerificationResult
        {
            Passed = true,
            VerifiedFiles = new List<string> { "/data/scripts/agent.sh", "/etc/init.d/agent" },
            ServiceStatus = "active (running)",
            AgentRunning = true,
            Messages = new List<string> { "All files present", "Service running" }
        };

        // Assert
        result.Verification.Should().NotBeNull();
        result.Verification!.Passed.Should().BeTrue();
        result.Verification.VerifiedFiles.Should().HaveCount(2);
        result.Verification.AgentRunning.Should().BeTrue();
        result.Verification.ServiceStatus.Should().Be("active (running)");
        result.Verification.Messages.Should().HaveCount(2);
    }

    #endregion

    #region DeployedFiles Tests

    [Fact]
    public void DeploymentResult_CanAddDeployedFiles()
    {
        // Arrange
        var result = DeploymentResult.CreateSuccess("id", "name", AgentType.Linux, "msg");

        // Act
        result.DeployedFiles.Add("/opt/agent/agent.sh");
        result.DeployedFiles.Add("/etc/systemd/system/agent.service");

        // Assert
        result.DeployedFiles.Should().HaveCount(2);
        result.DeployedFiles.Should().Contain("/opt/agent/agent.sh");
        result.DeployedFiles.Should().Contain("/etc/systemd/system/agent.service");
    }

    #endregion
}
