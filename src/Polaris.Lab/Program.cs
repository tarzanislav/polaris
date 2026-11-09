using System.Runtime.Versioning;
using System.Text;
using Polaris.Core;
using Polaris.Windows;

[assembly: SupportedOSPlatform("windows")]

namespace Polaris.Lab;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("POLARIS laboratórium beží len vo Windows.");
            return 2;
        }

        var command = args.Length > 0 ? args[0] : "";
        try
        {
            switch (command)
            {
                case "sluzba":
                    // started by the service control manager inside a layer
                    return ServiceHost.Run(OfflineImage.ServiceName, Agent.Service) ? 0 : 1;
                case "verzia":
                    Console.WriteLine($"{Product.Name} {Product.Stage} {Product.Version}");
                    return 0;
                default:
                    Console.OutputEncoding = Encoding.UTF8;
                    Console.InputEncoding = Encoding.UTF8;
                    return Menu.Run();
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Neočakávaná chyba: " + e);
            if (command.Length == 0)
            {
                Console.WriteLine("Stlač ľubovoľný kláves…");
                Console.ReadKey(intercept: true);
            }

            return 1;
        }
    }
}
