using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PdfProcessingService.Helpers;
using PdfProcessingService.Middleware;
using PdfProcessingService.Models;
using PdfProcessingService.Services;
using System;

namespace PdfProcessingService;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        ConfigureLogging(builder);
        ConfigureServices(builder);

        var app = builder.Build();

        ConfigureMiddleware(app);
        ConfigureEndpoints(app);

        try
        {
            app.Logger.LogInformation("Starting PDF Processing Service");

            // var elasticsearchService = app.Services.GetRequiredService<IElasticsearchService>();
            // elasticsearchService.EnsureIndexExistsAsync().GetAwaiter().GetResult();

            app.Run();
        }
        catch (Exception ex)
        {
            app.Logger.LogCritical(ex, "PDF Processing Service terminated unexpectedly");
        }
    }

    private static void ConfigureLogging(WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders(); // optional: clear default providers if needed
        builder.Logging.AddConsole();

        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);
        builder.Logging.AddFilter("Default", LogLevel.Information);
    }

    private static void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.Services.AddControllers()
            .AddJsonOptions(options => options.JsonSerializerOptions.WriteIndented = true);
        builder.Services.AddEndpointsApiExplorer();

        builder.Services.Configure<ElasticsearchSettings>(
            builder.Configuration.GetSection("ElasticsearchSettings"));
        builder.Services.Configure<ProcessingSettings>(
            builder.Configuration.GetSection("PdfProcessingSettings"));
        builder.Services.Configure<VespaSettings>(
            builder.Configuration.GetSection("VespaSettings"));

        builder.Services.AddSingleton<SystemMemoryInfo>();

        // builder.Services.AddSingleton<IElasticsearchService, ElasticsearchService>();
        builder.Services.AddScoped<TxtService>();

        builder.Services.AddCors(options =>
        {
            options.AddPolicy("AllowAll", policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });

        /*builder.Services.AddHttpClient("elasticsearch", client =>
        {
            var elasticsearchSettings = builder.Configuration
                .GetSection("ElasticsearchSettings")
                .Get<ElasticsearchSettings>();

            client.BaseAddress = new Uri(elasticsearchSettings.Url);
            client.Timeout = TimeSpan.FromMinutes(5);
        });*/
    }

    private static void ConfigureMiddleware(WebApplication app)
    {
        app.UseMiddleware<ExceptionHandlingMiddleware>();
        app.UseCors("AllowAll");
        app.UseAuthorization();
    }

    private static void ConfigureEndpoints(WebApplication app)
    {
        app.MapControllers();
    }
}
