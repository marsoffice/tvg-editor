using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MarsOffice.Tvg.Editor.Abstractions;
using Microsoft.Azure.Management.Media;
using Microsoft.Azure.Management.Media.Models;
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
            [QueueTrigger("request-stitch-video", Connection = "localsaconnectionstring")] RequestStitchVideo request,
            [Queue("stitch-video-response", Connection = "localsaconnectionstring")] IAsyncCollector<StitchVideoResponse> stitchVideoResponseQueue,
            ILogger log)
        {
            try
            {
                var client = await CreateMediaServicesClientAsync();
                client.LongRunningOperationRetryTimeout = 2;

                var transform = await CreateOrUpdateTransform(client, request);

                var job = await CreateJob(client, request);
            }
            catch (Exception e)
            {
                log.LogError(e, "Function threw an exception");
                await stitchVideoResponseQueue.AddAsync(new StitchVideoResponse
                {
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

        private async Task<Job> CreateJob(IAzureMediaServicesClient client, RequestStitchVideo request)
        {
            var inputs = new List<JobInput> { 
                new JobInputAsset("videobackground"),
                new JobInputAsset("audiobackground"),
                new JobInputAsset("speech")
            };
            var outputs = new List<JobOutput> { 
                new JobOutputAsset("final")
            };

            var job = await client.Jobs.CreateAsync(
                _config["mediaservicesresourcegroupname"],
                _config["mediaservicesaccountname"],
                "ZikMashTransform",
                request.VideoId,
                new Job
                {
                    Input = new JobInputs(inputs: inputs),
                    Outputs = outputs,
                    CorrelationData = new Dictionary<string, string>
                    {
                            {"VideoId", request.VideoId },
                            {"JobId", request.JobId },
                            {"UserId", request.UserId },
                            {"UserEmail", request.UserEmail }
                    }
                }
             );
            return job;
        }

        private async Task<Transform> CreateOrUpdateTransform(IAzureMediaServicesClient client, RequestStitchVideo request)
        {
            var overlays = new List<Overlay>
                            {
                                new VideoOverlay
                                {
                                    InputLabel = "textbox",
                                    Position = new Rectangle("10%", "50%", "80%", "50%"),
                                }
                            };

            overlays.Add(new AudioOverlay { 
                AudioGainLevel = 1,
                InputLabel = "speech"
            });

            overlays.Add(new AudioOverlay
            {
                AudioGainLevel = 0.4,
                InputLabel = "audiobackground"
            });

            var outputs = new List<TransformOutput> {
                new TransformOutput
                {
                    Preset = new StandardEncoderPreset
                    {
                        Filters = new Filters
                        {
                            Overlays = overlays
                        },
                        Codecs = new List<Codec>
                        {
                            new AacAudio
                            {
                            },
                            new H264Video
                            {
                                Layers = new List<H264Layer>
                                {
                                    new H264Layer
                                    {
                                        Profile = H264VideoProfile.Auto,
                                        Bitrate = 1000000,
                                        Width = "1080",
                                        Height = "1920"
                                    }
                                }
                            }
                        },
                        Formats = new List<Format>
                        {
                            new Mp4Format
                            {
                                FilenamePattern = "{Basename}{Extension}",
                            }
                        }
                    }
                }
            };
            return await client.Transforms.CreateOrUpdateAsync(
                _config["mediaservicesresourcegroupname"],
                _config["mediaservicesaccountname"],
                "ZikMashTransform",
                outputs
                );
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
