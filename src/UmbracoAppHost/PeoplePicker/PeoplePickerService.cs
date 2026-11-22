using AppBlocks.AccountPickers;
using System.Text.Json;
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
    IDataSourceUserResolver users, IEnumerable<IPeoplePickerSelectionObserver> observers) : IAccountPickerProvider, IFormSubmissionProcessor
{
    private readonly HashSet<(Guid Form, string Field, string Sid)> validated = [];
    public bool WasValidated(Guid form, string field, string sid) => validated.Contains((form, field, sid));

    public static bool Permits(PeoplePickerSettings settings, DataSourceUser? user) =>
        settings.Permits(user);

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

    private static PeoplePickerDefinition DirectoryPicker(AccountPickerDefinition picker) =>
        new(picker.PageKey, picker.BlockKey, (PeoplePickerSettings)picker.Settings);

    // Existing callers can still use the AD-only form API. Stock forms run all picker providers together.
    public static IReadOnlyList<PeoplePickerDefinition> Definitions(FormDefinition form)
    {
        try { return AccountPickerForm.Definitions(form, [new PeoplePickerBlock()]).Select(DirectoryPicker).ToArray(); }
        catch (AccountPickerException error) { throw new DirectoryQueryException(error.Message); }
    }

    public Task<FormSubmissionValues> ProcessAsync(FormDefinition form, JsonElement values, FormRecord? stored, FormExecutionContext context, CancellationToken ct) =>
        new AccountPickerForm([new PeoplePickerBlock()], users, _ => this).ProcessAsync(form, values, stored, context, ct);

    private static AccountPickerItem Project(DirectoryUser person, PeoplePickerSettings settings) =>
        new(person.Sid, person.DisplayName,
            settings.ReturnedAttributes.Contains("mail", StringComparer.OrdinalIgnoreCase) ? person.Email : null,
            settings.ReturnedAttributes.ToDictionary(name => name, name => PeoplePickerSettings.Attribute(person, name), StringComparer.OrdinalIgnoreCase));

    Task<AccountPickerSearch> IAccountPickerProvider.SearchAsync(AccountPickerDefinition definition, string? query, CancellationToken ct)
        => WithDirectoryAsync<AccountPickerSearch>(async () => {
            var picker = DirectoryPicker(definition);
            var (people, cached) = await SearchAsync(picker, query, ct);
            return new(people.Select(person => Project(person, picker.Settings)).ToArray(), cached);
        });

    Task<AccountPickerSelection?> IAccountPickerProvider.ResolveAsync(AccountPickerDefinition definition, string key,
        FormExecutionContext? submission, CancellationToken ct)
        => WithDirectoryAsync<AccountPickerSelection?>(async () => {
            var picker = DirectoryPicker(definition);
            if (await SelectAsync(picker, key, ct) is not { } selected) return null;
            if (submission is not null)
            {
                foreach (var observer in observers) await observer.SelectedAsync(submission, picker, selected, ct);
                validated.Add((submission.FormKey, picker.Settings.IdentityField, selected.Person.Sid));
            }
            return new(Project(selected.Person, picker.Settings), selected.Cached);
        });

    private static async Task<T> WithDirectoryAsync<T>(Func<Task<T>> lookup)
    {
        try { return await lookup(); }
        catch (DirectoryQueryException error) { throw new AccountPickerException(error.Message); }
        catch (DirectoryUnavailableException) { throw new AccountPickerException("The directory is unavailable. Try again later.", unavailable: true); }
    }
}
