namespace SmartcityBackend.src.AI.Models
{
    public class WasteAIInput
    {
        /// <summary>Public http(s) image URL, or a file name hint for uploads.</summary>
        public string ImageUrl { get; set; } = string.Empty;

        /// <summary>Optional uploaded image as base64 (no "data:" prefix).</summary>
        public string? ImageBase64 { get; set; }

        /// <summary>image/jpeg | image/png | image/gif | image/webp</summary>
        public string? MediaType { get; set; }
    }
}
