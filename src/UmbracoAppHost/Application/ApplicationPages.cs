using System.Security.Cryptography;
using System.Text;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace UmbracoAppHost.Application;

/// <summary>
/// Maps a published page key to a required member group name for <c>AppMemberPage.cshtml</c>.
/// An empty <paramref name="Role"/> admits any signed-in member.
/// </summary>
public sealed record ApplicationPageRole(Guid Page, string Role);

/// <summary>
/// Helper for code-first applications installing and publishing deterministic content nodes and member-gated pages.
/// </summary>
public sealed class ApplicationPages(IContentService contents, IContentTypeService types, ITemplateService templates)
{
    public const string MemberPageTemplateAlias = "AppMemberPage";

    /// <summary>Derives a stable deterministic GUID from <paramref name="scope"/> and <paramref name="name"/>.</summary>
    public static Guid Key(string scope, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}:{name}"))[..16]);
    }

    /// <summary>
    /// Returns the existing content node for <paramref name="key"/> or creates one under <paramref name="parentId"/>
    /// with <paramref name="contentTypeAlias"/>, updating its content type and default template when needed.
    /// </summary>
    public IContent GetOrCreate(Guid key, string name, int parentId, string contentTypeAlias)
    {
        var type = types.Get(contentTypeAlias)
            ?? throw new InvalidOperationException($"Content type '{contentTypeAlias}' is not installed.");
        var node = contents.GetById(key) ?? contents.Create(name, parentId, contentTypeAlias);
        node.Key = key;
        if (node is Content concrete && !string.Equals(node.ContentType.Alias, contentTypeAlias, StringComparison.OrdinalIgnoreCase))
        {
            concrete.ChangeContentType(type.ToSimple());
            concrete.Properties.EnsureCleanPropertyTypes(type.CompositionPropertyTypes);
        }
        node.TemplateId = type.DefaultTemplate?.Id;
        return node;
    }

    /// <summary>Assigns <paramref name="templateAlias"/> (by default <see cref="MemberPageTemplateAlias"/>) to <paramref name="page"/>.</summary>
    public async Task SetMemberTemplateAsync(IContent page, string templateAlias = MemberPageTemplateAlias)
    {
        var template = (await templates.GetAllAsync([templateAlias])).SingleOrDefault()
            ?? throw new InvalidOperationException($"Template '{templateAlias}' is not installed.");
        page.TemplateId = template.Id;
    }

    /// <summary>Saves and publishes <paramref name="node"/> across all cultures, throwing if validation fails.</summary>
    public void Publish(IContent node)
    {
        contents.Save(node);
        var result = contents.Publish(node, ["*"]);
        if (!result.Success)
            throw new InvalidOperationException(
                $"Could not publish {node.Name}: {result.Result}. "
                + string.Join("; ", result.EventMessages?.GetAll().Select(m => m.Message) ?? []));
    }
}
