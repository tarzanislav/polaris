using System.Text;
using Polaris.Centrum;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
return Commands.Run(args, Console.Out, DateTime.Now, DateTimeOffset.UtcNow);
