using System.Text.Json;

namespace EfficentS3UploadService.Helpers
{
    internal class MessageHelper
    {
        private JsonDocument _jsonDoc;

        public MessageHelper(string json)
        {
            try
            {
                _jsonDoc = JsonDocument.Parse(json);
            }
            catch (JsonException ex)
            {
                throw new ArgumentException("JSON non valido", ex);
            }
        }

        public string GetValue(string propertyName)
        {
            if (_jsonDoc.RootElement.TryGetProperty(propertyName, out JsonElement element))
            {
                return element.ToString();
            }

            return null;
        }

        public Dictionary<string, string> GetAllProperties()
        {
            var result = new Dictionary<string, string>();

            foreach (var property in _jsonDoc.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.ToString();
            }

            return result;
        }
    }
}
