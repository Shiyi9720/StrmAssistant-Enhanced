using System.IO;
using System.Text;
using System.Text.Json;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Adds the capture context to a TMDB response before it is written to the passive cache.
    ///
    /// Emby's untyped IJsonSerializer round-trip (Dictionary&lt;string, object&gt;) used to be
    /// used here, but it writes nested objects and arrays in a non-JSON dialect
    /// ({id: 18, name: Drama}) — the cache then served that text back to Emby verbatim, whose
    /// parser produced items with null genre names and dropped the rest of the payload. The
    /// annotation must therefore be a byte-level rewrite that preserves the original JSON.
    /// </summary>
    internal static class LocalMetadataCapture
    {
        internal static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static readonly JsonSerializerOptions MetaOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Copies the original document and appends "_meta". Returns the input unchanged when it
        /// is not a JSON object or cannot be parsed, so a capture never corrupts a response.
        /// </summary>
        public static string Annotate(string body, object meta)
        {
            if (string.IsNullOrWhiteSpace(body)) return body;

            try
            {
                using (var document = JsonDocument.Parse(body))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object) return body;

                    using (var stream = new MemoryStream())
                    {
                        using (var writer = new Utf8JsonWriter(stream))
                        {
                            writer.WriteStartObject();

                            foreach (var property in document.RootElement.EnumerateObject())
                            {
                                property.WriteTo(writer);
                            }

                            writer.WritePropertyName("_meta");
                            JsonSerializer.Serialize(writer, meta, MetaOptions);
                            writer.WriteEndObject();
                        }

                        return Utf8NoBom.GetString(stream.ToArray());
                    }
                }
            }
            catch (JsonException)
            {
                return body;
            }
        }
    }
}
