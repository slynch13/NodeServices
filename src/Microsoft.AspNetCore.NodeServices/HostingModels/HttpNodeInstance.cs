using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Microsoft.AspNetCore.NodeServices
{
    internal class HttpNodeInstance : OutOfProcessNodeInstance
    {
        private static readonly Regex PortMessageRegex =
            new Regex(@"^\[Microsoft.AspNetCore.NodeServices.HttpNodeHost:Listening on port (\d+)\]$");

        private static readonly JsonSerializerSettings JsonSerializerSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

	    private HttpClient _client;
        private bool _disposed;
        private int _portNumber;

        public HttpNodeInstance(string projectPath, int port = 0, string[] watchFileExtensions = null)
            : base(
                EmbeddedResourceReader.Read(
                    typeof(HttpNodeInstance),
                    "/Content/Node/entrypoint-http.js"),
                projectPath,
                MakeCommandLineOptions(port, watchFileExtensions))
        {
            _client = new HttpClient();
		}

        private static string MakeCommandLineOptions(int port, string[] watchFileExtensions)
        {
            var result = "--port " + port;
            if (watchFileExtensions != null && watchFileExtensions.Length > 0)
            {
                result += " --watch " + string.Join(",", watchFileExtensions);
            }

            return result;
        }

        public override async Task<T> Invoke<T>(NodeInvocationInfo invocationInfo)
        {
            await EnsureReady();

            // TODO: Use System.Net.Http.Formatting (PostAsJsonAsync etc.)
            var payloadJson = JsonConvert.SerializeObject(invocationInfo, JsonSerializerSettings);
            var payload = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            var response = await _client.PostAsync("http://localhost:" + _portNumber, payload);

            if (!response.IsSuccessStatusCode)
            {
                var responseErrorString = await response.Content.ReadAsStringAsync();
                throw new Exception("Call to Node module failed with error: " + responseErrorString);
            }

            var responseContentType = response.Content.Headers.ContentType;
            switch (responseContentType.MediaType)
            {
                case "text/plain":
                    // String responses can skip JSON encoding/decoding
                    if (typeof(T) != typeof(string))
                    {
                        throw new ArgumentException(
                            "Node module responded with non-JSON string. This cannot be converted to the requested generic type: " +
                            typeof(T).FullName);
                    }

                    var responseString = await response.Content.ReadAsStringAsync();
                    return (T)(object)responseString;

                case "application/json":
                    var responseJson = await response.Content.ReadAsStringAsync();
                    return JsonConvert.DeserializeObject<T>(responseJson);

                case "application/octet-stream":
                    // Streamed responses have to be received as System.IO.Stream instances
                    if (typeof(T) != typeof(Stream))
                    {
                        throw new ArgumentException(
                            "Node module responded with binary stream. This cannot be converted to the requested generic type: " +
                            typeof(T).FullName + ". Instead you must use the generic type System.IO.Stream.");
                    }

                    return (T)(object)(await response.Content.ReadAsStreamAsync());

                default:
                    throw new InvalidOperationException("Unexpected response content type: " + responseContentType.MediaType);
            }
        }

        protected override void OnOutputDataReceived(string outputData)
        {
            var match = _portNumber != 0 ? null : PortMessageRegex.Match(outputData);
            if (match != null && match.Success)
            {
                _portNumber = int.Parse(match.Groups[1].Captures[0].Value);
            }
            else
            {
                base.OnOutputDataReceived(outputData);
            }
        }

        protected override void OnBeforeLaunchProcess()
        {
            // Prepare to receive a new port number
            _portNumber = 0;
        }

	    protected override void Dispose(bool disposing) {
	        base.Dispose(disposing);

	        if (!_disposed)
            {
	            if (disposing)
                {
	                _client.Dispose();
	            }

	            _disposed = true;
	        }
	    }
	}
}