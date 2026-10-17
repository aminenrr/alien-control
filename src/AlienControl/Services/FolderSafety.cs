using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AlienControl.Services;

/// <summary>1.3.0-beta.1 (review #6): warns when the exe's folder can be written by non-admins (Users, Authenticated
/// Users, Everyone, Interactive). The app runs elevated, so files there should only be changeable by admins.</summary>
public static class FolderSafety
{
    private static readonly WellKnownSidType[] Risky =
    {
        WellKnownSidType.BuiltinUsersSid, WellKnownSidType.AuthenticatedUserSid,
        WellKnownSidType.WorldSid, WellKnownSidType.InteractiveSid,
    };

    private const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;

    public static bool UserWritable(string dir)
    {
        try
        {
            var rules = new DirectoryInfo(dir).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule r in rules)
            {
                if (r.AccessControlType != AccessControlType.Allow || (r.FileSystemRights & WriteRights) == 0) continue;
                var sid = (SecurityIdentifier)r.IdentityReference;
                foreach (var t in Risky) if (sid.IsWellKnown(t)) return true;
                // the signed-in user's own SID (e.g. a folder in their profile or C:\Tools created by them)
                if (sid == WindowsIdentity.GetCurrent().User) return true;
            }
        }
        catch (Exception ex) { Log.Warn("FolderSafety", ex); }
        return false;
    }
}
