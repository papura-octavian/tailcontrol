using TailControl.Client.ViewModels;
using TailControl.Client.Views;

namespace TailControl.Client;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(Routes.Device, typeof(DeviceDetailPage));
        Routing.RegisterRoute(Routes.DeviceEdit, typeof(DeviceEditPage));
    }
}
