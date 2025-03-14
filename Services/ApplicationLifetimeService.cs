namespace DeviceDataCollector.Services
{
    public class ApplicationLifetimeService
    {
        public DateTime StartTime { get; private set; }

        public ApplicationLifetimeService()
        {
            StartTime = DateTime.Now;
        }

        public TimeSpan GetUptime()
        {
            return DateTime.Now - StartTime;
        }
    }
}
