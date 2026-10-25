using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RobloxMultiManager.Core;

/// <summary>
/// Resolves Roblox headshot thumbnail URLs for a set of user ids.
/// </summary>
/// <remarks>
/// The thumbnail endpoint is best-effort by design: it answers <c>Pending</c>
/// while the render farm is still producing an image, so a missing id is a
/// normal outcome and never an error.  Every failure path therefore degrades to
/// "no URL for this id" instead of throwing, because the only caller is the UI
/// account list and an avatar is decoration, not data the app depends on.
/// </remarks>
public sealed class RobloxAvatarService
{
    /// <summary>
    /// Per-request id ceiling enforced by the thumbnails endpoint; a larger
    /// batch is rejected outright, which would lose every id in it.
    /// </summary>
    private const int MaxIdsPerRequest = 100;

    private static readonly Uri ThumbnailsBaseUri =
        new("https://thumbnails.roblox.com/");

    private readonly HttpClient _httpClient;

    /// <summary>
    /// Takes the application's shared <see cref="HttpClient"/> so connection
    /// pooling and DNS refresh stay owned by one instance for the process.
    /// </summary>
    public RobloxAvatarService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyDictionary<long, string>> FetchHeadshotsAsync(
        IEnumerable<long> userIds,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<long, string>();
        if (userIds is null)
        {
            return results;
        }

        // Deduplicate up front: profiles frequently share an id after a
        // re-import, and every duplicate would otherwise consume part of the
        // 100-id budget and come back as a redundant row.
        var seen = new HashSet<long>();
        var pending = new List<long>();
        foreach (var userId in userIds)
        {
            // Non-positive ids are profiles that were never resolved against
            // Roblox; sending them only makes the whole batch noisier.
            if (userId <= 0)
            {
                continue;
            }

            if (seen.Add(userId))
            {
                pending.Add(userId);
            }
        }

        if (pending.Count == 0)
        {
            // No request at all: an empty query string is answered with HTTP 400.
            return results;
        }

        for (var offset = 0; offset < pending.Count; offset += MaxIdsPerRequest)
        {
            // Honour a caller cancellation between batches so a closing window
            // does not pay for the remaining round trips.
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(MaxIdsPerRequest, pending.Count - offset);
            var batch = pending.GetRange(offset, count);

            // Batches are independent: one failing lot must not discard the
            // avatars the other lots already resolved.
            var batchResults = await FetchBatchAsync(batch, cancellationToken)
                .ConfigureAwait(false);
            foreach (var pair in batchResults)
            {
                results[pair.Key] = pair.Value;
            }
        }

        return results;
    }

    private async Task<IReadOnlyDictionary<long, string>> FetchBatchAsync(
        IReadOnlyList<long> batch,
        CancellationToken cancellationToken)
    {
        try
        {
            // Own timeout linked to the caller's token: the shared HttpClient
            // may carry a much longer global timeout, and a slow thumbnail
            // service must not hold the UI refresh for that long.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            var relative =
                "v1/users/avatar-headshot?userIds="
                + string.Join(",", batch.Select(
                    id => id.ToString(CultureInfo.InvariantCulture)))
                + "&size=150x150&format=Png&isCircular=true";
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(ThumbnailsBaseUri, relative));
            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("RobloxMultiManager/1.6.0");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(
                timeout.Token).ConfigureAwait(false);
            return ParseHeadshotPayload(json);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // A cancellation the caller asked for is a real control-flow signal
            // and must reach it; only the internal timeout is swallowed below.
            throw;
        }
        catch (HttpRequestException)
        {
            return EmptyResult();
        }
        catch (TaskCanceledException)
        {
            return EmptyResult();
        }
        catch (OperationCanceledException)
        {
            return EmptyResult();
        }
        catch (JsonException)
        {
            return EmptyResult();
        }
    }

    /// <summary>
    /// Extracts the completed headshot URLs from a thumbnails payload.
    /// </summary>
    /// <remarks>
    /// Separated from the transport so the tolerance rules can be exercised
    /// against literal payloads.  Every shape check is defensive on purpose:
    /// the endpoint occasionally answers an error envelope with no
    /// <c>data</c> array, and that must read as "nothing resolved".
    /// </remarks>
    internal static IReadOnlyDictionary<long, string> ParseHeadshotPayload(
        string json)
    {
        var results = new Dictionary<long, string>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return results;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!entry.TryGetProperty("targetId", out var targetIdElement)
                    || !TryReadUserId(targetIdElement, out var targetId))
                {
                    continue;
                }

                // Pending, Blocked and Error all mean "no image right now".
                // Skipping the id lets the next refresh try again, whereas
                // storing a placeholder would stick in the profile forever.
                if (!entry.TryGetProperty("state", out var stateElement)
                    || stateElement.ValueKind != JsonValueKind.String
                    || !string.Equals(
                        stateElement.GetString(),
                        "Completed",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!entry.TryGetProperty("imageUrl", out var imageUrlElement)
                    || imageUrlElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var imageUrl = imageUrlElement.GetString();
                if (string.IsNullOrWhiteSpace(imageUrl))
                {
                    continue;
                }

                // Last row wins: duplicates only happen when the service echoes
                // an id twice, and both rows carry the same rendered image.
                results[targetId] = imageUrl;
            }
        }
        catch (JsonException)
        {
            // Truncated or HTML bodies (captive portals, gateway pages) reach
            // here; the caller treats them the same as an empty result.
            return new Dictionary<long, string>();
        }

        return results;
    }

    /// <summary>
    /// Reads <c>targetId</c> from either the documented number form or the
    /// string form some proxies produce when re-serialising the payload.
    /// </summary>
    private static bool TryReadUserId(JsonElement element, out long userId)
    {
        userId = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (!element.TryGetInt64(out userId))
                {
                    return false;
                }

                break;
            case JsonValueKind.String:
                if (!long.TryParse(
                        element.GetString(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out userId))
                {
                    return false;
                }

                break;
            default:
                return false;
        }

        return userId > 0;
    }

    private static IReadOnlyDictionary<long, string> EmptyResult()
    {
        return new Dictionary<long, string>();
    }
}
