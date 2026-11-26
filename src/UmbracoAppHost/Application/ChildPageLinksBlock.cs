using System.Text.Json;
using AppBlocks.Blocks;

namespace UmbracoAppHost.Application;

/// <summary>Built-in block that renders navigation cards for the current page's accessible child pages.</summary>
public sealed class ChildPageLinksBlock : IAppBlockPlugin
{
    public const string BlockAlias = "appChildPageLinks";
    public static readonly Guid ElementKey = new("7a4e2c91-6f3b-4d8a-9c52-1b8f0d4e3a76");
    public string Alias => BlockAlias;
    public string ViewPath => "~/Views/Partials/ApplicationBlocks/ChildPageLinks.cshtml";
    public bool RequiresUser => false;
    public bool VariesByData => true;
    public bool CanNestInForm => false;
    public void Validate(JsonElement block, List<string> errors) { }
}
