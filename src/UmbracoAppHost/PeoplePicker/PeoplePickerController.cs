using AppBlocks.AccountPickers;
using Microsoft.AspNetCore.Mvc;

namespace UmbracoAppHost.PeoplePicker;

/// <summary>The original AD lookup URL and response format used by existing forms.</summary>
[ApiController]
[Asp.Versioning.ApiVersionNeutral]
[Route("api/people-picker")]
public sealed class PeoplePickerController(AccountPickerLookup lookup) : ControllerBase
{
    [HttpGet("{pageKey:guid}/{blockKey:guid}")]
    public Task<IResult> Get(Guid pageKey, Guid blockKey, CancellationToken ct) =>
        lookup.GetAsync(HttpContext, pageKey, blockKey, ct, PeoplePickerBlock.AliasName, "sid",
            item => new { sid = item.Key, item.DisplayName, email = item.Detail, item.Attributes });
}
