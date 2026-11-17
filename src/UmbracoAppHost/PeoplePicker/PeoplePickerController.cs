using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using IdentityServer.Directory;
using Microsoft.AspNetCore.Mvc;

namespace UmbracoAppHost.PeoplePicker;

[ApiController]
[Asp.Versioning.ApiVersionNeutral]
[Route("api/people-picker")]
public sealed class PeoplePickerController(AppBlockContent content, AppPageAccess access, IDataSourceUserResolver users,
    FormResourceRegistry resources, PublishedDataCatalog catalog, IEnumerable<IAppBlockPlugin> plugins, PeoplePickerService people) : ControllerBase
{
    [HttpGet("{pageKey:guid}/{blockKey:guid}")]
    public async Task<IActionResult> Get(Guid pageKey, Guid blockKey, [FromQuery] string? s, [FromQuery] string? sid, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (await content.PageAsync(pageKey) is not { } page) return NotFound();
        if (await access.VisitorAsync(page) is not { } visitor) return StatusCode(403);
        var culture = AppBlockContent.RequestCulture(HttpContext);
        var forms = AppBlockContent.Flatten(AppBlockContent.Blocks(page, culture), culture)
            .Where(b => b.Content.ContentType.Alias == "appForm"
                && AppBlockContent.Flatten(b.Areas.SelectMany(a => a)).Any(child => child.ContentKey == blockKey && child.Content.ContentType.Alias == PeoplePickerBlock.AliasName)).ToArray();
        if (forms.Length != 1) return NotFound();
        var form = await FormDefinition.ResolveAsync(page, forms[0].ContentKey, resources, catalog, culture, plugins);
        if (form is null) return NotFound();
        try {
            var picker = PeoplePickerService.Definitions(form).SingleOrDefault(p => p.BlockKey == blockKey);
            if (picker is null) return NotFound();
            var context = new FormExecutionContext(visitor, pageKey, form.Block.ContentKey, form.Revision);
            if (!PeoplePickerService.Permits(picker.Settings, await users.ResolveAsync(context, ct))) return StatusCode(403);
            if (Request.Query["s"].Count > 1 || Request.Query["sid"].Count > 1 || s is not null && sid is not null) return BadRequest();
            if (sid is not null) {
                var selected = await people.SelectAsync(picker, sid, ct);
                return selected is null ? UnprocessableEntity(new { message = "This person is not available in this directory scope." })
                    : Ok(new { person = Project(selected.Person, picker.Settings), cached = selected.Cached });
            }
            var (found, cached) = await people.SearchAsync(picker, s, ct);
            return Ok(new { items = found.Select(person => Project(person, picker.Settings)), cached });
        }
        catch (DirectoryQueryException error) { return UnprocessableEntity(new { message = error.Message }); }
        catch (DirectoryUnavailableException) { return StatusCode(503, new { message = "The directory is unavailable. Try again later." }); }
    }

    // Return only attributes this block requests, never the full directory record.
    private static object Project(DirectoryUser person, PeoplePickerSettings settings) => new {
        sid = person.Sid, displayName = person.DisplayName,
        email = settings.ReturnedAttributes.Contains("mail", StringComparer.OrdinalIgnoreCase) ? person.Email : null,
        attributes = settings.ReturnedAttributes.ToDictionary(name => name, name => PeoplePickerSettings.Attribute(person, name), StringComparer.OrdinalIgnoreCase),
    };
}
