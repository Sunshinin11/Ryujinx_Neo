namespace Ryujinx.HLE.HOS.Applets.Browser
{
    public enum WebExitReason : uint
    {
        ExitButton,
        BackButton,
        Requested,
        LastUrl,
        WindowClosed, // NEOV2-WEBOFF3: page closed itself (window.close()); value 4, as on hardware/Eden.
        ErrorDialog = 7,
    }
}
