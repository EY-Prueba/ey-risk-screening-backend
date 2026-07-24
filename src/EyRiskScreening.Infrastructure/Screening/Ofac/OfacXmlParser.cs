using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using EyRiskScreening.Domain.Screening;
using EyRiskScreening.Domain.Screening.History;

namespace EyRiskScreening.Infrastructure.Screening.Ofac;

internal sealed class OfacXmlParser
{
    internal const string OfficialNamespace =
        "https://sanctionslistservice.ofac.treas.gov/api/PublicationPreview/exports/XML";

    private readonly XmlReaderSettings _readerSettings = new()
    {
        Async = true,
        CloseInput = false,
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        XmlResolver = null,
    };

    public async Task<IReadOnlyList<OfacRecord>> ParseAsync(
        Stream stream,
        string listName,
        OfacAdapterOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        await using var cancellationAwareStream =
            new CancellationAwareStream(stream, cancellationToken);
        var settings = _readerSettings.Clone();
        settings.MaxCharactersInDocument = options.MaxResponseBytes;
        using var reader = XmlReader.Create(cancellationAwareStream, settings);
        var recordsByUid = new Dictionary<string, List<OfacRecord>>(
            StringComparer.Ordinal);

        try
        {
            if (!await MoveToDocumentElementAsync(reader, cancellationToken)
                    .ConfigureAwait(false)
                || reader.Depth != 0
                || reader.LocalName != "sdnList"
                || reader.NamespaceURI != OfficialNamespace)
            {
                throw new OfacAdapterException(
                    "The OFAC dataset has an unexpected root element or namespace.");
            }

            if (reader.IsEmptyElement)
            {
                throw new OfacAdapterException(
                    "The OFAC dataset does not contain the expected records.");
            }

            if (!await reader.ReadAsync().ConfigureAwait(false))
            {
                throw new OfacAdapterException(
                    "The OFAC dataset does not contain the expected records.");
            }

            while (!reader.EOF)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.Element
                    && reader.Depth == 1
                    && reader.LocalName == "sdnEntry"
                    && reader.NamespaceURI == OfficialNamespace)
                {
                    var node = await XNode
                        .ReadFromAsync(reader, cancellationToken)
                        .ConfigureAwait(false);
                    AddRecord(
                        recordsByUid,
                        ParseEntry(
                            (XElement)node,
                            listName,
                            options,
                            cancellationToken),
                        options);
                    continue;
                }

                if (!await reader.ReadAsync().ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (XmlException exception)
        {
            throw new OfacAdapterException(
                "The OFAC dataset is not valid XML.",
                exception);
        }

        if (recordsByUid.Count == 0)
        {
            throw new OfacAdapterException(
                "The OFAC dataset does not contain the expected records.");
        }

        var records = new List<OfacRecord>();
        foreach (var pair in recordsByUid.OrderBy(
                     pair => pair.Key,
                     StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.AddRange(pair.Value);
        }

        return OfacRecord.AsReadOnly(records);
    }

    private static async Task<bool> MoveToDocumentElementAsync(
        XmlReader reader,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element)
            {
                return true;
            }
        }

        return false;
    }

    private static void AddRecord(
        Dictionary<string, List<OfacRecord>> recordsByUid,
        OfacRecord record,
        OfacAdapterOptions options)
    {
        if (!recordsByUid.TryGetValue(record.Uid, out var existingRecords))
        {
            existingRecords = [];
            recordsByUid.Add(record.Uid, existingRecords);
            if (recordsByUid.Count > options.MaxCandidates)
            {
                throw new OfacAdapterException(
                    "The OFAC dataset exceeds the configured candidate limit.");
            }
        }
        else if (existingRecords.Any(existing =>
                     !OfacRecord.HasCompatibleIdentity(existing, record)))
        {
            throw new OfacAdapterException(
                "The OFAC dataset contains conflicting records for one UID.");
        }

        if (!existingRecords.Any(existing =>
                OfacRecord.AreEquivalent(existing, record)))
        {
            existingRecords.Add(record);
        }
    }

    private static OfacRecord ParseEntry(
        XElement entry,
        string listName,
        OfacAdapterOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var xmlNamespace = entry.Name.Namespace;
        var uid = ChildValue(entry, xmlNamespace, "uid", cancellationToken);
        if (uid.Length > 20
            || !ulong.TryParse(
                uid,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedUid)
            || parsedUid == 0)
        {
            throw new OfacAdapterException(
                "An OFAC record has an invalid UID.");
        }

        var primaryName = JoinName(
            ChildValue(entry, xmlNamespace, "firstName", cancellationToken),
            ChildValue(entry, xmlNamespace, "lastName", cancellationToken),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(primaryName)
            || EntityNameNormalizer.Normalize(primaryName).Value.Length == 0)
        {
            throw new OfacAdapterException(
                "An OFAC record is missing its primary name.");
        }

        var aliases = ParseAliases(
            entry,
            xmlNamespace,
            primaryName,
            options,
            cancellationToken);
        var addresses = ParseAddresses(
            entry,
            xmlNamespace,
            cancellationToken);
        var programs = ParseDistinctValues(
            entry,
            xmlNamespace,
            "program",
            element => element.Value.Trim(),
            cancellationToken);
        var nationalities = ParseDistinctValues(
            entry,
            xmlNamespace,
            "nationality",
            element => ChildValue(
                element,
                xmlNamespace,
                "country",
                cancellationToken),
            cancellationToken);

        return new OfacRecord(
            uid,
            primaryName,
            ChildValue(entry, xmlNamespace, "sdnType", cancellationToken),
            listName,
            OfacRecord.AsReadOnly(programs),
            OfacRecord.AsReadOnly(aliases),
            OfacRecord.AsReadOnly(addresses),
            OfacRecord.AsReadOnly(nationalities));
    }

    private static List<OfacAlias> ParseAliases(
        XElement entry,
        XNamespace xmlNamespace,
        string primaryName,
        OfacAdapterOptions options,
        CancellationToken cancellationToken)
    {
        var aliases = new List<OfacAlias>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal)
        {
            EntityNameNormalizer.Normalize(primaryName).Value,
        };
        foreach (var aliasElement in Descendants(
                     entry,
                     xmlNamespace,
                     "aka",
                     cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var quality = ChildValue(
                aliasElement,
                xmlNamespace,
                "category",
                cancellationToken);
            if (!options.IncludeWeakAliases
                && quality.Contains("weak", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var aliasName = JoinName(
                ChildValue(
                    aliasElement,
                    xmlNamespace,
                    "firstName",
                    cancellationToken),
                ChildValue(
                    aliasElement,
                    xmlNamespace,
                    "lastName",
                    cancellationToken),
                cancellationToken);
            if (string.IsNullOrWhiteSpace(aliasName))
            {
                continue;
            }

            var normalizedName = EntityNameNormalizer.Normalize(aliasName).Value;
            if (normalizedName.Length == 0 || !seenNames.Add(normalizedName))
            {
                continue;
            }

            aliases.Add(new OfacAlias(
                aliasName,
                ChildValue(
                    aliasElement,
                    xmlNamespace,
                    "type",
                    cancellationToken),
                quality));
            if (aliases.Count + 1 > options.MaxNamesPerCandidate)
            {
                throw new OfacAdapterException(
                    "An OFAC record exceeds the configured name limit.");
            }
        }

        return aliases;
    }

    private static List<OfacAddress> ParseAddresses(
        XElement entry,
        XNamespace xmlNamespace,
        CancellationToken cancellationToken)
    {
        var addresses = new List<OfacAddress>();
        foreach (var addressElement in Descendants(
                     entry,
                     xmlNamespace,
                     "address",
                     cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var country = ChildValue(
                addressElement,
                xmlNamespace,
                "country",
                cancellationToken);
            var names = new[]
            {
                "address1",
                "address2",
                "address3",
                "city",
                "stateOrProvince",
                "postalCode",
            };
            var parts = new List<string>(names.Length + 1);
            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = ChildValue(
                    addressElement,
                    xmlNamespace,
                    name,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add(value);
                }
            }

            if (!string.IsNullOrWhiteSpace(country))
            {
                parts.Add(country);
            }

            var formatted = string.Join(", ", parts);
            if (formatted.Length > 0 || country.Length > 0)
            {
                addresses.Add(new OfacAddress(formatted, country));
            }
        }

        return addresses;
    }

    private static List<string> ParseDistinctValues(
        XElement entry,
        XNamespace xmlNamespace,
        string localName,
        Func<XElement, string> selector,
        CancellationToken cancellationToken)
    {
        var values = new List<string>();
        var seenValues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in Descendants(
                     entry,
                     xmlNamespace,
                     localName,
                     cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = selector(element);
            if (value.Length > 0 && seenValues.Add(value))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static IEnumerable<XElement> Descendants(
        XElement element,
        XNamespace xmlNamespace,
        string localName,
        CancellationToken cancellationToken)
    {
        foreach (var descendant in element.Descendants())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (descendant.Name == xmlNamespace + localName)
            {
                yield return descendant;
            }
        }
    }

    private static string ChildValue(
        XElement element,
        XNamespace xmlNamespace,
        string localName,
        CancellationToken cancellationToken)
    {
        foreach (var child in element.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.Name == xmlNamespace + localName)
            {
                return child.Value.Trim();
            }
        }

        return string.Empty;
    }

    private static string JoinName(
        string firstName,
        string lastName,
        CancellationToken cancellationToken)
    {
        return JoinLimited(
            new[] { firstName, lastName },
            " ",
            ScreeningHistoryLimits.MatchNameRunes,
            cancellationToken);
    }

    private static string JoinLimited(
        IEnumerable<string> values,
        string separator,
        int maximumRunes,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var runeCount = 0;
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var separatorRunes = builder.Length == 0
                ? 0
                : separator.EnumerateRunes().Count();
            if (separatorRunes > 0)
            {
                runeCount = checked(runeCount + separatorRunes);
                if (runeCount > maximumRunes)
                {
                    throw new OfacAdapterException(
                        "An OFAC value exceeds the persistence limit.");
                }

                _ = builder.Append(separator);
            }

            foreach (var rune in value.EnumerateRunes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                runeCount = checked(runeCount + 1);
                if (runeCount > maximumRunes)
                {
                    throw new OfacAdapterException(
                        "An OFAC value exceeds the persistence limit.");
                }

                _ = builder.Append(rune);
            }
        }

        return builder.ToString();
    }

    private sealed class CancellationAwareStream(
        Stream inner,
        CancellationToken operationCancellationToken) : Stream
    {
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
            operationCancellationToken.ThrowIfCancellationRequested();
            return inner.Read(buffer, offset, count);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.ReadAsync(
                buffer,
                offset,
                count,
                GetCancellationToken(cancellationToken));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, GetCancellationToken(cancellationToken));

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The response owns the underlying stream.
            base.Dispose(disposing);
        }

        private CancellationToken GetCancellationToken(
            CancellationToken cancellationToken)
        {
            operationCancellationToken.ThrowIfCancellationRequested();
            return cancellationToken.CanBeCanceled
                ? cancellationToken
                : operationCancellationToken;
        }
    }
}
