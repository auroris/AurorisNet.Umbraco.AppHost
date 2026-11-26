using System.Globalization;
using AppBlocks.DataSources;
using Umbraco.Cms.Core.Services;

namespace UmbracoAppHost.Application;

/// <summary>
/// A list-of-values <see cref="IDataSourceHandler"/> that returns approved, unlocked Umbraco members belonging to one
/// or more member groups, with <c>ACCOUNT_KEY</c> (numeric member ID) and <c>DISPLAY_NAME</c> columns.
/// </summary>
public sealed class MemberGroupChoices(string alias, IReadOnlyList<string> memberGroups, IMemberService members)
    : IDataSourceHandler
{
    public string Alias { get; } = string.IsNullOrWhiteSpace(alias)
        ? throw new ArgumentException("Alias is required.", nameof(alias))
        : alias;

    public IReadOnlyList<string> MemberGroups { get; } = memberGroups;

    /// <summary>Reads approved, unlocked members in the configured groups keyed by numeric member ID.</summary>
    public IReadOnlyDictionary<string, string> Read()
        => MemberGroups
            .SelectMany(members.GetMembersByGroup)
            .DistinctBy(member => member.Key)
            .Where(member => member.IsApproved && !member.IsLockedOut)
            .ToDictionary(
                member => member.Id.ToString(CultureInfo.InvariantCulture),
                member => member.Name ?? member.Username,
                StringComparer.Ordinal);

    public ValueTask<IReadOnlyList<IReadOnlyDictionary<string, object?>>?> ReadAsync(DataSourceOperation op, CancellationToken ct)
        => new(Read()
            .Select(member => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["ACCOUNT_KEY"] = member.Key,
                ["DISPLAY_NAME"] = member.Value,
            })
            .ToArray());
}

/// <summary>Registration helpers for <see cref="MemberGroupChoices"/>.</summary>
public static class MemberGroupChoicesExtensions
{
    /// <summary>
    /// Registers a scoped <see cref="MemberGroupChoices"/> <see cref="IDataSourceHandler"/> under <paramref name="alias"/>
    /// returning approved, unlocked members of <paramref name="memberGroups"/>.
    /// </summary>
    public static IServiceCollection AddMemberGroupChoices(this IServiceCollection services, string alias, params string[] memberGroups)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentNullException.ThrowIfNull(memberGroups);
        var groups = memberGroups.Where(group => !string.IsNullOrWhiteSpace(group)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return services.AddScoped<IDataSourceHandler>(sp => new MemberGroupChoices(alias, groups, sp.GetRequiredService<IMemberService>()));
    }
}
