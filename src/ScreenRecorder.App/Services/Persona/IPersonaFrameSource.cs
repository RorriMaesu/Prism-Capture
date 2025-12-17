namespace ScreenRecorder.App.Services.Persona;

internal interface IPersonaFrameSource
{
    bool TryGetLatest(out byte[]? bgra, out int width, out int height, out long version);
}
