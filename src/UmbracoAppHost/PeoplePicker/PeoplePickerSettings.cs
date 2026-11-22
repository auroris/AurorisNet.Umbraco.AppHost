using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Umbraco.Cms.Core.Models.PublishedContent;
using IdentityServer.Directory;
using AppBlocks.AccountPickers;
using AppBlocks.DataSources;

namespace UmbracoAppHost.PeoplePicker;

/// <summary>One published picker's configuration. Editors use the ordinary block properties.</summary>
public sealed record PeoplePickerSettings : AccountPickerSettings
{
    public PeoplePickerSettings() { AllowManualEntry = true; Label = "Find person"; }
    public override bool AllowsEmpty => AllowManualEntry;
    public override bool Permits(DataSourceUser? user) =>
        AllowedMemberGroups.Length + AllowedUserGroups.Length == 0
        || user is not null && (AllowedMemberGroups.Any(user.IsInMemberGroup) || AllowedUserGroups.Any(user.IsInUserGroup));

    public string SearchRoot { get; init; } = "";
    public bool EntireDomain { get; init; }
    public bool IncludeChildOus { get; init; } = true;
    public string[] SearchAttributes { get; init; } = ["displayName"];
    public string[] ReturnedAttributes { get; init; } = ["displayName", "mail"];
    public string[] AllowedMemberGroups { get; init; } = [];
    public string[] AllowedUserGroups { get; init; } = [];
    public bool EnabledOnly { get; init; } = true;
    public string AdditionalFilter { get; init; } = "";
    public string ExcludedAccountPattern { get; init; } = "";
    public int MinTermLength { get; init; } = 2;
    public int MaxResults { get; init; } = 20;
    public int TimeoutSeconds { get; init; } = 5;
    public bool AllowCachedSelection { get; init; }
    public int CacheMinutes { get; init; } = 15;

    private static readonly Regex AttributeName = new("^[A-Za-z][A-Za-z0-9-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex FieldName = new("^[A-Za-z][A-Za-z0-9_]{0,99}$", RegexOptions.CultureInvariant);

    // Umbraco converts an absent integer to 0 and an absent checkbox to false. Read the authored
    // value so omitted settings use our defaults while explicitly saved 0/false stays explicit.
    public static PeoplePickerSettings Read(IPublishedElement element) => Read(alias => element.GetProperty(alias)?.GetSourceValue()?.ToString() ?? "");
    public static PeoplePickerSettings Read(JsonElement block)
    {
        var values = block.TryGetProperty("values", out var properties) && properties.ValueKind == JsonValueKind.Array
            ? properties.EnumerateArray().ToDictionary(p => p.GetProperty("alias").GetString()!, p => p.GetProperty("value").ToString())
            : new Dictionary<string, string>();
        return Read(alias => values.GetValueOrDefault(alias) ?? "");
    }

    public static PeoplePickerSettings Read(Func<string, string> text)
    {
        string Text(string key) => text(key).Trim();
        bool Flag(string key, bool fallback = false) => Text(key) is "" ? fallback : Text(key) is "1" or "true" or "True";
        int Number(string key, int fallback) => Text(key) is "" ? fallback
            : int.TryParse(Text(key), out var value) ? value : throw new DirectoryQueryException($"{key} must be a whole number.");
        string[] List(string key, params string[] fallback) => Text(key) is "" ? fallback : Split(Text(key));
        var result = new PeoplePickerSettings {
            SearchRoot = Text("searchRoot"), EntireDomain = Flag("entireDomain"), IncludeChildOus = Flag("includeChildOus", true),
            SearchAttributes = List("searchAttributes", "displayName"), ReturnedAttributes = List("returnedAttributes", "displayName", "mail"),
            AllowedMemberGroups = Lines(Text("allowedMemberGroups")), AllowedUserGroups = Lines(Text("allowedUserGroups")),
            EnabledOnly = Flag("enabledOnly", true), AdditionalFilter = Text("additionalFilter"), ExcludedAccountPattern = Text("excludedAccountPattern"),
            MinTermLength = Number("minTermLength", 2), MaxResults = Number("maxResults", 20), TimeoutSeconds = Number("timeoutSeconds", 5),
            AllowManualEntry = Flag("allowManualEntry", true), AllowCachedSelection = Flag("allowCachedSelection"), CacheMinutes = Number("cacheMinutes", 15),
            IdentityField = Text("identityField"), NameField = Text("nameField"), FieldMappings = Pairs(Text("fieldMappings")),
            FallbackFields = Pairs(Text("fallbackFields")), CompactUpperFields = List("compactUpperFields"),
            Label = Text("label") is { Length: > 0 } label ? label : "Find person",
        };
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (SearchRoot.Length == 0 && !EntireDomain) throw new DirectoryQueryException("Enter a search OU, or explicitly enable Search entire domain.");
        if (SearchRoot.Length > 2048 || SearchRoot.IndexOfAny(['\0', '\r', '\n']) >= 0
            || (SearchRoot.Length > 0 && !Regex.IsMatch(DistinguishedName, @"^(OU|CN|DC)=.+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new DirectoryQueryException("Search OU must be a distinguished name, such as OU=People,DC=example,DC=com (optionally prefixed with LDAP://).");
        if (SearchAttributes.Length is < 1 or > 16 || ReturnedAttributes.Length > 32
            || SearchAttributes.Concat(ReturnedAttributes).Any(a => !AttributeName.IsMatch(a)))
            throw new DirectoryQueryException("Use LDAP attribute names, with at most 16 search attributes and 32 returned attributes.");
        if (MinTermLength is < 2 or > 32 || MaxResults is < 1 or > 50 || TimeoutSeconds is < 1 or > 15 || CacheMinutes is < 1 or > 1440)
            throw new DirectoryQueryException("Use 2–32 characters per term, 1–50 results, a 1–15 second timeout and a 1–1440 minute cache age.");
        if (AllowedMemberGroups.Length + AllowedUserGroups.Length > 100 || AllowedMemberGroups.Concat(AllowedUserGroups).Any(g => g.Length > 200))
            throw new DirectoryQueryException("Use at most 100 allowed groups, each at most 200 characters.");
        if (!FieldName.IsMatch(IdentityField) || !FieldName.IsMatch(NameField) || IdentityField == NameField)
            throw new DirectoryQueryException("Choose distinct identity and name fields on the containing form.");
        if (FieldMappings.Count > 32 || FieldMappings.Any(m => !AttributeName.IsMatch(m.Key) || !FieldName.IsMatch(m.Value)
            || !ReturnedAttributes.Contains(m.Key, StringComparer.OrdinalIgnoreCase)))
            throw new DirectoryQueryException("Each mapping must be a returned LDAP attribute followed by = and its form field.");
        var targets = new[] { IdentityField, NameField }.Concat(FieldMappings.Values).ToArray();
        if (targets.Distinct(StringComparer.Ordinal).Count() != targets.Length || FallbackFields.Any(p => !FieldMappings.Values.Contains(p.Key)
            || !FieldName.IsMatch(p.Value) || targets.Contains(p.Value)) || FallbackFields.Values.Distinct().Count() != FallbackFields.Count
            || CompactUpperFields.Any(f => !FieldMappings.Values.Contains(f)))
            throw new DirectoryQueryException("Map each field once. Fallback fields must name a mapped field and a separate read-only directory-value field.");
        if (AdditionalFilter.Length > 2048 || (AdditionalFilter.Length > 0 && !ValidFilter(AdditionalFilter)))
            throw new DirectoryQueryException("Additional LDAP filter must be a complete, balanced parenthesized assertion.");
        if (ExcludedAccountPattern.Length > 256) throw new DirectoryQueryException("The excluded account pattern is too long.");
        try { if (ExcludedAccountPattern.Length > 0) _ = new Regex(ExcludedAccountPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
        catch (ArgumentException) { throw new DirectoryQueryException("The excluded account name pattern is not a valid regular expression."); }
    }

    public string DistinguishedName => SearchRoot.StartsWith("LDAP://", StringComparison.OrdinalIgnoreCase) ? SearchRoot[7..] : SearchRoot;
    [JsonIgnore]
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
    public string[] Properties => new[] { "displayName", "mail", "sAMAccountName", "givenName", "objectSid", "objectGUID", "userAccountControl" }
        .Concat(SearchAttributes).Concat(ReturnedAttributes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public bool Includes(DirectoryUser user)
    {
        if (EnabledOnly && !user.Enabled) return false;
        try { return ExcludedAccountPattern.Length == 0 || !Regex.IsMatch(user.UserName, ExcludedAccountPattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
        catch (RegexMatchTimeoutException) { throw new DirectoryQueryException("The excluded account name pattern took too long to evaluate."); }
    }

    public static string? Attribute(DirectoryUser user, string name) => name.ToLowerInvariant() switch {
        "objectsid" => user.Sid, "objectguid" => user.Guid, "displayname" => user.DisplayName, "samaccountname" => user.UserName,
        "givenname" => user.GivenName, "sn" => user.Surname, "mail" => user.Email, "telephonenumber" => user.Telephone,
        _ => user.Attributes?.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value,
    };

    private static string[] Split(string text) => text.Split([' ', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static Dictionary<string, string> Pairs(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(text)) {
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace) || !result.TryAdd(parts[0], parts[1]))
                throw new DirectoryQueryException("Use one unique attribute=field mapping per line.");
        }
        return result;
    }
    private static bool ValidFilter(string text)
    {
        if (text[0] != '(' || text[^1] != ')' || text.Contains('\0')) return false;
        var depth = 0;
        for (var i = 0; i < text.Length; i++) {
            if (text[i] == '\\') { if (i + 2 >= text.Length || !char.IsAsciiHexDigit(text[i + 1]) || !char.IsAsciiHexDigit(text[i + 2])) return false; i += 2; continue; }
            if (text[i] == '(') depth++;
            if (text[i] == ')' && --depth < 0) return false;
            if (depth == 0 && i != text.Length - 1) return false;
        }
        return depth == 0;
    }
}
