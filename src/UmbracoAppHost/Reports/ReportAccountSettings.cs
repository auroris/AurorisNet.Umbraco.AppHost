namespace UmbracoAppHost.Reports;

/// <summary>
/// This host's report keys in the <c>Accounts</c> section. The rest of that section belongs to the
/// <c>AurorisNet.Umbraco.IdentityServer.SSO</c> package's <c>AccountsSettings</c>; both bind from the same section,
/// so a site's configuration stays as it was. Array properties have no code defaults on purpose: the configuration
/// binder appends configured elements to a pre-populated array.
/// </summary>
public sealed class ReportAccountSettings
{
    /// <summary>The section both this class and the package's account settings bind from.</summary>
    public const string SectionName = "Accounts";

    /// <summary>
    /// Umbraco user group aliases whose backoffice members are Interactive Reports administrators.
    /// A member is an administrator when a backoffice user with the same sign-in identity belongs
    /// to one of these groups.
    /// </summary>
    public string[] ReportAdministratorUserGroups { get; set; } = [];

    /// <summary>
    /// Lets an anonymous visitor read report blocks as a plain AppBlocks site does: on a page Umbraco Public Access
    /// leaves open, a report block whose SQL binds no <c>:USER_*</c> value may be viewed, queried and exported without
    /// signing in. A report block's own table or SQL counts as Public and follows its page. Off by default, so every
    /// report needs an account: this host's export carries no Public Access rules, and a new or upgraded site would
    /// otherwise show every such block's rows to anyone. Other reports always need an account.
    /// </summary>
    public bool AllowAnonymousReportBlocks { get; set; }
}
