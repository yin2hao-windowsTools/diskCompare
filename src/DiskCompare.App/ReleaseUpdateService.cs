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
    private static readonly Uri ReleasesApiUrl = new("https://api.github.com/repos/yin2hao-windowsTools/diskCompare/releases?per_page=1");
    private static readonly Uri LatestReleasePageUrl = new($"{RepositoryUrl}/releases/latest");
    private static readonly HttpClient HttpClient = new();

    public async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        Exception? lastRecoverableException = null;

        var latestRelease = await TryGetReleaseAsync(GetLatestReleaseFromApiAsync, cancellationToken, ex => lastRecoverableException = ex);
        if (latestRelease is not null)
        {
            return latestRelease;
        }

        latestRelease = await TryGetReleaseAsync(GetLatestReleaseFromReleasesApiAsync, cancellationToken, ex => lastRecoverableException = ex);
        if (latestRelease is not null)
        {
            return latestRelease;
        }

        latestRelease = await TryGetReleaseAsync(GetLatestReleaseFromRedirectAsync, cancellationToken, ex => lastRecoverableException = ex);
        if (latestRelease is not null)
        {
            return latestRelease;
        }

        if (lastRecoverableException is not null)
        {
            throw new UpdateCheckException("GitHub 暂时无法响应更新检查，请稍后再试，或手动打开 Release 页面查看。", lastRecoverableException);
        }

        return null;
    }

    private static async Task<ReleaseInfo?> TryGetReleaseAsync(
        Func<CancellationToken, Task<ReleaseInfo?>> getRelease,
        CancellationToken cancellationToken,
        Action<Exception> onRecoverableError)
    {
        try
        {
            return await getRelease(cancellationToken);
        }
        catch (Exception ex) when (IsRecoverableUpdateCheckException(ex))
        {
            onRecoverableError(ex);
            return null;
        }
    }

    private static async Task<ReleaseInfo?> GetLatestReleaseFromApiAsync(CancellationToken cancellationToken)
    {
        var root = await ReadJsonObjectWithRetryAsync(LatestReleaseApiUrl, allowNotFound: true, cancellationToken);
        return root is null ? null : ReadReleaseInfo(root.Value);
    }

    private static async Task<ReleaseInfo?> GetLatestReleaseFromReleasesApiAsync(CancellationToken cancellationToken)
    {
        var document = await ReadJsonDocumentWithRetryAsync(ReleasesApiUrl, allowNotFound: false, cancellationToken)
            ?? throw new UpdateCheckException("GitHub Release 列表响应为空。");
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            {
                return null;
            }

            return ReadReleaseInfo(root[0]);
        }
    }

    private static async Task<ReleaseInfo?> GetLatestReleaseFromRedirectAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, LatestReleasePageUrl, acceptGitHubJson: false);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateCheckException($"GitHub Release 页面暂时不可用: {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var finalUri = response.RequestMessage?.RequestUri;
        var tagName = TryReadTagNameFromReleaseUri(finalUri);
        if (string.IsNullOrWhiteSpace(tagName))
        {
            throw new UpdateCheckException("无法从 GitHub Release 页面识别最新版本号。");
        }

        return CreateReleaseInfoFromTag(tagName);
    }

    private static ReleaseInfo ReadReleaseInfo(JsonElement root)
    {
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

        return new ReleaseInfo(
            tagName,
            string.IsNullOrWhiteSpace(name) ? tagName : name,
            releaseUrl,
            TryParseVersion(tagName),
            ReadAssets(root));
    }

    private static async Task<JsonElement?> ReadJsonObjectWithRetryAsync(Uri url, bool allowNotFound, CancellationToken cancellationToken)
    {
        var document = await ReadJsonDocumentWithRetryAsync(url, allowNotFound, cancellationToken);
        if (document is null)
        {
            return null;
        }

        using (document)
        {
            return document.RootElement.Clone();
        }
    }

    private static async Task<JsonDocument?> ReadJsonDocumentWithRetryAsync(Uri url, bool allowNotFound, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = CreateRequest(HttpMethod.Get, url, acceptGitHubJson: true);
                using var response = await HttpClient.SendAsync(request, cancellationToken);
                if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (IsTransientStatusCode(response.StatusCode))
                {
                    throw new HttpRequestException(
                        $"GitHub 暂时无法响应: {(int)response.StatusCode} {response.ReasonPhrase}",
                        null,
                        response.StatusCode);
                }

                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (IsTransientHttpException(ex) && attempt < maxAttempts)
            {
                lastException = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);
            }
            catch (Exception ex) when (IsTransientHttpException(ex))
            {
                lastException = ex;
                break;
            }
        }

        throw new UpdateCheckException("GitHub API 暂时无法响应更新检查。", lastException);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri url, bool acceptGitHubJson)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd($"DiskCompare/{GetCurrentDisplayVersion()}");
        if (acceptGitHubJson)
        {
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
        }

        return request;
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

    private static IReadOnlyList<ReleaseAsset> ReadAssets(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var assets = new List<ReleaseAsset>();
        foreach (var assetElement in assetsElement.EnumerateArray())
        {
            var name = assetElement.TryGetProperty("name", out var nameProperty)
                ? nameProperty.GetString()
                : null;
            var downloadUrl = assetElement.TryGetProperty("browser_download_url", out var urlProperty)
                ? urlProperty.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(name) || !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var assetUrl))
            {
                continue;
            }

            var size = assetElement.TryGetProperty("size", out var sizeProperty) && sizeProperty.TryGetInt64(out var assetSize)
                ? assetSize
                : 0;
            assets.Add(new ReleaseAsset(name, assetUrl, size));
        }

        return assets;
    }

    private static ReleaseInfo CreateReleaseInfoFromTag(string tagName)
    {
        var displayVersion = tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tagName : $"v{tagName}";
        var releaseUrl = new Uri($"{RepositoryUrl}/releases/tag/{Uri.EscapeDataString(tagName)}");
        var assets = new[]
        {
            CreateReleaseAsset(tagName, $"DiskCompare-{displayVersion}-win-x64.exe"),
            CreateReleaseAsset(tagName, $"DiskCompare-{displayVersion}-win-x64.msi"),
            CreateReleaseAsset(tagName, $"DiskCompare-{displayVersion}-win-x64-portable.zip")
        };

        return new ReleaseInfo(tagName, $"DiskCompare {displayVersion}", releaseUrl, TryParseVersion(tagName), assets);
    }

    private static ReleaseAsset CreateReleaseAsset(string tagName, string fileName)
    {
        var downloadUrl = new Uri($"{RepositoryUrl}/releases/download/{Uri.EscapeDataString(tagName)}/{Uri.EscapeDataString(fileName)}");
        return new ReleaseAsset(fileName, downloadUrl, 0);
    }

    private static string? TryReadTagNameFromReleaseUri(Uri? uri)
    {
        if (uri is null)
        {
            return null;
        }

        var segments = uri.Segments.Select(static segment => segment.Trim('/')).ToArray();
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (segments[index].Equals("tag", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(segments[index + 1]);
            }
        }

        return null;
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }

    private static bool IsTransientHttpException(Exception ex)
    {
        return (ex is HttpRequestException requestException
                && (requestException.StatusCode is null || IsTransientStatusCode(requestException.StatusCode.Value)))
            || ex is TaskCanceledException;
    }

    private static bool IsRecoverableUpdateCheckException(Exception ex)
    {
        return ex is UpdateCheckException || IsTransientHttpException(ex);
    }
}

public sealed record ReleaseInfo(string TagName, string Name, Uri HtmlUrl, Version? Version, IReadOnlyList<ReleaseAsset> Assets);

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

public sealed class UpdateCheckException : Exception
{
    public UpdateCheckException(string message)
        : base(message)
    {
    }

    public UpdateCheckException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
