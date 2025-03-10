using Microsoft.AspNetCore.Mvc.Rendering;

namespace DeviceDataCollector.Services
{
    public class ViewContextAccessor : IViewContextAccessor
    {
        private static readonly AsyncLocal<ViewContext> _viewContextCurrent = new AsyncLocal<ViewContext>();

        public ViewContext ViewContext
        {
            get => _viewContextCurrent.Value;
            set => _viewContextCurrent.Value = value;
        }
    }

    public interface IViewContextAccessor
    {
        ViewContext ViewContext { get; set; }
    }
}