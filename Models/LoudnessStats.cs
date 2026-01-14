namespace FfmpegDrop.Models;

public record LoudnessStats(
    double InputI,
    double InputTP,
    double InputLRA,
    double InputThresh,
    double TargetOffset
);
