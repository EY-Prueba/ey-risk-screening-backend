using System.Buffers;
using System.Net;
using System.Text.Json;
using EyRiskScreening.Application.Screening;
using Microsoft.AspNetCore.Http;

namespace EyRiskScreening.Infrastructure.Screening.OffshoreLeaks;

internal static class OffshoreLeaksHttpResponseReader
{
    public static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        IReadOnlySet<HttpStatusCode> acceptedStatuses,
        int maximumBytes,
        int maximumDepth,
        CancellationToken cancellationToken)
    {
        if (!acceptedStatuses.Contains(response.StatusCode))
        {
            ThrowForStatus(response);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(
                mediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase)
            && !(mediaType?.StartsWith(
                    "application/",
                    StringComparison.OrdinalIgnoreCase) == true
                && mediaType.EndsWith(
                    "+json",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new OffshoreLeaksAdapterException(
                "The ICIJ service returned an incompatible content type.");
        }

        byte[] bytes;
        try
        {
            bytes = await ReadBoundedAsync(
                    response.Content,
                    maximumBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw new OffshoreLeaksAdapterException(
                "The ICIJ service returned invalid compressed content.",
                exception);
        }
        catch (IOException exception)
        {
            throw new ScreeningSourceUnavailableException(
                "The ICIJ response stream became unavailable.",
                exception);
        }

        try
        {
            return JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = maximumDepth,
                });
        }
        catch (JsonException exception)
        {
            throw new OffshoreLeaksAdapterException(
                "The ICIJ service returned invalid JSON.",
                exception);
        }
    }

    public static TimeSpan? ParseRetryAfter(
        HttpResponseMessage response,
        DateTimeOffset utcNow)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var remaining = date - utcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return null;
    }

    private static void ThrowForStatus(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.RequestTimeout)
        {
            throw new ScreeningSourceTimedOutException(
                "The ICIJ service reported a timeout.");
        }

        if ((int)response.StatusCode == StatusCodes.Status429TooManyRequests
            || (int)response.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            throw new ScreeningSourceUnavailableException(
                "The ICIJ service is temporarily unavailable.");
        }

        throw new OffshoreLeaksAdapterException(
            "The ICIJ service rejected the request.");
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = new MemoryStream(
            Math.Min(maximumBytes, 64 * 1024));
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (output.Length + read > maximumBytes)
                {
                    throw new OffshoreLeaksAdapterException(
                        "The ICIJ response exceeded its configured byte limit.");
                }

                await output
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
