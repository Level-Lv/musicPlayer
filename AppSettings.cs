namespace musicPlayer
{
    public class AppSettings
    {
        public string CirclePictureFileName { get; set; } = "default_cover.png";

        public string BackgroundVideoFileName { get; set; } = "default_background.mp4";

        public string MusicPathFileName { get; set; } = "";

        public bool IsShuffleMode { get; set; } = false;

        public bool IsDesktopLyricsVisible { get; set; } = true;
    }
}