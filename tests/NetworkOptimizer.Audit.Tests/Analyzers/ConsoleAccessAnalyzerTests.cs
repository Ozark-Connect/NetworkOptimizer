using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

public class ConsoleAccessAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly ConsoleAccessAnalyzer _analyzer = new(new Mock<ILogger<ConsoleAccessAnalyzer>>().Object);

    #region No Access

    [Fact]
    public void Analyze_BothNull_ReturnsNoIssues()
    {
        _analyzer.Analyze(null, null, Now).Should().BeEmpty();
    }

    [Fact]
    public void Analyze_KeysNull_StillChecksUsers()
    {
        var issues = _analyzer.Analyze([LocalAdmin("Admin1", lastLoginDaysAgo: 40)], null, Now);

        issues.Should().ContainSingle().Which.Type.Should().Be(IssueTypes.UnusedLocalAdmins);
    }

    [Fact]
    public void Analyze_UsersNull_StillChecksKeys()
    {
        var issues = _analyzer.Analyze(null, [Key("Key1", lastUsedDaysAgo: 40)], Now);

        issues.Should().ContainSingle().Which.Type.Should().Be(IssueTypes.UnusedApiKeys);
    }

    [Fact]
    public void Analyze_EmptyLists_ReturnsNoIssues()
    {
        _analyzer.Analyze([], [], Now).Should().BeEmpty();
    }

    #endregion

    #region API Keys

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void ApiKey_UsedWithinSevenDays_NotFlagged(int daysAgo)
    {
        _analyzer.Analyze(null, [Key("Key1", lastUsedDaysAgo: daysAgo)], Now).Should().BeEmpty();
    }

    [Theory]
    [InlineData(7)]
    [InlineData(29)]
    public void ApiKey_UnusedSevenToTwentyNineDays_Recommended(int daysAgo)
    {
        var issue = _analyzer.Analyze(null, [Key("Key1", lastUsedDaysAgo: daysAgo)], Now).Single();

        issue.Severity.Should().Be(AuditSeverity.Recommended);
        issue.ScoreImpact.Should().Be(2);
        issue.Message.Should().Be($"1 API key unused for 7+ days: Key1 ({daysAgo} days).");
    }

    [Theory]
    [InlineData(30)]
    [InlineData(400)]
    public void ApiKey_UnusedThirtyDaysOrMore_Critical(int daysAgo)
    {
        var issue = _analyzer.Analyze(null, [Key("Key1", lastUsedDaysAgo: daysAgo)], Now).Single();

        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.ScoreImpact.Should().Be(5);
    }

    [Fact]
    public void ApiKey_NeverUsed_JudgedFromCreation()
    {
        var fresh = new UniFiOsApiKey { Name = "Fresh", CreatedAt = Now.AddDays(-3) };
        var old = new UniFiOsApiKey { Name = "Old", CreatedAt = Now.AddDays(-45) };

        var issue = _analyzer.Analyze(null, [fresh, old], Now).Single();

        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.Message.Should().Be("1 API key unused for 7+ days: Old (never used, created 45 days ago).");
    }

    [Fact]
    public void ApiKey_NoTimestamps_NotFlagged()
    {
        _analyzer.Analyze(null, [new UniFiOsApiKey { Name = "Mystery" }], Now).Should().BeEmpty();
    }

    [Fact]
    public void ApiKeys_Multiple_OneIssueListingAll_NoPointStacking()
    {
        var keys = new List<UniFiOsApiKey>
        {
            Key("Recent", lastUsedDaysAgo: 1),
            Key("Stale", lastUsedDaysAgo: 10),
            Key("Ancient", lastUsedDaysAgo: 90),
            Key("Older", lastUsedDaysAgo: 35),
        };

        var issues = _analyzer.Analyze(null, keys, Now);

        var issue = issues.Should().ContainSingle().Subject;
        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.ScoreImpact.Should().Be(5);
        issue.Message.Should().Be("3 API keys unused for 7+ days: Ancient (90 days), Older (35 days), Stale (10 days).");
        issue.Metadata!["unused_count"].Should().Be(3);
    }

    [Fact]
    public void ApiKeys_AllUnderThirtyDays_Recommended()
    {
        var issue = _analyzer.Analyze(null, [Key("A", 8), Key("B", 20)], Now).Single();

        issue.Severity.Should().Be(AuditSeverity.Recommended);
        issue.ScoreImpact.Should().Be(2);
    }

    [Fact]
    public void ApiKey_Unnamed_UsesPlaceholder()
    {
        var key = new UniFiOsApiKey { LastUsedAt = Now.AddDays(-10) };

        _analyzer.Analyze(null, [key], Now).Single().Message.Should().Contain("Unnamed key (10 days)");
    }

    #endregion

    #region Local Admin Accounts

    [Fact]
    public void LocalAdmin_SignedInWithinSevenDays_NotFlagged()
    {
        _analyzer.Analyze([LocalAdmin("Admin1", lastLoginDaysAgo: 2)], null, Now).Should().BeEmpty();
    }

    [Fact]
    public void LocalAdmin_UnusedTenDays_Recommended()
    {
        var issue = _analyzer.Analyze([LocalAdmin("Admin1", lastLoginDaysAgo: 10)], null, Now).Single();

        issue.Type.Should().Be(IssueTypes.UnusedLocalAdmins);
        issue.Severity.Should().Be(AuditSeverity.Recommended);
        issue.ScoreImpact.Should().Be(2);
        issue.Message.Should().Be("1 local admin account unused for 7+ days: Admin1 (10 days).");
    }

    [Fact]
    public void LocalAdmin_UnusedThirtyDays_Critical()
    {
        var issue = _analyzer.Analyze([LocalAdmin("Admin1", lastLoginDaysAgo: 30)], null, Now).Single();

        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.ScoreImpact.Should().Be(5);
    }

    [Fact]
    public void LocalAdmin_NeverSignedIn_JudgedFromCreation()
    {
        var newAccount = LocalAdmin("New", lastLoginDaysAgo: null, createdDaysAgo: 2);
        var oldAccount = LocalAdmin("Old", lastLoginDaysAgo: null, createdDaysAgo: 60);

        var issue = _analyzer.Analyze([newAccount, oldAccount], null, Now).Single();

        issue.Message.Should().Be("1 local admin account unused for 7+ days: Old (never signed in, created 60 days ago).");
        issue.Severity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public void LocalAdmin_LastActivityNewerThanLogin_Wins()
    {
        var user = LocalAdmin("Admin1", lastLoginDaysAgo: 40);
        user.LastActivityTime = Now.AddDays(-1).ToUnixTimeSeconds();

        _analyzer.Analyze([user], null, Now).Should().BeEmpty();
    }

    [Fact]
    public void Account_NotActive_NotFlagged()
    {
        var user = LocalAdmin("Disabled", lastLoginDaysAgo: 90);
        user.Status = "DEACTIVATED";

        _analyzer.Analyze([user], null, Now).Should().BeEmpty();
    }

    [Fact]
    public void Account_UiOrSso_NotFlagged()
    {
        var user = LocalAdmin("Cloud", lastLoginDaysAgo: 90);
        user.OnlyLocalAccount = false;

        _analyzer.Analyze([user], null, Now).Should().BeEmpty();
    }

    [Fact]
    public void Account_NoAdminRole_NotFlagged()
    {
        var user = LocalAdmin("EndUser", lastLoginDaysAgo: 90);
        user.Roles = [];

        _analyzer.Analyze([user], null, Now).Should().BeEmpty();
    }

    [Fact]
    public void Account_DisplayName_FallsBackToUsername()
    {
        var user = LocalAdmin("", lastLoginDaysAgo: 10);
        user.Username = "admin1";

        _analyzer.Analyze([user], null, Now).Single().Message.Should().Contain("admin1 (10 days)");
    }

    [Theory]
    [InlineData("netopt")]
    [InlineData("NETOPT")]
    [InlineData("netopt@example.com")]
    public void LocalAdmin_ConnectedAccount_NotFlagged(string connectedAs)
    {
        var connected = LocalAdmin("Network Optimizer", lastLoginDaysAgo: 60);
        connected.Username = "netopt";
        connected.Email = "netopt@example.com";
        var other = LocalAdmin("Other", lastLoginDaysAgo: 60);
        other.Username = "other";

        var issue = _analyzer.Analyze([connected, other], null, Now, connectedAs).Single();

        issue.Message.Should().Be("1 local admin account unused for 7+ days: Other (60 days).");
    }

    [Fact]
    public void LocalAdmin_NoConnectedAccount_AllConsidered()
    {
        var user = LocalAdmin("A", lastLoginDaysAgo: 60);
        user.Username = "a";

        _analyzer.Analyze([user], null, Now, connectedUsername: null).Should().ContainSingle();
    }

    [Fact]
    public void LocalAdmins_Multiple_OneIssue()
    {
        var users = new List<UniFiOsUser>
        {
            LocalAdmin("A", lastLoginDaysAgo: 12),
            LocalAdmin("B", lastLoginDaysAgo: 120),
        };

        var issue = _analyzer.Analyze(users, null, Now).Should().ContainSingle().Subject;

        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.ScoreImpact.Should().Be(5);
        issue.Message.Should().Be("2 local admin accounts unused for 7+ days: B (120 days), A (12 days).");
    }

    #endregion

    [Fact]
    public void BothKinds_Unused_TwoSeparateIssues()
    {
        var issues = _analyzer.Analyze([LocalAdmin("A", 40)], [Key("K", 10)], Now);

        issues.Should().HaveCount(2);
        issues.Should().ContainSingle(i => i.Type == IssueTypes.UnusedLocalAdmins && i.Severity == AuditSeverity.Critical);
        issues.Should().ContainSingle(i => i.Type == IssueTypes.UnusedApiKeys && i.Severity == AuditSeverity.Recommended);
    }

    private static UniFiOsApiKey Key(string name, int lastUsedDaysAgo) => new()
    {
        Name = name,
        CreatedAt = Now.AddDays(-500),
        LastUsedAt = Now.AddDays(-lastUsedDaysAgo),
    };

    private static UniFiOsUser LocalAdmin(string name, int? lastLoginDaysAgo, int createdDaysAgo = 500) => new()
    {
        FullName = name,
        Status = "ACTIVE",
        OnlyLocalAccount = true,
        CreateTime = Now.AddDays(-createdDaysAgo).ToUnixTimeSeconds(),
        LoginTime = lastLoginDaysAgo.HasValue ? Now.AddDays(-lastLoginDaysAgo.Value).ToUnixTimeSeconds() : 0,
        Roles = [new UniFiOsRole { SystemKey = "super_administrator", Name = "Super Admin" }],
    };
}
