using System;
using System.Threading.Tasks;
using MarsOffice.Tvg.Editor.Abstractions;
using Microsoft.Azure.Management.Media;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest;
using Microsoft.Rest.Azure.Authentication;

namespace MarsOffice.Tvg.Editor
{
    public class RequestStitchVideoConsumer
    {
        private readonly IConfiguration _config;

        public RequestStitchVideoConsumer(IConfiguration config)
        {
            _config = config;
        }

        [FunctionName("RequestStitchVideoConsumer")]
        public async Task Run(
            [QueueTrigger("request-stitch-video", Connection = "localsaconnectionstring")]RequestStitchVideo request,
            [Queue("stitch-video-response", Connection = "localsaconnectionstring")] IAsyncCollector<StitchVideoResponse> stitchVideoResponseQueue,
            ILogger log)
        {
            try
            {
                var client = await CreateMediaServicesClientAsync();
                client.ApiVersion.ToString();
            } catch (Exception e)
            {
                log.LogError(e, "Function threw an exception");
                await stitchVideoResponseQueue.AddAsync(new StitchVideoResponse { 
                    Error = e.Message,
                    JobId = request.JobId,
                    Success = false,
                    UserEmail = request.UserEmail,
                    UserId = request.UserId,
                    VideoId = request.VideoId
                });
                await stitchVideoResponseQueue.FlushAsync();
            }
        }

        private async Task<ServiceClientCredentials> GetCredentialsAsync()
        {

            var clientCredential = new ClientCredential(_config["adclientid"], _config["adclientsecret"]);
            return await ApplicationTokenProvider.LoginSilentAsync(_config["tenantid"], clientCredential, ActiveDirectoryServiceSettings.Azure);
        }

        private async Task<IAzureMediaServicesClient> CreateMediaServicesClientAsync()
        {
            var credentials = await GetCredentialsAsync();

            return new AzureMediaServicesClient(new Uri(_config["armendpoint"]), credentials)
            {
                SubscriptionId = _config["subscriptionid"]
            };
        }
    }
}
