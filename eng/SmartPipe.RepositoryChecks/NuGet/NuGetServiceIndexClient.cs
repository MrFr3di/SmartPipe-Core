using System.Text.Json;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.NuGet;

internal interface INuGetServiceIndexClient
{
    Task<Uri> GetPackageBaseAddressAsync(CancellationToken cancellationToken);
}

internal sealed class NuGetServiceIndexClient : INuGetServiceIndexClient
{
    private static readonly Uri DefaultServiceIndexUri = new("https://api.nuget.org/v3/index.json");
    private const string PackageBaseAddressType = "PackageBaseAddress/3.0.0";

    private readonly HttpClient _httpClient;
    private readonly Uri _serviceIndexUri;

    public NuGetServiceIndexClient(HttpClient httpClient, Uri? serviceIndexUri = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _serviceIndexUri = serviceIndexUri ?? DefaultServiceIndexUri;
    }

    public async Task<Uri> GetPackageBaseAddressAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                _serviceIndexUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new RepositoryCheckException(
                    ExitCodes.ExternalSourceUnavailable,
                    $"NuGet service index returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("resources", out var resources)
                || resources.ValueKind != JsonValueKind.Array)
            {
                throw CreateMissingResourceException();
            }

            foreach (var resource in resources.EnumerateArray())
            {
                if (!resource.TryGetProperty("@type", out var type) || !ContainsPackageBaseAddressType(type))
                {
                    continue;
                }

                if (!resource.TryGetProperty("@id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || !Uri.TryCreate(id.GetString(), UriKind.Absolute, out var resourceUri))
                {
                    throw new RepositoryCheckException(
                        ExitCodes.UsageOrConfigurationError,
                        "NuGet PackageBaseAddress/3.0.0 resource has an invalid @id URI.");
                }

                if (!string.Equals(resourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    throw new RepositoryCheckException(
                        ExitCodes.UsageOrConfigurationError,
                        "NuGet PackageBaseAddress/3.0.0 resource must use HTTPS.");
                }

                return resourceUri;
            }

            throw CreateMissingResourceException();
        }
        catch (RepositoryCheckException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new RepositoryCheckException(
                ExitCodes.ExternalSourceUnavailable,
                "NuGet service index contained malformed JSON.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new RepositoryCheckException(
                ExitCodes.ExternalSourceUnavailable,
                "NuGet service index could not be retrieved.",
                exception);
        }
    }

    private static bool ContainsPackageBaseAddressType(JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.String)
        {
            return string.Equals(type.GetString(), PackageBaseAddressType, StringComparison.Ordinal);
        }

        if (type.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return type.EnumerateArray().Any(
            item => item.ValueKind == JsonValueKind.String
                && string.Equals(item.GetString(), PackageBaseAddressType, StringComparison.Ordinal));
    }

    private static RepositoryCheckException CreateMissingResourceException()
    {
        return new RepositoryCheckException(
            ExitCodes.UsageOrConfigurationError,
            "NuGet service index does not contain a PackageBaseAddress/3.0.0 resource.");
    }
}
