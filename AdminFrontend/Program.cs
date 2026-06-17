using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using AdminFrontend;
using System.Net.Http.Headers;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<Frontend.Services.AuthState>();

var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);
var apiKey = "dev_super_secret_api_key_123";

builder.Services.AddHttpClient("PlayerClient", client => 
{
    client.BaseAddress = baseAddress;
    client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
});

builder.Services.AddHttpClient("AdminClient", client => 
{
    client.BaseAddress = baseAddress;
    client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
});

await builder.Build().RunAsync();
