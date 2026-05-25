using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace DiskCompare.App;

public sealed class ReleaseUpdateService
{
    public const string RepositoryUrl = "https://github.com/yin2hao-windowsTools/diskCompare";
    public const string DeveloperHomeUrl = "https://github.com/yin2hao-windowsTools";

    private static readonly Uri LatestReleaseApiUrl = new("https://api.github.com/repos/yin2hao-windowsTools/diskCompare/releases/latest");
    private static readonly HttpClient HttpClient = new();

    public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
        request.Headers.UserAgent.ParseAdd($"DiskCompare/{GetCurrentDisplayVersion()}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var tagName = root.TryGetProperty("tag_name", out var tagProperty)
            ? tagProperty.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(tagName))
        {
            throw new InvalidDataException("GitHub Release 响应缺少 tag_name。");
        }

        var name = root.TryGetProperty("name", out var nameProperty)
            ? nameProperty.GetString()
            : null;
        var htmlUrl = root.TryGetProperty("html_url", out var urlProperty)
            ? urlProperty.GetString()
            : null;
        if (!Uri.TryCreate(htmlUrl, UriKind.Absolute, out var releaseUrl))
        {
            releaseUrl = new Uri($"{RepositoryUrl}/releases/latest");
        }

        return new ReleaseInfo(tagName, string.IsNullOrWhiteSpace(name) ? tagName : name, releaseUrl, TryParseVersion(tagName));
    }

    public static string GetCurrentDisplayVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "未知版本";
    }

    public static Version? GetCurrentVersion()
    {
        return TryParseVersion(GetCurrentDisplayVersion())
            ?? Assembly.GetExecutingAssembly().GetName().Version;
    }

    public static Version? TryParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            text = text[1..];
        }

        var suffixIndex = text.IndexOfAny(['-', '+', ' ']);
        if (suffixIndex >= 0)
        {
            text = text[..suffixIndex];
        }

        var rawSegments = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (rawSegments.Length == 0 || rawSegments.Length > 4)
        {
            return null;
        }

        var segments = new int[4];
        for (var index = 0; index < rawSegments.Length; index++)
        {
            if (!int.TryParse(rawSegments[index], out var segment) || segment < 0)
            {
                return null;
            }

            segments[index] = segment;
        }

        return new Version(segments[0], segments[1], segments[2], segments[3]);
    }
}

public sealed record ReleaseInfo(string TagName, string Name, Uri HtmlUrl, Version? Version);
