namespace WindowStream.Core.Hosting;

public enum WorkerCommandTag : byte
{
    Pause = 0x01,
    Resume = 0x02,
    RequestKeyframe = 0x03,
    Shutdown = 0xFF
}
