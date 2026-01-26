namespace FfmpegDrop.Models;

public record MediaProbeInfo(
    double Duration,
    string Codec,           // Video codec
    string Resolution,
    double Fps,
    long Bitrate,          // Overall bitrate
    long SizeBytes,
    string AudioCodec,     // ✅ Audio codec name
    long AudioBitrate      // ✅ Audio bitrate
);
