using System.Net;
using System.Net.Http.Headers;
using EyRiskScreening.Application.Screening;
using Microsoft.Extensions.Options;

namespace EyRiskScreening.Infrastructure.Screening.Ofac;

internal sealed class OfacClient(
    IHttpClientFactory httpClientFactory,
    OfacXmlParser parser,
    OfacRedirectPolicy redirectPolicy,
    IOptions<OfacAdapterOptions> optionsAccessor) : IOfacClient
{
    public const string ClientName = "OfacSanctionsListService";
    private const string SdnPath = "/api/download/SDN.XML";
    private const string ConsolidatedPath = "/api/download/CONSOLIDATED.XML";

    private readonly OfacAdapterOptions _options = optionsAccessor.Value;

    public async Task<IReadOnlyList<OfacRecord>> DownloadAsync(
        OfacDatasetKind dataset,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = dataset == OfacDatasetKind.Sdn ? SdnPath : ConsolidatedPath;
        var fileName = dataset == OfacDatasetKind.Sdn ? "SDN.XML" : "CONSOLIDATED.XML";
        var listName = dataset == OfacDatasetKind.Sdn ? "SDN" : "Consolidated";

        try
        {
            var client = httpClientFactory.CreateClient(ClientName);
            client.DefaultRequestHeaders.Clear();
            using var initialRequest = CreateRequest(path);
            using var initialResponse = await client
                .SendAsync(
                    initialRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            if (initialResponse.StatusCode != HttpStatusCode.Found)
            {
                return await ParseFinalResponseAsync(
                        initialResponse,
                        listName,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var redirectUri = redirectPolicy.Validate(
                initialResponse.Headers.Location,
                fileName);
            using var redirectRequest = CreateRequest(redirectUri);
            using var redirectResponse = await client
                .SendAsync(
                    redirectRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            return await ParseFinalResponseAsync(
                    redirectResponse,
                    listName,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ScreeningSourceTimedOutException)
        {
            throw;
        }
        catch (ScreeningSourceUnavailableException)
        {
            throw;
        }
        catch (OfacAdapterException)
        {
            throw;
        }
        catch (HttpRequestException exception)
            when (ContainsInvalidDataException(exception))
        {
            throw new OfacAdapterException(
                "The OFAC response contains invalid compressed data.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ScreeningSourceUnavailableException(
                "The OFAC service could not be reached.",
                exception);
        }
        catch (InvalidDataException exception)
        {
            throw new OfacAdapterException(
                "The OFAC response contains invalid compressed data.",
                exception);
        }
        catch (IOException exception)
        {
            throw new ScreeningSourceUnavailableException(
                "The OFAC response could not be read.",
                exception);
        }
    }

    private HttpRequestMessage CreateRequest(string requestUri) =>
        CreateRequest(new Uri(requestUri, UriKind.Relative));

    private HttpRequestMessage CreateRequest(Uri requestUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.UserAgent.ParseAdd(_options.UserAgent);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/xml"));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("text/xml"));
        return request;
    }

    private async Task<IReadOnlyList<OfacRecord>> ParseFinalResponseAsync(
        HttpResponseMessage response,
        string listName,
        CancellationToken cancellationToken)
    {
        ThrowForStatus(response.StatusCode);
        ValidateContent(response);

        await using var responseStream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
        await using var limitedStream = new ResponseSizeLimitedStream(
            responseStream,
            _options.MaxResponseBytes);
        return await parser
            .ParseAsync(limitedStream, listName, _options, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool ContainsInvalidDataException(Exception exception)
    {
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (current is InvalidDataException)
            {
                return true;
            }
        }

        return false;
    }

    private static void ThrowForStatus(HttpStatusCode statusCode)
    {
        if (statusCode == HttpStatusCode.OK)
        {
            return;
        }

        if (statusCode == HttpStatusCode.RequestTimeout)
        {
            throw new ScreeningSourceTimedOutException(
                "The OFAC service reported a request timeout.");
        }

        if (statusCode == HttpStatusCode.TooManyRequests
            || (int)statusCode >= 500)
        {
            throw new ScreeningSourceUnavailableException(
                "The OFAC service is temporarily unavailable.");
        }

        throw new OfacAdapterException(
            $"The OFAC service returned unexpected HTTP status {(int)statusCode}.");
    }

    private void ValidateContent(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength > _options.MaxResponseBytes)
        {
            throw new OfacAdapterException(
                "The OFAC response exceeds the configured size limit.");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not ("application/xml" or "text/xml" or "application/octet-stream"))
        {
            throw new OfacAdapterException(
                "The OFAC response has an unexpected content type.");
        }
    }

    private sealed class ResponseSizeLimitedStream(Stream inner, long maximumBytes)
        : Stream
    {
        private long _bytesRead;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = inner.Read(buffer, offset, count);
            AddBytes(bytesRead);
            return bytesRead;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var bytesRead = await inner
                .ReadAsync(
                    buffer.AsMemory(offset, count),
                    cancellationToken)
                .ConfigureAwait(false);
            AddBytes(bytesRead);
            return bytesRead;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var bytesRead = await inner
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            AddBytes(bytesRead);
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The HttpResponseMessage owns the underlying stream.
            base.Dispose(disposing);
        }

        private void AddBytes(int bytesRead)
        {
            _bytesRead = checked(_bytesRead + bytesRead);
            if (_bytesRead > maximumBytes)
            {
                throw new OfacAdapterException(
                    "The OFAC response exceeds the configured size limit.");
            }
        }
    }
}
