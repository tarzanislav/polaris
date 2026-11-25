using System.Text;
using Polaris.Agent;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
if (args is ["sluzba"] && OperatingSystem.IsWindows())
{
    if (AgentService.RunAsService(AgentHost.Windows()))
    {
        return 0;
    }

    Console.WriteLine("Príkaz sluzba spúšťa Windows pri štarte služby POLARIS agent. Nainštaluj ju: polaris-agent instalovat-sluzbu");
    return 2;
}

// a double click (no command, someone at the window): the installer of this PC
if (OperatingSystem.IsWindows() && (args is [] || args is ["instalovat"]) && !Console.IsInputRedirected)
{
    return Setup.Run(Console.Out, Commands.DefaultHost());
}

return Commands.Run(args, Console.Out);
