using GraphPaper.Infrastructure.Interfaces;

namespace GraphPaper.Infrastructure.Commons;

public class CurrentTime : ICurrentTime
{
    public DateTime GetCurrentTime()
    {
        return DateTime.UtcNow; // Đảm bảo trả về thời gian UTC
    }
}