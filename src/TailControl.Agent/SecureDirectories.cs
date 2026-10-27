using System.Security.AccessControl;
using System.Security.Principal;

namespace TailControl.Agent;

/// <summary>
/// ACL-urile folderelor agentului. Serviciul ruleaza ca LocalService:
/// <list type="bullet">
/// <item><c>config\</c>: doar administratorii si SYSTEM scriu; serviciul doar citeste.
/// Un agent compromis nu poate autoriza clienti noi.</item>
/// <item><c>logs\</c>: serviciul poate scrie (audit).</item>
/// </list>
/// ACL-ul e protejat (fara mostenire de la parinte).
/// </summary>
public static class SecureDirectories
{
    public static readonly SecurityIdentifier ServiceAccount = new(WellKnownSidType.LocalServiceSid, null);

    public static void EnsureConfigDirectory(string path) =>
        Ensure(path, FileSystemRights.ReadAndExecute);

    public static void EnsureLogDirectory(string path) =>
        Ensure(path, FileSystemRights.Modify);

    private static void Ensure(string path, FileSystemRights serviceRights)
    {
        var directory = Directory.CreateDirectory(path);

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            ServiceAccount, serviceRights, inherit, PropagationFlags.None, AccessControlType.Allow));

        directory.SetAccessControl(security);
    }
}
