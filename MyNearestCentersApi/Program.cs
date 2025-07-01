using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using MyNearestCentersApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddLogging(logging => logging.AddConsole());
builder.Services.AddHttpClient<NearestCentersService>();
builder.Services.AddSingleton<NearestCentersService>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<NearestCentersService>>();
    var httpClient = provider.GetRequiredService<IHttpClientFactory>().CreateClient();
    var configuration = provider.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("DefaultConnection") 
        ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    return new NearestCentersService(logger, httpClient, connectionString);
});

var app = builder.Build();
app.UseRouting();
app.MapControllers();
app.Run();