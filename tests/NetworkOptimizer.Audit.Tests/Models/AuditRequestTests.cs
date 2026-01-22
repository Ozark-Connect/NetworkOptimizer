using System.Text.Json;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.UniFi.Models;
using Xunit;
using FirewallRule = NetworkOptimizer.Audit.Models.FirewallRule;

namespace NetworkOptimizer.Audit.Tests.Models;

public class AuditRequestTests
{
    [Fact]
    public void AuditRequest_RequiredProperty_DeviceDataJson()
    {
        var request = new AuditRequest { DeviceDataJson = "{}" };
        Assert.Equal("{}", request.DeviceDataJson);
    }

    [Fact]
    public void AuditRequest_OptionalProperties_DefaultToNull()
    {
        var request = new AuditRequest { DeviceDataJson = "{}" };

        Assert.Null(request.Clients);
        Assert.Null(request.ClientHistory);
        Assert.Null(request.FingerprintDb);
        Assert.Null(request.SettingsData);
        Assert.Null(request.FirewallRules);
        Assert.Null(request.AllowanceSettings);
        Assert.Null(request.ProtectCameras);
        Assert.Null(request.ClientName);
    }

    [Fact]
    public void AuditRequest_Clients_CanBeSet()
    {
        var clients = new List<UniFiClientResponse>
        {
            new() { Mac = "aa:bb:cc:dd:ee:ff" }
        };

        var request = new AuditRequest
        {
            DeviceDataJson = "{}",
            Clients = clients
        };

        Assert.NotNull(request.Clients);
        Assert.Single(request.Clients);
        Assert.Equal("aa:bb:cc:dd:ee:ff", request.Clients[0].Mac);
    }

    [Fact]
    public void AuditRequest_ClientHistory_CanBeSet()
    {
        var history = new List<UniFiClientHistoryResponse>
        {
            new() { Mac = "aa:bb:cc:dd:ee:ff" }
        };

        var request = new AuditRequest
        {
            DeviceDataJson = "{}",
            ClientHistory = history
        };

        Assert.NotNull(request.ClientHistory);
        Assert.Single(request.ClientHistory);
    }

    [Fact]
    public void AuditRequest_FingerprintDb_CanBeSet()
    {
        var db = new UniFiFingerprintDatabase();

        var request = new AuditRequest
        {
            DeviceDataJson = "{}",
            FingerprintDb = db
        };

        Assert.NotNull(request.FingerprintDb);
    }

    [Fact]
    public void AuditRequest_SettingsData_CanBeSet()
    {
        var json = JsonDocument.Parse("{\"test\": 123}");
        var element = json.RootElement;

        var request = new AuditRequest
        {
            DeviceDataJson = "{}",
            SettingsData = element
        };

        Assert.NotNull(request.SettingsData);
        Assert.Equal(123, request.SettingsData.Value.GetProperty("test").GetInt32());
    }

    [Fact]
    public void AuditRequest_FirewallRules_CanBeSet()
    {
        var rules = new List<FirewallRule>
        {
            new() { Id = "test-rule", Name = "Test Rule" }
        };

        var request = new AuditRequest
        {
            DeviceDataJson = "{}",
            FirewallRules = rules
        };

        Assert.NotNull(request.FirewallRules);
        Assert.Single(request.FirewallRules);
    }

    [Fact]
    public void AuditRequest_AllowanceSettings_CanBeSet()
    {
        var settings = new DeviceAllowanceSettings();

        var request = new AuditRequest
        {
            DeviceDataJson = "{}",
            AllowanceSettings = settings
        };

        Assert.NotNull(request.AllowanceSettings);
    }

    [Fact]
    public void AuditRequest_ProtectCameras_CanBeSet()
    {
        var cameras = new ProtectCameraCollection();

        var request = new AuditRequest
        {
            DeviceDataJson = "{}",
            ProtectCameras = cameras
        };

        Assert.NotNull(request.ProtectCameras);
    }

    [Fact]
    public void AuditRequest_ClientName_CanBeSet()
    {
        var request = new AuditRequest
        {
            DeviceDataJson = "{}",
            ClientName = "TestClient"
        };

        Assert.Equal("TestClient", request.ClientName);
    }

    [Fact]
    public void AuditRequest_AllPropertiesSet()
    {
        var clients = new List<UniFiClientResponse>();
        var history = new List<UniFiClientHistoryResponse>();
        var db = new UniFiFingerprintDatabase();
        var settings = JsonDocument.Parse("{}").RootElement;
        var firewallRules = new List<FirewallRule> { new() { Id = "rule1" } };
        var allowance = new DeviceAllowanceSettings();
        var cameras = new ProtectCameraCollection();

        var request = new AuditRequest
        {
            DeviceDataJson = "{\"data\":[]}",
            Clients = clients,
            ClientHistory = history,
            FingerprintDb = db,
            SettingsData = settings,
            FirewallRules = firewallRules,
            AllowanceSettings = allowance,
            ProtectCameras = cameras,
            ClientName = "FullTest"
        };

        Assert.Equal("{\"data\":[]}", request.DeviceDataJson);
        Assert.Same(clients, request.Clients);
        Assert.Same(history, request.ClientHistory);
        Assert.Same(db, request.FingerprintDb);
        Assert.Same(firewallRules, request.FirewallRules);
        Assert.Same(allowance, request.AllowanceSettings);
        Assert.Same(cameras, request.ProtectCameras);
        Assert.Equal("FullTest", request.ClientName);
    }
}
