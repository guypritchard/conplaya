using Conplaya.App;
using Conplaya.Logging;
using Conplaya.Terminal;

Console.OutputEncoding = System.Text.Encoding.UTF8;
TerminalCapabilities.EnsureVirtualTerminal();

var options = AppOptions.Parse(args);
Logger.Configure(options.Verbose);

var app = new ConplayaApplication(options);
return await app.RunAsync();
