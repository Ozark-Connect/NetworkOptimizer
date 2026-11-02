using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class UniFiOsUsersAppTests
{
    // Shape of GET /proxy/users/api/v2/users, trimmed to the fields read plus a few that are not.
    private const string UsersJson = """
        {
          "code": 1, "codeS": "SUCCESS", "msg": "",
          "data": [
            {
              "_id": 1, "unique_id": "00000000-0000-0000-0000-000000000001",
              "first_name": "Test", "last_name": "Admin", "full_name": "Test Admin",
              "username": "testadmin", "status": "ACTIVE", "user_status": "ACTIVE",
              "local_account_exist": true, "only_local_account": true, "only_ui_account": false,
              "create_time": 1751508069, "login_time": 1783121291, "last_activity_time": 0,
              "roles": [ { "unique_id": "r1", "name": "Super Admin", "system_role": true,
                           "system_key": "super_administrator", "level": 2 } ],
              "api_keys": [], "permissions": { "network.management": ["admin"] }
            },
            {
              "_id": 2, "unique_id": "00000000-0000-0000-0000-000000000002",
              "full_name": "Cloud Owner", "username": "", "status": "ACTIVE",
              "local_account_exist": false, "only_local_account": false,
              "create_time": "1745956246", "login_time": "1793595565", "last_activity_time": 0,
              "roles": [ { "name": "Owner", "system_key": "owner", "level": 1 } ]
            }
          ],
          "page": 1, "num": 2, "total": 2, "update_time": 1793603797, "show_source": true
        }
        """;

    // Shape of GET /proxy/users/api/v2/keys.
    private const string KeysJson = """
        {
          "code": 1, "codeS": "SUCCESS", "msg": "",
          "data": [
            {
              "id": "k1", "name": "Automation", "masked_api_key": "abc***",
              "created_at": "2026-10-28T16:30:59Z", "updated_at": "2026-11-02T07:11:14Z",
              "key_permissions": [], "scopes": ["network.read"],
              "creator_user_id": "00000000-0000-0000-0000-000000000001",
              "last_used_at": "2026-11-02T01:11:14-06:00"
            },
            { "id": "k2", "name": "Never Used", "created_at": "2026-09-01T00:00:00Z", "last_used_at": null },
            { "id": "k3", "name": "Empty", "created_at": "2026-09-01T00:00:00Z", "last_used_at": "" },
            { "id": "k4", "name": "Zero", "created_at": "2026-09-01T00:00:00Z", "last_used_at": "0001-01-01T00:00:00Z" }
          ]
        }
        """;

    [Fact]
    public void Users_Deserialize()
    {
        var response = JsonSerializer.Deserialize<UniFiOsUsersAppResponse<UniFiOsUser>>(UsersJson)!;

        Assert.Equal(1, response.Code);
        Assert.Equal(2, response.Total);
        Assert.Equal(2, response.Data!.Count);

        var local = response.Data[0];
        Assert.Equal("Test Admin", local.DisplayName);
        Assert.Equal("ACTIVE", local.Status);
        Assert.True(local.OnlyLocalAccount);
        Assert.Equal(1751508069, local.CreateTime);
        Assert.Equal(1783121291, local.LoginTime);
        Assert.Equal(0, local.LastActivityTime);
        Assert.Equal("super_administrator", Assert.Single(local.Roles!).SystemKey);
    }

    [Fact]
    public void Users_StringTimestamps_ReadAsNumbers()
    {
        var cloud = JsonSerializer.Deserialize<UniFiOsUsersAppResponse<UniFiOsUser>>(UsersJson)!.Data![1];

        Assert.False(cloud.OnlyLocalAccount);
        Assert.Equal(1745956246, cloud.CreateTime);
        Assert.Equal(1793595565, cloud.LoginTime);
    }

    [Fact]
    public void User_DisplayName_FallsBackToUsernameThenId()
    {
        Assert.Equal("u1", new UniFiOsUser { FullName = " ", Username = "u1", UniqueId = "id" }.DisplayName);
        Assert.Equal("id", new UniFiOsUser { UniqueId = "id" }.DisplayName);
    }

    [Fact]
    public void Keys_Deserialize()
    {
        var keys = JsonSerializer.Deserialize<UniFiOsUsersAppResponse<UniFiOsApiKey>>(KeysJson)!.Data!;

        Assert.Equal(4, keys.Count);
        Assert.Equal("Automation", keys[0].Name);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 16, 30, 59, TimeSpan.Zero), keys[0].CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 7, 11, 14, TimeSpan.Zero), keys[0].LastUsedAt);
    }

    [Fact]
    public void Keys_NeverUsedForms_ReadAsNull()
    {
        var keys = JsonSerializer.Deserialize<UniFiOsUsersAppResponse<UniFiOsApiKey>>(KeysJson)!.Data!;

        Assert.All(keys.Skip(1), k => Assert.Null(k.LastUsedAt));
        Assert.All(keys.Skip(1), k => Assert.NotNull(k.CreatedAt));
    }
}

public class FlexibleDateTimeOffsetConverterTests
{
    private record Holder([property: JsonConverter(typeof(FlexibleDateTimeOffsetConverter))] DateTimeOffset? Value);

    private static DateTimeOffset? Read(string json) => JsonSerializer.Deserialize<Holder>($"{{\"Value\":{json}}}")!.Value;

    [Theory]
    [InlineData("\"2026-11-02T07:11:14Z\"")]
    [InlineData("\"2026-11-02T01:11:14-06:00\"")]
    [InlineData("\"2026-11-02T07:11:14\"")] // no offset: UTC
    public void Reads_iso8601(string json)
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 7, 11, 14, TimeSpan.Zero), Read(json));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"not a date\"")]
    [InlineData("\"0001-01-01T00:00:00Z\"")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2]")]
    public void Reads_unusable_as_null(string json)
    {
        Assert.Null(Read(json));
    }

    [Fact]
    public void Unusable_value_does_not_disturb_following_properties()
    {
        var result = JsonSerializer.Deserialize<UniFiOsApiKey>("{\"last_used_at\":{\"x\":[1]},\"name\":\"after\"}")!;

        Assert.Null(result.LastUsedAt);
        Assert.Equal("after", result.Name);
    }

    [Fact]
    public void Round_trips()
    {
        var value = new DateTimeOffset(2026, 11, 2, 7, 11, 14, TimeSpan.Zero);
        var json = JsonSerializer.Serialize(new Holder(value));

        Assert.Equal(value, JsonSerializer.Deserialize<Holder>(json)!.Value);
        Assert.Equal("{\"Value\":null}", JsonSerializer.Serialize(new Holder(null)));
    }
}

public class FlexibleScalarStringConverterTests
{
    private record Holder([property: JsonConverter(typeof(FlexibleScalarStringConverter))] string? Value);

    private static string? Read(string json) => JsonSerializer.Deserialize<Holder>($"{{\"Value\":{json}}}")!.Value;

    [Theory]
    [InlineData("\"abc\"", "abc")]
    [InlineData("42", "42")]
    [InlineData("-3.25", "-3.25")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    public void Reads_scalar_as_text(string json, string expected)
    {
        Assert.Equal(expected, Read(json));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"a\":1}")]
    [InlineData("[1,2]")]
    public void Reads_non_scalar_as_null(string json)
    {
        Assert.Null(Read(json));
    }
}
