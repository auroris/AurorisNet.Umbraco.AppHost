using DirectoryAccounts;
using IdentityServer.Sso;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models.Membership;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// AppHost's account retirement, for the Add from directory dialog: an account renamed to its SID
/// or to the retired- prefix is retired, and the advice follows "Retiring accounts" in the README.
/// </summary>
internal sealed class AppHostRetiredAccountPolicy(IOptions<AccountsSettings> accounts) : IRetiredAccountPolicy
{
    public bool IsRetired(IMembershipUser account) => AccountRetirement.IsRetired(account.Username);

    public string RestoreAdvice(DirectoryAccountKind kind)
    {
        var (noun, section) = kind == DirectoryAccountKind.User ? ("user", "Users") : ("member", "Members");
        return "Its username is now that identity. "
            + (accounts.Value.LinkByUserNameOnFirstSignIn
                ? $"To bring the person back, enable that {noun} in {section} and give it back its username and email: their next sign-in links it again"
                : $"To give the person an account again, change that {noun}'s username to one that starts with {AccountRetirement.RetiredUserNamePrefix}, "
                    + "which keeps it retired with its history, then add them again")
            + " (see \"Retiring accounts\" in the README).";
    }
}

/// <summary>
/// AppHost's single sign-on, for the Add from directory dialog: links from the removed Windows
/// provider still count, and Accounts:LinkByUserNameOnFirstSignIn decides whether a first
/// sign-in claims an account by its name.
/// </summary>
internal sealed class AppHostSignInLinkPolicy(IOptions<AccountsSettings> accounts) : ISignInLinkPolicy
{
    public IEnumerable<string> PreviousLoginProviders(DirectoryAccountKind kind)
        => [kind == DirectoryAccountKind.User ? LegacyWindowsLoginSchemes.BackOffice : LegacyWindowsLoginSchemes.Member];

    public bool FirstSignInLinksByName => accounts.Value.LinkByUserNameOnFirstSignIn;
}
