using System.Text.Json;
using AppBlocks.Blocks;

namespace UmbracoAppHost.Authentication;

public sealed class MemberSignInBlock : IAppBlockPlugin
{
    public const string BlockAlias = "appMemberSignIn";
    public string Alias => BlockAlias;
    public string ViewPath => "~/Views/Partials/ApplicationBlocks/MemberSignIn.cshtml";
    public bool RequiresUser => false;
    public bool VariesByData => true;
    public bool CanNestInForm => false;
    public void Validate(JsonElement block, List<string> errors) { }
}
