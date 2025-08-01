using AVMTradeReporter.Model.Configuration;
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

            // Configure AppConfiguration from appsettings.json
            builder.Services.Configure<AppConfiguration>(
                builder.Configuration.GetSection("AppConfiguration"));

            // Register the background service
            builder.Services.AddHostedService<TradeReporterBackgroundService>();

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

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseHttpsRedirection();

            app.UseAuthorization();

            app.MapControllers();


            var bw = app.Services.GetService<TradeReporterBackgroundService>();
            bw?.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            app.Run();
        }
    }
}
