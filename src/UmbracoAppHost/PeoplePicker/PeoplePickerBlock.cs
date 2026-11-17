using System.Text.Json;
using AppBlocks.Blocks;
using IdentityServer.Directory;

namespace UmbracoAppHost.PeoplePicker;

/// <summary>A directory companion to the fields of an AppBlocks form.</summary>
public sealed class PeoplePickerBlock : IAppBlockPlugin
{
    public const string AliasName = "appPeoplePicker";
    public string Alias => AliasName;
    public string ViewPath => "~/Views/Partials/PeoplePicker.cshtml";
    public bool RequiresUser => false;
    public bool VariesByData => true;
    public void Validate(JsonElement block, List<string> errors)
    {
        try { PeoplePickerSettings.Read(block); }
        catch (DirectoryQueryException error) { errors.Add("People picker: " + error.Message); }
    }
}
