namespace V380Decoder.src;

internal sealed record VideoStreamInfo(VideoCodec Codec, int Width, int Height, int FrameRate, bool Known = true);
