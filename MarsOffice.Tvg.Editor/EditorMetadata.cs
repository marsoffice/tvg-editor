using System;
using System.Threading.Tasks;
using MarsOffice.Microfunction;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace MarsOffice.Tvg.Editor
{
    public class ContentTypes
    {

        public ContentTypes()
        {
        }

        [FunctionName("GetAllFonts")]
        public async Task<IActionResult> GetAllFonts(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/editor/getAllFonts")] HttpRequest req,
            ILogger log
            )
        {
            try
            {
                var principal = MarsOfficePrincipal.Parse(req);
                var userId = principal.FindFirst("id").Value;

                return new OkObjectResult(new[] { 
                    "Arial",
                    "Times New Roman"
                });
            }
            catch (Exception e)
            {
                log.LogError(e, "Exception occured in function");
                return new BadRequestObjectResult(Errors.Extract(e));
            }
        }

        [FunctionName("GetAllResolutions")]
        public async Task<IActionResult> GetAllResolutions(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/editor/getAllResolutions")] HttpRequest req,
            ILogger log
            )
        {
            try
            {
                var principal = MarsOfficePrincipal.Parse(req);
                var userId = principal.FindFirst("id").Value;

                return new OkObjectResult(new[] {
                    "1280x720",
                    "1920x1080"
                });
            }
            catch (Exception e)
            {
                log.LogError(e, "Exception occured in function");
                return new BadRequestObjectResult(Errors.Extract(e));
            }
        }
    }
}