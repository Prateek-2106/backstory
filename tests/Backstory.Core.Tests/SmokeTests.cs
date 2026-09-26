using Backstory.Core;

namespace Backstory.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void ServiceName_IsBackstory()
    {
        Assert.Equal("backstory", BackstoryInfo.ServiceName);
    }

    [Fact]
    public void Version_IsPresent()
    {
        Assert.False(string.IsNullOrWhiteSpace(BackstoryInfo.Version));
    }
}
