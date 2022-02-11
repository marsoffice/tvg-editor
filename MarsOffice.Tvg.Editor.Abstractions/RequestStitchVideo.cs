using System;
using System.Collections.Generic;
using System.Text;

namespace MarsOffice.Tvg.Editor.Abstractions
{
    public class RequestStitchVideo
    {
        public string VideoId { get; set; }
        public string JobId { get; set; }
        public string UserId { get; set; }
        public string UserEmail { get; set; }
        public IEnumerable<string> Sentences { get; set; }
        public IEnumerable<long> Durations { get; set; }
        public string VoiceFileLink { get; set; }
        public string AudioBackgroundFileLink { get; set; }
        public float? AudioBackgroundVolumeInPercent { get; set; }
        public string VideoBackgroundFileLink { get; set; }
        public string Resolution { get; set; }
        public string TextFontFamily { get; set; }
        public float? TextFontSize { get; set; }
        public string TextBoxColor { get; set; }
        public float? TextBoxOpacity { get; set; }
        public string TextBoxBorderColor { get; set; }
    }
}
