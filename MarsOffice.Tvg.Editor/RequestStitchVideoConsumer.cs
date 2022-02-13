using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarsOffice.Tvg.Editor.Abstractions;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
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
            [QueueTrigger("request-stitch-video", Connection = "localsaconnectionstring")] RequestStitchVideo request,
            [Queue("stitch-video-response", Connection = "localsaconnectionstring")] IAsyncCollector<StitchVideoResponse> stitchVideoResponseQueue,
            ILogger log)
        {
            string tempDirectory = null;
            try
            {
                tempDirectory = Path.GetTempPath() + Guid.NewGuid().ToString();
                Directory.CreateDirectory(tempDirectory);
                await Task.WhenAll(new[]
                {
                    DownloadFile(request.AudioBackgroundFileLink, tempDirectory + "/audiobg.mp3"),
                    DownloadFile(request.VideoBackgroundFileLink, tempDirectory + "/videobg.mp4"),
                    DownloadFile(request.VoiceFileLink, tempDirectory + "/speech.mp3")
                });

                var success = await FfMpegTransform(request, tempDirectory);
                if (!success)
                {
                    throw new Exception("Text overlay failed");
                }

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
            } finally
            {
                try
                {
                    if (!string.IsNullOrEmpty(tempDirectory) && Directory.Exists(tempDirectory))
                    {
                        Directory.Delete(tempDirectory, true);
                    }
                } catch (Exception)
                {
                    // ignored







                }
            }
        }

        private async Task<bool> FfMpegTransform(RequestStitchVideo request, string tempDirectory)
        {
            long startSecs = 0;
            var drawTextCommands = new List<string>();
            for (var i = 0; i < request.Sentences.Count(); i++)
            {
                var cmd = $"drawtext=font='{request.TextFontFamily ?? "Times New Roman"}':text='{request.Sentences.ElementAt(i)}':fontcolor=white:fontsize={request.TextFontSize ?? 24}:box=1:boxcolor={request.TextBoxColor ?? "black"}@{(request.TextBoxOpacity != null ? request.TextBoxOpacity / 100d : 0.5)}:boxborderw=5:x=(w-text_w)/2:y=(h-text_h)/2:enable='between(t,{startSecs},{startSecs + request.Durations.ElementAt(i)})'";
                drawTextCommands.Add(cmd);
                startSecs += request.Durations.ElementAt(i);
            }
            var command = $"-i videobg.mp4 -y -c:v libx264 -preset ultrafast -vf \"[in]{string.Join(", ", drawTextCommands)}\" -codec:a copy videobg_overlayed.mp4";
            return await ExecuteFfmpeg(command, tempDirectory);
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
            process.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds);
            await Task.CompletedTask;
            return process.ExitCode != 0;
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
