using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Umbraco.Cms.Web.Common.Hosting;

namespace UmbracoAppHost.Hosting;

/// <summary>Include the scoped modules' content in Umbraco's immutable asset URLs. Other changes to
/// AppHost leave browsers' backoffice asset caches intact.</summary>
internal sealed class InstallationBackOfficePaths(IBackOfficePathGenerator inner, BackOfficeAuthFiles files) : IBackOfficePathGenerator
{
    public string BackOfficePath => inner.BackOfficePath;
    public string BackOfficeVirtualDirectory => inner.BackOfficeVirtualDirectory;
    public string BackOfficeCacheBustHash { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        inner.BackOfficeCacheBustHash + files.ContentHash)));
    public string BackOfficeAssetsPath => BackOfficeVirtualDirectory.TrimEnd('/') + "/" + BackOfficeCacheBustHash;
}

/// <summary>Umbraco 18 shares auth messages, refresh locks, popup targets and temporary redirect state by
/// origin. Scope the names in its own modules so applications mounted at different paths can sign in independently.</summary>
internal sealed class BackOfficeAuthFiles : IFileProvider
{
    private readonly Dictionary<string, IFileInfo> _files = new(StringComparer.OrdinalIgnoreCase);
    public string ContentHash { get; }

    public BackOfficeAuthFiles(IWebHostEnvironment environment, InstallationCookies cookies)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var suffix = cookies.Suffix;
        Add("auth", source =>
        {
            foreach (var key in new[] { "umb:auth", "umb:token-refresh", "umb:pkce" })
                source = source.Replace(JsonSerializer.Serialize(key), JsonSerializer.Serialize(key + suffix), StringComparison.Ordinal);

            // External providers can supply their own popup name. Keep reserved browser targets such as _blank.
            return source.Replace("window.open(", "openAppHostAuthPopup(", StringComparison.Ordinal) + $$"""

                function openAppHostAuthPopup(url, target, features) {
                  const name = target && !target.startsWith("_") ? target + {{JsonSerializer.Serialize(suffix)}} : target;
                  return window.open(url, name, features);
                }
                """;
        });
        Add("utils", source => source.Replace("\"umb:auth:redirect\"", JsonSerializer.Serialize("umb:auth:redirect" + suffix),
            StringComparison.Ordinal));
        ContentHash = Convert.ToHexStringLower(hash.GetHashAndReset());

        void Add(string module, Func<string, string> update)
        {
            var path = "/umbraco/backoffice/packages/core/" + module + "/index.js";
            var original = environment.WebRootFileProvider.GetFileInfo(path);
            if (!original.Exists) return;
            using var reader = new StreamReader(original.CreateReadStream());
            var bytes = Encoding.UTF8.GetBytes(update(reader.ReadToEnd()));
            _files.Add(path, new ModuleFile(original, bytes));
            hash.AppendData(bytes);
        }
    }

    public IFileInfo GetFileInfo(string subpath) =>
        _files.TryGetValue("/" + subpath.TrimStart('/'), out var file) ? file : new NotFoundFileInfo(subpath);

    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;
    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    private sealed class ModuleFile(IFileInfo original, byte[] bytes) : IFileInfo
    {
        public string Name => original.Name;
        public long Length => bytes.LongLength;
        public DateTimeOffset LastModified => original.LastModified;
        public bool Exists => true;
        public bool IsDirectory => false;
        public string? PhysicalPath => null;
        public Stream CreateReadStream() => new MemoryStream(bytes, writable: false);
    }
}
