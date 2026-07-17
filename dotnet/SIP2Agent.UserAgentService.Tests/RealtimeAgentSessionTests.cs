using SIP2Agent.UserAgentService.Service;
using Xunit;

namespace SIP2Agent.UserAgentService.Tests;

public sealed class RealtimeAgentSessionTests
{
    [Fact]
    public void OutputIdentity_ValidatesProviderIdentifiersOnceAtConstruction()
    {
        Assert.Throws<ArgumentException>(
            () => new RealtimeOutputIdentity("", "item", 0));
        Assert.Throws<ArgumentException>(
            () => new RealtimeOutputIdentity("response", " ", 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RealtimeOutputIdentity("response", "item", -1));

        Assert.Equal(
            new RealtimeOutputIdentity("response", "item", 3),
            new RealtimeOutputIdentity("response", "item", 3));
    }
}
