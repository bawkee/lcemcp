using MailKit;

namespace LceMcp.Tests;

public sealed class ImapFolderDiscoveryTests
{
    [Fact]
    public void GmailNonExistentContainerIsNotSelectable()
    {
        var attributes = FolderAttributes.NonExistent
            | FolderAttributes.Subscribed
            | FolderAttributes.HasChildren;

        Assert.False(ImapFolderDiscovery.IsSelectable(attributes));
    }

    [Fact]
    public void OrdinaryFolderIsSelectable()
    {
        Assert.True(ImapFolderDiscovery.IsSelectable(FolderAttributes.HasNoChildren));
    }
}
