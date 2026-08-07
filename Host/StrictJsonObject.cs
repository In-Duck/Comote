using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Host
{
    internal static class StrictJsonObject
    {
        private const int MaximumDepth = 32;

        public static JObject Parse(ReadOnlySpan<byte> utf8)
        {
            var json = new UTF8Encoding(false, true).GetString(utf8);
            using var textReader = new StringReader(json);
            using var reader = new JsonTextReader(textReader)
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Decimal,
                MaxDepth = MaximumDepth,
                SupportMultipleContent = true,
            };
            var value = JToken.Load(
                reader,
                new JsonLoadSettings
                {
                    CommentHandling = CommentHandling.Load,
                    DuplicatePropertyNameHandling =
                        DuplicatePropertyNameHandling.Error,
                    LineInfoHandling = LineInfoHandling.Ignore,
                });
            if (value is not JObject result)
            {
                throw new JsonReaderException(
                    "The protocol message must be a JSON object.");
            }
            while (reader.Read())
            {
                if (reader.TokenType != JsonToken.Comment)
                {
                    throw new JsonReaderException(
                        "The protocol message contains trailing content.");
                }
            }
            return result;
        }
    }
}
