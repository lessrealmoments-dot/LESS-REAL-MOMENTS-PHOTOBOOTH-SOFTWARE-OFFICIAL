using System.IO;
using System.IO.MemoryMappedFiles;

namespace BoothDesktop.Services;

/// <summary>
/// Reads JPEG frames from sony_bridge Local\SonyBridgeFrameMapV1 (same contract as native-poc virtualcam_shared.h).
/// </summary>
public static class SonyBridgeSharedMemoryReader
{
    public const uint ExpectedMagic = 0x53425631; // 'SBV1'
    private const string MapName = @"Local\SonyBridgeFrameMapV1";
    private const int HeaderSize = 40;
    private const int MaxMapBytes = 8 * 1024 * 1024;

    /// <summary>Try read latest JPEG from shared map. Runs synchronously; call from thread pool.</summary>
    public static bool TryReadLatestJpeg(out byte[] jpeg, out ulong frameId, out string? error)
    {
        jpeg = Array.Empty<byte>();
        frameId = 0;
        error = null;

        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            using var acc = mmf.CreateViewAccessor(0, MaxMapBytes, MemoryMappedFileAccess.Read);

            var magic = acc.ReadUInt32(0);
            if (magic != ExpectedMagic)
            {
                error = $"bad magic 0x{magic:X8} (expected SBV1)";
                return false;
            }

            _ = acc.ReadUInt32(4);  // version
            _ = acc.ReadUInt32(8);  // width
            _ = acc.ReadUInt32(12); // height
            _ = acc.ReadUInt32(16); // format
            var frameBytes = acc.ReadUInt32(20);
            frameId = acc.ReadUInt64(24);
            _ = acc.ReadUInt64(32); // timestampMs

            if (frameBytes == 0 || HeaderSize + frameBytes > MaxMapBytes)
            {
                error = frameBytes == 0 ? "frameBytes=0 (live view not publishing yet)" : "frame size out of range";
                return false;
            }

            jpeg = new byte[frameBytes];
            acc.ReadArray(HeaderSize, jpeg, 0, (int)frameBytes);
            return true;
        }
        catch (FileNotFoundException)
        {
            error = "shared map not found (start sony_bridge with camera connected)";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = "shared map access denied";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
