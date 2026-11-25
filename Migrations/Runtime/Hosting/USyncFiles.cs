using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.FileProviders;
using UmbracoAppHost.Themes;

namespace UmbracoAppHost.Hosting;

/// <summary>The packaged source baseline; working exports always go to the instance.</summary>
internal static class USyncFiles
{
    /// <summary>The host's own saved export, relative to its <see cref="AppHostProject"/> root; empty disables it.</summary>
    public const string HostExportSetting = "UmbracoAppHost:HostExport";

    private static readonly IFileProvider Files = new ManifestEmbeddedFileProvider(typeof(USyncFiles).Assembly, "uSync");

    /// <summary>
    /// The folder <see cref="HostExportSetting"/> names (default <c>uSync</c>) as an absolute path, or null when the
    /// setting is empty, the folder does not exist, or it is the working folder itself.
    /// </summary>
    public static string? HostExport(IConfiguration configuration, string contentRoot, string workingFolder)
    {
        if ((configuration[HostExportSetting] ?? "uSync") is not { Length: > 0 } setting) return null;
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(setting, AppHostProject.Root(configuration, contentRoot)));
        return Directory.Exists(folder) && !folder.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingFolder)), StringComparison.OrdinalIgnoreCase)
            ? folder : null;
    }

    /// <summary>
    /// Supplies a working folder that has no <c>usync.config</c>: the library's export, without the sample content
    /// unless <paramref name="includeSamples"/>, then every file of <paramref name="hostExport"/> at a path the library
    /// does not ship, then the same of each theme library's export. Existing files are never overwritten.
    /// </summary>
    public static void EnsureWorkingCopy(string destination, bool includeSamples, string? hostExport, ILogger logger, IEnumerable<ThemeLibrary>? libraries = null)
    {
        if (File.Exists(Path.Combine(destination, "usync.config"))) return;
        var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CopyDirectory("/", destination);
        if (hostExport is not null)
        {
            var working = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
            var copied = 0;
            foreach (var file in Directory.EnumerateFiles(hostExport, "*.config", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(hostExport, file);
                var target = Path.Combine(destination, relative);
                // uSync's default working folder, uSync/v18, sits inside a host's uSync folder.
                if (shipped.Contains(relative) || file.StartsWith(working, StringComparison.OrdinalIgnoreCase) || File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                copied++;
            }
            logger.LogInformation("Copied {Count} files of the host's export {HostExport} to the working uSync folder {Destination}.", copied, hostExport, destination);
        }
        foreach (var library in libraries ?? []) CopyLibraryExport(library.Files, "uSync", destination);

        void CopyLibraryExport(IFileProvider files, string sourceDirectory, string targetDirectory)
        {
            foreach (var file in files.GetDirectoryContents(sourceDirectory))
            {
                var target = Path.Combine(targetDirectory, file.Name);
                if (file.IsDirectory) { CopyLibraryExport(files, sourceDirectory + "/" + file.Name, target); continue; }
                if (!file.Name.EndsWith(".config", StringComparison.OrdinalIgnoreCase) || shipped.Contains(Path.GetRelativePath(destination, target)) || File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = file.CreateReadStream();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                source.CopyTo(output);
            }
        }

        void CopyDirectory(string sourceDirectory, string targetDirectory)
        {
            foreach (var file in Files.GetDirectoryContents(sourceDirectory))
            {
                var target = Path.Combine(targetDirectory, file.Name);
                if (file.IsDirectory) { CopyDirectory(sourceDirectory.TrimEnd('/') + "/" + file.Name, target); continue; }
                if (!file.Name.EndsWith(".config", StringComparison.OrdinalIgnoreCase)) continue;
                shipped.Add(Path.GetRelativePath(destination, target));
                if (File.Exists(target) || (!includeSamples && sourceDirectory == "/Content" && IsSample(file))) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = file.CreateReadStream();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                source.CopyTo(output);
            }
        }
    }

    private static bool IsSample(IFileInfo file)
    {
        using var stream = file.CreateReadStream();
        return SampleContent.IsSample(XElement.Load(stream));
    }

    /// <summary>
    /// The uSync folders whose definitions every site takes from its build, as <c>deployment/SiteTools.ps1</c> lists them.
    /// Content, domains and media belong to a site once it is created.
    /// </summary>
    public static readonly string[] SharedFolders = ["ContentTypes", "DataTypes", "Dictionary", "Languages", "MediaTypes", "MemberTypes", "RelationTypes", "Templates"];

    /// <summary>
    /// The shared definitions this build ships, by path under the uSync folder with forward slashes. They come in the
    /// order <see cref="EnsureWorkingCopy"/> fills a new working folder in: the library's export, then
    /// <paramref name="hostExport"/> at the paths the library does not ship, then each theme library's export at the
    /// paths none before it ships. A new site and an updated one therefore take the same files.
    /// </summary>
    public static SortedDictionary<string, byte[]> SharedDefinitions(string? hostExport, string workingFolder, IEnumerable<ThemeLibrary>? libraries = null)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        void Add(string relative, Func<Stream> open)
        {
            relative = relative.Replace('\\', '/').TrimStart('/');
            if (!relative.EndsWith(".config", StringComparison.OrdinalIgnoreCase) || files.ContainsKey(relative)
                || !SharedFolders.Contains(relative.Split('/')[0], StringComparer.OrdinalIgnoreCase)) return;
            using var stream = open();
            using var content = new MemoryStream();
            stream.CopyTo(content);
            files[relative] = content.ToArray();
        }
        void AddAll(IFileProvider provider, string directory, string prefix)
        {
            foreach (var file in provider.GetDirectoryContents(directory))
            {
                if (file.IsDirectory) AddAll(provider, directory.TrimEnd('/') + "/" + file.Name, prefix + file.Name + "/");
                else Add(prefix + file.Name, file.CreateReadStream);
            }
        }
        AddAll(Files, "/", "");
        if (hostExport is not null)
        {
            var working = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingFolder)) + Path.DirectorySeparatorChar;
            foreach (var file in Directory.EnumerateFiles(hostExport, "*.config", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                if (!file.StartsWith(working, StringComparison.OrdinalIgnoreCase)) Add(Path.GetRelativePath(hostExport, file), () => File.OpenRead(file));
        }
        foreach (var library in libraries ?? []) AddAll(library.Files, "uSync", "");
        return files;
    }

    /// <summary>
    /// The SHA-256 of a set of definition files, over each path and text. A file's byte-order mark, line endings and
    /// final line break are left out, so the checkout a build was made from does not change it.
    /// </summary>
    public static string Fingerprint(SortedDictionary<string, byte[]> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, content) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(path.ToLowerInvariant() + "\n"));
            hash.AppendData(SHA256.HashData(Text(content)));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// Makes the working folder hold each of <paramref name="files"/> as given, and no other file for the same item. A
    /// file that is missing or whose text differs is written. Another file in the shared folders that carries the key
    /// of one of them is deleted: uSync's export on save leaves one behind when an item is renamed while exports are
    /// paused, and uSync refuses to import a folder that holds two files for one key. Returns the paths it changed,
    /// each with what the file held before, which is null for a file that was not there.
    /// </summary>
    public static IReadOnlyList<(string Path, byte[]? Before)> Deliver(SortedDictionary<string, byte[]> files, string workingFolder)
    {
        var changed = new List<(string, byte[]?)>();
        var shipped = new Dictionary<Guid, string>();
        foreach (var (relative, content) in files)
        {
            if (Key(content) is { } key) shipped.TryAdd(key, relative);
            var target = Path.Combine(workingFolder, relative.Replace('/', Path.DirectorySeparatorChar));
            var before = File.Exists(target) ? File.ReadAllBytes(target) : null;
            if (before is not null && Text(before).AsSpan().SequenceEqual(Text(content))) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
            changed.Add((relative, before));
        }
        foreach (var folder in SharedFolders)
        {
            var directory = Path.Combine(workingFolder, folder);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.config", SearchOption.AllDirectories).ToArray())
            {
                var relative = Path.GetRelativePath(workingFolder, file).Replace('\\', '/');
                var content = File.ReadAllBytes(file);
                if (Key(content) is not { } key || !shipped.TryGetValue(key, out var path) || path.Equals(relative, StringComparison.OrdinalIgnoreCase)) continue;
                File.Delete(file);
                changed.Add((relative, content));
            }
        }
        return changed;
    }

    /// <summary>Puts the working folder back as it was before <see cref="Deliver"/> made <paramref name="changed"/>.</summary>
    public static void Restore(string workingFolder, IEnumerable<(string Path, byte[]? Before)> changed)
    {
        foreach (var (relative, before) in changed)
        {
            var target = Path.Combine(workingFolder, relative.Replace('/', Path.DirectorySeparatorChar));
            if (before is null) File.Delete(target);
            else File.WriteAllBytes(target, before);
        }
    }

    /// <summary>The key of the item a uSync file holds, or null when the file is no such item.</summary>
    private static Guid? Key(byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content);
            return Guid.TryParse(XElement.Load(stream).Attribute("Key")?.Value, out var key) ? key : null;
        }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>
    /// Deletes the working folder's copies of the definitions <paramref name="owns"/> names by key, which uSync's export on save
    /// writes and an import refuses, and returns their paths.
    /// </summary>
    public static IReadOnlyList<string> RemoveCopies(string workingFolder, Func<Guid, bool> owns)
    {
        var removed = new List<string>();
        foreach (var folder in new[] { "ContentTypes", "DataTypes" })
        {
            var directory = Path.Combine(workingFolder, folder);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*.config", SearchOption.AllDirectories).ToArray())
            {
                if (Key(File.ReadAllBytes(file)) is not { } key || !owns(key)) continue;
                File.Delete(file);
                removed.Add(Path.GetRelativePath(workingFolder, file).Replace('\\', '/'));
            }
        }
        return removed;
    }

    private static byte[] Text(byte[] content)
        => Encoding.UTF8.GetBytes(new UTF8Encoding(false).GetString(content).TrimStart((char)0xFEFF).Replace("\r\n", "\n").TrimEnd('\n'));
}
