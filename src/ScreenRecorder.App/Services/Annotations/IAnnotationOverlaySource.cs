namespace ScreenRecorder.App.Services.Annotations;

internal interface IAnnotationOverlaySource
{
    bool TryGetLatest(out byte[]? bgraPremul, out int width, out int height, out long version);
}
