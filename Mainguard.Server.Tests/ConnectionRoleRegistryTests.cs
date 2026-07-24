using Mainguard.Server.Auth;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// MG-12 — <see cref="ConnectionRoleRegistry.Resolve"/> must fail <b>closed</b>. It previously returned
/// <see cref="ConnectionRole.Operator"/> for every token that was not a registered coordinator token,
/// so an unknown or malformed credential resolved to the FULL-privilege role. Combined with
/// <c>IssueCoordinatorToken</c> having no production callers, the registry was always empty and every
/// connection resolved to Operator.
/// </summary>
public sealed class ConnectionRoleRegistryTests
{
    private const string OperatorToken = "0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void OperatorToken_ResolvesToOperator()
    {
        var registry = new ConnectionRoleRegistry();
        Assert.Equal(ConnectionRole.Operator, registry.Resolve(OperatorToken, OperatorToken));
    }

    [Fact]
    public void RegisteredCoordinatorToken_ResolvesToCoordinator()
    {
        var registry = new ConnectionRoleRegistry();
        var token = registry.IssueCoordinatorToken();
        Assert.Equal(ConnectionRole.Coordinator, registry.Resolve(token, OperatorToken));
    }

    [Fact]
    public void UnknownToken_ResolvesToLeastPrivilege_NotOperator()
    {
        var registry = new ConnectionRoleRegistry();
        Assert.Equal(ConnectionRole.Coordinator, registry.Resolve("some-unknown-token", OperatorToken));
    }

    [Fact]
    public void NullToken_ResolvesToLeastPrivilege_NotOperator()
    {
        var registry = new ConnectionRoleRegistry();
        Assert.Equal(ConnectionRole.Coordinator, registry.Resolve(null, OperatorToken));
    }

    [Fact]
    public void IssuedToken_IsRecognisedAsACoordinatorCredential()
    {
        var registry = new ConnectionRoleRegistry();
        var token = registry.IssueCoordinatorToken();

        // This is what lets BearerTokenInterceptor authenticate the credential (MG-12).
        Assert.True(registry.IsCoordinatorToken(token));
        Assert.False(registry.IsCoordinatorToken(OperatorToken));
        Assert.False(registry.IsCoordinatorToken(null));
    }

    [Fact]
    public void IssuedTokens_AreDistinct_And64HexChars()
    {
        var registry = new ConnectionRoleRegistry();
        var a = registry.IssueCoordinatorToken();
        var b = registry.IssueCoordinatorToken();

        Assert.NotEqual(a, b);
        Assert.Equal(64, a.Length);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }
}
