using MailKit;

namespace LceMcp;

internal static class ImapFolderRoles
{
    public static string Infer(FolderAttributes attributes)
    {
        if (attributes.HasFlag(FolderAttributes.Inbox))
            return "inbox";

        if (attributes.HasFlag(FolderAttributes.Sent))
            return "sent";

        if (attributes.HasFlag(FolderAttributes.Archive))
            return "archive";

        if (attributes.HasFlag(FolderAttributes.All))
            return "all_mail";

        if (attributes.HasFlag(FolderAttributes.Drafts))
            return "drafts";

        if (attributes.HasFlag(FolderAttributes.Trash))
            return "trash";

        if (attributes.HasFlag(FolderAttributes.Junk))
            return "spam";

        return "custom";
    }
}
