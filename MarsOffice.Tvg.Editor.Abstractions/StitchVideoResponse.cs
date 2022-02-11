using System;
using System.Collections.Generic;
using System.Text;

namespace MarsOffice.Tvg.Editor.Abstractions
{
    public class StitchVideoResponse
    {
        public string VideoId { get; set; }
        public string JobId { get; set; }
        public string UserId { get; set; }
        public string UserEmail { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
    }
}
