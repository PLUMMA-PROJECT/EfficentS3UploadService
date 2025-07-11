using System.Security.Cryptography;
using System.Text;

namespace EfficentS3UploadService
{
    internal class AwsSigV4Signer
    {
        public static string CreatePresignedUrl(string accessKey, string secretKey, string region, string iotEndpoint)
        {
            const string method = "GET";
            const string service = "iotdevicegateway";
            const string canonicalUri = "/mqtt";
            const string signedHeaders = "host";

            var t = DateTime.UtcNow;
            var amzDate = t.ToString("yyyyMMdd'T'HHmmss'Z'");
            var dateStamp = t.ToString("yyyyMMdd");

            var credentialScope = $"{dateStamp}/{region}/{service}/aws4_request";
            var algorithm = "AWS4-HMAC-SHA256";
            var credential = $"{accessKey}/{credentialScope}";

            var host = iotEndpoint;

            // 1. Canonical query string
            var queryParams = new SortedDictionary<string, string>
            {
                ["X-Amz-Algorithm"] = algorithm,
                ["X-Amz-Credential"] = Uri.EscapeDataString(credential),
                ["X-Amz-Date"] = amzDate,
                ["X-Amz-SignedHeaders"] = signedHeaders
            };

            var canonicalQueryString = string.Join("&", queryParams
                .Select(kvp => $"{kvp.Key}={kvp.Value}"));

            // 2. Canonical headers
            var canonicalHeaders = $"host:{host}\n";

            // 3. Create payload hash (empty string SHA256 hash)
            const string payloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

            // 4. Canonical request
            var canonicalRequest = $"{method}\n{canonicalUri}\n{canonicalQueryString}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";
            var hashedCanonicalRequest = ToHex(Hash(Encoding.UTF8.GetBytes(canonicalRequest)));

            // 5. String to sign
            var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{hashedCanonicalRequest}";

            // 6. Calculate the signature
            var signingKey = GetSignatureKey(secretKey, dateStamp, region, service);
            var signature = ToHex(HmacSHA256(Encoding.UTF8.GetBytes(stringToSign), signingKey));

            // 7. Append signature to query
            var presignedUrl = $"wss://{host}{canonicalUri}?{canonicalQueryString}&X-Amz-Signature={signature}";

            return presignedUrl;
        }

        private static byte[] HmacSHA256(byte[] data, byte[] key) =>
            new HMACSHA256(key).ComputeHash(data);

        private static byte[] Hash(byte[] data) =>
            SHA256.HashData(data);

        private static byte[] GetSignatureKey(string key, string dateStamp, string regionName, string serviceName)
        {
            var kDate = HmacSHA256(Encoding.UTF8.GetBytes(dateStamp), Encoding.UTF8.GetBytes("AWS4" + key));
            var kRegion = HmacSHA256(Encoding.UTF8.GetBytes(regionName), kDate);
            var kService = HmacSHA256(Encoding.UTF8.GetBytes(serviceName), kRegion);
            var kSigning = HmacSHA256(Encoding.UTF8.GetBytes("aws4_request"), kService);
            return kSigning;
        }

        private static string ToHex(byte[] bytes, bool lowercase = true)
        {
            var hex = BitConverter.ToString(bytes).Replace("-", "");
            return lowercase ? hex.ToLower() : hex.ToUpper();
        }
    }
}
