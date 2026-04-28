using BoothDesktop.Models;

namespace BoothDesktop.Services;

public interface INavigationHost
{
    void EnterPhotobooth(BoothEventSummary boothEvent);
    void ExitPhotobooth();
}
