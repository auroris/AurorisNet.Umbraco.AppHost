using AppBlocks.AccountPickers;
using IdentityServer.Directory;

namespace UmbracoAppHost.PeoplePicker;

public sealed class PeoplePickerBlock : AccountPickerBlock
{
    public const string AliasName = "appPeoplePicker";
    public override string Alias => AliasName;
    public override string ViewPath => "~/Views/Partials/PeoplePicker.cshtml";

    public override PeoplePickerSettings ReadSettings(Func<string, string> value)
    {
        try { return PeoplePickerSettings.Read(value); }
        catch (DirectoryQueryException error) { throw new AccountPickerException(error.Message); }
    }
}
