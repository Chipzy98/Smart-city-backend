namespace SmartcityBackend.src.ViewModels
{
    public class WasteClassifyRequestVM
    {
        /// <summary>Public image URL (URL mode) or original file name (upload mode).</summary>
        public string ImageUrl { get; set; } = string.Empty;

        /// <summary>Upload mode: base64 image data without the "data:...;base64," prefix.</summary>
        public string? ImageBase64 { get; set; }

        /// <summary>Upload mode: image/jpeg, image/png, image/gif or image/webp.</summary>
        public string? MediaType { get; set; }
    }
}
