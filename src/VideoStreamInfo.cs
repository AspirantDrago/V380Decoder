namespace V380Decoder.src;

internal sealed record VideoStreamInfo(VideoCodec Codec, int Width, int Height, int FrameRate, bool Known = true,
    string H264Profile = "High", int Bitrate = 4096, int GovLength = 30);
