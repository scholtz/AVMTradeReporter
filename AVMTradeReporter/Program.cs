using AlgorandAuthenticationV2;
using AVMIndexReporter.Repository;
using AVMTradeReporter.Hubs;
using AVMTradeReporter.Model.Configuration;
using AVMTradeReporter.Repository;
using AVMTradeReporter.Services;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Security;
using Elastic.Transport;
using Microsoft.Extensions.Options;

namespace AVMTradeReporter
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddControllers();
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();
            builder.Services.AddSignalR();

            // Configure AppConfiguration from appsettings.json
            builder.Services.Configure<AppConfiguration>(
                builder.Configuration.GetSection("AppConfiguration"));

            builder.Services.AddSingleton<IndexerRepository>();
            builder.Services.AddSingleton<TradeRepository>();
            builder.Services.AddSingleton<TransactionProcessor>();

            // Register the background service
            builder.Services.AddHostedService<TradeReporterBackgroundService>();
            builder.Services.AddHostedService<GossipBackgroundService>();

            builder.Services.AddSingleton<ElasticsearchClient>(sp =>
            {
                var appConfig = sp.GetRequiredService<IOptions<AppConfiguration>>().Value;

                var settings = new ElasticsearchClientSettings(new Uri(appConfig.Elastic.Host))
                .Authentication(new Elastic.Transport.ApiKey(appConfig.Elastic.ApiKey ?? throw new Exception("Api key for elastic is null")))
                .ThrowExceptions()
                .EnableDebugMode()
                .EnableHttpCompression()
                .PrettyJson()
                .RequestTimeout(TimeSpan.FromMinutes(1))
                .DefaultMappingFor<Model.Data.Trade>(m => m
                    .IndexName("trades")
                    .IdProperty(t => t.TxId))
                .DefaultMappingFor<Model.Data.Indexer>(m => m
                    .IndexName("indexers")
                    .IdProperty(t => t.Id))
                ;

                return new ElasticsearchClient(settings);
            });

            // Add CORS policy
            var corsConfig = builder.Configuration.GetSection("Cors").AsEnumerable().Select(k => k.Value ?? "").Where(k => !string.IsNullOrEmpty(k)).ToArray();
            if (!(corsConfig?.Length > 0)) throw new Exception("Cors not defined");
            Console.WriteLine($"Cors: {string.Join(",",corsConfig)}");
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(
                builder =>
                {
                    builder.WithOrigins(corsConfig)
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .AllowCredentials();
                });
            });

            var authOptions = builder.Configuration.GetSection("AlgorandAuthentication").Get<AlgorandAuthenticationOptionsV2>();
            if (authOptions == null) throw new Exception("Config for the authentication is missing");
            builder.Services.AddAuthentication(AlgorandAuthenticationHandlerV2.ID).AddAlgorand(a =>
            {
                a.Realm = authOptions.Realm;
                a.AllowEmptyAccounts = authOptions.AllowEmptyAccounts;
                a.CheckExpiration = authOptions.CheckExpiration;
                a.EmptySuccessOnFailure = authOptions.EmptySuccessOnFailure;
                a.AllowedNetworks = authOptions.AllowedNetworks;
                a.Debug = authOptions.Debug;
            });

            builder.Services.AddProblemDetails();

            var app = builder.Build();

            app.UseCors();
            app.UseSwagger();
            app.UseSwaggerUI();

            app.UseWebSockets();
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapControllers();
            app.MapHub<BiatecScanHub>("/biatecScanHub");


            var bw = app.Services.GetService<TradeReporterBackgroundService>();
            bw?.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            app.Run();
        }
    }
}
