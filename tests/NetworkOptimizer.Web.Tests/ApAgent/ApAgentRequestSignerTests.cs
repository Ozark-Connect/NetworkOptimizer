using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

public class ApAgentRequestSignerTests
{
    private const string Token = "a-per-agent-token";

    // Duplicated verbatim in the agent's TestCanonicalFormMatchesTheServer. Two languages build
    // this string independently, so if either drifts they stop agreeing and every agent 401s.
    [Fact]
    public void CanonicalFormMatchesTheAgent()
    {
        var sig = ApAgentRequestSigner.Signature(Token, "GET", "/clients", "1700000000", "n1", null);
        Assert.Equal("jIrgeEUstgz5okESBy5t4t/LVTSW2/Mcf1kecvFgfoo=", sig);
    }

    [Fact]
    public void HeaderNeverCarriesTheToken()
    {
        var header = ApAgentRequestSigner.Sign(Token, "GET", "/clients", null);
        Assert.DoesNotContain(Token, header);
    }

    [Fact]
    public void EveryRequestGetsItsOwnNonce()
    {
        // A repeated nonce is a replay to the agent, so it would reject our own second request.
        var first = ApAgentRequestSigner.Sign(Token, "GET", "/clients", null);
        var second = ApAgentRequestSigner.Sign(Token, "GET", "/clients", null);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void BodyIsCovered()
    {
        var a = ApAgentRequestSigner.Signature(Token, "POST", "/clients/aa/bss-transitions", "1700000000", "n1", "{\"duration\":100}");
        var b = ApAgentRequestSigner.Signature(Token, "POST", "/clients/aa/bss-transitions", "1700000000", "n1", "{\"duration\":9999}");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void MethodAndPathAreCovered()
    {
        var get = ApAgentRequestSigner.Signature(Token, "GET", "/clients", "1700000000", "n1", null);
        var post = ApAgentRequestSigner.Signature(Token, "POST", "/clients", "1700000000", "n1", null);
        var other = ApAgentRequestSigner.Signature(Token, "GET", "/radios", "1700000000", "n1", null);

        Assert.NotEqual(get, post);
        Assert.NotEqual(get, other);
    }

    [Fact]
    public void AnotherAgentsTokenProducesADifferentSignature()
    {
        var mine = ApAgentRequestSigner.Signature(Token, "GET", "/clients", "1700000000", "n1", null);
        var theirs = ApAgentRequestSigner.Signature("a-different-agents-token", "GET", "/clients", "1700000000", "n1", null);
        Assert.NotEqual(mine, theirs);
    }
}
