using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ByteDev.Subtitles.SubRip;
using MarsOffice.Tvg.Editor.Abstractions;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using Microsoft.Azure.Storage.Queue;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MarsOffice.Tvg.Editor
{
    public class RequestStitchVideoConsumer
    {
        private readonly IConfiguration _config;
        private readonly CloudBlobClient _blobClient;

        public RequestStitchVideoConsumer(IConfiguration config)
        {
            _config = config;
            var csa = CloudStorageAccount.Parse(config["localsaconnectionstring"]);
            _blobClient = csa.CreateCloudBlobClient();
        }

        [FunctionName("RequestStitchVideoConsumer")]
        public async Task Run(
            [QueueTrigger("request-stitch-video", Connection = "localsaconnectionstring")] CloudQueueMessage message,
            [Queue("stitch-video-response", Connection = "localsaconnectionstring")] IAsyncCollector<StitchVideoResponse> stitchVideoResponseQueue,
            ILogger log)
        {
            var request = Newtonsoft.Json.JsonConvert.DeserializeObject<RequestStitchVideo>(message.AsString,
                    new Newtonsoft.Json.JsonSerializerSettings
                    {
                        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
                        NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore
                    });
            string tempDirectory = null;
            try
            {
#if !DEBUG
                var chmodPsi1 = new ProcessStartInfo
                {
                    Arguments = $"-c \"chmod u+x {_config["ffprobepath"]}\"",
                    FileName = "/bin/bash",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                var chmod1Process = Process.Start(chmodPsi1);
                chmod1Process.WaitForExit((int)TimeSpan.FromSeconds(60).TotalMilliseconds);
                if (chmod1Process.ExitCode != 0)
                {
                    throw new Exception("Could not execute chmod 1");
                }

                var chmodPsi2 = new ProcessStartInfo
                {
                    Arguments = $"-c \"chmod u+x {_config["ffmpegpath"]}\"",
                    FileName = "/bin/bash",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                var chmod2Process = Process.Start(chmodPsi2);
                chmod2Process.WaitForExit((int)TimeSpan.FromSeconds(60).TotalMilliseconds);
                if (chmod2Process.ExitCode != 0)
                {
                    throw new Exception("Could not execute chmod 2");
                }
#endif

                tempDirectory = Path.GetTempPath() + Guid.NewGuid().ToString();
                Directory.CreateDirectory(tempDirectory);
                await Task.WhenAll(new[]
                {
                    DownloadFile(request.AudioBackgroundFileLink, tempDirectory + "/audiobg.mp3"),
                    DownloadFile(request.VideoBackgroundFileLink, tempDirectory + "/videobg.mp4"),
                    DownloadFile(request.VoiceFileLink, tempDirectory + "/speech.mp3"),
                    CreateSrtFile(request.Sentences, request.Durations, tempDirectory + "/subs.srt")
                });

                var success = await MergeAudio(request, tempDirectory);
                if (!success)
                {
                    throw new Exception("Text overlay failed");
                }

                success = await FfMpegTransform(request, tempDirectory);
                if (!success)
                {
                    throw new Exception("Text overlay failed");
                }

                var editorContainerReference = _blobClient.GetContainerReference("editor");
#if DEBUG
                await editorContainerReference.CreateIfNotExistsAsync();
#endif
                var finalBlobReference = editorContainerReference.GetBlockBlobReference($"{request.VideoId}.mp4");
                await finalBlobReference.UploadFromFileAsync(tempDirectory + "/final.mp4");
                finalBlobReference.Metadata.Add("VideoId", request.VideoId);
                finalBlobReference.Metadata.Add("JobId", request.JobId);
                finalBlobReference.Metadata.Add("UserId", request.UserId);
                finalBlobReference.Metadata.Add("UserEmail", request.UserEmail);
                await finalBlobReference.SetMetadataAsync();

                var sas = finalBlobReference.GetSharedAccessSignature(new SharedAccessBlobPolicy
                {
                    Permissions = SharedAccessBlobPermissions.Read,
                    SharedAccessStartTime = DateTimeOffset.UtcNow,
                    SharedAccessExpiryTime = DateTimeOffset.UtcNow.AddYears(10)
                });

                await stitchVideoResponseQueue.AddAsync(new StitchVideoResponse
                {
                    JobId = request.JobId,
                    Success = true,
                    UserEmail = request.UserEmail,
                    UserId = request.UserId,
                    VideoId = request.VideoId,
                    FinalVideoLink = $"editor/{request.VideoId}.mp4",
                    SasUrl = finalBlobReference.Uri.ToString() + sas
                });
                await stitchVideoResponseQueue.FlushAsync();
            }
            catch (Exception e)
            {
                log.LogError(e, "Function threw an exception");
                if (message.DequeueCount >= 5)
                {
                    await stitchVideoResponseQueue.AddAsync(new StitchVideoResponse
                    {
                        Error = "EditorService: " + e.Message,
                        JobId = request.JobId,
                        Success = false,
                        UserEmail = request.UserEmail,
                        UserId = request.UserId,
                        VideoId = request.VideoId
                    });
                    await stitchVideoResponseQueue.FlushAsync();
                }
                throw;
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrEmpty(tempDirectory) && Directory.Exists(tempDirectory))
                    {
                        Directory.Delete(tempDirectory, true);
                    }
                }
                catch (Exception)
                {
                    // ignored
                }
            }
        }

        private static async Task CreateSrtFile(IEnumerable<string> sentences, IEnumerable<long> durations, string outputFile)
        {
            var entries = new List<SubRipEntry>();
            long startSecs = 0;
            for (var i = 0; i < sentences.Count(); i++)
            {
                var endSecs = startSecs + durations.ElementAt(i);

                var startClassicTs = TimeSpan.FromMilliseconds(startSecs);
                var endClassicTs = TimeSpan.FromMilliseconds(endSecs);

                var startTs = new SubRipTimeSpan(startClassicTs.Hours, startClassicTs.Minutes, startClassicTs.Seconds, startClassicTs.Milliseconds);
                var endTs = new SubRipTimeSpan(endClassicTs.Hours, endClassicTs.Minutes, endClassicTs.Seconds, endClassicTs.Milliseconds);
                entries.Add(new SubRipEntry(i + 1, new SubRipDuration(startTs, endTs), sentences.ElementAt(i)));
                startSecs = endSecs;
            }
            var file = new SubRipFile("subs.srt", entries);
            file.SetTextLineMaxLength(100);
            await File.WriteAllTextAsync(outputFile, file.ToString());
        }

        private async Task<bool> FfMpegTransform(RequestStitchVideo request, string tempDirectory)
        {
            var to = TimeSpan.FromMilliseconds(request.Durations.Sum());
            if (request.FinalFileDurationInMillis.HasValue && request.FinalFileDurationInMillis.Value < request.Durations.Sum())
            {
                if (request.TrimGracefullyToMaxDuration == true)
                {
                    long totalMillis = 0;
                    foreach (var duration in request.Durations)
                    {
                        if ((totalMillis + duration) > request.FinalFileDurationInMillis.Value)
                        {
                            break;
                        }
                        totalMillis += duration;
                    }
                    to = TimeSpan.FromMilliseconds(totalMillis);
                }
                else
                {
                    to = TimeSpan.FromMilliseconds(request.FinalFileDurationInMillis.Value);
                }
            }
            var command = $"-hide_banner -loglevel error -stream_loop -1 -i videobg.mp4 -i audio_merged.mp3 -ss 00:00:00 -to {to} -map 0:v -map 1:a -y -c:v libx264 -c:a aac -preset veryfast -vf \"subtitles=subs.srt:force_style='Alignment=10,BackColour={EncodeBackColor(request.TextBoxColor, request.TextBoxOpacity)},BorderStyle=4,Fontsize={request.TextFontSize ?? 13},PrimaryColour={EncodeTextColor(request.TextColor)}'\" -codec:a aac final.mp4";
            return await ExecuteFfmpeg(command, tempDirectory);
        }

        private async Task<bool> MergeAudio(RequestStitchVideo request, string tempDirectory)
        {
            var command = $"-hide_banner -loglevel error -i speech.mp3 -stream_loop -1 -i audiobg.mp3 -filter_complex \"[1:a]volume={(request.AudioBackgroundVolumeInPercent == null ? 0.1 : Math.Round(request.AudioBackgroundVolumeInPercent.Value / 100d, 2))},apad[A];[0:a]volume=1,[A]amerge[out]\" -c:v copy -map [out] -y audio_merged.mp3";
            return await ExecuteFfmpeg(command, tempDirectory);
        }

        private static string ToHex(int perc)
        {
            var decValue = perc * 255 / 100;
            return decValue.ToString("X");
        }

        private static string EncodeBackColor(string color, int? opacity)
        {
            if (string.IsNullOrEmpty(color))
            {
                color = "000000";
            }
            else
            {
                color = color.Replace("#", "");
            }
            if (opacity == null)
            {
                opacity = 50;
            }
            var bgr = $"{color[4]}{color[5]}{color[2]}{color[3]}{color[0]}{color[1]}";
            return $"&H{ToHex(100 - opacity.Value)}{bgr}&";
        }

        private static string EncodeTextColor(string color)
        {
            if (string.IsNullOrEmpty(color))
            {
                color = "ffffff";
            }
            else
            {
                color = color.Replace("#", "");
            }
            var bgr = $"{color[4]}{color[5]}{color[2]}{color[3]}{color[0]}{color[1]}";
            return $"&H{bgr}&";
        }

        private async Task<bool> ExecuteFfmpeg(string arguments, string workingDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _config["ffmpegpath"],
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = workingDir
            };

            var process = Process.Start(psi);
            process.WaitForExit(5 * 60 * 1000);
            await Task.CompletedTask;
            return process.ExitCode == 0;
        }

        private async Task DownloadFile(string link, string outPath)
        {
            var containerReference = _blobClient.GetContainerReference(link.Split("/").First());
            var fileName = string.Join("/", link.Split("/").Skip(1).ToList());
            var blobReference = containerReference.GetBlockBlobReference(fileName);
            using var fileStream = File.OpenWrite(outPath);
            using var readStream = await blobReference.OpenReadAsync();
            await readStream.CopyToAsync(fileStream);
        }
    }
}
