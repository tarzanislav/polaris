using System.Text;
using Polaris.Agent;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
return Commands.Run(args, Console.Out);
