using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MyDicomViewer.Web;
using MyDicomViewer.Web.State;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.Services.AddSingleton<BrowserIo>();
builder.Services.AddSingleton<Workbench>();
await builder.Build().RunAsync();
