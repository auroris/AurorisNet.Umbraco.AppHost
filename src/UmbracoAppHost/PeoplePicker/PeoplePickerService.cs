using System.Text.Json;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using IdentityServer.Directory;
using Microsoft.Extensions.Caching.Memory;

namespace UmbracoAppHost.PeoplePicker;

public sealed record PeoplePickerDefinition(Guid PageKey, Guid BlockKey, PeoplePickerSettings Settings);
public sealed record PeoplePickerResult(DirectoryUser Person, bool Cached = false);

/// <summary>Application-specific contact storage after a new identity passes save-time validation.</summary>
public interface IPeoplePickerSelectionObserver
{
    Task SelectedAsync(FormExecutionContext context, PeoplePickerDefinition picker, PeoplePickerResult selection, CancellationToken ct);
}

/// <summary>Bounded, process-local eligibility cache; never the application's historical contact table.</summary>
public sealed class PeoplePickerCache(TimeProvider clock) : IDisposable
{
    private sealed record Key(Guid Page, Guid Block, string Settings, string Sid);
    private sealed record Entry(DirectoryUser Person, DateTimeOffset Expires);
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 2000 });
    private static Key Identity(PeoplePickerDefinition picker, string sid) => new(picker.PageKey, picker.BlockKey, picker.Settings.Fingerprint, sid);
    public void Store(PeoplePickerDefinition picker, DirectoryUser person)
    {
        if (!picker.Settings.AllowCachedSelection) return;
        var age = TimeSpan.FromMinutes(picker.Settings.CacheMinutes);
        cache.Set(Identity(picker, person.Sid), new Entry(person, clock.GetUtcNow() + age), new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = age });
    }
    public void Remove(PeoplePickerDefinition picker, string sid) => cache.Remove(Identity(picker, sid));
    public IReadOnlyList<DirectoryUser> Read(PeoplePickerDefinition picker)
    {
        if (!picker.Settings.AllowCachedSelection) return [];
        var fingerprint = picker.Settings.Fingerprint;
        return cache.Keys.OfType<Key>().Where(key => key.Page == picker.PageKey && key.Block == picker.BlockKey && key.Settings == fingerprint)
            .Select(key => cache.TryGetValue<Entry>(key, out var entry) && entry!.Expires > clock.GetUtcNow() ? entry.Person : null)
            .OfType<DirectoryUser>().Where(picker.Settings.Includes).ToArray();
    }
    public void Dispose() => cache.Dispose();
}

/// <summary>Resolves canonical people within one published block's scope and rechecks changed SIDs before persistence.</summary>
public sealed class PeoplePickerService(IPeoplePickerDirectory directory, PeoplePickerCache cache,
    IDataSourceUserResolver users, IEnumerable<IPeoplePickerSelectionObserver> observers) : IFormSubmissionProcessor
{
    private readonly HashSet<(Guid Form, string Field, string Sid)> validated = [];
    public bool WasValidated(Guid form, string field, string sid) => validated.Contains((form, field, sid));

    public static bool Permits(PeoplePickerSettings settings, DataSourceUser? user) =>
        settings.AllowedMemberGroups.Length + settings.AllowedUserGroups.Length == 0
        || user is not null && (settings.AllowedMemberGroups.Any(user.IsInMemberGroup) || settings.AllowedUserGroups.Any(user.IsInUserGroup));

    public async Task<(IReadOnlyList<DirectoryUser> People, bool Cached)> SearchAsync(PeoplePickerDefinition picker, string? query, CancellationToken ct)
    {
        picker.Settings.Validate();
        var terms = DirectoryQuery.Tokenize(query);
        DirectoryQuery.EnsureGlobalTerms(terms, picker.Settings.MinTermLength);
        bool Matches(DirectoryUser user) => picker.Settings.Includes(user) && terms.All(term => picker.Settings.SearchAttributes.Any(attribute =>
            PeoplePickerSettings.Attribute(user, attribute)?.Contains(term, StringComparison.OrdinalIgnoreCase) == true));
        try {
            var people = (await directory.SearchAsync(picker.Settings, terms, ct)).Where(Matches)
                .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).Take(picker.Settings.MaxResults).ToArray();
            foreach (var person in people) cache.Store(picker, person);
            return (people, false);
        }
        catch (DirectoryUnavailableException) when (picker.Settings.AllowCachedSelection) {
            var people = cache.Read(picker).Where(Matches).OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).Take(picker.Settings.MaxResults).ToArray();
            if (people.Length == 0) throw;
            return (people, true);
        }
    }

    public async Task<PeoplePickerResult?> SelectAsync(PeoplePickerDefinition picker, string sid, CancellationToken ct)
    {
        picker.Settings.Validate();
        if (sid.Length is < 1 or > 200) throw new DirectoryQueryException("Choose a valid directory identity.");
        try {
            var person = await directory.FindAsync(picker.Settings, sid, ct);
            if (person is null || person.Sid != sid || !picker.Settings.Includes(person)) { cache.Remove(picker, sid); return null; }
            cache.Store(picker, person);
            return new(person);
        }
        catch (DirectoryUnavailableException) when (picker.Settings.AllowCachedSelection) {
            var person = cache.Read(picker).FirstOrDefault(p => p.Sid == sid);
            if (person is null) throw;
            return new(person, true);
        }
    }

    public static IReadOnlyList<PeoplePickerDefinition> Definitions(FormDefinition form)
    {
        var pickers = AppBlockContent.Flatten(form.Block.Areas.SelectMany(area => area))
            .Where(block => block.Content.ContentType.Alias == PeoplePickerBlock.AliasName)
            .Select(block => new PeoplePickerDefinition(form.Page.Key, block.ContentKey, PeoplePickerSettings.Read(block.Content))).ToArray();
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var picker in pickers) {
            foreach (var field in new[] { picker.Settings.IdentityField, picker.Settings.NameField }.Concat(picker.Settings.FieldMappings.Values)) {
                var binding = form.Fields.FirstOrDefault(f => f.Content.Value<string>("field") == field);
                if (!owned.Add(field) || binding is null || binding.Content.ContentType.Alias != "appText" || binding.Content.Value<bool>("readOnly")
                    || !form.Resource.Fields.Any(f => f.Key == field && f.Kind == "text" && !f.ReadOnly))
                    throw new DirectoryQueryException("Each people picker must bind its own writable text fields in the containing form.");
            }
            foreach (var field in picker.Settings.FallbackFields.Values)
                if (!owned.Add(field) || !form.Fields.Any(f => f.Content.Value<string>("field") == field)
                    || !form.Resource.Fields.Any(f => f.Key == field && f.Kind == "text" && f.ReadOnly))
                    throw new DirectoryQueryException("A directory-value mirror must be a separate read-only text field in the containing form.");
        }
        return pickers;
    }

    public async Task<FormSubmissionValues> ProcessAsync(FormDefinition form, JsonElement values, FormRecord? stored, FormExecutionContext context, CancellationToken ct)
    {
        IReadOnlyList<PeoplePickerDefinition> pickers;
        try { pickers = Definitions(form); }
        catch (DirectoryQueryException error) { return new(values, FormResult.Invalid("", error.Message)); }
        if (pickers.Count == 0) return new(values);
        var result = values.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
        foreach (var picker in pickers) {
            var settings = picker.Settings;
            if (!Permits(settings, await users.ResolveAsync(context, ct))) return new(values, FormResult.Denied());
            var sid = Text(result.GetValueOrDefault(settings.IdentityField))?.Trim();
            var previous = stored is null ? null : Text(stored.Values.GetValueOrDefault(settings.IdentityField))?.Trim();
            if (string.IsNullOrEmpty(sid)) {
                if (!settings.AllowManualEntry) return new(values, FormResult.Invalid(settings.IdentityField, "Choose a person from the directory."));
                result[settings.IdentityField] = null;
                foreach (var field in settings.CompactUpperFields) result[field] = settings.Normalize(field, Text(result.GetValueOrDefault(field)));
                continue;
            }
            // A saved identity remains valid for unrelated edits during an outage. Its directory-owned fields cannot be forged.
            if (sid == previous) {
                result[settings.NameField] = stored!.Values.GetValueOrDefault(settings.NameField);
                foreach (var field in settings.FieldMappings.Values)
                    if (!settings.FallbackFields.TryGetValue(field, out var mirror) || !string.IsNullOrEmpty(Text(stored.Values.GetValueOrDefault(mirror))))
                        result[field] = stored.Values.GetValueOrDefault(field);
                foreach (var field in settings.CompactUpperFields) result[field] = settings.Normalize(field, Text(result.GetValueOrDefault(field)));
                continue;
            }
            PeoplePickerResult? selected;
            try { selected = await SelectAsync(picker, sid, ct); }
            catch (Exception error) when (error is DirectoryQueryException or DirectoryUnavailableException) {
                return new(values, FormResult.Invalid(settings.IdentityField, "The directory selection could not be validated. Try again when the directory is available."));
            }
            if (selected is null) return new(values, FormResult.Invalid(settings.IdentityField, "This person is no longer available in this picker's directory scope."));
            result[settings.IdentityField] = selected.Person.Sid;
            result[settings.NameField] = selected.Person.DisplayName;
            foreach (var (attribute, field) in settings.FieldMappings) {
                var value = PeoplePickerSettings.Attribute(selected.Person, attribute);
                result[field] = settings.Normalize(field, !string.IsNullOrWhiteSpace(value) ? value
                    : settings.FallbackFields.ContainsKey(field) ? Text(result.GetValueOrDefault(field)) : null);
            }
            foreach (var observer in observers) await observer.SelectedAsync(context, picker, selected, ct);
            validated.Add((context.FormKey, settings.IdentityField, selected.Person.Sid));
        }
        return new(JsonSerializer.SerializeToElement(result));
    }

    private static string? Text(object? value) => value is JsonElement json ? json.ValueKind == JsonValueKind.String ? json.GetString() : null : value as string;
}
