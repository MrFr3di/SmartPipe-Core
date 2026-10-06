using System.Net;

namespace SmartPipe.Consumers.Http;

/// <summary>Assertions for the direct HTTP consumer's requests and client ownership.</summary>
static class HttpConsumerChecks
{
    public static void VerifyRequests(IReadOnlyList<LoopbackRequest> requests, string sourcePath, string sinkPath)
    {
        ConsumerCheck.Require(
            requests.Count == 2 && requests[0].Path == sourcePath && requests[1].Path == sinkPath && requests[1].Body == "37",
            "The loopback server did not observe the expected requests.");
    }

    public static void VerifyAdapterOwnership(
        CountingHandler sourceHandler,
        CountingHandler sinkHandler,
        NamedClientFactory clientFactory)
    {
        ConsumerCheck.Require(
            sourceHandler.SendCount == 1 && sourceHandler.DisposeCount == 0,
            "The source must send once and leave its application-owned handler alive.");
        ConsumerCheck.Require(
            clientFactory.CreateCount == 1 && sinkHandler.SendCount == 1 && sinkHandler.DisposeCount == 1,
            "The factory sink must create and dispose one client after one send.");
    }

    /// <summary>A direct client remains usable after the adapter finishes.</summary>
    public static async Task VerifyBorrowedClientAliveAsync(
        HttpClient client,
        CountingHandler handler,
        string sourcePath)
    {
        using var response = await client.GetAsync(sourcePath).ConfigureAwait(false);
        ConsumerCheck.Require(
            response.StatusCode == HttpStatusCode.OK && handler.SendCount == 2,
            "The source adapter disposed the borrowed HttpClient.");
    }

    /// <summary>The adapter owns and disposes clients it obtains from a factory.</summary>
    public static async Task VerifyFactoryClientDisposedAsync(NamedClientFactory clientFactory)
    {
        var client = clientFactory.CreatedClient
            ?? throw new InvalidOperationException("The sink adapter did not obtain a factory client.");
        try
        {
            using var response = await client.GetAsync("/after-dispose").ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        throw new InvalidOperationException("The sink adapter did not dispose its factory-created HttpClient.");
    }
}
