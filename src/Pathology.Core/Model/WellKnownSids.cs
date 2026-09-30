namespace Pathology.Core.Model;

/// <summary>
/// SIDs PATHology names or builds perspectives from. Names come from this table rather than an account
/// lookup: <c>LookupAccountSid</c> can reach a domain controller, and a scan stays on this machine.
/// </summary>
public static class WellKnownSids
{
    public const string Everyone = "S-1-1-0";
    public const string Local = "S-1-2-0";
    public const string CreatorOwner = "S-1-3-0";
    public const string OwnerRights = "S-1-3-4";
    public const string Interactive = "S-1-5-4";
    public const string AuthenticatedUsers = "S-1-5-11";
    public const string ThisOrganization = "S-1-5-15";
    public const string LocalSystem = "S-1-5-18";
    public const string LocalService = "S-1-5-19";
    public const string NetworkService = "S-1-5-20";
    public const string Administrators = "S-1-5-32-544";
    public const string Users = "S-1-5-32-545";
    public const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    public const string LowIntegrity = "S-1-16-4096";
    public const string MediumIntegrity = "S-1-16-8192";
    public const string HighIntegrity = "S-1-16-12288";
    public const string SystemIntegrity = "S-1-16-16384";

    /// <summary>
    /// The user SID of the synthetic <see cref="Perspective.StandardUser"/>. It sits in the <c>S-1-5-21-0-0-*</c>
    /// range the redactor uses for placeholders, so it can never match a real account.
    /// </summary>
    public const string SyntheticStandardUser = "S-1-5-21-0-0-0-1000";

    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        [Everyone] = "Everyone",
        [Local] = "LOCAL",
        [CreatorOwner] = "CREATOR OWNER",
        [OwnerRights] = "OWNER RIGHTS",
        [Interactive] = "INTERACTIVE",
        [AuthenticatedUsers] = "Authenticated Users",
        [ThisOrganization] = "This Organization",
        [LocalSystem] = "SYSTEM",
        [LocalService] = "LOCAL SERVICE",
        [NetworkService] = "NETWORK SERVICE",
        [Administrators] = "Administrators",
        [Users] = "Users",
        [TrustedInstaller] = "TrustedInstaller",
        [LowIntegrity] = "Low Mandatory Level",
        [MediumIntegrity] = "Medium Mandatory Level",
        [HighIntegrity] = "High Mandatory Level",
        [SystemIntegrity] = "System Mandatory Level",
        [SyntheticStandardUser] = "a standard user",
    };

    /// <summary>A display name for a well-known SID, or the SID itself.</summary>
    public static string NameOf(string sid) => Names.TryGetValue(sid, out var name) ? name : sid;
}
