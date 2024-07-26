using System.Text.Json.Serialization;

namespace PnP.Core.Model.SharePoint
{
    /// <summary>
    /// Class representing the json control data that will describe a control versus the zones and sections on a page
    /// </summary>
    internal sealed class CanvasControlPosition : CanvasPosition
    {
        /// <summary>
        /// Gets or sets JsonProperty "controlIndex"
        /// </summary>
        [JsonPropertyName("controlIndex")]
        // HEU: making nullable because server seems to send Null sometimes which throws in JSON deserialization
        public float? ControlIndex { get; set; }
    }
}
